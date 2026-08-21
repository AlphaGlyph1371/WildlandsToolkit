using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Wildlands.Toolkit;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += ShowCrash;
    }

    static void ShowCrash(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var text = new StringBuilder();
        text.AppendLine("Something went wrong that the toolkit did not expect.");
        text.AppendLine();
        text.AppendLine(e.Exception.Message);
        text.AppendLine();
        text.AppendLine("Your archives are untouched unless a write was already running.");
        text.AppendLine("Please report this on the Discord server with the text below.");
        text.AppendLine();
        text.AppendLine(e.Exception.ToString());

        MessageBox.Show(text.ToString(), "Wildlands Toolkit", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
