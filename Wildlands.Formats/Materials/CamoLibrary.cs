using System;
using System.Collections.Generic;
using System.Linq;
using Wildlands.Formats.Forge;

namespace Wildlands.Formats.Materials;

public sealed record CamoEntry(string Name, string Display, ulong Id, string ArchivePath);

public static class CamoLibrary
{
    public static List<CamoEntry> Read(IEnumerable<ForgeArchive> archives)
    {
        var found = new Dictionary<string, CamoEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var archive in archives)
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FileExtension != ".data")
                    continue;

                string name = entry.Name;
                if (!IsPattern(name))
                    continue;

                found.TryAdd(name, new CamoEntry(name, Display(name), entry.Id, archive.FilePath));
            }
        }

        return found.Values.OrderBy(x => x.Display, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool IsPattern(string name)
    {
        if (name.Contains("_Mip", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Desc", StringComparison.OrdinalIgnoreCase)
            || name.Contains("-icon", StringComparison.OrdinalIgnoreCase))
            return false;

        if (name.StartsWith("Camo_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Pattern_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("W_CAMO_", StringComparison.OrdinalIgnoreCase))
            return name.Contains("Map", StringComparison.OrdinalIgnoreCase);

        return char.IsDigit(name[0]) && name.Contains('-') && name.EndsWith("_Map", StringComparison.OrdinalIgnoreCase);
    }

    static string Display(string name)
    {
        string text = name;

        foreach (string tail in new[] { "_DiffuseMap_PC", "_DiffuseMap", "_Map_PC", "_Map" })
        {
            if (text.EndsWith(tail, StringComparison.OrdinalIgnoreCase))
            {
                text = text[..^tail.Length];
                break;
            }
        }

        foreach (string head in new[] { "Camo_", "Pattern_", "W_CAMO_" })
        {
            if (text.StartsWith(head, StringComparison.OrdinalIgnoreCase))
            {
                text = text[head.Length..];
                break;
            }
        }

        return text.Replace('_', ' ');
    }
}
