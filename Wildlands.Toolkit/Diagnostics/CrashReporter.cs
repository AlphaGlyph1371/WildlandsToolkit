using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Wildlands.Toolkit;

internal static class CrashReporter
{
    static readonly object ReportGate = new();
    static string? _lastFingerprint;
    static DateTime _lastReportAt;
    static int _reportNumber;
    static int _dialogOpen;

    public static void Report(Exception exception, string source, bool terminating)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string fingerprint = $"{source}\n{exception}";
        lock (ReportGate)
        {
            DateTime now = DateTime.UtcNow;
            if (fingerprint == _lastFingerprint
                && now - _lastReportAt < TimeSpan.FromSeconds(5))
                return;
            _lastFingerprint = fingerprint;
            _lastReportAt = now;
        }

        string? path = WriteReport(exception, source, terminating);
        ShowMessage(exception, path, terminating);
    }

    static string? WriteReport(Exception exception, string source, bool terminating)
    {
        try
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "WildlandsToolkit", "Crashes");
            Directory.CreateDirectory(folder);
            int number = Interlocked.Increment(ref _reportNumber);
            string path = Path.Combine(folder,
                $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}-{number}.log");
            var text = new StringBuilder()
                .AppendLine($"Time: {DateTimeOffset.Now:O}")
                .AppendLine($"Source: {source}")
                .AppendLine($"Process terminating: {terminating}")
                .AppendLine($"Toolkit: {Assembly.GetEntryAssembly()?.GetName().Version}")
                .AppendLine($"Runtime: {Environment.Version}")
                .AppendLine($"OS: {Environment.OSVersion}")
                .AppendLine($"Command line: {Environment.CommandLine}")
                .AppendLine()
                .AppendLine(exception.ToString());
            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            return path;
        }
        catch
        {
            return null;
        }
    }

    static void ShowMessage(Exception exception, string? reportPath, bool terminating)
    {
        if (Interlocked.CompareExchange(ref _dialogOpen, 1, 0) != 0)
            return;

        void Show()
        {
            try
            {
                string state = terminating
                    ? "The Toolkit must close."
                    : "The error was caught and the Toolkit can remain open.";
                string report = reportPath is null
                    ? "The crash report could not be written."
                    : $"Crash report:\n{reportPath}";
                MessageBox.Show(
                    $"An unexpected error occurred.\n\n{exception.Message}\n\n{state}\n\n{report}",
                    "Wildlands Toolkit", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                Debug.WriteLine(exception);
            }
            finally
            {
                Interlocked.Exchange(ref _dialogOpen, 0);
            }
        }

        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted)
        {
            Show();
            return;
        }
        if (dispatcher.CheckAccess())
            Show();
        else
            _ = dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)Show);
    }
}
