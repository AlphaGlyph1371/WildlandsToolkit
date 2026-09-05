using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Wildlands.Formats.Models;

public sealed class MeshImportResult
{
    public int Vertices { get; set; }
    public int Triangles { get; set; }
    public int Ranges { get; set; }
    public float QuantizationFactor { get; set; }
    public float UvQuantizationFactor { get; set; }
    public bool TransferredSkinning { get; set; }
    public bool CarriedSkinning { get; set; }
    public int PatchedVertices { get; set; }
    public int JointsInFile { get; set; }
    public int JointsMatched { get; set; }
    public int BoneCount { get; set; }
    public bool Cloth { get; set; }
    public int RangesBefore { get; set; }
}

public static class MeshImport
{
    const int DescriptionSize = 36;

    public static MeshImportResult Replace(Mesh mesh, ObjFile obj) => Replace(mesh, obj.ToGeometry());

    public static MeshImportResult Replace(Mesh mesh, ImportedGeometry geometry)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(geometry);

        if (mesh.Clustered is null)
            throw new InvalidDataException("This mesh keeps its geometry outside the clustered buffers; importing into it is not supported.");

        int ranges = geometry.Groups.Count;
        if (ranges == 0)
            throw new InvalidDataException("The file holds no groups, so there is nothing to draw.");

        int before = mesh.Data.Standard.Count;
        var layout = VertexLayout.For(mesh.VertexFormat, mesh.VertexStride);
        var original = MeshGeometry.ReadVertices(mesh);

        var vertices = new List<MeshVertex>();
        var perRange = new List<(int Start, int Count, int IndexStart, int IndexCount)>();
        var rangesWithoutTangents = new List<(int Start, int Count, int IndexStart, int IndexCount)>();
        var indices = new List<int>();

        foreach (var group in geometry.Groups)
        {
            if (group.Vertices.Count == 0 || group.Indices.Count == 0)
                throw new InvalidDataException(
                    $"Group {group.Name} has no renderable vertices and triangles.");
            if (group.Indices.Count % 3 != 0)
                throw new InvalidDataException($"Group {group.Name} holds {group.Indices.Count} indices, which is not whole triangles.");

            int vertexStart = vertices.Count;
            int indexStart = indices.Count;

            foreach (var source in group.Vertices)
                vertices.Add(Fit(source, layout));

            foreach (int corner in group.Indices)
            {
                if (corner < 0 || corner >= group.Vertices.Count)
                    throw new InvalidDataException($"Group {group.Name} refers to vertex {corner}, which it does not hold.");

                indices.Add(corner);
            }

            var range = (vertexStart, vertices.Count - vertexStart, indexStart, indices.Count - indexStart);
            perRange.Add(range);
            if (!group.HasTangents)
                rangesWithoutTangents.Add(range);
        }

        if (rangesWithoutTangents.Count > 0)
            BuildTangents(vertices, indices, rangesWithoutTangents, layout);

        bool skinned = layout.IsSkinned;
        bool transferred = false;

        int patched = 0;
        int matched = 0;

        if (skinned)
        {
            if (geometry.HasSkinning)
            {
                patched = RemapJoints(vertices, geometry, mesh, original, out matched);
                if (matched == 0)
                {
                    // A foreign rig is not a reason to reject otherwise valid geometry.
                    // Treat it like an unskinned import and attach every vertex to the
                    // nearest weights of the template mesh. Matching Wildlands bones are
                    // still carried exactly when the file was exported from this asset.
                    TransferSkinning(vertices, original, all: true);
                    transferred = true;
                }
            }
            else
            {
                TransferSkinning(vertices, original, all: true);
                transferred = true;
            }
        }

        var (lo, hi) = Bounds(vertices);
        float quantization = Quantization(lo, hi, layout.IsSkinned);
        float uvQuantization = UvQuantization(vertices);

        foreach (var vertex in vertices)
            vertex.PositionScale = layout.IsSkinned ? (short)32767 : (short)quantization;

        mesh.QuantizationFactor = quantization;
        mesh.UvQuantizationFactor = uvQuantization;

        byte[] vertexBuffer = MeshGeometry.WriteVertices([.. vertices], layout, quantization, uvQuantization);
        int widest = 0;
        foreach (int index in indices)
            widest = Math.Max(widest, index);

        bool wide = widest > ushort.MaxValue;
        byte[] indexBuffer = WriteIndices(indices, wide);

        mesh.Data.Indices32Bit = wide;
        mesh.Data.VertexFormat = (byte)mesh.VertexFormat;
        mesh.Data.VertexStride = (byte)layout.Stride;
        mesh.Data.VertexBuffer = [];
        mesh.Data.IndexBuffer = [];

        bool hadShadow = mesh.Data.Shadow.Count > 0;
        ulong next = NextObjectId(mesh);

        RebuildRanges(mesh.Data.Standard, perRange, ref next);

        if (hadShadow)
            RebuildRanges(mesh.Data.Shadow, perRange, ref next);

        MatchRangeCount(mesh, geometry.Groups, perRange, ref next);

        var clustered = mesh.Clustered;
        clustered.VertexFormat = (byte)mesh.VertexFormat;
        clustered.VertexStride = layout.Stride;
        clustered.ClusterCount = 0;
        clustered.VertexBuffer = vertexBuffer;
        clustered.IndexBuffer = indexBuffer;
        clustered.DrawPrimitiveCount = ranges;
        clustered.ClustersPerDrawPrimitive = Filled(ranges, 1);
        clustered.VertexOffsetPerDrawPrimitive = VertexOffsets(perRange, layout.Stride);
        clustered.PrimitiveDescriptions = Descriptions(vertices, perRange, layout.Stride);

        var center = (lo + hi) / 2;
        var half = (hi - lo) / 2;
        clustered.Center[0] = center.X; clustered.Center[1] = center.Y; clustered.Center[2] = center.Z;
        clustered.HalfExtent[0] = half.X; clustered.HalfExtent[1] = half.Y; clustered.HalfExtent[2] = half.Z;

        mesh.ExtentMin[0] = lo.X; mesh.ExtentMin[1] = lo.Y; mesh.ExtentMin[2] = lo.Z; mesh.ExtentMin[3] = 0;
        mesh.ExtentMax[0] = hi.X; mesh.ExtentMax[1] = hi.Y; mesh.ExtentMax[2] = hi.Z; mesh.ExtentMax[3] = 0;

        if (mesh.Dynamic)
        {
            mesh.DynamicMeshVertexCount = vertices.Count;
            mesh.DynamicMeshIndexCount = indices.Count;
        }

        ValidateGpuLayout(mesh, perRange, indices.Count);

        return new MeshImportResult
        {
            Vertices = vertices.Count,
            Triangles = indices.Count / 3,
            Ranges = ranges,
            RangesBefore = before,
            QuantizationFactor = quantization,
            UvQuantizationFactor = uvQuantization,
            TransferredSkinning = transferred,
            Cloth = mesh.Dynamic,
            CarriedSkinning = skinned && geometry.HasSkinning && matched > 0,
            PatchedVertices = patched,
            JointsInFile = geometry.JointNames.Count,
            JointsMatched = matched,
            BoneCount = mesh.Bones.Count,
        };
    }

    static MeshVertex Fit(MeshVertex source, VertexLayout layout)
    {
        var normal = source.Normal.LengthSquared() > 0 ? Vector3.Normalize(source.Normal) : Vector3.UnitZ;
        var texture = new Vector2[layout.UvCount];

        for (int i = 0; i < texture.Length; i++)
            texture[i] = i < source.Uv.Length ? source.Uv[i]
                : source.Uv.Length > 0 ? source.Uv[0] : Vector2.Zero;

        var joints = new byte[layout.JointsPerVertex];
        var weights = new byte[layout.JointsPerVertex];

        for (int i = 0; i < layout.JointsPerVertex; i++)
        {
            joints[i] = i < source.JointIndices.Length ? source.JointIndices[i] : (byte)0;
            weights[i] = i < source.JointWeights.Length ? source.JointWeights[i] : (byte)0;
        }

        byte tangentSign = Vector3.Dot(Vector3.Cross(normal, source.Tangent), source.Binormal) < 0
            ? (byte)0 : (byte)255;

        return new MeshVertex
        {
            Position = source.Position,
            Normal = normal,
            Tangent = source.Tangent,
            Binormal = source.Binormal,
            Uv = texture,
            Color = layout.HasColor ? source.Color : 0xFFFFFFFF,
            TangentSign = tangentSign,
            JointIndices = layout.IsSkinned ? joints : [],
            JointWeights = layout.IsSkinned ? weights : [],
        };
    }

    static int RemapJoints(List<MeshVertex> vertices, ImportedGeometry geometry, Mesh mesh, MeshVertex[] original, out int matched)
    {
        var slotOf = new Dictionary<int, byte>();

        for (int i = 0; i < geometry.JointNames.Count; i++)
        {
            int bone = mesh.Bones.FindIndex(b => b.Name == geometry.JointNames[i]);
            if (bone >= 0 && bone <= byte.MaxValue)
                slotOf[i] = (byte)bone;
        }

        matched = slotOf.Count;

        if (slotOf.Count == 0)
            return 0;

        var stranded = new List<MeshVertex>();

        foreach (var vertex in vertices)
        {
            for (int i = 0; i < vertex.JointIndices.Length; i++)
            {
                if (vertex.JointWeights[i] == 0)
                {
                    vertex.JointIndices[i] = 0;
                    continue;
                }

                if (slotOf.TryGetValue(vertex.JointIndices[i], out byte bone))
                {
                    vertex.JointIndices[i] = bone;
                    continue;
                }

                vertex.JointIndices[i] = 0;
                vertex.JointWeights[i] = 0;
            }

            int sum = 0;
            foreach (byte weight in vertex.JointWeights) sum += weight;

            if (sum == 0)
                stranded.Add(vertex);
            else
                Normalise(vertex.JointWeights);
        }

        if (stranded.Count > 0)
            TransferSkinning(stranded, original, all: false);

        return stranded.Count;
    }

    static void Normalise(byte[] weights)
    {
        int sum = 0;
        foreach (byte weight in weights) sum += weight;

        if (sum == 255 || sum == 0)
            return;

        int running = 0, biggest = 0;

        for (int i = 0; i < weights.Length; i++)
        {
            weights[i] = (byte)(weights[i] * 255 / sum);
            running += weights[i];
            if (weights[i] > weights[biggest]) biggest = i;
        }

        weights[biggest] = (byte)(weights[biggest] + (255 - running));
    }

    static void BuildTangents(List<MeshVertex> vertices, List<int> indices,
        List<(int Start, int Count, int IndexStart, int IndexCount)> ranges, VertexLayout layout)
    {
        var alongU = new Vector3[vertices.Count];
        var alongV = new Vector3[vertices.Count];

        foreach (var range in ranges)
        {
            for (int i = 0; i + 2 < range.IndexCount; i += 3)
            {
                int a = range.Start + indices[range.IndexStart + i];
                int b = range.Start + indices[range.IndexStart + i + 1];
                int c = range.Start + indices[range.IndexStart + i + 2];

                var edge1 = vertices[b].Position - vertices[a].Position;
                var edge2 = vertices[c].Position - vertices[a].Position;

                var uvA = First(vertices[a]);
                float du1 = First(vertices[b]).X - uvA.X, dv1 = First(vertices[b]).Y - uvA.Y;
                float du2 = First(vertices[c]).X - uvA.X, dv2 = First(vertices[c]).Y - uvA.Y;

                float determinant = du1 * dv2 - du2 * dv1;
                if (MathF.Abs(determinant) < 1e-12f)
                    continue;

                var u = (edge1 * dv2 - edge2 * dv1) / determinant;
                var v = (edge2 * du1 - edge1 * du2) / determinant;

                alongU[a] += u; alongU[b] += u; alongU[c] += u;
                alongV[a] += v; alongV[b] += v; alongV[c] += v;
            }
        }

        for (int i = 0; i < vertices.Count; i++)
        {
            var normal = vertices[i].Normal;
            var tangent = alongU[i] - normal * Vector3.Dot(normal, alongU[i]);

            if (tangent.LengthSquared() < 1e-12f)
            {
                tangent = Vector3.Cross(normal, Vector3.UnitZ);
                if (tangent.LengthSquared() < 1e-12f)
                    tangent = Vector3.Cross(normal, Vector3.UnitY);
            }

            tangent = tangent.LengthSquared() > 0 ? Vector3.Normalize(tangent) : Vector3.UnitX;
            var binormal = Vector3.Cross(normal, tangent);

            if (Vector3.Dot(binormal, alongV[i]) > 0)
                binormal = -binormal;

            vertices[i].Tangent = tangent;
            vertices[i].Binormal = binormal;
            vertices[i].TangentSign = Vector3.Dot(Vector3.Cross(normal, tangent), binormal) < 0
                ? (byte)0 : (byte)255;
        }
    }

    static Vector2 First(MeshVertex vertex) => vertex.Uv.Length > 0 ? vertex.Uv[0] : Vector2.Zero;

    static void TransferSkinning(List<MeshVertex> vertices, MeshVertex[] original, bool all)
    {
        if (original.Length == 0)
            throw new InvalidDataException("The mesh is skinned but carries no original vertices to take joint weights from.");

        foreach (var vertex in vertices)
        {
            float best = float.MaxValue;
            var source = original[0];

            foreach (var candidate in original)
            {
                float distance = Vector3.DistanceSquared(candidate.Position, vertex.Position);
                if (distance >= best)
                    continue;

                best = distance;
                source = candidate;
                if (distance == 0)
                    break;
            }

            int lanes = vertex.JointIndices.Length;

            for (int i = 0; i < lanes; i++)
            {
                vertex.JointIndices[i] = i < source.JointIndices.Length ? source.JointIndices[i] : (byte)0;
                vertex.JointWeights[i] = i < source.JointWeights.Length ? source.JointWeights[i] : (byte)0;
            }

            Normalise(vertex.JointWeights);
        }
    }

    static void RebuildRanges(List<MeshPrimitive> target, List<(int Start, int Count, int IndexStart, int IndexCount)> ranges, ref ulong next)
    {
        var kept = new List<MeshPrimitive>(target);
        target.Clear();

        for (int i = 0; i < ranges.Count; i++)
        {
            var source = i < kept.Count ? kept[i] : new MeshPrimitive { Id = next++ };

            target.Add(new MeshPrimitive
            {
                Id = source.Id,
                MinIndex = ranges[i].Start,
                UsesDepthOnlyBuffers = source.UsesDepthOnlyBuffers,
                VertexCount = ranges[i].Count,
                StartIndex = ranges[i].IndexStart,
                TriangleCount = ranges[i].IndexCount / 3,
                Type = source.Type != 0 ? source.Type : 1,
            });
        }
    }

    static ulong NextObjectId(Mesh mesh)
    {
        ulong highest = Math.Max(mesh.CompiledMeshId, Math.Max(mesh.ClusteredId, mesh.DataId));

        foreach (var bone in mesh.Bones) highest = Math.Max(highest, bone.Id);
        foreach (var entry in mesh.Instancing) highest = Math.Max(highest, entry.Id);
        foreach (var material in mesh.Materials) highest = Math.Max(highest, material.Id);
        foreach (var primitive in mesh.Data.Standard) highest = Math.Max(highest, primitive.Id);
        foreach (var primitive in mesh.Data.Shadow) highest = Math.Max(highest, primitive.Id);

        return highest + 1;
    }

    static void MatchRangeCount(Mesh mesh, IReadOnlyList<ImportedGroup> groups,
        List<(int Start, int Count, int IndexStart, int IndexCount)> ranges, ref ulong next)
    {
        if (mesh.Materials.Count == 0)
            throw new InvalidDataException("The mesh lists no material to copy for its draw ranges.");
        if (mesh.Instancing.Count == 0)
            throw new InvalidDataException("The mesh lists no instancing entry to copy for its draw ranges.");

        var oldMaterials = new List<MeshMaterial>(mesh.Materials);
        var oldInstancing = new List<MeshInstancing>(mesh.Instancing);
        var used = new HashSet<int>();
        mesh.Materials.Clear();
        mesh.Instancing.Clear();

        for (int i = 0; i < ranges.Count; i++)
        {
            int source = FindMaterialSource(groups[i].Name, oldMaterials, oldInstancing, used);
            if (source < 0 && i < Math.Min(oldMaterials.Count, oldInstancing.Count) && !used.Contains(i))
                source = i;
            if (source < 0)
                source = Math.Min(oldMaterials.Count, oldInstancing.Count) - 1;

            MeshMaterial material = oldMaterials[source];
            MeshInstancing instancing = oldInstancing[source];
            bool reused = used.Add(source);
            mesh.Materials.Add(new MeshMaterial
            {
                Id = reused ? material.Id : next++,
                ReferenceTag = material.ReferenceTag,
                Global = material.Global,
                MaterialId = material.MaterialId,
                HandleTag = material.HandleTag,
                HandleId = material.HandleId,
            });
            mesh.Instancing.Add(new MeshInstancing
            {
                Id = reused ? instancing.Id : next++,
                ShadowCaster = instancing.ShadowCaster,
                SubMeshIndex = checked((ushort)i),
                Padding = instancing.Padding,
                MaterialType = instancing.MaterialType,
                VertexCount = ranges[i].Count,
                MaterialPointerTag = instancing.MaterialPointerTag,
                MaterialId = instancing.MaterialId,
                BoneTable = (byte[])instancing.BoneTable.Clone(),
            });
        }

        for (int i = 0; i < ranges.Count; i++)
        {
            if (ranges[i].Count > ushort.MaxValue)
                throw new InvalidDataException(
                    $"Draw range {i} has {ranges[i].Count} vertices; the game mesh format stores at most {ushort.MaxValue} per range.");
            mesh.Instancing[i].SubMeshIndex = checked((ushort)i);
            mesh.Instancing[i].VertexCount = ranges[i].Count;
        }
    }

    static int FindMaterialSource(string name, IReadOnlyList<MeshMaterial> materials,
        IReadOnlyList<MeshInstancing> instancing, HashSet<int> used)
    {
        const string prefix = "Material_";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !ulong.TryParse(name[prefix.Length..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out ulong materialId))
            return -1;

        for (int i = 0; i < Math.Min(materials.Count, instancing.Count); i++)
            if (!used.Contains(i) && materials[i].MaterialId == materialId)
                return i;
        return -1;
    }

    static void ValidateGpuLayout(Mesh mesh,
        List<(int Start, int Count, int IndexStart, int IndexCount)> ranges, int indexCount)
    {
        var clustered = mesh.Clustered
            ?? throw new InvalidDataException("The imported mesh lost its clustered GPU buffers.");
        if (clustered.VertexStride <= 0
            || clustered.VertexBuffer.Length % clustered.VertexStride != 0)
            throw new InvalidDataException("The imported vertex buffer is not aligned to its stride.");
        int indexSize = mesh.Data.Indices32Bit ? 4 : 2;
        if (clustered.IndexBuffer.Length != checked(indexCount * indexSize))
            throw new InvalidDataException("The imported index-buffer size does not match its index count.");
        if (mesh.Data.Standard.Count != ranges.Count
            || (mesh.Data.Shadow.Count != 0 && mesh.Data.Shadow.Count != ranges.Count)
            || mesh.Materials.Count != ranges.Count
            || mesh.Instancing.Count != ranges.Count
            || clustered.DrawPrimitiveCount != ranges.Count
            || clustered.ClustersPerDrawPrimitive.Length != ranges.Count
            || clustered.VertexOffsetPerDrawPrimitive.Length != ranges.Count
            || clustered.PrimitiveDescriptions.Length != ranges.Count * DescriptionSize)
            throw new InvalidDataException("The imported GPU draw tables disagree about their range count.");

        for (int i = 0; i < ranges.Count; i++)
        {
            MeshPrimitive primitive = mesh.Data.Standard[i];
            MeshInstancing instancing = mesh.Instancing[i];
            if (primitive.MinIndex != ranges[i].Start
                || primitive.VertexCount != ranges[i].Count
                || primitive.StartIndex != ranges[i].IndexStart
                || primitive.TriangleCount * 3 != ranges[i].IndexCount
                || instancing.SubMeshIndex != i
                || instancing.VertexCount != ranges[i].Count)
                throw new InvalidDataException(
                    $"Imported draw range {i} disagrees with its primitive or instancing record.");
        }
    }

    static byte[] WriteIndices(List<int> indices, bool wide)
    {
        var buffer = new byte[indices.Count * (wide ? 4 : 2)];

        for (int i = 0; i < indices.Count; i++)
        {
            if (wide)
                BitConverter.TryWriteBytes(buffer.AsSpan(i * 4), indices[i]);
            else
                BitConverter.TryWriteBytes(buffer.AsSpan(i * 2), (ushort)indices[i]);
        }

        return buffer;
    }

    static int[] Filled(int count, int value)
    {
        var values = new int[count];
        Array.Fill(values, value);
        return values;
    }

    static int[] VertexOffsets(List<(int Start, int Count, int IndexStart, int IndexCount)> ranges, int stride)
    {
        var offsets = new int[ranges.Count];

        for (int i = 0; i < ranges.Count; i++)
            offsets[i] = ranges[i].Start * stride / 4;

        return offsets;
    }

    static byte[] Descriptions(List<MeshVertex> vertices, List<(int Start, int Count, int IndexStart, int IndexCount)> ranges, int stride)
    {
        var buffer = new byte[ranges.Count * DescriptionSize];

        for (int i = 0; i < ranges.Count; i++)
        {
            var (start, count, indexStart, indexCount) = ranges[i];
            var lo = new Vector3(float.MaxValue);
            var hi = new Vector3(float.MinValue);

            for (int v = start; v < start + count; v++)
            {
                lo = Vector3.Min(lo, vertices[v].Position);
                hi = Vector3.Max(hi, vertices[v].Position);
            }

            if (count == 0)
            {
                lo = Vector3.Zero;
                hi = Vector3.Zero;
            }

            var center = (lo + hi) / 2;
            var half = (hi - lo) / 2;
            var target = buffer.AsSpan(i * DescriptionSize);

            BitConverter.TryWriteBytes(target, center.X);
            BitConverter.TryWriteBytes(target[4..], center.Y);
            BitConverter.TryWriteBytes(target[8..], center.Z);
            BitConverter.TryWriteBytes(target[12..], half.X);
            BitConverter.TryWriteBytes(target[16..], half.Y);
            BitConverter.TryWriteBytes(target[20..], half.Z);
            BitConverter.TryWriteBytes(target[24..], stride);
            BitConverter.TryWriteBytes(target[28..], (indexCount << 8) | 1);
            BitConverter.TryWriteBytes(target[32..], indexStart);
        }

        return buffer;
    }

    static (Vector3 Low, Vector3 High) Bounds(List<MeshVertex> vertices)
    {
        var lo = new Vector3(float.MaxValue);
        var hi = new Vector3(float.MinValue);

        foreach (var vertex in vertices)
        {
            lo = Vector3.Min(lo, vertex.Position);
            hi = Vector3.Max(hi, vertex.Position);
        }

        return vertices.Count > 0 ? (lo, hi) : (Vector3.Zero, Vector3.Zero);
    }

    static float Quantization(Vector3 lo, Vector3 hi, bool skinned)
    {
        float widest = 0;
        foreach (float value in new[] { lo.X, lo.Y, lo.Z, hi.X, hi.Y, hi.Z })
            widest = MathF.Max(widest, MathF.Abs(value));

        if (widest <= 0)
            return 1;

        return skinned ? widest : MathF.Ceiling(widest);
    }

    static float UvQuantization(List<MeshVertex> vertices)
    {
        float widest = 0;

        foreach (var vertex in vertices)
            foreach (var uv in vertex.Uv)
                widest = MathF.Max(widest, MathF.Max(MathF.Abs(uv.X), MathF.Abs(uv.Y)));

        return widest > 0 ? widest : 1;
    }
}
