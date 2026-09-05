using System.ComponentModel;
using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Runtime.InteropServices;

internal static class ProcessMemoryTrace
{
    const uint ContextAmd64 = 0x00100000;
    const uint ContextControl = ContextAmd64 | 0x00000001;
    const uint ContextInteger = ContextAmd64 | 0x00000002;
    const uint ContextDebugRegisters = ContextAmd64 | 0x00000010;
    const uint ThreadSuspendResume = 0x0002;
    const uint ThreadGetContext = 0x0008;
    const uint ThreadSetContext = 0x0010;
    const uint ThreadQueryInformation = 0x0040;
    const uint ProcessVmRead = 0x0010;
    const uint ProcessQueryInformation = 0x0400;
    const uint ExceptionDebugEvent = 1;
    const uint CreateThreadDebugEvent = 2;
    const uint ExitProcessDebugEvent = 5;
    const uint ExceptionBreakpoint = 0x80000003;
    const uint ExceptionSingleStep = 0x80000004;
    const uint DbgContinue = 0x00010002;
    const uint DbgExceptionNotHandled = 0x80010001;

    public static int Probe()
    {
        nint address = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt32(address, unchecked((int)0xE8559179));
            Marshal.WriteInt32(address + 4, unchecked((int)0xEC7B1322));
            Console.WriteLine($"probe pid {Environment.ProcessId} addresses 0x{unchecked((ulong)address):X16} 0x{unchecked((ulong)(address + 4)):X16}");
            Console.WriteLine("press Enter to read; type q then Enter to quit");
            while (Console.ReadLine() is string input && !input.Equals("q", StringComparison.OrdinalIgnoreCase))
            {
                uint threadId = GetCurrentThreadId();
                uint first = unchecked((uint)Marshal.ReadInt32(address));
                uint second = unchecked((uint)Marshal.ReadInt32(address + 4));
                Console.WriteLine($"thread {threadId} read 0x{first:X8} 0x{second:X8}");
            }
            return 0;
        }
        finally
        {
            Marshal.FreeHGlobal(address);
        }
    }
    public static int Run(string processText, string[] addressTexts)
    {
        Process process;
        ulong[] addresses;
        try
        {
            process = ResolveProcess(processText);
            addresses = addressTexts.Select(ParseUnsigned).Distinct().ToArray();
            if (addresses.Length is < 1 or > 4)
                throw new ArgumentException("provide one to four watched addresses");
            if (addresses.Any(address => (address & 3) != 0))
                throw new ArgumentException("each watched 4-byte address must be aligned to 4 bytes");
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }

        nint processHandle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, process.Id);
        if (processHandle == 0)
        {
            Console.WriteLine($"could not open {process.ProcessName} ({process.Id}): Windows error {Marshal.GetLastWin32Error()}");
            return 1;
        }

        bool attached = false;
        try
        {
            if (!DebugActiveProcess(process.Id))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "could not attach debugger");
            attached = true;
            DebugSetProcessKillOnExit(false);

            ulong moduleBase = unchecked((ulong)(process.MainModule?.BaseAddress ?? 0));
            Task<string?> stop = Task.Run(Console.ReadLine);
            bool armed = false;
            bool audited = false;
            DateTime auditAt = DateTime.MaxValue;
            int hits = 0;
            Console.WriteLine($"attaching to {process.ProcessName} ({process.Id}), watch {string.Join(" ", addresses.Select(address => $"0x{address:X16}"))}");

            while (!stop.IsCompleted)
            {
                if (!WaitForDebugEvent(out DebugEvent debugEvent, 100))
                {
                    if (armed && !audited && DateTime.UtcNow >= auditAt)
                    {
                        process.Refresh();
                        int verified = 0;
                        int total = 0;
                        foreach (ProcessThread thread in process.Threads)
                        {
                            total++;
                            if (SetBreakpoint((uint)thread.Id, addresses, true, true))
                                verified++;
                        }
                        Console.WriteLine($"post-resume audit {verified}/{total} threads");
                        audited = true;
                    }
                    continue;
                }

                uint status = DbgContinue;
                if (debugEvent.Code == ExceptionDebugEvent)
                {
                    if (debugEvent.ExceptionCode == ExceptionBreakpoint && !armed)
                    {
                        int configured = 0;
                        int total = 0;
                        foreach (ProcessThread thread in process.Threads)
                        {
                            total++;
                            if (SetBreakpoint((uint)thread.Id, addresses, true, true))
                                configured++;
                        }
                        armed = true;
                        Console.WriteLine($"armed {configured}/{total} threads; trigger the game action, then press Enter to stop");
                        auditAt = DateTime.UtcNow.AddSeconds(1);
                    }
                    else if (debugEvent.ExceptionCode == ExceptionSingleStep && armed)
                    {
                        if (ReadContext(debugEvent.ThreadId, out Context64 context)
                            && (context.Dr6 & ((1UL << addresses.Length) - 1)) != 0)
                        {
                            hits++;
                            PrintHit(processHandle, moduleBase, debugEvent.ThreadId, hits, context, addresses);
                            context.ContextFlags = ContextDebugRegisters;
                            context.Dr6 = 0;
                            WriteContext(debugEvent.ThreadId, context);
                        }
                        else
                            status = DbgExceptionNotHandled;
                    }
                    else if (debugEvent.ExceptionCode != ExceptionBreakpoint)
                        status = DbgExceptionNotHandled;
                }
                else if (debugEvent.Code == CreateThreadDebugEvent && armed)
                    SetBreakpoint(debugEvent.ThreadId, addresses, true, true);
                else if (debugEvent.Code == ExitProcessDebugEvent)
                {
                    ContinueDebugEvent(debugEvent.ProcessId, debugEvent.ThreadId, status);
                    attached = false;
                    Console.WriteLine("process exited");
                    return 0;
                }

                if (!ContinueDebugEvent(debugEvent.ProcessId, debugEvent.ThreadId, status))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "could not continue debug event");
            }

            Console.WriteLine($"captured {hits} access hit(s)");
            return 0;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }
        finally
        {
            if (attached)
            {
                try
                {
                    process.Refresh();
                    foreach (ProcessThread thread in process.Threads)
                        SetBreakpoint((uint)thread.Id, Array.Empty<ulong>(), false, false);
                }
                catch
                {
                }
                DebugActiveProcessStop(process.Id);
            }
            CloseHandle(processHandle);
        }
    }

    static void PrintHit(nint process, ulong moduleBase, uint threadId, int hit, Context64 context, ulong[] addresses)
    {
        ulong start = context.Rip >= 24 ? context.Rip - 24 : 0;
        var bytes = new byte[56];
        string code = ReadProcessMemory(process, unchecked((nint)start), bytes,
            (nuint)bytes.Length, out nuint read) && read > 0
            ? Convert.ToHexString(bytes.AsSpan(0, checked((int)read)))
            : "unreadable";
        string location = context.Rip >= moduleBase
            ? $"GRW+0x{context.Rip - moduleBase:X}"
            : "outside GRW.exe";
        string watched = string.Join(", ", addresses.Select((address, index) => (address, index))
            .Where(item => (context.Dr6 & (1UL << item.index)) != 0)
            .Select(item => $"DR{item.index}=0x{item.address:X16}"));
        Console.WriteLine($"hit {hit} thread {threadId}: RIP=0x{context.Rip:X16} {location}");
        Console.WriteLine($"  watched {watched}");
        Console.WriteLine($"  RAX={context.Rax:X16} RBX={context.Rbx:X16} RCX={context.Rcx:X16} RDX={context.Rdx:X16}");
        Console.WriteLine($"  RSI={context.Rsi:X16} RDI={context.Rdi:X16} RBP={context.Rbp:X16} RSP={context.Rsp:X16}");
        Console.WriteLine($"  R8 ={context.R8:X16} R9 ={context.R9:X16} R10={context.R10:X16} R11={context.R11:X16}");
        Console.WriteLine($"  R12={context.R12:X16} R13={context.R13:X16} R14={context.R14:X16} R15={context.R15:X16}");
        Console.WriteLine($"  bytes 0x{start:X16}: {code}");
        var stack = new byte[256];
        if (ReadProcessMemory(process, unchecked((nint)context.Rsp), stack,
            (nuint)stack.Length, out nuint stackRead))
        {
            var callers = new List<string>();
            for (int offset = 0; offset + 8 <= checked((int)stackRead); offset += 8)
            {
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(stack.AsSpan(offset, 8));
                if (value >= moduleBase && value < moduleBase + 0x100000000UL)
                    callers.Add($"+0x{offset:X}=GRW+0x{value - moduleBase:X}");
            }
            Console.WriteLine($"  stack {string.Join(" ", callers)}");
        }
    }

    static bool ReadContext(uint threadId, out Context64 context)
    {
        context = new Context64 { ContextFlags = ContextControl | ContextInteger | ContextDebugRegisters };
        nint thread = OpenThread(ThreadGetContext | ThreadQueryInformation, false, threadId);
        if (thread == 0)
            return false;
        try
        {
            return GetContext(thread, ref context);
        }
        finally
        {
            CloseHandle(thread);
        }
    }

    static void WriteContext(uint threadId, Context64 context)
    {
        nint thread = OpenThread(ThreadSetContext | ThreadQueryInformation, false, threadId);
        if (thread == 0)
            return;
        try
        {
            SetContext(thread, ref context);
        }
        finally
        {
            CloseHandle(thread);
        }
    }

    static bool SetBreakpoint(uint threadId, ulong[] addresses, bool enabled, bool report)
    {
        nint thread = OpenThread(ThreadSuspendResume | ThreadGetContext | ThreadSetContext
            | ThreadQueryInformation, false, threadId);
        if (thread == 0)
        {
            if (report)
                Console.WriteLine($"thread {threadId}: OpenThread failed, Windows error {Marshal.GetLastWin32Error()}");
            return false;
        }
        try
        {
            uint suspended = SuspendThread(thread);
            if (suspended == uint.MaxValue)
            {
                if (report)
                    Console.WriteLine($"thread {threadId}: SuspendThread failed, Windows error {Marshal.GetLastWin32Error()}");
                return false;
            }
            try
            {
                var context = new Context64 { ContextFlags = ContextDebugRegisters };
                if (!GetContext(thread, ref context))
                {
                    if (report)
                        Console.WriteLine($"thread {threadId}: GetThreadContext failed, Windows error {Marshal.GetLastWin32Error()}");
                    return false;
                }
                context.Dr0 = enabled && addresses.Length > 0 ? addresses[0] : 0;
                context.Dr1 = enabled && addresses.Length > 1 ? addresses[1] : 0;
                context.Dr2 = enabled && addresses.Length > 2 ? addresses[2] : 0;
                context.Dr3 = enabled && addresses.Length > 3 ? addresses[3] : 0;
                context.Dr6 = 0;
                context.Dr7 &= ~(0xFFUL | (0xFFFFUL << 16));
                ulong requiredDr7 = 0;
                if (enabled)
                {
                    for (int index = 0; index < addresses.Length; index++)
                    {
                        requiredDr7 |= 1UL << (index * 2);
                        requiredDr7 |= 0xFUL << (16 + index * 4);
                    }
                    context.Dr7 |= requiredDr7;
                }
                context.ContextFlags = ContextDebugRegisters;
                if (!SetContext(thread, ref context))
                {
                    if (report)
                        Console.WriteLine($"thread {threadId}: SetThreadContext failed, Windows error {Marshal.GetLastWin32Error()}");
                    return false;
                }
                var verify = new Context64 { ContextFlags = ContextDebugRegisters };
                if (!GetContext(thread, ref verify))
                    return false;
                bool correct = (verify.Dr7 & requiredDr7) == requiredDr7;
                for (int index = 0; enabled && index < addresses.Length; index++)
                    correct &= GetDebugAddress(verify, index) == addresses[index];
                if (!correct && report)
                    Console.WriteLine($"thread {threadId}: verify DR0=0x{verify.Dr0:X16} DR1=0x{verify.Dr1:X16} DR2=0x{verify.Dr2:X16} DR3=0x{verify.Dr3:X16} DR7=0x{verify.Dr7:X16}");
                return correct;
            }
            finally
            {
                ResumeThread(thread);
            }
        }
        finally
        {
            CloseHandle(thread);
        }
    }

    static ulong GetDebugAddress(Context64 context, int index) => index switch
    {
        0 => context.Dr0,
        1 => context.Dr1,
        2 => context.Dr2,
        3 => context.Dr3,
        _ => 0,
    };
    static bool GetContext(nint thread, ref Context64 context)
    {
        nint allocation = Marshal.AllocHGlobal(1248);
        nint aligned = unchecked((nint)(((ulong)allocation + 15UL) & ~15UL));
        try
        {
            Marshal.StructureToPtr(context, aligned, false);
            bool result = GetThreadContext(thread, aligned);
            if (result)
                context = Marshal.PtrToStructure<Context64>(aligned);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(allocation);
        }
    }

    static bool SetContext(nint thread, ref Context64 context)
    {
        nint allocation = Marshal.AllocHGlobal(1248);
        nint aligned = unchecked((nint)(((ulong)allocation + 15UL) & ~15UL));
        try
        {
            Marshal.StructureToPtr(context, aligned, false);
            return SetThreadContext(thread, aligned);
        }
        finally
        {
            Marshal.FreeHGlobal(allocation);
        }
    }
    static Process ResolveProcess(string text)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int id))
            return Process.GetProcessById(id);
        string name = Path.GetFileNameWithoutExtension(text);
        Process[] matches = Process.GetProcessesByName(name);
        return matches.Length switch
        {
            0 => throw new InvalidOperationException($"no running process named {name}"),
            1 => matches[0],
            _ => throw new InvalidOperationException("more than one matching process; use a pid"),
        };
    }

    static ulong ParseUnsigned(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.Parse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
        : ulong.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    [StructLayout(LayoutKind.Explicit, Size = 1232)]
    struct Context64
    {
        [FieldOffset(0x30)] public uint ContextFlags;
        [FieldOffset(0x44)] public uint EFlags;
        [FieldOffset(0x48)] public ulong Dr0;
        [FieldOffset(0x50)] public ulong Dr1;
        [FieldOffset(0x58)] public ulong Dr2;
        [FieldOffset(0x60)] public ulong Dr3;
        [FieldOffset(0x68)] public ulong Dr6;
        [FieldOffset(0x70)] public ulong Dr7;
        [FieldOffset(0x78)] public ulong Rax;
        [FieldOffset(0x80)] public ulong Rcx;
        [FieldOffset(0x88)] public ulong Rdx;
        [FieldOffset(0x90)] public ulong Rbx;
        [FieldOffset(0x98)] public ulong Rsp;
        [FieldOffset(0xA0)] public ulong Rbp;
        [FieldOffset(0xA8)] public ulong Rsi;
        [FieldOffset(0xB0)] public ulong Rdi;
        [FieldOffset(0xB8)] public ulong R8;
        [FieldOffset(0xC0)] public ulong R9;
        [FieldOffset(0xC8)] public ulong R10;
        [FieldOffset(0xD0)] public ulong R11;
        [FieldOffset(0xD8)] public ulong R12;
        [FieldOffset(0xE0)] public ulong R13;
        [FieldOffset(0xE8)] public ulong R14;
        [FieldOffset(0xF0)] public ulong R15;
        [FieldOffset(0xF8)] public ulong Rip;
    }

    [StructLayout(LayoutKind.Explicit, Size = 176)]
    struct DebugEvent
    {
        [FieldOffset(0)] public uint Code;
        [FieldOffset(4)] public uint ProcessId;
        [FieldOffset(8)] public uint ThreadId;
        [FieldOffset(16)] public uint ExceptionCode;
    }

    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DebugActiveProcess(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DebugActiveProcessStop(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool DebugSetProcessKillOnExit(bool killOnExit);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WaitForDebugEvent(out DebugEvent debugEvent, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ContinueDebugEvent(uint processId, uint threadId, uint continueStatus);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint SuspendThread(nint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetThreadContext(nint thread, nint context);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool SetThreadContext(nint thread, nint context);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool ReadProcessMemory(nint process, nint baseAddress, byte[] buffer,
        nuint size, out nuint bytesRead);

    [DllImport("kernel32.dll")]
    static extern bool CloseHandle(nint handle);
}
