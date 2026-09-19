using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Wildlands.Formats.Models;

public sealed record SkeletonGltfImportResult(int BoneCount, int ChangedBones,
    float MaximumTranslation, float MaximumRotationDegrees);

public static class SkeletonGltf
{
    const uint Magic = 0x46546C67;
    const uint JsonChunk = 0x4E4F534A;
    const uint BinaryChunk = 0x004E4942;
    const int Float = 5126;
    const int UnsignedByte = 5121;
    const int UnsignedShort = 5123;

    public static void Write(SkeletonAsset asset, string name, string path)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (asset.Bones.Count == 0)
            throw new InvalidDataException("The Skeleton has no bones.");

        Matrix4x4[] worlds = WorldMatrices(asset.Bones);
        var binary = new MemoryStream();
        var views = new JsonArray();
        var accessors = new JsonArray();

        int positions = AddPositions(binary, views, accessors);
        int joints = AddJoints(binary, views, accessors);
        int weights = AddWeights(binary, views, accessors);
        int indices = AddIndices(binary, views, accessors);
        int inverseBindMatrices = AddBindMatrices(binary, views, accessors, worlds);

        var nodes = new JsonArray
        {
            new JsonObject
            {
                ["name"] = name + "_SkeletonReference",
                ["mesh"] = 0,
                ["skin"] = 0,
            },
        };
        var jointNodes = new JsonArray();
        for (int index = 0; index < asset.Bones.Count; index++)
        {
            SkeletonBone bone = asset.Bones[index];
            Quaternion rotation = Normalized(bone.LocalRotation, $"bone {index} local rotation");
            var node = new JsonObject
            {
                ["name"] = BoneName(bone.Name),
                ["translation"] = Vector(bone.LocalPosition),
                ["rotation"] = Rotation(rotation),
                ["extras"] = new JsonObject
                {
                    ["wildlandsBoneHash"] = bone.Name.ToString("X8"),
                    ["wildlandsBoneIndex"] = index,
                },
            };

            var children = new JsonArray();
            for (int child = 0; child < asset.Bones.Count; child++)
                if (asset.Bones[child].ParentIndex == index)
                    children.Add(child + 1);
            if (children.Count > 0)
                node["children"] = children;

            nodes.Add(node);
            jointNodes.Add(index + 1);
        }

        var sceneNodes = new JsonArray { 0 };
        foreach (int rootBone in Enumerable.Range(0, asset.Bones.Count)
                     .Where(index => asset.Bones[index].ParentIndex < 0))
            sceneNodes.Add(rootBone + 1);

        var skin = new JsonObject
        {
            ["name"] = name,
            ["joints"] = jointNodes,
            ["inverseBindMatrices"] = inverseBindMatrices,
        };
        int firstRoot = asset.Bones.FindIndex(bone => bone.ParentIndex < 0);
        if (firstRoot >= 0)
            skin["skeleton"] = firstRoot + 1;

        var root = new JsonObject
        {
            ["asset"] = new JsonObject
            {
                ["version"] = "2.0",
                ["generator"] = "Wildlands Toolkit",
                ["extras"] = new JsonObject
                {
                    ["wildlandsSkeleton"] = true,
                    ["boneCount"] = asset.Bones.Count,
                },
            },
            ["scene"] = 0,
            ["scenes"] = new JsonArray { new JsonObject { ["nodes"] = sceneNodes } },
            ["nodes"] = nodes,
            ["skins"] = new JsonArray { skin },
            ["meshes"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = name + "_SkeletonReference",
                    ["primitives"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["attributes"] = new JsonObject
                            {
                                ["POSITION"] = positions,
                                ["JOINTS_0"] = joints,
                                ["WEIGHTS_0"] = weights,
                            },
                            ["indices"] = indices,
                            ["material"] = 0,
                            ["mode"] = 4,
                        },
                    },
                },
            },
            ["materials"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "SkeletonReference_Invisible",
                    ["alphaMode"] = "BLEND",
                    ["doubleSided"] = true,
                    ["pbrMetallicRoughness"] = new JsonObject
                    {
                        ["baseColorFactor"] = new JsonArray { 0, 0, 0, 0 },
                        ["metallicFactor"] = 0,
                        ["roughnessFactor"] = 1,
                    },
                },
            },
            ["accessors"] = accessors,
            ["bufferViews"] = views,
            ["buffers"] = new JsonArray { new JsonObject { ["byteLength"] = binary.Length } },
        };

        WriteGlb(path, root, binary.ToArray());
    }

    public static SkeletonGltfImportResult Apply(SkeletonAsset asset, string path)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        JsonObject root = ReadJson(path);
        JsonArray nodes = root["nodes"]?.AsArray()
            ?? throw new InvalidDataException("The glTF file has no nodes.");
        JsonArray skins = root["skins"]?.AsArray()
            ?? throw new InvalidDataException("The glTF file has no armature skin.");
        if (skins.Count == 0)
            throw new InvalidDataException("The glTF file has no armature skin.");

        var jointNodes = new HashSet<int>();
        foreach (JsonNode? skinNode in skins)
        {
            JsonArray skinJoints = skinNode?["joints"]?.AsArray()
                ?? throw new InvalidDataException("A glTF skin has no joint list.");
            foreach (JsonNode? joint in skinJoints)
            {
                int index = joint?.GetValue<int>()
                    ?? throw new InvalidDataException("A glTF skin contains an empty joint.");
                if ((uint)index >= (uint)nodes.Count)
                    throw new InvalidDataException($"A glTF skin refers to missing node {index}.");
                jointNodes.Add(index);
            }
        }

        var nodeByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < nodes.Count; index++)
        {
            string? nodeName = nodes[index]?["name"]?.GetValue<string>();
            if (nodeName is not null && !nodeByName.TryAdd(nodeName, index))
                throw new InvalidDataException($"The glTF file contains node name {nodeName} more than once.");
        }

        var expectedNodes = new int[asset.Bones.Count];
        for (int index = 0; index < asset.Bones.Count; index++)
        {
            string name = BoneName(asset.Bones[index].Name);
            if (!nodeByName.TryGetValue(name, out int nodeIndex) || !jointNodes.Contains(nodeIndex))
                throw new InvalidDataException($"The armature is missing {name}. Bone names must not be changed in Blender.");
            expectedNodes[index] = nodeIndex;
        }
        if (jointNodes.Count != asset.Bones.Count)
            throw new InvalidDataException($"The armature contains {jointNodes.Count} joints; the Skeleton requires exactly {asset.Bones.Count}. Do not add or delete bones.");

        int[] parents = ParentNodes(nodes);
        var expectedBoneByNode = expectedNodes
            .Select((node, bone) => (node, bone))
            .ToDictionary(pair => pair.node, pair => pair.bone);
        for (int bone = 0; bone < asset.Bones.Count; bone++)
        {
            int foundParent = -1;
            for (int node = parents[expectedNodes[bone]]; node >= 0; node = parents[node])
            {
                if (expectedBoneByNode.TryGetValue(node, out int matchedParent))
                {
                    foundParent = matchedParent;
                    break;
                }
            }
            if (foundParent != asset.Bones[bone].ParentIndex)
                throw new InvalidDataException($"{BoneName(asset.Bones[bone].Name)} has a different parent. Reparenting bones is not supported.");
        }

        Matrix4x4[] nodeWorlds = NodeWorldMatrices(nodes, parents);
        var importedPositions = new Vector3[asset.Bones.Count];
        var importedRotations = new Quaternion[asset.Bones.Count];
        var changedBones = new bool[asset.Bones.Count];
        int changed = 0;
        float maximumTranslation = 0;
        float maximumRotation = 0;

        for (int index = 0; index < asset.Bones.Count; index++)
        {
            Matrix4x4 local = nodeWorlds[expectedNodes[index]];
            int parent = asset.Bones[index].ParentIndex;
            if (parent >= 0)
            {
                if (!Matrix4x4.Invert(nodeWorlds[expectedNodes[parent]], out Matrix4x4 inverseParent))
                    throw new InvalidDataException($"{BoneName(asset.Bones[parent].Name)} has a singular transform.");
                local *= inverseParent;
            }

            if (!Matrix4x4.Decompose(local, out Vector3 scale, out Quaternion rotation,
                    out Vector3 position))
                throw new InvalidDataException($"{BoneName(asset.Bones[index].Name)} has a transform that cannot be decomposed.");
            if (Vector3.Distance(scale, Vector3.One) > 0.001f)
                throw new InvalidDataException($"{BoneName(asset.Bones[index].Name)} has scale {scale}; Skeleton bones may only be moved and rotated.");
            rotation = Normalized(rotation, $"{BoneName(asset.Bones[index].Name)} rotation");
            if (Quaternion.Dot(rotation, asset.Bones[index].LocalRotation) < 0)
                rotation = Negated(rotation);
            importedPositions[index] = position;
            importedRotations[index] = rotation;

            float translation = Vector3.Distance(position, asset.Bones[index].LocalPosition);
            float angle = RotationDegrees(rotation, asset.Bones[index].LocalRotation);
            maximumTranslation = Math.Max(maximumTranslation, translation);
            maximumRotation = Math.Max(maximumRotation, angle);
            if (translation > 0.00001f || angle > 0.001f)
            {
                changedBones[index] = true;
                changed++;
            }
        }

        if (changed == 0)
            return new SkeletonGltfImportResult(asset.Bones.Count, 0,
                maximumTranslation, maximumRotation);

        for (int index = 0; index < asset.Bones.Count; index++)
        {
            if (!changedBones[index])
                continue;
            asset.Bones[index].LocalPosition = importedPositions[index];
            asset.Bones[index].LocalRotation = importedRotations[index];
        }
        UpdateGlobals(asset.Bones, changedBones);

        return new SkeletonGltfImportResult(asset.Bones.Count, changed,
            maximumTranslation, maximumRotation);
    }

    public static string BoneName(uint hash) => $"Bone_{hash:X8}";

    static void UpdateGlobals(List<SkeletonBone> bones, bool[] localChanges)
    {
        Matrix4x4[] worlds = WorldMatrices(bones);
        var affected = new bool[bones.Count];
        for (int index = 0; index < bones.Count; index++)
            affected[index] = IsAffected(index);
        for (int index = 0; index < bones.Count; index++)
        {
            if (!affected[index])
                continue;
            if (!Matrix4x4.Decompose(worlds[index], out _, out Quaternion rotation,
                    out Vector3 position))
                throw new InvalidDataException($"Bone {index} has a global transform that cannot be decomposed.");
            rotation = Normalized(rotation, $"bone {index} global rotation");
            if (Quaternion.Dot(rotation, bones[index].GlobalRotation) < 0)
                rotation = Negated(rotation);
            bones[index].GlobalPosition = position;
            bones[index].GlobalRotation = rotation;
        }

        bool IsAffected(int index) => localChanges[index]
            || bones[index].ParentIndex >= 0 && (affected[bones[index].ParentIndex]
                || IsAffected(bones[index].ParentIndex));
    }

    static Matrix4x4[] WorldMatrices(List<SkeletonBone> bones)
    {
        var worlds = new Matrix4x4[bones.Count];
        var state = new byte[bones.Count];
        for (int index = 0; index < bones.Count; index++)
            _ = World(index);
        return worlds;

        Matrix4x4 World(int index)
        {
            if (state[index] == 2)
                return worlds[index];
            if (state[index] == 1)
                throw new InvalidDataException("The Skeleton hierarchy contains a cycle.");
            state[index] = 1;
            SkeletonBone bone = bones[index];
            Matrix4x4 local = Matrix4x4.CreateFromQuaternion(Normalized(
                bone.LocalRotation, $"bone {index} local rotation"));
            local.Translation = bone.LocalPosition;
            worlds[index] = bone.ParentIndex < 0 ? local : local * World(bone.ParentIndex);
            state[index] = 2;
            return worlds[index];
        }
    }

    static int[] ParentNodes(JsonArray nodes)
    {
        var parents = Enumerable.Repeat(-1, nodes.Count).ToArray();
        for (int parent = 0; parent < nodes.Count; parent++)
        {
            foreach (JsonNode? childNode in nodes[parent]?["children"]?.AsArray() ?? [])
            {
                int child = childNode?.GetValue<int>()
                    ?? throw new InvalidDataException($"Node {parent} contains an empty child.");
                if ((uint)child >= (uint)nodes.Count)
                    throw new InvalidDataException($"Node {parent} refers to missing child {child}.");
                if (parents[child] >= 0)
                    throw new InvalidDataException($"Node {child} has more than one parent.");
                parents[child] = parent;
            }
        }
        return parents;
    }

    static Matrix4x4[] NodeWorldMatrices(JsonArray nodes, int[] parents)
    {
        var worlds = new Matrix4x4[nodes.Count];
        var state = new byte[nodes.Count];
        for (int index = 0; index < nodes.Count; index++)
            _ = World(index);
        return worlds;

        Matrix4x4 World(int index)
        {
            if (state[index] == 2)
                return worlds[index];
            if (state[index] == 1)
                throw new InvalidDataException("The glTF node hierarchy contains a cycle.");
            state[index] = 1;
            Matrix4x4 local = NodeTransform(nodes[index]?.AsObject()
                ?? throw new InvalidDataException($"Node {index} is missing."), index);
            worlds[index] = parents[index] < 0 ? local : local * World(parents[index]);
            state[index] = 2;
            return worlds[index];
        }
    }

    static Matrix4x4 NodeTransform(JsonObject node, int index)
    {
        if (node["matrix"] is JsonNode matrixNode)
        {
            if (node["translation"] is not null || node["rotation"] is not null
                || node["scale"] is not null)
                throw new InvalidDataException($"Node {index} defines both matrix and TRS transforms.");
            float[] m = Floats(matrixNode, 16, $"node {index} matrix");
            return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        }

        float[] t = node["translation"] is null
            ? [0, 0, 0] : Floats(node["translation"]!, 3, $"node {index} translation");
        float[] r = node["rotation"] is null
            ? [0, 0, 0, 1] : Floats(node["rotation"]!, 4, $"node {index} rotation");
        float[] s = node["scale"] is null
            ? [1, 1, 1] : Floats(node["scale"]!, 3, $"node {index} scale");
        Quaternion rotation = Normalized(new Quaternion(r[0], r[1], r[2], r[3]),
            $"node {index} rotation");
        return Matrix4x4.CreateScale(s[0], s[1], s[2])
            * Matrix4x4.CreateFromQuaternion(rotation)
            * Matrix4x4.CreateTranslation(t[0], t[1], t[2]);
    }

    static float[] Floats(JsonNode node, int count, string field)
    {
        JsonArray values = node.AsArray();
        if (values.Count != count)
            throw new InvalidDataException($"The {field} has {values.Count} values, expected {count}.");
        var result = new float[count];
        for (int index = 0; index < count; index++)
        {
            result[index] = values[index]?.GetValue<float>()
                ?? throw new InvalidDataException($"The {field} contains an empty value.");
            if (!float.IsFinite(result[index]))
                throw new InvalidDataException($"The {field} contains a non-finite value.");
        }
        return result;
    }

    static JsonObject ReadJson(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length < 20 || BitConverter.ToUInt32(bytes, 0) != Magic)
            throw new InvalidDataException("The selected file is not a binary glTF 2.0 file (.glb).");
        if (BitConverter.ToUInt32(bytes, 4) != 2)
            throw new NotSupportedException("Only glTF 2.0 files are supported.");
        int declared = checked((int)BitConverter.ToUInt32(bytes, 8));
        if (declared > bytes.Length)
            throw new InvalidDataException("The glTF file is truncated.");

        int at = 12;
        while (at + 8 <= declared)
        {
            int length = checked((int)BitConverter.ToUInt32(bytes, at));
            uint type = BitConverter.ToUInt32(bytes, at + 4);
            at += 8;
            if (length < 0 || length > declared - at)
                throw new InvalidDataException("The glTF file contains a truncated chunk.");
            if (type == JsonChunk)
            {
                string json = Encoding.UTF8.GetString(bytes, at, length).TrimEnd('\0', ' ', '\t', '\r', '\n');
                return JsonNode.Parse(json)?.AsObject()
                    ?? throw new InvalidDataException("The glTF JSON document is empty.");
            }
            at += length;
        }
        throw new InvalidDataException("The glTF file has no JSON chunk.");
    }

    static void WriteGlb(string path, JsonObject root, byte[] binary)
    {
        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
        int jsonPadding = (4 - json.Length % 4) % 4;
        int binaryPadding = (4 - binary.Length % 4) % 4;
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(Magic);
        writer.Write(2u);
        writer.Write(12u + 8 + (uint)(json.Length + jsonPadding)
            + 8 + (uint)(binary.Length + binaryPadding));
        writer.Write((uint)(json.Length + jsonPadding));
        writer.Write(JsonChunk);
        writer.Write(json);
        for (int index = 0; index < jsonPadding; index++) writer.Write((byte)0x20);
        writer.Write((uint)(binary.Length + binaryPadding));
        writer.Write(BinaryChunk);
        writer.Write(binary);
        for (int index = 0; index < binaryPadding; index++) writer.Write((byte)0);
    }

    static int AddPositions(MemoryStream binary, JsonArray views, JsonArray accessors)
    {
        int start = Align(binary);
        float[] values = [0, 0, 0, 0.001f, 0, 0, 0, 0.001f, 0];
        foreach (float value in values) Put(binary, value);
        return AddAccessor(binary, views, accessors, start, Float, 3, "VEC3",
            new JsonArray { 0, 0, 0 }, new JsonArray { 0.001f, 0.001f, 0 });
    }

    static int AddJoints(MemoryStream binary, JsonArray views, JsonArray accessors)
    {
        int start = Align(binary);
        for (int vertex = 0; vertex < 3; vertex++)
            for (int component = 0; component < 4; component++)
                binary.WriteByte(0);
        return AddAccessor(binary, views, accessors, start, UnsignedByte, 3, "VEC4");
    }

    static int AddWeights(MemoryStream binary, JsonArray views, JsonArray accessors)
    {
        int start = Align(binary);
        for (int vertex = 0; vertex < 3; vertex++)
        {
            Put(binary, 1f); Put(binary, 0f); Put(binary, 0f); Put(binary, 0f);
        }
        return AddAccessor(binary, views, accessors, start, Float, 3, "VEC4");
    }

    static int AddIndices(MemoryStream binary, JsonArray views, JsonArray accessors)
    {
        int start = Align(binary);
        Put(binary, (ushort)0); Put(binary, (ushort)1); Put(binary, (ushort)2);
        return AddAccessor(binary, views, accessors, start, UnsignedShort, 3, "SCALAR");
    }

    static int AddBindMatrices(MemoryStream binary, JsonArray views, JsonArray accessors,
        Matrix4x4[] worlds)
    {
        int start = Align(binary);
        foreach (Matrix4x4 world in worlds)
        {
            if (!Matrix4x4.Invert(world, out Matrix4x4 inverse))
                throw new InvalidDataException("A Skeleton bone has a singular bind transform.");
            Put(binary, inverse.M11); Put(binary, inverse.M12); Put(binary, inverse.M13); Put(binary, inverse.M14);
            Put(binary, inverse.M21); Put(binary, inverse.M22); Put(binary, inverse.M23); Put(binary, inverse.M24);
            Put(binary, inverse.M31); Put(binary, inverse.M32); Put(binary, inverse.M33); Put(binary, inverse.M34);
            Put(binary, inverse.M41); Put(binary, inverse.M42); Put(binary, inverse.M43); Put(binary, inverse.M44);
        }
        return AddAccessor(binary, views, accessors, start, Float, worlds.Length, "MAT4");
    }

    static int AddAccessor(MemoryStream binary, JsonArray views, JsonArray accessors,
        int start, int componentType, int count, string type,
        JsonArray? minimum = null, JsonArray? maximum = null)
    {
        var accessor = new JsonObject
        {
            ["bufferView"] = AddView(views, start, checked((int)binary.Length - start)),
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = type,
        };
        if (minimum is not null) accessor["min"] = minimum;
        if (maximum is not null) accessor["max"] = maximum;
        accessors.Add(accessor);
        return accessors.Count - 1;
    }

    static int AddView(JsonArray views, int offset, int length)
    {
        views.Add(new JsonObject
        {
            ["buffer"] = 0,
            ["byteOffset"] = offset,
            ["byteLength"] = length,
        });
        return views.Count - 1;
    }

    static int Align(MemoryStream binary)
    {
        while (binary.Length % 4 != 0) binary.WriteByte(0);
        return checked((int)binary.Length);
    }

    static JsonArray Vector(Vector3 value) => new(value.X, value.Y, value.Z);
    static JsonArray Rotation(Quaternion value) => new(value.X, value.Y, value.Z, value.W);
    static void Put(MemoryStream stream, float value) => stream.Write(BitConverter.GetBytes(value));
    static void Put(MemoryStream stream, ushort value) => stream.Write(BitConverter.GetBytes(value));

    static Quaternion Normalized(Quaternion value, string field)
    {
        float length = value.Length();
        if (!float.IsFinite(length) || length < 0.000001f)
            throw new InvalidDataException($"The {field} is invalid.");
        return Quaternion.Normalize(value);
    }

    static Quaternion Negated(Quaternion value) =>
        new(-value.X, -value.Y, -value.Z, -value.W);

    static float RotationDegrees(Quaternion left, Quaternion right)
    {
        float dot = Math.Clamp(Math.Abs(Quaternion.Dot(Normalized(left, "rotation"),
            Normalized(right, "rotation"))), 0, 1);
        return 2 * MathF.Acos(dot) * 180 / MathF.PI;
    }
}
