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
    internal static int CensusMeshes(string forgePath, int limit)
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
    
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
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

    internal static int CheckGeometry(string forgePath, int limit)
    {
        using var archive = ForgeArchive.Open(forgePath);
    
        var results = new Dictionary<string, int>();
        int seen = 0;
    
        foreach (var entry in archive.Entries)
        {
            if (seen >= limit) break;
            if (entry.FileExtension != ".data") continue;
    
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
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

    internal static bool RangeFits(Mesh mesh, MeshPrimitive range)
    {
        for (int i = 0; i < range.TriangleCount * 3; i++)
        {
            if (Index(mesh, range.StartIndex + i) >= range.VertexCount)
                return false;
        }
    
        return true;
    }
    
    // The extent box is padded, so a vertex only has to stay inside it.

    internal static bool InsideExtent(System.Numerics.Vector3 p, Mesh mesh)
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
    
    internal static void Report<T>(string title, Dictionary<T, int> counter) where T : notnull
    {
        if (counter.Count == 0) return;
    
        Console.WriteLine();
        Console.WriteLine(title);
        foreach (var pair in counter.OrderByDescending(p => p.Value).Take(15))
            Console.WriteLine("  " + pair.Value.ToString().PadLeft(6) + "  " + pair.Key);
    }
    
    internal static void Bump<T>(Dictionary<T, int> counter, T key) where T : notnull
    {
        counter[key] = counter.GetValueOrDefault(key) + 1;
    }

    internal static int ShowMesh(string path, string name)
    {
        var file = DataFile.Read(path);
        Resource resource = RequireMesh(file, name);
    
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
        if (mesh.Bones.Count > 0)
            Console.WriteLine("  bone ids  " + string.Join(", ", mesh.Bones.Select(b => "0x" + b.Name.ToString("X8"))));
        Console.WriteLine("  draws     " + mesh.Data.Standard.Count + " standard, " + mesh.Data.Shadow.Count + " shadow");
    
        foreach (var p in mesh.Data.Standard)
            Console.WriteLine("    index " + p.StartIndex + " +" + p.TriangleCount * 3
                + "   vertex " + p.MinIndex + " +" + p.VertexCount + "   type " + p.Type);
    
        return 0;
    }

    internal static string Range(IEnumerable<float> values) =>
        values.Min().ToString("0.###") + ".." + values.Max().ToString("0.###");

    internal static string Triple(float[] v) =>
        v[0].ToString("0.###") + " " + v[1].ToString("0.###") + " " + v[2].ToString("0.###");
    
    // Reads every texture set of an archive and says what the filled slots point
    // at. A slot is only known by its position, so the proof that the order is
    // right is that the names of the textures line up with it.

    internal static int ExportFbx(string path, string name, string output, string? cachePath)
    {
        var file = DataFile.Read(path);
        Resource resource = RequireMesh(file, name);
    
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

    internal static int BuildSkeletonIndex(string folder, string cachePath)
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

    internal static int CheckSkeletons(string forgePath, int limit, string? cachePath)
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

    internal static List<SkeletonBone>? FindSkeleton(
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

    internal static int ShowRanges(string path, string name)
    {
        var file = DataFile.Read(path);
        Resource resource = RequireMesh(file, name);
    
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

    internal static int Index(Mesh mesh, int position) =>
        mesh.Data.Indices32Bit
            ? BitConverter.ToInt32(mesh.IndexBuffer, position * 4)
            : BitConverter.ToUInt16(mesh.IndexBuffer, position * 2);
    
    // CRC32 of a name, which is how class hashes are formed.

    internal static int CheckSkeletonsAgainstDocs(string path)
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

    internal static int[] SubtreeSizes(SkeletonAsset asset)
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

    internal static bool SubtreeSizesMatch(SkeletonAsset asset, bool includingSelf)
    {
        var size = SubtreeSizes(asset);
    
        for (int i = 0; i < asset.Bones.Count; i++)
            if (asset.Bones[i].ChildrenCount != size[i] - (includingSelf ? 0 : 1))
                return false;
    
        return true;
    }

    internal static void ReportAverageRig(string name, int bytes, SkeletonAsset asset, (string Name, uint Hash)[] documented)
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

    internal static int CheckSkeletonRoundTrips(string path)
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

    internal static Resource? FindMesh(DataFile file, string name)
    {
        var resource = file.Resources.FirstOrDefault(r => r.ClassHash == Mesh.ClassHash
            && r.Name.Contains(name, StringComparison.OrdinalIgnoreCase));
    
        if (resource is null)
            Console.WriteLine("no mesh matching " + name);
    
        return resource;
    }

    internal static int ExportObj(string path, string name, string output)
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

    internal static int ImportObj(string path, string name, string input, string output)
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

    internal static int ExportGltf(string path, string name, string output, string? cachePath)
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

    internal static int ImportGltf(string path, string name, string input, string output)
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

    internal static int CheckGltfRoundTrips(string path, int limit)
    {
        GltfImportSelfTest.Run();
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

    internal static int CheckObjRoundTrips(string path, int limit)
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

    internal static int CheckMeshRoundTrips(string path, int limit)
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

    internal static void CheckAgainstExtents(Mesh mesh, MeshVertex[] vertices, Dictionary<string, int> problems)
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

    internal static Resource RequireMesh(DataFile file, string name)
    {
        List<Resource> matches = file.Resources
            .Where(resource => resource.ClassHash == Mesh.ClassHash && resource.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidOperationException("No mesh matching " + name + "."),
            _ => throw new InvalidOperationException($"Mesh filter {name} is ambiguous ({matches.Count} matches)."),
        };
    }
}
