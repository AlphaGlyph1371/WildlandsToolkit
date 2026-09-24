using System;
using System.Collections.Generic;
using System.Linq;
using Wildlands.Formats.Data;
using Wildlands.Formats.Models;

namespace Wildlands.Formats.Materials;

public sealed record ClothStyleValue(uint Property, BuildTableValue Value, BuildTableMaterialTarget Target);

public sealed class ClothStyle
{
    public uint Tag { get; init; }
    public float Weight { get; init; }
    public string Name { get; init; } = "";
    public string Group { get; init; } = "";
    public List<ClothStyleValue> Values { get; } = [];
}

public sealed class ClothStyleIndex
{
    public static readonly string[] Containers =
    [
        "GR_PLAYER_Template",
        "REAL_ICONIC_Template",
        "GR_Avatar_Template",
        "GR_TEAMMATE_Template",
        "GR_DLC_TEAMMATE_Template",
        "Game Bootstrap Settings",
    ];

    readonly List<BuildTableAsset> _tables = [];
    readonly Dictionary<ulong, BuildTableAsset> _byId = [];
    readonly Dictionary<ulong, string> _names = [];
    readonly Dictionary<uint, (ulong Id, string Name)> _options = [];

    public int TableCount => _tables.Count;

    public void Add(Resource resource)
    {
        if (resource.ClassHash == CustomizationOption.ClassHash)
        {
            if (CustomizationOption.TagOf(resource.Data) is uint tag
                && (!_options.TryGetValue(tag, out var known) || resource.Id < known.Id))
                _options[tag] = (resource.Id, resource.Name);
            return;
        }

        if (resource.ClassHash != BuildTable.ClassHash || _byId.ContainsKey(resource.Id))
            return;

        BuildTableAsset table;
        try
        {
            table = BuildTable.Read(resource.Data);
        }
        catch (Exception)
        {
            return;
        }

        _tables.Add(table);
        _byId[table.Id] = table;
        _names[table.Id] = resource.Name;
    }

    public List<ClothStyle> ForModel(IReadOnlyCollection<ulong> modelIds, IReadOnlyList<Material> materials)
    {
        var search = new Search(this, materials);
        var wanted = modelIds.ToHashSet();

        for (int depth = 0; depth < 4 && wanted.Count > 0 && !search.Styles.Any(style => style.Tag != 0); depth++)
        {
            var owners = new HashSet<ulong>();

            foreach (var table in _tables)
            {
                foreach (var row in table.Rows)
                {
                    if (!row.Values.Values.Any(value => wanted.Contains(value.Id)))
                        continue;

                    owners.Add(table.Id);
                    search.AddStyles(table, row);
                }
            }

            wanted = owners;
        }

        return search.Styles;
    }

    sealed class Search(ClothStyleIndex index, IReadOnlyList<Material> materials)
    {
        readonly HashSet<(ulong, uint)> _seen = [];

        public List<ClothStyle> Styles { get; } = [];

        public void AddStyles(BuildTableAsset owner, BuildTableRow row)
        {
            var referenced = row.Values.Values
                .Select(value => index._byId.GetValueOrDefault(value.Id))
                .OfType<BuildTableAsset>()
                .Where(table => table != owner && Reaches(table))
                .Distinct()
                .ToList();

            var fixedTables = referenced.Where(table => table.Rows.Count == 1).ToList();
            var choices = referenced.Where(table => table.Rows.Count > 1 && IsChoice(table)).ToList();

            foreach (var choice in choices)
            {
                foreach (var choiceRow in choice.Rows)
                {
                    if (!_seen.Add((choice.Id, choiceRow.Tag)))
                        continue;

                    var style = new ClothStyle
                    {
                        Tag = choiceRow.Tag,
                        Weight = choiceRow.Weight,
                        Name = OptionOf(choiceRow) ?? $"0x{choiceRow.Tag:X8}",
                        Group = index._names.GetValueOrDefault(choice.Id) ?? "",
                    };

                    if (Reaches(owner))
                        Collect(owner, row, style, 0);
                    foreach (var fixedTable in fixedTables)
                        Collect(fixedTable, fixedTable.Rows[0], style, 0);
                    Collect(choice, choiceRow, style, 0);
                    Styles.Add(style);
                }
            }

            if (choices.Count > 0 || !Reaches(owner) || !_seen.Add((owner.Id, row.Tag)))
                return;

            var only = new ClothStyle
            {
                Weight = row.Weight,
                Name = index._names.GetValueOrDefault(owner.Id) ?? $"0x{owner.Id:X}",
                Group = index._names.GetValueOrDefault(owner.Id) ?? "",
            };
            Collect(owner, row, only, 0);
            foreach (var fixedTable in fixedTables)
                Collect(fixedTable, fixedTable.Rows[0], only, 0);
            Styles.Add(only);
        }

        bool Reaches(BuildTableAsset table) => table.Columns.Any(column => column.Property != 0 && column.Target is { } target && materials.Any(target.AppliesTo));

        bool IsChoice(BuildTableAsset table) => table.Rows.Count(row => OptionOf(row) is not null) * 2 >= table.Rows.Count;

        string? OptionOf(BuildTableRow row)
        {
            if (index._options.TryGetValue(row.Tag, out var option))
                return option.Name;

            foreach (uint tag in row.PossibleTags)
            {
                if (index._options.TryGetValue(tag, out option))
                    return option.Name;
            }

            return null;
        }

        void Collect(BuildTableAsset table, BuildTableRow row, ClothStyle style, int depth)
        {
            foreach (var column in table.Columns)
            {
                if (!row.Values.TryGetValue(column.Index, out var value))
                    continue;

                if (column.Property != 0)
                {
                    if (column.Target is { } target && materials.Any(target.AppliesTo))
                        style.Values.Add(new ClothStyleValue(column.Property, value, target));
                    continue;
                }

                if (depth < 3 && index._byId.TryGetValue(value.Id, out var sub) && sub.Rows.Count > 0 && Reaches(sub))
                {
                    var subRow = sub.Rows.FirstOrDefault(candidate => candidate.Tag == row.Tag) ?? sub.Rows.MaxBy(candidate => candidate.Weight)!;
                    Collect(sub, subRow, style, depth + 1);
                }
            }
        }
    }
}
