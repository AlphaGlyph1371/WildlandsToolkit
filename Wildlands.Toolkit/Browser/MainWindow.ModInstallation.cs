using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Wildlands.Toolkit;

public partial class MainWindow
{
    async void OpenModPackage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Install mod package",
            Filter = "Wildlands Toolkit mod (*.wlmod)|*.wlmod",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) != true)
            return;

        await InstallModPackageAsync(dialog.FileName);
    }

    void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_loading && !_applying && TryGetDroppedModPackage(e.Data, out _)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    async void Window_PreviewDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_loading || _applying)
            return;
        if (!TryGetDroppedModPackage(e.Data, out string packagePath))
        {
            SetStatus("Drop exactly one .wlmod package to install it");
            return;
        }

        await InstallModPackageAsync(packagePath);
    }

    static bool TryGetDroppedModPackage(IDataObject data, out string path)
    {
        path = "";
        if (!data.GetDataPresent(DataFormats.FileDrop) || data.GetData(DataFormats.FileDrop) is not string[] { Length: 1 } files)
            return false;

        string candidate = files[0];
        if (!File.Exists(candidate) || !Path.GetExtension(candidate).Equals(".wlmod", StringComparison.OrdinalIgnoreCase))
            return false;

        path = Path.GetFullPath(candidate);
        return true;
    }

    async Task InstallModPackageAsync(string packagePath)
    {
        try
        {
            SetApplying(true, "Reading and validating the mod package…");
            ModPackageInfo package;
            try
            {
                package = await Task.Run(() => ModProject.InspectPackage(packagePath));
            }
            finally
            {
                SetApplying(false);
            }

            string? workspaceNotice = _project is not null
                ? $"Installing this package will leave {_project.Name}. Its changes remain saved in that project and will not be mixed with the mod being installed."
                : _changes.Count > 0
                    ? $"Installing this package will discard {Amount(OperationCount(pendingOnly: true), "pending Free Mode change")}."
                    : null;
            var installDialog = new ModInstallWindow(package, workspaceNotice)
            {
                Owner = this,
            };
            if (installDialog.ShowDialog() != true)
                return;

            ModProject? previousProject = _project;
            List<PendingChange> previousChanges = _changes.Changes.ToList();
            SetApplying(true, "Adding the mod package to the local library…");
            ModProject project;
            try
            {
                project = await Task.Run(() => ModProject.ImportPackage(packagePath, AppSettings.ModLibraryPath));
            }
            finally
            {
                SetApplying(false);
            }
            ModCompileResult installation = await ActivateProjectAsync(project, installing: true);

            if (installation.Problems.Count > 0)
            {
                RestoreWorkspace(previousProject, previousChanges);
                RemoveCancelledPackage(project);
                return;
            }
            if (_changes.Count == 0)
            {
                SetStatus($"{package.Name}: already installed");
                MessageBox.Show(this, $"{package.Name} is already present in the game archives. No files needed to be written.", "Mod already installed", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApplyResult result = await ApplyChanges(this, package.Name);
            if (result == ApplyResult.Applied)
                MessageBox.Show(this, $"{package.Name} was installed successfully.", "Mod installed", MessageBoxButton.OK, MessageBoxImage.Information);
            else if (result is ApplyResult.Cancelled or ApplyResult.FailedBeforeWrite)
            {
                RestoreWorkspace(previousProject, previousChanges);
                RemoveCancelledPackage(project);
            }
        }
        catch (Exception ex)
        {
            ShowError("Could not install the mod package", ex);
        }
    }

    void RestoreWorkspace(ModProject? project, IReadOnlyList<PendingChange> changes)
    {
        _changes.Clear();
        foreach (PendingChange change in changes)
            _changes.Set(change);
        _project = project;
        UpdateChangeButtons();
        ShowArchiveAgain();
        SetStatus("Installation cancelled; the previous workspace was restored");
    }

    static void RemoveCancelledPackage(ModProject project)
    {
        try
        {
            string library = Path.GetFullPath(AppSettings.ModLibraryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string folder = Path.GetFullPath(Path.GetDirectoryName(project.FilePath)!);
            string parent = Path.GetFullPath(Path.GetDirectoryName(folder)!);
            if (!parent.Equals(library, StringComparison.OrdinalIgnoreCase))
                return;
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch
        {
            // A leftover local package cache is harmless
        }
    }
}
