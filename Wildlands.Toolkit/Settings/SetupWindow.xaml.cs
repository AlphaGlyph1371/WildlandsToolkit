using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public partial class SetupWindow : Window
{
    public string GamePath => PathBox.Text.Trim();

    public SetupWindow(string current)
    {
        InitializeComponent();

        PathBox.Text = current.Length > 0 ? current : GameLocator.FindGameFolder() ?? "";

        if (PathBox.Text.Length > 0 && current.Length == 0)
            HintText.Text = "Found this installation automatically.";
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the Wildlands folder" };
        if (PathBox.Text.Length > 0 && Directory.Exists(PathBox.Text))
            dialog.InitialDirectory = PathBox.Text;

        if (dialog.ShowDialog() == true)
            PathBox.Text = dialog.FolderName;
    }

    void Path_Changed(object sender, TextChangedEventArgs e)
    {
        string path = GamePath;

        if (path.Length == 0)
        {
            HintText.Text = "";
            ContinueButton.IsEnabled = false;
            return;
        }

        if (!Directory.Exists(path))
        {
            HintText.Text = "That folder does not exist.";
            ContinueButton.IsEnabled = false;
            return;
        }

        int archives = ArchiveLocator.Find(path).Count;

        if (!GameLocator.LooksLikeGameFolder(path))
        {
            HintText.Text = archives > 0
                ? $"No GRW.exe here, but {archives} forge archives were found. You can continue."
                : "No GRW.exe and no forge archives here.";
            ContinueButton.IsEnabled = archives > 0;
            return;
        }

        HintText.Text = $"Wildlands found, {archives} forge archives.";
        ContinueButton.IsEnabled = true;
    }

    void Continue_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
