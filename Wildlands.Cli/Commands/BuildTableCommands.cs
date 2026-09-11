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
    internal static Dictionary<ulong, (string Name, HashSet<uint> Tags)> ReadOptionTags(
        ForgeArchive archive, ForgeEntry entry)
    {
        var tables = new Dictionary<ulong, (string, HashSet<uint>)>();
        DataFile file;
        try
        {
            using var stream = new MemoryStream(archive.ReadEntry(entry));
            file = DataFile.Read(stream);
        }
        catch { return tables; }
    
        foreach (var resource in file.Resources)
        {
            if (resource.ClassHash != BuildTable.ClassHash)
                continue;
            try
            {
                var table = BuildTable.Read(resource.Data);
                tables[resource.Id] = (resource.Name, table.Rows.Select(row => row.Tag).ToHashSet());
            }
            catch { }
        }
        return tables;
    }
    
    // Writes a data file back out and reads the result again, so that every resource
    // can be held against the one it came from. The bytes on disk differ because our
    // packer is not the game's, only the content has to survive.

    internal static int CheckBuildTableRoundTrips(string path)
    {
        var paths = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.data") : [path];
        int seen = 0;
        int failed = 0;
        var problems = new Dictionary<string, int>();
    
        foreach (string each in paths)
        {
            DataFile file;
            try { file = DataFile.Read(each); }
            catch { continue; }
    
            foreach (var resource in file.Resources.Where(r => r.ClassHash == BuildTable.ClassHash))
            {
                seen++;
                try
                {
                    var asset = BuildTable.Read(resource.Data);
                    byte[] rebuilt = asset.Write();
                    if (!rebuilt.AsSpan().SequenceEqual(resource.Data))
                    {
                        failed++;
                        Bump(problems, "roundtrip differs at 0x" + FirstDifference(resource.Data, rebuilt).ToString("X"));
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Bump(problems, ex.GetType().Name + ": " + ex.Message);
                }
            }
        }
    
        Console.WriteLine($"{seen} BuildTable(s), {failed} failure(s)");
        if (problems.Count > 0)
            Report("problems:", problems);
        return failed == 0 ? 0 : 1;
    }

    internal static int AuditForgeBuildTables(string archivePath, string containerFilter)
    {
        using var archive = ForgeArchive.Open(archivePath);
        int containers = 0, tables = 0, rows = 0, duplicatable = 0, duplicated = 0, failed = 0;
        var problems = new Dictionary<string, int>();
        var rejected = new Dictionary<string, int>();
    
        foreach (var entry in archive.Entries)
        {
            if (!entry.Name.Contains(containerFilter, StringComparison.OrdinalIgnoreCase))
                continue;
    
            DataFile file;
            try
            {
                using var stream = new MemoryStream(archive.ReadEntry(entry), writable: false);
                file = DataFile.Read(stream);
            }
            catch
            {
                continue;
            }
    
            var resources = file.Resources.Where(resource => resource.ClassHash == BuildTable.ClassHash).ToList();
            if (resources.Count == 0)
                continue;
            containers++;
    
            foreach (var resource in resources)
            {
                tables++;
                try
                {
                    var table = BuildTable.Read(resource.Data);
                    rows += table.Rows.Count;
                    byte[] rebuilt = table.Write();
                    if (!rebuilt.AsSpan().SequenceEqual(resource.Data))
                        throw new InvalidDataException(
                            $"roundtrip differs at 0x{FirstDifference(resource.Data, rebuilt):X}");
    
                    for (int row = 0; row < table.Rows.Count; row++)
                    {
                        if (!table.CanDuplicateRow(row, out string reason))
                        {
                            Bump(rejected, reason);
                            continue;
                        }
                        duplicatable++;
    
                        byte[] data = table.DuplicateRow(row);
                        var copy = BuildTable.Read(data);
                        if (copy.Rows.Count != table.Rows.Count + 1)
                            throw new InvalidDataException(
                                $"row {row} duplication produced {copy.Rows.Count} rows instead of {table.Rows.Count + 1}");
                        byte[] copiedAgain = copy.Write();
                        if (!copiedAgain.AsSpan().SequenceEqual(data))
                            throw new InvalidDataException(
                                $"row {row} duplicate roundtrip differs at 0x{FirstDifference(data, copiedAgain):X}");
                        duplicated++;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Bump(problems, ex.GetType().Name + ": " + ex.Message);
                    Console.WriteLine($"FAILED {entry.Name} / {resource.Name}: {ex.Message}");
                }
            }
    
            if ((containers % 100) == 0)
                Console.WriteLine($"checked {containers} BuildTable container(s), {tables} table(s), {duplicated} row duplication(s)…");
        }
    
        Console.WriteLine($"{containers} container(s), {tables} BuildTable(s), {rows} row(s)");
        Console.WriteLine($"{duplicatable} safely duplicatable row(s), {duplicated} successful duplication(s), {failed} failure(s)");
        if (problems.Count > 0)
            Report("problems:", problems);
        if (rejected.Count > 0)
            Report("intentionally rejected rows:", rejected);
        return failed == 0 && tables > 0 ? 0 : 1;
    }

    internal static int FirstDifference(byte[] left, byte[] right)
    {
        int count = Math.Min(left.Length, right.Length);
        for (int i = 0; i < count; i++)
            if (left[i] != right[i])
                return i;
        return count;
    }

    internal static int InspectBuildTables(string archivePath, string containerFilter, string tableFilter)
    {
        int found = 0;
        using var archive = ForgeArchive.Open(archivePath);
        foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"
                     && entry.Name.Contains(containerFilter, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
            foreach (Resource resource in file.Resources.Where(resource =>
                         resource.ClassHash == BuildTable.ClassHash
                         && resource.Name.Contains(tableFilter, StringComparison.OrdinalIgnoreCase)))
            {
                BuildTableAsset table;
                try
                {
                    table = BuildTable.Read(resource.Data);
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"0x{resource.Id:X12} {resource.Name}: {exception.Message}");
                    continue;
                }
    
                found++;
                Console.WriteLine($"{Path.GetFileName(archivePath)} | {entry.Name} | "
                    + $"0x{resource.Id:X12} {resource.Name} | {table.RowCount} row(s)");
                foreach (BuildTableRow row in table.Rows)
                {
                    string possible = row.PossibleTags.Count == 0
                        ? "-"
                        : string.Join(",", row.PossibleTags.Select(tag => $"0x{tag:X8}"));
                    string references = string.Join(", ", row.References
                        .Where(reference => reference.Value != 0)
                        .Select(reference => $"{reference.Kind}:{reference.ComponentIndex?.ToString() ?? "-"}="
                            + $"0x{reference.Value:X}"));
                    Console.WriteLine($"  row {row.Index} id=0x{row.Id:X} tag=0x{row.Tag:X8} "
                        + $"possible=[{possible}] {references}");
                }
            }
        }
        Console.WriteLine($"{found} BuildTable(s)");
        return found == 0 ? 1 : 0;
    }

    internal static int InspectHandleLists(string archivePath, string containerFilter, string tableFilter,
        IReadOnlyList<ulong> wanted)
    {
        int found = 0;
        int holding = 0;
        using var archive = ForgeArchive.Open(archivePath);
        foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"
                     && entry.Name.Contains(containerFilter, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
            foreach (Resource resource in file.Resources.Where(resource =>
                         resource.ClassHash == BuildTable.ClassHash
                         && resource.Name.Contains(tableFilter, StringComparison.OrdinalIgnoreCase)))
            {
                BuildTableAsset table;
                try
                {
                    table = BuildTable.Read(resource.Data);
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"0x{resource.Id:X12} {resource.Name}: {exception.Message}");
                    continue;
                }
    
                found++;
                var values = table.HandleLists.SelectMany(list => list.Entries)
                    .Select(reference => reference.Value)
                    .ToList();
                int present = wanted.Count(id => values.Contains(id));
                if (wanted.Count > 0 && present == 0)
                    continue;
                if (present == wanted.Count && wanted.Count > 0)
                    holding++;
    
                string lists = string.Join(", ", table.HandleLists.Select(list =>
                    $"{list.Path} at 0x{list.CountOffset:X}: {list.Entries.Count}"));
                string carried = wanted.Count == 0 ? "" : $" | {present}/{wanted.Count} wanted";
                Console.WriteLine($"{entry.Name} | 0x{resource.Id:X12} {resource.Name} | {lists}{carried}");
                foreach (ulong id in wanted.Where(id => values.Contains(id)))
                {
                    BuildTableReference match = table.HandleLists.SelectMany(list => list.Entries)
                        .First(reference => reference.Value == id);
                    Console.WriteLine($"    0x{id:X12} at 0x{match.Offset:X} in {match.Path}");
                }
            }
        }
        Console.WriteLine($"{found} BuildTable(s), {holding} holding every wanted id");
        return found == 0 ? 1 : 0;
    }

    internal static int CheckDuplicatedBuildTable(string archivePath, string containerName,
        string tableName, int rowIndex)
    {
        using var archive = ForgeArchive.Open(archivePath);
        ForgeEntry entry = archive.Entries.Single(entry => entry.FileExtension == ".data"
            && string.Equals(entry.Name, containerName, StringComparison.OrdinalIgnoreCase));
        using var stream = new MemoryStream(archive.ReadEntry(entry));
        Resource resource = DataFile.Read(stream).Resources.Single(resource =>
            resource.ClassHash == BuildTable.ClassHash
            && string.Equals(resource.Name, tableName, StringComparison.OrdinalIgnoreCase));
        BuildTableAsset original = BuildTable.Read(resource.Data);
        BuildTableAsset duplicated = BuildTable.Read(original.DuplicateRow(rowIndex));
        Console.WriteLine("rows: " + string.Join(", ", duplicated.Rows.Select(row => $"0x{row.Id:X}")));
        Console.WriteLine("objects: " + string.Join(", ", duplicated.Objects
            .Where(obj => obj.Id is >= 0xF0000000UL and <= uint.MaxValue)
            .OrderBy(obj => obj.Offset).Select(obj => $"0x{obj.Id:X}")));
        return 0;
    }

    internal static int InspectBuildTagColumnMaps(string archivePath, string containerFilter,
        IReadOnlyList<string> tagTexts)
    {
        const uint mapClassHash = 0xFFC5A970;
        const uint entryMarker = 0xA31AA51D;
        const int entryStride = 21;
        var tags = tagTexts.Select(text => checked((uint)ParseResourceId(text))).ToList();
        int found = 0;
    
        using var archive = ForgeArchive.Open(archivePath);
        foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"
                     && entry.Name.Contains(containerFilter, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
            foreach (Resource resource in file.Resources.Where(resource => resource.ClassHash == mapClassHash))
            {
                foreach (uint tag in tags)
                {
                    byte[] needle = new byte[sizeof(uint)];
                    BinaryPrimitives.WriteUInt32LittleEndian(needle, tag);
                    int search = 0;
                    while (search <= resource.Data.Length - sizeof(uint))
                    {
                        int relative = resource.Data.AsSpan(search).IndexOf(needle);
                        if (relative < 0)
                            break;
                        int tagOffset = search + relative;
                        int itemStart = tagOffset - 13;
                        if (IsTagColumnMapEntry(resource.Data, itemStart, entryMarker))
                        {
                            int runStart = itemStart;
                            while (IsTagColumnMapEntry(resource.Data, runStart - entryStride, entryMarker))
                                runStart -= entryStride;
                            int runEnd = itemStart;
                            while (IsTagColumnMapEntry(resource.Data, runEnd + entryStride, entryMarker))
                                runEnd += entryStride;
                            int regularEntries = (runEnd - runStart) / entryStride + 1;
                            int countOffset = runStart - 24;
                            bool hasFirstEntry = countOffset >= 1
                                && resource.Data[countOffset - 1] == 1
                                && countOffset + 16 <= resource.Data.Length
                                && BinaryPrimitives.ReadUInt32LittleEndian(
                                    resource.Data.AsSpan(countOffset + 12)) == entryMarker;
                            int actualCount = regularEntries + (hasFirstEntry ? 1 : 0);
                            uint declaredCount = countOffset >= 0
                                ? BinaryPrimitives.ReadUInt32LittleEndian(resource.Data.AsSpan(countOffset))
                                : 0;
                            ulong localId = BinaryPrimitives.ReadUInt64LittleEndian(
                                resource.Data.AsSpan(itemStart + 1));
                            uint mask = BinaryPrimitives.ReadUInt32LittleEndian(
                                resource.Data.AsSpan(tagOffset + sizeof(uint)));
                            Console.WriteLine($"0x{tag:X8} {entry.Name} 0x{resource.Id:X}: "
                                + $"tag@0x{tagOffset:X}, item@0x{itemStart:X}, local=0x{localId:X}, "
                                + $"mask=0x{mask:X8}, run@0x{runStart:X}-0x{runEnd + entryStride:X}, "
                                + $"count@0x{countOffset:X}=0x{declaredCount:X} ({declaredCount}), actual={actualCount}");
                            found++;
                        }
                        search = tagOffset + sizeof(uint);
                    }
                }
            }
        }
    
        Console.WriteLine($"{found} structured tag-map occurrence(s)");
        return found == 0 ? 1 : 0;
    
        static bool IsTagColumnMapEntry(ReadOnlySpan<byte> data, int offset, uint marker) =>
            offset >= 0 && offset + entryStride <= data.Length
            && data[offset] == 1
            && BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 1)..])
                is >= 0xF8000000UL and < 0xF9000000UL
            && BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 9)..]) == marker;
    }

    internal static int CheckBuildTagColumnMaps(string gameFolder, string templateValue, string newValue)
    {
        IReadOnlyList<string> archivePaths = ArchiveLocator.Find(gameFolder);
        ArmoryIndex index = ArmoryIndex.Load(AppSettings.ArmoryCachePath, archivePaths)
            ?? ArmoryIndex.Build(archivePaths);
        uint templateTag = checked((uint)ParseResourceId(templateValue));
        uint newTag = checked((uint)ParseResourceId(newValue));
        IReadOnlyList<ArmoryDatabaseResourceChange> changes = index.CreateBuildTagColumnMapInsertions(templateTag, newTag);
        foreach (ArmoryDatabaseResourceChange change in changes)
            Console.WriteLine($"verified {Path.GetFileName(change.ArchivePath)}/{change.EntryName} resource {change.ResourceIndex} {change.ResourceName}");
        return 0;
    }

    internal static int CheckBuildTagInsertion(string archivePath, string containerName, string tableName, int rowIndex)
    {
        using var archive = ForgeArchive.Open(archivePath);
        ForgeEntry entry = archive.Entries.Single(entry => entry.FileExtension == ".data"
            && string.Equals(entry.Name, containerName, StringComparison.OrdinalIgnoreCase));
        using var stream = new MemoryStream(archive.ReadEntry(entry));
        Resource resource = DataFile.Read(stream).Resources.Single(resource => resource.ClassHash == BuildTable.ClassHash
            && string.Equals(resource.Name, tableName, StringComparison.OrdinalIgnoreCase));
        BuildTableAsset original = BuildTable.Read(resource.Data);
        BuildTableRow row = original.Rows[rowIndex];
        BuildTableTagEntry template = row.PossibleTagEntries.LastOrDefault()
            ?? throw new InvalidDataException($"Row {rowIndex} has no PossibleTags entry to clone.");
        var used = original.BuildTagLists.SelectMany(list => list.Entries).Select(tag => tag.Value).Concat(original.Rows.Select(row => row.Tag)).ToHashSet();
        uint newTag = 0x80000000;
        while (!used.Add(newTag))
            newTag++;
    
        BuildTableAsset replacement = BuildTable.Read(resource.Data);
        replacement.ReplacePossibleTag(rowIndex, template.Value, newTag);
        BuildTableAsset replaced = BuildTable.Read(replacement.Write());
        if (replaced.Rows[rowIndex].PossibleTagEntries.Count(entry => entry.Value == newTag) != 1
            || replaced.Rows[rowIndex].PossibleTagEntries.Any(entry => entry.Value == template.Value))
            throw new InvalidDataException("The exact PossibleTags replacement was not retained after parsing.");
    
        do
            newTag++;
        while (!used.Add(newTag));
        byte[] added = original.AddPossibleTag(rowIndex, row.PossibleTagEntries.Count - 1, newTag);
        BuildTableAsset parsed = BuildTable.Read(added);
        if (parsed.Id != original.Id || parsed.RowCount != original.RowCount || parsed.BuildTagLists.Sum(list => list.Entries.Count) != original.BuildTagLists.Sum(list => list.Entries.Count) + 1)
            throw new InvalidDataException("The in-memory BuildTags insertion changed unrelated BuildTable structure.");
        if (!parsed.BuildTagLists.SelectMany(list => list.Entries).Any(tag => tag.Value == newTag))
            throw new InvalidDataException("The inserted BuildTag was not retained after parsing.");
        if (!parsed.Write().AsSpan().SequenceEqual(added))
            throw new InvalidDataException("The edited BuildTable changed during its second write.");
    
        Console.WriteLine($"0x{resource.Id:X12} {resource.Name}: exact replacement and insertion after 0x{template.Value:X8} are parse/write/parse stable");
        return 0;
    }

    internal static int CheckTargetResolution(string gameFolder, IReadOnlyList<string> values)
    {
        IReadOnlyList<string> archivePaths = ArchiveLocator.Find(gameFolder);
        ulong[] ids = values.Select(ParseResourceId).Distinct().ToArray();
        var timer = Stopwatch.StartNew();
        var progress = new Progress<string>(Console.WriteLine);
        BuildTableTargetCatalog catalog = BuildTableTargetResolver.ResolveReferences(archivePaths, [], ids, progress);
        timer.Stop();
    
        foreach (ulong id in ids)
            Console.WriteLine(catalog.ById.TryGetValue(id, out BuildTableTarget? target)
                ? $"0x{id:X12}: {target.Name} ({target.Type}) in {target.Location}"
                : $"0x{id:X12}: unresolved");
        Console.WriteLine($"Resolved {catalog.ById.Count} of {ids.Length} requested assets across {archivePaths.Count} archives in {timer.Elapsed.TotalSeconds:0.000} s.");
        return catalog.ById.Count == ids.Length ? 0 : 1;
    }
}
