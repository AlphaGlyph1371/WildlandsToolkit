using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Models;
using Wildlands.Formats.Textures;

namespace Wildlands.Toolkit;

public partial class MainWindow
{
    void MenuReplace_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is not null)
        {
            ReplaceTexture(_preview);
            return;
        }

        if (ItemList.SelectedItem is not BrowserItem item || item.Resource is null || _showing is null)
            return;

        if (item.Resource.ClassHash == Mesh.ClassHash)
        {
            ReplaceMesh(item);
            return;
        }

        ReplaceRawResource(item);
    }

    void MenuReplaceRaw_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is BrowserItem item && item.Resource is not null)
            ReplaceRawResource(item);
    }

    bool QueueChanges(IReadOnlyList<PendingChange> changes, string? operationLabel = null)
    {
        if (changes.Count == 0)
            return true;
        string groupId = Guid.NewGuid().ToString("N");
        string label = operationLabel ?? DescribeOperation(changes);
        var grouped = changes.Select(change => change with
        {
            OperationGroupId = groupId,
            OperationLabel = label,
        }).ToList();
        IReadOnlyList<string>? projectKeys = null;
        try
        {
            if (_project is not null)
                projectKeys = _project.Record(grouped, _settings.GamePath, groupId, label);
        }
        catch (Exception ex)
        {
            ShowError("Could not save the changes in the active mod project", ex);
            return false;
        }

        for (int index = 0; index < grouped.Count; index++)
        {
            PendingChange change = projectKeys is null
                ? grouped[index]
                : grouped[index] with { ProjectOperationKey = projectKeys[index] };
            _changes.Set(change);
        }
        UpdateChangeButtons();
        return true;
    }

    static string DescribeOperation(IReadOnlyList<PendingChange> changes)
    {
        if (changes.Count == 1)
        {
            PendingChange change = changes[0];
            if (change.EntryAddition is not null)
                return $"Add asset container {change.EntryName}";
            if (change.Addition is not null)
                return $"Add resource {change.ResourceName}";
            return $"Replace {change.ResourceName}";
        }
        return $"Edit {changes.Select(change => change.EntryName).Distinct().Count()} asset container(s)";
    }

    void ReplaceRawResource(BrowserItem item)
    {
        if (item.Resource is null || _showing is null)
            return;

        var data = RawReplace.Choose(this, item.Resource, _settings);
        if (data is null)
            return;

        if (!QueueChanges([new PendingChange(_showing.ArchivePath, _showing.EntryIndex,
                _showing.EntryName, item.Index, item.Name, data,
                ResourceClassHash: item.Resource.ClassHash)]))
            return;

        SetStatus($"{item.Name}: replacement queued");
        UpdateChangeButtons();
    }

    void ReplaceMesh(BrowserItem item)
    {
        if (item.Resource is null || _showing is null)
            return;

        var rebuilt = MeshImporter.Replace(this, EffectiveData(item), item.Name, _settings, out _);
        if (rebuilt is null)
            return;

        if (!QueueChanges([new PendingChange(_showing.ArchivePath, _showing.EntryIndex,
                _showing.EntryName, item.Index, item.Name, rebuilt,
                ResourceClassHash: Mesh.ClassHash)]))
            return;

        SetStatus($"{item.Name}: replacement queued");
        UpdateChangeButtons();
    }

    async Task<ApplyResult> ApplyChanges(Window dialogOwner, string? installingPackage = null)
    {
        if (_changes.Count == 0 || _applying)
            return ApplyResult.FailedBeforeWrite;

        List<ArchiveWork> plans;
        SetApplying(true, "Preparing the changed archive data…");
        try
        {
            var planningProgress = new Progress<string>(SetApplyProgress);
            plans = await Task.Run(() => _changes.Plan(planningProgress));
        }
        catch (System.Exception ex)
        {
            ShowError("Could not build the changed files", ex, dialogOwner);
            return ApplyResult.FailedBeforeWrite;
        }
        finally
        {
            SetApplying(false);
        }

        var fresh = plans.Where(x => !ArchiveBackup.Exists(x.Path)).ToList();
        var rebuilds = plans.Where(x => x.NeedsRebuild).ToList();
        IReadOnlyList<DriveSpaceRequirement> spaceRequirements;
        try
        {
            spaceRequirements = DiskSpacePreflight.Calculate(plans);
        }
        catch (Exception ex)
        {
            ShowError("Could not check the available disk space", ex, dialogOwner);
            return ApplyResult.FailedBeforeWrite;
        }

        List<DriveSpaceRequirement> insufficientSpace = spaceRequirements
            .Where(requirement => !requirement.HasEnoughSpace)
            .ToList();
        if (insufficientSpace.Count > 0)
        {
            string details = string.Join(Environment.NewLine, insufficientSpace.Select(requirement =>
                $"{requirement.DriveName}: {DiskSpacePreflight.FormatBytes(requirement.RequiredBytes)} required, "
                + $"{DiskSpacePreflight.FormatBytes(requirement.AvailableBytes)} available"));
            MessageBox.Show(dialogOwner,
                "There is not enough free disk space to apply these changes safely.\n\n"
                + details
                + "\n\nThis includes the required backups, temporary rebuild files, archive growth, "
                + "and a 1 GB safety margin.",
                "Not enough disk space", MessageBoxButton.OK, MessageBoxImage.Error);
            SetStatus("Nothing written: not enough free disk space");
            return ApplyResult.FailedBeforeWrite;
        }

        int pendingOperations = OperationCount(pendingOnly: true);
        var question = new StringBuilder();
        question.AppendLine(installingPackage is null
            ? plans.Count == 1
                ? $"Apply {Amount(pendingOperations, "pending change")} to {plans[0].Name}?"
                : $"Apply {Amount(pendingOperations, "pending change")} to {Amount(plans.Count, "archive")}?"
            : plans.Count == 1
                ? $"Install {installingPackage} into {plans[0].Name}?"
                : $"Install {installingPackage} into {Amount(plans.Count, "archive")}?");
        question.AppendLine();

        if (plans.Count == 1)
        {
            var only = plans[0];
            question.AppendLine(only.NeedsRebuild
                ? $"{Amount(only.EntryNames.Count, "entry", "entries")} change, and the whole archive is written again."
                : $"{Amount(only.EntryNames.Count, "entry", "entries")} change, written where they already are.");
        }
        else
        {
            foreach (var work in plans)
                question.AppendLine($"{work.Name}   {Amount(work.EntryNames.Count, "entry", "entries")}" + (work.NeedsRebuild ? ", written again completely" : ""));
        }

        if (rebuilds.Count > 0)
        {
            question.AppendLine();
            question.AppendLine($"{Amount(rebuilds.Count, "archive")} will be written again completely. This can take a few minutes.");
        }

        if (fresh.Count > 0)
        {
            question.AppendLine();
            question.AppendLine($"{Amount(fresh.Count, "new .original backup")} will be created first.");
        }

        if (spaceRequirements.Count > 0)
        {
            question.AppendLine();
            question.AppendLine("Required free disk space (including a 1 GB safety margin):");
            foreach (DriveSpaceRequirement requirement in spaceRequirements)
                question.AppendLine($"{requirement.DriveName}   "
                    + $"{DiskSpacePreflight.FormatBytes(requirement.RequiredBytes)} required, "
                    + $"{DiskSpacePreflight.FormatBytes(requirement.AvailableBytes)} available");
        }

        string confirmationTitle = installingPackage is null
            ? "Apply changes" : $"Install {installingPackage}";
        if (MessageBox.Show(dialogOwner, question.ToString(), confirmationTitle,
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            SetStatus(installingPackage is null
                ? "Nothing written"
                : $"Installation of {installingPackage} cancelled; nothing was written");
            return ApplyResult.Cancelled;
        }

        Location? place = _showing;
        SetApplying(true, "Preparing the archives for writing…");
        _archives?.Dispose();
        _archive?.Dispose();
        _archive = null;
        _archives = null;

        var writingProgress = new Progress<string>(SetApplyProgress);
        var watch = Stopwatch.StartNew();
        var appliedArmoryChanges = _changes.Changes
            .Where(change => BuildTableGameMetadataResolver.IsGameDatabaseContainer(change.EntryName))
            .Select(change => new ArmoryDatabaseResourceChange(change.ArchivePath, change.EntryIndex,
                change.EntryName, change.ResourceIndex, change.ResourceName, change.Data))
            .ToList();
        bool changedLocalization = _changes.Changes.Any(change =>
            BuildTableGameMetadataResolver.TryGetLanguagePackage(change.EntryName) is not null);

        bool written = false;
        try
        {
            await Task.Run(() => _changes.Write(plans, writingProgress));
            written = true;
            string? projectWarning = null;
            if (_project is not null)
            {
                try { _project.MarkDeployed(); }
                catch (Exception ex) { projectWarning = ex.Message; }
            }
            string? cacheWarning = null;
            if (_armoryIndex is not null)
            {
                if (changedLocalization)
                {
                    // A newly added attachment brings new gameplay and localization
                    // resources which cannot be mirrored by replacing old cache rows.
                    // Rebuild now so the next Armory window immediately resolves it.
                    try
                    {
                        string gameFolder = _settings.GamePath;
                        var refreshProgress = new Progress<string>(SetApplyProgress);
                        SetApplyProgress("Refreshing the Armory index from the changed game files…");
                        var refreshed = await Task.Run(() => ArmoryIndex.Build(
                            ArchiveLocator.Find(gameFolder), refreshProgress));
                        refreshed.Save(AppSettings.ArmoryCachePath);
                        _armoryIndex = refreshed;
                        UpdateArmoryIndexButton();
                    }
                    catch (Exception ex)
                    {
                        _armoryIndex = null;
                        UpdateArmoryIndexButton();
                        cacheWarning = ex.Message;
                    }
                }
                else
                {
                    try
                    {
                        var index = _armoryIndex;
                        string gameFolder = _settings.GamePath;
                        await Task.Run(() =>
                        {
                            index.ApplyChanges(appliedArmoryChanges);
                            index.RefreshFingerprint(ArchiveLocator.Find(gameFolder));
                            index.Save(AppSettings.ArmoryCachePath);
                        });
                    }
                    catch (Exception ex)
                    {
                        cacheWarning = ex.Message;
                    }
                }
            }
            watch.Stop();
            if (place is not null)
                await Navigate(place);
            string? warning = cacheWarning ?? projectWarning;
            SetStatus(warning is null
                ? $"Applied in {watch.Elapsed.TotalSeconds:0.0} s"
                : $"Applied in {watch.Elapsed.TotalSeconds:0.0} s, but: {warning}");
        }
        catch (System.Exception ex)
        {
            ShowError("Could not write the changes", ex, dialogOwner);
            if (place is not null)
                await Navigate(place);
        }
        finally
        {
            SetApplying(false);
            UpdateChangeButtons();
        }
        return written ? ApplyResult.Applied : ApplyResult.FailedDuringWrite;
    }

    void SetApplying(bool applying, string status = "")
    {
        _applying = applying;
        // Disabling the content makes WPF apply disabled control styles, which
        // replaces parts of the dark theme with washed-out system colors. The
        // overlay already owns the foreground, so only block pointer input.
        MainContent.IsHitTestVisible = !applying;
        ApplyLoadingOverlay.Visibility = applying ? Visibility.Visible : Visibility.Collapsed;
        Mouse.OverrideCursor = applying || _loading ? Cursors.Wait : null;
        _changeListWindow?.SetInteractionEnabled(!applying && !_loading);

        if (applying)
        {
            ApplyLoadingText.Text = status;
            ApplyLoadingOverlay.Focus();
            SetStatus(status);
        }
    }

    void SetApplyProgress(string status)
    {
        ApplyLoadingText.Text = status;
        SetStatus(status);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_applying)
        {
            e.Cancel = true;
            return;
        }
        if (_project is null && _changes.Count > 0
            && MessageBox.Show(this,
                $"Exit and discard {Amount(OperationCount(pendingOnly: true), "pending Free Mode change")}?\n\n" +
                "Changes that were already applied to the game are not affected.",
                "Discard Free Mode changes", MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        base.OnClosing(e);
    }

    static string Amount(int count, string one, string? many = null) =>
        count == 1 ? $"1 {one}" : $"{count} {many ?? one + "s"}";

    // Nothing was written, so both the preview and any open texture window have to go back
    // to what the archive holds.
    void ShowArchiveAgain()
    {
        ShowPreview(ItemList.SelectedItem as BrowserItem);

        foreach (var window in _textureWindows.ToList())
        {
            try
            {
                var (resource, location, index) = LocateTexture(window.View);
                var texture = TextureMap.Read(EffectiveData(location, index, resource));
                window.ShowView(TextureLoader.FromTexture(resource.Name, texture, _archives,
                    PendingCompiledMips()));
            }
            catch
            {
                // A window whose texture cannot be found again simply keeps what it shows
            }
        }
    }

    void UpdateChangeButtons()
    {
        ChangeListButton.IsEnabled = !_loading && !_applying;
        int count = OperationCount();
        ChangeListButton.Content = count == 0 ? "Changes" : $"Changes ({count})";
        _changeListWindow?.Refresh();
        UpdateProjectUi();
    }

    int OperationCount(bool pendingOnly = false)
    {
        if (!pendingOnly && _project is not null)
            return _project.Operations.Select(operation =>
                    string.IsNullOrWhiteSpace(operation.OperationGroupId)
                        ? operation.Id : operation.OperationGroupId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

        return _changes.Changes.Select(change =>
                change.OperationGroupId ?? change.ProjectOperationKey
                ?? $"{change.ArchivePath}|{change.EntryIndex}|{change.ResourceIndex}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
    }

    void ChangeList_Click(object sender, RoutedEventArgs e)
    {
        if (_changeListWindow is not null)
        {
            _changeListWindow.Activate();
            return;
        }

        _changeListWindow = new ChangeListWindow(
            () => new ChangeListState(_changes.Changes.ToList(),
                _project?.Operations.ToList() ?? [], _project?.Name),
            RemoveQueuedChanges, RevealChangeOperation,
            async () => await ApplyChanges((Window?)_changeListWindow ?? this));
        _changeListWindow.Closed += (_, _) => _changeListWindow = null;
        _changeListWindow.Show();
    }

    async Task RemoveQueuedChanges(IReadOnlyList<PendingChange> selected)
    {
        if (selected.Count == 0 || _loading || _applying)
            return;

        var groupIds = selected.Select(change => change.OperationGroupId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expanded = _changes.Changes.Where(change =>
                selected.Contains(change)
                || change.OperationGroupId is { } id && groupIds.Contains(id))
            .ToList();

        try
        {
            if (_project is not null)
            {
                var keys = expanded.Select(change => change.ProjectOperationKey)
                    .Where(key => !string.IsNullOrWhiteSpace(key))
                    .Select(key => key!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (keys.Count == 0)
                    throw new InvalidOperationException(
                        "The selected project change could not be matched to the project file.");
                _project.RevertPending(keys);
                try
                {
                    await ActivateProjectAsync(_project);
                }
                catch
                {
                    // The project on disk is already rolled back. Do not leave the old,
                    // now-invalid pending changes visible if reloading fails.
                    _changes.Clear();
                    UpdateChangeButtons();
                    throw;
                }
            }
            else
            {
                foreach (PendingChange change in expanded)
                    _changes.Remove(change);
                UpdateChangeButtons();
            }

            SetStatus("Pending change removed");
            ShowArchiveAgain();
        }
        catch (Exception ex)
        {
            ShowError("Could not remove the pending change", ex);
        }
    }

    byte[] EffectiveData(BrowserItem item)
    {
        if (item.Resource is null)
            return [];
        return _showing is null
            ? item.Resource.Data
            : EffectiveData(_showing, item.Index, item.Resource);
    }

    byte[] EffectiveData(Location location, int resourceIndex, Resource resource) =>
        _changes.Find(location.ArchivePath, location.EntryIndex, resourceIndex)?.Data
        ?? resource.Data;

    Dictionary<ulong, byte[]> PendingCompiledMips()
    {
        var result = new Dictionary<ulong, byte[]>();
        foreach (PendingChange change in _changes.Changes.Where(change =>
                     change.ResourceClassHash == CompiledMip.ClassHash
                     && change.Data.Length >= sizeof(ulong)))
            result[BitConverter.ToUInt64(change.Data, 0)] = change.Data;
        return result;
    }

    async Task RevealQueuedChange(PendingChange change)
    {
        if (change.EntryAddition is not null || change.EntryIndex < 0)
            return;

        Activate();
        await Navigate(new Location(change.ArchivePath, change.EntryIndex, change.EntryName));
        if (_showing?.ArchivePath != change.ArchivePath || _showing.EntryIndex != change.EntryIndex)
            return;
        if (change.ResourceIndex >= 0 && ItemList.Items.OfType<BrowserItem>()
                .FirstOrDefault(item => item.Index == change.ResourceIndex) is { } item)
        {
            ItemList.SelectedItem = item;
            ItemList.ScrollIntoView(item);
        }
    }

    async Task RevealChangeOperation(ChangeOperationRow row)
    {
        try
        {
            if (row.RevealChange is { } pending)
            {
                await RevealQueuedChange(pending);
                return;
            }
            if (row.RevealOperation is not { } operation)
                return;

            string gameRoot = Path.GetFullPath(_settings.GamePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string archivePath = Path.GetFullPath(Path.Combine(gameRoot,
                operation.Archive.Replace('/', Path.DirectorySeparatorChar)));
            if (!archivePath.StartsWith(gameRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The project archive path is outside the game folder.");

            string entryName;
            using (var archive = ForgeArchive.Open(archivePath))
            {
                ForgeEntry entry = archive.Entries.FirstOrDefault(candidate =>
                    candidate.Id == operation.EntryId)
                    ?? throw new InvalidDataException(
                        $"{operation.EntryName} is not present in the game archive.");
                entryName = entry.Name;
                await Navigate(new Location(archivePath, entry.Index, entryName));
            }

            Activate();
            if (operation.Kind != ModOperationKind.AddForgeEntry
                && ItemList.Items.OfType<BrowserItem>().FirstOrDefault(item =>
                    item.Id == operation.ResourceId
                    && item.Resource?.ClassHash == operation.ResourceClassHash) is { } resource)
            {
                ItemList.SelectedItem = resource;
                ItemList.ScrollIntoView(resource);
            }
        }
        catch (Exception ex)
        {
            ShowError("Could not open the affected project resource", ex,
                _changeListWindow);
        }
    }
}
