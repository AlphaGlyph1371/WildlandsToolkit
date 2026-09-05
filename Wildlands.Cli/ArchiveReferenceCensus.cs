using System.Globalization;
using System.IO;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Toolkit;

internal static class ArchiveReferenceCensus
{
    public static int Run(string gameFolder, IReadOnlyList<string> valueTexts)
    {
        ulong[] values;
        try
        {
            values = valueTexts.Select(Parse).Distinct().ToArray();
        }
        catch (Exception exception)
        {
            Console.WriteLine(exception.Message);
            return 1;
        }

        IReadOnlyList<string> archives = ArchiveLocator.Find(gameFolder);
        if (archives.Count == 0)
        {
            Console.WriteLine("no forge archives in " + gameFolder);
            return 1;
        }

        var hits = values.ToDictionary(value => value, _ => new List<ReferenceHit>());
        long containers = 0;
        long unreadable = 0;
        long unparsed = 0;
        for (int archiveIndex = 0; archiveIndex < archives.Count; archiveIndex++)
        {
            string path = archives[archiveIndex];
            Console.WriteLine($"[{archiveIndex + 1}/{archives.Count}] {Path.GetFileName(path)}");
            try
            {
                using var archive = ForgeArchive.Open(path);
                int inArchive = 0;
                foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"))
                {
                    containers++;
                    inArchive++;
                    if (inArchive % 1000 == 0)
                        Console.WriteLine($"  {inArchive:N0} data containers");

                    byte[] entryData;
                    try
                    {
                        entryData = archive.ReadEntry(entry);
                    }
                    catch
                    {
                        unreadable++;
                        continue;
                    }

                    DataFile file;
                    try
                    {
                        using var stream = new MemoryStream(entryData);
                        file = DataFile.Read(stream);
                    }
                    catch
                    {
                        unparsed++;
                        foreach (ulong value in values)
                            AddOccurrences(entryData, value, hits[value], Path.GetFileName(path),
                                entry.Name, "<raw entry>", entry.Extension, entry.Id);
                        continue;
                    }

                    foreach (Resource resource in file.Resources)
                    {
                        foreach (ulong value in values)
                        {
                            AddOccurrences(resource.Data, value, hits[value], Path.GetFileName(path),
                                entry.Name, resource.Name, resource.ClassHash, resource.Id);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine("  could not scan: " + exception.Message);
            }
        }

        Console.WriteLine();
        foreach (ulong value in values)
        {
            Console.WriteLine($"0x{value:X12}: {hits[value].Count:N0} occurrence(s)");
            foreach (ReferenceHit hit in hits[value])
                Console.WriteLine($"  {hit.Archive} | {hit.Container} | "
                    + $"{ResourceTypes.NameOf(hit.ClassHash)} {hit.Resource} "
                    + $"(0x{hit.ResourceId:X12}) @ 0x{hit.Offset:X}");
        }
        Console.WriteLine($"scanned {containers:N0} data containers in {archives.Count} archives; "
            + $"{unreadable:N0} unreadable, {unparsed:N0} scanned as raw payloads");
        return 0;
    }

    static void AddOccurrences(ReadOnlySpan<byte> data, ulong value, List<ReferenceHit> hits,
        string archive, string container, string resource, uint classHash, ulong resourceId)
    {
        byte[] needle = BitConverter.GetBytes(value);
        int start = 0;
        while (start <= data.Length - needle.Length)
        {
            int relative = data[start..].IndexOf(needle);
            if (relative < 0)
                break;
            int offset = start + relative;
            hits.Add(new ReferenceHit(archive, container, resource, classHash, resourceId, offset));
            start = offset + 1;
        }
    }

    static ulong Parse(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        ? ulong.Parse(text[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture)
        : ulong.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);

    sealed record ReferenceHit(string Archive, string Container, string Resource,
        uint ClassHash, ulong ResourceId, int Offset);
}
