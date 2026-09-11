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
    internal static int CheckModelHandles(string gameFolder, string archivePath, string prefix)
    {
        var installed = ArchiveLocator.Find(gameFolder);
        if (installed.Count == 0)
        {
            Console.WriteLine("no forge archives in " + gameFolder);
            return 1;
        }
    
        var entryIds = new Dictionary<ulong, string>();
        foreach (string path in installed)
        {
            try
            {
                using var archive = ForgeArchive.Open(path);
                foreach (var entry in archive.Entries)
                    entryIds.TryAdd(entry.Id, Path.GetFileName(path));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  skipped {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        Console.WriteLine($"{installed.Count} installed archives, {entryIds.Count} Forge entry ids");
    
        using var target = ForgeArchive.Open(archivePath);
        int tables = 0, resolved = 0;
        var unresolved = new List<string>();
    
        foreach (var entry in target.Entries)
        {
            if (entry.FileExtension != ".data"
                || !entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
    
            DataFile file;
            try
            {
                using var stream = new MemoryStream(target.ReadEntry(entry));
                file = DataFile.Read(stream);
            }
            catch { continue; }
    
            var localIds = file.Resources.Select(resource => resource.Id).ToHashSet();
            foreach (var resource in file.Resources)
            {
                if (resource.ClassHash != BuildTable.ClassHash)
                    continue;
    
                BuildTableAsset table;
                try { table = BuildTable.Read(resource.Data); }
                catch { continue; }
                tables++;
    
                foreach (var row in table.Rows)
                {
                    foreach (var reference in row.References)
                    {
                        if (reference.Kind != BuildTableReferenceKind.Handle
                            || reference.ComponentIndex is null
                            || reference.Value == 0)
                            continue;
    
                        if (entryIds.ContainsKey(reference.Value))
                        {
                            resolved++;
                            continue;
                        }
    
                        string where = localIds.Contains(reference.Value)
                            ? "it is a plain resource inside this same container"
                            : "no installed archive holds a Forge entry with that id";
                        unresolved.Add($"{entry.Name}/{resource.Name} row {row.Index} "
                            + $"tag 0x{row.Tag:X8} names 0x{reference.Value:X}: {where}");
                    }
                }
            }
        }
    
        Console.WriteLine($"{tables} BuildTables in {Path.GetFileName(archivePath)}: "
            + $"{resolved} model handles reach a Forge entry, {unresolved.Count} do not");
        foreach (string line in unresolved)
            Console.WriteLine("  " + line);
        return unresolved.Count == 0 ? 0 : 1;
    }
    
    // Four bytes behind the handle of a LOD that lives in a Forge entry of its own, a LODSelector
    // carries how much GPU data that LOD is. Measured over all 2846 LODSelectors of DataPC.forge
    // that stream a LOD: 1949 count the vertex buffer, the index buffer and the primitive
    // descriptions, 838 leave the descriptions out, 59 hold zero, and nothing else occurs. A
    // number left over from a replaced mesh promises the streamer more data than the file holds,
    // and everything that wants the streamed LOD - the Gunsmith above all - stops loading.

    internal static int CheckStreamedLodSizes(string gameFolder, string archivePath, string prefix)
    {
        var installed = ArchiveLocator.Find(gameFolder);
        if (installed.Count == 0)
        {
            Console.WriteLine("no forge archives in " + gameFolder);
            return 1;
        }
    
        var homeOf = new Dictionary<ulong, string>();
        foreach (string path in installed)
        {
            try
            {
                using var archive = ForgeArchive.Open(path);
                foreach (var entry in archive.Entries)
                    homeOf.TryAdd(entry.Id, path);
            }
            catch { }
        }
    
        var open = new Dictionary<string, ForgeArchive>(StringComparer.OrdinalIgnoreCase);
        var meshes = new Dictionary<ulong, (int Buffers, int WithDescriptions)>();
        int selectors = 0, correct = 0, zero = 0;
        var wrong = new List<string>();
    
        try
        {
            using var target = ForgeArchive.Open(archivePath);
            uint selectorHash = ResourceTypes.Crc32("LODSelector");
    
            foreach (var entry in target.Entries)
            {
                if (entry.FileExtension != ".data" || entry.Extension != selectorHash
                    || !entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    continue;
    
                DataFile file;
                try
                {
                    using var stream = new MemoryStream(target.ReadEntry(entry));
                    file = DataFile.Read(stream);
                }
                catch { continue; }
    
                Resource selector = file.Resources[0];
                var own = file.Resources.Select(resource => resource.Id).ToHashSet();
                selectors++;
    
                byte[] data = selector.Data;
                for (int offset = 0; offset + sizeof(ulong) + 2 * sizeof(uint) <= data.Length; offset++)
                {
                    ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
                    if (id == selector.Id || own.Contains(id) || !homeOf.TryGetValue(id, out string? home))
                        continue;
    
                    if (!meshes.TryGetValue(id, out var size))
                    {
                        if (!open.TryGetValue(home, out ForgeArchive? source))
                            open[home] = source = ForgeArchive.Open(home);
                        var lodEntry = source.Entries.First(candidate => candidate.Id == id);
                        using var stream = new MemoryStream(source.ReadEntry(lodEntry));
                        Resource lod = DataFile.Read(stream).Resources[0];
                        if (lod.ClassHash != Mesh.ClassHash) continue;
                        var mesh = Mesh.Read(lod.Data);
                        if (mesh.Clustered is null) continue;
                        int buffers = mesh.Clustered.VertexBuffer.Length + mesh.Clustered.IndexBuffer.Length;
                        meshes[id] = size = (buffers, buffers + mesh.Clustered.PrimitiveDescriptions.Length);
                    }
    
                    uint value = BinaryPrimitives.ReadUInt32LittleEndian(
                        data.AsSpan(offset + sizeof(ulong) + sizeof(uint)));
                    if (value == 0) zero++;
                    else if (value == size.Buffers || value == size.WithDescriptions) correct++;
                    else
                        wrong.Add($"{entry.Name}: streamed LOD 0x{id:X} is {size.Buffers} bytes "
                            + $"({size.WithDescriptions} with descriptions), the selector says {value}");
                }
            }
        }
        finally
        {
            foreach (var archive in open.Values)
                archive.Dispose();
        }
    
        Console.WriteLine($"{selectors} LODSelectors in {Path.GetFileName(archivePath)}: "
            + $"{correct} streamed sizes correct, {zero} left at zero, {wrong.Count} wrong");
        foreach (string line in wrong)
            Console.WriteLine("  " + line);
        return wrong.Count == 0 ? 0 : 1;
    }
    
    // A container ships in the base archive and again in the patch, and both are mounted. A mod
    // that changes one copy and leaves the other is the oldest way to waste an evening here: part
    // of the game shows the change and part of it does not. This holds every BuildTable of a
    // container against the same table in every other installed copy and lists the options that
    // only one of them has.

    internal static int CheckCopiesAgree(string gameFolder, string archivePath, string prefix)
    {
        var installed = ArchiveLocator.Find(gameFolder)
            .Where(path => !string.Equals(path, archivePath, StringComparison.OrdinalIgnoreCase))
            .ToList();
    
        using var target = ForgeArchive.Open(archivePath);
        var wanted = target.Entries
            .Where(entry => entry.FileExtension == ".data"
                && entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(entry => entry.Id, entry => entry);
        if (wanted.Count == 0)
        {
            Console.WriteLine($"no container in {Path.GetFileName(archivePath)} matches \"{prefix}\"");
            return 1;
        }
    
        var here = new Dictionary<ulong, Dictionary<ulong, (string Name, HashSet<uint> Tags)>>();
        foreach (var entry in wanted.Values)
            here[entry.Id] = ReadOptionTags(target, entry);
    
        int compared = 0, agree = 0;
        var differences = new List<string>();
    
        foreach (string path in installed)
        {
            ForgeArchive other;
            try { other = ForgeArchive.Open(path); }
            catch { continue; }
    
            using (other)
            {
                foreach (var entry in other.Entries.Where(entry => wanted.ContainsKey(entry.Id)))
                {
                    var mine = here[entry.Id];
                    var theirs = ReadOptionTags(other, entry);
                    foreach ((ulong id, var table) in mine)
                    {
                        if (!theirs.TryGetValue(id, out var second))
                        {
                            differences.Add($"{table.Name}: {Path.GetFileName(path)} has no such table "
                                + $"in its copy of {entry.Name}");
                            continue;
                        }
                        compared++;
                        var onlyHere = table.Tags.Except(second.Tags).ToList();
                        var onlyThere = second.Tags.Except(table.Tags).ToList();
                        if (onlyHere.Count == 0 && onlyThere.Count == 0) { agree++; continue; }
                        foreach (uint tag in onlyHere)
                            differences.Add($"{table.Name}: option 0x{tag:X8} is only in "
                                + $"{Path.GetFileName(archivePath)}, not in {Path.GetFileName(path)}");
                        foreach (uint tag in onlyThere)
                            differences.Add($"{table.Name}: option 0x{tag:X8} is only in "
                                + $"{Path.GetFileName(path)}, not in {Path.GetFileName(archivePath)}");
                    }
                }
            }
        }
    
        Console.WriteLine($"{wanted.Count} container(s) matching \"{prefix}\": {compared} BuildTables "
            + $"held against a second installed copy, {agree} agree, {differences.Count} difference(s)");
        foreach (string line in differences)
            Console.WriteLine("  " + line);
        return differences.Count == 0 ? 0 : 1;
    }

    internal static int PackData(string path, string output)
    {
        var original = DataFile.Read(path);
        original.Write(output);
        var again = DataFile.Read(output);
    
        Console.WriteLine($"{original.Resources.Count} resources -> {output} ({new FileInfo(output).Length} bytes)");
    
        if (again.Resources.Count != original.Resources.Count)
        {
            Console.WriteLine($"resource count changed: {again.Resources.Count}");
            return 1;
        }
    
        int differing = 0;
        for (int i = 0; i < original.Resources.Count; i++)
        {
            var a = original.Resources[i];
            var b = again.Resources[i];
    
            if (a.Id == b.Id && a.ClassHash == b.ClassHash && a.Name == b.Name
                && a.Header.AsSpan().SequenceEqual(b.Header) && a.Data.AsSpan().SequenceEqual(b.Data))
                continue;
    
            if (differing++ < 5)
                Console.WriteLine($"  differs: [{i}] {a.Name}");
        }
    
        Console.WriteLine(differing == 0 ? "round trip is identical" : $"{differing} resources differ");
        return differing == 0 ? 0 : 1;
    }
    
    // Puts new bytes into one resource and writes the whole data file.

    internal static int SetResource(string path, string name, string input, string output)
    {
        var file = DataFile.Read(path);
        Resource resource = RequireNamedResource(file, name);
    
        int before = resource.Data.Length;
        resource.Data = File.ReadAllBytes(input);
        file.Write(output);
    
        Console.WriteLine($"{resource.Name}  {before} -> {resource.Data.Length} bytes, wrote {output}");
        return 0;
    }
    
    // Replaces the data of one archive entry without rebuilding the archive.

    internal static int RebuildArchive(string forgePath, string outputPath)
    {
        var timer = Stopwatch.StartNew();
    
        using (var archive = ForgeArchive.Open(forgePath))
        {
            Console.WriteLine($"{archive.Entries.Count} entries");
            archive.Rebuild(outputPath, new Dictionary<int, byte[]>(),
                new Progress<string>(Console.WriteLine));
        }
    
        Console.WriteLine($"written in {timer.Elapsed.TotalSeconds:0.0} s");
    
        var original = new FileInfo(forgePath);
        var rebuilt = new FileInfo(outputPath);
    
        Console.WriteLine($"{original.Length:N0} -> {rebuilt.Length:N0} bytes");
    
        if (original.Length != rebuilt.Length)
        {
            Console.WriteLine("the rebuilt archive has a different size");
            return 1;
        }
    
        using var a = File.OpenRead(forgePath);
        using var b = File.OpenRead(outputPath);
    
        var left = new byte[1 << 20];
        var right = new byte[1 << 20];
        long at = 0;
    
        while (true)
        {
            int read = a.Read(left);
            if (read == 0)
                break;
    
            b.ReadExactly(right, 0, read);
    
            for (int i = 0; i < read; i++)
            {
                if (left[i] != right[i])
                {
                    Console.WriteLine($"first difference at 0x{at + i:X}");
                    return 1;
                }
            }
    
            at += read;
        }
    
        Console.WriteLine("the rebuilt archive is byte identical");
        return 0;
    }
    
    // Writes one entry back. In place when it still fits, which leaves the rest of
    // the archive untouched. When it grew, only a full rebuild can take it, and
    // that needs somewhere to write the new archive.

    internal static int PutEntry(string forgePath, int index, string input, string? rebuiltPath)
    {
        var data = File.ReadAllBytes(input);
    
        if (rebuiltPath is null)
        {
            if (!ArchiveBackup.Exists(forgePath))
            {
                Console.WriteLine("backing up the untouched archive...");
                ArchiveBackup.Ensure(forgePath);
            }
    
            using var archive = ForgeArchive.OpenForUpdate(forgePath);
            var entry = archive.Entries.FirstOrDefault(e => e.Index == index);
    
            if (entry is null)
            {
                Console.WriteLine("no entry with index " + index);
                return 1;
            }
    
            int previous = entry.Length;
    
            try
            {
                archive.ReplaceEntry(entry, data);
            }
            catch (InvalidOperationException ex)
            {
                Console.WriteLine(ex.Message);
                Console.WriteLine("pass an output path as the fourth argument to rebuild the archive instead.");
                return 1;
            }
    
            Console.WriteLine($"{entry.Name}  {previous} -> {data.Length} bytes, in place at 0x{entry.Offset:X}");
            return 0;
        }
    
        var timer = Stopwatch.StartNew();
    
        using (var archive = ForgeArchive.Open(forgePath))
        {
            var entry = archive.Entries.FirstOrDefault(e => e.Index == index);
    
            if (entry is null)
            {
                Console.WriteLine("no entry with index " + index);
                return 1;
            }
    
            Console.WriteLine($"{entry.Name}  {entry.Length} -> {data.Length} bytes, rebuilding {archive.Entries.Count} entries");
            archive.Rebuild(rebuiltPath, new Dictionary<int, byte[]> { [index] = data });
        }
    
        Console.WriteLine($"written -> {rebuiltPath} in {timer.Elapsed.TotalSeconds:0.0} s");
    
        using (var check = ForgeArchive.Open(rebuiltPath))
        {
            var entry = check.Entries.FirstOrDefault(e => e.Index == index);
    
            if (entry is null || !check.ReadEntry(entry).AsSpan().SequenceEqual(data))
            {
                Console.WriteLine("the rebuilt archive does not hold what was just written");
                return 1;
            }
        }
    
        Console.WriteLine("reopened and verified");
        return 0;
    }
    
    // Writes one resource of a data file to disk, picked by name.

    internal static int GetResource(string path, string name, string output)
    {
        var file = DataFile.Read(path);
        Resource resource = RequireNamedResource(file, name);
    
        File.WriteAllBytes(output, resource.Data);
        Console.WriteLine(ResourceTypes.NameOf(resource.ClassHash) + "  " + resource.Data.Length + " bytes -> " + output);
        return 0;
    }
    
    // Counts which geometry container and which vertex format the meshes of an
    // archive actually use, so the reader is built for what is there. The trailing
    // values of a CompiledMesh are constants, so they say whether the whole chain
    // was read at the right offsets.

    internal static int WriteEntry(string path, int index, string output)
    {
        using var archive = ForgeArchive.Open(path);
        var entry = archive.Entries.First(e => e.Index == index);
        File.WriteAllBytes(output, archive.ReadEntry(entry));
        Console.WriteLine($"{entry.Name}{entry.FileExtension} -> {output} ({entry.Length} bytes)");
        return 0;
    }

    internal static int ListResources(string path, int count)
    {
        var watch = Stopwatch.StartNew();
        var file = DataFile.Read(path);
        watch.Stop();
    
        Console.WriteLine($"{Path.GetFileName(path)}  id 0x{file.Id:X}  {file.Resources.Count} resources  ({watch.ElapsedMilliseconds} ms)");
    
        foreach (var resource in file.Resources.Take(count))
            Console.WriteLine($"  {ResourceTypes.NameOf(resource.ClassHash),-32} {resource.Data.Length,10}  " +
                              $"0x{resource.Id:X12}  {resource.Name}");
    
        return 0;
    }
    
    // Walks an archive and reports, per texture, which mip levels are embedded and
    // which ones come from a separate CompiledMip.

    internal static int ListForge(string path, int count)
    {
        var watch = Stopwatch.StartNew();
        using var archive = ForgeArchive.Open(path);
        watch.Stop();
    
        Console.WriteLine($"{Path.GetFileName(path)}  version {archive.Version}  {archive.Entries.Count} entries  ({watch.ElapsedMilliseconds} ms)");
    
        foreach (var entry in archive.Entries.Take(count))
            Console.WriteLine($"  {entry.Index,6}  {entry.Length,10}  0x{entry.Id:X12}  {entry.Name}{entry.FileExtension}");
    
        return 0;
    }

    internal static int ReadBlobs(string path, string? outputDirectory)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
    
        Console.WriteLine($"{Path.GetFileName(path)}  ({stream.Length} bytes)");
    
        if (outputDirectory is not null)
            Directory.CreateDirectory(outputDirectory);
    
        int index = 0;
        while (stream.Position < stream.Length - 8)
        {
            long start = stream.Position;
            byte[] data;
            try
            {
                data = CompressedBlob.Read(reader);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  blob {index} at 0x{start:x}: {ex.Message}");
                return 1;
            }
    
            Console.WriteLine($"  blob {index} at 0x{start:x} -> {data.Length} bytes");
    
            if (outputDirectory is not null)
            {
                string name = Path.Combine(outputDirectory, $"{Path.GetFileNameWithoutExtension(path)}.blob{index}.bin");
                File.WriteAllBytes(name, data);
            }
    
            index++;
        }
    
        Console.WriteLine("ok");
        return 0;
    }
    
    
    // Writes one time cycle out in the form TimeCycleText defines: a block per
    // entry, a line per key. That text is the editing surface - change the numbers,
    // hand it to "settimecycle", put the data file back with "putentry".

    internal static int WhereIsResource(string gameFolder, string name, bool includeWorldMap)
    {
        var archives = ArchiveLocator.Find(gameFolder);
        if (archives.Count == 0)
        {
            Console.WriteLine("no forge archives in " + gameFolder);
            return 1;
        }
    
        var skipped = new List<string>();
        var found = new List<(string Archive, string Container, ulong Id, string Resource, string Kind, int Bytes)>();
    
        foreach (string path in archives)
        {
            if (!includeWorldMap && Path.GetFileName(path).Contains("WorldMap", StringComparison.OrdinalIgnoreCase))
            {
                skipped.Add(Path.GetFileName(path));
                continue;
            }
    
            ForgeArchive archive;
            try { archive = ForgeArchive.Open(path); }
            catch { continue; }
    
            using (archive)
            {
                foreach (var entry in archive.Entries)
                {
                    if (entry.FileExtension != ".data") continue;
    
                    DataFile file;
                    try
                    {
                        using var stream = new MemoryStream(archive.ReadEntry(entry));
                        file = DataFile.Read(stream);
                    }
                    catch { continue; }
    
                    foreach (var resource in file.Resources)
                    {
                        if (!resource.Name.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;
    
                        found.Add((Path.GetFileName(path), entry.Name, resource.Id, resource.Name,
                            ResourceTypes.NameOf(resource.ClassHash), resource.Data.Length));
                    }
                }
            }
        }
    
        if (found.Count == 0)
        {
            Console.WriteLine("nothing matching " + name);
            if (skipped.Count > 0)
                Console.WriteLine("(the world map archives were skipped; add --all to search them too)");
            return 1;
        }
    
        foreach (var perResource in found
                     .GroupBy(foundResource => (foundResource.Resource, foundResource.Id))
                     .OrderBy(group => group.Key.Resource))
        {
            Console.WriteLine(perResource.Key.Resource + "  (" + perResource.First().Kind + ")");
    
            foreach (var hit in perResource)
                Console.WriteLine($"    0x{hit.Id:X12}  {hit.Archive,-42} container \"{hit.Container}\"  {hit.Bytes} bytes");
    
            if (perResource.Count() > 1)
                Console.WriteLine($"    -> {perResource.Count()} copies. Change every one of them, or the game keeps showing an older copy.");
    
            Console.WriteLine();
        }
    
        if (skipped.Count > 0)
            Console.WriteLine($"{skipped.Count} world map archive(s) were skipped because they are huge; add --all to search them too.");
    
        return 0;
    }

    internal static int FindAssetCopies(string archivePath, string text)
    {
        ulong id = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? Convert.ToUInt64(text[2..], 16)
            : Convert.ToUInt64(text);
        var progress = new Progress<string>(Console.WriteLine);
        var copies = AssetUsageScanner.Find(archivePath, id, progress);
    
        foreach (var copy in copies)
            Console.WriteLine($"{copy.Archive} | {copy.Container} | {copy.Type} | {copy.Name} | {copy.SizeText}");
        Console.WriteLine(copies.Count == 1 ? "1 exact copy" : $"{copies.Count} exact copies");
        return copies.Count == 0 ? 1 : 0;
    }

    internal static int FindResourcesById(string archivePath, IReadOnlyList<string> texts)
    {
        var wanted = texts.Select(text => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt64(text[2..], 16)
                : Convert.ToUInt64(text))
            .Distinct()
            .ToHashSet();
        var found = new HashSet<ulong>();
    
        using var archive = ForgeArchive.Open(archivePath);
        foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"))
        {
            IReadOnlyList<ResourceIndexEntry> resources;
            try
            {
                resources = archive.ReadResourceIndex(entry);
            }
            catch
            {
                continue;
            }
    
            if (!resources.Any(resource => wanted.Contains(resource.Id)))
                continue;
    
            DataFile file;
            try
            {
                using var stream = new MemoryStream(archive.ReadEntry(entry));
                file = DataFile.Read(stream);
            }
            catch
            {
                continue;
            }
    
            foreach (Resource resource in file.Resources.Where(resource => wanted.Contains(resource.Id)))
            {
                found.Add(resource.Id);
                Console.WriteLine($"0x{resource.Id:X12}  {ResourceTypes.NameOf(resource.ClassHash),-32} "
                    + $"{resource.Name}  in {entry.Name}");
            }
        }
    
        foreach (ulong missing in wanted.Except(found))
            Console.WriteLine($"0x{missing:X12}  not present in {Path.GetFileName(archivePath)}");
        return found.Count == wanted.Count ? 0 : 1;
    }

    internal static int InspectPrefetchReferences(string archivePath, string entryText,
        IReadOnlyList<string> referenceTexts)
    {
        ulong entryId = ParseResourceId(entryText);
        ulong[] references = referenceTexts.Select(ParseResourceId).Distinct().ToArray();
        using var archive = ForgeArchive.Open(archivePath);
        ForgeEntry entry = archive.Entries.Single(candidate => candidate.Id == entryId);
        ForgeEntry prefetchEntry = archive.Entries.Single(candidate =>
            candidate is { Id: 145, Name: "PrefetchingFileInfos" });
        byte[] prefetch = archive.ReadEntry(prefetchEntry);
        byte[] block = PrefetchingFileInfos.ReadObjectBlock(prefetch, entryId);
        Console.WriteLine($"{Path.GetFileName(archivePath)} | {entry.Name} | "
            + $"0x{entry.Id:X12} | {block.Length} prefetch bytes");
        foreach (ulong reference in references)
        {
            byte[] encoded = BitConverter.GetBytes(reference);
            int count = 0;
            for (int offset = 0; offset <= block.Length - encoded.Length; offset++)
            {
                if (!block.AsSpan(offset, encoded.Length).SequenceEqual(encoded))
                    continue;
                count++;
                offset += encoded.Length - 1;
            }
            Console.WriteLine($"  0x{reference:X12}: {count} exact occurrence(s)");
        }
        return 0;
    }

    internal static int CreatePatchArchive(string sourcePath, string outputPath, IReadOnlyList<string> entryNames)
    {
        var entries = new List<ForgeNewEntry>();
        using (var source = ForgeArchive.Open(sourcePath))
        {
            ForgeEntry prefetchEntry = source.Entries.Single(entry => entry.Id == 145);
            byte[] prefetch = source.ReadEntry(prefetchEntry);
            var wanted = source.Entries.Where(entry => entry.Id is not (16 or 145)
                    && entryNames.Any(name => name.EndsWith('*')
                        ? entry.Name.StartsWith(name[..^1], StringComparison.OrdinalIgnoreCase)
                        : entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (wanted.Count == 0)
                throw new InvalidOperationException("No entry in the source archive matched.");
            foreach (ForgeEntry entry in wanted)
                entries.Add(new ForgeNewEntry(entry.Id, entry.Name, entry.Extension, entry.UmacHash,
                    source.ReadEntry(entry), PrefetchingFileInfos.ReadObjectBlock(prefetch, entry.Id)));
        }
    
        ForgeArchive.Create(outputPath, entries, Guid.NewGuid().ToString(),
            (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    
        using var written = ForgeArchive.Open(outputPath);
        Console.WriteLine($"{Path.GetFileName(outputPath)}: {written.Entries.Count} entries, "
            + $"{new FileInfo(outputPath).Length / (1024.0 * 1024.0):0.0} MB");
        foreach (ForgeEntry entry in written.Entries)
            Console.WriteLine($"  {entry.Length,12:n0}  0x{entry.Id:X12}  {entry.Name}{entry.FileExtension}");
        return 0;
    }

    internal static int InstallPackageAsAddon(string gameFolder, string packagePath)
    {
        string staging = Path.Combine(Path.GetTempPath(), "wlcli-addon-" + Guid.NewGuid().ToString("N"));
        try
        {
            ModProject project = ModProject.ImportPackage(packagePath, staging);
            ModCompileResult compiled = project.Compile(gameFolder);
            if (compiled.Problems.Count != 0)
            {
                Console.Error.WriteLine("The package does not compile against this installation:");
                foreach (string problem in compiled.Problems)
                    Console.Error.WriteLine("  " + problem);
                return 1;
            }
    
            List<ArchiveWork> plans = ArchiveChangePlanner.Plan(compiled.Changes);
            if (plans.Count == 0)
            {
                Console.WriteLine("Nothing to write; every change is already installed.");
                return 0;
            }
    
            if (!AddonArchiveService.CanWrite(plans, out string reason))
            {
                Console.Error.WriteLine(reason);
                return 1;
            }
    
            Console.WriteLine($"{compiled.Changes.Count} change(s) in {plans.Count} archive(s), "
                + $"{AddonArchiveService.EstimateSize(plans) / (1024.0 * 1024.0):0.0} MB of entries");
            var progress = new Progress<string>(Console.WriteLine);
            IReadOnlyList<string> written = AddonArchiveService.Write(plans, Guid.NewGuid().ToString(), progress);
            foreach (string path in written)
                Console.WriteLine($"wrote {path} ({new FileInfo(path).Length / (1024.0 * 1024.0):0.0} MB)");
            return 0;
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (IOException) { }
        }
    }

    internal static int CensusResourceClasses(string folder)
    {
        var counts = new Dictionary<uint, int>();
        var bytes = new Dictionary<uint, long>();
        int files = 0, unreadable = 0;
        foreach (string path in Directory.EnumerateFiles(folder, "*.data"))
        {
            try
            {
                using var stream = File.OpenRead(path);
                DataFile file = DataFile.Read(stream);
                files++;
                foreach (Resource resource in file.Resources)
                {
                    counts[resource.ClassHash] = counts.GetValueOrDefault(resource.ClassHash) + 1;
                    bytes[resource.ClassHash] = bytes.GetValueOrDefault(resource.ClassHash) + resource.Data.Length;
                }
            }
            catch
            {
                unreadable++;
            }
        }
    
        Console.WriteLine($"{files:n0} container(s), {unreadable:n0} unreadable, {counts.Count} class(es)");
        Console.WriteLine($"{"count",10}  {"bytes",14}  class       name");
        foreach ((uint hash, int count) in counts.OrderByDescending(pair => pair.Value))
            Console.WriteLine($"{count,10:n0}  {bytes[hash],14:n0}  0x{hash:X8}  {ResourceTypes.NameOf(hash)}");
        return 0;
    }

    internal static int DumpPrefetchBlock(string archivePath, IReadOnlyList<string> entryTexts)
    {
        var wanted = entryTexts.Select(ParseResourceId).ToHashSet();
        using var archive = ForgeArchive.Open(archivePath);
        ForgeEntry prefetchEntry = archive.Entries.Single(candidate => candidate.Id == 145);
        byte[] data = archive.ReadEntry(prefetchEntry);
        foreach ((ulong id, byte[] block) in PrefetchingFileInfos.ReadObjects(data))
        {
            if (wanted.Count > 0 && !wanted.Contains(id))
                continue;
            Console.WriteLine($"0x{id:X12}  {block.Length} bytes");
            for (int offset = 0; offset < block.Length; offset += 16)
                Console.WriteLine($"  {offset:X4}  "
                    + Convert.ToHexString(block.AsSpan(offset, Math.Min(16, block.Length - offset))));
        }
        return 0;
    }

    internal static int CyclePrefetchBlocks(string gameFolder, string filter = "", bool dump = false)
    {
        int archives = 0, blocks = 0, exact = 0, failed = 0, references = 0;
        foreach (string path in ArchiveLocator.Find(gameFolder)
                     .Where(path => Path.GetFileName(path).Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            byte[] data;
            try
            {
                using var archive = ForgeArchive.Open(path);
                ForgeEntry? entry = archive.Entries.FirstOrDefault(candidate => candidate.Id == 145);
                if (entry is null)
                    continue;
                data = archive.ReadEntry(entry);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"{Path.GetFileName(path)}: {exception.Message}");
                failed++;
                continue;
            }
    
            archives++;
            int archiveBlocks = 0, archiveExact = 0, archiveRefs = 0;
            foreach ((ulong id, byte[] block) in PrefetchingFileInfos.ReadObjects(data))
            {
                archiveBlocks++;
                try
                {
                    PrefetchBlock parsed = PrefetchingFileInfos.ReadBlock(block);
                    archiveRefs += parsed.References.Count;
                    if (PrefetchingFileInfos.WriteBlock(parsed).AsSpan().SequenceEqual(block))
                        archiveExact++;
                    else
                        Console.WriteLine($"  0x{id:X12} in {Path.GetFileName(path)} differs after rewrite");
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"  0x{id:X12} in {Path.GetFileName(path)}: {exception.Message}");
                    if (dump)
                    {
                        Console.WriteLine($"    flags 0x{BitConverter.ToUInt16(block, 0):X4} count {BitConverter.ToUInt16(block, 2)} length {block.Length}");
                        Console.WriteLine("    " + Convert.ToHexString(block.AsSpan(0, Math.Min(64, block.Length))));
                    }
                }
            }
    
            blocks += archiveBlocks;
            exact += archiveExact;
            references += archiveRefs;
            failed += archiveBlocks - archiveExact;
            Console.WriteLine($"{Path.GetFileName(path),-44} {archiveBlocks,7:n0} blocks  {archiveRefs,8:n0} references  "
                + (archiveBlocks == archiveExact ? "byte for byte" : $"{archiveBlocks - archiveExact:n0} FAILED"));
        }
    
        Console.WriteLine($"{archives} archive(s), {blocks:n0} prefetch block(s) with {references:n0} reference(s), "
            + $"{exact:n0} byte for byte, {failed:n0} failure(s)");
        return failed == 0 ? 0 : 1;
    }

    internal static int CycleGlobalMetaFiles(string gameFolder)
    {
        int read = 0, exact = 0, failed = 0;
        foreach (string path in ArchiveLocator.Find(gameFolder))
        {
            ForgeEntry? entry;
            byte[] data;
            try
            {
                using var archive = ForgeArchive.Open(path);
                entry = archive.Entries.FirstOrDefault(candidate => candidate.Id == GlobalMetaFile.EntryId);
                if (entry is null)
                    continue;
                data = archive.ReadEntry(entry);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"{Path.GetFileName(path)}: {exception.Message}");
                failed++;
                continue;
            }
    
            read++;
            try
            {
                GlobalMetaFileAsset asset = GlobalMetaFile.Read(data);
                byte[] written = GlobalMetaFile.Write(asset);
                bool same = written.AsSpan().SequenceEqual(data);
                if (same)
                    exact++;
                else
                    failed++;
                GlobalMetaField? identity = asset.Find(GlobalMetaFile.IdentityTag);
                string synthesised = "";
                if (identity is { Kind: GlobalMetaFieldKind.Text } && asset.Fields.Count == 20)
                    synthesised = GlobalMetaFile.CreatePatch(identity.Text).AsSpan().SequenceEqual(data)
                        ? "  synthesised exactly"
                        : "  SYNTHESIS DIFFERS";
                Console.WriteLine($"{Path.GetFileName(path),-44} {data.Length,9:n0} B  {asset.Fields.Count,3} fields  "
                    + (same ? "byte for byte" : $"DIFFERS ({written.Length:n0} B)")
                    + (identity is { Kind: GlobalMetaFieldKind.Text } ? "  " + identity.Text : "") + synthesised);
            }
            catch (Exception exception)
            {
                failed++;
                Console.WriteLine($"{Path.GetFileName(path),-44} {data.Length,9:n0} B  {exception.Message}");
            }
        }
    
        Console.WriteLine($"{read} GlobalMetaFile(s), {exact} byte for byte, {failed} failure(s)");
        return failed == 0 ? 0 : 1;
    }
}
