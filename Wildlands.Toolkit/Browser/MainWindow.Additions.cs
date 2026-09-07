using System.IO;
using System.Windows;
using Microsoft.Win32;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public partial class MainWindow
{
    void UpdateAssetActions()
    {
        bool available = _showing is not null && !_loading && !_applying;
        bool root = _showing?.IsArchiveRoot == true;
        int selected = ItemList.SelectedItems.Count;

        AddAssetButton.Content = root ? "Add .data…" : "Add resource…";
        AddAssetButton.IsEnabled = available;
        DeleteAssetButton.Content = selected > 1 ? $"Delete {selected} items" : "Delete";
        DeleteAssetButton.IsEnabled = available && selected > 0;
        AssetActionHint.Text = _showing is null
            ? "Open an archive to add or delete assets"
            : root
                ? "Forge containers"
                : "Resources in this .data container";

        MenuAdd.Header = root ? "Add .data container..." : "Add resource to this container...";
        MenuAdd.IsEnabled = available;
        MenuDelete.Header = selected > 1 ? $"Delete {selected} items" : "Delete";
        MenuDelete.IsEnabled = available && selected > 0;
    }

    async void MenuAdd_Click(object sender, RoutedEventArgs e)
    {
        if (_showing is null || _archive is null || _loading || _applying)
            return;

        if (_showing.IsArchiveRoot)
            await AddContainer();
        else
            AddResource();
    }

    void AddResource()
    {
        if (_showing is null || _showing.IsArchiveRoot)
            return;

        List<Resource> resources = _shown
            .Select(item => item.Resource)
            .Where(resource => resource is not null)
            .Select(resource => resource!)
            .ToList();
        Resource? preferred = (ItemList.SelectedItem as BrowserItem)?.Resource;
        string selectedType = preferred is null
            ? "game resource"
            : Wildlands.Formats.ResourceTypes.NameOf(preferred.ClassHash);
        var fileDialog = new OpenFileDialog
        {
            Title = $"Choose the raw {selectedType} to add",
            Filter = preferred is null
                ? "Raw game resources (*.bin;*.dat)|*.bin;*.dat|All files (*.*)|*.*"
                : $"{selectedType} resources (*.{selectedType};*.bin;*.dat)|*.{selectedType};*.bin;*.dat|All files (*.*)|*.*",
        };
        SetInitialFolder(fileDialog);
        if (fileDialog.ShowDialog(this) != true)
            return;

        try
        {
            ResourceAdditionSource source = ArchiveAdditionService.InspectResource(fileDialog.FileName, resources, preferred);
            IReadOnlyList<Resource> templates = ArchiveAdditionService.ResourceTemplates(source, resources);
            Resource? preferredTemplate = preferred?.ClassHash == source.ClassHash ? preferred : templates.FirstOrDefault();
            List<PendingChange> pending = PendingForCurrentContainer();
            ulong suggestedId = ArchiveAdditionService.SuggestedResourceId(source, resources, pending);
            var dialog = new AddAssetWindow(source, _showing.Display, templates, preferredTemplate, suggestedId) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.ResourceTemplate is not { } template)
                return;

            PendingChange change = ArchiveAdditionService.CreateResourceAddition(_showing, source, template, dialog.AssetName, dialog.AssetId, resources, pending);
            if (!QueueChanges([change], $"Add {source.Type} {dialog.AssetName}"))
                return;

            RememberSourceFolder(fileDialog.FileName);
            SetStatus($"{dialog.AssetName}: new {source.Type} queued in Changes");
        }
        catch (Exception ex)
        {
            ShowError("Could not add the resource", ex);
        }
    }

    async Task AddContainer()
    {
        if (_showing is null || !_showing.IsArchiveRoot || _archive is null)
            return;

        var fileDialog = new OpenFileDialog
        {
            Title = "Choose the complete .data container to add",
            Filter = "Wildlands data containers (*.data)|*.data|All files (*.*)|*.*",
        };
        SetInitialFolder(fileDialog);
        if (fileDialog.ShowDialog(this) != true)
            return;

        try
        {
            ContainerAdditionSource source = ArchiveAdditionService.InspectContainer(fileDialog.FileName);
            IReadOnlyList<ForgeEntry> templates = ArchiveAdditionService.ContainerTemplates(source, _archive.Entries);
            ForgeEntry? selected = (ItemList.SelectedItem as BrowserItem)?.Entry;
            ForgeEntry? preferred = selected?.Extension == source.ClassHash && selected.FileExtension == ".data" ? selected : templates.FirstOrDefault();
            var dialog = new AddAssetWindow(source, _showing.Display, templates, preferred) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.EntryTemplate is not { } template)
                return;

            string assetName = dialog.AssetName;
            string archivePath = _showing.ArchivePath;
            List<PendingChange> pending = _changes.Changes.ToList();
            SetLoading(true, $"Validating {assetName} and the Forge archive layout…");
            PendingChange change;
            try
            {
                change = await Task.Run(() => ArchiveAdditionService.CreateContainerAddition(archivePath, source, template.Id, assetName, pending));
            }
            finally
            {
                SetLoading(false);
            }

            if (!QueueChanges([change], $"Add .data container {assetName}"))
                return;

            RememberSourceFolder(fileDialog.FileName);
            SetStatus($"{assetName}: new .data container queued in Changes");
        }
        catch (Exception ex)
        {
            if (_loading)
                SetLoading(false);
            ShowError("Could not add the .data container", ex);
        }
    }

    void DeleteAsset_Click(object sender, RoutedEventArgs e)
    {
        if (_showing is null || _archive is null || _loading || _applying)
            return;

        List<BrowserItem> selected = ItemList.SelectedItems.Cast<BrowserItem>().ToList();
        if (selected.Count == 0)
            return;

        if (_showing.IsArchiveRoot)
            DeleteContainers(selected);
        else
            DeleteResources(selected);
    }

    void DeleteContainers(IReadOnlyList<BrowserItem> selected)
    {
        List<ForgeEntry> entries = selected.Select(item => item.Entry)
            .Where(entry => entry is not null).Select(entry => entry!).ToList();
        if (entries.Count == 0 || _showing is null)
            return;

        HashSet<int> indexes = entries.Select(entry => entry.Index).ToHashSet();
        bool hasPendingChildren = _changes.Changes.Any(change =>
                change.ArchivePath.Equals(_showing.ArchivePath, StringComparison.OrdinalIgnoreCase)
                && indexes.Contains(change.EntryIndex)
                && change.EntryRemoval is null);
        HashSet<ulong> entryIds = entries.Select(entry => entry.Id).ToHashSet();
        bool hasSavedChildren = _project?.Operations.Any(operation =>
            Path.GetFullPath(Path.Combine(_settings.GamePath, operation.Archive.Replace('/', Path.DirectorySeparatorChar)))
                .Equals(_showing.ArchivePath, StringComparison.OrdinalIgnoreCase)
            && entryIds.Contains(operation.EntryId)
            && operation.Kind != ModOperationKind.RemoveForgeEntry
            && operation.Kind != ModOperationKind.AddForgeEntry) == true;
        if (hasPendingChildren || hasSavedChildren)
        {
            MessageBox.Show(this,
                "At least one selected container already has pending changes. Remove or apply "
                + "those changes in the Changes window before deleting the whole container.",
                "Delete asset containers", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string target = entries.Count == 1 ? entries[0].Name : $"{entries.Count} selected containers";
        if (MessageBox.Show(this,
                $"Delete {target} from {Path.GetFileName(_showing.ArchivePath)}?\n\nNothing is written until Apply changes. " +
                $"The Toolkit creates a .original backup before the first write.",
                "Delete asset containers", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        List<PendingChange> changes = entries.Select(entry => new PendingChange(_showing.ArchivePath, entry.Index, entry.Name, -1, entry.Name, [], EntryRemoval: new PendingForgeEntryRemoval(entry.Id))).ToList();
        string label = entries.Count == 1
            ? $"Delete asset container {entries[0].Name}"
            : $"Delete {entries.Count} asset containers";
        if (!QueueChanges(changes, label))
            return;

        SetStatus($"{target}: deletion queued in Changes");
    }

    void DeleteResources(IReadOnlyList<BrowserItem> selected)
    {
        if (_showing is null)
            return;
        List<BrowserItem> resources = selected.Where(item => item.Resource is not null).ToList();
        if (resources.Count == 0)
            return;

        string target = resources.Count == 1 ? resources[0].Name
            : $"{resources.Count} selected resources";
        if (MessageBox.Show(this,
                $"Delete {target} from {_showing.EntryName}?\n\nReferences elsewhere in the game are not removed automatically. Missing resources can break the affected asset.\n\nNothing is written until Apply changes. ",
                "Delete resources", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;

        List<PendingChange> changes = resources.Select(item => new PendingChange(_showing.ArchivePath, _showing.EntryIndex, _showing.EntryName,
            item.Index, item.Name, [], Removal: new PendingResourceRemoval(item.Resource!.Id, item.Resource.ClassHash), ResourceClassHash: item.Resource.ClassHash)).ToList();
        string label = resources.Count == 1
            ? $"Delete resource {resources[0].Name}"
            : $"Delete {resources.Count} resources from {_showing.EntryName}";
        if (!QueueChanges(changes, label))
            return;

        SetStatus($"{target}: deletion queued in Changes");
    }

    List<PendingChange> PendingForCurrentContainer()
    {
        if (_showing is null)
            return [];
        return _changes.Changes.Where(change => change.ArchivePath.Equals(_showing.ArchivePath, StringComparison.OrdinalIgnoreCase) && change.EntryIndex == _showing.EntryIndex)
            .ToList();
    }

    void SetInitialFolder(OpenFileDialog dialog)
    {
        if (_settings.ExportFolder.Length > 0 && Directory.Exists(_settings.ExportFolder))
            dialog.InitialDirectory = _settings.ExportFolder;
    }

    void RememberSourceFolder(string path)
    {
        _settings.ExportFolder = Path.GetDirectoryName(path) ?? "";
        _settings.Save();
    }
}
