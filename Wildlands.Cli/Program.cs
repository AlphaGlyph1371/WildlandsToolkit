using System.Diagnostics;
using System.IO;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using System.Linq;
using System.Collections.Generic;
using Wildlands.Formats.Models;
using Wildlands.Formats.Materials;
using Wildlands.Formats.Textures;
using Wildlands.Formats.Weather;
using Wildlands.Toolkit;

if (args.Length < 2)
{
    Console.WriteLine("usage: wlcli <command> <args>");
    Console.WriteLine("  blobs <file.data>              list the compressed blobs of a data file");
    Console.WriteLine("  dump  <file.data> <outdir>     write each decompressed blob to disk");
    Console.WriteLine("  list  <file.forge> [count]     list forge entries");
    Console.WriteLine("  entry <file.forge> <index> <out>  write one forge entry to disk");
    Console.WriteLine("  res   <file.data> [count]      list the resources inside a data file");
    Console.WriteLine("  tex   <file.data>              read every texture in a data file");
    Console.WriteLine("  mips  <file.forge> [name] [n]  show where each mip level of a texture comes from");
    Console.WriteLine("  get   <file.data> <name> <out>    write one resource to disk");
    Console.WriteLine("  mesh  <file.data> <name>       what a mesh holds");
    Console.WriteLine("  sets  <file.forge> [n]         which textures the texture sets point at");
    Console.WriteLine("  mats  <file.forge> [n]         whether every draw range finds its material");
    Console.WriteLine("  params <file.forge> [n] [out.txt]  walk every material parameter list and count what is in it");
    Console.WriteLine("  camo  <file.forge> [file2.forge...]  match every camo option to the texture it wears");
    Console.WriteLine("  ranges <file.data> <name>      how its index buffer splits over the draw ranges");
    Console.WriteLine("  meshes <file.forge> [n]        count the geometry containers and vertex formats");
    Console.WriteLine("  geometry <file.forge> [n]      decode every mesh and check it against its own header");
    Console.WriteLine("  fbx   <file.data> <name> <out> [cache.bin]  write one mesh as a binary FBX file");
    Console.WriteLine("  objout <file.data> <name> <out.obj>  write one mesh as a Wavefront OBJ file");
    Console.WriteLine("  objin <file.data> <name> <in.obj> <out.data>  replace one mesh's geometry from an OBJ file");
    Console.WriteLine("  objcycle <folder|file.data> [n]  export every mesh to OBJ, read it back and compare the geometry");
    Console.WriteLine("  gltfout <file.data> <name> <out.glb> [cache.bin]  write one mesh as binary glTF, with skin and every uv set");
    Console.WriteLine("  gltfin <file.data> <name> <in.glb> <out.data>  replace one mesh's geometry from a glTF file");
    Console.WriteLine("  gltfcycle <folder|file.data> [n]  export every mesh to glTF, read it back and compare everything");
    Console.WriteLine("  hash  <name> [name...]        the class hash of a type name");
    Console.WriteLine("  hashscan <binary> <hash> [hash...]  resolve CRC32 hashes from strings retained in a binary");
    Console.WriteLine("  audit <file.forge> [n]         count the textures that cannot be shown, and why");
    Console.WriteLine("  recode <file.forge> [n]        decode every texture, encode it again and measure what was lost");
    Console.WriteLine("  texcycle <file.forge> [n]      write every texture out as dds, read it back and rebuild the resource");
    Console.WriteLine("  texout <file.forge> <filter> <outdir>  write every matching texture out as png, top level only");
    Console.WriteLine("  texin  <file.forge> <indir>    read a folder of png back in, subfolders included, and write the archive");
    Console.WriteLine("  guess <list.txt> <file.forge> [file2.forge...]  check candidate names against real hashes");
    Console.WriteLine("  skeletons <file.forge> [n] [cache.bin]  for every skinned mesh, check the matching Skeleton's hierarchy");
    Console.WriteLine("  skelcycle <folder|file.data>   read and rewrite every Skeleton byte for byte");
    Console.WriteLine("  skelcheck <folder|file.data>   check Skeletons against the published Anvil documentation");
    Console.WriteLine("  buildcycle <folder|file.data>  parse and rewrite every BuildTable byte for byte");
    Console.WriteLine("  meshcycle <folder|file.data> [n]  read and rewrite every Mesh byte for byte");
    Console.WriteLine("  skelindex <folder-with-forges> <cache.bin>  index every Skeleton resource across a folder of archives");
    Console.WriteLine("  pack  <file.data> <out.data>   read a data file and write it back, then compare both");
    Console.WriteLine("  setres <file.data> <name> <in.bin> <out.data>  replace one resource and write the data file");
    Console.WriteLine("  putentry <file.forge> <index> <file> [out.forge]  put a file into an archive entry, in place or into a rebuilt copy");
    Console.WriteLine("  rebuild <file.forge> <out.forge>  write every entry again in order, then compare both");
    Console.WriteLine("  timecycle <file.data> <name> [out.txt]  write one time cycle out as editable text");
    Console.WriteLine("  settimecycle <file.data> <name> <in.txt> <out.data>  read that text back and rebuild the data file");
    Console.WriteLine("  weather <file.forge> [n]       read and rewrite every time cycle in an archive, byte for byte");
    Console.WriteLine("  weatherprops <file.forge> [n]  census PropertyPath hashes in time-of-day and weather controllers");
    Console.WriteLine("  profileprops <file.forge> <hash> [hash...]  show the actual curves for selected property hashes");
    Console.WriteLine("  crackprops <file.forge> <source> [source...]  match unknown property hashes only against literal strings or extracted file names");
    Console.WriteLine("  graphicsaudit <file.forge> <out.json>  write a structured inventory of every graphics controller and curve");
    Console.WriteLine("  guessprops <audit.json> [words.txt] [parts]  combine graphics terms and match unknown leaf hashes");
    Console.WriteLine("  graphicsprofile <file.data> <resource> <profile.json> [out.data]  preview or apply a graphics profile");
    Console.WriteLine("  refs  <file.data> <name|0xid>  which resources hold this resource's id");
    Console.WriteLine("  where <game folder> <name> [--all]  which archives hold a resource, and which one the game loads last");
    return 1;
}

switch (args[0])
{
    case "blobs":
        return ReadBlobs(args[1], null);
    case "dump" when args.Length >= 3:
        return ReadBlobs(args[1], args[2]);
    case "list":
        return ListForge(args[1], args.Length >= 3 ? int.Parse(args[2]) : 20);
    case "entry" when args.Length >= 4:
        return WriteEntry(args[1], int.Parse(args[2]), args[3]);
    case "res":
        return ListResources(args[1], args.Length >= 3 ? int.Parse(args[2]) : 20);
    case "tex":
        return ReadTextures(args[1]);
    case "mips":
        return ListMips(args[1], args.Length >= 3 ? args[2] : "", args.Length >= 4 ? int.Parse(args[3]) : 10);
    case "get" when args.Length >= 4:
        return GetResource(args[1], args[2], args[3]);
    case "geometry":
        return CheckGeometry(args[1], args.Length >= 3 ? int.Parse(args[2]) : 2000);
    case "fbx" when args.Length >= 4:
        return ExportFbx(args[1], args[2], args[3], args.Length >= 5 ? args[4] : null);
    case "ranges" when args.Length >= 3:
        return ShowRanges(args[1], args[2]);
    case "meshes":
        return CensusMeshes(args[1], args.Length >= 3 ? int.Parse(args[2]) : 2000);
    case "mesh" when args.Length >= 3:
        return ShowMesh(args[1], args[2]);
    case "sets":
        return CheckTextureSets(args[1], args.Length >= 3 ? int.Parse(args[2]) : 2000);
    case "mats":
        return CheckMaterials(args[1], args.Length >= 3 ? int.Parse(args[2]) : 2000);
    case "camo":
        return MatchCamo(args[1..]);
    case "params":
        return CheckParameters(args[1], args.Length >= 3 ? int.Parse(args[2]) : int.MaxValue,
            args.Length >= 4 ? args[3] : null);
    case "objout" when args.Length >= 4:
        return ExportObj(args[1], args[2], args[3]);
    case "objin" when args.Length >= 5:
        return ImportObj(args[1], args[2], args[3], args[4]);
    case "objcycle":
        return CheckObjRoundTrips(args[1], args.Length >= 3 ? int.Parse(args[2]) : int.MaxValue);
    case "gltfout" when args.Length >= 4:
        return ExportGltf(args[1], args[2], args[3], args.Length >= 5 ? args[4] : null);
    case "gltfin" when args.Length >= 5:
        return ImportGltf(args[1], args[2], args[3], args[4]);
    case "gltfcycle":
        return CheckGltfRoundTrips(args[1], args.Length >= 3 ? int.Parse(args[2]) : int.MaxValue);
    case "hash":
        return HashNames(args[1..]);
    case "hashscan" when args.Length >= 3:
        return ScanHashes(args[1], args[2..]);
    case "audit":
        return Audit(args[1], args.Length >= 3 ? int.Parse(args[2]) : 500);
    case "recode":
        return Recode(args[1], args.Length >= 3 ? int.Parse(args[2]) : 200);
    case "texcycle":
        return TexCycle(args[1], args.Length >= 3 ? int.Parse(args[2]) : 200);
    case "texhdr" when args.Length >= 3:
        return ShowTextureHeaders(args[1], args[2]);
    case "texout" when args.Length >= 4:
        return ExportTextures(args[1], args[2], args[3]);
    case "texin" when args.Length >= 3:
        return ImportTextures(args[1], args[2]);
    case "guess" when args.Length >= 2:
        return GuessTypes(args[1], args[2..]);
    case "skeletons":
        return CheckSkeletons(args[1], args.Length >= 3 ? int.Parse(args[2]) : 20000,
            args.Length >= 4 ? args[3] : null);
    case "skelcycle":
        return CheckSkeletonRoundTrips(args[1]);
    case "skelcheck":
        return CheckSkeletonsAgainstDocs(args[1]);
    case "buildcycle":
        return CheckBuildTableRoundTrips(args[1]);
    case "meshcycle":
        return CheckMeshRoundTrips(args[1], args.Length >= 3 ? int.Parse(args[2]) : int.MaxValue);
    case "skelindex" when args.Length >= 3:
        return BuildSkeletonIndex(args[1], args[2]);
    case "pack" when args.Length >= 3:
        return PackData(args[1], args[2]);
    case "setres" when args.Length >= 5:
        return SetResource(args[1], args[2], args[3], args[4]);
    case "rebuild" when args.Length >= 3:
        return RebuildArchive(args[1], args[2]);
    case "putentry" when args.Length >= 4:
        return PutEntry(args[1], int.Parse(args[2]), args[3], args.Length >= 5 ? args[4] : null);
    case "timecycle" when args.Length >= 3:
        return ShowTimeCycle(args[1], args[2], args.Length >= 4 ? args[3] : null);
    case "settimecycle" when args.Length >= 5:
        return ApplyTimeCycle(args[1], args[2], args[3], args[4]);
    case "weather":
        return CheckTimeCycles(args[1], args.Length >= 3 ? int.Parse(args[2]) : int.MaxValue);
    case "weatherprops":
        return CensusWeatherProperties(args[1], args.Length >= 3 ? int.Parse(args[2]) : int.MaxValue);
    case "profileprops" when args.Length >= 3:
        return ProfileWeatherProperties(args[1], args[2..]);
    case "crackprops" when args.Length >= 3:
        return CrackPropertyNames(args[1], args[2..]);
    case "graphicsaudit" when args.Length >= 3:
        return GraphicsAudit.Write(args[1], args[2]);
    case "guessprops" when args.Length >= 2:
        return PropertyGuesser.Run(args[1], args.Length >= 3 ? args[2] : null,
            args.Length >= 4 ? int.Parse(args[3]) : 3);
    case "graphicsprofile" when args.Length >= 4:
        return ApplyGraphicsProfile(args[1], args[2], args[3], args.Length >= 5 ? args[4] : null);
    case "refs" when args.Length >= 3:
        return FindReferences(args[1], args[2]);
    case "where" when args.Length >= 3:
        return WhereIsResource(args[1], args[2], args.Contains("--all"));
    default:
        Console.WriteLine($"unknown command: {args[0]}");
        return 1;
}

// Writes a data file back out and reads the result again, so that every resource
// can be held against the one it came from. The bytes on disk differ because our
// packer is not the game's, only the content has to survive.
static int PackData(string path, string output)
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
static int SetResource(string path, string name, string input, string output)
{
    var file = DataFile.Read(path);
    var resource = file.Resources.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    if (resource is null)
    {
        Console.WriteLine("no resource named " + name);
        return 1;
    }

    int before = resource.Data.Length;
    resource.Data = File.ReadAllBytes(input);
    file.Write(output);

    Console.WriteLine($"{resource.Name}  {before} -> {resource.Data.Length} bytes, wrote {output}");
    return 0;
}

// Replaces the data of one archive entry without rebuilding the archive.
static int RebuildArchive(string forgePath, string outputPath)
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
static int PutEntry(string forgePath, int index, string input, string? rebuiltPath)
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
static int GetResource(string path, string name, string output)
{
    var file = DataFile.Read(path);
    var resource = file.Resources.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    if (resource is null)
    {
        Console.WriteLine("no resource named " + name);
        return 1;
    }

    File.WriteAllBytes(output, resource.Data);
    Console.WriteLine(ResourceTypes.NameOf(resource.ClassHash) + "  " + resource.Data.Length + " bytes -> " + output);
    return 0;
}

// Counts which geometry container and which vertex format the meshes of an
// archive actually use, so the reader is built for what is there. The trailing
// values of a CompiledMesh are constants, so they say whether the whole chain
// was read at the right offsets.
static int CensusMeshes(string forgePath, int limit)
{
    using var archive = ForgeArchive.Open(forgePath);

    var kinds = new Dictionary<string, int>();
    var formats = new Dictionary<string, int>();
    var notes = new Dictionary<string, int>();
    var tails = new Dictionary<string, int>();
    int seen = 0;

    foreach (var entry in archive.Entries)
    {
        if (seen >= limit) break;
        if (entry.FileExtension != ".data") continue;

        DataFile file;
        try { using var s = new MemoryStream(archive.ReadEntry(entry)); file = DataFile.Read(s); }
        catch { continue; }

        foreach (var resource in file.Resources.Where(r => r.ClassHash == Mesh.ClassHash))
        {
            if (seen >= limit) break;
            seen++;

            try
            {
                var mesh = Mesh.Read(resource.Data);
                Bump(kinds, mesh.Geometry.ToString());

                if (mesh.Geometry == GeometryKind.None) continue;

                Bump(formats, "format " + mesh.VertexFormat + ", stride " + mesh.VertexStride);
                Bump(tails, "platform " + mesh.PlatformVersion + ", sdk " + mesh.SdkVersion
                    + ", scale " + mesh.QuantizationFactor + ", uv " + mesh.UvQuantizationFactor);
            }
            catch (Exception ex)
            {
                Bump(notes, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    Console.WriteLine(seen + " meshes");
    Report("geometry container:", kinds);
    Report("vertex format and stride:", formats);
    Report("trailing constants of the CompiledMesh:", tails);
    Report("notes:", notes);
    return 0;
}

// Decodes the geometry of every mesh in an archive and checks it against what
// the mesh header promises: the positions have to sit inside the extent box and
// the indices have to point at vertices that exist.
static int CheckGeometry(string forgePath, int limit)
{
    using var archive = ForgeArchive.Open(forgePath);

    var results = new Dictionary<string, int>();
    int seen = 0;

    foreach (var entry in archive.Entries)
    {
        if (seen >= limit) break;
        if (entry.FileExtension != ".data") continue;

        DataFile file;
        try { using var s = new MemoryStream(archive.ReadEntry(entry)); file = DataFile.Read(s); }
        catch { continue; }

        foreach (var resource in file.Resources.Where(r => r.ClassHash == Mesh.ClassHash))
        {
            if (seen >= limit) break;

            Mesh mesh;
            try { mesh = Mesh.Read(resource.Data); }
            catch (Exception ex) { Bump(results, "unreadable: " + ex.GetType().Name); seen++; continue; }

            if (mesh.Geometry == GeometryKind.None) continue;
            seen++;

            string what = "format " + mesh.VertexFormat + ", stride " + mesh.VertexStride;
            MeshVertex[] vertices;
            int[] triangles;
            try
            {
                vertices = MeshGeometry.ReadVertices(mesh);
                triangles = MeshGeometry.ReadTriangles(mesh);
            }
            catch (Exception ex)
            {
                Bump(results, what + ": invalid geometry (" + ex.Message + ")");
                continue;
            }

            if (vertices.Length == 0 || triangles.Length == 0)
            {
                Bump(results, what + ": empty");
                continue;
            }

            if (triangles.Any(i => i < 0 || i >= vertices.Length))
            {
                Bump(results, what + ": index out of range");
                continue;
            }

            if (mesh.Data!.Standard.Any(range => !RangeFits(mesh, range)))
            {
                Bump(results, what + ": a draw range runs past its own vertices");
                continue;
            }

            if (vertices.Any(v => !InsideExtent(v.Position, mesh)))
            {
                Bump(results, what + ": position outside the extent");
                continue;
            }

            if (vertices.Any(v => Math.Abs(v.Normal.Length() - 1) > 0.1f))
            {
                Bump(results, what + ": normal not unit length");
                continue;
            }

            if (vertices.Any(v => v.Uv.Length > 0 && (Math.Abs(v.Uv[0].X) > 64 || Math.Abs(v.Uv[0].Y) > 64)))
            {
                Bump(results, what + ": texture coordinate far outside the map");
                continue;
            }

            Bump(results, what + ": ok");
        }
    }

    Console.WriteLine(seen + " meshes with geometry");
    Report("outcome:", results);
    return 0;
}

// In a clustered mesh the indices of a draw range have to stay inside the
// stretch of vertices the range claims.
static bool RangeFits(Mesh mesh, MeshPrimitive range)
{
    for (int i = 0; i < range.TriangleCount * 3; i++)
    {
        if (Index(mesh, range.StartIndex + i) >= range.VertexCount)
            return false;
    }

    return true;
}

// The extent box is padded, so a vertex only has to stay inside it.
static bool InsideExtent(System.Numerics.Vector3 p, Mesh mesh)
{
    const float slack = 0.01f;

    for (int axis = 0; axis < 3; axis++)
    {
        float value = axis == 0 ? p.X : axis == 1 ? p.Y : p.Z;
        float size = mesh.ExtentMax[axis] - mesh.ExtentMin[axis];

        if (value < mesh.ExtentMin[axis] - size * slack || value > mesh.ExtentMax[axis] + size * slack)
            return false;
    }

    return true;
}

static void Report<T>(string title, Dictionary<T, int> counter) where T : notnull
{
    if (counter.Count == 0) return;

    Console.WriteLine();
    Console.WriteLine(title);
    foreach (var pair in counter.OrderByDescending(p => p.Value).Take(15))
        Console.WriteLine("  " + pair.Value.ToString().PadLeft(6) + "  " + pair.Key);
}

static void Bump<T>(Dictionary<T, int> counter, T key) where T : notnull
{
    counter[key] = counter.GetValueOrDefault(key) + 1;
}

static int ShowMesh(string path, string name)
{
    var file = DataFile.Read(path);
    var resource = file.Resources.FirstOrDefault(r => r.ClassHash == Mesh.ClassHash
        && r.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    if (resource is null)
    {
        Console.WriteLine("no mesh matching " + name);
        return 1;
    }

    var mesh = Mesh.Read(resource.Data);

    Console.WriteLine(resource.Name + "  " + resource.Data.Length + " bytes");
    Console.WriteLine("  submeshes " + mesh.SubMeshIds.Count + "  bones " + mesh.Bones.Count
        + "  descriptor 0x" + mesh.DescriptorMask.ToString("X2"));
    Console.WriteLine("  extent    " + Triple(mesh.ExtentMin) + "   to   " + Triple(mesh.ExtentMax));

    Console.WriteLine("  geometry  " + mesh.Geometry + "  format " + mesh.VertexFormat
        + "  stride " + mesh.VertexStride + "  indices " + (mesh.Data.Indices32Bit ? 32 : 16) + " bit");
    Console.WriteLine("  scale     " + mesh.QuantizationFactor + "  uv " + mesh.UvQuantizationFactor
        + "  platform " + mesh.PlatformVersion + "  sdk " + mesh.SdkVersion);
    Console.WriteLine("  vertices  " + (mesh.VertexStride > 0 ? mesh.VertexBuffer.Length / mesh.VertexStride : 0)
        + "  (" + mesh.VertexBuffer.Length + " bytes)");
    Console.WriteLine("  indices   " + mesh.IndexBuffer.Length / 2 + "  (" + mesh.IndexBuffer.Length + " bytes)");

    if (mesh.Clustered is { } clustered)
        Console.WriteLine("  clusters  " + clustered.ClusterCount + " over " + clustered.DrawPrimitiveCount
            + " draw ranges, " + clustered.PrimitiveDescriptions.Length + " bytes of descriptions"
            + (clustered.FixedClusterSize ? ", fixed size" : ""));

    var vertices = MeshGeometry.ReadVertices(mesh);
    var triangles = MeshGeometry.ReadTriangles(mesh);

    if (vertices.Length > 0)
    {
        Console.WriteLine("  decoded   x " + Range(vertices.Select(v => v.Position.X))
            + "  y " + Range(vertices.Select(v => v.Position.Y))
            + "  z " + Range(vertices.Select(v => v.Position.Z)));
        Console.WriteLine("  uv        u " + Range(vertices.Where(v => v.Uv.Length > 0).Select(v => v.Uv[0].X))
            + "  v " + Range(vertices.Where(v => v.Uv.Length > 0).Select(v => v.Uv[0].Y))
            + "  normals " + Range(vertices.Select(v => v.Normal.Length())));
        Console.WriteLine("  triangles " + triangles.Length / 3 + "  indices " + Range(triangles.Select(i => (float)i)));
    }

    Console.WriteLine("  materials " + string.Join(", ", mesh.Materials.Select(m => "0x" + m.MaterialId.ToString("X"))));
    Console.WriteLine("  draws     " + mesh.Data.Standard.Count + " standard, " + mesh.Data.Shadow.Count + " shadow");

    foreach (var p in mesh.Data.Standard)
        Console.WriteLine("    index " + p.StartIndex + " +" + p.TriangleCount * 3
            + "   vertex " + p.MinIndex + " +" + p.VertexCount + "   type " + p.Type);

    return 0;
}

static string Range(IEnumerable<float> values) =>
    values.Min().ToString("0.###") + ".." + values.Max().ToString("0.###");

static string Triple(float[] v) =>
    v[0].ToString("0.###") + " " + v[1].ToString("0.###") + " " + v[2].ToString("0.###");

// Reads every texture set of an archive and says what the filled slots point
// at. A slot is only known by its position, so the proof that the order is
// right is that the names of the textures line up with it.
static int CheckTextureSets(string forgePath, int limit)
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
static int MatchCamo(string[] forgePaths)
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

static void Collect(SortedDictionary<int, List<string>> into, int number, string name)
{
    if (!into.TryGetValue(number, out var named))
        into[number] = named = [];
    if (!named.Contains(name))
        named.Add(name);
}

// "37-AtacsAT-X" and "37-A-TacsAT-X_DiffuseMap" both start with 37.
static int LeadingNumber(string name)
{
    int digits = 0;
    while (digits < name.Length && char.IsAsciiDigit(name[digits])) digits++;

    return digits > 0 && digits < name.Length && name[digits] == '-'
        ? int.Parse(name[..digits]) : -1;
}

static int CheckParameters(string forgePath, int limit, string? dumpPath)
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

        DataFile file;
        try { using var s = new MemoryStream(archive.ReadEntry(entry)); file = DataFile.Read(s); }
        catch { continue; }

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

static int CheckMaterials(string forgePath, int limit)
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

        DataFile file;
        try { using var s = new MemoryStream(archive.ReadEntry(entry)); file = DataFile.Read(s); }
        catch { continue; }

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
static string ClassOf(ArchiveSet archives, ulong id)
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
static string KindOf(string name)
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
static int ExportFbx(string path, string name, string output, string? cachePath)
{
    var file = DataFile.Read(path);
    var resource = file.Resources.FirstOrDefault(r => r.ClassHash == Mesh.ClassHash
        && r.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    if (resource is null)
    {
        Console.WriteLine("no mesh matching " + name);
        return 1;
    }

    var mesh = Mesh.Read(resource.Data);
    if (mesh.Geometry == GeometryKind.None)
    {
        Console.WriteLine(resource.Name + " has no geometry to write");
        return 1;
    }

    var skeleton = FindSkeleton(file.Resources, mesh.Bones);
    if (skeleton is null && cachePath is not null)
        skeleton = SkeletonIndex.Load(cachePath)?.FindBest(mesh.Bones.Select(b => b.Name));

    FbxWriter.Write(mesh, resource.Name, output, skeleton);

    Console.WriteLine(resource.Name + " -> " + output + "  ("
        + MeshGeometry.ReadVertices(mesh).Length + " vertices, "
        + MeshGeometry.ReadTriangles(mesh).Length / 3 + " triangles)"
        + (skeleton is null ? "" : ", skeleton with " + skeleton.Count + " bones"));
    return 0;
}

// Scans every .forge in a folder for Skeleton resources and saves the
// result, so fbx/skeletons do not have to re-scan the whole game for every
// mesh that does not bundle its own Skeleton.
static int BuildSkeletonIndex(string folder, string cachePath)
{
    var archives = Directory.GetFiles(folder, "*.forge").Select(ForgeArchive.Open).ToList();
    try
    {
        var index = SkeletonIndex.Build(archives, name => Console.WriteLine("  " + name));
        index.Save(cachePath);
        Console.WriteLine(index.SkeletonCount + " skeleton(s) indexed -> " + cachePath);
        return 0;
    }
    finally
    {
        foreach (var archive in archives)
            archive.Dispose();
    }
}

// For every skinned mesh in an archive: does a Skeleton resource turn up
// next to it (or, failing that, in the prebuilt cross-archive index), and if
// so, does that Skeleton's own hierarchy hold up (one root, no cycles)? A
// mesh that finds no Skeleton at all still exports fine - it just falls back
// to the old flat, parent-less layout - so that is counted separately from
// an actual structural problem in a Skeleton that WAS found.
static int CheckSkeletons(string forgePath, int limit, string? cachePath)
{
    using var archive = ForgeArchive.Open(forgePath);
    var cache = cachePath is null ? null : SkeletonIndex.Load(cachePath);

    var results = new Dictionary<string, int>();
    int seen = 0;

    foreach (var entry in archive.Entries)
    {
        if (seen >= limit) break;
        if (entry.FileExtension != ".data") continue;

        List<Resource> resources;
        try
        {
            using var stream = new MemoryStream(archive.ReadEntry(entry));
            resources = DataFile.Read(stream).Resources;
        }
        catch { continue; }

        foreach (var meshRes in resources.Where(r => r.ClassHash == Mesh.ClassHash))
        {
            if (seen >= limit) break;

            Mesh mesh;
            try { mesh = Mesh.Read(meshRes.Data); } catch { continue; }
            if (mesh.Geometry == GeometryKind.None || mesh.Bones.Count == 0) continue;

            seen++;

            string source = "same file";
            var skeleton = FindSkeleton(resources, mesh.Bones);
            if (skeleton is null && cache is not null)
            {
                skeleton = cache.FindBest(mesh.Bones.Select(b => b.Name));
                source = "cross-archive index";
            }

            if (skeleton is null)
            {
                Bump(results, "no matching Skeleton resource found anywhere (flat export)");
                continue;
            }

            int roots = skeleton.Count(b => b.ParentIndex < 0);
            bool cycleFree = true;
            foreach (var b in skeleton)
            {
                int cur = b.ParentIndex, steps = 0;
                while (cur >= 0)
                {
                    cur = skeleton[cur].ParentIndex;
                    if (++steps > skeleton.Count) { cycleFree = false; break; }
                }
                if (!cycleFree) break;
            }

            if (roots != 1)
                Bump(results, "Skeleton found (" + source + "), but " + roots + " roots instead of 1");
            else if (!cycleFree)
                Bump(results, "Skeleton found (" + source + "), but has a cycle");
            else
            {
                int resolved = mesh.Bones.Count(mb => skeleton.Any(sb => sb.Name == mb.Name));
                Bump(results, resolved == mesh.Bones.Count
                    ? "Skeleton found (" + source + "), every mesh bone resolved, hierarchy ok"
                    : "Skeleton found (" + source + "), hierarchy ok, but only " + resolved + "/" + mesh.Bones.Count + " mesh bones resolved");
            }
        }
    }

    Console.WriteLine(seen + " skinned meshes");
    Report("outcome:", results);
    return 0;
}

// A Mesh carries no reference of its own to the Skeleton it was rigged
// against, so every Skeleton resource sitting next to it is tried and the
// one whose bones actually overlap the mesh's own (matched by their shared
// name hash) wins - a mesh piece usually only uses a slice of a full
// character rig, so this only needs one bone in common to be worth using.
static List<SkeletonBone>? FindSkeleton(
    List<Resource> siblings, List<MeshBone> meshBones)
{
    var meshHashes = meshBones.Select(b => b.Name).ToHashSet();

    List<SkeletonBone>? best = null;
    int bestOverlap = 0;

    foreach (var candidate in siblings.Where(r => r.ClassHash == Skeleton.ClassHash))
    {
        List<SkeletonBone> bones;
        try { bones = Skeleton.Read(candidate.Data); }
        catch { continue; }

        int overlap = bones.Count(b => meshHashes.Contains(b.Name));
        if (overlap > bestOverlap)
        {
            bestOverlap = overlap;
            best = bones;
        }
    }

    return best;
}

// Shows how the index buffer of a mesh is split over its draw ranges, which is
// where a clustered mesh keeps its own numbering.
static int ShowRanges(string path, string name)
{
    var file = DataFile.Read(path);
    var resource = file.Resources.FirstOrDefault(r => r.ClassHash == Mesh.ClassHash
        && r.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    if (resource is null)
    {
        Console.WriteLine("no mesh matching " + name);
        return 1;
    }

    var mesh = Mesh.Read(resource.Data);
    int vertexCount = mesh.VertexStride > 0 ? mesh.VertexBuffer.Length / mesh.VertexStride : 0;
    Console.WriteLine(resource.Name + "  " + vertexCount + " vertices, "
        + mesh.IndexBuffer.Length / 2 + " indices");

    foreach (var kind in new[] { "standard", "shadow" })
    {
        foreach (var range in kind == "standard" ? mesh.Data!.Standard : mesh.Data!.Shadow)
        {
            var indices = new List<int>();
            int degenerate = 0;

            for (int t = 0; t < range.TriangleCount; t++)
            {
                int a = Index(mesh, range.StartIndex + t * 3);
                int b = Index(mesh, range.StartIndex + t * 3 + 1);
                int c = Index(mesh, range.StartIndex + t * 3 + 2);

                indices.Add(a); indices.Add(b); indices.Add(c);
                if (a == b || b == c || a == c) degenerate++;
            }

            if (indices.Count == 0) continue;

            Console.WriteLine("  " + kind.PadRight(9)
                + "min " + range.MinIndex.ToString().PadLeft(6)
                + "  verts " + range.VertexCount.ToString().PadLeft(6)
                + "  start " + range.StartIndex.ToString().PadLeft(6)
                + "  tris " + range.TriangleCount.ToString().PadLeft(6)
                + "  local " + indices.Min().ToString().PadLeft(6) + ".." + indices.Max().ToString().PadLeft(6)
                + (indices.Max() < range.VertexCount ? "  fits" : "  OVERRUNS ITS VERTICES")
                + "  degenerate " + degenerate);
        }
    }

    return 0;
}

static int Index(Mesh mesh, int position) =>
    mesh.Data.Indices32Bit
        ? BitConverter.ToInt32(mesh.IndexBuffer, position * 4)
        : BitConverter.ToUInt16(mesh.IndexBuffer, position * 2);

// CRC32 of a name, which is how class hashes are formed.
static int HashNames(string[] names)
{
    foreach (var name in names)
        Console.WriteLine("0x" + ResourceTypes.Crc32(name).ToString("X8") + "  " + name);

    return 0;
}

// CRC32 cannot be run backwards, so the only way from a hash to a name is the
// other direction: hash a candidate and see whether the result is a class hash
// that actually turns up somewhere in the game. This hashes every line of a
// text file (blank lines and lines starting with # are skipped, so the file
// can carry its own notes) and checks each one against every class hash seen
// across the given archives. A hit that ResourceTypes does not already know
// is a genuine new find, worth adding to KnownNames in ResourceTypes.cs.
static int GuessTypes(string listPath, string[] forgePaths)
{
    if (forgePaths.Length == 0)
    {
        Console.WriteLine("usage: wlcli guess <list.txt> <file.forge> [file2.forge...]");
        Console.WriteLine("  needs at least one archive to check the candidates against");
        return 1;
    }

    var candidates = File.ReadAllLines(listPath)
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith('#'))
        .Distinct()
        .ToList();

    var seen = new Dictionary<uint, int>();

    foreach (string forgePath in forgePaths)
    {
        using var archive = ForgeArchive.Open(forgePath);

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
                seen[resource.ClassHash] = seen.GetValueOrDefault(resource.ClassHash) + 1;
        }
    }

    Console.WriteLine(candidates.Count + " candidate(s), " + seen.Count + " distinct class hash(es) seen across "
        + forgePaths.Length + " archive(s)");
    Console.WriteLine();

    int newHits = 0, alreadyKnown = 0;

    foreach (string candidate in candidates)
    {
        uint hash = ResourceTypes.Crc32(candidate);
        if (!seen.TryGetValue(hash, out int count))
            continue;

        if (ResourceTypes.NameOf(hash) == candidate)
        {
            alreadyKnown++;
            continue;
        }

        newHits++;
        Console.WriteLine("HIT   " + candidate.PadRight(36) + "0x" + hash.ToString("X8")
            + "   seen " + count + " time(s)");
    }

    Console.WriteLine();
    Console.WriteLine(newHits + " new match(es), " + alreadyKnown + " already in ResourceTypes.KnownNames, "
        + (candidates.Count - newHits - alreadyKnown) + " no match");

    return 0;
}

static int WriteEntry(string path, int index, string output)
{
    using var archive = ForgeArchive.Open(path);
    var entry = archive.Entries.First(e => e.Index == index);
    File.WriteAllBytes(output, archive.ReadEntry(entry));
    Console.WriteLine($"{entry.Name}{entry.FileExtension} -> {output} ({entry.Length} bytes)");
    return 0;
}

static int ListResources(string path, int count)
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
static int ListMips(string forgePath, string filter, int count)
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
static int ShowTextureHeaders(string forgePath, string filter)
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

static int ExportTextures(string forgePath, string filter, string outputFolder)
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

static int ImportTextures(string forgePath, string inputFolder)
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

static int TexCycle(string forgePath, int count)
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

static void NoteProblem(List<string> problems, string text)
{
    if (problems.Count < 50)
        problems.Add(text);
}

static int Recode(string forgePath, int count)
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

static int Audit(string forgePath, int count)
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

static void Note(Dictionary<string, int> reasons, Dictionary<string, string> examples, string reason, string name)
{
    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
    examples.TryAdd(reason, name);
}

static int ReadTextures(string path)
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

static int ListForge(string path, int count)
{
    var watch = Stopwatch.StartNew();
    using var archive = ForgeArchive.Open(path);
    watch.Stop();

    Console.WriteLine($"{Path.GetFileName(path)}  version {archive.Version}  {archive.Entries.Count} entries  ({watch.ElapsedMilliseconds} ms)");

    foreach (var entry in archive.Entries.Take(count))
        Console.WriteLine($"  {entry.Index,6}  {entry.Length,10}  0x{entry.Id:X12}  {entry.Name}{entry.FileExtension}");

    return 0;
}

static int ReadBlobs(string path, string? outputDirectory)
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
static int ShowTimeCycle(string path, string name, string? output)
{
    var file = DataFile.Read(path);
    var resource = file.Resources.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    if (resource is null)
    {
        Console.WriteLine("no resource named " + name);
        return 1;
    }

    var cycle = TimeCycle.Read(resource.Data);
    string text = TimeCycleText.Write(cycle, name);

    if (output is null)
        Console.Write(text);
    else
    {
        File.WriteAllText(output, text);
        Console.WriteLine($"{cycle.Entries.Count} entries -> {output}");
    }
    return 0;
}

// The way back in. The text only carries values, everything structural comes from
// the resource that is already there, so a patch cannot break the layout - the
// worst it can do is set a number the game does not like.
static int ApplyTimeCycle(string path, string name, string input, string output)
{
    var file = DataFile.Read(path);
    var resource = file.Resources.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    if (resource is null)
    {
        Console.WriteLine("no resource named " + name);
        return 1;
    }

    var cycle = TimeCycle.Read(resource.Data);
    int changed = TimeCycleText.Apply(cycle, File.ReadAllLines(input));

    resource.Data = cycle.Write();
    file.Write(output);

    Console.WriteLine($"{changed} of {cycle.Entries.Count} entries changed -> {output}");
    return 0;
}

static int CheckSkeletonsAgainstDocs(string path)
{
    (string Name, uint Hash)[] documented =
    [
        ("Skeleton", 0x24AECB7C), ("Bone", 0x95741049), ("BoneHandle", 0xC11EA419),
        ("Reference", 0x2C52CBB0), ("Hips", 0xDED10611), ("Head", 0x07C159A2),
        ("Neck1", 0xB05FD12B), ("LeftShoulder", 0x2D4660A8), ("LeftArm", 0xEB830ADA),
        ("LeftForeArm", 0x89B93A80), ("LeftHand", 0xB675F36C), ("RightHand", 0x75F94D30),
        ("wb-gunroot", 0), ("wb-HandRight", 0x1BD2DF4E), ("wb-HandLeft", 0x68D6F1C1),
    ];

    Console.WriteLine("CRC32 of names the documentation pins down:");
    int hashFailures = 0;

    foreach (var (name, expected) in documented)
    {
        if (expected == 0) continue;
        uint actual = ResourceTypes.Crc32(name);
        bool same = actual == expected;
        if (!same) hashFailures++;
        Console.WriteLine($"  {(same ? "ok  " : "FAIL")}  {name,-14} 0x{actual:X8}" + (same ? "" : $"  documented 0x{expected:X8}"));
    }

    var paths = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.data") : [path];
    var tags = new Dictionary<string, int>();
    int seen = 0, subtreeOk = 0, subtreeDescendants = 0, subtreeBad = 0, singleRoot = 0, multiRoot = 0;
    bool sawAverage = false;

    foreach (string each in paths)
    {
        DataFile file;
        try { file = DataFile.Read(each); }
        catch { continue; }

        foreach (var resource in file.Resources.Where(r => r.ClassHash == Skeleton.ClassHash))
        {
            SkeletonAsset asset;
            try { asset = Skeleton.ReadAsset(resource.Data); }
            catch { continue; }

            seen++;
            foreach (var bone in asset.Bones)
            {
                Bump(tags, "parent pointer tag " + bone.ParentPointerTag);
                Bump(tags, "mirror pointer tag " + bone.MirrorPointerTag);
            }

            if (SubtreeSizesMatch(asset, includingSelf: true)) subtreeOk++;
            else if (SubtreeSizesMatch(asset, includingSelf: false)) subtreeDescendants++;
            else subtreeBad++;
            int roots = asset.Bones.Count(b => b.ParentIndex < 0);
            if (roots == 1) singleRoot++; else multiRoot++;

            if (resource.Name.Contains("GR_PCF_Skeleton_Average", StringComparison.OrdinalIgnoreCase) && !sawAverage)
            {
                sawAverage = true;
                ReportAverageRig(resource.Name, resource.Data.Length, asset, documented);
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine($"{seen} Skeleton(s) read");
    Console.WriteLine($"  ChildrenCount = subtree including self : {subtreeOk}");
    Console.WriteLine($"  ChildrenCount = descendants only       : {subtreeDescendants}");
    Console.WriteLine($"  neither                               : {subtreeBad}");
    Console.WriteLine($"  exactly one root                     : {singleRoot} yes, {multiRoot} no");
    Report("object-pointer tags actually seen:", tags);

    if (!sawAverage)
        Console.WriteLine("GR_PCF_Skeleton_Average was not in this path, so its numbers were not checked.");

    return hashFailures == 0 && subtreeBad == 0 ? 0 : 1;
}

static int[] SubtreeSizes(SkeletonAsset asset)
{
    var size = new int[asset.Bones.Count];

    for (int i = asset.Bones.Count - 1; i >= 0; i--)
    {
        size[i]++;
        int parent = asset.Bones[i].ParentIndex;
        if (parent >= 0)
            size[parent] += size[i];
    }

    return size;
}

static bool SubtreeSizesMatch(SkeletonAsset asset, bool includingSelf)
{
    var size = SubtreeSizes(asset);

    for (int i = 0; i < asset.Bones.Count; i++)
        if (asset.Bones[i].ChildrenCount != size[i] - (includingSelf ? 0 : 1))
            return false;

    return true;
}

static void ReportAverageRig(string name, int bytes, SkeletonAsset asset, (string Name, uint Hash)[] documented)
{
    Console.WriteLine();
    Console.WriteLine($"{name}, the rig the documentation describes:");
    Console.WriteLine($"  size          {bytes} bytes          (documented 14006)");
    Console.WriteLine($"  bones         {asset.Bones.Count}                    (documented 100)");
    Console.WriteLine($"  roots         {asset.Bones.Count(b => b.ParentIndex < 0)}                      (documented 1)");

    int deepest = 0;
    foreach (var bone in asset.Bones)
    {
        int depth = 0;
        for (int at = bone.ParentIndex; at >= 0; at = asset.Bones[at].ParentIndex) depth++;
        deepest = Math.Max(deepest, depth);
    }
    Console.WriteLine($"  depth         {deepest}                     (documented 12)");

    float low = float.MaxValue, high = float.MinValue;
    foreach (var bone in asset.Bones)
    {
        low = Math.Min(low, bone.GlobalPosition.Z);
        high = Math.Max(high, bone.GlobalPosition.Z);
    }
    Console.WriteLine($"  height in Z   {high - low:0.###} m               (documented 1.77 m, Z up)");

    foreach (var (label, hash) in documented)
    {
        if (hash == 0 || label is "Skeleton" or "Bone" or "BoneHandle" || label.StartsWith("wb-")) continue;
        int index = asset.Bones.FindIndex(b => b.Name == hash);
        string where = index < 0 ? "not in this rig" : $"index {index}, global z {asset.Bones[index].GlobalPosition.Z:0.###}";
        Console.WriteLine($"  {label,-14} {where}");
    }

    int hand = asset.Bones.FindIndex(b => b.Name == 0xB675F36C);
    if (hand >= 0)
    {
        var chain = new List<string>();
        for (int at = hand; at >= 0; at = asset.Bones[at].ParentIndex)
            chain.Add(ResourceTypes.NameOf(asset.Bones[at].Name) is { Length: > 0 } named && !named.StartsWith("0x")
                ? named : "#" + at);
        Console.WriteLine("  LeftHand chain up to the root: " + string.Join(" <- ", chain));
    }
}

static int CheckSkeletonRoundTrips(string path)
{
    var paths = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.data") : [path];
    int seen = 0;
    int failed = 0;
    int bones = 0;
    var problems = new Dictionary<string, int>();

    foreach (string each in paths)
    {
        DataFile file;
        try { file = DataFile.Read(each); }
        catch { continue; }

        foreach (var resource in file.Resources.Where(r => r.ClassHash == Skeleton.ClassHash))
        {
            seen++;
            try
            {
                var asset = Skeleton.ReadAsset(resource.Data);
                byte[] rebuilt = Skeleton.Write(asset);
                if (!rebuilt.AsSpan().SequenceEqual(resource.Data))
                {
                    failed++;
                    Bump(problems, "roundtrip differs at 0x" + FirstDifference(resource.Data, rebuilt).ToString("X"));
                    continue;
                }
                bones += asset.Bones.Count;
            }
            catch (Exception ex)
            {
                failed++;
                Bump(problems, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    Console.WriteLine($"{seen} Skeleton(s), {failed} failure(s), {bones} bone(s) read");
    if (problems.Count > 0)
        Report("problems:", problems);
    return failed == 0 ? 0 : 1;
}

static Resource? FindMesh(DataFile file, string name)
{
    var resource = file.Resources.FirstOrDefault(r => r.ClassHash == Mesh.ClassHash
        && r.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

    if (resource is null)
        Console.WriteLine("no mesh matching " + name);

    return resource;
}

static int ExportObj(string path, string name, string output)
{
    var file = DataFile.Read(path);
    if (FindMesh(file, name) is not { } resource)
        return 1;

    var mesh = Mesh.Read(resource.Data);
    if (mesh.Geometry == GeometryKind.None)
    {
        Console.WriteLine(resource.Name + " has no geometry to write");
        return 1;
    }

    ObjFile.Write(mesh, resource.Name, output);
    Console.WriteLine(resource.Name + " -> " + output + "  ("
        + MeshGeometry.ReadVertices(mesh).Length + " vertices, "
        + MeshGeometry.ReadTriangles(mesh).Length / 3 + " triangles, "
        + mesh.Data.Standard.Count + " draw range(s))");
    return 0;
}

static int ImportObj(string path, string name, string input, string output)
{
    var file = DataFile.Read(path);
    if (FindMesh(file, name) is not { } resource)
        return 1;

    var mesh = Mesh.Read(resource.Data);
    var obj = ObjFile.Read(input);
    var result = MeshImport.Replace(mesh, obj);

    resource.Data = mesh.Write();
    Mesh.Read(resource.Data);

    file.Write(output);
    var written = DataFile.Read(output);
    var check = written.Resources.FirstOrDefault(r => r.Id == resource.Id && r.ClassHash == Mesh.ClassHash);

    if (check is null || !check.Data.AsSpan().SequenceEqual(resource.Data))
        throw new InvalidDataException("The written data file does not hold the mesh that was just built.");

    Console.WriteLine($"{resource.Name} <- {input}");
    Console.WriteLine($"  {result.Vertices} vertices, {result.Triangles} triangles, {result.Ranges} draw range(s)");
    Console.WriteLine($"  scale {result.QuantizationFactor:0.####}, uv {result.UvQuantizationFactor:0.####}"
        + (result.TransferredSkinning ? ", joint weights taken from the nearest original vertex" : ""));
    Console.WriteLine("written -> " + output + "  (reopened and verified)");
    return 0;
}

static int ExportGltf(string path, string name, string output, string? cachePath)
{
    var file = DataFile.Read(path);
    if (FindMesh(file, name) is not { } resource)
        return 1;

    var mesh = Mesh.Read(resource.Data);
    if (mesh.Geometry == GeometryKind.None)
    {
        Console.WriteLine(resource.Name + " has no geometry to write");
        return 1;
    }

    var skeleton = FindSkeleton(file.Resources, mesh.Bones);
    if (skeleton is null && cachePath is not null)
        skeleton = SkeletonIndex.Load(cachePath)?.FindBest(mesh.Bones.Select(b => b.Name));

    GltfFile.Write(mesh, resource.Name, output, skeleton);
    var layout = VertexLayout.For(mesh.VertexFormat, mesh.VertexStride);

    Console.WriteLine(resource.Name + " -> " + output);
    Console.WriteLine($"  {MeshGeometry.ReadVertices(mesh).Length} vertices, {mesh.Data.Standard.Count} primitive(s), "
        + $"{layout.UvCount} uv set(s)" + (layout.HasColor ? ", vertex colours" : "")
        + (layout.IsSkinned ? $", skin over {mesh.Bones.Count} bones"
            + (skeleton is null ? " without a hierarchy" : $" with their hierarchy from {skeleton.Count} skeleton bones") : ""));
    return 0;
}

static int ImportGltf(string path, string name, string input, string output)
{
    var file = DataFile.Read(path);
    if (FindMesh(file, name) is not { } resource)
        return 1;

    var mesh = Mesh.Read(resource.Data);
    var result = MeshImport.Replace(mesh, GltfFile.Read(input));

    resource.Data = mesh.Write();
    Mesh.Read(resource.Data);
    file.Write(output);

    var written = DataFile.Read(output);
    var check = written.Resources.FirstOrDefault(r => r.Id == resource.Id && r.ClassHash == Mesh.ClassHash);

    if (check is null || !check.Data.AsSpan().SequenceEqual(resource.Data))
        throw new InvalidDataException("The written data file does not hold the mesh that was just built.");

    Console.WriteLine($"{resource.Name} <- {input}");
    Console.WriteLine($"  {result.Vertices} vertices, {result.Triangles} triangles, {result.Ranges} draw range(s)");
    Console.WriteLine($"  scale {result.QuantizationFactor:0.####}, uv {result.UvQuantizationFactor:0.####}"
        + (result.CarriedSkinning ? ", joint weights taken from the file" : "")
        + (result.TransferredSkinning ? ", joint weights taken from the nearest original vertex" : "")
        + (result.PatchedVertices > 0
            ? $", {result.PatchedVertices} vertex(es) had no bone this mesh knows and took the nearest original weights" : ""));
    Console.WriteLine("written -> " + output + "  (reopened and verified)");
    return 0;
}

static int CheckGltfRoundTrips(string path, int limit)
{
    var paths = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.data") : [path];
    string scratch = Path.Combine(Path.GetTempPath(), "wl-gltfcycle.glb");
    int seen = 0, failed = 0;
    var problems = new Dictionary<string, int>();

    foreach (string each in paths)
    {
        if (seen >= limit) break;

        DataFile file;
        try { file = DataFile.Read(each); }
        catch { continue; }

        foreach (var resource in file.Resources)
        {
            if (resource.ClassHash != Mesh.ClassHash || seen >= limit) continue;

            Mesh mesh;
            try
            {
                mesh = Mesh.Read(resource.Data);
                if (mesh.Geometry == GeometryKind.None || !VertexLayout.IsKnown(mesh.VertexFormat)) continue;
            }
            catch { continue; }

            seen++;
            try
            {
                var before = MeshGeometry.ReadVertices(mesh);
                var cornersBefore = MeshGeometry.ReadTriangles(mesh);
                var layout = VertexLayout.For(mesh.VertexFormat, mesh.VertexStride);

                GltfFile.Write(mesh, resource.Name, scratch);
                var rebuilt = Mesh.Read(resource.Data);
                var result = MeshImport.Replace(rebuilt, GltfFile.Read(scratch));
                rebuilt = Mesh.Read(rebuilt.Write());

                var after = MeshGeometry.ReadVertices(rebuilt);
                var cornersAfter = MeshGeometry.ReadTriangles(rebuilt);

                if (cornersAfter.Length != cornersBefore.Length)
                {
                    failed++;
                    Bump(problems, "triangle count changed");
                    continue;
                }

                float span = Math.Max(1e-6f, Math.Max(mesh.ExtentMax[0] - mesh.ExtentMin[0],
                    Math.Max(mesh.ExtentMax[1] - mesh.ExtentMin[1], mesh.ExtentMax[2] - mesh.ExtentMin[2])));
                float worst = 0, worstUv = 0;
                int colourKept = 0, colourTotal = 0, jointKept = 0, jointTotal = 0;

                for (int i = 0; i < cornersBefore.Length; i++)
                {
                    var a = before[cornersBefore[i]];
                    var b = after[cornersAfter[i]];
                    worst = Math.Max(worst, System.Numerics.Vector3.Distance(a.Position, b.Position));

                    for (int set = 0; set < Math.Min(a.Uv.Length, b.Uv.Length); set++)
                        worstUv = Math.Max(worstUv, System.Numerics.Vector2.Distance(a.Uv[set], b.Uv[set]));

                    if (layout.HasColor)
                    {
                        colourTotal++;
                        if (a.Color == b.Color) colourKept++;
                    }

                    if (layout.IsSkinned)
                    {
                        jointTotal++;
                        if (a.JointIndices.AsSpan().SequenceEqual(b.JointIndices)
                            && a.JointWeights.AsSpan().SequenceEqual(b.JointWeights)) jointKept++;
                    }
                }

                Bump(problems, worst / span < 0.005f ? "corners kept within half a percent" : "corners moved further");
                if (layout.UvCount > 0)
                    Bump(problems, worstUv < 0.01f ? "every uv set kept" : "uv drifted more than 0.01");
                if (colourTotal > 0)
                    Bump(problems, colourKept == colourTotal ? "vertex colours kept exactly" : "vertex colours changed");
                if (jointTotal > 0)
                    Bump(problems, jointKept == jointTotal ? "joints and weights kept exactly" : "joints or weights changed");
                if (layout.IsSkinned && !result.CarriedSkinning)
                    Bump(problems, "skinning did not survive the file");
            }
            catch (Exception ex)
            {
                failed++;
                Bump(problems, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    Console.WriteLine(seen + " mesh(es) through glTF and back, " + failed + " failure(s)");
    Report("result:", problems);
    return failed == 0 ? 0 : 1;
}

static int CheckObjRoundTrips(string path, int limit)
{
    var paths = Directory.Exists(path) ? Directory.EnumerateFiles(path, "*.data") : [path];
    string scratch = Path.Combine(Path.GetTempPath(), "wl-objcycle.obj");
    int seen = 0, failed = 0;
    var problems = new Dictionary<string, int>();

    foreach (string each in paths)
    {
        if (seen >= limit) break;

        DataFile file;
        try { file = DataFile.Read(each); }
        catch { continue; }

        foreach (var resource in file.Resources)
        {
            if (resource.ClassHash != Mesh.ClassHash) continue;
            if (seen >= limit) break;

            Mesh mesh;
            try
            {
                mesh = Mesh.Read(resource.Data);
                if (mesh.Geometry == GeometryKind.None || !VertexLayout.IsKnown(mesh.VertexFormat)) continue;
            }
            catch { continue; }

            seen++;
            try
            {
                var before = MeshGeometry.ReadVertices(mesh);
                var cornersBefore = MeshGeometry.ReadTriangles(mesh);

                ObjFile.Write(mesh, resource.Name, scratch);
                var rebuilt = Mesh.Read(resource.Data);
                MeshImport.Replace(rebuilt, ObjFile.Read(scratch));
                rebuilt = Mesh.Read(rebuilt.Write());

                var after = MeshGeometry.ReadVertices(rebuilt);
                var cornersAfter = MeshGeometry.ReadTriangles(rebuilt);

                if (cornersAfter.Length != cornersBefore.Length)
                {
                    failed++;
                    Bump(problems, "triangle count changed");
                    continue;
                }

                float worst = 0;
                float span = Math.Max(1e-6f, Math.Max(mesh.ExtentMax[0] - mesh.ExtentMin[0],
                    Math.Max(mesh.ExtentMax[1] - mesh.ExtentMin[1], mesh.ExtentMax[2] - mesh.ExtentMin[2])));

                for (int i = 0; i < cornersBefore.Length; i++)
                    worst = Math.Max(worst, System.Numerics.Vector3.Distance(
                        before[cornersBefore[i]].Position, after[cornersAfter[i]].Position));

                Bump(problems, worst / span < 0.005f ? "corners kept within half a percent"
                    : worst / span < 0.02f ? "corners kept within two percent"
                    : "corners moved by more than two percent");
            }
            catch (Exception ex)
            {
                failed++;
                Bump(problems, ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    Console.WriteLine(seen + " mesh(es) through OBJ and back, " + failed + " failure(s)");
    Report("result:", problems);
    return failed == 0 ? 0 : 1;
}

static int CheckMeshRoundTrips(string path, int limit)
{
    var files = Directory.Exists(path)
        ? Directory.EnumerateFiles(path, "*.data")
        : [path];

    int seen = 0, failed = 0, decoded = 0;
    var formats = new Dictionary<string, int>();
    var problems = new Dictionary<string, int>();

    foreach (string file in files)
    {
        if (seen >= limit) break;

        DataFile data;
        try { data = DataFile.Read(file); }
        catch { continue; }

        foreach (var resource in data.Resources)
        {
            if (resource.ClassHash != Mesh.ClassHash) continue;
            if (seen >= limit) break;
            seen++;

            Mesh mesh;
            try
            {
                mesh = Mesh.Read(resource.Data);
                byte[] rebuilt = mesh.Write();

                if (!rebuilt.AsSpan().SequenceEqual(resource.Data))
                {
                    failed++;
                    Bump(problems, "roundtrip differs at 0x" + FirstDifference(resource.Data, rebuilt).ToString("X"));
                    continue;
                }
            }
            catch (Exception ex)
            {
                failed++;
                Bump(problems, ex.GetType().Name + ": " + ex.Message);
                continue;
            }

            Bump(formats, "format " + mesh.VertexFormat + ", stride " + mesh.VertexStride);

            if (mesh.Geometry == GeometryKind.None) continue;

            try
            {
                var vertices = MeshGeometry.ReadVertices(mesh);
                MeshGeometry.ReadTriangles(mesh);
                CheckAgainstExtents(mesh, vertices, problems);
                decoded++;
            }
            catch (Exception ex)
            {
                Bump(problems, "decode: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }

    Console.WriteLine(seen + " mesh(es), " + failed + " that do not rewrite identically, " + decoded + " decoded");
    Report("vertex formats:", formats);
    if (problems.Count > 0)
        Report("problems:", problems);
    return failed == 0 ? 0 : 1;
}

static void CheckAgainstExtents(Mesh mesh, MeshVertex[] vertices, Dictionary<string, int> problems)
{
    if (vertices.Length == 0) return;

    float slack = 0.01f * Math.Max(1e-6f, Math.Max(mesh.ExtentMax[0] - mesh.ExtentMin[0],
        Math.Max(mesh.ExtentMax[1] - mesh.ExtentMin[1], mesh.ExtentMax[2] - mesh.ExtentMin[2])));

    foreach (var vertex in vertices)
    {
        float[] p = [vertex.Position.X, vertex.Position.Y, vertex.Position.Z];
        for (int a = 0; a < 3; a++)
        {
            if (p[a] < mesh.ExtentMin[a] - slack || p[a] > mesh.ExtentMax[a] + slack)
            {
                Bump(problems, "vertex outside the extents the mesh states");
                return;
            }
        }

        if (Math.Abs(vertex.Normal.Length() - 1) > 0.1f)
        {
            Bump(problems, "normal is not unit length");
            return;
        }
    }
}

static int CheckBuildTableRoundTrips(string path)
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

static int FirstDifference(byte[] left, byte[] right)
{
    int count = Math.Min(left.Length, right.Length);
    for (int i = 0; i < count; i++)
        if (left[i] != right[i])
            return i;
    return count;
}

static int ScanHashes(string binaryPath, string[] hashTexts)
{
    var targets = hashTexts.Select(ParseHash).ToHashSet();
    var matches = new Dictionary<uint, SortedSet<string>>();

    VisitAsciiTokens(binaryPath, Record);
    VisitUtf16LeTokens(binaryPath, Record);

    foreach (uint hash in targets.Order())
    {
        if (matches.TryGetValue(hash, out var names))
            Console.WriteLine($"0x{hash:X8}  {string.Join(" | ", names)}");
        else
            Console.WriteLine($"0x{hash:X8}  <no literal match>");
    }

    return 0;

    void Record(string candidate, long _)
    {
        uint hash = ResourceTypes.Crc32(candidate);
        if (!targets.Contains(hash))
            return;

        if (!matches.TryGetValue(hash, out var names))
            matches[hash] = names = [];
        names.Add(candidate);
    }
}

static uint ParseHash(string text)
{
    if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        return Convert.ToUInt32(text[2..], 16);
    return Convert.ToUInt32(text, 16);
}

static int ApplyGraphicsProfile(string path, string name, string profilePath, string? output)
{
    var file = DataFile.Read(path);
    var resource = file.Resources.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    if (resource is null)
    {
        Console.WriteLine("no resource named " + name);
        return 1;
    }

    var cycle = TimeCycle.Read(resource.Data);
    var profile = GraphicsProfile.Load(profilePath);
    var result = profile.Apply(cycle, resource.Name);

    Console.WriteLine($"{result.Name}{(result.Experimental ? " [EXPERIMENTAL]" : "")}: "
        + $"{result.Changes.Count} curve(s), {result.ChangedValues} value(s)");
    foreach (var change in result.Changes)
        Console.WriteLine($"  {change.Label}: {change.BeforeMinimum:0.#####}..{change.BeforeMaximum:0.#####} "
            + $"-> {change.AfterMinimum:0.#####}..{change.AfterMaximum:0.#####} ({change.ChangedValues} values)");

    if (output is null)
    {
        Console.WriteLine("preview only; pass out.data to write the modified data file");
        return 0;
    }

    resource.Data = cycle.Write();
    file.Write(output);
    var written = DataFile.Read(output);
    if (written.Resources.Count != file.Resources.Count)
        throw new InvalidDataException("The written data file has a different resource count.");

    for (int i = 0; i < file.Resources.Count; i++)
    {
        var expected = file.Resources[i];
        var actual = written.Resources[i];
        if (expected.Id != actual.Id || expected.ClassHash != actual.ClassHash
            || expected.Name != actual.Name || !expected.Data.AsSpan().SequenceEqual(actual.Data))
            throw new InvalidDataException($"The written data file failed verification at resource {i} ({expected.Name}).");
    }

    Console.WriteLine("written -> " + output);
    Console.WriteLine($"verified {written.Resources.Count} resource(s) after reopening the output");
    return 0;
}

// Reads every time cycle in an archive and writes it straight back. The resource
// carries eight bytes per entry that nothing derives, and object ids that are
// handed out again on writing, so "identical" is the only answer that proves both
// the reader and the writer. Anything less and an edited cycle would come back
// subtly different from the one the game shipped.
static int CheckTimeCycles(string forgePath, int limit)
{
    using var archive = ForgeArchive.Open(forgePath);

    int read = 0, identical = 0, failed = 0, entries = 0;
    var kinds = new Dictionary<string, int>();
    var unreadable = new List<string>();

    foreach (var entry in archive.Entries)
    {
        if (read >= limit) break;
        if (entry.FileExtension != ".data") continue;

        DataFile file;
        try { using var s = new MemoryStream(archive.ReadEntry(entry)); file = DataFile.Read(s); }
        catch (Exception ex) { unreadable.Add($"{entry.Name}: {ex.Message}"); continue; }

        foreach (var resource in file.Resources)
        {
            if (!TimeCycle.IsTimeCycle(resource.ClassHash)) continue;
            read++;

            try
            {
                var cycle = TimeCycle.Read(resource.Data);
                bool same = cycle.Write().SequenceEqual(resource.Data);

                if (same) identical++; else Console.WriteLine($"  differs: {resource.Name}");

                Console.WriteLine($"  {ResourceTypes.NameOf(resource.ClassHash),-32} " +
                                  $"{cycle.Entries.Count,4} entries  {entry.Name} :: {resource.Name}");

                entries += cycle.Entries.Count;
                foreach (var e in cycle.Entries)
                    Bump(kinds, e.Kind.ToString());
            }
            catch (Exception ex)
            {
                failed++;
                Console.WriteLine($"  {resource.Name}: {ex.Message}");
            }
        }
    }

    Console.WriteLine($"{identical} of {read} time cycles rewrite identically, {failed} failed to read");
    Console.WriteLine($"{entries} entries in total");
    foreach (var kind in kinds.OrderByDescending(k => k.Value))
        Console.WriteLine($"  {kind.Key,-8} {kind.Value}");

    // A data file that will not open is worth saying out loud. Skipping it quietly
    // is how a broken archive comes back as "0 of 0" and reads like success.
    if (unreadable.Count > 0)
    {
        Console.WriteLine($"{unreadable.Count} data files could not be opened:");
        foreach (string note in unreadable.Take(10))
            Console.WriteLine($"  {note}");
    }

    return failed == 0 && unreadable.Count == 0 && identical == read ? 0 : 1;
}

// A property hash alone says very little. This census keeps the curve kind and
// every full property path in which it occurs, which turns a future name match
// into something we can check semantically as well as by CRC32.
static int CensusWeatherProperties(string forgePath, int limit)
{
    var properties = CollectWeatherProperties(forgePath, limit, out int controllers, out int unreadable);
    int confirmed = properties.Count(p => PropertyNames.IsConfirmed(p.Key));
    int unverified = properties.Count - confirmed;

    Console.WriteLine($"{controllers} controller(s), {properties.Count} distinct property hash(es): "
        + $"{confirmed} confirmed, {unverified} still unverified");

    if (unreadable > 0)
        Console.WriteLine($"{unreadable} data file(s) could not be read and were skipped.");

    foreach (var (hash, observation) in properties
        .Where(p => !PropertyNames.IsConfirmed(p.Key))
        .OrderByDescending(p => p.Value.Uses)
        .ThenBy(p => p.Key))
    {
        string display = PropertyNames.TryName(hash, out string? name)
            ? name!
            : "unknown";
        string kinds = string.Join(", ", observation.Kinds.OrderBy(p => p.Key)
            .Select(p => $"{p.Key} {p.Value}"));

        Console.WriteLine();
        Console.WriteLine($"0x{hash:X8}  {display}  used {observation.Uses} time(s), {kinds}");
        foreach (string path in observation.Paths.Take(4))
            Console.WriteLine("  " + path);
        if (observation.Paths.Count > 4)
            Console.WriteLine($"  … {observation.Paths.Count - 4} more path(s)");
    }

    return 0;
}

// Values are the second half of the evidence: a field that is a constant RGBA
// colour across all climates should not be called a density, and a 32-element
// animated array should not be called an exposure threshold. This command keeps
// the original curves visible while trying names.
static int ProfileWeatherProperties(string forgePath, string[] hashTexts)
{
    var wanted = new HashSet<uint>();
    foreach (string text in hashTexts)
    {
        string number = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (!uint.TryParse(number, System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture, out uint hash))
        {
            Console.WriteLine($"not a hexadecimal property hash: {text}");
            return 1;
        }
        wanted.Add(hash);
    }

    int controllers = 0;
    int matches = 0;
    using var archive = ForgeArchive.Open(forgePath);
    foreach (var entry in archive.Entries)
    {
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

        foreach (var resource in file.Resources)
        {
            if (!TimeCycle.IsTimeCycle(resource.ClassHash))
                continue;

            TimeCycle cycle;
            try { cycle = TimeCycle.Read(resource.Data); }
            catch { continue; }

            controllers++;
            foreach (var curve in cycle.Entries)
            {
                uint? hash = curve.Path.Where(step => wanted.Contains(step.NameHash))
                    .Select(step => (uint?)step.NameHash)
                    .FirstOrDefault();
                if (hash is null)
                    continue;

                matches++;
                var values = curve.Values.SelectMany(value => value).ToArray();
                float low = values.Min();
                float high = values.Max();
                bool constant = curve.Values.All(value => value.AsSpan().SequenceEqual(curve.Values[0]));
                string lowText = low.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
                string highText = high.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

                Console.WriteLine($"{resource.Name}  {curve.PathText()}");
                Console.WriteLine($"  0x{hash:X8}  {curve.Kind}, {curve.Values.Count} key(s), "
                    + $"range {lowText}..{highText}, {(constant ? "constant" : "animated")}");
                Console.WriteLine("  " + string.Join(" | ", curve.Values.Select((value, index) =>
                    $"{TimeCycleText.Axis(curve.Times[index], cycle.IsWeather)}={string.Join(",", value.Select(v => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)))}")));
            }
        }
    }

    Console.WriteLine();
    Console.WriteLine($"{matches} matching curve(s) in {controllers} controller(s)");
    return matches > 0 ? 0 : 1;
}

// Property strings are sometimes retained in the executable, diagnostics, or
// companion DLLs. Only byte strings actually present in the files supplied by
// the user are tried, and only against hashes observed in a controller. This is
// deliberately much narrower than a dictionary attack.
static int CrackPropertyNames(string forgePath, string[] binaryPaths)
{
    var properties = CollectWeatherProperties(forgePath, int.MaxValue, out int controllers, out int unreadable);
    var targets = properties
        .Where(p => !PropertyNames.IsConfirmed(p.Key))
        .Select(p => p.Key)
        .ToHashSet();

    Console.WriteLine($"{controllers} controller(s), {targets.Count} unverified property hash(es)");
    if (unreadable > 0)
        Console.WriteLine($"{unreadable} data file(s) could not be read and were skipped.");

    var matches = new Dictionary<uint, Dictionary<string, List<string>>>();

    foreach (string binaryPath in binaryPaths)
    {
        if (Directory.Exists(binaryPath))
        {
            int names = 0;
            VisitFileNameTokens(binaryPath, (candidate, source) =>
            {
                names++;
                Record(candidate, source);
            });
            Console.WriteLine($"{Path.GetFileName(Path.TrimEndingDirectorySeparator(binaryPath))}: examined {names:N0} extracted file-name identifier(s)");
            continue;
        }

        if (!File.Exists(binaryPath))
        {
            Console.WriteLine($"missing: {binaryPath}");
            continue;
        }

        int asciiStrings = 0;
        int wideStrings = 0;
        VisitAsciiTokens(binaryPath, (candidate, offset) =>
        {
            asciiStrings++;
            Record(candidate, $"{Path.GetFileName(binaryPath)}+0x{offset:X}");
        });
        VisitUtf16LeTokens(binaryPath, (candidate, offset) =>
        {
            wideStrings++;
            Record(candidate, $"{Path.GetFileName(binaryPath)}+0x{offset:X}");
        });

        Console.WriteLine($"{Path.GetFileName(binaryPath)}: examined {asciiStrings:N0} ASCII and {wideStrings:N0} UTF-16 identifier(s)");

        void Record(string candidate, string source)
        {
            uint hash = ResourceTypes.Crc32(candidate);
            if (!targets.Contains(hash))
                return;

            if (!matches.TryGetValue(hash, out var candidates))
                matches[hash] = candidates = [];
            if (!candidates.TryGetValue(candidate, out var places))
                candidates[candidate] = places = [];
            if (places.Count < 3)
                places.Add(source);
        }
    }

    Console.WriteLine();
    if (matches.Count == 0)
    {
        Console.WriteLine("No literal property-name match. Try GRW.exe plus engine DLLs, or a symbol/config dump from the same game build.");
        return 0;
    }

    foreach (var (hash, candidates) in matches.OrderBy(p => p.Key))
    {
        Console.WriteLine($"0x{hash:X8}  {string.Join(" | ", candidates.Keys.Order())}");
        foreach (string place in candidates.Values.SelectMany(p => p).Take(3))
            Console.WriteLine("  " + place);
    }

    Console.WriteLine();
    Console.WriteLine($"{matches.Count} hash match(es). Validate each candidate against the printed controller paths before adding it to PropertyNames.ByHash.");
    return 0;
}

static Dictionary<uint, (int Uses, Dictionary<CurveKind, int> Kinds, SortedSet<string> Paths)> CollectWeatherProperties(string forgePath, int limit,
    out int controllers, out int unreadable)
{
    var properties = new Dictionary<uint, (int Uses, Dictionary<CurveKind, int> Kinds, SortedSet<string> Paths)>();
    controllers = 0;
    unreadable = 0;

    using var archive = ForgeArchive.Open(forgePath);
    foreach (var entry in archive.Entries)
    {
        if (controllers >= limit)
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
            unreadable++;
            continue;
        }

        foreach (var resource in file.Resources)
        {
            if (controllers >= limit)
                break;
            if (!TimeCycle.IsTimeCycle(resource.ClassHash))
                continue;

            TimeCycle cycle;
            try
            {
                cycle = TimeCycle.Read(resource.Data);
            }
            catch
            {
                continue;
            }

            controllers++;
            foreach (var curve in cycle.Entries)
            {
                string path = curve.PathText();
                foreach (var step in curve.Path)
                {
                    if (!properties.TryGetValue(step.NameHash, out var observation))
                        observation = (0, [], []);

                    observation.Uses++;
                    observation.Kinds[curve.Kind] = observation.Kinds.GetValueOrDefault(curve.Kind) + 1;
                    observation.Paths.Add(path);
                    properties[step.NameHash] = observation;
                }
            }
        }
    }

    return properties;
}

static void VisitAsciiTokens(string path, Action<string, long> visit)
{
    const int MaximumLength = 128;
    var buffer = new byte[1 << 20];
    var word = new System.Text.StringBuilder();
    bool tooLong = false;
    long position = 0;
    long start = 0;

    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length);
    int read;
    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
    {
        for (int i = 0; i < read; i++, position++)
        {
            byte value = buffer[i];
            bool identifier = value is >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9'
                or (byte)'_';

            if (identifier)
            {
                if (word.Length == 0 && !tooLong)
                    start = position;

                if (word.Length < MaximumLength && !tooLong)
                    word.Append((char)value);
                else
                    tooLong = true;
                continue;
            }

            Emit();
        }
    }

    Emit();

    void Emit()
    {
        if (!tooLong && word.Length >= 3 && IsAsciiLetter(word[0]))
            visit(word.ToString(), start);

        word.Clear();
        tooLong = false;
    }
}

static void VisitUtf16LeTokens(string path, Action<string, long> visit)
{
    const int MaximumLength = 128;
    var buffer = new byte[1 << 20];
    var word = new System.Text.StringBuilder();
    bool tooLong = false;
    bool waitingForZero = false;
    byte pending = 0;
    long position = 0;
    long pendingPosition = 0;
    long start = 0;

    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length);
    int read;
    while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
    {
        for (int i = 0; i < read; i++, position++)
        {
            byte value = buffer[i];
            if (!waitingForZero)
            {
                if (IsIdentifierByte(value))
                {
                    pending = value;
                    pendingPosition = position;
                    waitingForZero = true;
                }
                else
                {
                    Emit();
                }
                continue;
            }

            if (value == 0)
            {
                if (word.Length == 0 && !tooLong)
                    start = pendingPosition;

                if (word.Length < MaximumLength && !tooLong)
                    word.Append((char)pending);
                else
                    tooLong = true;
                waitingForZero = false;
                continue;
            }

            Emit();
            waitingForZero = IsIdentifierByte(value);
            if (waitingForZero)
            {
                pending = value;
                pendingPosition = position;
            }
        }
    }

    Emit();

    void Emit()
    {
        if (!tooLong && word.Length >= 3 && IsAsciiLetter(word[0]))
            visit(word.ToString(), start);

        word.Clear();
        tooLong = false;
    }
}

static void VisitFileNameTokens(string folder, Action<string, string> visit)
{
    foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        int start = 0;

        for (int i = 0; i <= stem.Length; i++)
        {
            bool identifier = i < stem.Length && (char.IsLetterOrDigit(stem[i]) || stem[i] == '_');
            if (identifier)
                continue;

            if (i - start >= 3 && IsAsciiLetter(stem[start]))
                visit(stem[start..i], Path.GetRelativePath(folder, path));
            start = i + 1;
        }
    }
}

static bool IsIdentifierByte(byte value) => value is >= (byte)'A' and <= (byte)'Z'
    or >= (byte)'a' and <= (byte)'z'
    or >= (byte)'0' and <= (byte)'9'
    or (byte)'_';

static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';


// Who points at a resource. Ids are eight bytes and a resource that refers to
// another simply carries its id, so a scan for that pattern finds the holders
// without knowing any of the formats involved. Good enough to answer whether a
// thing is used at all, which is otherwise hard to tell.
// Before changing a mesh for a real test it matters which archive the game
// actually loads. The same resource often sits in the base archive, in the
// world map and in a patch, and only the last one wins.
static int WhereIsResource(string gameFolder, string name, bool includeWorldMap)
{
    var archives = ArchiveLocator.Find(gameFolder);
    if (archives.Count == 0)
    {
        Console.WriteLine("no forge archives in " + gameFolder);
        return 1;
    }

    var skipped = new List<string>();
    var found = new List<(string Archive, string Container, string Resource, string Kind, int Bytes)>();

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

                    found.Add((Path.GetFileName(path), entry.Name, resource.Name,
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

    foreach (var perResource in found.GroupBy(f => f.Resource).OrderBy(g => g.Key))
    {
        Console.WriteLine(perResource.Key + "  (" + perResource.First().Kind + ")");

        foreach (var hit in perResource)
            Console.WriteLine($"    {hit.Archive,-42} container \"{hit.Container}\"  {hit.Bytes} bytes");

        if (perResource.Count() > 1)
            Console.WriteLine($"    -> {perResource.Count()} copies. Change every one of them, or the game keeps showing an older copy.");

        Console.WriteLine();
    }

    if (skipped.Count > 0)
        Console.WriteLine($"{skipped.Count} world map archive(s) were skipped because they are huge; add --all to search them too.");

    return 0;
}

static int FindReferences(string path, string what)
{
    var file = DataFile.Read(path);

    ulong id;
    if (what.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        id = Convert.ToUInt64(what[2..], 16);
    else
    {
        var target = file.Resources.FirstOrDefault(r => r.Name.Equals(what, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            Console.WriteLine("no resource named " + what);
            return 1;
        }
        id = target.Id;
    }

    var needle = BitConverter.GetBytes(id);
    int found = 0;

    Console.WriteLine($"0x{id:X12}  held by:");

    foreach (var resource in file.Resources)
    {
        if (resource.Id == id) continue;

        var data = resource.Data.AsSpan();
        for (int i = 0; i + 8 <= data.Length; i++)
        {
            if (!data.Slice(i, 8).SequenceEqual(needle)) continue;

            Console.WriteLine($"  {ResourceTypes.NameOf(resource.ClassHash),-32} at {i,7}  {resource.Name}");
            found++;
            break;
        }
    }

    Console.WriteLine(found == 0 ? "  nothing" : $"  {found} resources");
    return 0;
}
