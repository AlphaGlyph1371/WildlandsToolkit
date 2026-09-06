using System.Windows;

namespace Wildlands.Toolkit;

public partial class ModProjectWindow : Window
{
    public string ProjectName => NameBox.Text.Trim();
    public string Author => AuthorBox.Text.Trim();
    public string ProjectVersion => VersionBox.Text.Trim();

    public ModProjectWindow(ModProject? project = null)
    {
        InitializeComponent();
        if (project is null)
        {
            AuthorBox.Text = Environment.UserName;
        }
        else
        {
            Title = "Project settings";
            HeadingText.Text = "Project settings";
            DescriptionText.Text = "These details are included in the .wlmod package. They do not change the tested game-content revision.";
            ConfirmButton.Content = "Save";
            NameBox.Text = project.Name;
            AuthorBox.Text = project.Author;
            VersionBox.Text = project.Version;
        }
        Loaded += (_, _) => NameBox.Focus();
    }

    void Create_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectName.Length == 0)
        {
            ErrorText.Text = "Enter a project name.";
            return;
        }
        if (ProjectVersion.Length == 0)
        {
            ErrorText.Text = "Enter a release version, for example 1.0.0 or 1.0.0-beta.";
            return;
        }

        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
