using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Wildlands.Toolkit;

public partial class MainWindow
{
    void ProjectMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ProjectButton.ContextMenu is not { } menu)
            return;
        menu.PlacementTarget = ProjectButton;
        menu.IsOpen = true;
    }

    async void NewProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmProjectSwitch())
            return;

        var details = new ModProjectWindow { Owner = this };
        if (details.ShowDialog() != true)
            return;

        var dialog = new OpenFolderDialog
        {
            Title = "Choose or create the project folder",
            InitialDirectory = Directory.Exists(_settings.ExportFolder)
                ? _settings.ExportFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            ModProject project = ModProject.CreateInFolder(dialog.FolderName, details.ProjectName, details.Author, details.ProjectVersion);
            await ActivateProjectAsync(project);
            SetStatus($"Created mod project {project.Name}");
        }
        catch (Exception ex)
        {
            ShowError("Could not create the mod project", ex);
        }
    }

    async void OpenProject_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmProjectSwitch())
            return;

        var dialog = new OpenFileDialog
        {
            Title = "Open mod project",
            Filter = "Wildlands Toolkit project (*.wlproj)|*.wlproj|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
            await OpenProjectAsync(dialog.FileName);
    }

    void ProjectSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null)
            return;

        var details = new ModProjectWindow(_project) { Owner = this };
        if (details.ShowDialog() != true)
            return;

        string oldName = _project.Name;
        string oldAuthor = _project.Author;
        string oldVersion = _project.Version;
        try
        {
            _project.Name = details.ProjectName;
            _project.Author = details.Author;
            _project.Version = details.ProjectVersion;
            _project.Save();
            UpdateProjectUi();
            SetStatus($"Saved project settings for {_project.Name}");
        }
        catch (Exception ex)
        {
            _project.Name = oldName;
            _project.Author = oldAuthor;
            _project.Version = oldVersion;
            ShowError("Could not save the project settings", ex);
        }
    }

    async void ReloadProject_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null)
            return;
        if (_changes.Count > 0 && MessageBox.Show(this, "Replace the current pending changes with the changes saved in this project?", "Reload project changes", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            await ActivateProjectAsync(_project);
        }
        catch (Exception ex)
        {
            ShowError("Could not reload the mod project", ex);
        }
    }

    void BuildMod_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null)
            return;
        if (_project.Operations.Count == 0)
        {
            MessageBox.Show(this, "This project does not contain any changes yet.", "Nothing to build", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string currentHash;
        try
        {
            currentHash = _project.ContentHash();
        }
        catch (Exception ex)
        {
            ShowError("Could not validate the mod project", ex);
            return;
        }

        if (!currentHash.Equals(_project.LastDeployedHash, StringComparison.OrdinalIgnoreCase))
        {
            MessageBoxResult answer = MessageBox.Show(this,
                "This exact project revision has not been applied to the game yet. You can build it, but the package may not match what you tested in the game.\n\nBuild it anyway?",
                "Project changes are not tested yet", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
                return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Build mod package",
            Filter = "Wildlands Toolkit mod (*.wlmod)|*.wlmod",
            FileName = SafeFileName(_project.Name) + "-" + SafeFileName(_project.Version) + ".wlmod",
            InitialDirectory = Directory.Exists(_settings.ExportFolder) ? _settings.ExportFolder : Path.GetDirectoryName(_project.FilePath),
            AddExtension = true,
            DefaultExt = ".wlmod",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            _project.ExportPackage(dialog.FileName);
            SetStatus($"Built {Path.GetFileName(dialog.FileName)}");
            MessageBox.Show(this, $"Built {Path.GetFileName(dialog.FileName)}\n\nProject revision: {currentHash[..12]}", "Mod package ready", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowError("Could not build the mod package", ex);
        }
    }

    void CloseProject_Click(object sender, RoutedEventArgs e)
    {
        if (_project is null)
            return;
        if (_changes.Count > 0 && MessageBox.Show(this,
                $"Leave {_project.Name} and return to Free Mode?\n\n"
                + "All project changes remain saved in the project folder. "
                + "They will not be copied into Free Mode.",
                "Leave mod project", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _changes.Clear();
        _project = null;
        UpdateProjectUi();
        UpdateChangeButtons();
        ShowArchiveAgain();
        SetStatus("Project closed");
    }

    async Task OpenProjectAsync(string path)
    {
        try
        {
            ModProject project = await Task.Run(() => ModProject.Load(path));
            await ActivateProjectAsync(project);
        }
        catch (Exception ex)
        {
            ShowError("Could not open the mod project", ex);
        }
    }

    async Task<ModCompileResult> ActivateProjectAsync(ModProject project, bool installing = false)
    {
        SetApplying(true, $"Loading mod project {project.Name}…");
        ModCompileResult result;
        try
        {
            result = await Task.Run(() => project.Compile(_settings.GamePath));
        }
        finally
        {
            SetApplying(false);
        }

        _changes.Clear();
        _project = project;
        UpdateProjectUi();

        if (result.Problems.Count > 0)
        {
            UpdateChangeButtons();
            ShowArchiveAgain();
            MessageBox.Show(this, ConflictMessage(project, result, installing),
                installing ? "Mod does not fit your game" : "Mod project conflicts",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(installing
                ? $"{project.Name}: installation blocked by {result.Problems.Count} conflict(s)"
                : $"Opened {project.Name} with {result.Problems.Count} conflict(s)");
            return result;
        }

        foreach (PendingChange change in result.Changes)
            _changes.Set(change);
        UpdateChangeButtons();
        ShowArchiveAgain();
        SetStatus(project.Operations.Count == 0
            ? $"Opened {project.Name}: no changes yet"
            : result.Changes.Count > 0
                ? $"Opened {project.Name}: changes ready to apply"
                : $"Opened {project.Name}: project is already applied");
        return result;
    }

    static string ConflictMessage(ModProject project, ModCompileResult result, bool installing)
    {
        var others = result.Problems.Except(result.Mismatches).ToList();
        string message = "";

        if (result.Mismatches.Count > 0)
            message = installing
                ? $"{project.Name} does not fit the current version of your game, so it cannot be installed.\n\n"
                    + "This usually happens after a game update, or when another mod already changes the same things. "
                    + $"Nothing was changed.\n\nAsk the mod author for an updated version."
                : $"{project.Name} no longer fits the game files. {result.Mismatches.Count} of its changes expect "
                    + $"a different game version.\n\nThis usually happens after a game update, or when another mod "
                    + "is still installed. The project was opened, but its changes cannot be applied.";

        if (others.Count > 0)
        {
            string list = string.Join("\n", others.Take(5)) + (others.Count > 5 ? $"\n…and {others.Count - 5} more" : "");
            message = message.Length == 0
                ? (installing ? "The mod cannot be installed:" : "The project's changes cannot be applied:") + $"\n\n{list}"
                : message + $"\n\nAlso:\n{list}";
        }

        return message;
    }

    bool ConfirmProjectSwitch()
    {
        if (_changes.Count == 0)
            return true;

        int count = OperationCount(pendingOnly: true);
        string message = _project is null
            ? $"Discard {Amount(count, "pending Free Mode change")} and open a mod project?\n\n"
                + "Changes that were already applied to the game are not affected."
            : $"Switch away from {_project.Name}?\n\n"
                + "Its changes remain saved in that project folder and will not be copied into "
                + "the next project.";
        return MessageBox.Show(this, message, "Switch workspace",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    void UpdateProjectUi()
    {
        bool active = _project is not null;
        ProjectText.Text = active ? $"Project: {_project!.Name} ▾" : "Project: None ▾";
        ProjectText.Foreground = (Brush)FindResource(active ? "Text" : "TextDim");
        ProjectButton.ToolTip = active
            ? $"{_project!.Author.Default("Unknown author")} · {_project.Version}"
            : "Create or open a persistent mod project";
        BuildModMenu.IsEnabled = active;
        ProjectSettingsMenu.IsEnabled = active;
        ReloadProjectMenu.IsEnabled = active;
        CloseProjectMenu.IsEnabled = active;
    }

    static string SafeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        result = result.Trim().TrimEnd('.');
        return result.Length == 0 ? "mod" : result;
    }
}
