using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Wildlands.Formats.Models;

/// <summary>Strict glTF 2.0 geometry reader used by the mesh replacement pipeline.</summary>
static class GltfImport
{
    const uint Magic = 0x46546C67;
    const uint JsonChunk = 0x4E4F534A;
    const uint BinaryChunk = 0x004E4942;

    const int SignedByte = 5120;
    const int UnsignedByte = 5121;
    const int SignedShort = 5122;
    const int UnsignedShort = 5123;
    const int UnsignedInt = 5125;
    const int Float = 5126;

    sealed class AccessorData
    {
        public required byte[] Buffer { get; init; }
        public required int Offset { get; init; }
        public required int Count { get; init; }
        public required int ComponentType { get; init; }
        public required string Type { get; init; }
        public required int ElementSize { get; init; }
        public required int Stride { get; init; }
        public required bool Normalized { get; init; }

        public ReadOnlySpan<byte> Element(int index) =>
            Buffer.AsSpan(checked(Offset + index * Stride), ElementSize);
    }

    readonly record struct MeshInstance(int Mesh, int? Skin, Matrix4x4 World, string Name);

    public static ImportedGeometry Read(string path)
    {
        var (root, buffers) = Open(path);
        ValidateExtensions(root);
        var meshes = root["meshes"]?.AsArray()
            ?? throw new InvalidDataException("The file holds no meshes.");
        if (meshes.Count == 0)
            throw new InvalidDataException("The file holds no meshes.");

        var accessors = root["accessors"]?.AsArray() ?? [];
        var views = root["bufferViews"]?.AsArray() ?? [];
        var materials = root["materials"]?.AsArray();
        var geometry = new ImportedGeometry();
        var jointSlots = new Dictionary<uint, byte>();

        foreach (var instance in Instances(root, meshes.Count))
        {
            var mesh = meshes[instance.Mesh]?.AsObject()
                ?? throw new InvalidDataException($"Mesh {instance.Mesh} is missing.");
            var primitives = mesh["primitives"]?.AsArray()
                ?? throw new InvalidDataException($"Mesh {instance.Mesh} has no primitives.");

            foreach (var primitiveNode in primitives)
            {
                var primitive = primitiveNode?.AsObject()
                    ?? throw new InvalidDataException($"Mesh {instance.Mesh} contains an empty primitive.");
                int mode = primitive["mode"]?.GetValue<int>() ?? 4;
                if (mode != 4)
                    throw new NotSupportedException(
                        $"{instance.Name} uses primitive mode {mode}. Replace mesh supports triangle-list primitives only; triangulate the model before export.");
                if ((primitive["targets"]?.AsArray().Count ?? 0) > 0)
                    throw new NotSupportedException(
                        $"{instance.Name} uses morph targets. Apply the desired shape before exporting the replacement mesh.");

                var attributes = primitive["attributes"]?.AsObject()
                    ?? throw new InvalidDataException($"A primitive in {instance.Name} carries no attributes.");
                int material = primitive["material"]?.GetValue<int>() ?? -1;
                if (material >= (materials?.Count ?? 0))
                    throw new InvalidDataException($"A primitive refers to material {material}, which does not exist.");
                string label = material >= 0
                    ? materials![material]?["name"]?.GetValue<string>() ?? $"material_{material}"
                    : "default";

                var positions = ReadVec3(accessors, views, buffers, attributes, "POSITION", required: true)!;
                var normals = ReadVec3(accessors, views, buffers, attributes, "NORMAL", required: false);
                var tangents = ReadVec4(accessors, views, buffers, attributes, "TANGENT");
                var colours = ReadColour(accessors, views, buffers, attributes);
                if (tangents is not null && normals is null)
                    throw new InvalidDataException("A primitive has tangents but no normals. Export normals with the mesh or omit its tangents.");

                MatchCount("NORMAL", normals?.Length, positions.Length);
                MatchCount("TANGENT", tangents?.Length, positions.Length);
                MatchCount("COLOR_0", colours?.Length, positions.Length);

                var uvSets = new List<Vector2[]>();
                foreach (string key in AttributeKeys(attributes, "TEXCOORD_"))
                {
                    var values = ReadVec2(accessors, views, buffers, attributes, key)!;
                    MatchCount(key, values.Length, positions.Length);
                    uvSets.Add(values);
                }

                var jointBlocks = new List<ushort[][]>();
                var weightBlocks = new List<float[][]>();
                foreach (string key in AttributeKeys(attributes, "JOINTS_"))
                {
                    string suffix = key["JOINTS_".Length..];
                    string weightKey = "WEIGHTS_" + suffix;
                    var joints = ReadJoints(accessors, views, buffers, attributes, key);
                    var weights = ReadWeights(accessors, views, buffers, attributes, weightKey);
                    MatchCount(key, joints.Length, positions.Length);
                    MatchCount(weightKey, weights.Length, positions.Length);
                    jointBlocks.Add(joints);
                    weightBlocks.Add(weights);
                }

                var orphanWeights = AttributeKeys(attributes, "WEIGHTS_");
                if (orphanWeights.Count != weightBlocks.Count)
                    throw new InvalidDataException("The primitive contains WEIGHTS attributes without matching JOINTS attributes.");

                List<uint>? skinJoints = null;
                byte[]? skinSlots = null;
                if (jointBlocks.Count > 0)
                {
                    if (instance.Skin is null)
                        throw new InvalidDataException(
                            $"{instance.Name} contains skin weights, but its scene node does not select a skin.");
                    skinJoints = SkinJoints(root, instance.Skin.Value);
                    skinSlots = new byte[skinJoints.Count];
                    for (int joint = 0; joint < skinJoints.Count; joint++)
                        skinSlots[joint] = JointSlot(skinJoints[joint], geometry, jointSlots);
                }

                if (!Matrix4x4.Invert(instance.World, out Matrix4x4 inverseWorld))
                    throw new InvalidDataException($"{instance.Name} has a singular transform that cannot be applied to its normals.");
                Matrix4x4 normalMatrix = Matrix4x4.Transpose(inverseWorld);

                var group = new ImportedGroup { Name = label, HasTangents = tangents is not null };
                for (int i = 0; i < positions.Length; i++)
                {
                    Vector3 normal = normals is null ? Vector3.Zero : Unit(Vector3.TransformNormal(normals[i], normalMatrix), "normal");
                    var vertex = new MeshVertex
                    {
                        Position = Finite(Vector3.Transform(positions[i], instance.World), "position"),
                        Normal = normal,
                        Uv = new Vector2[Math.Max(uvSets.Count, 1)],
                        Color = colours?[i] ?? 0xFFFFFFFF,
                    };

                    for (int set = 0; set < uvSets.Count; set++)
                        vertex.Uv[set] = new Vector2(uvSets[set][i].X, 1 - uvSets[set][i].Y);

                    if (tangents is not null)
                    {
                        Vector3 sourceTangent = new(tangents[i].X, tangents[i].Y, tangents[i].Z);
                        sourceTangent = Unit(sourceTangent, "tangent");
                        Vector3 sourceBinormal = Vector3.Cross(Unit(normals![i], "normal"), sourceTangent)
                            * (tangents[i].W < 0 ? -1f : 1f);
                        Vector3 tangent = Vector3.TransformNormal(sourceTangent, instance.World);
                        tangent -= normal * Vector3.Dot(normal, tangent);
                        tangent = Unit(tangent, "transformed tangent");
                        Vector3 transformedBinormal = Vector3.TransformNormal(sourceBinormal, instance.World);
                        float sign = Vector3.Dot(Vector3.Cross(normal, tangent), transformedBinormal) < 0 ? -1f : 1f;
                        vertex.Tangent = tangent;
                        vertex.Binormal = Vector3.Cross(normal, tangent) * sign;
                        vertex.TangentSign = sign < 0 ? (byte)0 : (byte)255;
                    }

                    if (jointBlocks.Count > 0)
                    {
                        var joints = new byte[jointBlocks.Count * 4];
                        var weights = new byte[jointBlocks.Count * 4];
                        for (int block = 0; block < jointBlocks.Count; block++)
                            for (int slot = 0; slot < 4; slot++)
                            {
                                float weight = weightBlocks[block][i][slot];
                                int destination = block * 4 + slot;
                                if (weight <= 0)
                                    continue;

                                ushort sourceJoint = jointBlocks[block][i][slot];
                                if (sourceJoint >= skinJoints!.Count)
                                    throw new InvalidDataException(
                                        $"Vertex {i} refers to joint {sourceJoint}, but the selected skin has only {skinJoints.Count} joints.");
                                joints[destination] = skinSlots![sourceJoint];
                                weights[destination] = (byte)Math.Clamp(MathF.Round(weight * 255f), 0, 255);
                            }

                        vertex.JointIndices = joints;
                        vertex.JointWeights = weights;
                    }

                    group.Vertices.Add(vertex);
                }

                group.Indices.AddRange(ReadIndices(accessors, views, buffers, primitive, positions.Length));
                if (instance.World.GetDeterminant() < 0)
                    for (int i = 0; i + 2 < group.Indices.Count; i += 3)
                        (group.Indices[i + 1], group.Indices[i + 2]) = (group.Indices[i + 2], group.Indices[i + 1]);
                if (normals is null)
                    GenerateNormals(group);

                geometry.Groups.Add(group);
                geometry.HasColour |= colours is not null;
                geometry.HasSkinning |= jointBlocks.Count > 0;
                geometry.UvSets = Math.Max(geometry.UvSets, uvSets.Count);
            }
        }

        geometry.HasTangents = geometry.Groups.Count > 0;
        foreach (var group in geometry.Groups)
            geometry.HasTangents &= group.HasTangents;
        return geometry;
    }

    static void ValidateExtensions(JsonObject root)
    {
        var required = root["extensionsRequired"]?.AsArray();
        if (required is null || required.Count == 0)
            return;
        var names = new List<string>();
        foreach (var entry in required)
            names.Add(entry?.GetValue<string>() ?? "<empty>");
        throw new NotSupportedException(
            "This glTF requires extension(s) the mesh importer does not decode: " + string.Join(", ", names)
            + ". Export an uncompressed glTF 2.0 mesh with modifiers applied.");
    }

    static byte JointSlot(uint name, ImportedGeometry geometry, Dictionary<uint, byte> slots)
    {
        if (slots.TryGetValue(name, out byte known))
            return known;
        if (geometry.JointNames.Count >= byte.MaxValue + 1)
            throw new NotSupportedException("The imported scene uses more than 256 distinct joints, which the Wildlands mesh format cannot store.");
        byte slot = checked((byte)geometry.JointNames.Count);
        geometry.JointNames.Add(name);
        slots[name] = slot;
        return slot;
    }

    static void GenerateNormals(ImportedGroup group)
    {
        var accumulated = new Vector3[group.Vertices.Count];
        for (int i = 0; i + 2 < group.Indices.Count; i += 3)
        {
            int a = group.Indices[i], b = group.Indices[i + 1], c = group.Indices[i + 2];
            if ((uint)a >= group.Vertices.Count || (uint)b >= group.Vertices.Count || (uint)c >= group.Vertices.Count)
                throw new InvalidDataException($"Group {group.Name} contains an index outside its vertex array.");
            Vector3 face = Vector3.Cross(group.Vertices[b].Position - group.Vertices[a].Position,
                group.Vertices[c].Position - group.Vertices[a].Position);
            accumulated[a] += face; accumulated[b] += face; accumulated[c] += face;
        }

        for (int i = 0; i < group.Vertices.Count; i++)
            group.Vertices[i].Normal = accumulated[i].LengthSquared() > 1e-20f
                ? Vector3.Normalize(accumulated[i]) : Vector3.UnitZ;
    }

    static List<MeshInstance> Instances(JsonObject root, int meshCount)
    {
        var nodes = root["nodes"]?.AsArray();
        if (nodes is null || nodes.Count == 0)
        {
            var direct = new List<MeshInstance>(meshCount);
            for (int i = 0; i < meshCount; i++) direct.Add(new MeshInstance(i, null, Matrix4x4.Identity, $"mesh {i}"));
            return direct;
        }

        var roots = new List<int>();
        var scenes = root["scenes"]?.AsArray();
        if (scenes is not null && scenes.Count > 0)
        {
            int scene = root["scene"]?.GetValue<int>() ?? 0;
            if ((uint)scene >= scenes.Count)
                throw new InvalidDataException($"The selected scene {scene} does not exist.");
            foreach (var entry in scenes[scene]?["nodes"]?.AsArray() ?? [])
                roots.Add(entry?.GetValue<int>() ?? throw new InvalidDataException("The scene contains an empty node reference."));
        }
        else
        {
            var child = new HashSet<int>();
            for (int i = 0; i < nodes.Count; i++)
                foreach (var entry in nodes[i]?["children"]?.AsArray() ?? [])
                    child.Add(entry?.GetValue<int>() ?? throw new InvalidDataException($"Node {i} contains an empty child reference."));
            for (int i = 0; i < nodes.Count; i++) if (!child.Contains(i)) roots.Add(i);
            if (roots.Count == 0)
                throw new InvalidDataException("The node graph has no root; it may contain a cycle.");
        }

        var result = new List<MeshInstance>();
        var visited = new HashSet<int>();
        foreach (int rootNode in roots)
            Visit(rootNode, Matrix4x4.Identity, nodes, meshCount, visited, new HashSet<int>(), result);
        if (result.Count == 0)
            throw new InvalidDataException("The active scene contains no mesh nodes.");
        return result;
    }

    static void Visit(int index, Matrix4x4 parent, JsonArray nodes, int meshCount,
        HashSet<int> visited, HashSet<int> path, List<MeshInstance> result)
    {
        if ((uint)index >= nodes.Count)
            throw new InvalidDataException($"Node {index} does not exist.");
        if (!path.Add(index))
            throw new InvalidDataException($"The node graph contains a cycle at node {index}.");
        if (!visited.Add(index))
            throw new InvalidDataException($"Node {index} is referenced more than once in the active scene.");

        var node = nodes[index]?.AsObject() ?? throw new InvalidDataException($"Node {index} is missing.");
        Matrix4x4 world = LocalTransform(node, index) * parent;
        string name = node["name"]?.GetValue<string>() ?? $"node {index}";
        if (node["mesh"] is { } meshNode)
        {
            int mesh = meshNode.GetValue<int>();
            if ((uint)mesh >= meshCount)
                throw new InvalidDataException($"{name} refers to mesh {mesh}, which does not exist.");
            int? skin = node["skin"]?.GetValue<int>();
            result.Add(new MeshInstance(mesh, skin, world, name));
        }

        foreach (var child in node["children"]?.AsArray() ?? [])
            Visit(child?.GetValue<int>() ?? throw new InvalidDataException($"{name} contains an empty child reference."),
                world, nodes, meshCount, visited, path, result);
        path.Remove(index);
    }

    static Matrix4x4 LocalTransform(JsonObject node, int index)
    {
        bool matrix = node["matrix"] is not null;
        bool trs = node["translation"] is not null || node["rotation"] is not null || node["scale"] is not null;
        if (matrix && trs)
            throw new InvalidDataException($"Node {index} defines both a matrix and TRS transforms.");
        if (matrix)
        {
            float[] m = Floats(node["matrix"], 16, $"node {index} matrix");
            return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        }

        float[] t = node["translation"] is null ? [0, 0, 0] : Floats(node["translation"], 3, $"node {index} translation");
        float[] r = node["rotation"] is null ? [0, 0, 0, 1] : Floats(node["rotation"], 4, $"node {index} rotation");
        float[] s = node["scale"] is null ? [1, 1, 1] : Floats(node["scale"], 3, $"node {index} scale");
        var rotation = new Quaternion(r[0], r[1], r[2], r[3]);
        if (rotation.LengthSquared() < 1e-20f)
            throw new InvalidDataException($"Node {index} has a zero-length rotation quaternion.");
        rotation = Quaternion.Normalize(rotation);
        return Matrix4x4.CreateScale(s[0], s[1], s[2]) * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
    }

    static float[] Floats(JsonNode? node, int expected, string label)
    {
        var array = node?.AsArray() ?? throw new InvalidDataException($"The {label} is not an array.");
        if (array.Count != expected)
            throw new InvalidDataException($"The {label} contains {array.Count} values instead of {expected}.");
        var result = new float[expected];
        for (int i = 0; i < expected; i++)
        {
            result[i] = array[i]?.GetValue<float>() ?? throw new InvalidDataException($"The {label} contains an empty value.");
            if (!float.IsFinite(result[i])) throw new InvalidDataException($"The {label} contains a non-finite value.");
        }
        return result;
    }

    static List<uint> SkinJoints(JsonObject root, int skinIndex)
    {
        var skins = root["skins"]?.AsArray() ?? throw new InvalidDataException("A mesh node selects a skin, but the file has no skins.");
        var nodes = root["nodes"]?.AsArray() ?? throw new InvalidDataException("A skin is present, but the file has no nodes.");
        if ((uint)skinIndex >= skins.Count)
            throw new InvalidDataException($"Skin {skinIndex} does not exist.");
        var result = new List<uint>();
        foreach (var entry in skins[skinIndex]?["joints"]?.AsArray() ?? [])
        {
            int node = entry?.GetValue<int>() ?? throw new InvalidDataException($"Skin {skinIndex} contains an empty joint reference.");
            if ((uint)node >= nodes.Count) throw new InvalidDataException($"Skin {skinIndex} refers to node {node}, which does not exist.");
            string name = nodes[node]?["name"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException($"Joint node {node} has no name.");
            result.Add(HashOf(name));
        }
        if (result.Count == 0) throw new InvalidDataException($"Skin {skinIndex} has no joints.");
        return result;
    }

    static uint HashOf(string name) => name.StartsWith("Bone_", StringComparison.OrdinalIgnoreCase)
        && uint.TryParse(name[5..], System.Globalization.NumberStyles.HexNumber, null, out uint hash)
        ? hash : ResourceTypes.Crc32(name);

    static (JsonObject Root, List<byte[]> Buffers) Open(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        JsonObject root;
        byte[]? embedded = null;
        if (bytes.Length >= 12 && BitConverter.ToUInt32(bytes) == Magic)
        {
            if (BitConverter.ToUInt32(bytes, 4) != 2) throw new NotSupportedException("Only glTF 2.0 files are supported.");
            uint declared = BitConverter.ToUInt32(bytes, 8);
            if (declared != bytes.Length) throw new InvalidDataException("The glb header length does not match the file length.");
            int at = 12;
            JsonObject? document = null;
            while (at + 8 <= bytes.Length)
            {
                int length = BitConverter.ToInt32(bytes, at);
                uint kind = BitConverter.ToUInt32(bytes, at + 4);
                at += 8;
                if (length < 0 || (long)at + length > bytes.Length)
                    throw new InvalidDataException("The glb file has a chunk that runs past its end.");
                if (kind == JsonChunk)
                {
                    if (document is not null) throw new InvalidDataException("The glb file contains more than one JSON chunk.");
                    document = JsonNode.Parse(Encoding.UTF8.GetString(bytes, at, length))?.AsObject();
                }
                else if (kind == BinaryChunk)
                {
                    if (embedded is not null) throw new InvalidDataException("The glb file contains more than one binary chunk.");
                    embedded = bytes[at..(at + length)];
                }
                at += length;
            }
            if (at != bytes.Length) throw new InvalidDataException("The glb file ends with an incomplete chunk header.");
            root = document ?? throw new InvalidDataException("The glb file holds no JSON chunk.");
        }
        else
        {
            root = JsonNode.Parse(Encoding.UTF8.GetString(bytes))?.AsObject()
                ?? throw new InvalidDataException("The file is neither glb nor readable glTF JSON.");
        }

        string version = root["asset"]?["version"]?.GetValue<string>() ?? "";
        if (!version.StartsWith("2.", StringComparison.Ordinal)) throw new NotSupportedException("Only glTF 2.0 files are supported.");
        var definitions = root["buffers"]?.AsArray() ?? throw new InvalidDataException("The file defines no buffers.");
        var buffers = new List<byte[]>(definitions.Count);
        for (int i = 0; i < definitions.Count; i++)
        {
            var definition = definitions[i]?.AsObject() ?? throw new InvalidDataException($"Buffer {i} is missing.");
            string? uri = definition["uri"]?.GetValue<string>();
            byte[] payload;
            if (uri is null)
            {
                if (i != 0 || embedded is null) throw new InvalidDataException($"Buffer {i} has no URI or embedded glb data.");
                payload = embedded;
            }
            else if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',');
                if (comma < 0 || !uri[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException($"Buffer {i} uses a non-base64 data URI.");
                payload = Convert.FromBase64String(uri[(comma + 1)..]);
            }
            else
            {
                string file = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path) ?? "", Uri.UnescapeDataString(uri)));
                payload = File.ReadAllBytes(file);
            }
            int declaredLength = definition["byteLength"]?.GetValue<int>() ?? -1;
            if (declaredLength < 0 || declaredLength > payload.Length)
                throw new InvalidDataException($"Buffer {i} is shorter than its declared byteLength.");
            buffers.Add(payload.Length == declaredLength ? payload : payload[..declaredLength]);
        }
        return (root, buffers);
    }

    static AccessorData Accessor(JsonArray accessors, JsonArray views, IReadOnlyList<byte[]> buffers, int accessorIndex)
    {
        if ((uint)accessorIndex >= accessors.Count) throw new InvalidDataException($"Accessor {accessorIndex} does not exist.");
        var accessor = accessors[accessorIndex]?.AsObject() ?? throw new InvalidDataException($"Accessor {accessorIndex} is missing.");
        if (accessor["sparse"] is not null) throw new NotSupportedException($"Accessor {accessorIndex} is sparse. Apply or expand sparse accessors before import.");
        int count = accessor["count"]?.GetValue<int>() ?? -1;
        int component = accessor["componentType"]?.GetValue<int>() ?? 0;
        string type = accessor["type"]?.GetValue<string>() ?? "";
        if (count < 0) throw new InvalidDataException($"Accessor {accessorIndex} has an invalid count.");
        int componentSize = ComponentSize(component);
        int elementSize = checked(componentSize * Lanes(type));
        int viewIndex = accessor["bufferView"]?.GetValue<int>()
            ?? throw new NotSupportedException($"Accessor {accessorIndex} has no buffer view.");
        if ((uint)viewIndex >= views.Count) throw new InvalidDataException($"Buffer view {viewIndex} does not exist.");
        var view = views[viewIndex]?.AsObject() ?? throw new InvalidDataException($"Buffer view {viewIndex} is missing.");
        int bufferIndex = view["buffer"]?.GetValue<int>() ?? 0;
        if ((uint)bufferIndex >= buffers.Count) throw new InvalidDataException($"Buffer view {viewIndex} selects missing buffer {bufferIndex}.");
        int viewOffset = view["byteOffset"]?.GetValue<int>() ?? 0;
        int viewLength = view["byteLength"]?.GetValue<int>() ?? -1;
        int accessorOffset = accessor["byteOffset"]?.GetValue<int>() ?? 0;
        int stride = view["byteStride"]?.GetValue<int>() ?? elementSize;
        if (viewOffset < 0 || viewLength < 0 || accessorOffset < 0 || stride < elementSize || stride % componentSize != 0)
            throw new InvalidDataException($"Accessor {accessorIndex} has invalid offsets, length, or stride.");
        byte[] buffer = buffers[bufferIndex];
        if ((long)viewOffset + viewLength > buffer.Length) throw new InvalidDataException($"Buffer view {viewIndex} runs past buffer {bufferIndex}.");
        long required = count == 0 ? 0 : (long)(count - 1) * stride + elementSize;
        if ((long)accessorOffset + required > viewLength) throw new InvalidDataException($"Accessor {accessorIndex} runs past buffer view {viewIndex}.");
        int absolute = checked(viewOffset + accessorOffset);
        if (absolute % componentSize != 0) throw new InvalidDataException($"Accessor {accessorIndex} is not aligned to its component size.");
        return new AccessorData
        {
            Buffer = buffer, Offset = absolute, Count = count, ComponentType = component, Type = type,
            ElementSize = elementSize, Stride = stride, Normalized = accessor["normalized"]?.GetValue<bool>() ?? false,
        };
    }

    static int ComponentSize(int type) => type switch
    {
        SignedByte or UnsignedByte => 1,
        SignedShort or UnsignedShort => 2,
        UnsignedInt or Float => 4,
        _ => throw new NotSupportedException($"glTF component type {type} is not supported."),
    };

    static int Lanes(string type) => type switch
    {
        "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" => 4, "MAT2" => 4, "MAT3" => 9, "MAT4" => 16,
        _ => throw new InvalidDataException($"Unknown glTF accessor type '{type}'."),
    };

    static Vector3[]? ReadVec3(JsonArray accessors, JsonArray views, IReadOnlyList<byte[]> buffers,
        JsonObject attributes, string key, bool required)
    {
        if (attributes[key] is not { } node)
            return required ? throw new InvalidDataException($"A primitive carries no {key} attribute.") : null;
        var data = Accessor(accessors, views, buffers, node.GetValue<int>());
        Require(data, key, "VEC3", Float);
        var result = new Vector3[data.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var e = data.Element(i);
            result[i] = Finite(new Vector3(BitConverter.ToSingle(e), BitConverter.ToSingle(e[4..]), BitConverter.ToSingle(e[8..])), key);
        }
        return result;
    }

    static Vector4[]? ReadVec4(JsonArray accessors, JsonArray views, IReadOnlyList<byte[]> buffers,
        JsonObject attributes, string key)
    {
        if (attributes[key] is not { } node) return null;
        var data = Accessor(accessors, views, buffers, node.GetValue<int>());
        Require(data, key, "VEC4", Float);
        var result = new Vector4[data.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var e = data.Element(i);
            result[i] = new Vector4(BitConverter.ToSingle(e), BitConverter.ToSingle(e[4..]), BitConverter.ToSingle(e[8..]), BitConverter.ToSingle(e[12..]));
            if (!Finite(result[i])) throw new InvalidDataException($"{key} contains a non-finite value.");
        }
        return result;
    }

    static Vector2[]? ReadVec2(JsonArray accessors, JsonArray views, IReadOnlyList<byte[]> buffers,
        JsonObject attributes, string key)
    {
        if (attributes[key] is not { } node) return null;
        var data = Accessor(accessors, views, buffers, node.GetValue<int>());
        if (data.Type != "VEC2") throw new InvalidDataException($"{key} must use a VEC2 accessor.");
        if (data.ComponentType is not (Float or UnsignedByte or UnsignedShort)) throw new InvalidDataException($"{key} uses an unsupported component type.");
        if (data.ComponentType != Float && !data.Normalized) throw new InvalidDataException($"Integer {key} data must be normalized.");
        if (data.ComponentType == Float && data.Normalized) throw new InvalidDataException($"Float {key} data cannot be marked normalized.");
        var result = new Vector2[data.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var e = data.Element(i);
            int size = ComponentSize(data.ComponentType);
            result[i] = new Vector2(ComponentFloat(e, 0, data), ComponentFloat(e, size, data));
            if (!float.IsFinite(result[i].X) || !float.IsFinite(result[i].Y)) throw new InvalidDataException($"{key} contains a non-finite value.");
        }
        return result;
    }

    static uint[]? ReadColour(JsonArray accessors, JsonArray views, IReadOnlyList<byte[]> buffers, JsonObject attributes)
    {
        if (attributes["COLOR_0"] is not { } node) return null;
        var data = Accessor(accessors, views, buffers, node.GetValue<int>());
        int lanes = data.Type == "VEC3" ? 3 : data.Type == "VEC4" ? 4 : throw new InvalidDataException("COLOR_0 must use VEC3 or VEC4.");
        if (data.ComponentType is not (Float or UnsignedByte or UnsignedShort)) throw new InvalidDataException("COLOR_0 uses an unsupported component type.");
        if (data.ComponentType != Float && !data.Normalized) throw new InvalidDataException("Integer COLOR_0 data must be normalized.");
        if (data.ComponentType == Float && data.Normalized) throw new InvalidDataException("Float COLOR_0 data cannot be marked normalized.");
        var result = new uint[data.Count];
        for (int i = 0; i < result.Length; i++)
        {
            var e = data.Element(i);
            byte[] c = [255, 255, 255, 255];
            int size = ComponentSize(data.ComponentType);
            for (int lane = 0; lane < lanes; lane++)
                c[lane] = (byte)Math.Clamp(MathF.Round(ComponentFloat(e, lane * size, data) * 255f), 0, 255);
            result[i] = (uint)(c[0] | c[1] << 8 | c[2] << 16 | c[3] << 24);
        }
        return result;
    }

    static ushort[][] ReadJoints(JsonArray accessors, JsonArray views, IReadOnlyList<byte[]> buffers,
        JsonObject attributes, string key)
    {
        var data = Accessor(accessors, views, buffers, attributes[key]!.GetValue<int>());
        if (data.Type != "VEC4" || data.ComponentType is not (UnsignedByte or UnsignedShort) || data.Normalized)
            throw new InvalidDataException($"{key} must be a non-normalized unsigned-byte or unsigned-short VEC4.");
        var result = new ushort[data.Count][];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new ushort[4];
            var e = data.Element(i);
            for (int lane = 0; lane < 4; lane++)
                result[i][lane] = data.ComponentType == UnsignedByte ? e[lane] : BitConverter.ToUInt16(e[(lane * 2)..]);
        }
        return result;
    }

    static float[][] ReadWeights(JsonArray accessors, JsonArray views, IReadOnlyList<byte[]> buffers,
        JsonObject attributes, string key)
    {
        if (attributes[key] is not { } node) throw new InvalidDataException($"{key} is missing while matching joints are present.");
        var data = Accessor(accessors, views, buffers, node.GetValue<int>());
        if (data.Type != "VEC4" || data.ComponentType is not (Float or UnsignedByte or UnsignedShort))
            throw new InvalidDataException($"{key} uses an unsupported accessor format.");
        if (data.ComponentType != Float && !data.Normalized) throw new InvalidDataException($"Integer {key} data must be normalized.");
        if (data.ComponentType == Float && data.Normalized) throw new InvalidDataException($"Float {key} data cannot be marked normalized.");
        var result = new float[data.Count][];
        int size = ComponentSize(data.ComponentType);
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = new float[4];
            var e = data.Element(i);
            for (int lane = 0; lane < 4; lane++)
            {
                float value = ComponentFloat(e, lane * size, data);
                if (!float.IsFinite(value) || value < 0 || value > 1) throw new InvalidDataException($"{key} contains a weight outside 0..1.");
                result[i][lane] = value;
            }
        }
        return result;
    }

    static List<int> ReadIndices(JsonArray accessors, JsonArray views, IReadOnlyList<byte[]> buffers,
        JsonObject primitive, int vertexCount)
    {
        var result = new List<int>();
        if (primitive["indices"] is not { } node)
        {
            for (int i = 0; i < vertexCount; i++) result.Add(i);
        }
        else
        {
            var data = Accessor(accessors, views, buffers, node.GetValue<int>());
            if (data.Type != "SCALAR" || data.ComponentType is not (UnsignedByte or UnsignedShort or UnsignedInt))
                throw new InvalidDataException("Triangle indices must be unsigned SCALAR values.");
            if (data.Normalized) throw new InvalidDataException("Triangle indices cannot be normalized.");
            for (int i = 0; i < data.Count; i++)
            {
                var e = data.Element(i);
                uint value = data.ComponentType switch
                {
                    UnsignedByte => e[0], UnsignedShort => BitConverter.ToUInt16(e), _ => BitConverter.ToUInt32(e),
                };
                if (value >= vertexCount) throw new InvalidDataException($"Triangle index {value} is outside the primitive's {vertexCount} vertices.");
                result.Add((int)value);
            }
        }
        if (result.Count % 3 != 0) throw new InvalidDataException($"A triangle-list primitive contains {result.Count} indices, which is not a multiple of three.");
        return result;
    }

    static float ComponentFloat(ReadOnlySpan<byte> element, int offset, AccessorData data) => data.ComponentType switch
    {
        Float => BitConverter.ToSingle(element[offset..]),
        UnsignedByte => data.Normalized ? element[offset] / 255f : element[offset],
        UnsignedShort => data.Normalized ? BitConverter.ToUInt16(element[offset..]) / 65535f : BitConverter.ToUInt16(element[offset..]),
        SignedByte => data.Normalized ? Math.Max((sbyte)element[offset] / 127f, -1f) : (sbyte)element[offset],
        SignedShort => data.Normalized ? Math.Max(BitConverter.ToInt16(element[offset..]) / 32767f, -1f) : BitConverter.ToInt16(element[offset..]),
        _ => throw new InvalidDataException("The accessor component cannot be converted to float."),
    };

    static void Require(AccessorData data, string key, string type, int component)
    {
        if (data.Type != type || data.ComponentType != component)
            throw new InvalidDataException($"{key} must be stored as {type} float data.");
        if (data.Normalized)
            throw new InvalidDataException($"Float {key} data cannot be marked normalized.");
    }

    static List<string> AttributeKeys(JsonObject attributes, string prefix)
    {
        var numbered = new SortedDictionary<int, string>();
        foreach (var entry in attributes)
            if (entry.Key.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (!int.TryParse(entry.Key[prefix.Length..], out int number) || number < 0)
                    throw new InvalidDataException($"Attribute {entry.Key} has an invalid set number.");
                numbered[number] = entry.Key;
            }
        var result = new List<string>(numbered.Count);
        int expected = 0;
        foreach (var entry in numbered)
        {
            if (entry.Key != expected) throw new InvalidDataException($"Attribute sets for {prefix} are not contiguous from zero.");
            result.Add(entry.Value); expected++;
        }
        return result;
    }

    static void MatchCount(string name, int? count, int positions)
    {
        if (count is not null && count.Value != positions)
            throw new InvalidDataException($"{name} contains {count.Value} values, but POSITION contains {positions}.");
    }

    static Vector3 Unit(Vector3 value, string label)
    {
        value = Finite(value, label);
        if (value.LengthSquared() < 1e-20f) throw new InvalidDataException($"The mesh contains a zero-length {label}.");
        return Vector3.Normalize(value);
    }

    static Vector3 Finite(Vector3 value, string label)
    {
        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
            throw new InvalidDataException($"The mesh contains a non-finite {label}.");
        return value;
    }

    static bool Finite(Vector4 value) => float.IsFinite(value.X) && float.IsFinite(value.Y)
        && float.IsFinite(value.Z) && float.IsFinite(value.W);
}
