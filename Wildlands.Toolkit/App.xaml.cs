using System.Windows;
using System.Windows.Threading;

namespace Wildlands.Toolkit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        base.OnExit(e);
    }

    static void OnDispatcherUnhandledException(object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            CrashReporter.Report(e.Exception, "UI thread", terminating: false);
        }
        finally
        {
            e.Handled = true;
        }
    }

    static void OnUnobservedTaskException(object? sender,
        UnobservedTaskExceptionEventArgs e)
    {
        try
        {
            CrashReporter.Report(e.Exception.Flatten(), "background task", terminating: false);
        }
        finally
        {
            e.SetObserved();
        }
    }

    static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Exception exception = e.ExceptionObject as Exception
            ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown process error");
        CrashReporter.Report(exception, "process", e.IsTerminating);
    }
}
