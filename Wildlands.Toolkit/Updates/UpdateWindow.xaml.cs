using System.Diagnostics;
using System.Windows;

namespace Wildlands.Toolkit;

public partial class UpdateWindow : Window
{
    readonly Uri _releasePage;

    public UpdateWindow(ToolkitUpdate update)
    {
        InitializeComponent();
        _releasePage = update.ReleasePage;

        VersionText.Text = $"Installed: {update.LocalVersion}   ·   "
            + $"Available: {update.Tag}   ·   {update.Name}";
        NotesText.Text = update.Notes.Length > 0
            ? update.Notes
            : "No release notes were provided for this version.";
    }

    void Later_Click(object sender, RoutedEventArgs e) => Close();

    void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_releasePage.AbsoluteUri)
            {
                UseShellExecute = true,
            });
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not open the release page",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
