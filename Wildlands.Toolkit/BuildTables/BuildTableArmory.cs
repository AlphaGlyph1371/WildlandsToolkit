using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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
    public required bool IsOverview { get; init; }
    public required int Order { get; init; }

    public string Title => Document.Name;
    public string Subtitle => IsOverview
        ? "References the linked BuildTables"
        : $"{Document.Table.RowCount} rows from the game file";
    public string InternalName => Document.Name;
}

sealed class BuildTableOptionItem : INotifyPropertyChanged
{
    Model3D? _previewModel;
    string _previewStatus = "Loading model…";
    string _previewDetails = "Resolving the mesh referenced by this BuildTable row.";
    string _previewBadge = "";

    public required int RowIndex { get; init; }
    public required IReadOnlyList<BuildTableReferenceRow> Parts { get; init; }
    public required BuildTableReferenceRow? PrimaryPart { get; init; }
    public required string DisplayName { get; init; }
    public required string InternalName { get; init; }
    public bool IsHiddenFromGunsmith { get; init; }
    public string GunsmithBadge { get; init; } = "";

    public string Number => $"OPTION {RowIndex + 1}";
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

    public void ShowPreview(BuildTableMeshPreview preview, double familyLargestDimension,
        bool sharedBySeveralOptions)
    {
        _previewModel = preview.AtFamilyScale(familyLargestDimension);
        _previewStatus = "";
        _previewDetails = $"BuildTable mesh: {preview.MeshName} · {preview.Triangles:N0} triangles";
        _previewBadge = sharedBySeveralOptions ? "SHARED BUILDTABLE MESH" : "";
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
    static readonly Dictionary<BuildTableSlot, int> SlotOrder = new()
    {
        [BuildTableSlot.Optic] = 10,
        [BuildTableSlot.Barrel] = 20,
        [BuildTableSlot.Muzzle] = 30,
        [BuildTableSlot.Magazine] = 40,
        [BuildTableSlot.Underbarrel] = 50,
        [BuildTableSlot.SideRail] = 60,
        [BuildTableSlot.Stock] = 70,
        [BuildTableSlot.Headgear] = 110,
        [BuildTableSlot.Facewear] = 120,
        [BuildTableSlot.Hair] = 130,
        [BuildTableSlot.Beard] = 140,
        [BuildTableSlot.Top] = 150,
        [BuildTableSlot.Vest] = 160,
        [BuildTableSlot.Backpack] = 170,
        [BuildTableSlot.Gloves] = 180,
        [BuildTableSlot.Pants] = 190,
        [BuildTableSlot.Footwear] = 200,
        [BuildTableSlot.Holster] = 210,
        [BuildTableSlot.Character] = 220,
    };

    public static IReadOnlyList<BuildTableFamilyItem> Find(
        IReadOnlyList<BuildTableDocument> documents, BuildTableDocument opened)
    {
        var byId = documents.ToDictionary(document => document.Id);
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
                var slot = BuildTableCompatibility.DetectSlot(document.Name);
                bool isOverview = document == overview && component.Count > 1;
                int order = isOverview ? 0 : SlotOrder.GetValueOrDefault(slot, 900);
                return new BuildTableFamilyItem
                {
                    Document = document,
                    Slot = slot,
                    IsOverview = isOverview,
                    Order = order,
                };
            })
            .OrderBy(item => item.Order)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

public static class BuildTableNames
{
    public static string FamilyTitle(string name) => name;
    public static string FriendlyAsset(string name, string familyName) => name;
    public static string Friendly(string value) => value;

    public static string SlotTitle(BuildTableSlot slot) => slot switch
    {
        BuildTableSlot.Barrel => "Barrels",
        BuildTableSlot.Magazine => "Magazines",
        BuildTableSlot.Optic => "Sights & scopes",
        BuildTableSlot.Underbarrel => "Underbarrel",
        BuildTableSlot.Muzzle => "Muzzle",
        BuildTableSlot.Stock => "Stocks",
        BuildTableSlot.SideRail => "Side rail",
        BuildTableSlot.Attachment => "Attachments",
        BuildTableSlot.Headgear => "Headgear",
        BuildTableSlot.Vest => "Vests",
        BuildTableSlot.Backpack => "Backpacks",
        BuildTableSlot.Pants => "Pants",
        BuildTableSlot.Top => "Tops",
        BuildTableSlot.Gloves => "Gloves",
        BuildTableSlot.Footwear => "Footwear",
        BuildTableSlot.Facewear => "Facewear",
        BuildTableSlot.Hair => "Hair",
        BuildTableSlot.Beard => "Facial hair",
        BuildTableSlot.Holster => "Holsters",
        BuildTableSlot.Character => "Character",
        _ => "Options",
    };

    public static string TableTitle(string tableName, BuildTableSlot slot) => tableName;

    public static string OptionCount(int count) => count == 1 ? "option" : "options";

    public static string EmptyOption(BuildTableSlot slot) => "No asset assigned";
}
