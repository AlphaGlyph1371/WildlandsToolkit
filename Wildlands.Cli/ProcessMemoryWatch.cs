using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class ProcessMemoryWatch
{
    const uint ProcessVmRead = 0x0010;
    const uint ProcessQueryInformation = 0x0400;
    const uint MemCommit = 0x1000;
    const uint PageNoAccess = 0x01;
    const uint PageGuard = 0x100;
    const int ChunkSize = 4 * 1024 * 1024;
    const int ContextBytes = 24;
    const int ComparisonContextBytes = 96;
    const int MaxHitsPerValue = 100_000;

    public static int Run(string processText, IReadOnlyList<string> valueTexts)
    {
        List<SearchValue> values;
        try
        {
            values = valueTexts.Select(ParseValue).ToList();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }

        return WithProcessHandle(processText, (process, handle) =>
        {
            Console.WriteLine($"watching {process.ProcessName} ({process.Id})");
            foreach (SearchValue value in values)
                Console.WriteLine($"  {value.Label}  {Convert.ToHexString(value.Bytes)}");

            Console.WriteLine();
            Console.Write("Select a working stock attachment, wait for its preview, then press Enter: ");
            Console.ReadLine();
            Snapshot before = Capture(handle, values);
            PrintSnapshot("stock", before, values);

            Console.WriteLine();
            Console.Write("Select the WT attachment, wait for the black preview, then press Enter: ");
            Console.ReadLine();
            Snapshot after = Capture(handle, values);
            PrintSnapshot("WT", after, values);

            Console.WriteLine();
            PrintDifference(before, after, values);
            return 0;
        });
    }

    public static int Find(string processText, IReadOnlyList<string> valueTexts)
    {
        List<SearchValue> values;
        try
        {
            values = valueTexts.Select(ParseValue).ToList();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }

        return WithProcessHandle(processText, (_, handle) =>
        {
            Snapshot snapshot = Capture(handle, values);
            PrintSnapshot("current", snapshot, values);
            foreach (SearchValue value in values)
            {
                Console.WriteLine(value.Label + ":");
                PrintAddresses(snapshot, "=", snapshot.Hits[value.Label].Order().ToList());
            }
            return 0;
        });
    }

    // The first value is the locked object. Every later value is a known-working
    // peer of the same kind. One full-process scan finds the ids, then this ranks
    // their surrounding runtime records by byte similarity. This avoids drowning
    // the unlock investigation in unrelated copies from archive buffers and code.
    public static int CompareContexts(string processText, IReadOnlyList<string> valueTexts)
    {
        List<SearchValue> values;
        try
        {
            values = valueTexts.Select(ParseValue).ToList();
            if (values.Count < 2)
                throw new ArgumentException("provide one locked value and at least one known-working value");
            if (values.Any(value => value.Bytes.Length != values[0].Bytes.Length))
                throw new ArgumentException("all compared values must use the same width (u32 or u64)");
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }

        return WithProcessHandle(processText, (process, handle) =>
        {
            Console.WriteLine($"comparing runtime contexts in {process.ProcessName} ({process.Id})");
            Snapshot snapshot = Capture(handle, values);
            PrintSnapshot("current", snapshot, values);

            SearchValue locked = values[0];
            var lockedWindows = snapshot.Hits[locked.Label]
                .Order()
                .Select(address => ReadComparisonWindow(handle, address, locked.Bytes.Length))
                .Where(window => window is not null)
                .Cast<ContextWindow>()
                .ToList();
            if (lockedWindows.Count == 0)
            {
                Console.WriteLine($"no readable contexts for {locked.Label}");
                return 1;
            }

            foreach (SearchValue working in values.Skip(1))
            {
                var workingWindows = snapshot.Hits[working.Label]
                    .Order()
                    .Select(address => ReadComparisonWindow(handle, address, working.Bytes.Length))
                    .Where(window => window is not null)
                    .Cast<ContextWindow>()
                    .ToList();
                Console.WriteLine();
                Console.WriteLine($"{locked.Label} versus {working.Label}:");
                if (workingWindows.Count == 0)
                {
                    Console.WriteLine("  no readable working contexts");
                    continue;
                }

                var matches = (from lockedWindow in lockedWindows
                               from workingWindow in workingWindows
                               let score = Similarity(lockedWindow.Bytes, workingWindow.Bytes,
                                   ComparisonContextBytes, locked.Bytes.Length)
                               orderby score descending, lockedWindow.Address, workingWindow.Address
                               select new ContextMatch(lockedWindow, workingWindow, score))
                    .Take(12)
                    .ToList();

                foreach (ContextMatch match in matches)
                {
                    Console.WriteLine($"  {match.Score:P1}  locked 0x{match.Locked.Address:X16}  working 0x{match.Working.Address:X16}");
                    Console.WriteLine("    locked  " + FormatComparisonWindow(match.Locked.Bytes, locked.Bytes.Length));
                    Console.WriteLine("    working " + FormatComparisonWindow(match.Working.Bytes, working.Bytes.Length));
                    Console.WriteLine("    differs " + DescribeDifferences(match.Locked.Bytes,
                        match.Working.Bytes, ComparisonContextBytes, locked.Bytes.Length));
                }
            }
            return 0;
        });
    }

    static ContextWindow? ReadComparisonWindow(nint process, ulong address, int valueLength)
    {
        ulong start = address >= ComparisonContextBytes ? address - ComparisonContextBytes : 0;
        var buffer = new byte[ComparisonContextBytes * 2 + valueLength];
        if (!ReadProcessMemory(process, unchecked((nint)start), buffer,
                (nuint)buffer.Length, out nuint bytesRead)
            || bytesRead != (nuint)buffer.Length)
            return null;
        return new ContextWindow(address, buffer);
    }

    static double Similarity(byte[] left, byte[] right, int center, int valueLength)
    {
        int equal = 0;
        int compared = 0;
        for (int index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            if (index >= center && index < center + valueLength)
                continue;
            compared++;
            if (left[index] == right[index])
                equal++;
        }
        return compared == 0 ? 0 : (double)equal / compared;
    }

    static string FormatComparisonWindow(byte[] bytes, int valueLength)
    {
        const int shown = 32;
        int center = ComparisonContextBytes;
        return Convert.ToHexString(bytes.AsSpan(center - shown, shown))
            + " [" + Convert.ToHexString(bytes.AsSpan(center, valueLength)) + "] "
            + Convert.ToHexString(bytes.AsSpan(center + valueLength, shown));
    }

    static string DescribeDifferences(byte[] left, byte[] right, int center, int valueLength)
    {
        var ranges = new List<string>();
        int index = 0;
        while (index < Math.Min(left.Length, right.Length))
        {
            if (index >= center && index < center + valueLength || left[index] == right[index])
            {
                index++;
                continue;
            }

            int start = index;
            while (index + 1 < Math.Min(left.Length, right.Length)
                && !(index + 1 >= center && index + 1 < center + valueLength)
                && left[index + 1] != right[index + 1])
                index++;
            int end = index;
            ranges.Add(start == end ? RelativeOffset(start, center, valueLength)
                : RelativeOffset(start, center, valueLength) + ".." + RelativeOffset(end, center, valueLength));
            index++;
        }
        return ranges.Count == 0 ? "only the searched id" : string.Join(", ", ranges.Take(24))
            + (ranges.Count > 24 ? $", ... ({ranges.Count - 24} more)" : "");
    }

    static string RelativeOffset(int index, int center, int valueLength)
    {
        int relative = index < center ? index - center : index - center - valueLength;
        return relative >= 0 ? $"+0x{relative:X}" : $"-0x{-relative:X}";
    }

    public static int Save(string processText, string outputPath, IReadOnlyList<string> valueTexts)
    {
        List<SearchValue> values;
        try
        {
            values = valueTexts.Select(ParseValue).ToList();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }

        return WithProcessHandle(processText, (_, handle) =>
        {
            Snapshot snapshot = Capture(handle, values);
            PrintSnapshot(Path.GetFileNameWithoutExtension(outputPath), snapshot, values);
            var saved = new SavedSnapshot(
                snapshot.Hits.ToDictionary(pair => pair.Key, pair => pair.Value.Order().ToArray()),
                snapshot.Contexts.ToDictionary(), snapshot.Regions, snapshot.Bytes);
            File.WriteAllText(outputPath, JsonSerializer.Serialize(saved));
            Console.WriteLine("saved " + Path.GetFullPath(outputPath));
            return 0;
        });
    }

    public static int Compare(string beforePath, string afterPath)
    {
        SavedSnapshot beforeSaved = JsonSerializer.Deserialize<SavedSnapshot>(
            File.ReadAllText(beforePath))
            ?? throw new InvalidDataException("The first memory snapshot is empty.");
        SavedSnapshot afterSaved = JsonSerializer.Deserialize<SavedSnapshot>(
            File.ReadAllText(afterPath))
            ?? throw new InvalidDataException("The second memory snapshot is empty.");
        Snapshot before = Restore(beforeSaved);
        Snapshot after = Restore(afterSaved);
        var labels = before.Hits.Keys.Union(after.Hits.Keys)
            .Select(label => new SearchValue(label, []))
            .ToList();
        PrintDifference(before, after, labels);
        return 0;
    }

    static Snapshot Restore(SavedSnapshot saved) => new(
        saved.Hits.ToDictionary(pair => pair.Key, pair => pair.Value.ToHashSet()),
        saved.Contexts, saved.Regions, saved.Bytes);

    public static int Read(string processText, string addressText, string lengthText)
    {
        ulong address;
        int length;
        try
        {
            address = ParseUnsigned(addressText);
            ulong parsedLength = ParseUnsigned(lengthText);
            if (parsedLength is 0 or > 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(lengthText),
                    "length must be between 1 byte and 1 MiB");
            length = (int)parsedLength;
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }

        return WithProcessHandle(processText, (_, handle) =>
        {
            var buffer = new byte[length];
            if (!ReadProcessMemory(handle, unchecked((nint)address), buffer,
                    (nuint)buffer.Length, out nuint bytesRead)
                || bytesRead == 0)
            {
                Console.WriteLine($"could not read 0x{address:X}: Windows error {Marshal.GetLastWin32Error()}");
                return 1;
            }

            int read = checked((int)bytesRead);
            for (int offset = 0; offset < read; offset += 16)
            {
                int rowLength = Math.Min(16, read - offset);
                ReadOnlySpan<byte> row = buffer.AsSpan(offset, rowLength);
                string hex = Convert.ToHexString(row);
                string ascii = new(row.ToArray().Select(value => value is >= 32 and < 127
                    ? (char)value : '.').ToArray());
                Console.WriteLine($"{address + (ulong)offset:X16}  {hex,-32}  {ascii}");
            }
            return 0;
        });
    }

    public static int SaveRegion(string processText, string addressText, string outputPath)
    {
        ulong address;
        try
        {
            address = ParseUnsigned(addressText);
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }

        return WithProcessHandle(processText, (_, handle) =>
        {
            int informationSize = Marshal.SizeOf<MemoryBasicInformation>();
            if (VirtualQueryEx(handle, unchecked((nint)address), out MemoryBasicInformation information,
                    (nuint)informationSize) == 0)
            {
                Console.WriteLine($"could not query 0x{address:X}: Windows error {Marshal.GetLastWin32Error()}");
                return 1;
            }

            ulong regionBase = unchecked((ulong)information.BaseAddress);
            ulong regionSize = (ulong)information.RegionSize;
            if (information.State != MemCommit
                || (information.Protect & (PageNoAccess | PageGuard)) != 0)
            {
                Console.WriteLine($"region 0x{regionBase:X}-0x{regionBase + regionSize:X} is not readable");
                return 1;
            }
            if (regionSize > 512UL * 1024 * 1024)
            {
                Console.WriteLine($"region is too large to save: {FormatBytes((long)regionSize)}");
                return 1;
            }

            var buffer = new byte[checked((int)regionSize)];
            if (!ReadProcessMemory(handle, information.BaseAddress, buffer, (nuint)buffer.Length,
                    out nuint bytesRead)
                || bytesRead == 0)
            {
                Console.WriteLine($"could not read region 0x{regionBase:X}: Windows error {Marshal.GetLastWin32Error()}");
                return 1;
            }

            int read = checked((int)bytesRead);
            File.WriteAllBytes(outputPath, buffer.AsSpan(0, read).ToArray());
            Console.WriteLine($"saved 0x{regionBase:X16}-0x{regionBase + (ulong)read:X16} "
                + $"({FormatBytes(read)}) to {Path.GetFullPath(outputPath)}");
            Console.WriteLine($"requested address is at file offset 0x{address - regionBase:X}");
            return 0;
        });
    }

    static int WithProcessHandle(string processText, Func<Process, nint, int> action)
    {
        Process process;
        try
        {
            process = ResolveProcess(processText);
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }

        nint handle = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, process.Id);
        if (handle == 0)
        {
            Console.WriteLine($"could not open {process.ProcessName} ({process.Id}): Windows error {Marshal.GetLastWin32Error()}");
            return 1;
        }

        try
        {
            return action(process, handle);
        }
        finally
        {
            CloseHandle(handle);
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
            _ => throw new InvalidOperationException(
                $"more than one process is named {name}; use one of these pids: "
                + string.Join(", ", matches.Select(process => process.Id)))
        };
    }

    static SearchValue ParseValue(string text)
    {
        int separator = text.IndexOf(':');
        if (separator <= 0 || separator == text.Length - 1)
            throw new FormatException($"{text}: expected u32:0xvalue or u64:0xvalue");

        string type = text[..separator].ToLowerInvariant();
        string number = text[(separator + 1)..];
        ulong value = number.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(number[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
            : ulong.Parse(number, NumberStyles.None, CultureInfo.InvariantCulture);

        return type switch
        {
            "u32" when value <= uint.MaxValue => new SearchValue($"u32:0x{value:X8}",
                BitConverter.GetBytes((uint)value)),
            "u64" => new SearchValue($"u64:0x{value:X16}", BitConverter.GetBytes(value)),
            "u32" => throw new OverflowException($"{text}: the value does not fit in 32 bits"),
            _ => throw new FormatException($"{text}: expected u32:0xvalue or u64:0xvalue")
        };
    }

    static ulong ParseUnsigned(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.Parse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
        : ulong.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    static Snapshot Capture(nint process, IReadOnlyList<SearchValue> values)
    {
        var hits = values.ToDictionary(value => value.Label, _ => new HashSet<ulong>());
        ulong address = 0;
        int regions = 0;
        long bytes = 0;
        int informationSize = Marshal.SizeOf<MemoryBasicInformation>();

        while (VirtualQueryEx(process, unchecked((nint)address), out MemoryBasicInformation information,
                   (nuint)informationSize) != 0)
        {
            ulong regionBase = unchecked((ulong)information.BaseAddress);
            ulong regionSize = (ulong)information.RegionSize;
            ulong next = regionBase + regionSize;
            if (next <= address)
                break;

            if (information.State == MemCommit
                && (information.Protect & (PageNoAccess | PageGuard)) == 0)
            {
                ScanRegion(process, regionBase, regionSize, values, hits);
                regions++;
                bytes += regionSize > long.MaxValue ? long.MaxValue : (long)regionSize;
            }

            address = next;
        }

        var contexts = hits.Values.SelectMany(valueHits => valueHits)
            .Distinct()
            .ToDictionary(hit => hit, hit => ReadContext(process, hit));
        return new Snapshot(hits, contexts, regions, bytes);
    }

    static void ScanRegion(nint process, ulong regionBase, ulong regionSize,
        IReadOnlyList<SearchValue> values, IReadOnlyDictionary<string, HashSet<ulong>> hits)
    {
        int overlap = Math.Max(0, values.Max(value => value.Bytes.Length) - 1);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            ulong offset = 0;
            while (offset < regionSize)
            {
                int requested = (int)Math.Min((ulong)ChunkSize, regionSize - offset);
                if (!ReadProcessMemory(process, unchecked((nint)(regionBase + offset)), buffer,
                        (nuint)requested, out nuint bytesRead)
                    || bytesRead == 0)
                {
                    offset += (ulong)requested;
                    continue;
                }

                int length = checked((int)bytesRead);
                foreach (SearchValue value in values)
                {
                    HashSet<ulong> valueHits = hits[value.Label];
                    if (valueHits.Count >= MaxHitsPerValue)
                        continue;

                    ReadOnlySpan<byte> remaining = buffer.AsSpan(0, length);
                    int consumed = 0;
                    while (remaining.Length >= value.Bytes.Length)
                    {
                        int relative = remaining.IndexOf(value.Bytes);
                        if (relative < 0)
                            break;
                        int found = consumed + relative;
                        valueHits.Add(regionBase + offset + (ulong)found);
                        consumed = found + 1;
                        remaining = buffer.AsSpan(consumed, length - consumed);
                        if (valueHits.Count >= MaxHitsPerValue)
                            break;
                    }
                }

                if ((ulong)length >= regionSize - offset)
                    break;
                int advance = Math.Max(1, length - overlap);
                offset += (ulong)advance;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    static void PrintSnapshot(string name, Snapshot snapshot, IReadOnlyList<SearchValue> values)
    {
        Console.WriteLine($"{name}: scanned {snapshot.Regions:N0} readable regions, {FormatBytes(snapshot.Bytes)}");
        foreach (SearchValue value in values)
        {
            int count = snapshot.Hits[value.Label].Count;
            string suffix = count >= MaxHitsPerValue ? "+ (limit reached)" : "";
            Console.WriteLine($"  {value.Label}: {count:N0}{suffix}");
        }
    }

    static void PrintDifference(Snapshot before, Snapshot after,
        IReadOnlyList<SearchValue> values)
    {
        PrintReplacements(before, after, values);
        foreach (SearchValue value in values)
        {
            HashSet<ulong> beforeHits = before.Hits[value.Label];
            HashSet<ulong> afterHits = after.Hits[value.Label];
            var added = afterHits.Except(beforeHits).Order().ToList();
            var removed = beforeHits.Except(afterHits).Order().ToList();
            Console.WriteLine($"{value.Label}: +{added.Count:N0} / -{removed.Count:N0}");
            PrintAddresses(after, "+", added);
            PrintAddresses(before, "-", removed);
        }
    }

    static void PrintReplacements(Snapshot before, Snapshot after,
        IReadOnlyList<SearchValue> values)
    {
        var beforeAt = ValuesByAddress(before, values);
        var afterAt = ValuesByAddress(after, values);
        var changed = beforeAt.Keys.Intersect(afterAt.Keys)
            .Where(address => !beforeAt[address].SequenceEqual(afterAt[address]))
            .Order()
            .ToList();
        if (changed.Count == 0)
            return;

        Console.WriteLine("replacements at the same address:");
        foreach (ulong address in changed.Take(100))
        {
            Console.WriteLine($"  0x{address:X16}  {string.Join(", ", beforeAt[address])} -> "
                + string.Join(", ", afterAt[address]));
            Console.WriteLine("    stock " + before.Contexts[address]);
            Console.WriteLine("    WT    " + after.Contexts[address]);
        }
        if (changed.Count > 100)
            Console.WriteLine($"  ... {changed.Count - 100:N0} more");
    }

    static Dictionary<ulong, List<string>> ValuesByAddress(Snapshot snapshot,
        IReadOnlyList<SearchValue> values)
    {
        var result = new Dictionary<ulong, List<string>>();
        foreach (SearchValue value in values)
        {
            foreach (ulong address in snapshot.Hits[value.Label])
            {
                if (!result.TryGetValue(address, out List<string>? labels))
                    result[address] = labels = [];
                labels.Add(value.Label);
            }
        }
        return result;
    }

    static void PrintAddresses(Snapshot snapshot, string prefix, IReadOnlyList<ulong> addresses)
    {
        const int shown = 100;
        foreach (ulong address in addresses.Take(shown))
            Console.WriteLine($"  {prefix} 0x{address:X16}  {snapshot.Contexts[address]}");
        if (addresses.Count > shown)
            Console.WriteLine($"  ... {addresses.Count - shown:N0} more");
    }

    static string ReadContext(nint process, ulong address)
    {
        ulong start = address >= ContextBytes ? address - ContextBytes : 0;
        var buffer = new byte[ContextBytes * 2 + sizeof(ulong)];
        if (!ReadProcessMemory(process, unchecked((nint)start), buffer,
                (nuint)buffer.Length, out nuint bytesRead)
            || bytesRead == 0)
            return "<unreadable>";
        return Convert.ToHexString(buffer.AsSpan(0, checked((int)bytesRead)));
    }

    static string FormatBytes(long bytes)
    {
        double gibibytes = bytes / 1024d / 1024d / 1024d;
        return gibibytes >= 1 ? $"{gibibytes:N2} GiB" : $"{bytes / 1024d / 1024d:N1} MiB";
    }

    sealed record SearchValue(string Label, byte[] Bytes);
    sealed record ContextWindow(ulong Address, byte[] Bytes);
    sealed record ContextMatch(ContextWindow Locked, ContextWindow Working, double Score);
    sealed record Snapshot(IReadOnlyDictionary<string, HashSet<ulong>> Hits,
        IReadOnlyDictionary<ulong, string> Contexts, int Regions, long Bytes);
    sealed record SavedSnapshot(Dictionary<string, ulong[]> Hits,
        Dictionary<ulong, string> Contexts, int Regions, long Bytes);

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool ReadProcessMemory(nint process, nint baseAddress, byte[] buffer,
        nuint size, out nuint bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern nuint VirtualQueryEx(nint process, nint address,
        out MemoryBasicInformation information, nuint length);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseHandle(nint handle);
}
