using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Media3D;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public sealed record BuildTableResourceSource(
    int ResourceIndex,
    ulong Id,
    string Name,
    byte[] Data);

public sealed record BuildTableResourceChange(
    int ResourceIndex,
    string Name,
    byte[] Data);

sealed class BuildTableDocument
{
    public BuildTableDocument(BuildTableResourceSource source)
    {
        Source = source;
        SavedData = (byte[])source.Data.Clone();
        Table = BuildTable.Read(source.Data);
    }

    public BuildTableResourceSource Source { get; }
    public string Name => Source.Name;
    public ulong Id => Source.Id;
    public byte[] SavedData { get; set; }
    public BuildTableAsset Table { get; set; }
    public bool Dirty => !SavedData.AsSpan().SequenceEqual(Table.Write());
}

sealed class BuildTableFamilyItem
{
    public required BuildTableDocument Document { get; init; }
    public required BuildTableSlot Slot { get; init; }
    public required BuildTableGroup Group { get; init; }
    public required bool IsOverview { get; init; }
    public required int Order { get; init; }

    public bool IsSelectionValue => BuildTableFamily.IsSelectionValue(Document.Table);
    public string Title => Document.Name;
    public string GroupTitle => BuildTableNames.GroupTitle(Group);
    public string Subtitle => IsOverview
        ? "References the linked BuildTables"
        : IsSelectionValue ? "Selection value from the game file" : $"{Document.Table.RowCount} rows";
    public string InternalName => Document.Name;
    public string SearchText => $"{Title} {GroupTitle} {InternalName}";
}

enum BuildTableGroup
{
    ModelMappings,
    BuildTableLinks,
    ResourceMappings,
    SelectionValues,
    TagOnly,
}

sealed class BuildTableOptionItem : INotifyPropertyChanged
{
    Model3D? _previewModel;
    string _previewStatus = "Loading preview…";
    string _previewDetails = "Resolving preview-capable assets referenced by this BuildTable row.";
    string _previewBadge = "";

    public required int RowIndex { get; init; }
    public required IReadOnlyList<BuildTableReferenceRow> Parts { get; init; }
    public required BuildTableReferenceRow? PrimaryPart { get; init; }
    public required string RowLabel { get; init; }
    public required string DisplayName { get; init; }
    public required string InternalName { get; init; }
    public bool IsSelectionValue { get; init; }
    public bool IsHiddenFromGunsmith { get; init; }
    public string GunsmithBadge { get; init; } = "";

    public Visibility GunsmithBadgeVisibility => GunsmithBadge.Length > 0
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Model3D? PreviewModel => _previewModel;
    public string PreviewStatus => _previewStatus;
    public string PreviewDetails => _previewDetails;
    public string PreviewBadge => _previewBadge;
    public Visibility PreviewBadgeVisibility => _previewBadge.Length > 0
        ? Visibility.Visible
        : Visibility.Collapsed;

    public void ShowPreview(BuildTableMeshPreview preview, double familyLargestDimension, bool sharedBySeveralOptions)
    {
        _previewModel = preview.AtFamilyScale(familyLargestDimension);
        _previewStatus = "";
        _previewDetails = $"3D preview: {preview.MeshName} · {preview.Triangles:N0} triangles"
            + (sharedBySeveralOptions ? " · the same preview mesh is referenced by multiple rows" : "");
        _previewBadge = sharedBySeveralOptions ? "SAME PREVIEW MESH" : "";
        Changed(nameof(PreviewModel));
        Changed(nameof(PreviewStatus));
        Changed(nameof(PreviewDetails));
        Changed(nameof(PreviewBadge));
        Changed(nameof(PreviewBadgeVisibility));
    }

    public void ShowPreviewStatus(string status, string details)
    {
        _previewModel = null;
        _previewStatus = status;
        _previewDetails = details;
        _previewBadge = "";
        Changed(nameof(PreviewModel));
        Changed(nameof(PreviewStatus));
        Changed(nameof(PreviewDetails));
        Changed(nameof(PreviewBadge));
        Changed(nameof(PreviewBadgeVisibility));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

static class BuildTableFamily
{
    static readonly Dictionary<BuildTableGroup, int> GroupOrder = new()
    {
        [BuildTableGroup.ModelMappings] = 10,
        [BuildTableGroup.BuildTableLinks] = 20,
        [BuildTableGroup.ResourceMappings] = 30,
        [BuildTableGroup.SelectionValues] = 40,
        [BuildTableGroup.TagOnly] = 50,
    };

    public static IReadOnlyList<BuildTableFamilyItem> Find(IReadOnlyList<BuildTableDocument> documents,
        BuildTableDocument opened, IReadOnlyList<BuildTableTarget> localTargets)
    {
        var byId = documents.ToDictionary(document => document.Id);
        var tableIds = byId.Keys.ToHashSet();
        var targetTypes = localTargets
            .Where(target => target.Id != 0)
            .GroupBy(target => target.Id)
            .ToDictionary(group => group.Key, group => group.First().Type);
        var links = documents.ToDictionary(document => document.Id, _ => new HashSet<ulong>());

        foreach (var document in documents)
        {
            foreach (ulong target in document.Table.References.Select(reference => reference.Value))
            {
                if (!byId.ContainsKey(target) || target == document.Id)
                    continue;
                links[document.Id].Add(target);
                links[target].Add(document.Id);
            }
        }

        var connected = new HashSet<ulong> { opened.Id };
        var pending = new Queue<ulong>();
        pending.Enqueue(opened.Id);
        while (pending.TryDequeue(out ulong id))
            foreach (ulong linked in links[id])
                if (connected.Add(linked))
                    pending.Enqueue(linked);

        var component = documents.Where(document => connected.Contains(document.Id)).ToList();
        BuildTableDocument overview = component
            .OrderByDescending(document => component.Count(other => other.Table.References
                .Any(reference => reference.Value == document.Id)) == 0)
            .ThenByDescending(document => document.Table.References
                .Select(reference => reference.Value).Distinct().Count(connected.Contains))
            .ThenBy(document => document.Name.Length)
            .FirstOrDefault() ?? opened;

        return component
            .Select(document =>
            {
                var slot = BuildTableCompatibility.GuessSlotForDisplay(document.Name);
                bool isOverview = document == overview && component.Count > 1;
                BuildTableGroup group = Classify(document.Table, tableIds, targetTypes);
                return new BuildTableFamilyItem
                {
                    Document = document,
                    Slot = slot,
                    Group = group,
                    IsOverview = isOverview,
                    Order = GroupOrder[group],
                };
            })
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static BuildTableGroup Classify(BuildTableAsset table, IReadOnlySet<ulong> tableIds,
        IReadOnlyDictionary<ulong, string> targetTypes)
    {
        if (IsSelectionValue(table))
            return BuildTableGroup.SelectionValues;

        var targets = table.References
            .Where(reference => reference.Kind != BuildTableReferenceKind.TableIdentity
                && reference.Value != 0 && reference.Value != table.Id)
            .Select(reference => reference.Value)
            .Distinct()
            .ToList();
        if (targets.Any(id => targetTypes.GetValueOrDefault(id) is "Mesh" or "LODSelector"))
            return BuildTableGroup.ModelMappings;
        if (targets.Any(tableIds.Contains))
            return BuildTableGroup.BuildTableLinks;
        return targets.Count > 0 ? BuildTableGroup.ResourceMappings : BuildTableGroup.TagOnly;
    }

    public static bool IsSelectionValue(BuildTableAsset table) => table.RowCount == 1
        && table.References
            .Where(reference => reference.Kind != BuildTableReferenceKind.TableIdentity)
            .All(reference => reference.Value == 0 || reference.Value == table.Id);
}

static class BuildTableNames
{
    internal static string GroupTitle(BuildTableGroup group) => group switch
    {
        BuildTableGroup.ModelMappings => "3D asset mappings",
        BuildTableGroup.BuildTableLinks => "BuildTable links",
        BuildTableGroup.ResourceMappings => "Resource mappings",
        BuildTableGroup.SelectionValues => "Selection values",
        BuildTableGroup.TagOnly => "Tag-only tables",
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    internal static int GroupCount(IEnumerable<BuildTableFamilyItem> items) => items
        .Select(item => item.GroupTitle)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

}
