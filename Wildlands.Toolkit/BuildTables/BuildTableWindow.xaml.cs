using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public sealed record AttachmentSaveChanges(
    string DisplayName,
    IReadOnlyList<BuildTableResourceChange> LocalChanges,
    IReadOnlyList<ArmoryDatabaseResourceChange> DatabaseChanges,
    IReadOnlyList<ArmoryArchiveResourceAddition> ResourceAdditions,
    IReadOnlyList<ArmoryArchiveEntryAddition> EntryAdditions);

public partial class BuildTableWindow : Window
{
    BuildTableAsset _table;
    readonly List<BuildTableDocument> _documents;
    BuildTableDocument _currentDocument;
    IReadOnlyList<BuildTableFamilyItem> _family;
    byte[] _savedData
    {
        get => _currentDocument.SavedData;
        set => _currentDocument.SavedData = value;
    }
    string _name => _currentDocument.Name;
    readonly IReadOnlyList<BuildTableTarget> _localTargets;
    readonly Dictionary<ulong, Resource> _localPreviewResources;
    readonly IReadOnlyDictionary<ulong, int> _localResourceIndexes;
    readonly IReadOnlyList<string> _archivePaths;
    readonly ArmoryIndex? _armoryIndex;
    readonly Action<IReadOnlyList<BuildTableResourceChange>> _save;
    readonly Action<IReadOnlyList<ArmoryDatabaseResourceChange>>? _saveGunsmith;
    readonly Action<AttachmentSaveChanges>? _saveAttachment;
    readonly string _localArchivePath;
    readonly int _localEntryIndex;
    readonly string _localEntryName;
    readonly HashSet<ulong> _gunsmithRemovals = [];
    readonly HashSet<ulong> _gunsmithAdditions = [];
    readonly Dictionary<uint, BuildTableOptionMetadata> _addedMetadata = [];
    readonly Dictionary<ulong, IReadOnlyList<string>> _addedOwnersByRecordId = [];
    readonly Dictionary<ulong, bool> _addedGunsmithVisibility = [];
    readonly Dictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]>
        _preparedResourceData = [];
    readonly HashSet<ulong> _preparedIds = [];
    readonly List<BuildTableReferenceRow> _rows = [];
    readonly Dictionary<ulong, BuildTableTarget> _targets = [];
    readonly Dictionary<ulong, Task<BuildTableMeshPreview?>> _meshPreviewTasks = [];
    readonly SemaphoreSlim _meshPreviewGate = new(2);
    readonly CancellationTokenSource _stopResolving = new();
    readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(180) };
    BuildTableGameMetadata _gameMetadata = BuildTableGameMetadata.Empty;
    IReadOnlyList<ulong> _ownerEntityIds = [];
    CancellationTokenSource? _languageLoad;

    IReadOnlyList<BuildTableTarget> _catalog = [];
    int _searchVersion;
    int _languageVersion;
    bool _dirty;
    bool _closed;
    bool _changingSelection;
    bool _settingLanguage;
    bool _targetsResolved;
    int? _selectedArmoryRow;

    public BuildTableWindow(byte[] data, string name,
        IReadOnlyList<BuildTableTarget> localTargets,
        IReadOnlyList<string> archivePaths,
        Action<byte[]> save)
        : this(data, name, localTargets, archivePaths,
            [new BuildTableResourceSource(-1, BuildTable.Read(data).Id, name, data)],
            changes => save(changes.Single().Data), null, null)
    {
    }

    public BuildTableWindow(byte[] data, string name,
        IReadOnlyList<BuildTableTarget> localTargets,
        IReadOnlyList<string> archivePaths,
        IReadOnlyList<BuildTableResourceSource> familyResources,
        Action<IReadOnlyList<BuildTableResourceChange>> save,
        ArmoryIndex? armoryIndex = null,
        Action<IReadOnlyList<ArmoryDatabaseResourceChange>>? saveGunsmith = null,
        IReadOnlyList<Resource>? previewResources = null,
        Action<AttachmentSaveChanges>? saveAttachment = null,
        string localArchivePath = "",
        int localEntryIndex = -1,
        string localEntryName = "",
        IReadOnlyDictionary<ulong, int>? localResourceIndexes = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(localTargets);
        ArgumentNullException.ThrowIfNull(archivePaths);
        ArgumentNullException.ThrowIfNull(familyResources);
        ArgumentNullException.ThrowIfNull(save);

        InitializeComponent();

        _localTargets = localTargets;
        _localPreviewResources = (previewResources ?? [])
            .Where(resource => resource.Id != 0)
            .GroupBy(resource => resource.Id)
            .ToDictionary(group => group.Key, group => group.First());
        _localResourceIndexes = localResourceIndexes
            ?? new Dictionary<ulong, int>();
        _archivePaths = archivePaths;
        _armoryIndex = armoryIndex;
        _save = save;
        _saveGunsmith = saveGunsmith;
        _saveAttachment = saveAttachment;
        _localArchivePath = localArchivePath;
        _localEntryIndex = localEntryIndex;
        _localEntryName = localEntryName;
        ulong openedId = BuildTable.Read(data).Id;
        _documents = familyResources
            .Where(source => source.Data.Length > 0)
            .GroupBy(source => source.Id)
            .Select(group => new BuildTableDocument(group.First()))
            .ToList();
        _currentDocument = _documents.FirstOrDefault(document => document.Id == openedId)
            ?? new BuildTableDocument(new BuildTableResourceSource(-1, openedId, name, data));
        if (!_documents.Contains(_currentDocument))
            _documents.Add(_currentDocument);
        _table = _currentDocument.Table;
        _family = BuildTableFamily.Find(_documents, _currentDocument);

        foreach (var target in localTargets)
            if (target.Id != 0)
                _targets[target.Id] = target;
        _catalog = localTargets.OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase).ToList();

        RebuildRows();

        string familyTitle = _family.FirstOrDefault(item => item.IsOverview)?.Document.Name ?? name;
        Title = $"{familyTitle} - Armory editor";
        FamilyTitleText.Text = familyTitle;
        FamilySubtitleText.Text = _family.Count > 1
            ? $"{_family.Count - 1} linked tables"
            : "One table";
        FamilyList.ItemsSource = _family;
        KindBox.ItemsSource = new[]
        {
            "Editable fields",
            "Variant choices",
            "Built assets",
            "Related groups",
            "Empty optional fields",
            "All fields (advanced)",
            "Internal fields",
            "Other fields",
        };
        KindBox.SelectedIndex = 0;

        ShowReferences();
        SelectInitialFamilyItem();
        SetStatus($"Loaded {familyTitle}. Reading referenced assets and confirmed game labels…");

        _searchDelay.Tick += async (_, _) =>
        {
            _searchDelay.Stop();
            ShowTargetResults();
            await EnrichTargetSearch(_searchVersion);
        };
        Loaded += ResolveTargets;
    }

    void SelectInitialFamilyItem()
    {
        var selected = _family.FirstOrDefault(item => item.Document == _currentDocument);
        if (selected?.IsOverview == true)
            selected = _family.FirstOrDefault(item => !item.IsOverview) ?? selected;
        FamilyList.SelectedItem = selected ?? _family.FirstOrDefault();
    }

    BuildTableTarget? Resolve(ulong value) => _targets.GetValueOrDefault(value);

    void Family_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection || FamilyList.SelectedItem is not BuildTableFamilyItem item)
            return;

        _selectedArmoryRow = null;
        _currentDocument.Table = _table;
        _currentDocument = item.Document;
        _table = item.Document.Table;
        RebuildRows();
        RebuildOptions(item);
        ShowSelectedReference();
        ShowTargetResults();
    }

    void TechnicalMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
            return;
        bool technical = TechnicalModeBox.IsChecked == true;
        ArmoryPane.Visibility = technical ? Visibility.Collapsed : Visibility.Visible;
        TechnicalPane.Visibility = technical ? Visibility.Visible : Visibility.Collapsed;

        if (technical && ReferenceList.SelectedItem is null && ReferenceList.Items.Count > 0)
            ReferenceList.SelectedIndex = 0;
        if (!technical)
            ShowAllAssetsBox.IsChecked = false;
        ShowAllAssetsBox.IsEnabled = technical;
        RawReferencePanel.Visibility = technical ? Visibility.Visible : Visibility.Collapsed;
        TechnicalDetailsPanel.Visibility = technical ? Visibility.Visible : Visibility.Collapsed;
        DuplicateRowButton.Visibility = technical ? Visibility.Visible : Visibility.Collapsed;
        RemoveRowButton.Visibility = technical ? Visibility.Visible : Visibility.Collapsed;
        GunsmithAvailabilityNote.Visibility = Visibility.Collapsed;
        GunsmithLinkPanel.Visibility = Visibility.Collapsed;
        GunsmithAvailabilityButton.Visibility = technical ? Visibility.Collapsed : GunsmithAvailabilityButton.Visibility;
        ClearFieldButton.Visibility = technical ? ClearFieldButton.Visibility : Visibility.Collapsed;
        if (!technical)
            PartPickerPanel.Visibility = Visibility.Collapsed;
        ShowSelectedReference();
    }

    void RebuildOptions(BuildTableFamilyItem? familyItem = null, int? preferredRow = null)
    {
        familyItem ??= FamilyList.SelectedItem as BuildTableFamilyItem
            ?? _family.FirstOrDefault(item => item.Document == _currentDocument);

        string slotTitle = familyItem?.Document.Name ?? _name;
        SlotTitleText.Text = slotTitle;

        if (familyItem?.IsOverview == true)
        {
            _selectedArmoryRow = null;
            OverviewPane.Visibility = Visibility.Visible;
            OptionHeaderPanel.Visibility = Visibility.Collapsed;
            OptionList.Visibility = Visibility.Collapsed;
            SlotSubtitleText.Text = "";
            OptionList.ItemsSource = Array.Empty<BuildTableOptionItem>();
            ReferenceList.SelectedItem = null;
            return;
        }

        OverviewPane.Visibility = Visibility.Collapsed;
        OptionHeaderPanel.Visibility = Visibility.Visible;
        OptionList.Visibility = Visibility.Visible;
        SlotSubtitleText.Text = $"{_table.RowCount} options";
        var gunsmithLists = FindGunsmithLists();
        var options = new List<BuildTableOptionItem>();
        for (int rowIndex = 0; rowIndex < _table.RowCount; rowIndex++)
        {
            var allParts = _rows.Where(row => row.BuildRowIndex == rowIndex && row.Editable
                    && (row.Reference.Kind is BuildTableReferenceKind.FileReference
                        or BuildTableReferenceKind.Handle
                        || row.Reference.Kind == BuildTableReferenceKind.ObjectPointer
                            && row.Reference.ComponentIndex is not null))
                .ToList();
            var visibleParts = allParts.Where(part => part.Reference.Kind == BuildTableReferenceKind.FileReference)
                .ToList();
            var selectorParts = allParts.Where(part =>
                {
                    if (part.Reference.Kind == BuildTableReferenceKind.Handle)
                        return true;
                    if (part.Reference.Kind != BuildTableReferenceKind.ObjectPointer)
                        return false;
                    uint? localClass = _localPreviewResources
                        .GetValueOrDefault(part.Reference.Value)?.ClassHash;
                    return localClass == Mesh.ClassHash
                        || localClass == ResourceTypes.Crc32("LODSelector")
                        || part.Resolved?.Type is "Mesh" or "LODSelector";
                })
                .ToList();
            static List<string> NamesOf(IEnumerable<BuildTableReferenceRow> parts) => parts
                .Where(part => part.Reference.Value != 0)
                .Select(part => part.Resolved?.Name ?? part.Target)
                .Where(target => target.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var modelSelectors = NamesOf(selectorParts);
            var linkedAssets = NamesOf(visibleParts);
            BuildTableOptionMetadata? metadata = MetadataForTag(_table.Rows[rowIndex].Tag);
            string display = metadata?.DisplayName
                ?? $"Unresolved game label · BuildTag 0x{_table.Rows[rowIndex].Tag:X8}";
            BuildTableReferenceRow? primary = visibleParts.FirstOrDefault(part => part.Reference.Value != 0)
                ?? selectorParts.FirstOrDefault(part => part.Reference.Value != 0)
                ?? allParts.FirstOrDefault();
            var descriptions = new List<string>();
            if (metadata is not null)
                descriptions.Add($"Gameplay record: {metadata.RecordName}");
            if (modelSelectors.Count > 0)
                descriptions.Add($"Model selector: {string.Join(", ", modelSelectors)}");
            if (linkedAssets.Count > 0)
                descriptions.Add($"Linked assets: {string.Join(", ", linkedAssets)}");
            string internalName = descriptions.Count == 0
                ? "No asset assigned"
                : string.Join("  ·  ", descriptions);
            bool pendingRemoval = metadata is not null
                && _gunsmithRemovals.Contains(metadata.RecordId);
            bool pendingAddition = metadata is not null
                && _gunsmithAdditions.Contains(metadata.RecordId);
            bool hiddenFromGunsmith = pendingRemoval
                || metadata is not null && gunsmithLists.Count > 0
                    && gunsmithLists.All(list => list.IndexOf(metadata.RecordId) < 0);
            string gunsmithBadge = pendingRemoval
                ? "HIDE PENDING SAVE"
                : pendingAddition
                    ? "RESTORE PENDING SAVE"
                    : hiddenFromGunsmith ? "HIDDEN FROM GUNSMITH" : "";
            options.Add(new BuildTableOptionItem
            {
                RowIndex = rowIndex,
                Parts = allParts,
                PrimaryPart = primary,
                DisplayName = display,
                InternalName = internalName,
                IsHiddenFromGunsmith = hiddenFromGunsmith,
                GunsmithBadge = gunsmithBadge,
            });
        }

        OptionList.ItemsSource = options;
        QueueOptionPreviews(options);
        int wanted = preferredRow ?? Math.Min(OptionList.SelectedIndex, options.Count - 1);
        OptionList.SelectedIndex = wanted >= 0 ? wanted : options.Count > 0 ? 0 : -1;
    }

    void Option_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection)
            return;

        _changingSelection = true;
        try
        {
            if (OptionList.SelectedItem is not BuildTableOptionItem option)
            {
                PartBox.ItemsSource = null;
                PartPickerPanel.Visibility = Visibility.Collapsed;
                ReferenceList.SelectedItem = null;
            }
            else
            {
                _selectedArmoryRow = option.RowIndex;
                PartBox.ItemsSource = null;
                PartPickerPanel.Visibility = Visibility.Collapsed;
                ReferenceList.SelectedItem = option.PrimaryPart;
            }
        }
        finally
        {
            _changingSelection = false;
        }

        ShowSelectedReference();
        ShowTargetResults();
    }

    sealed record PreviewCandidate(
        ulong Id,
        string Name,
        uint ClassHash,
        BuildTableReferenceKind ReferenceKind);

    sealed record OptionPreviewRequest(
        BuildTableOptionItem Option,
        IReadOnlyList<PreviewCandidate> Candidates);

    sealed record OptionPreviewResult(
        BuildTableOptionItem Option,
        BuildTableMeshPreview? Preview,
        string? Error);

    void QueueOptionPreviews(IEnumerable<BuildTableOptionItem> options)
    {
        var requests = new List<OptionPreviewRequest>();
        foreach (var option in options)
        {
            var allCandidates = option.Parts
                .Where(part => part.Reference.Value != 0)
                .Select(part =>
                {
                    ulong id = part.Reference.Value;
                    var resolved = part.Resolved;
                    uint classHash = _localPreviewResources.GetValueOrDefault(id)?.ClassHash
                        ?? resolved?.ClassHash ?? 0;
                    string name = _localPreviewResources.GetValueOrDefault(id)?.Name
                        ?? resolved?.Name ?? part.Target;
                    return new PreviewCandidate(id, name, classHash, part.Reference.Kind);
                })
                .OrderBy(candidate => candidate.ClassHash == Mesh.ClassHash ? 0
                    : candidate.ClassHash == ResourceTypes.Crc32("LODSelector") ? 1
                    : candidate.ReferenceKind == BuildTableReferenceKind.Handle ? 2 : 3)
                .DistinctBy(candidate => candidate.Id)
                .ToList();
            var candidates = allCandidates
                .Where(candidate => candidate.ClassHash != 0 || _targetsResolved)
                .ToList();

            if (candidates.Count == 0)
            {
                if (!_targetsResolved && allCandidates.Any(candidate => candidate.ClassHash == 0))
                    option.ShowPreviewStatus("Resolving…", "Waiting for the referenced asset types.");
                else
                    option.ShowPreviewStatus("No mesh", "This BuildTable row has no reachable Mesh resource reference.");
                continue;
            }

            requests.Add(new OptionPreviewRequest(option, candidates));
        }

        if (requests.Count > 0)
            _ = LoadOptionPreviews(requests);
    }

    async Task LoadOptionPreviews(IReadOnlyList<OptionPreviewRequest> requests)
    {
        var results = await Task.WhenAll(requests.Select(ResolveOptionPreview));
        if (_closed)
            return;

        double familyLargestDimension = results
            .Where(result => result.Preview is not null)
            .Select(result => result.Preview!.LargestDimension)
            .DefaultIfEmpty(1)
            .Max();
        var usageByMesh = results
            .Where(result => result.Preview is not null)
            .GroupBy(result => result.Preview!.MeshName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var result in results)
        {
            if (result.Preview is not null)
            {
                result.Option.ShowPreview(result.Preview, familyLargestDimension,
                    usageByMesh[result.Preview.MeshName] > 1);
            }
            else if (result.Error is not null)
            {
                result.Option.ShowPreviewStatus("Preview failed", result.Error);
            }
            else
            {
                result.Option.ShowPreviewStatus("No preview",
                    "No BuildTable model selector could be resolved to a readable Mesh resource.");
            }
        }
    }

    async Task<OptionPreviewResult> ResolveOptionPreview(OptionPreviewRequest request)
    {
        try
        {
            foreach (var candidate in request.Candidates)
            {
                BuildTableMeshPreview? preview = await PreviewFor(candidate);
                if (preview is not null)
                    return new OptionPreviewResult(request.Option, preview, null);
            }

            return new OptionPreviewResult(request.Option, null, null);
        }
        catch (OperationCanceledException)
        {
            // Closing the editor cancels pending preview work.
            return new OptionPreviewResult(request.Option, null, null);
        }
        catch (Exception ex)
        {
            return new OptionPreviewResult(request.Option, null, ex.Message);
        }
    }

    Task<BuildTableMeshPreview?> PreviewFor(PreviewCandidate candidate)
    {
        if (_meshPreviewTasks.TryGetValue(candidate.Id, out var cached))
            return cached;

        var loading = LoadMeshPreview(candidate);
        _meshPreviewTasks[candidate.Id] = loading;
        return loading;
    }

    async Task<BuildTableMeshPreview?> LoadMeshPreview(PreviewCandidate candidate)
    {
        CancellationToken token = _stopResolving.Token;
        await _meshPreviewGate.WaitAsync(token);
        try
        {
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                Resource? resource = _localPreviewResources.GetValueOrDefault(candidate.Id);
                IReadOnlyList<Resource> containerResources = resource is null
                    ? BuildTableTargetResolver.LoadResourceGroup(_archivePaths, candidate.Id, token)
                    : [];
                resource ??= containerResources.FirstOrDefault(item => item.Id == candidate.Id);
                var graph = _localPreviewResources.Values
                    .Concat(containerResources)
                    .GroupBy(item => item.Id)
                    .ToDictionary(group => group.Key, group => group.First());
                resource = FindReachableMesh(resource, graph, token);
                if (resource is null)
                    return null;
                return BuildTableMeshPreviewBuilder.Build(Mesh.Read(resource.Data), resource.Name);
            }, token);
        }
        finally
        {
            _meshPreviewGate.Release();
        }
    }

    static Resource? FindReachableMesh(Resource? root,
        IReadOnlyDictionary<ulong, Resource> graph, CancellationToken token)
    {
        if (root is null)
            return null;

        var pending = new Queue<Resource>();
        var visited = new HashSet<ulong>();
        pending.Enqueue(root);
        while (pending.TryDequeue(out var resource) && visited.Count < 64)
        {
            token.ThrowIfCancellationRequested();
            if (!visited.Add(resource.Id))
                continue;
            if (resource.ClassHash == Mesh.ClassHash)
                return resource;

            ReadOnlySpan<byte> data = resource.Data;
            for (int offset = 0; offset <= data.Length - sizeof(ulong); offset++)
            {
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
                if (!visited.Contains(id) && graph.TryGetValue(id, out var linked))
                    pending.Enqueue(linked);
            }
        }

        // LODSelector payloads use internal handles rather than the resource IDs
        // present in the surrounding DataFile. Their renderable LOD resources are
        // stored beside the selector; LOD1 conventionally precedes it by one ID.
        // Prefer that exact layout, then fall back to a same-named Mesh sibling.
        if (root.ClassHash == ResourceTypes.Crc32("LODSelector"))
        {
            if (root.Id > 0
                && graph.TryGetValue(root.Id - 1, out var lod1)
                && lod1.ClassHash == Mesh.ClassHash)
                return lod1;

            string prefix = root.Name + "_LOD";
            return graph.Values
                .Where(candidate => candidate.ClassHash == Mesh.ClassHash
                    && candidate.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        return null;
    }

    async void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (FamilyList.SelectedItem is not BuildTableFamilyItem { IsOverview: false })
            return;
        if (!_targetsResolved || _armoryIndex is null)
        {
            MessageBox.Show(this,
                "Wait until the installed gameplay records and asset types have finished loading.",
                "Add attachment", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_armoryIndex.MatchesArchives(_archivePaths))
        {
            MessageBox.Show(this,
                "The game archives changed after this Armory editor was opened. Close this editor and open it again; the Toolkit will rebuild the Armory index automatically.",
                "Armory index changed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_saveAttachment is null
            || string.IsNullOrWhiteSpace(_localArchivePath)
            || _localEntryIndex < 0)
        {
            MessageBox.Show(this,
                "This BuildTable was opened without an archive target for new resources.",
                "Add attachment", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var templates = OptionList.Items.OfType<BuildTableOptionItem>()
            .Where(option => MetadataForTag(_table.Rows[option.RowIndex].Tag) is not null
                && !_addedMetadata.ContainsKey(_table.Rows[option.RowIndex].Tag))
            .Select(option => new AddAttachmentTemplate(
                option.RowIndex, option.DisplayName, option.InternalName))
            .ToList();
        if (templates.Count == 0)
        {
            MessageBox.Show(this, "No confirmed installed attachment can be used as a template.",
                "Add attachment", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new AddAttachmentWindow(SlotTitleText.Text, templates) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Draft is not { } draft)
            return;

        ArmoryIndex.IndexedResource? existingRecord = _armoryIndex.DatabaseResources
            .LastOrDefault(resource => string.Equals(resource.Name, draft.InternalName,
                StringComparison.OrdinalIgnoreCase));
        if (existingRecord is not null)
        {
            MessageBox.Show(this,
                $"A gameplay resource named {draft.InternalName} already exists in "
                + $"{Path.GetFileName(existingRecord.ArchivePath)}. Base and patch archives share "
                + "one effective game-resource namespace, so the same attachment must not be "
                + "created a second time in DataPC.forge. Restore the previous test or choose a "
                + "different internal name.",
                "Attachment already exists", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            if (_addedMetadata.Values.Any(metadata =>
                    string.Equals(metadata.RecordName, draft.InternalName,
                        StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    $"A newly prepared attachment named {draft.InternalName} already exists.");
            if (draft.AddToGunsmith && _saveGunsmith is null)
                throw new InvalidOperationException("This editor has no Game Bootstrap write target.");
            var templateMetadata = MetadataForTag(_table.Rows[draft.Template.RowIndex].Tag)
                ?? throw new InvalidOperationException("The selected gameplay template is no longer resolved.");
            var modelSource = await ResolveAddModelSource(draft);
            if (_closed)
                return;

            var gunsmithLists = FindGunsmithLists();
            SetMetadataLoading(true, $"Building {draft.DisplayName} and validating its game resources…");
            // Catch expected validation/import failures in the worker itself. A faulted
            // Task used to make Visual Studio stop at the original throw as
            // "user-unhandled" before this method's outer catch/finally could clear the
            // loading overlay, which looked like a frozen or crashed editor.
            var attempt = await Task.Run(() =>
            {
                try
                {
                    return (Plan: AttachmentAddPipeline.Build(
                        _armoryIndex, _table, draft.Template.RowIndex, templateMetadata, draft,
                        modelSource.Target, modelSource.TemplateSelectorId, gunsmithLists,
                        GunsmithRecordOrder(), _gameMetadata.OwnerRecordIds,
                        _gameMetadata.LanguagePackage, _archivePaths, _localPreviewResources,
                        _localResourceIndexes, _localArchivePath, _localEntryIndex,
                        _localEntryName, _preparedResourceData, _preparedIds),
                        Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Plan: (AttachmentAddPlan?)null, Error: ex);
                }
            });
            if (_closed)
                return;
            if (attempt.Error is not null)
            {
                SetError($"Could not add the attachment: {attempt.Error.Message}");
                MessageBox.Show(this, attempt.Error.Message, "Could not add attachment",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            AttachmentAddPlan plan = attempt.Plan!;

            var localChanges = plan.LocalChanges.Append(new BuildTableResourceChange(
                _currentDocument.Source.ResourceIndex, _currentDocument.Name, plan.BuildTableData)).ToList();
            _saveAttachment(new AttachmentSaveChanges(draft.DisplayName, localChanges,
                plan.DatabaseChanges, plan.ResourceAdditions, plan.EntryAdditions));
            _armoryIndex.ApplyAdditions(plan.ResourceAdditions);
            if (plan.DatabaseChanges.Count > 0)
                _armoryIndex.ApplyChanges(plan.DatabaseChanges);

            foreach (BuildTableResourceChange change in plan.LocalChanges)
            {
                BuildTableDocument? document = _documents.FirstOrDefault(candidate =>
                    candidate.Source.ResourceIndex == change.ResourceIndex);
                if (document is not null)
                {
                    document.SavedData = (byte[])change.Data.Clone();
                    document.Table = BuildTable.Read(change.Data);
                }
                ulong resourceId = _localResourceIndexes.FirstOrDefault(pair =>
                    pair.Value == change.ResourceIndex).Key;
                if (resourceId != 0
                    && _localPreviewResources.TryGetValue(resourceId, out Resource? localResource))
                    localResource.Data = (byte[])change.Data.Clone();
            }

            _addedMetadata[plan.Metadata.BuildTag] = plan.Metadata;
            _addedGunsmithVisibility[plan.Metadata.RecordId] = plan.AddedToGunsmith;
            _addedOwnersByRecordId[plan.Metadata.RecordId] = plan.OwnerNames;
            _preparedIds.Add(plan.Metadata.NameStringId);
            foreach (ArmoryDatabaseResourceChange change in plan.DatabaseChanges)
                _preparedResourceData[(change.ArchivePath, change.EntryIndex,
                    change.ResourceIndex)] = (byte[])change.Data.Clone();
            foreach (ArmoryArchiveResourceAddition addition in plan.ResourceAdditions)
                _preparedIds.Add(addition.ResourceId);
            foreach (ArmoryArchiveEntryAddition addition in plan.EntryAdditions)
                _preparedIds.Add(addition.EntryId);
            foreach (Resource resource in plan.PreviewResources)
                _localPreviewResources[resource.Id] = resource;
            Resource? selector = plan.PreviewResources.FirstOrDefault(resource =>
                resource.Id == plan.ModelSelectorId);
            _targets[plan.ModelSelectorId] = new BuildTableTarget(plan.ModelSelectorId,
                selector?.Name ?? modelSource.Target.Name,
                selector?.ClassHash ?? modelSource.Target.ClassHash,
                ResourceTypes.NameOf(selector?.ClassHash ?? modelSource.Target.ClassHash),
                _localEntryName, Path.GetFileName(_localArchivePath));

            _currentDocument.SavedData = (byte[])plan.BuildTableData.Clone();
            _currentDocument.Table = BuildTable.Read(plan.BuildTableData);
            _table = _currentDocument.Table;
            int newRowIndex = draft.Template.RowIndex + 1;
            RebuildRows(newRowIndex);
            RebuildOptions(preferredRow: newRowIndex);
            MarkDirty();
            ShowSelectedReference();
            ShowTargetResults();
            SetStatus($"Added {draft.DisplayName} to the change list with "
                + $"{plan.ResourceAdditions.Count} new resource(s) and "
                + $"{plan.EntryAdditions.Count} new asset container(s). Use Apply changes, then restart Wildlands.");
        }
        catch (Exception ex)
        {
            SetError($"Could not add the attachment: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Add attachment",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetMetadataLoading(false);
        }
    }

    async Task<ResolvedAddModelSource> ResolveAddModelSource(AddAttachmentDraft draft)
    {
        var templateHandles = _table.Rows[draft.Template.RowIndex].References
            .Where(reference => reference.Value != 0
                && (reference.Kind == BuildTableReferenceKind.Handle
                    || reference.Kind == BuildTableReferenceKind.ObjectPointer
                        && reference.ComponentIndex is not null))
            .ToList();
        BuildTableTarget? templateTarget = null;
        ulong templateSelectorId = 0;
        foreach (BuildTableReference reference in templateHandles)
        {
            BuildTableTarget? candidate = Resolve(reference.Value);
            if (candidate is null
                && _localPreviewResources.TryGetValue(reference.Value, out Resource? localCandidate))
                candidate = new BuildTableTarget(localCandidate.Id, localCandidate.Name,
                    localCandidate.ClassHash, ResourceTypes.NameOf(localCandidate.ClassHash),
                    _localEntryName, Path.GetFileName(_localArchivePath));
            if (candidate is null || candidate.ClassHash == 0)
                candidate = await Task.Run(() => BuildTableTargetResolver.ResolveOne(
                    _archivePaths, reference.Value, _stopResolving.Token));
            if (candidate is not null && (candidate.ClassHash == Mesh.ClassHash
                || candidate.ClassHash == ResourceTypes.Crc32("LODSelector")))
            {
                templateTarget = candidate;
                templateSelectorId = reference.Value;
                break;
            }
        }
        if (templateTarget is null)
            throw new InvalidOperationException(
                "The selected template has no model-selector handle that resolves to a Mesh or LODSelector.");

        BuildTableTarget? target;
        if (draft.ImportModel)
        {
            target = templateTarget;
        }
        else
        {
            string input = draft.ModelOrAsset.Trim();
            IEnumerable<BuildTableTarget> exact;
            if (TryId(input, out ulong id))
                exact = _targets.Values.Concat(_catalog).Where(candidate => candidate.Id == id);
            else
                exact = _targets.Values.Concat(_catalog).Where(candidate =>
                    string.Equals(candidate.Name, input, StringComparison.OrdinalIgnoreCase));
            var candidates = exact.DistinctBy(candidate => candidate.Id).ToList();
            if (candidates.Count == 0 && TryId(input, out id)
                && _localPreviewResources.TryGetValue(id, out Resource? local))
                candidates.Add(new BuildTableTarget(local.Id, local.Name, local.ClassHash,
                    ResourceTypes.NameOf(local.ClassHash), _localEntryName,
                    Path.GetFileName(_localArchivePath)));

            var resolvedCandidates = new List<BuildTableTarget>();
            foreach (BuildTableTarget candidate in candidates)
            {
                BuildTableTarget? resolvedCandidate = candidate.ClassHash == 0
                    ? await Task.Run(() => BuildTableTargetResolver.ResolveOne(
                        _archivePaths, candidate.Id, _stopResolving.Token))
                    : candidate;
                if (resolvedCandidate is not null)
                    resolvedCandidates.Add(resolvedCandidate);
            }
            uint selectorHash = ResourceTypes.Crc32("LODSelector");
            target = resolvedCandidates
                .Where(candidate => candidate.ClassHash == selectorHash
                    || candidate.ClassHash == Mesh.ClassHash)
                .OrderBy(candidate => candidate.ClassHash == selectorHash ? 0 : 1)
                .FirstOrDefault();
        }

        if (target is null || target.Id == 0 || target.ClassHash == 0)
            throw new InvalidOperationException(
                "The model source could not be resolved to an exact installed resource. Use its exact asset name or hexadecimal ID.");
        if (target.ClassHash != Mesh.ClassHash
            && target.ClassHash != ResourceTypes.Crc32("LODSelector"))
            throw new InvalidOperationException(
                $"{target.Name} is {ResourceTypes.NameOf(target.ClassHash)}, not a Mesh or LODSelector.");
        return new ResolvedAddModelSource(target, templateSelectorId);
    }

    sealed record ResolvedAddModelSource(BuildTableTarget Target, ulong TemplateSelectorId);

    void Part_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_changingSelection || PartBox.SelectedItem is not BuildTableReferenceRow part)
            return;
        ReferenceList.SelectedItem = part;
        ShowSelectedReference();
        ShowTargetResults();
    }

    void RebuildRows(int? preferredBuildRow = null)
    {
        var saved = BuildTable.Read(_savedData).References
            .Where(reference => reference.Kind != BuildTableReferenceKind.TableIdentity)
            .GroupBy(reference => reference.Path)
            .ToDictionary(group => group.Key, group => new Queue<ulong>(group.Select(reference => reference.Value)));

        _rows.Clear();
        int index = 1;
        foreach (var reference in _table.References
            .Where(reference => reference.Kind != BuildTableReferenceKind.TableIdentity))
        {
            ulong? savedValue = saved.TryGetValue(reference.Path, out var values) && values.Count > 0
                ? values.Dequeue()
                : null;
            _rows.Add(new BuildTableReferenceRow(
                index++, reference, _table.Id, _name, Resolve, savedValue));
        }

        ShowReferences();
        if (preferredBuildRow is int rowIndex)
        {
            var preferred = (ReferenceList.ItemsSource as IEnumerable<BuildTableReferenceRow>)?
                .FirstOrDefault(row => row.BuildRowIndex == rowIndex && row.Editable);
            if (preferred is not null)
                ReferenceList.SelectedItem = preferred;
        }
        if (ReferenceList.SelectedItem is null && ReferenceList.Items.Count > 0)
            ReferenceList.SelectedIndex = 0;
    }

    async void ResolveTargets(object sender, RoutedEventArgs e)
    {
        Loaded -= ResolveTargets;
        var wanted = _documents.SelectMany(document => document.Table.References)
            .Where(reference => reference.Kind != BuildTableReferenceKind.TableIdentity)
            .Select(reference => reference.Value)
            .Where(value => value != 0 && value != _table.Id)
            .Distinct()
            .ToList();
        var progress = new Progress<string>(message =>
        {
            if (!_closed && !_dirty)
            {
                SetStatus(message);
                MetadataLoadingText.Text = message;
            }
        });

        SetMetadataLoading(true, "Reading confirmed labels from the installed game files…");
        try
        {
            _ownerEntityIds = FindSameContainerEntityBuilderIds();
            var targetTask = Task.Run(() => BuildTableTargetResolver.Build(
                _archivePaths, _localTargets, wanted, progress, _stopResolving.Token));
            var languageTask = _armoryIndex is null
                ? Task.Run(() => BuildTableGameMetadataResolver.FindAvailableLanguagePackages(
                    _archivePaths, _stopResolving.Token))
                : Task.FromResult(_armoryIndex.LanguagePackages);
            var metadataTask = Task.Run(() => _armoryIndex is null
                ? BuildTableGameMetadataResolver.Build(
                    _archivePaths, _ownerEntityIds, progress, _stopResolving.Token,
                    buildTags: FamilyBuildTags())
                : BuildTableGameMetadataResolver.Build(_armoryIndex, _ownerEntityIds,
                    cancellationToken: _stopResolving.Token, buildTags: FamilyBuildTags()));
            await Task.WhenAll(targetTask, metadataTask, languageTask);
            if (_closed)
                return;

            var result = targetTask.Result;
            _gameMetadata = metadataTask.Result;
            _targetsResolved = true;
            SetAvailableLanguages(languageTask.Result, _gameMetadata.LanguagePackage);

            foreach (var target in result.ById.Values)
                _targets[target.Id] = target;
            _catalog = result.Targets;

            RefreshResolvedNames();

            int resolved = wanted.Count(id => _targets.ContainsKey(id));
            string ownerStatus = _gameMetadata.OwnerRecords.Count == 0
                ? "No exact same-container gameplay owner was found; labels remain unresolved."
                : $"Gameplay owner: {string.Join(", ", _gameMetadata.OwnerRecords)} · labels: {_gameMetadata.LanguagePackage}.";
            SetStatus($"Resolved {resolved} of {wanted.Count} referenced assets and "
                + $"{_gameMetadata.ByBuildTag.Count} labels linked by the game database. {ownerStatus}");
        }
        catch (OperationCanceledException)
        {
            // Closing the editor cancels the background catalog cleanly.
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                _targetsResolved = true;
                RefreshResolvedNames();
                SetError($"Could not finish asset-name resolution: {ex.Message}");
            }
        }
        finally
        {
            if (!_closed)
                SetMetadataLoading(false);
        }
    }

    void SetAvailableLanguages(IReadOnlyList<string> packages, string selectedPackage)
    {
        var languages = packages
            .Select(package => new BuildTableLanguageOption(package, DisplayLanguagePackage(package)))
            .ToList();
        if (languages.Count == 0)
        {
            LanguageBox.IsEnabled = false;
            return;
        }

        _settingLanguage = true;
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedItem = languages.FirstOrDefault(language =>
            string.Equals(language.Package, selectedPackage, StringComparison.OrdinalIgnoreCase))
            ?? languages.FirstOrDefault(language =>
                string.Equals(language.Package, "English(US)", StringComparison.OrdinalIgnoreCase))
            ?? languages[0];
        _settingLanguage = false;
        LanguageBox.IsEnabled = true;
    }

    async void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingLanguage || !IsLoaded
            || LanguageBox.SelectedItem is not BuildTableLanguageOption language
            || string.Equals(language.Package, _gameMetadata.LanguagePackage, StringComparison.OrdinalIgnoreCase))
            return;

        _languageLoad?.Cancel();
        _languageLoad?.Dispose();
        _languageLoad = CancellationTokenSource.CreateLinkedTokenSource(_stopResolving.Token);
        CancellationToken token = _languageLoad.Token;
        int version = ++_languageVersion;
        LanguageBox.IsEnabled = false;
        SetStatus($"Loading confirmed {language.DisplayName} game labels…");
        SetMetadataLoading(true, $"Reading the installed {language.DisplayName} language package…");

        try
        {
            var metadata = await Task.Run(() => _armoryIndex is null
                ? BuildTableGameMetadataResolver.Build(_archivePaths, _ownerEntityIds,
                    cancellationToken: token, preferredLanguagePackage: language.Package,
                    buildTags: FamilyBuildTags())
                : BuildTableGameMetadataResolver.Build(_armoryIndex, _ownerEntityIds,
                    preferredLanguagePackage: language.Package, cancellationToken: token,
                    buildTags: FamilyBuildTags()), token);
            if (_closed || version != _languageVersion)
                return;

            _gameMetadata = metadata;
            RefreshResolvedNames();
            SetStatus($"Showing {_gameMetadata.ByBuildTag.Count} labels from the installed "
                + $"{DisplayLanguagePackage(_gameMetadata.LanguagePackage)} package.");
        }
        catch (OperationCanceledException)
        {
            // A newly selected language supersedes this background read.
        }
        catch (Exception ex)
        {
            if (!_closed && version == _languageVersion)
                SetError($"Could not load {language.DisplayName} game labels: {ex.Message}");
        }
        finally
        {
            if (!_closed && version == _languageVersion)
            {
                LanguageBox.IsEnabled = true;
                SetMetadataLoading(false);
            }
        }
    }

    void SetMetadataLoading(bool loading, string text = "")
    {
        MetadataLoadingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (loading)
        {
            MetadataLoadingText.Text = text;
            MetadataLoadingOverlay.Focus();
        }
        Mouse.OverrideCursor = loading ? Cursors.Wait : null;
    }

    static string DisplayLanguagePackage(string package) => package switch
    {
        "English(US)" => "English (US)",
        "German" => "Deutsch",
        "French(France)" => "Français",
        "Italian" => "Italiano",
        "Spanish(Spain)" => "Español",
        "Portuguese(Brazil)" => "Português (Brasil)",
        "Russian" => "Русский",
        "Japanese" => "日本語",
        "Arabic" => "العربية",
        "Dutch" => "Nederlands",
        _ => package,
    };

    IReadOnlyList<ulong> FindSameContainerEntityBuilderIds()
    {
        // Wildlands' attachment root is named <EntityBuilder>_Attachments.
        // This is deliberately an exact same-container resource-key match; no fuzzy
        // filename scoring or slot/name inference is permitted for gameplay labels.
        string? rootName = _family.FirstOrDefault(item => item.IsOverview)?.Document.Name;
        const string suffix = "_Attachments";
        if (string.IsNullOrEmpty(rootName)
            || !rootName.EndsWith(suffix, StringComparison.Ordinal))
            return [];

        string entityBuilderName = rootName[..^suffix.Length];
        return _localTargets
            .Where(target => target.Type == "EntityBuilder"
                && string.Equals(target.Name, entityBuilderName, StringComparison.Ordinal))
            .Select(target => target.Id)
            .Where(id => id != 0)
            .Distinct()
            .ToList();
    }

    IReadOnlyList<uint> FamilyBuildTags() => _documents
        .SelectMany(document => document.Table.Rows)
        .Select(row => row.Tag)
        .Distinct()
        .ToList();

    void RefreshResolvedNames()
    {
        foreach (var row in _rows)
            row.Refresh();
        ShowReferences();
        RebuildOptions();
        ShowSelectedReference();
        ShowTargetResults();
    }

    void ShowReferences()
    {
        string filter = FilterBox.Text.Trim();
        string view = KindBox.SelectedItem as string ?? "Editable fields";
        var selected = ReferenceList.SelectedItem as BuildTableReferenceRow;

        var shown = _rows.Where(row => MatchesView(row, view)
            && (filter.Length == 0
                || row.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.RawPath.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.Category.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.ValueText.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || row.Target.Contains(filter, StringComparison.OrdinalIgnoreCase)
                || (row.Resolved?.Details.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)))
            .ToList();

        ReferenceList.ItemsSource = shown;
        if (selected is not null && shown.Contains(selected))
            ReferenceList.SelectedItem = selected;
        else if (shown.Count > 0)
            ReferenceList.SelectedIndex = 0;
    }

    static bool MatchesView(BuildTableReferenceRow row, string view) => view switch
    {
        "Editable fields" => row.Editable,
        "Variant choices" => row.Category == "Variant choice",
        "Built assets" => row.Category == "Built asset",
        "Related groups" => row.Category == "Related group",
        "Empty optional fields" => row.Category == "Empty optional field",
        "All fields (advanced)" => true,
        "Internal fields" => row.Category == "Internal field",
        "Other fields" => row.Category == "Other asset",
        _ => true,
    };

    void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            ShowReferences();
    }

    void Reference_Changed(object sender, SelectionChangedEventArgs e)
    {
        ShowSelectedReference();
        ShowTargetResults();
    }

    void ShowSelectedReference()
    {
        if (ReferenceList.SelectedItem is not BuildTableReferenceRow row)
        {
            var option = OptionList.SelectedItem as BuildTableOptionItem;
            ReferenceTitle.Text = option?.DisplayName ?? "Select an option";
            ReferenceMeta.Text = option?.InternalName ?? "";
            ReferenceMeta.ToolTip = null;
            ResolvedNameText.Text = "";
            ResolvedText.Text = "";
            ValueBox.Text = "";
            ValueBox.IsEnabled = false;
            TargetSearchBox.IsEnabled = false;
            ReplaceMatchingBox.IsEnabled = false;
            ApplyValueButton.IsEnabled = false;
            string reason = "Select an option first";
            bool canDuplicateOption = option is not null
                && _table.CanDuplicateRow(option.RowIndex, out reason);
            DuplicateRowButton.IsEnabled = canDuplicateOption;
            DuplicateRowButton.ToolTip = canDuplicateOption
                ? "Create another complete option with the same structure"
                : reason;
            RemoveRowButton.IsEnabled = TechnicalModeBox.IsChecked == true
                && option is not null && _table.RowCount > 1;
            ShowGunsmithLinks(option, option is { RowIndex: var emptyOptionRow }
                && (uint)emptyOptionRow < (uint)_table.Rows.Count
                ? MetadataForTag(_table.Rows[emptyOptionRow].Tag)
                : null);
            ClearFieldButton.Visibility = Visibility.Collapsed;
            TargetEmptyText.Text = option is null
                ? "Select an option first."
                : "This option has no visible asset to replace.";
            TargetEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var selectedOption = OptionList.SelectedItem as BuildTableOptionItem;
        ReferenceTitle.Text = TechnicalModeBox.IsChecked == true
            ? row.Path
            : selectedOption?.DisplayName ?? row.Target;
        // During a table mutation WPF can raise SelectionChanged while its old card is
        // still selected, but the new binary table already has one fewer row.
        BuildTableOptionMetadata? optionMetadata = selectedOption is { RowIndex: var optionRow }
            && (uint)optionRow < (uint)_table.Rows.Count
                ? MetadataForTag(_table.Rows[optionRow].Tag)
                : null;
        ShowGunsmithLinks(selectedOption, optionMetadata);
        ReferenceMeta.Text = TechnicalModeBox.IsChecked == true
            ? row.Category
            : optionMetadata is not null
                ? "Game option"
                : "Unresolved option";
        ReferenceMeta.ToolTip = TechnicalModeBox.IsChecked == true
            ? $"Binary field: {row.Reference.Kind}, offset 0x{row.Offset:X}"
            : null;
        ResolvedNameText.Text = TechnicalModeBox.IsChecked != true && selectedOption is not null
            ? selectedOption.DisplayName
            : row.Reference.Value == 0
                ? "No asset assigned"
                : row.Target;
        ResolvedText.Text = TechnicalModeBox.IsChecked != true && selectedOption is not null
            ? selectedOption.InternalName
            : row.Resolved?.Location ?? row.TargetDetails;
        ValueBox.Text = row.ValueText;
        bool technicalEdit = TechnicalModeBox.IsChecked == true && row.Editable;
        ValueBox.IsEnabled = technicalEdit;
        TargetSearchBox.IsEnabled = technicalEdit && ShowAllAssetsBox.IsChecked == true;
        ReplaceMatchingBox.IsEnabled = technicalEdit;
        ApplyValueButton.IsEnabled = technicalEdit;
        string duplicateReason = "Select an option first";
        bool canDuplicate = row.BuildRowIndex is int buildRow
            && _table.CanDuplicateRow(buildRow, out duplicateReason);
        DuplicateRowButton.IsEnabled = technicalEdit && canDuplicate;
        DuplicateRowButton.Visibility = TechnicalModeBox.IsChecked == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        DuplicateRowButton.ToolTip = canDuplicate
            ? "Technical operation: duplicate this exact binary row. No new gameplay option is inferred."
            : duplicateReason;
        RemoveRowButton.Visibility = TechnicalModeBox.IsChecked == true
            ? Visibility.Visible : Visibility.Collapsed;
        RemoveRowButton.IsEnabled = technicalEdit && row.BuildRowIndex is not null && _table.RowCount > 1;
        RemoveRowButton.ToolTip = _table.RowCount > 1
            ? "Technical operation: remove this BuildTable row. This does not change the Gunsmith list."
            : "The last remaining option is kept so the table stays usable";
        ClearFieldButton.Visibility = technicalEdit && row.BuildRowIndex is null
            && row.Reference.Value != 0 && row.Reference.Kind == BuildTableReferenceKind.FileReference
                ? Visibility.Visible
                : Visibility.Collapsed;
        ReplaceMatchingHelp.Text = $"Leave this off to change only {row.Path}. Turn it on to update every field that currently uses {row.Target}.";
    }

    void ShowGunsmithLinks(BuildTableOptionItem? option, BuildTableOptionMetadata? metadata)
    {
        if (TechnicalModeBox.IsChecked == true)
        {
            GunsmithAvailabilityNote.Visibility = Visibility.Collapsed;
            GunsmithLinkPanel.Visibility = Visibility.Collapsed;
            GunsmithAvailabilityButton.Visibility = Visibility.Collapsed;
            return;
        }

        GunsmithLinkPanel.Visibility = Visibility.Collapsed;
        if (option is null)
        {
            GunsmithAvailabilityNote.Visibility = Visibility.Collapsed;
            GunsmithAvailabilityButton.Visibility = Visibility.Collapsed;
            return;
        }

        if (metadata is null)
        {
            GunsmithAvailabilityNote.Visibility = Visibility.Collapsed;
            GunsmithAvailabilityButton.Visibility = Visibility.Visible;
            GunsmithAvailabilityButton.Content = "Gunsmith status unavailable";
            GunsmithAvailabilityButton.IsEnabled = false;
            GunsmithAvailabilityButton.ToolTip = "Not confirmed for Gunsmith.";
            return;
        }

        if (_addedGunsmithVisibility.TryGetValue(metadata.RecordId, out bool addedVisible))
        {
            GunsmithAvailabilityNote.Visibility = Visibility.Visible;
            GunsmithAvailabilityText.Text = addedVisible
                ? "This new option is queued for Gunsmith. Apply changes and restart Wildlands."
                : "This new option is queued without Gunsmith visibility. Apply changes and restart Wildlands.";
            GunsmithAvailabilityButton.Visibility = Visibility.Visible;
            GunsmithAvailabilityButton.Content = addedVisible
                ? "Add pending Apply"
                : "Hidden on creation";
            GunsmithAvailabilityButton.IsEnabled = false;
            GunsmithAvailabilityButton.ToolTip = "Apply or discard the complete new attachment change set first.";
            return;
        }

        var confirmedLists = FindGunsmithLists();
        GunsmithAvailabilityButton.Visibility = Visibility.Visible;
        bool availableInFile = confirmedLists.Any(list => list.IndexOf(metadata.RecordId) >= 0);
        bool pendingRemoval = _gunsmithRemovals.Contains(metadata.RecordId);
        bool pendingAddition = _gunsmithAdditions.Contains(metadata.RecordId);
        bool projectedAvailable = pendingAddition || availableInFile && !pendingRemoval;
        int projectedCount = confirmedLists.FirstOrDefault()?.RecordIds.Count ?? 0;
        projectedCount -= _gunsmithRemovals.Count(id => confirmedLists.Any(list => list.IndexOf(id) >= 0));
        projectedCount += _gunsmithAdditions.Count(id => confirmedLists.All(list => list.IndexOf(id) < 0));
        GunsmithAvailabilityNote.Visibility = Visibility.Visible;
        GunsmithAvailabilityText.Text = pendingRemoval
            ? "Hiding this option is queued. Save it to the change list, then Apply and restart Wildlands."
            : pendingAddition
                ? "Restoring this option is queued. Save it to the change list, then Apply and restart Wildlands."
                : projectedAvailable
                    ? "This option is currently listed in the weapon's edited owner data."
                    : confirmedLists.Count > 0
                        ? "This game option is known, but it is not currently listed in the weapon's edited owner data."
                        : "The game label is known, but the matching Gunsmith list could not be confirmed.";
        GunsmithAvailabilityButton.Content = pendingRemoval
            ? "Hide pending Save"
            : pendingAddition
                ? "Restore pending Save"
                : projectedAvailable ? "Hide from Gunsmith" : "Restore to Gunsmith";
        bool lastVisibleOption = projectedAvailable && projectedCount <= 1;
        GunsmithAvailabilityButton.IsEnabled = confirmedLists.Count > 0
            && !pendingRemoval && !pendingAddition && !lastVisibleOption;
        GunsmithAvailabilityButton.ToolTip = confirmedLists.Count == 0
            ? "No matching Gunsmith list was confirmed."
            : pendingRemoval || pendingAddition
                ? "Save or discard the queued availability change first."
                : lastVisibleOption
                    ? "The last visible option is kept so the Gunsmith list remains recoverable."
                    : projectedAvailable
                        ? "Hide this option from Gunsmith without deleting its BuildTable row or assets."
                        : "Restore this option from its BuildTag and gameplay record.";
    }

    void ToggleGunsmithAvailability_Click(object sender, RoutedEventArgs e)
    {
        if (OptionList.SelectedItem is not BuildTableOptionItem { RowIndex: var rowIndex }
            || (uint)rowIndex >= (uint)_table.Rows.Count
            || MetadataForTag(_table.Rows[rowIndex].Tag) is not { } metadata
            || _addedGunsmithVisibility.ContainsKey(metadata.RecordId))
            return;

        var lists = FindGunsmithLists();
        if (lists.Count == 0 || _gunsmithRemovals.Contains(metadata.RecordId)
            || _gunsmithAdditions.Contains(metadata.RecordId))
            return;

        bool restoring = lists.All(list => list.IndexOf(metadata.RecordId) < 0);
        var answer = MessageBox.Show(this,
            restoring
                ? $"Restore {metadata.DisplayName} to Gunsmith?\n\n"
                    + "Its BuildTable row, gameplay record and assets are already present in the game files."
                : $"Hide {metadata.DisplayName} from Gunsmith?\n\n"
                    + "Its BuildTable row, gameplay record and assets stay in the game files.",
            restoring ? "Restore to Gunsmith" : "Hide from Gunsmith",
            MessageBoxButton.YesNo, restoring ? MessageBoxImage.Question : MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            if (restoring)
                _gunsmithAdditions.Add(metadata.RecordId);
            else
                _gunsmithRemovals.Add(metadata.RecordId);
            GunsmithAvailability.Rewrite(lists, _gunsmithRemovals, _gunsmithAdditions,
                GunsmithRecordOrder());
            MarkDirty();
            RebuildOptions(preferredRow: rowIndex);
            ShowSelectedReference();
            SetStatus(restoring
                ? $"{metadata.DisplayName} will return to Gunsmith after Save and Apply."
                : $"{metadata.DisplayName} will be hidden from Gunsmith after Save and Apply.");
        }
        catch (Exception ex)
        {
            _gunsmithRemovals.Remove(metadata.RecordId);
            _gunsmithAdditions.Remove(metadata.RecordId);
            SetError($"Could not prepare the Gunsmith change: {ex.Message}");
            MessageBox.Show(this, ex.Message, restoring ? "Restore to Gunsmith" : "Hide from Gunsmith",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    IReadOnlyList<ulong> GunsmithRecordOrder() => _table.Rows
        .Select(row => MetadataForTag(row.Tag)?.RecordId ?? 0)
        .Where(id => id != 0)
        .Distinct()
        .ToList();

    BuildTableOptionMetadata? MetadataForTag(uint tag) =>
        _addedMetadata.GetValueOrDefault(tag) ?? _gameMetadata.ByBuildTag.GetValueOrDefault(tag);

    IReadOnlyList<GunsmithAvailabilityList> FindGunsmithLists() =>
        GunsmithAvailability.Find(_armoryIndex, _table, _gameMetadata,
            _addedMetadata, _addedOwnersByRecordId);

    void SearchMode_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            _searchVersion++;
            SearchModeNote.Text = ShowAllAssetsBox.IsChecked == true
                ? "Unverified raw assets are visible. No result is claimed to be game-compatible."
                : "Disabled: no individual asset replacement is confirmed for this exact row.";
            TargetSearchBox.IsEnabled = ShowAllAssetsBox.IsChecked == true
                && TechnicalModeBox.IsChecked == true;
            ShowTargetResults();
            _ = EnrichTargetSearch(_searchVersion);
        }
    }

    void TargetSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded)
        {
            _searchVersion++;
            _searchDelay.Stop();
            _searchDelay.Start();
        }
    }

    async Task EnrichTargetSearch(int version)
    {
        await Task.CompletedTask;
    }

    void ShowTargetResults()
    {
        if (ReferenceList.SelectedItem is not BuildTableReferenceRow row || !row.Editable)
        {
            TargetResults.ItemsSource = null;
            UseTargetButton.IsEnabled = false;
            TargetEmptyText.Text = "Select an editable field first.";
            TargetEmptyText.Visibility = Visibility.Visible;
            return;
        }

        string query = TargetSearchBox.Text.Trim();
        bool showAll = ShowAllAssetsBox.IsChecked == true;
        IEnumerable<BuildTableTarget> matches = showAll
            ? _catalog.Where(target => target.Id != _table.Id)
            : Enumerable.Empty<BuildTableTarget>();

        if (showAll && query.Length < 2)
            matches = Enumerable.Empty<BuildTableTarget>();
        else if (query.Length > 0)
            matches = matches.Where(target => MatchesSearch(target, query));

        var results = matches
            .Select(target => BuildTableCompatibility.Evaluate(_name, row.Reference, row.Resolved, target))
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            .ThenBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToList();
        TargetResults.ItemsSource = results;
        TargetEmptyText.Text = (showAll, query.Length) switch
        {
            (true, 0) => "Type at least 2 characters to search all installed assets.",
            (true, 1) => "Type one more character to start searching.",
            (true, _) => "No asset with that name was found.",
            (false, _) => "No individual asset replacement is confirmed by the game database. Enable the unverified technical search only if you know the exact binary relationship.",
        };
        TargetEmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UseTargetButton.IsEnabled = false;
    }

    void Target_Changed(object sender, SelectionChangedEventArgs e) =>
        UseTargetButton.IsEnabled = ShowAllAssetsBox.IsChecked == true
            && TargetResults.SelectedItem is BuildTableCandidate
            && ReferenceList.SelectedItem is BuildTableReferenceRow { Editable: true };

    async void Target_DoubleClick(object sender, MouseButtonEventArgs e) => await UseSelectedTarget();

    async void UseTarget_Click(object sender, RoutedEventArgs e) => await UseSelectedTarget();

    async Task UseSelectedTarget()
    {
        if (TargetResults.SelectedItem is not BuildTableCandidate selectedCandidate)
            return;

        BuildTableTarget target = selectedCandidate.Target;

        if (target.ClassHash == 0)
        {
            UseTargetButton.IsEnabled = false;
            SetStatus($"Resolving {target.Name} before editing…");
            try
            {
                target = await Task.Run(() => BuildTableTargetResolver.ResolveOne(
                    _archivePaths, target.Id, _stopResolving.Token)) ?? target;
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_closed)
                return;
        }

        _targets[target.Id] = target;
        if (ShowAllAssetsBox.IsChecked != true)
            return;
        ValueBox.Text = $"0x{target.Id:X}";
        ApplyValue(false);
    }

    void Value_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        ApplyValue(ReplaceMatchingBox.IsChecked == true);
        e.Handled = true;
    }

    void ApplyValue_Click(object sender, RoutedEventArgs e) =>
        ApplyValue(ReplaceMatchingBox.IsChecked == true);

    void ApplyValue(bool replaceMatching)
    {
        if (ReferenceList.SelectedItem is not BuildTableReferenceRow selected || !selected.Editable)
            return;

        if (!TryId(ValueBox.Text, out ulong value))
        {
            SetError($"\"{ValueBox.Text}\" is not a 64-bit hexadecimal id. Use for example 0x79B660AD42.");
            ValueBox.SelectAll();
            ValueBox.Focus();
            return;
        }

        ulong old = selected.Reference.Value;
        if (value == old)
        {
            SetStatus($"{selected.Path} already points to {selected.Target}.");
            return;
        }

        if (!ConfirmCompatible(selected, value))
            return;

        int changed = 0;
        if (replaceMatching)
        {
            foreach (var row in _rows.Where(row => row.Editable && row.Reference.Value == old))
            {
                row.Reference.Value = value;
                row.Refresh();
                changed++;
            }
        }
        else
        {
            selected.Reference.Value = value;
            selected.Refresh();
            changed = 1;

        }

        int? selectedRow = SelectedBuildRowIndex();
        MarkDirty();
        ShowReferences();
        RebuildOptions(preferredRow: selectedRow);
        ShowSelectedReference();
        ShowTargetResults();
        SetStatus(changed == 1
            ? $"Changed {selected.Path} to {selected.Target}. Save adds it to the change list."
            : $"Changed {changed} matching asset links to {selected.Target}. Save adds them to the change list.");
    }

    bool ConfirmCompatible(BuildTableReferenceRow row, ulong newValue)
    {
        if (newValue == 0)
        {
            if (row.BuildRowIndex is null)
                return true;
            SetError("An option cannot be emptied one field at a time. Use Remove this option so its selector and resources stay consistent.");
            return false;
        }

        var newTarget = Resolve(newValue);
        if (newTarget is not null)
        {
            var compatibility = BuildTableCompatibility.Evaluate(_name, row.Reference, row.Resolved, newTarget);

            var compatibilityAnswer = MessageBox.Show(this,
                $"No gameplay-database link confirms {newTarget.Name} for {row.Path}.\n\n"
                + $"{compatibility.Reason}\n\nThis edit changes only one raw reference; it does not update the BuildTag, "
                + "selector, gameplay record or companion resources. Continue as an unverified technical edit?",
                "Unverified BuildTable change", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return compatibilityAnswer == MessageBoxResult.Yes;
        }

        var answer = MessageBox.Show(this,
            $"Asset 0x{newValue:X} could not be resolved, so its compatibility cannot be checked.\n\n"
            + "Use this ID anyway?",
            "Unknown BuildTable asset", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        return answer == MessageBoxResult.Yes;
    }

    void DuplicateRow_Click(object sender, RoutedEventArgs e)
    {
        if (TechnicalModeBox.IsChecked != true)
            return;
        if (!TryGetSelectedBuildRowIndex(out int rowIndex))
            return;

        try
        {
            byte[] data = _table.DuplicateRow(rowIndex);
            ClearSelectionForTableMutation();
            _table = BuildTable.Read(data);
            RebuildRows(rowIndex + 1);
            RebuildOptions(preferredRow: rowIndex + 1);
            MarkDirty();
            ShowSelectedReference();
            ShowTargetResults();
            SetStatus($"Duplicated a technical BuildTable row. This does not add a Gunsmith option.");
        }
        catch (Exception ex)
        {
            SetError($"Could not add the option: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Add BuildTable option",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (TechnicalModeBox.IsChecked != true)
            return;
        if (!TryGetSelectedBuildRowIndex(out int rowIndex) || _table.RowCount <= 1)
            return;

        var answer = MessageBox.Show(this,
            $"Remove technical BuildTable row {rowIndex + 1}?\n\n"
            + "This changes only this table's mapping. It does not remove, hide or alter the corresponding Gunsmith option in Game Bootstrap Settings.",
            "Remove technical BuildTable row", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.Yes)
            return;

        try
        {
            byte[] data = _table.RemoveRow(rowIndex);
            ClearSelectionForTableMutation();
            _table = BuildTable.Read(data);
            RebuildRows(Math.Min(rowIndex, _table.RowCount - 1));
            RebuildOptions(preferredRow: Math.Min(rowIndex, _table.RowCount - 1));
            MarkDirty();
            ShowSelectedReference();
            ShowTargetResults();
            SetStatus($"Removed technical BuildTable row {rowIndex + 1}. Gunsmith availability was not changed.");
        }
        catch (Exception ex)
        {
            SetError($"Could not remove the option safely: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Remove technical BuildTable row",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void ClearField_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceList.SelectedItem is not BuildTableReferenceRow row
            || row.BuildRowIndex is not null || !row.Editable
            || row.Reference.Kind != BuildTableReferenceKind.FileReference)
            return;

        row.Reference.Value = 0;
        row.Refresh();
        MarkDirty();
        ShowReferences();
        RebuildOptions();
        ShowSelectedReference();
        ShowTargetResults();
        SetStatus($"Cleared {row.Path}. Save adds the updated table to the change list.");
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _currentDocument.Table = _table;
            int? selectedRow = SelectedBuildRowIndex();
            var changed = _documents.Where(document => document.Dirty)
                .Select(document =>
                {
                    byte[] data = document.Table.Write();
                    Validate(document.Table, data);
                    return new BuildTableResourceChange(
                        document.Source.ResourceIndex, document.Name, data);
                })
                .ToList();
            bool gunsmithDirty = _gunsmithRemovals.Count > 0 || _gunsmithAdditions.Count > 0;
            var gunsmithChanges = !gunsmithDirty
                ? []
                : GunsmithAvailability.Rewrite(
                    FindGunsmithLists(),
                    _gunsmithRemovals, _gunsmithAdditions, GunsmithRecordOrder());
            if (changed.Count == 0 && gunsmithChanges.Count == 0)
                return;

            if (changed.Count > 0)
                _save(changed);
            if (gunsmithChanges.Count > 0)
            {
                if (_saveGunsmith is null)
                    throw new InvalidOperationException("This editor was not given a Game Bootstrap write target.");
                _saveGunsmith(gunsmithChanges);
                if (_armoryIndex is not null)
                {
                    // The callback has just replaced the pending database resource.
                    // Mirror those bytes in the session index so the next toggle is
                    // based on the current list instead of overwriting this change.
                    _armoryIndex.ApplyChanges(gunsmithChanges);
                    _gameMetadata = BuildTableGameMetadataResolver.Build(_armoryIndex, _ownerEntityIds,
                        preferredLanguagePackage: _gameMetadata.LanguagePackage,
                        buildTags: FamilyBuildTags());
                }
            }
            foreach (var change in changed)
            {
                var document = _documents.First(document => document.Source.ResourceIndex == change.ResourceIndex
                    && string.Equals(document.Name, change.Name, StringComparison.Ordinal));
                document.SavedData = (byte[])change.Data.Clone();
                document.Table = BuildTable.Read(change.Data);
            }
            int gunsmithChangeCount = gunsmithDirty
                ? _gunsmithRemovals.Count + _gunsmithAdditions.Count
                : 0;
            if (gunsmithDirty)
            {
                _gunsmithRemovals.Clear();
                _gunsmithAdditions.Clear();
            }
            _table = _currentDocument.Table;
            RebuildRows(selectedRow);
            RebuildOptions(preferredRow: selectedRow);
            MarkDirty();
            SetStatus(gunsmithChangeCount > 0
                ? $"Saved {gunsmithChangeCount} confirmed Gunsmith change(s) to the change list. Apply changes, restart Wildlands, then reopen the Armory."
                : $"Saved {changed.Count} {(changed.Count == 1 ? "table" : "tables")} "
                    + "to the main window's change list. Apply changes writes them into the archive.");
        }
        catch (Exception ex)
        {
            SetError($"Could not save the BuildTable: {ex.Message}");
            MessageBox.Show(this, ex.Message, "BuildTable editor", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    static void Validate(BuildTableAsset expectedTable, byte[] data)
    {
        var checkedTable = BuildTable.Read(data);
        if (checkedTable.Id != expectedTable.Id
            || checkedTable.ColumnCount != expectedTable.ColumnCount
            || checkedTable.RowCount != expectedTable.RowCount
            || checkedTable.References.Count != expectedTable.References.Count)
            throw new InvalidOperationException("The edited table did not parse back to the same structure.");

        for (int i = 0; i < expectedTable.References.Count; i++)
        {
            var expected = expectedTable.References[i];
            var actual = checkedTable.References[i];
            if (actual.Offset != expected.Offset || actual.Kind != expected.Kind || actual.Value != expected.Value)
                throw new InvalidOperationException(
                    $"Reference {i} did not read back exactly at offset 0x{expected.Offset:X}.");
        }
    }

    void Discard_Click(object sender, RoutedEventArgs e)
    {
        foreach (var document in _documents)
            document.Table = BuildTable.Read(document.SavedData);
        _table = _currentDocument.Table;
        _gunsmithRemovals.Clear();
        _gunsmithAdditions.Clear();
        RebuildRows();
        RebuildOptions();
        MarkDirty();
        ShowSelectedReference();
        ShowTargetResults();
        SetStatus("Discarded edits made since the last save.");
    }

    void MarkDirty()
    {
        _currentDocument.Table = _table;
        _dirty = _documents.Any(document => document.Dirty)
            || _gunsmithRemovals.Count > 0 || _gunsmithAdditions.Count > 0;
        SaveButton.IsEnabled = _dirty;
        DiscardButton.IsEnabled = _dirty;
        FamilyList.Items.Refresh();
    }

    int? SelectedBuildRowIndex()
    {
        // In the armory view, the selected card is the source of truth. A technical
        // reference can be left over while WPF swaps a BuildTable family item.
        if (TechnicalModeBox.IsChecked == true)
            return (ReferenceList.SelectedItem as BuildTableReferenceRow)?.BuildRowIndex
                ?? (OptionList.SelectedItem as BuildTableOptionItem)?.RowIndex;

        return (OptionList.SelectedItem as BuildTableOptionItem)?.RowIndex ?? _selectedArmoryRow;
    }

    bool TryGetSelectedBuildRowIndex(out int rowIndex)
    {
        rowIndex = SelectedBuildRowIndex() ?? -1;
        if ((uint)rowIndex < (uint)_table.Rows.Count)
            return true;

        // Never pass a stale UI index into the binary editor. Rebuild the visual model
        // from the current document instead, so the user can make an intentional choice.
        RebuildRows();
        RebuildOptions();
        ShowSelectedReference();
        ShowTargetResults();
        SetError("The selected option changed while the table was refreshed. Choose the option again before editing it.");
        return false;
    }

    void ClearSelectionForTableMutation()
    {
        _changingSelection = true;
        try
        {
            // Do this before replacing _table. The old card can otherwise be rendered
            // against the newly shortened Rows collection for one dispatcher turn.
            OptionList.SelectedItem = null;
            ReferenceList.SelectedItem = null;
            PartBox.ItemsSource = null;
            PartPickerPanel.Visibility = Visibility.Collapsed;
            _selectedArmoryRow = null;
        }
        finally
        {
            _changingSelection = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_dirty)
        {
            var answer = MessageBox.Show(this,
                "There are BuildTable edits that were never saved to the change list. Close anyway?",
                "BuildTable editor", MessageBoxButton.YesNo, MessageBoxImage.Question);
            e.Cancel = answer != MessageBoxResult.Yes;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _searchDelay.Stop();
        _stopResolving.Cancel();
        _stopResolving.Dispose();
        _languageLoad?.Cancel();
        _languageLoad?.Dispose();
        base.OnClosed(e);
    }

    static bool TryId(string text, out ulong value)
    {
        value = 0;
        text = text.Trim().Replace("_", "", StringComparison.Ordinal);
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        return text.Length > 0
            && ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    static bool MatchesSearch(BuildTableTarget target, string query)
    {
        string haystack = $"{target.Name} {target.Type} {target.Container} {target.Archive} 0x{target.Id:X}"
            .Replace('_', ' ').Replace('-', ' ');
        string[] terms = query.Replace('_', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length > 0 && terms.All(term =>
            haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    void SetError(string text)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource("Warning");
    }

    void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource("TextDim");
    }
}

public sealed record BuildTableLanguageOption(string Package, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public sealed class BuildTableReferenceRow : INotifyPropertyChanged
{
    readonly ulong _tableId;
    readonly string _tableName;
    readonly Func<ulong, BuildTableTarget?> _resolve;
    ulong _savedValue;
    bool _hasSavedValue;

    public BuildTableReferenceRow(int index, BuildTableReference reference,
        ulong tableId, string tableName, Func<ulong, BuildTableTarget?> resolve,
        ulong? savedValue = null)
    {
        Index = index;
        Reference = reference;
        _tableId = tableId;
        _tableName = tableName;
        _resolve = resolve;
        _savedValue = savedValue ?? reference.Value;
        _hasSavedValue = savedValue.HasValue;
    }

    public int Index { get; }
    public BuildTableReference Reference { get; }
    public int Offset => Reference.Offset;
    public string RawPath => Reference.Path;
    public int? BuildRowIndex => ParseBuildRowIndex(RawPath);
    public BuildTableTarget? Resolved => _resolve(Reference.Value);
    public string ValueText => $"0x{Reference.Value:X}";
    public bool Internal => Reference.Value == _tableId;
    public bool Editable => !Internal;
    public bool Changed => !_hasSavedValue || Reference.Value != _savedValue;
    public string Mark => Changed ? "●" : "";

    public string Category
    {
        get
        {
            if (Internal)
                return "Internal field";
            if (Reference.Value == 0)
                return "Empty optional field";

            string type = Resolved?.Type ?? "";
            if (type == "LODSelector" || Reference.Kind == BuildTableReferenceKind.Handle)
                return "Variant choice";
            if (type == "BuildTable" || Reference.Kind is BuildTableReferenceKind.Table
                or BuildTableReferenceKind.ObjectPointer)
                return "Related group";
            if (Reference.Kind == BuildTableReferenceKind.FileReference)
                return "Built asset";
            return "Other asset";
        }
    }

    public string Target => Reference.Value switch
    {
        0 => "None",
        _ when Internal => $"This BuildTable · {_tableName}",
        _ when Resolved is { } target => target.Name,
        _ => $"Unresolved asset · 0x{Reference.Value:X}",
    };

    public string TargetDetails => Reference.Value switch
    {
        0 => "No asset is assigned to this slot.",
        _ when Internal => "Internal owner link. It is kept read-only because changing it would detach the row or column from this BuildTable.",
        _ when Resolved is { } target => target.Details,
        _ => "This ID was not found as a resource or forge entry in the installed archives.",
    };

    public string PartLabel => Reference.Kind switch
    {
        BuildTableReferenceKind.FileReference when Resolved?.Type == "BuildTable" => "Linked armory slot",
        BuildTableReferenceKind.FileReference => "Visible asset",
        _ when Resolved?.Type is "Mesh" or "LODSelector" => "Variant behavior",
        BuildTableReferenceKind.Handle => "Variant behavior",
        BuildTableReferenceKind.Table or BuildTableReferenceKind.ObjectPointer => "Related asset group",
        _ => "Asset link",
    };

    public string Path => FriendlyPath(RawPath, Category);

    static string FriendlyPath(string path, string category)
    {
        var parts = path.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && int.TryParse(parts[1], out int index))
        {
            int shown = index + 1;
            if (parts[0] == "row")
            {
                if (path.Contains(" component ", StringComparison.Ordinal))
                    return category switch
                    {
                        "Variant choice" => $"Option {shown} · variant choice",
                        "Built asset" => $"Option {shown} · visible asset",
                        "Related group" => $"Option {shown} · related asset group",
                        "Empty optional field" => $"Option {shown} · optional asset",
                        _ => $"Option {shown} · asset",
                    };
                if (path.EndsWith(" table", StringComparison.Ordinal))
                    return $"Option {shown} · owning asset group";
            }

            if (parts[0] == "column")
            {
                if (path.Contains(" component ", StringComparison.Ordinal))
                    return category == "Empty optional field"
                        ? $"Column {shown} · optional default resource"
                        : $"Column {shown} · default asset";
                if (path.EndsWith(" table", StringComparison.Ordinal))
                    return $"Column {shown} · owning asset group";
                if (path.Contains("target material template", StringComparison.Ordinal))
                    return $"Column {shown} · target material template";
                if (path.Contains("target material", StringComparison.Ordinal))
                    return $"Column {shown} · target material";
            }

            if (parts[0] == "sub-table")
                return $"Additional asset group {shown}";
        }

        if (path == "associated entity builder")
            return "Associated entity / asset group";
        if (path.StartsWith("additional table ", StringComparison.Ordinal))
            return "Additional asset group " + (ParseLast(path) + 1);
        if (path.StartsWith("default selector selection ", StringComparison.Ordinal))
            return "Default selection " + (ParseLast(path) + 1);

        return string.Join(' ', path.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((word, i) => i == 0 && word.Length > 0
                ? char.ToUpperInvariant(word[0]) + word[1..]
                : word));
    }

    static int ParseLast(string path) =>
        int.TryParse(path.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1], out int value)
            ? value
            : 0;

    public void Accept()
    {
        _savedValue = Reference.Value;
        _hasSavedValue = true;
        Refresh();
    }

    public void Discard()
    {
        if (_hasSavedValue)
            Reference.Value = _savedValue;
    }

    static int? ParseBuildRowIndex(string path)
    {
        if (!path.StartsWith("row ", StringComparison.Ordinal))
            return null;
        string value = path.AsSpan(4).ToString().Split(' ', 2)[0];
        return int.TryParse(value, out int index) && index >= 0 ? index : null;
    }

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Resolved)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ValueText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Target)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TargetDetails)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PartLabel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Category)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Path)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Internal)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Editable)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mark)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Changed)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
