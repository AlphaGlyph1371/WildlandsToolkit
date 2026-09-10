using System.Buffers.Binary;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
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
    const ulong MediumVestConfigurationTableId = 0x001E9ED02A7B;

    BuildTableAsset _table;
    readonly List<BuildTableDocument> _documents;
    BuildTableDocument _currentDocument;
    IReadOnlyList<BuildTableFamilyItem> _family;
    readonly ListCollectionView _familyView;
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
    readonly bool _weaponFamily;
    readonly HashSet<ulong> _containerLocalReferenceIds;
    readonly HashSet<ulong> _gunsmithRemovals = [];
    readonly HashSet<ulong> _gunsmithAdditions = [];
    readonly Dictionary<uint, BuildTableOptionMetadata> _addedMetadata = [];
    readonly Dictionary<ulong, IReadOnlySet<ulong>> _addedOwnerIdsByRecordId = [];
    readonly Dictionary<ulong, bool> _addedGunsmithVisibility = [];
    readonly Dictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]> _preparedResourceData = [];
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
    Task<BuildTableTargetCatalog?>? _fullCatalogTask;

    IReadOnlyList<BuildTableTarget> _catalog = [];
    int _searchVersion;
    int _languageVersion;
    bool _dirty;
    bool _closed;
    bool _changingSelection;
    bool _settingLanguage;
    bool _targetsResolved;
    bool _fullCatalogLoaded;
    int? _selectedArmoryRow;

    public BuildTableWindow(byte[] data, string name,
        IReadOnlyList<BuildTableTarget> localTargets,
        IReadOnlyList<string> archivePaths,
        Action<byte[]> save) : this(data, name, localTargets, archivePaths, [new BuildTableResourceSource(-1, BuildTable.Read(data).Id, name, data)], changes => save(changes.Single().Data), null, null)
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
        _localResourceIndexes = localResourceIndexes ?? new Dictionary<ulong, int>();
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
        _currentDocument = _documents.FirstOrDefault(document => document.Id == openedId) ?? new BuildTableDocument(new BuildTableResourceSource(-1, openedId, name, data));
        if (!_documents.Contains(_currentDocument))
            _documents.Add(_currentDocument);
        _table = _currentDocument.Table;
        _family = BuildTableFamily.Find(_documents, _currentDocument, localTargets);
        _containerLocalReferenceIds = _documents
            .SelectMany(document => document.Table.References)
            .Where(reference => reference.Value != 0 && reference.IsContainerLocal)
            .Select(reference => reference.Value)
            .ToHashSet();

        foreach (var target in localTargets)
            if (target.Id != 0)
                _targets[target.Id] = target;
        _catalog = localTargets.OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase).ToList();

        RebuildRows();

        string familyTitle = _family.FirstOrDefault(item => item.IsOverview)?.Document.Name ?? name;
        _weaponFamily = _family.Any(item => IsWeaponDisplaySlot(item.Slot));
        Title = $"{familyTitle} - BuildTable editor";
        FamilyTitleText.Text = familyTitle;
        FamilySubtitleText.Text = _family.Count > 1
            ? $"{_family.Count - 1} linked BuildTables"
            : "One table";
        OverviewTitleText.Text = _weaponFamily ? "Weapon BuildTable connections" : "BuildTable connections";
        OverviewSummaryText.Text = _weaponFamily
            ? "This table is the map of this weapon's armory."
            : "This table connects an asset to its related BuildTables.";
        OverviewDetailsText.Text = _weaponFamily
            ? "It connects the weapon to its attachment categories. It does not contain individual barrels, sights or magazines itself."
            : "BuildTable families may span variants, models, materials, colors, loadouts and compatibility tables. Choose a linked table to inspect its actual rows.";
        _familyView = new ListCollectionView(_family.ToList());
        _familyView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(BuildTableFamilyItem.GroupTitle)));
        FamilyList.ItemsSource = _familyView;
        RefreshFamilySearch();
        KindBox.ItemsSource = new[]
        {
            "Editable fields",
            "Variant choices",
            "Built assets",
            "Related groups",
            "Container-local references",
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

    void FamilySearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (IsInitialized)
            RefreshFamilySearch();
    }

    void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            FamilySearchBox.Focus();
            FamilySearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && FamilySearchBox.IsKeyboardFocusWithin
            && FamilySearchBox.Text.Length > 0)
        {
            FamilySearchBox.Clear();
            e.Handled = true;
        }
    }

    void RefreshFamilySearch()
    {
        string[] words = FamilySearchBox.Text
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Matches(BuildTableFamilyItem item) => words.All(word =>
            item.SearchText.Contains(word, StringComparison.OrdinalIgnoreCase));

        _familyView.Filter = candidate => candidate is BuildTableFamilyItem item && Matches(item);
        _familyView.Refresh();

        int visible = _family.Count(Matches);
        FamilySearchCountText.Text = words.Length == 0
            ? $"{visible:N0} tables in {BuildTableNames.GroupCount(_family)} groups"
            : $"{visible:N0} of {_family.Count:N0} tables";

        if (FamilyList.SelectedItem is not BuildTableFamilyItem selected || !Matches(selected))
            FamilyList.SelectedItem = _family.FirstOrDefault(item => !item.IsOverview && Matches(item))
                ?? _family.FirstOrDefault(Matches);
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
        OptionPane.Visibility = technical ? Visibility.Collapsed : Visibility.Visible;
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
        familyItem ??= FamilyList.SelectedItem as BuildTableFamilyItem ?? _family.FirstOrDefault(item => item.Document == _currentDocument);

        BuildTableSlot slot = familyItem?.Slot ?? BuildTableSlot.Unknown;
        string internalTableName = familyItem?.Document.Name ?? _name;
        SlotTitleText.Text = internalTableName;

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
        bool weaponTable = IsWeaponDisplaySlot(slot);
        bool selectionValue = familyItem?.IsSelectionValue == true;
        SlotSubtitleText.Text = weaponTable
            ? $"{_table.RowCount} options from {internalTableName}"
            : $"{_table.RowCount} BuildTable rows";
        IReadOnlyList<GunsmithAvailabilityList> gunsmithLists = weaponTable ? FindGunsmithLists() : [];
        TryGetAttachmentWriteSupport(out string addLabel, out string addReason);
        bool vestTable = _table.Id == MediumVestConfigurationTableId;
        int vestTemplateCount = vestTable && _armoryIndex is not null
            ? CharacterItemAddPipeline.FindVestTemplates(_table, _localPreviewResources, MetadataForTag).Count
            : 0;
        AddAttachmentButton.Content = addLabel;
        AddAttachmentButton.IsEnabled = vestTable
            ? _targetsResolved && vestTemplateCount > 0 && _saveAttachment is not null
            : gunsmithLists.Count > 0;
        AddAttachmentButton.ToolTip = vestTable && vestTemplateCount > 0
            ? $"Create a distinct vest from one of {vestTemplateCount} structurally verified installed templates"
            : gunsmithLists.Count > 0
            ? $"Set up a new option for {internalTableName}"
            : addReason;
        AddAttachmentButton.Visibility = weaponTable || _table.Id == MediumVestConfigurationTableId
            ? Visibility.Visible
            : Visibility.Collapsed;
        var options = new List<BuildTableOptionItem>();
        for (int rowIndex = 0; rowIndex < _table.RowCount; rowIndex++)
        {
            var allParts = _rows.Where(row => row.BuildRowIndex == rowIndex && (row.Reference.Kind is BuildTableReferenceKind.FileReference or BuildTableReferenceKind.Handle || row.Reference.Kind == BuildTableReferenceKind.ObjectPointer && row.Reference.ComponentIndex is not null))
                .ToList();
            var selectorParts = allParts.Where(part =>
                {
                    uint? localClass = _localPreviewResources
                        .GetValueOrDefault(part.Reference.Value)?.ClassHash;
                    return localClass == Mesh.ClassHash
                        || localClass == ResourceTypes.Crc32("LODSelector")
                        || part.Resolved?.Type is "Mesh" or "LODSelector";
                })
                .ToList();
            var linkedParts = allParts.Except(selectorParts).ToList();
            static List<string> NamesOf(IEnumerable<BuildTableReferenceRow> parts) => parts
                .Where(part => part.Reference.Value != 0)
                .Select(part => part.Resolved?.Name ?? part.Target)
                .Where(target => target.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            var modelSelectors = NamesOf(selectorParts);
            var linkedAssets = NamesOf(linkedParts);
            BuildTableOptionMetadata? metadata = MetadataForRow(_table.Rows[rowIndex]);
            string display = metadata?.DisplayName
                ?? modelSelectors.FirstOrDefault()
                ?? linkedAssets.FirstOrDefault()
                ?? (selectionValue ? familyItem!.Title : $"Unidentified row {rowIndex + 1}");
            BuildTableReferenceRow? primary = selectorParts.FirstOrDefault(part => part.Reference.Value != 0)
                ?? linkedParts.FirstOrDefault(part => part.Reference.Value != 0)
                ?? allParts.FirstOrDefault();
            var descriptions = new List<string>();
            if (metadata is not null)
                descriptions.Add($"Gameplay record: {metadata.RecordName}");
            if (modelSelectors.Count > 0)
                descriptions.Add($"3D asset: {string.Join(", ", modelSelectors)}");
            if (linkedAssets.Count > 0)
                descriptions.Add($"Linked assets: {string.Join(", ", linkedAssets)}");
            if (selectionValue)
                descriptions.Add("Selection value used by linked BuildTables");
            if (metadata is null && modelSelectors.Count == 0 && linkedAssets.Count == 0)
                descriptions.Add($"Technical BuildTag: 0x{_table.Rows[rowIndex].Tag:X8}");
            string internalName = descriptions.Count == 0
                ? "No asset assigned"
                : string.Join("  ·  ", descriptions);
            bool pendingRemoval = metadata is not null && _gunsmithRemovals.Contains(metadata.RecordId);
            bool pendingAddition = metadata is not null && _gunsmithAdditions.Contains(metadata.RecordId);
            bool hiddenFromGunsmith = pendingRemoval || metadata is not null && gunsmithLists.Count > 0 && gunsmithLists.All(list => list.IndexOf(metadata.RecordId) < 0);
            var branchLabels = _table.Rows[rowIndex].PossibleTags
                .Select(tag => MetadataForTag(tag)?.DisplayName)
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            string rowLabel = selectionValue
                ? "SELECTION VALUE"
                : !weaponTable && branchLabels.Count == 1
                ? $"{branchLabels[0]!.ToUpperInvariant()} · ROW {rowIndex + 1}"
                : weaponTable ? $"OPTION {rowIndex + 1}" : $"ROW {rowIndex + 1}";
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
                RowLabel = rowLabel,
                DisplayName = display,
                InternalName = internalName,
                IsSelectionValue = selectionValue,
                IsHiddenFromGunsmith = hiddenFromGunsmith,
                GunsmithBadge = gunsmithBadge,
            });
        }

        OptionList.ItemsSource = options;
        QueueOptionPreviews(options);
        int wanted = preferredRow ?? Math.Min(OptionList.SelectedIndex, options.Count - 1);
        OptionList.SelectedIndex = wanted >= 0 ? wanted : options.Count > 0 ? 0 : -1;
    }

    static bool IsWeaponDisplaySlot(BuildTableSlot slot) => slot is
        BuildTableSlot.Barrel or BuildTableSlot.Magazine or BuildTableSlot.Optic
        or BuildTableSlot.Underbarrel or BuildTableSlot.Muzzle or BuildTableSlot.Stock
        or BuildTableSlot.SideRail or BuildTableSlot.Attachment;

    IReadOnlyList<GunsmithAvailabilityList> TryGetAttachmentWriteSupport(out string label, out string reason)
    {
        if (_table.Id == MediumVestConfigurationTableId)
        {
            label = "Add vest";
            reason = !_targetsResolved
                ? "Wait until the installed game records have finished loading."
                : "No installed vest with a complete configuration, gameplay and male/female model branch was verified.";
            return [];
        }

        label = "Add attachment";
        reason = "Adding assets is disabled until every registry and archive-copy target is selected without resource-name heuristics.";
        return [];
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

    void OptionPreview_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement
            {
                DataContext: BuildTableOptionItem
                {
                    PreviewModel: { } model,
                } option,
            })
            return;

        var preview = new BuildTableModelPreviewWindow(model, option.DisplayName,
            option.PreviewDetails)
        { Owner = this };
        preview.Show();
        e.Handled = true;
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
                if (option.IsSelectionValue)
                    option.ShowPreviewStatus("No standalone model", "This value is referenced by linked BuildTables and does not directly name a Mesh or LODSelector.");
                else if (!_targetsResolved && allCandidates.Any(candidate => candidate.ClassHash == 0))
                    option.ShowPreviewStatus("Resolving…", "Waiting for the referenced asset types.");
                else
                    option.ShowPreviewStatus("No 3D preview", "This BuildTable row has no reachable Mesh or LODSelector reference. Other linked asset types remain editable in technical view.");
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
                result.Option.ShowPreview(result.Preview, familyLargestDimension, usageByMesh[result.Preview.MeshName] > 1);
            }
            else if (result.Error is not null)
            {
                result.Option.ShowPreviewStatus("Preview failed", result.Error);
            }
            else
            {
                result.Option.ShowPreviewStatus("No preview", "No BuildTable model selector could be resolved to a readable Mesh resource.");
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
            // Closing the editor cancels pending preview work
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

    static Resource? FindReachableMesh(Resource? root, IReadOnlyDictionary<ulong, Resource> graph, CancellationToken token)
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

        if (root.ClassHash == ResourceTypes.Crc32("LODSelector"))
        {
            if (root.Id > 0 && graph.TryGetValue(root.Id - 1, out var lod1) && lod1.ClassHash == Mesh.ClassHash)
                return lod1;

            string prefix = root.Name + "_LOD";
            return graph.Values
                .Where(candidate => candidate.ClassHash == Mesh.ClassHash && candidate.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        return null;
    }

    async void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (FamilyList.SelectedItem is not BuildTableFamilyItem { IsOverview: false })
            return;
        if (_table.Id == MediumVestConfigurationTableId)
        {
            await AddVest();
            return;
        }
        IReadOnlyList<GunsmithAvailabilityList> gunsmithLists = TryGetAttachmentWriteSupport(out string addLabel, out string unsupportedReason);
        if (gunsmithLists.Count == 0)
        {
            MessageBox.Show(this, unsupportedReason, addLabel, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_targetsResolved || _armoryIndex is null)
        {
            MessageBox.Show(this, "Wait until the installed gameplay records and asset types have finished loading.", "Add attachment", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_armoryIndex.MatchesArchives(_archivePaths))
        {
            MessageBox.Show(this, "The game archives changed after this Armory editor was opened. Close this editor and open it again; the Toolkit will rebuild the Armory index automatically.", "Armory index changed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_saveAttachment is null || string.IsNullOrWhiteSpace(_localArchivePath) || _localEntryIndex < 0)
        {
            MessageBox.Show(this, "This BuildTable was opened without an archive target for new resources.", "Add attachment", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var templates = OptionList.Items.OfType<BuildTableOptionItem>()
            .Where(option => MetadataForTag(_table.Rows[option.RowIndex].Tag) is not null && !_addedMetadata.ContainsKey(_table.Rows[option.RowIndex].Tag))
            .Select(option => new AddAttachmentTemplate(option.RowIndex, option.DisplayName, option.InternalName))
            .ToList();
        if (templates.Count == 0)
        {
            MessageBox.Show(this, "No confirmed installed attachment can be used as a template.", "Add attachment", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dialog = new AddAttachmentWindow(SlotTitleText.Text, templates) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Draft is not { } draft)
            return;

        ArmoryIndex.IndexedResource? existingRecord = _armoryIndex.DatabaseResources
            .LastOrDefault(resource => string.Equals(resource.Name, draft.InternalName, StringComparison.OrdinalIgnoreCase));
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
            if (_addedMetadata.Values.Any(metadata => string.Equals(metadata.RecordName, draft.InternalName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A newly prepared attachment named {draft.InternalName} already exists.");
            if (draft.AddToGunsmith && _saveGunsmith is null)
                throw new InvalidOperationException("This editor has no Game Bootstrap write target.");
            var templateMetadata = MetadataForTag(_table.Rows[draft.Template.RowIndex].Tag)
                ?? throw new InvalidOperationException("The selected gameplay template is no longer resolved.");
            var modelSource = await ResolveAddModelSource(draft);
            if (_closed)
                return;

            SetMetadataLoading(true, $"Building {draft.DisplayName} and validating its game resources…");
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
                MessageBox.Show(this, attempt.Error.Message, "Could not add attachment", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            AttachmentAddPlan plan = attempt.Plan!;

            var localChanges = plan.LocalChanges.Append(new BuildTableResourceChange(_currentDocument.Source.ResourceIndex, _currentDocument.Name, plan.BuildTableData)).ToList();
            _saveAttachment(new AttachmentSaveChanges(draft.DisplayName, localChanges, plan.DatabaseChanges, plan.ResourceAdditions, plan.EntryAdditions));
            _armoryIndex.ApplyAdditions(plan.ResourceAdditions);
            if (plan.DatabaseChanges.Count > 0)
                _armoryIndex.ApplyChanges(plan.DatabaseChanges);

            ApplyPreparedLocalChanges(plan.LocalChanges);

            _addedMetadata[plan.Metadata.BuildTag] = plan.Metadata;
            _addedGunsmithVisibility[plan.Metadata.RecordId] = plan.AddedToGunsmith;
            _addedOwnerIdsByRecordId[plan.Metadata.RecordId] = plan.OwnerIds;
            _preparedIds.Add(plan.Metadata.NameStringId);
            foreach (ArmoryDatabaseResourceChange change in plan.DatabaseChanges)
                _preparedResourceData[(change.ArchivePath, change.EntryIndex, change.ResourceIndex)] = (byte[])change.Data.Clone();
            foreach (ArmoryArchiveResourceAddition addition in plan.ResourceAdditions)
                _preparedIds.Add(addition.ResourceId);
            foreach (ArmoryArchiveEntryAddition addition in plan.EntryAdditions)
                _preparedIds.Add(addition.EntryId);
            foreach (Resource resource in plan.PreviewResources)
                _localPreviewResources[resource.Id] = resource;
            Resource? selector = plan.PreviewResources.FirstOrDefault(resource => resource.Id == plan.ModelSelectorId);
            _targets[plan.ModelSelectorId] = new BuildTableTarget(plan.ModelSelectorId, selector?.Name ?? modelSource.Target.Name,
                selector?.ClassHash ?? modelSource.Target.ClassHash, ResourceTypes.NameOf(selector?.ClassHash ?? modelSource.Target.ClassHash),
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
            MessageBox.Show(this, ex.Message, "Add attachment", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetMetadataLoading(false);
        }
    }

    async Task AddVest()
    {
        if (!_targetsResolved || _armoryIndex is null)
        {
            MessageBox.Show(this, "Wait until the installed gameplay records and asset types have finished loading.",
                "Add vest", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_armoryIndex.MatchesArchives(_archivePaths))
        {
            MessageBox.Show(this, "The game archives changed after this editor was opened. Close and reopen the editor so its index can be rebuilt.",
                "Armory index changed", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_saveAttachment is null || string.IsNullOrWhiteSpace(_localArchivePath) || _localEntryIndex < 0)
        {
            MessageBox.Show(this, "This BuildTable was opened without an archive target for new character resources.",
                "Add vest", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        IReadOnlyList<VestTemplate> verified = CharacterItemAddPipeline.FindVestTemplates(
            _table, _localPreviewResources, MetadataForTag);
        var templates = verified.Select(template => new AddAttachmentTemplate(
            template.ConfigurationRowIndex, template.Metadata.DisplayName, template.Metadata.RecordName)).ToList();
        if (templates.Count == 0)
        {
            MessageBox.Show(this, "No installed vest has a complete and unambiguous configuration, gameplay and male/female model branch.",
                "Add vest", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dialog = new AddAttachmentWindow("Player clothing · vests", templates, characterVest: true) { Owner = this };
        if (dialog.ShowDialog() != true || dialog.Draft is not { } draft)
            return;

        try
        {
            if (_addedMetadata.Values.Any(metadata => string.Equals(metadata.RecordName, draft.InternalName, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException($"A newly prepared vest named {draft.InternalName} already exists.");

            SetMetadataLoading(true, $"Building {draft.DisplayName} and validating all character registrations…");
            IProgress<string> progress = new Progress<string>(message =>
            {
                if (_closed)
                    return;
                MetadataLoadingText.Text = message;
                SetStatus(message);
            });
            var attempt = await Task.Run(() =>
            {
                try
                {
                    return (Plan: CharacterItemAddPipeline.BuildVest(_armoryIndex, _table,
                        draft.Template.RowIndex, draft, _gameMetadata.LanguagePackage, _archivePaths,
                        _localPreviewResources, _localResourceIndexes, _localArchivePath,
                        _localEntryIndex, _localEntryName, _preparedResourceData, _preparedIds,
                        MetadataForTag, progress), Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Plan: (CharacterItemAddPlan?)null, Error: ex);
                }
            });
            if (_closed)
                return;
            if (attempt.Error is not null)
                throw new InvalidOperationException(attempt.Error.Message, attempt.Error);

            CharacterItemAddPlan plan = attempt.Plan!;
            var localChanges = plan.LocalChanges.Append(new BuildTableResourceChange(
                _currentDocument.Source.ResourceIndex, _currentDocument.Name, plan.BuildTableData)).ToList();
            _saveAttachment(new AttachmentSaveChanges(draft.DisplayName, localChanges,
                plan.DatabaseChanges, plan.ResourceAdditions, plan.EntryAdditions));
            _armoryIndex.ApplyAdditions(plan.ResourceAdditions);
            _armoryIndex.ApplyChanges(plan.DatabaseChanges);

            ApplyPreparedLocalChanges(plan.LocalChanges);
            _addedMetadata[plan.Metadata.BuildTag] = plan.Metadata;
            _preparedIds.Add(plan.Metadata.NameStringId);
            foreach (ArmoryDatabaseResourceChange change in plan.DatabaseChanges)
                _preparedResourceData[(change.ArchivePath, change.EntryIndex, change.ResourceIndex)] = (byte[])change.Data.Clone();
            foreach (ArmoryArchiveResourceAddition addition in plan.ResourceAdditions)
                _preparedIds.Add(addition.ResourceId);
            foreach (ArmoryArchiveEntryAddition addition in plan.EntryAdditions)
                _preparedIds.Add(addition.EntryId);
            foreach (Resource resource in plan.PreviewResources)
                _localPreviewResources[resource.Id] = resource;
            foreach ((ulong oldSelectorId, ulong newSelectorId) in plan.ModelSelectors)
            {
                Resource? selector = plan.PreviewResources.FirstOrDefault(resource => resource.Id == newSelectorId);
                BuildTableTarget source = _targets.GetValueOrDefault(oldSelectorId)
                    ?? new BuildTableTarget(oldSelectorId, $"0x{oldSelectorId:X12}", 0, "Asset", "", "");
                _targets[newSelectorId] = new BuildTableTarget(newSelectorId,
                    selector?.Name ?? draft.InternalName, selector?.ClassHash ?? source.ClassHash,
                    ResourceTypes.NameOf(selector?.ClassHash ?? source.ClassHash), _localEntryName,
                    Path.GetFileName(_localArchivePath));
            }

            _currentDocument.SavedData = (byte[])plan.BuildTableData.Clone();
            _currentDocument.Table = BuildTable.Read(plan.BuildTableData);
            _table = _currentDocument.Table;
            int newRowIndex = draft.Template.RowIndex + 1;
            RebuildRows(newRowIndex);
            RebuildOptions(preferredRow: newRowIndex);
            MarkDirty();
            ShowSelectedReference();
            ShowTargetResults();
            SetStatus($"{draft.DisplayName} is queued as a distinct vest with male and female models, "
                + $"{plan.ResourceAdditions.Count} new data resources and {plan.EntryAdditions.Count} new asset containers.");
        }
        catch (Exception ex)
        {
            SetError($"Could not add the vest: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Could not add vest", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetMetadataLoading(false);
        }
    }

    void ApplyPreparedLocalChanges(IReadOnlyList<BuildTableResourceChange> changes)
    {
        foreach (BuildTableResourceChange change in changes)
        {
            BuildTableDocument? document = _documents.FirstOrDefault(candidate =>
                candidate.Source.ResourceIndex == change.ResourceIndex);
            if (document is not null)
            {
                document.SavedData = (byte[])change.Data.Clone();
                document.Table = BuildTable.Read(change.Data);
            }
            ulong resourceId = _localResourceIndexes.FirstOrDefault(pair => pair.Value == change.ResourceIndex).Key;
            if (resourceId != 0 && _localPreviewResources.TryGetValue(resourceId, out Resource? localResource))
                localResource.Data = (byte[])change.Data.Clone();
        }
    }

    async Task<ResolvedAddModelSource> ResolveAddModelSource(AddAttachmentDraft draft)
    {
        var templateHandles = _table.Rows[draft.Template.RowIndex].References
            .Where(reference => reference.Value != 0 && (reference.Kind == BuildTableReferenceKind.Handle || reference.Kind == BuildTableReferenceKind.ObjectPointer && reference.ComponentIndex is not null))
            .ToList();
        BuildTableTarget? templateTarget = null;
        ulong templateSelectorId = 0;
        foreach (BuildTableReference reference in templateHandles)
        {
            BuildTableTarget? candidate = Resolve(reference.Value);
            if (candidate is null && _localPreviewResources.TryGetValue(reference.Value, out Resource? localCandidate))
                candidate = new BuildTableTarget(localCandidate.Id, localCandidate.Name, localCandidate.ClassHash, ResourceTypes.NameOf(localCandidate.ClassHash), _localEntryName, Path.GetFileName(_localArchivePath));
            if (candidate is null || candidate.ClassHash == 0)
                candidate = await Task.Run(() => BuildTableTargetResolver.ResolveOne(_archivePaths, reference.Value, _stopResolving.Token));
            if (candidate is not null && (candidate.ClassHash == Mesh.ClassHash || candidate.ClassHash == ResourceTypes.Crc32("LODSelector")))
            {
                templateTarget = candidate;
                templateSelectorId = reference.Value;
                break;
            }
        }
        if (templateTarget is null)
            throw new InvalidOperationException("The selected template has no model-selector handle that resolves to a Mesh or LODSelector.");

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
                exact = _targets.Values.Concat(_catalog).Where(candidate => string.Equals(candidate.Name, input, StringComparison.OrdinalIgnoreCase));
            var candidates = exact.DistinctBy(candidate => candidate.Id).ToList();
            if (candidates.Count == 0 && TryId(input, out id) && _localPreviewResources.TryGetValue(id, out Resource? local))
                candidates.Add(new BuildTableTarget(local.Id, local.Name, local.ClassHash, ResourceTypes.NameOf(local.ClassHash), _localEntryName, Path.GetFileName(_localArchivePath)));

            var resolvedCandidates = new List<BuildTableTarget>();
            foreach (BuildTableTarget candidate in candidates)
            {
                BuildTableTarget? resolvedCandidate = candidate.ClassHash == 0
                    ? await Task.Run(() => BuildTableTargetResolver.ResolveOne(_archivePaths, candidate.Id, _stopResolving.Token))
                    : candidate;
                if (resolvedCandidate is not null)
                    resolvedCandidates.Add(resolvedCandidate);
            }
            uint selectorHash = ResourceTypes.Crc32("LODSelector");
            target = resolvedCandidates
                .Where(candidate => candidate.ClassHash == selectorHash || candidate.ClassHash == Mesh.ClassHash)
                .OrderBy(candidate => candidate.ClassHash == selectorHash ? 0 : 1)
                .FirstOrDefault();
        }

        if (target is null || target.Id == 0 || target.ClassHash == 0)
            throw new InvalidOperationException("The model source could not be resolved to an exact installed resource. Use its exact asset name or hexadecimal ID.");
        if (target.ClassHash != Mesh.ClassHash && target.ClassHash != ResourceTypes.Crc32("LODSelector"))
            throw new InvalidOperationException($"{target.Name} is {ResourceTypes.NameOf(target.ClassHash)}, not a Mesh or LODSelector.");
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
        foreach (var reference in _table.References.Where(reference => reference.Kind != BuildTableReferenceKind.TableIdentity))
        {
            ulong? savedValue = saved.TryGetValue(reference.Path, out var values) && values.Count > 0
                ? values.Dequeue()
                : null;
            bool containerLocal = IsContainerLocalReference(reference);
            _rows.Add(new BuildTableReferenceRow(index++, reference, _table.Id, _name,
                _localEntryName, containerLocal, Resolve, savedValue));
        }

        ShowReferences();
        if (preferredBuildRow is int rowIndex)
        {
            var preferred = (ReferenceList.ItemsSource as IEnumerable<BuildTableReferenceRow>)?.FirstOrDefault(row => row.BuildRowIndex == rowIndex && row.Editable);
            if (preferred is not null)
                ReferenceList.SelectedItem = preferred;
        }
        if (ReferenceList.SelectedItem is null && ReferenceList.Items.Count > 0)
            ReferenceList.SelectedIndex = 0;
    }

    bool IsContainerLocalReference(BuildTableReference reference) => reference.IsContainerLocal
        || reference.Kind == BuildTableReferenceKind.Handle
        && _containerLocalReferenceIds.Contains(reference.Value);

    async void ResolveTargets(object sender, RoutedEventArgs e)
    {
        Loaded -= ResolveTargets;
        var wanted = _documents.SelectMany(document => document.Table.References)
            .Where(reference => reference.Kind != BuildTableReferenceKind.TableIdentity
                && !IsContainerLocalReference(reference))
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
        CancellationToken token = _stopResolving.Token;
        try
        {
            _ownerEntityIds = FindSameContainerEntityBuilderIds();
            var targetTask = RunIndexing(() => BuildTableTargetResolver.ResolveReferences(_archivePaths, _localTargets, wanted, progress, token), token);
            Task<IReadOnlyList<string>?> languageTask = _armoryIndex is null
                ? RunIndexing(() => BuildTableGameMetadataResolver.FindAvailableLanguagePackages(_archivePaths, token), token)
                : Task.FromResult<IReadOnlyList<string>?>(_armoryIndex.LanguagePackages);
            var metadataTask = RunIndexing(() => _armoryIndex is null
                ? BuildTableGameMetadataResolver.Build(_archivePaths, _ownerEntityIds, progress, token, buildTags: FamilyBuildTags())
                : BuildTableGameMetadataResolver.Build(_armoryIndex, _ownerEntityIds, cancellationToken: token, buildTags: FamilyBuildTags(), progress: progress), token);
            await Task.WhenAll(targetTask, metadataTask, languageTask);
            if (_closed || targetTask.Result is not { } result || metadataTask.Result is not { } metadata || languageTask.Result is not { } languages)
                return;

            _gameMetadata = metadata;
            _targetsResolved = true;
            SetAvailableLanguages(languages, _gameMetadata.LanguagePackage);

            foreach (var target in result.ById.Values)
                _targets[target.Id] = target;
            _catalog = result.Targets;

            RefreshResolvedNames();

            int resolved = wanted.Count(id => _targets.ContainsKey(id));
            int localReferences = _documents.SelectMany(document => document.Table.References)
                .Count(reference => reference.Value != 0 && IsContainerLocalReference(reference));
            string ownerStatus = !_weaponFamily
                ? $"Labels: {_gameMetadata.LanguagePackage}. Weapon availability is not applied to this BuildTable family."
                : _gameMetadata.OwnerRecords.Count == 0
                    ? "No exact same-container weapon owner was found; Gunsmith availability remains unavailable."
                    : $"Weapon owner: {string.Join(", ", _gameMetadata.OwnerRecords)} · labels: {_gameMetadata.LanguagePackage}.";
            SetStatus($"Resolved {resolved} of {wanted.Count} global assets, identified {localReferences} container-local references, and {_gameMetadata.ByBuildTag.Count} labels linked by the game database. {ownerStatus}");
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

    static Task<T?> RunIndexing<T>(Func<T> work, CancellationToken cancellationToken)
        where T : class => Task.Run(() =>
    {
        try
        {
            return work();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is the normal result of closing the editor. Handle it in
            // the worker so it cannot surface as a faulted fire-and-forget UI event.
            return null;
        }
    });

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
            ?? languages.FirstOrDefault(language => string.Equals(language.Package, "English(US)", StringComparison.OrdinalIgnoreCase))
            ?? languages[0];
        _settingLanguage = false;
        LanguageBox.IsEnabled = true;
    }

    async void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_settingLanguage || !IsLoaded || LanguageBox.SelectedItem is not BuildTableLanguageOption language
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
                ? BuildTableGameMetadataResolver.Build(_archivePaths, _ownerEntityIds, cancellationToken: token, preferredLanguagePackage: language.Package, buildTags: FamilyBuildTags())
                : BuildTableGameMetadataResolver.Build(_armoryIndex, _ownerEntityIds, preferredLanguagePackage: language.Package, cancellationToken: token, buildTags: FamilyBuildTags()), token);
            if (_closed || version != _languageVersion)
                return;

            _gameMetadata = metadata;
            RefreshResolvedNames();
            SetStatus($"Showing {_gameMetadata.ByBuildTag.Count} labels from the installed {DisplayLanguagePackage(_gameMetadata.LanguagePackage)} package.");
        }
        catch (OperationCanceledException)
        {
            // A newly selected language supersedes this background read
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
        string? rootName = _family.FirstOrDefault(item => item.IsOverview)?.Document.Name;
        const string suffix = "_Attachments";
        if (string.IsNullOrEmpty(rootName) || !rootName.EndsWith(suffix, StringComparison.Ordinal))
            return [];

        string entityBuilderName = rootName[..^suffix.Length];
        return _localTargets
            .Where(target => target.Type == "EntityBuilder" && string.Equals(target.Name, entityBuilderName, StringComparison.Ordinal))
            .Select(target => target.Id)
            .Where(id => id != 0)
            .Distinct()
            .ToList();
    }

    IReadOnlyList<uint> FamilyBuildTags() => _documents
        .SelectMany(document => document.Table.Rows)
        .SelectMany(row => row.PossibleTags.Append(row.Tag))
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

        var shown = _rows.Where(row => MatchesView(row, view) && (filter.Length == 0
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
        "Container-local references" => row.Category == "Container-local reference",
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
            ReferenceTitle.Text = option?.DisplayName ?? "Select a row";
            ReferenceMeta.Text = option?.InternalName ?? "";
            ReferenceMeta.ToolTip = null;
            ResolvedNameText.Text = "";
            ResolvedText.Text = "";
            ValueBox.Text = "";
            ValueBox.IsEnabled = false;
            TargetSearchBox.IsEnabled = false;
            ReplaceMatchingBox.IsEnabled = false;
            ApplyValueButton.IsEnabled = false;
            string reason = "Select a row first";
            bool canDuplicateOption = option is not null && _table.CanDuplicateRow(option.RowIndex, out reason);
            DuplicateRowButton.IsEnabled = canDuplicateOption;
            DuplicateRowButton.ToolTip = canDuplicateOption
                ? "Create another complete row with the same structure"
                : reason;
            RemoveRowButton.IsEnabled = TechnicalModeBox.IsChecked == true && option is not null && _table.RowCount > 1;
            ShowGunsmithLinks(option, option is { RowIndex: var emptyOptionRow } && (uint)emptyOptionRow < (uint)_table.Rows.Count
                ? MetadataForRow(_table.Rows[emptyOptionRow])
                : null);
            ClearFieldButton.Visibility = Visibility.Collapsed;
            TargetEmptyText.Text = option is null
                ? "Select a row first."
                : "This row has no resolved asset to replace. Its other fields remain available in Technical view.";
            TargetEmptyText.Visibility = Visibility.Visible;
            return;
        }

        var selectedOption = OptionList.SelectedItem as BuildTableOptionItem;
        ReferenceTitle.Text = TechnicalModeBox.IsChecked == true
            ? row.Path
            : selectedOption?.DisplayName ?? row.Target;
        BuildTableOptionMetadata? optionMetadata = selectedOption is { RowIndex: var optionRow } && (uint)optionRow < (uint)_table.Rows.Count
                ? MetadataForRow(_table.Rows[optionRow])
                : null;
        ShowGunsmithLinks(selectedOption, optionMetadata);
        ReferenceMeta.Text = TechnicalModeBox.IsChecked == true
            ? row.Category
            : optionMetadata is not null
                ? "Resolved game record"
                : "BuildTable row";
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
        string duplicateReason = "Select a row first";
        bool canDuplicate = row.BuildRowIndex is int buildRow && _table.CanDuplicateRow(buildRow, out duplicateReason);
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
            ? CurrentTableSupportsGunsmith()
                ? "Technical operation: remove this BuildTable row. This does not change the Gunsmith list."
                : "Technical operation: remove this exact BuildTable row. Referenced assets are not deleted."
            : "The last remaining row is kept so the table stays usable";
        ClearFieldButton.Visibility = technicalEdit && row.BuildRowIndex is null
            && row.Reference.Value != 0 && row.Reference.Kind == BuildTableReferenceKind.FileReference
                ? Visibility.Visible
                : Visibility.Collapsed;
        ReplaceMatchingHelp.Text = $"Leave this off to change only {row.Path}. Turn it on to update every field that currently uses {row.Target}.";
    }

    void ShowGunsmithLinks(BuildTableOptionItem? option, BuildTableOptionMetadata? metadata)
    {
        if (TechnicalModeBox.IsChecked == true || !CurrentTableSupportsGunsmith())
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
            GunsmithAvailability.Rewrite(lists, _gunsmithRemovals, _gunsmithAdditions, GunsmithRecordOrder());
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
            MessageBox.Show(this, ex.Message, restoring ? "Restore to Gunsmith" : "Hide from Gunsmith", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    IReadOnlyList<ulong> GunsmithRecordOrder() => _table.Rows
        .Select(row => MetadataForTag(row.Tag)?.RecordId ?? 0)
        .Where(id => id != 0)
        .Distinct()
        .ToList();

    BuildTableOptionMetadata? MetadataForTag(uint tag) => _addedMetadata.GetValueOrDefault(tag) ?? _gameMetadata.ByBuildTag.GetValueOrDefault(tag);

    BuildTableOptionMetadata? MetadataForRow(BuildTableRow row)
    {
        BuildTableOptionMetadata? direct = MetadataForTag(row.Tag);
        if (direct is not null)
            return direct;
        var linked = row.PossibleTags.Select(MetadataForTag).OfType<BuildTableOptionMetadata>()
            .DistinctBy(metadata => metadata.RecordId).ToList();
        return linked.Count == 1 ? linked[0] : null;
    }

    IReadOnlyList<GunsmithAvailabilityList> FindGunsmithLists() => GunsmithAvailability.Find(_armoryIndex, _table, _gameMetadata, _addedMetadata, _addedOwnerIdsByRecordId);

    bool CurrentTableSupportsGunsmith() => _weaponFamily
        && FamilyList.SelectedItem is BuildTableFamilyItem item
        && IsWeaponDisplaySlot(item.Slot);

    void SearchMode_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
        {
            _searchVersion++;
            SearchModeNote.Text = ShowAllAssetsBox.IsChecked == true
                ? "Unverified raw assets are visible. No result is claimed to be game-compatible."
                : "Disabled: no individual asset replacement is confirmed for this exact row.";
            TargetSearchBox.IsEnabled = ShowAllAssetsBox.IsChecked == true && TechnicalModeBox.IsChecked == true;
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
        if (_fullCatalogLoaded || ShowAllAssetsBox.IsChecked != true || TargetSearchBox.Text.Trim().Length < 2)
            return;

        SetStatus("Preparing the complete installed-asset search…");
        TargetEmptyText.Text = "Preparing the complete installed-asset search…";
        TargetEmptyText.Visibility = Visibility.Visible;
        var progress = new Progress<string>(message =>
        {
            if (!_closed)
            {
                SetStatus(message);
                TargetEmptyText.Text = message;
            }
        });

        try
        {
            _fullCatalogTask ??= RunIndexing(() => BuildTableTargetResolver.Build(_archivePaths, _localTargets, [], progress, _stopResolving.Token), _stopResolving.Token);
            BuildTableTargetCatalog? catalog = await _fullCatalogTask;
            if (_closed || catalog is null)
                return;

            _catalog = catalog.Targets;
            foreach (BuildTableTarget target in catalog.ById.Values)
                _targets.TryAdd(target.Id, target);
            _fullCatalogLoaded = true;
            SetStatus($"Installed-asset search ready: {_catalog.Count:N0} assets.");
            if (version == _searchVersion)
                ShowTargetResults();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _fullCatalogTask = null;
            SetError($"Could not prepare the installed-asset search: {exception.Message}");
        }
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

    void Target_Changed(object sender, SelectionChangedEventArgs e) => UseTargetButton.IsEnabled = ShowAllAssetsBox.IsChecked == true && TargetResults.SelectedItem is BuildTableCandidate && ReferenceList.SelectedItem is BuildTableReferenceRow { Editable: true };

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
                target = await Task.Run(() => BuildTableTargetResolver.ResolveOne(_archivePaths, target.Id, _stopResolving.Token)) ?? target;
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

    void ApplyValue_Click(object sender, RoutedEventArgs e) => ApplyValue(ReplaceMatchingBox.IsChecked == true);

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
            SetError("A BuildTable row cannot be emptied one field at a time. Remove the complete row so its references stay consistent.");
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
            SetStatus(CurrentTableSupportsGunsmith()
                ? "Duplicated a technical BuildTable row. This does not add a Gunsmith option."
                : "Duplicated the exact technical BuildTable row.");
        }
        catch (Exception ex)
        {
            SetError($"Could not duplicate the BuildTable row: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Duplicate BuildTable row", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void RemoveRow_Click(object sender, RoutedEventArgs e)
    {
        if (TechnicalModeBox.IsChecked != true)
            return;
        if (!TryGetSelectedBuildRowIndex(out int rowIndex) || _table.RowCount <= 1)
            return;

        string impact = CurrentTableSupportsGunsmith()
            ? "This changes only this table's mapping. It does not remove, hide or alter the corresponding Gunsmith option in Game Bootstrap Settings."
            : "This changes only this table's mapping. Referenced assets and other linked BuildTables are not deleted.";
        var answer = MessageBox.Show(this,
            $"Remove technical BuildTable row {rowIndex + 1}?\n\n{impact}",
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
            SetStatus(CurrentTableSupportsGunsmith()
                ? $"Removed technical BuildTable row {rowIndex + 1}. Gunsmith availability was not changed."
                : $"Removed technical BuildTable row {rowIndex + 1}. Referenced assets were not deleted.");
        }
        catch (Exception ex)
        {
            SetError($"Could not remove the BuildTable row safely: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Remove technical BuildTable row", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void ClearField_Click(object sender, RoutedEventArgs e)
    {
        if (ReferenceList.SelectedItem is not BuildTableReferenceRow row || row.BuildRowIndex is not null || !row.Editable || row.Reference.Kind != BuildTableReferenceKind.FileReference)
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
                    return new BuildTableResourceChange(document.Source.ResourceIndex, document.Name, data);
                })
                .ToList();
            bool gunsmithDirty = _gunsmithRemovals.Count > 0 || _gunsmithAdditions.Count > 0;
            var gunsmithChanges = !gunsmithDirty
                ? []
                : GunsmithAvailability.Rewrite(FindGunsmithLists(), _gunsmithRemovals, _gunsmithAdditions, GunsmithRecordOrder());
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
                    _armoryIndex.ApplyChanges(gunsmithChanges);
                    _gameMetadata = BuildTableGameMetadataResolver.Build(_armoryIndex, _ownerEntityIds,
                        preferredLanguagePackage: _gameMetadata.LanguagePackage, buildTags: FamilyBuildTags());
                }
            }
            foreach (var change in changed)
            {
                var document = _documents.First(document => document.Source.ResourceIndex == change.ResourceIndex && string.Equals(document.Name, change.Name, StringComparison.Ordinal));
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
                ? $"Saved {gunsmithChangeCount} confirmed Gunsmith change(s) to the change list. Apply changes, restart Wildlands, then reopen this BuildTable."
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
                throw new InvalidOperationException($"Reference {i} did not read back exactly at offset 0x{expected.Offset:X}.");
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
        _dirty = _documents.Any(document => document.Dirty) || _gunsmithRemovals.Count > 0 || _gunsmithAdditions.Count > 0;
        SaveButton.IsEnabled = _dirty;
        DiscardButton.IsEnabled = _dirty;
        FamilyList.Items.Refresh();
    }

    int? SelectedBuildRowIndex()
    {
        if (TechnicalModeBox.IsChecked == true)
            return (ReferenceList.SelectedItem as BuildTableReferenceRow)?.BuildRowIndex ?? (OptionList.SelectedItem as BuildTableOptionItem)?.RowIndex;

        return (OptionList.SelectedItem as BuildTableOptionItem)?.RowIndex ?? _selectedArmoryRow;
    }

    bool TryGetSelectedBuildRowIndex(out int rowIndex)
    {
        rowIndex = SelectedBuildRowIndex() ?? -1;

        if ((uint)rowIndex < (uint)_table.Rows.Count)
            return true;

        RebuildRows();
        RebuildOptions();
        ShowSelectedReference();
        ShowTargetResults();
        SetError("The selected row changed while the table was refreshed. Choose the row again before editing it.");

        return false;
    }

    void ClearSelectionForTableMutation()
    {
        _changingSelection = true;
        try
        {
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
        return text.Length > 0 && ulong.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
    }

    static bool MatchesSearch(BuildTableTarget target, string query)
    {
        string haystack = $"{target.Name} {target.Type} {target.Container} {target.Archive} 0x{target.Id:X}"
            .Replace('_', ' ').Replace('-', ' ');
        string[] terms = query.Replace('_', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length > 0 && terms.All(term => haystack.Contains(term, StringComparison.OrdinalIgnoreCase));
    }

    void SetError(string text)
    {
        StatusBar.Visibility = Visibility.Visible;
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource("Warning");
    }

    void SetStatus(string _)
    {
        StatusBar.Visibility = Visibility.Collapsed;
        StatusText.Text = "";
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
    readonly string _containerName;
    readonly bool _containerLocal;
    readonly Func<ulong, BuildTableTarget?> _resolve;
    ulong _savedValue;
    bool _hasSavedValue;

    public BuildTableReferenceRow(int index, BuildTableReference reference, ulong tableId,
        string tableName, string containerName, bool containerLocal,
        Func<ulong, BuildTableTarget?> resolve, ulong? savedValue = null)
    {
        Index = index;
        Reference = reference;
        _tableId = tableId;
        _tableName = tableName;
        _containerName = containerName;
        _containerLocal = containerLocal;
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
    public bool Editable => !Internal && !_containerLocal;
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
            if (_containerLocal)
                return "Container-local reference";

            string type = Resolved?.Type ?? "";
            if (type == "LODSelector" || Reference.Kind == BuildTableReferenceKind.Handle)
                return "Variant choice";
            if (type == "BuildTable" || Reference.Kind is BuildTableReferenceKind.Table or BuildTableReferenceKind.ObjectPointer)
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
        _ when _containerLocal => $"{LocalOwner} local reference · 0x{Reference.Value:X}",
        _ when Resolved is { } target => target.Name,
        _ when Reference.Kind == BuildTableReferenceKind.Handle => $"Unresolved handle · 0x{Reference.Value:X}",
        _ => $"Unresolved asset · 0x{Reference.Value:X}",
    };

    public string TargetDetails => Reference.Value switch
    {
        0 => "No asset is assigned to this field.",
        _ when Internal => "Internal owner link. It is kept read-only because changing it would detach the row or column from this BuildTable.",
        _ when Reference.IsContainerLocal => $"Container-local reference owned by {LocalOwner} (tag 0x{Reference.ReferenceTag:X2}, global flag 0x{Reference.GlobalFlag:X2}). It is not an independently indexed Forge asset.",
        _ when _containerLocal => $"Handle scoped to {LocalOwner} because the same ID is explicitly marked container-local elsewhere in this BuildTable family (tag 0x{Reference.ReferenceTag:X2}).",
        _ when Resolved is { } target => target.Details,
        _ when Reference.Kind == BuildTableReferenceKind.Handle => "This handle did not resolve to an indexed resource or Forge entry. It may address an object owned by the surrounding container.",
        _ => "This ID was not found as a resource or forge entry in the installed archives.",
    };

    public string PartLabel => Reference.Kind switch
    {
        _ when _containerLocal => "Container-local reference",
        BuildTableReferenceKind.FileReference when Resolved?.Type == "BuildTable" => "Linked BuildTable",
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
                        "Variant choice" => $"Row {shown} · variant choice",
                        "Built asset" => $"Row {shown} · visible asset",
                        "Related group" => $"Row {shown} · related asset group",
                        "Container-local reference" => $"Row {shown} · container-local reference",
                        "Empty optional field" => $"Row {shown} · optional asset",
                        _ => $"Row {shown} · asset",
                    };
                if (path.EndsWith(" table", StringComparison.Ordinal))
                    return $"Row {shown} · owning asset group";
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

    static int ParseLast(string path) => int.TryParse(path.Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1], out int value)
            ? value
            : 0;

    string LocalOwner => _containerName.Length > 0 ? _containerName : _tableName;

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
