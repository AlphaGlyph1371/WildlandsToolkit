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
    internal static int CheckTextureSets(string forgePath, int limit)
    {
        using var archive = ForgeArchive.Open(forgePath);
    
        var names = new Dictionary<ulong, string>();
        var sets = new List<TextureSet>();
    
        foreach (var entry in archive.Entries)
        {
            if (sets.Count >= limit) break;
            if (entry.FileExtension != ".data") continue;
    
            DataFile file;
            try { using var s = new MemoryStream(archive.ReadEntry(entry)); file = DataFile.Read(s); }
            catch { continue; }
    
            foreach (var resource in file.Resources)
            {
                names[resource.Id] = resource.Name;
    
                if (resource.ClassHash == TextureSet.ClassHash && sets.Count < limit)
                    sets.Add(TextureSet.Read(resource.Data));
            }
        }
    
        var kinds = new Dictionary<string, int>();
        int filled = 0, missing = 0;
    
        foreach (var set in sets)
        {
            foreach (var texture in set.Textures)
            {
                filled++;
    
                if (!names.TryGetValue(texture.Id, out var target))
                {
                    missing++;
                    continue;
                }
    
                Bump(kinds, texture.Name + "  ->  " + KindOf(target));
            }
        }
    
        Console.WriteLine(sets.Count + " texture sets, " + filled + " filled slots, "
            + missing + " pointing outside this archive");
        Report("slot and what it points at:", kinds);
        return 0;
    }
    
    // Checks the material list that follows the CompiledMesh: one entry per draw
    // range is what it should be, and each entry has to point at a real Material.
    // Walks the parameter list of every material in an archive. Object values carry
    // no length in the file, so the lengths in Material were solved for; a material
    // whose walk ends exactly on its last byte is the proof that they hold. The name
    // of a parameter is a CRC32 nobody wrote down, so the census also dumps the
    // hashes it saw - that list is what "guess" is fed to crack them.
    // Camo options and camo textures both carry a leading number, and that number
    // is the whole link: option 36 is "36-Multicam Alpine" and its texture is
    // "36-MulicamAlpine_Map", misspelt - so the game cannot be matching on the name.
    // The options without a texture are the ones called "solid", which are a plain
    // colour and need none.

    internal static int MatchCamo(string[] forgePaths)
    {
        const uint CustomizationOption = 0x65594AE5;
    
        var options = new SortedDictionary<int, List<string>>();
        var textures = new SortedDictionary<int, List<string>>();
        var names = new Dictionary<ulong, string>();
        var patterns = new List<CamoPattern>();
    
        foreach (string forgePath in forgePaths)
        {
            using var archive = ForgeArchive.Open(forgePath);
    
            foreach (var entry in archive.Entries)
            {
                if (entry.FileExtension != ".data") continue;
    
                DataFile file;
                try { using var s = new MemoryStream(archive.ReadEntry(entry)); file = DataFile.Read(s); }
                catch { continue; }
    
                foreach (var resource in file.Resources)
                {
                    names[resource.Id] = resource.Name;
    
                    // A camo pattern resource is the other half of the picture: it
                    // carries the name the game shows and points at its texture.
                    if (resource.ClassHash == CamoPattern.ClassHash)
                        patterns.Add(CamoPattern.Read(resource.Data));
    
                    int number = LeadingNumber(resource.Name);
                    if (number < 0) continue;
    
                    if (resource.ClassHash == CustomizationOption)
                        Collect(options, number, resource.Name);
                    else if (resource.ClassHash == TextureMap.ClassHash)
                        Collect(textures, number, resource.Name);
                }
            }
        }
    
        int matched = 0, solid = 0, orphan = 0;
    
        foreach (var (number, named) in options)
        {
            bool hasTexture = textures.TryGetValue(number, out var wearing);
            foreach (string name in named)
            {
                if (hasTexture) matched++;
                else if (name.Contains("solid", StringComparison.OrdinalIgnoreCase)) solid++;
                else orphan++;
    
                Console.WriteLine("  " + name.PadRight(28) + "  ->  "
                    + (hasTexture ? string.Join(", ", wearing!) : "no texture"));
            }
        }
    
        Console.WriteLine();
        Console.WriteLine(options.Sum(o => o.Value.Count) + " camo options: " + matched + " wear a numbered texture, "
            + solid + " are a plain colour, " + orphan + " neither");
    
        if (patterns.Count == 0)
            return 0;
    
        Console.WriteLine();
        int found = 0;
    
        foreach (var pattern in patterns.Take(20))
        {
            bool here = names.TryGetValue(pattern.TextureId, out var texture);
            if (here) found++;
    
            Console.WriteLine("  " + pattern.DisplayName.PadRight(28) + "  ->  "
                + (here ? texture : "0x" + pattern.TextureId.ToString("X") + "  (not in these archives)"));
        }
    
        Console.WriteLine();
        Console.WriteLine(patterns.Count + " camo pattern resources, "
            + patterns.Count(p => names.ContainsKey(p.TextureId)) + " find their texture here");
        return 0;
    }

    internal static void Collect(SortedDictionary<int, List<string>> into, int number, string name)
    {
        if (!into.TryGetValue(number, out var named))
            into[number] = named = [];
        if (!named.Contains(name))
            named.Add(name);
    }
    
    // "37-AtacsAT-X" and "37-A-TacsAT-X_DiffuseMap" both start with 37.

    internal static int LeadingNumber(string name)
    {
        int digits = 0;
        while (digits < name.Length && char.IsAsciiDigit(name[digits])) digits++;
    
        return digits > 0 && digits < name.Length && name[digits] == '-'
            ? int.Parse(name[..digits]) : -1;
    }

    internal static int CheckParameters(string forgePath, int limit, string? dumpPath)
    {
        using var archive = ForgeArchive.Open(forgePath);
    
        var outcome = new Dictionary<string, int>();
        var kinds = new Dictionary<string, int>();
        var objects = new Dictionary<string, int>();
        var names = new Dictionary<uint, int>();
        var valueKinds = new Dictionary<uint, SortedSet<string>>();
        int seen = 0, exact = 0, shown = 0;
    
        foreach (var entry in archive.Entries)
        {
            if (seen >= limit) break;
            if (entry.FileExtension != ".data") continue;
    
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
            foreach (var resource in file.Resources.Where(r => r.ClassHash == Material.ClassHash))
            {
                if (seen >= limit) break;
                seen++;
    
                Material material;
                try { material = Material.Read(resource.Data); }
                catch (Exception ex)
                {
                    Bump(outcome, "header unreadable: " + ex.GetType().Name);
                    continue;
                }
    
                if (material.Note.Length > 0)
                {
                    Bump(outcome, "stopped: " + material.Note[..Math.Min(material.Note.Length, 40)]);
                    if (shown < 8)
                    {
                        Console.WriteLine("  " + resource.Name + "  ->  " + material.Note);
                        shown++;
                    }
                }
                else if (material.TrailingBytes == 0)
                {
                    exact++;
                    Bump(outcome, "ends on the last byte");
                }
                else
                {
                    Bump(outcome, "walked, but " + material.TrailingBytes + " bytes left over");
                    if (shown < 8)
                    {
                        Console.WriteLine("  " + resource.Name + "  ->  " + material.TrailingBytes
                            + " bytes left of " + resource.Data.Length);
                        shown++;
                    }
                }
    
                foreach (var parameter in material.Parameters)
                {
                    Bump(names, parameter.Name);
                    Bump(kinds, "0x" + parameter.Kind.ToString("X2"));
                    if (parameter.ObjectClass != 0)
                        Bump(objects, ResourceTypes.NameOf(parameter.ObjectClass));
    
                    // What a name is worth is only half the hash; the other half is
                    // that the value type has to fit it. A "...Color" that turns out
                    // to be a float is a collision, not a name.
                    if (!valueKinds.TryGetValue(parameter.Name, out var seenKinds))
                        valueKinds[parameter.Name] = seenKinds = [];
                    seenKinds.Add(parameter.ObjectClass != 0
                        ? ResourceTypes.NameOf(parameter.ObjectClass)
                        : "0x" + parameter.Kind.ToString("X2"));
                }
            }
        }
    
        Console.WriteLine();
        Console.WriteLine(seen + " materials, " + exact + " end exactly on their last byte ("
            + (seen == 0 ? 0 : exact * 100.0 / seen).ToString("0.00") + " %)");
        Report("how the walk ended:", outcome);
        Report("value kinds:", kinds);
        Report("object classes:", objects);
    
        Console.WriteLine();
        Console.WriteLine(names.Count + " distinct parameter name hashes, "
            + names.Keys.Count(h => !ParameterNames.NameOf(h).StartsWith("0x")) + " of them named");
        foreach (var pair in names.OrderByDescending(p => p.Value).Take(20))
            Console.WriteLine("  " + pair.Value.ToString().PadLeft(7) + "  0x" + pair.Key.ToString("X8")
                + "  " + ParameterNames.NameOf(pair.Key).PadRight(22)
                + string.Join(" ", valueKinds[pair.Key]));
    
        if (dumpPath is not null)
        {
            File.WriteAllLines(dumpPath, names.OrderByDescending(p => p.Value)
                .Select(p => "0x" + p.Key.ToString("X8") + " " + p.Value + " " + string.Join(",", valueKinds[p.Key])));
            Console.WriteLine("wrote " + names.Count + " hashes to " + dumpPath);
        }
    
        return 0;
    }

    internal static int ShowMaterialLinks(string forgePath, string containerName, string materialName)
    {
        using var archive = ForgeArchive.Open(forgePath);
        ForgeEntry? entry = archive.Entries.FirstOrDefault(candidate =>
            candidate.FileExtension == ".data"
            && candidate.Name.Equals(containerName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            Console.WriteLine($"container {containerName} was not found");
            return 1;
        }
    
        DataFile file;
        using (var stream = new MemoryStream(archive.ReadEntry(entry), writable: false))
            file = DataFile.Read(stream);
    
        Resource? resource = file.Resources.FirstOrDefault(candidate =>
            candidate.ClassHash == Material.ClassHash
            && candidate.Name.Equals(materialName, StringComparison.OrdinalIgnoreCase));
        if (resource is null)
        {
            Console.WriteLine($"material {materialName} was not found in {containerName}");
            return 1;
        }
    
        var material = Material.Read(resource.Data);
        var sets = file.Resources.Where(candidate => candidate.ClassHash == TextureSet.ClassHash)
            .Select(candidate => (candidate.Name, Set: TextureSet.Read(candidate.Data)))
            .ToDictionary(candidate => candidate.Set.Id);
    
        Console.WriteLine($"material 0x{material.Id:X}  default set 0x{material.TextureSetId:X}");
        foreach (var set in sets.Values)
        {
            Console.WriteLine($"set 0x{set.Set.Id:X}  {set.Name}");
            foreach (var slot in set.Set.Textures)
                Console.WriteLine($"  {slot.Name,-16} 0x{slot.Id:X}");
        }
    
        Console.WriteLine("parameters:");
        foreach (var parameter in material.Parameters.Where(parameter => parameter.TextureId != 0))
        {
            string name = ParameterNames.NameOf(parameter.Name);
            string match = sets.TryGetValue(parameter.TextureSetId, out var selected)
                ? selected.Set.Textures.FirstOrDefault(slot => slot.Id == parameter.TextureId)?.Name ?? "<not in selected set>"
                : "<set not in container>";
            Console.WriteLine($"  0x{parameter.Name:X8} {name,-18} set 0x{parameter.TextureSetId:X}  "
                + $"texture 0x{parameter.TextureId:X}  slot {match}");
        }
        return 0;
    }

    internal static int CheckMaterials(string forgePath, int limit)
    {
        using var archive = ForgeArchive.Open(forgePath);
        using var archives = new ArchiveSet(archive);
    
        var counts = new Dictionary<string, int>();
        var found = new Dictionary<string, int>();
        int seen = 0, shown = 0;
    
        foreach (var entry in archive.Entries)
        {
            if (seen >= limit) break;
            if (entry.FileExtension != ".data") continue;
    
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
            // What this data file itself holds, which is where a mesh keeps its own
            // materials in nearly every case.
            var classes = new Dictionary<ulong, uint>();
            foreach (var resource in file.Resources)
                classes[resource.Id] = resource.ClassHash;
    
            foreach (var resource in file.Resources.Where(r => r.ClassHash == Mesh.ClassHash))
            {
                if (seen >= limit) break;
    
                Mesh mesh;
                try { mesh = Mesh.Read(resource.Data); }
                catch (Exception ex) { Bump(counts, "unreadable: " + ex.GetType().Name); seen++; continue; }
    
                if (mesh.Data is null) continue;
                seen++;
    
                // Which of the texture sets in this data file the mesh really uses,
                // and how many are left over.
                var setsHere = file.Resources.Where(r => r.ClassHash == TextureSet.ClassHash)
                    .Select(r => r.Id).ToHashSet();
    
                var usedSets = new HashSet<ulong>();
                foreach (ulong materialId in mesh.Materials.Select(m => m.MaterialId))
                {
                    var owner = file.Resources.FirstOrDefault(r => r.Id == materialId
                        && r.ClassHash == Material.ClassHash);
                    if (owner is not null)
                        usedSets.Add(Material.Read(owner.Data).TextureSetId);
                }
    
                int spare = setsHere.Count(id => !usedSets.Contains(id));
                if (setsHere.Count <= 8)
                    Bump(counts, "small file: " + spare + " of " + setsHere.Count + " sets unused");
                int ranges = mesh.Data.Standard.Count;
                Bump(counts, "draw ranges: " + ranges);
                Bump(counts, mesh.Materials.Count == ranges ? "one per draw range"
                    : mesh.Materials.Count + " materials for " + ranges + " ranges");
    
                foreach (ulong id in mesh.Materials.Select(m => m.MaterialId))
                {
                    if (classes.TryGetValue(id, out uint hash))
                    {
                        Bump(found, hash == Material.ClassHash ? "a Material in the same data file"
                            : "something else: " + ResourceTypes.NameOf(hash));
                        continue;
                    }
    
                    // The rest are shared between models and live somewhere else.
                    string what = ClassOf(archives, id);
                    Bump(found, "elsewhere: " + what);
    
                    if (what == "not found at all" && shown < 8)
                    {
                        Console.WriteLine("  " + resource.Name + "  ->  0x" + id.ToString("X")
                            + "   (" + mesh.Materials.Count + " materials)");
                        shown++;
                    }
                }
            }
        }
    
        Console.WriteLine(seen + " meshes with geometry");
        Report("how many materials a mesh lists:", counts);
        Report("what those references point at:", found);
        return 0;
    }
    
    // What class a resource id turns out to be, looked up anywhere in the archives.

    internal static string ClassOf(ArchiveSet archives, ulong id)
    {
        foreach (var found in archives.Candidates(id))
        {
            DataFile file;
            try { using var s = new MemoryStream(found.Data); file = DataFile.Read(s); }
            catch { continue; }
    
            var resource = file.Resources.FirstOrDefault(r => r.Id == id);
            if (resource is not null)
                return ResourceTypes.NameOf(resource.ClassHash);
        }
    
        return "not found at all";
    }
    
    // The tail of a texture name says what it is for. Names often end in the
    // platform they were built for, which has to come off first.

    internal static string KindOf(string name)
    {
        foreach (string platform in new[] { "_PC", "_Orbis", "_Durango" })
        {
            if (name.EndsWith(platform, StringComparison.OrdinalIgnoreCase))
                name = name[..^platform.Length];
        }
    
        int cut = name.LastIndexOf('_');
        return cut < 0 ? name : name[(cut + 1)..];
    }
    
    // Writes one mesh of a data file as a binary FBX file. Most meshes do not
    // share a .data file with their own Skeleton - the fallback to a prebuilt
    // cross-archive index (see skelindex) is what finds those.

    internal static int ListMips(string forgePath, string filter, int count)
    {
        using var archive = ForgeArchive.Open(forgePath);
        using var archives = new ArchiveSet(archive);
    
        int shown = 0, drawn = 0, failed = 0;
    
        foreach (var entry in archive.Entries)
        {
            if (shown >= count)
                break;
    
            if (entry.FileExtension != ".data")
                continue;
    
            if (filter.Length > 0 && !entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;
    
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
            foreach (var resource in file.Resources.Where(r => r.ClassHash == TextureMap.ClassHash))
            {
                if (shown >= count)
                    break;
    
                var texture = TextureMap.Read(resource.Data);
                var mips = TextureMipSet.Collect(texture, archives);
    
                Console.WriteLine();
                Console.WriteLine($"{resource.Name}  {texture.Width}x{texture.Height}  {texture.Format}  " +
                    $"{texture.MipCount} mips  {texture.StreamedMips.Length} streamed  " +
                    $"embedded {texture.Pixels.Length:N0} B");
    
                foreach (var level in mips.Levels)
                {
                    string note = "";
                    if (BlockDecoder.CanDecode(texture.Format))
                    {
                        try
                        {
                            int size = texture.Format.LevelSize(level.Width, level.Height);
                            BlockDecoder.Decode(level.Pixels.AsSpan(0, size), texture.Format, level.Width, level.Height);
                            note = "ok";
                            drawn++;
                        }
                        catch (Exception ex)
                        {
                            note = $"FAILED {ex.GetType().Name}";
                            failed++;
                        }
                    }
    
                    Console.WriteLine($"   mip {level.Level,-2} {level.Size,-13} {level.Pixels.Length,9:N0} B  " +
                        $"{(level.IsStreamed ? "CompiledMip" : "TextureMap "),-12} {note,-8} {(level.IsStreamed ? level.SourceName : "")}");
                }
    
                foreach (ulong missing in mips.MissingStreamed)
                    Console.WriteLine($"   streamed mip 0x{missing:X} not found in this archive");
    
                shown++;
            }
        }
    
        Console.WriteLine();
        Console.WriteLine($"{shown} textures, {drawn} levels decoded, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
    
    // Walks the textures of an archive and groups the ones that cannot be drawn by
    // the reason, so the gaps are countable instead of anecdotal.

    internal static int ShowTextureHeaders(string forgePath, string filter)
    {
        using var archive = ForgeArchive.Open(forgePath);
    
        Console.WriteLine($"{"width",6} {"height",6} {"mips",4} {"topMip",12} {"totalSize",12} {"align",6} {"pixels",12}  name");
    
        foreach (var entry in archive.Entries)
        {
            if (entry.FileExtension != ".data") continue;
            if (!entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
    
            DataFile file;
            try
            {
                using var stream = new MemoryStream(archive.ReadEntry(entry));
                file = DataFile.Read(stream);
            }
            catch { continue; }
    
            foreach (var resource in file.Resources.Where(r => r.ClassHash == TextureMap.ClassHash))
            {
                TextureMap texture;
                try { texture = TextureMap.Read(resource.Data); }
                catch { continue; }
    
                Console.WriteLine($"{texture.Width,6} {texture.Height,6} {texture.MipCount,4} {texture.TopMipSize,12} "
                    + $"{texture.TotalTextureSize,12} {texture.Alignment,6} {texture.Pixels.Length,12}  {resource.Name}");
            }
        }
    
        return 0;
    }

    internal static int ExportTextures(string forgePath, string filter, string outputFolder)
    {
        using var archive = ForgeArchive.Open(forgePath);
        using var archives = new ArchiveSet(archive);
    
        int written = 0, skipped = 0;
    
        foreach (var entry in archive.Entries)
        {
            if (entry.FileExtension != ".data")
                continue;
    
            if (!entry.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || entry.Name.Contains("_Mip"))
                continue;
    
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
            foreach (var resource in file.Resources.Where(r => r.ClassHash == TextureMap.ClassHash))
            {
                TextureView view;
                try
                {
                    view = TextureLoader.FromTexture(resource.Name, TextureMap.Read(resource.Data), archives);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  {resource.Name}: {ex.Message}");
                    skipped++;
                    continue;
                }
    
                var best = view.Mips.Best;
                if (best is null || !view.CanDraw)
                {
                    Console.WriteLine($"  {resource.Name}: {view.Texture.Format} cannot be drawn");
                    skipped++;
                    continue;
                }
    
                TextureExporter.Export(view, [best], ExportFormat.Png, outputFolder, resource.Name);
                Console.WriteLine($"  {best.Width,5} x {best.Height,-5} {view.Texture.Format,-6} {resource.Name}");
                written++;
            }
        }
    
        Console.WriteLine($"{written} textures written to {outputFolder}, {skipped} skipped");
        return skipped > 0 ? 1 : 0;
    }

    internal static int ImportTextures(string forgePath, string inputFolder)
    {
        var images = Directory.GetFiles(inputFolder, "*.png", SearchOption.AllDirectories).OrderBy(x => x).ToList();
        if (images.Count == 0)
        {
            Console.WriteLine($"No png files in {inputFolder}.");
            return 1;
        }
    
        var changes = new ChangeSet();
        int matched = 0, missing = 0;
    
        using (var archive = ForgeArchive.Open(forgePath))
        using (var archives = new ArchiveSet(archive))
        {
            var byName = new Dictionary<string, List<ForgeEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (!byName.TryGetValue(entry.Name, out var sameName))
                    byName[entry.Name] = sameName = [];
                sameName.Add(entry);
            }
    
            foreach (string image in images)
            {
                string name = Path.GetFileNameWithoutExtension(image);
    
                if (!byName.TryGetValue(name, out var candidates))
                {
                    Console.WriteLine($"  {name}: no entry of that name in the archive");
                    missing++;
                    continue;
                }
    
                int copies = 0;
                string summary = "";
    
                foreach (var entry in candidates)
                {
                    using var stream = new MemoryStream(archive.ReadEntry(entry));
                    var file = DataFile.Read(stream);
    
                    int index = file.Resources.FindIndex(r => r.ClassHash == TextureMap.ClassHash && r.Name == name);
                    if (index < 0)
                        continue;
    
                    var resource = file.Resources[index];
                    var location = new Location(forgePath, entry.Index, entry.Name);
    
                    try
                    {
                        var built = TextureImporter.Build(image, TextureMap.Read(resource.Data), resource.Data, location,
                            index, resource.Name, archives, generateMips: true, out summary);
    
                        foreach (var change in built)
                            changes.Set(change);
    
                        copies++;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  {name}: {ex.Message}");
                    }
                }
    
                if (copies == 0)
                {
                    Console.WriteLine($"  {name}: no entry of that name holds a TextureMap");
                    missing++;
                    continue;
                }
    
                Console.WriteLine($"  {name}: {summary}" + (copies > 1 ? $"   ({copies} copies of that name)" : ""));
                matched++;
            }
        }
    
        if (matched == 0)
        {
            Console.WriteLine("Nothing to write.");
            return 1;
        }
    
        var progress = new Progress<string>(Console.WriteLine);
        var plans = changes.Plan(progress);
    
        foreach (var plan in plans)
            Console.WriteLine($"{plan.Name}: {plan.Entries.Count} entries, {(plan.NeedsRebuild ? "rebuild" : "patched in place")}");
    
        var watch = Stopwatch.StartNew();
        changes.Write(plans, progress);
    
        Console.WriteLine($"{matched} textures written in {watch.Elapsed.TotalSeconds:F1} s, {missing} skipped");
        return missing > 0 ? 1 : 0;
    }

    internal static int TexCycle(string forgePath, int count)
    {
        using var archive = ForgeArchive.Open(forgePath);
    
        int seen = 0, ddsSame = 0, resourceSame = 0, skipped = 0, reencoded = 0, fromTop = 0;
        var problems = new List<string>();
    
        foreach (var entry in archive.Entries)
        {
            if (seen >= count)
                break;
    
            if (entry.FileExtension != ".data")
                continue;
    
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
            foreach (var resource in file.Resources.Where(r => r.ClassHash == TextureMap.ClassHash))
            {
                if (seen >= count)
                    break;
    
                TextureMap texture;
                try
                {
                    texture = TextureMap.Read(resource.Data);
                }
                catch
                {
                    continue;
                }
    
                int start = texture.FirstStoredLevel();
                if (start < 0 || !texture.HasPixels || texture.Format == PixelFormat.Unknown || texture.Faces != 1)
                {
                    skipped++;
                    continue;
                }
    
                if (start == 0)
                    fromTop++;
    
                var embedded = TextureMipSet.Collect(texture, null).Levels;
                if (embedded.Count == 0)
                {
                    skipped++;
                    continue;
                }
    
                seen++;
    
                try
                {
                    using var dds = new MemoryStream();
                    DdsWriter.Write(dds, texture.Format, embedded);
    
                    dds.Position = 0;
                    var read = DdsReader.Read(dds);
    
                    bool same = read.Format == texture.Format
                        && read.Width == embedded[0].Width && read.Height == embedded[0].Height
                        && read.MipCount == embedded.Count;
    
                    var levels = new List<byte[]>();
                    for (int level = 0; level < texture.MipCount; level++)
                        levels.Add([]);
    
                    for (int i = 0; i < embedded.Count; i++)
                    {
                        var back = read.Level(i);
                        if (!back.AsSpan().SequenceEqual(embedded[i].Pixels))
                            same = false;
    
                        levels[embedded[i].Level] = back;
                    }
    
                    if (same)
                        ddsSame++;
                    else
                        NoteProblem(problems, $"{resource.Name}: the dds round trip changed the pixels");
    
                    try
                    {
                        var rebuilt = TextureImport.ReplacePixels(resource.Data, texture.PixelOffset,
                            TextureImport.EmbeddedChain(texture, levels, (int)texture.Width, (int)texture.Height));
    
                        if (rebuilt.AsSpan().SequenceEqual(resource.Data))
                            resourceSame++;
                        else
                            NoteProblem(problems, $"{resource.Name}: the rebuilt resource differs");
                    }
                    catch (Exception ex)
                    {
                        NoteProblem(problems, $"{resource.Name}: taking the dds over, {ex.Message}");
                    }
    
                    if (start == 0)
                    {
                        var top = BlockDecoder.Decode(embedded[0].Pixels, texture.Format, embedded[0].Width, embedded[0].Height);
                        var encodedLevels = TextureImport.EncodeLevels(texture, top, embedded[0].Width, embedded[0].Height);
                        var fresh = TextureImport.ReplacePixels(resource.Data, texture.PixelOffset,
                            TextureImport.EmbeddedChain(texture, encodedLevels, (int)texture.Width, (int)texture.Height));
    
                        var reread = TextureMap.Read(fresh);
                        if (reread.Width == texture.Width && reread.Height == texture.Height
                            && reread.MipCount == texture.MipCount && reread.Format == texture.Format
                            && fresh.Length == resource.Data.Length)
                            reencoded++;
                        else
                            NoteProblem(problems, $"{resource.Name}: the re-encoded resource does not read back the same");
                    }
                }
                catch (Exception ex)
                {
                    NoteProblem(problems, $"{resource.Name}: depth {texture.Depth}, {texture.MipCount} mips, type {texture.TextureFormat}, {ex.Message}");
                }
            }
        }
    
        Console.WriteLine($"{seen} textures, {skipped} without an embedded chain and skipped");
        Console.WriteLine($"  {ddsSame} survive the dds round trip unchanged");
        Console.WriteLine($"  {resourceSame} rebuild into a byte identical resource");
        Console.WriteLine($"  {reencoded} of {fromTop} with a full chain survive decode, encode and rebuild");
    
        foreach (string problem in problems.Take(10))
            Console.WriteLine($"  {problem}");
    
        return seen > 0 && resourceSame == seen && ddsSame == seen ? 0 : 1;
    }

    internal static void NoteProblem(List<string> problems, string text)
    {
        if (problems.Count < 50)
            problems.Add(text);
    }

    internal static int Recode(string forgePath, int count)
    {
        using var archive = ForgeArchive.Open(forgePath);
    
        var totals = new Dictionary<PixelFormat, (int Textures, long Pixels, double Error, int Worst, long Identical)>();
        int seen = 0, failed = 0;
        var timer = Stopwatch.StartNew();
    
        foreach (var entry in archive.Entries)
        {
            if (seen >= count)
                break;
    
            if (entry.FileExtension != ".data")
                continue;
    
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
            foreach (var resource in file.Resources.Where(r => r.ClassHash == TextureMap.ClassHash))
            {
                if (seen >= count)
                    break;
    
                TextureMap texture;
                try
                {
                    texture = TextureMap.Read(resource.Data);
                }
                catch
                {
                    continue;
                }
    
                var level = TextureMipSet.Collect(texture, null).Best;
                if (level is null || texture.Format == PixelFormat.Unknown || texture.Faces != 1)
                    continue;
    
                seen++;
    
                try
                {
                    var original = BlockDecoder.Decode(level.Pixels, texture.Format, level.Width, level.Height);
                    var encoded = BlockEncoder.Encode(original, texture.Format, level.Width, level.Height);
                    var again = BlockDecoder.Decode(encoded, texture.Format, level.Width, level.Height);
    
                    double sum = 0;
                    int worst = 0;
                    for (int i = 0; i < original.Length; i++)
                    {
                        int diff = Math.Abs(original[i] - again[i]);
                        sum += diff;
                        if (diff > worst)
                            worst = diff;
                    }
    
                    bool identical = encoded.AsSpan().SequenceEqual(level.Pixels.AsSpan(0, encoded.Length));
    
                    var current = totals.GetValueOrDefault(texture.Format);
                    totals[texture.Format] = (current.Textures + 1, current.Pixels + original.Length,
                        current.Error + sum, Math.Max(current.Worst, worst),
                        current.Identical + (identical ? 1 : 0));
                }
                catch (Exception ex)
                {
                    if (failed++ < 5)
                        Console.WriteLine($"  {resource.Name}: {ex.Message}");
                }
            }
        }
    
        Console.WriteLine($"{seen} textures in {timer.Elapsed.TotalSeconds:0.0} s, {failed} could not be encoded");
        Console.WriteLine();
        Console.WriteLine($"{"format",-22} {"textures",8} {"avg error",10} {"worst",6} {"unchanged",10}");
    
        foreach (var (format, value) in totals.OrderByDescending(x => x.Value.Textures))
        {
            double average = value.Pixels > 0 ? value.Error / value.Pixels : 0;
            Console.WriteLine($"{format,-22} {value.Textures,8} {average,10:0.00} {value.Worst,6} {value.Identical,10}");
        }
    
        return 0;
    }

    internal static int Audit(string forgePath, int count)
    {
        using var archive = ForgeArchive.Open(forgePath);
        using var archives = new ArchiveSet(archive);
    
        var reasons = new Dictionary<string, int>();
        var examples = new Dictionary<string, string>();
        int seen = 0, drawable = 0, cubes = 0, cubesWithMips = 0, compiledMips = 0;
    
        foreach (var entry in archive.Entries)
        {
            if (seen >= count)
                break;
    
            if (entry.FileExtension != ".data")
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
    
            // A CompiledMip is only useful if its TextureMap can be found, which is
            // what the browser needs when one of them is picked.
            foreach (var resource in file.Resources.Where(r => r.ClassHash == CompiledMip.ClassHash))
            {
                compiledMips++;
    
                var mip = CompiledMip.Read(resource.Data);
                if (TextureMipSet.LoadParent(mip, archives) is null)
                    Note(reasons, examples, "CompiledMip without a TextureMap", resource.Name);
            }
    
            foreach (var resource in file.Resources.Where(r => r.ClassHash == TextureMap.ClassHash))
            {
                if (seen >= count)
                    break;
    
                seen++;
    
                TextureMap texture;
                try
                {
                    texture = TextureMap.Read(resource.Data);
                }
                catch (Exception ex)
                {
                    Note(reasons, examples, $"unreadable: {ex.Message}", resource.Name);
                    continue;
                }
    
                // TextureFormat 2 is a cube map, which stores six faces per level.
                if (texture.TextureFormat == 2)
                {
                    cubes++;
                    if (texture.MipCount > 1)
                        cubesWithMips++;
                }
    
                if (!BlockDecoder.CanDecode(texture.Format))
                {
                    Note(reasons, examples, $"format {texture.Format} not decodable", resource.Name);
                    continue;
                }
    
                if (TextureMipSet.Collect(texture, archives).Levels.Count > 0)
                {
                    drawable++;
                    continue;
                }
    
                Note(reasons, examples, texture.HasPixels
                    ? $"chain does not fit ({texture.Pixels.Length} B stored)"
                    : "no pixels here and none streamed in", resource.Name);
            }
        }
    
        Console.WriteLine($"{seen} textures, {drawable} can be drawn, {seen - drawable} cannot");
        Console.WriteLine($"{cubes} cube maps, {cubesWithMips} of them with more than one mip level");
        Console.WriteLine($"{compiledMips} streamed mips seen along the way");
        Console.WriteLine();
    
        foreach (var reason in reasons.OrderByDescending(r => r.Value))
            Console.WriteLine($"  {reason.Value,5}  {reason.Key,-46}  e.g. {examples[reason.Key]}");
    
        return 0;
    }

    internal static void Note(Dictionary<string, int> reasons, Dictionary<string, string> examples, string reason, string name)
    {
        reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
        examples.TryAdd(reason, name);
    }

    internal static int ReadTextures(string path)
    {
        var file = DataFile.Read(path);
        int ok = 0, failed = 0;
    
        foreach (var resource in file.Resources.Where(r => r.ClassHash == TextureMap.ClassHash))
        {
            try
            {
                var texture = TextureMap.Read(resource.Data);
                int first = texture.FirstStoredLevel();
                Console.WriteLine(
                    $"  {texture.Width,5}x{texture.Height,-5} {texture.Format,-8} mips {texture.MipCount,2} " +
                    $"streamed {texture.StreamedMips.Length}  from level {first}  {texture.Pixels.Length,9} B  {resource.Name}");
    
                foreach (var id in texture.StreamedMips)
                    Console.WriteLine($"        streamed mip id 0x{id:X}");
    
                ok++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  FAILED  {resource.Name}: {ex.Message}");
                failed++;
            }
        }
    
        Console.WriteLine($"{ok} textures read, {failed} failed");
        return failed == 0 ? 0 : 1;
    }
}
