using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wildlands.Formats.Models;

public static class GltfFile
{
    const uint Magic = 0x46546C67;
    const uint JsonChunk = 0x4E4F534A;
    const uint BinaryChunk = 0x004E4942;

    const int Byte = 5121;
    const int UnsignedInt = 5125;
    const int Float = 5126;

    public static void Write(Mesh mesh, string name, string path, List<SkeletonBone>? skeleton = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var vertices = MeshGeometry.ReadVertices(mesh);
        var layout = VertexLayout.For(mesh.VertexFormat, mesh.VertexStride);
        bool skinned = layout.IsSkinned && mesh.Bones.Count > 0;

        var binary = new MemoryStream();
        var accessors = new JsonArray();
        var views = new JsonArray();

        var primitives = new JsonArray();
        var materials = new JsonArray();

        for (int r = 0; r < mesh.Data.Standard.Count; r++)
        {
            var corners = MeshGeometry.ReadTriangles(mesh, mesh.Data.Standard[r]);
            var used = new List<int>();
            var localOf = new Dictionary<int, int>();
            var indices = new List<int>();

            foreach (int corner in corners)
            {
                if (!localOf.TryGetValue(corner, out int local))
                {
                    local = used.Count;
                    localOf[corner] = local;
                    used.Add(corner);
                }

                indices.Add(local);
            }

            var attributes = new JsonObject
            {
                ["POSITION"] = Vectors(binary, views, accessors, used, vertices, v => v.Position, withBounds: true),
                ["NORMAL"] = Vectors(binary, views, accessors, used, vertices, v => v.Normal, withBounds: false),
            };

            attributes["TANGENT"] = Tangents(binary, views, accessors, used, vertices);

            for (int set = 0; set < layout.UvCount; set++)
            {
                int which = set;
                attributes["TEXCOORD_" + set] = Pairs(binary, views, accessors, used, vertices, v => which < v.Uv.Length ? new Vector2(v.Uv[which].X, 1 - v.Uv[which].Y) : Vector2.Zero);
            }

            if (layout.HasColor)
                attributes["COLOR_0"] = Colours(binary, views, accessors, used, vertices);

            if (skinned)
            {
                attributes["JOINTS_0"] = Joints(binary, views, accessors, used, vertices, 0);
                attributes["WEIGHTS_0"] = Weights(binary, views, accessors, used, vertices, 0);

                if (layout.JointsPerVertex > 4)
                {
                    attributes["JOINTS_1"] = Joints(binary, views, accessors, used, vertices, 4);
                    attributes["WEIGHTS_1"] = Weights(binary, views, accessors, used, vertices, 4);
                }
            }

            primitives.Add(new JsonObject
            {
                ["attributes"] = attributes,
                ["indices"] = Indices(binary, views, accessors, indices),
                ["material"] = r,
                ["mode"] = 4,
            });

            materials.Add(new JsonObject
            {
                ["name"] = ObjFile.MaterialName(mesh, r),
                ["pbrMetallicRoughness"] = new JsonObject { ["metallicFactor"] = 0.0, ["roughnessFactor"] = 1.0 },
            });
        }

        var nodes = new JsonArray { new JsonObject { ["name"] = name, ["mesh"] = 0 } };
        var root = new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Wildlands Toolkit" },
            ["scene"] = 0,
            ["meshes"] = new JsonArray { new JsonObject { ["name"] = name, ["primitives"] = primitives } },
            ["materials"] = materials,
        };

        var parents = skinned ? BoneHierarchy.Resolve(mesh.Bones, skeleton) : [];

        if (skinned)
        {
            var joints = new JsonArray();

            for (int i = 0; i < mesh.Bones.Count; i++)
            {
                var at = BoneHierarchy.WorldPosition(mesh.Bones[i]);
                if (parents[i] >= 0)
                    at -= BoneHierarchy.WorldPosition(mesh.Bones[parents[i]]);

                var node = new JsonObject
                {
                    ["name"] = BoneName(mesh.Bones[i].Name),
                    ["translation"] = new JsonArray { at.X, at.Y, at.Z },
                };

                var children = new JsonArray();
                for (int child = 0; child < mesh.Bones.Count; child++)
                    if (parents[child] == i)
                        children.Add(child + 1);

                if (children.Count > 0)
                    node["children"] = children;

                nodes.Add(node);
                joints.Add(i + 1);
            }

            ((JsonObject)nodes[0]!)["skin"] = 0;
            root["skins"] = new JsonArray
            {
                new JsonObject
                {
                    ["joints"] = joints,
                    ["inverseBindMatrices"] = BindMatrices(binary, views, accessors, mesh.Bones),
                },
            };
        }

        var sceneNodes = new JsonArray { 0 };
        if (skinned)
            for (int i = 0; i < mesh.Bones.Count; i++)
                if (parents[i] < 0) sceneNodes.Add(i + 1);

        root["scenes"] = new JsonArray { new JsonObject { ["nodes"] = sceneNodes } };
        root["nodes"] = nodes;
        root["accessors"] = accessors;
        root["bufferViews"] = views;
        root["buffers"] = new JsonArray { new JsonObject { ["byteLength"] = binary.Length } };

        WriteGlb(path, root, binary.ToArray());
    }

    public static string BoneName(uint hash) => "Bone_" + hash.ToString("X8");

    static void WriteGlb(string path, JsonObject root, byte[] binary)
    {
        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString());
        int jsonPadding = (4 - json.Length % 4) % 4;
        int binaryPadding = (4 - binary.Length % 4) % 4;

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(Magic);
        writer.Write(2u);
        writer.Write(12u + 8 + (uint)(json.Length + jsonPadding) + 8 + (uint)(binary.Length + binaryPadding));

        writer.Write((uint)(json.Length + jsonPadding));
        writer.Write(JsonChunk);
        writer.Write(json);
        for (int i = 0; i < jsonPadding; i++)
            writer.Write((byte)0x20);

        writer.Write((uint)(binary.Length + binaryPadding));
        writer.Write(BinaryChunk);
        writer.Write(binary);
        for (int i = 0; i < binaryPadding; i++)
            writer.Write((byte)0);
    }

    static int Vectors(MemoryStream binary, JsonArray views, JsonArray accessors, List<int> used, MeshVertex[] vertices, Func<MeshVertex, Vector3> pick, bool withBounds)
    {
        int start = Align(binary);
        var low = new Vector3(float.MaxValue);
        var high = new Vector3(float.MinValue);

        foreach (int index in used)
        {
            var value = pick(vertices[index]);
            low = Vector3.Min(low, value);
            high = Vector3.Max(high, value);
            Put(binary, value.X); Put(binary, value.Y); Put(binary, value.Z);
        }

        int view = AddView(views, start, (int)binary.Length - start);
        var accessor = new JsonObject
        {
            ["bufferView"] = view,
            ["componentType"] = Float,
            ["count"] = used.Count,
            ["type"] = "VEC3",
        };

        if (withBounds && used.Count > 0)
        {
            accessor["min"] = new JsonArray { low.X, low.Y, low.Z };
            accessor["max"] = new JsonArray { high.X, high.Y, high.Z };
        }

        accessors.Add(accessor);
        return accessors.Count - 1;
    }

    static int Tangents(MemoryStream binary, JsonArray views, JsonArray accessors, List<int> used, MeshVertex[] vertices)
    {
        int start = Align(binary);

        foreach (int index in used)
        {
            var vertex = vertices[index];
            var tangent = vertex.Tangent;
            float sign = Vector3.Dot(Vector3.Cross(vertex.Normal, tangent), vertex.Binormal) < 0 ? -1f : 1f;
            Put(binary, tangent.X); Put(binary, tangent.Y); Put(binary, tangent.Z); Put(binary, sign);
        }

        return AddAccessor(binary, views, accessors, start, Float, used.Count, "VEC4");
    }

    static int Pairs(MemoryStream binary, JsonArray views, JsonArray accessors, List<int> used, MeshVertex[] vertices, Func<MeshVertex, Vector2> pick)
    {
        int start = Align(binary);

        foreach (int index in used)
        {
            var value = pick(vertices[index]);
            Put(binary, value.X); Put(binary, value.Y);
        }

        return AddAccessor(binary, views, accessors, start, Float, used.Count, "VEC2");
    }

    static int Colours(MemoryStream binary, JsonArray views, JsonArray accessors, List<int> used, MeshVertex[] vertices)
    {
        int start = Align(binary);

        foreach (int index in used)
        {
            uint colour = vertices[index].Color;
            binary.WriteByte((byte)(colour & 0xFF));
            binary.WriteByte((byte)(colour >> 8 & 0xFF));
            binary.WriteByte((byte)(colour >> 16 & 0xFF));
            binary.WriteByte((byte)(colour >> 24 & 0xFF));
        }

        return AddAccessor(binary, views, accessors, start, Byte, used.Count, "VEC4", normalized: true);
    }

    static int Joints(MemoryStream binary, JsonArray views, JsonArray accessors, List<int> used, MeshVertex[] vertices, int from)
    {
        int start = Align(binary);

        foreach (int index in used)
        {
            var joints = vertices[index].JointIndices;
            for (int i = 0; i < 4; i++)
                binary.WriteByte(from + i < joints.Length ? joints[from + i] : (byte)0);
        }

        return AddAccessor(binary, views, accessors, start, Byte, used.Count, "VEC4");
    }

    static int Weights(MemoryStream binary, JsonArray views, JsonArray accessors, List<int> used, MeshVertex[] vertices, int from)
    {
        int start = Align(binary);

        foreach (int index in used)
        {
            var weights = vertices[index].JointWeights;
            for (int i = 0; i < 4; i++)
                Put(binary, from + i < weights.Length ? weights[from + i] / 255f : 0f);
        }

        return AddAccessor(binary, views, accessors, start, Float, used.Count, "VEC4");
    }

    static int BindMatrices(MemoryStream binary, JsonArray views, JsonArray accessors, List<MeshBone> bones)
    {
        int start = Align(binary);

        foreach (var bone in bones)
        {
            var m = bone.Matrix;
            Put(binary, m.M11); Put(binary, m.M12); Put(binary, m.M13); Put(binary, m.M14);
            Put(binary, m.M21); Put(binary, m.M22); Put(binary, m.M23); Put(binary, m.M24);
            Put(binary, m.M31); Put(binary, m.M32); Put(binary, m.M33); Put(binary, m.M34);
            Put(binary, m.M41); Put(binary, m.M42); Put(binary, m.M43); Put(binary, m.M44);
        }

        return AddAccessor(binary, views, accessors, start, Float, bones.Count, "MAT4");
    }

    static int Indices(MemoryStream binary, JsonArray views, JsonArray accessors, List<int> indices)
    {
        int start = Align(binary);

        foreach (int index in indices)
            Put(binary, (uint)index);

        return AddAccessor(binary, views, accessors, start, UnsignedInt, indices.Count, "SCALAR");
    }

    static int AddAccessor(MemoryStream binary, JsonArray views, JsonArray accessors, int start, int componentType, int count, string type, bool normalized = false)
    {
        var accessor = new JsonObject
        {
            ["bufferView"] = AddView(views, start, (int)binary.Length - start),
            ["componentType"] = componentType,
            ["count"] = count,
            ["type"] = type,
        };
        if (normalized)
            accessor["normalized"] = true;
        accessors.Add(accessor);
        return accessors.Count - 1;
    }

    static int AddView(JsonArray views, int offset, int length)
    {
        views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = length });
        return views.Count - 1;
    }

    static int Align(MemoryStream binary)
    {
        while (binary.Length % 4 != 0)
            binary.WriteByte(0);

        return (int)binary.Length;
    }

    static void Put(MemoryStream binary, float value) => binary.Write(BitConverter.GetBytes(value));
    static void Put(MemoryStream binary, uint value) => binary.Write(BitConverter.GetBytes(value));

    public static ImportedGeometry Read(string path) => GltfImport.Read(path);
}
