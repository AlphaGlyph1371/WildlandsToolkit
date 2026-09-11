using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Wildlands.Formats.Models;
using Wildlands.Formats.Materials;
using Wildlands.Formats.Textures;
using Wildlands.Formats.Weather;
using Wildlands.Toolkit;

static partial class Commands
{
    internal static bool TryReadDataFile(ForgeArchive archive, ForgeEntry entry, out DataFile file)
    {
        if (ForgeDataFileReader.TryRead(archive, entry, out file, out string error))
            return true;
        Console.Error.WriteLine($"Could not read {Path.GetFileName(archive.FilePath)}/{entry.Name}{entry.FileExtension}: {error}");
        return false;
    }

    internal static Resource RequireNamedResource(DataFile file, string name)
    {
        List<Resource> matches = file.Resources
            .Where(resource => resource.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException("No resource named " + name + "."),
            _ => throw new InvalidOperationException($"Resource name {name} is ambiguous ({matches.Count} matches)."),
        };
    }

    internal static ulong ParseResourceId(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? Convert.ToUInt64(text[2..], 16)
        : Convert.ToUInt64(text);

    internal static int ParseFlexibleInt32(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.Parse(text.AsSpan(2), System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture);
        return int.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }
}
