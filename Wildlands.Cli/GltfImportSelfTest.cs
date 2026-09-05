using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Wildlands.Formats.Models;

static class GltfImportSelfTest
{
    public static void Run()
    {
        string path = Path.Combine(Path.GetTempPath(), "wildlands-gltf-import-self-test.gltf");
        try
        {
            byte[] vertices = VertexBuffer();
            byte[] second = SecondBuffer();
            var root = Document(vertices, second);
            File.WriteAllText(path, root.ToJsonString(), new UTF8Encoding(false));

            ImportedGeometry geometry = GltfFile.Read(path);
            if (geometry.Groups.Count != 2)
                throw new InvalidDataException("glTF scene traversal imported an unused mesh or lost an active primitive.");
            if (!geometry.Groups[0].HasTangents || geometry.Groups[1].HasTangents || geometry.HasTangents)
                throw new InvalidDataException("glTF per-primitive tangent availability was not retained.");

            ImportedGroup first = geometry.Groups[0];
            Close(first.Vertices[0].Position, new(10, 0, 0), "nested node translation");
            Close(first.Vertices[1].Position, new(8, 0, 0), "nested negative scale");
            if (!first.Indices.SequenceEqual([0, 2, 1]))
                throw new InvalidDataException("A mirrored glTF node did not reverse triangle winding.");
            MeshVertex tangentVertex = first.Vertices[0];
            if (Vector3.Dot(Vector3.Cross(tangentVertex.Normal, tangentVertex.Tangent), tangentVertex.Binormal) <= 0)
                throw new InvalidDataException("A mirrored glTF node did not preserve tangent handedness.");

            foreach (MeshVertex vertex in geometry.Groups[1].Vertices)
                if (Vector3.Dot(vertex.Normal, Vector3.UnitZ) < 0.999f)
                    throw new InvalidDataException("Missing glTF normals were not generated from transformed triangles.");

            byte[] skinBuffer = SkinBuffer();
            File.WriteAllText(path, SkinDocument(skinBuffer).ToJsonString(), new UTF8Encoding(false));
            ImportedGeometry skinned = GltfFile.Read(path);
            if (skinned.Groups.Count != 2 || skinned.JointNames.Count != 2
                || skinned.Groups[0].Vertices[0].JointIndices[0] != 0
                || skinned.Groups[1].Vertices[0].JointIndices[0] != 1)
                throw new InvalidDataException("glTF nodes using different skins were not merged by bone name.");
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    static JsonObject Document(byte[] first, byte[] second)
    {
        var accessors = new JsonArray
        {
            Accessor(0, 0, 3, "VEC3", 5126),
            Accessor(0, 12, 3, "VEC3", 5126),
            Accessor(0, 24, 3, "VEC4", 5126),
            Accessor(0, 40, 3, "VEC2", 5126),
            Accessor(1, 0, 3, "SCALAR", 5123),
            Accessor(2, 0, 3, "VEC3", 5126),
            Accessor(3, 0, 3, "SCALAR", 5123),
        };
        var actual = new JsonObject
        {
            ["primitives"] = new JsonArray
            {
                new JsonObject
                {
                    ["attributes"] = new JsonObject
                    {
                        ["POSITION"] = 0, ["NORMAL"] = 1, ["TANGENT"] = 2, ["TEXCOORD_0"] = 3,
                    },
                    ["indices"] = 4, ["material"] = 0, ["mode"] = 4,
                },
                new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = 5 },
                    ["indices"] = 6, ["material"] = 1, ["mode"] = 4,
                },
            },
        };
        var unused = new JsonObject
        {
            ["primitives"] = new JsonArray
            {
                new JsonObject
                {
                    ["attributes"] = new JsonObject { ["POSITION"] = 5 },
                    ["indices"] = 6, ["mode"] = 4,
                },
            },
        };
        return new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray { new JsonObject { ["nodes"] = new JsonArray { 0 } } },
            ["nodes"] = new JsonArray
            {
                new JsonObject { ["translation"] = new JsonArray { 10, 0, 0 }, ["children"] = new JsonArray { 1 } },
                new JsonObject { ["scale"] = new JsonArray { -2, 2, 2 }, ["mesh"] = 0 },
                new JsonObject { ["mesh"] = 1 },
            },
            ["meshes"] = new JsonArray { actual, unused },
            ["materials"] = new JsonArray
            {
                new JsonObject { ["name"] = "Material_A" }, new JsonObject { ["name"] = "Material_B" },
            },
            ["buffers"] = new JsonArray
            {
                Buffer(first), Buffer(second),
            },
            ["bufferViews"] = new JsonArray
            {
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = first.Length, ["byteStride"] = 48 },
                new JsonObject { ["buffer"] = 1, ["byteOffset"] = 0, ["byteLength"] = 6 },
                new JsonObject { ["buffer"] = 1, ["byteOffset"] = 8, ["byteLength"] = 36 },
                new JsonObject { ["buffer"] = 1, ["byteOffset"] = 44, ["byteLength"] = 6 },
            },
            ["accessors"] = accessors,
        };
    }

    static JsonObject Buffer(byte[] bytes) => new()
    {
        ["byteLength"] = bytes.Length,
        ["uri"] = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes),
    };

    static JsonObject SkinDocument(byte[] bytes)
    {
        var primitive = new JsonObject
        {
            ["attributes"] = new JsonObject { ["POSITION"] = 0, ["JOINTS_0"] = 1, ["WEIGHTS_0"] = 2 },
            ["indices"] = 3, ["mode"] = 4,
        };
        return new JsonObject
        {
            ["asset"] = new JsonObject { ["version"] = "2.0" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray { new JsonObject { ["nodes"] = new JsonArray { 0, 1 } } },
            ["nodes"] = new JsonArray
            {
                new JsonObject { ["name"] = "skin A", ["mesh"] = 0, ["skin"] = 0 },
                new JsonObject { ["name"] = "skin B", ["mesh"] = 0, ["skin"] = 1, ["translation"] = new JsonArray { 2, 0, 0 } },
                new JsonObject { ["name"] = "Bone_11111111" },
                new JsonObject { ["name"] = "Bone_22222222" },
            },
            ["skins"] = new JsonArray
            {
                new JsonObject { ["joints"] = new JsonArray { 2 } },
                new JsonObject { ["joints"] = new JsonArray { 3 } },
            },
            ["meshes"] = new JsonArray { new JsonObject { ["primitives"] = new JsonArray { primitive } } },
            ["buffers"] = new JsonArray { Buffer(bytes) },
            ["bufferViews"] = new JsonArray
            {
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = 0, ["byteLength"] = 36 },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = 36, ["byteLength"] = 24 },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = 60, ["byteLength"] = 48 },
                new JsonObject { ["buffer"] = 0, ["byteOffset"] = 108, ["byteLength"] = 6 },
            },
            ["accessors"] = new JsonArray
            {
                Accessor(0, 0, 3, "VEC3", 5126), Accessor(1, 0, 3, "VEC4", 5123),
                Accessor(2, 0, 3, "VEC4", 5126), Accessor(3, 0, 3, "SCALAR", 5123),
            },
        };
    }

    static JsonObject Accessor(int view, int offset, int count, string type, int component) => new()
    {
        ["bufferView"] = view, ["byteOffset"] = offset, ["count"] = count,
        ["type"] = type, ["componentType"] = component,
    };

    static byte[] VertexBuffer()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (Vector3 position in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY })
        {
            Vector(writer, position); Vector(writer, Vector3.UnitZ);
            writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(-1f);
            writer.Write(position.X); writer.Write(position.Y);
        }
        return stream.ToArray();
    }

    static byte[] SecondBuffer()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2); writer.Write((ushort)0);
        Vector(writer, new(0, 0, 1)); Vector(writer, new(1, 0, 1)); Vector(writer, new(0, 1, 1));
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2);
        return stream.ToArray();
    }

    static byte[] SkinBuffer()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        Vector(writer, Vector3.Zero); Vector(writer, Vector3.UnitX); Vector(writer, Vector3.UnitY);
        for (int vertex = 0; vertex < 3; vertex++)
            for (int lane = 0; lane < 4; lane++) writer.Write((ushort)0);
        for (int vertex = 0; vertex < 3; vertex++)
        {
            writer.Write(1f); writer.Write(0f); writer.Write(0f); writer.Write(0f);
        }
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)2);
        return stream.ToArray();
    }

    static void Vector(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X); writer.Write(value.Y); writer.Write(value.Z);
    }

    static void Close(Vector3 actual, Vector3 expected, string label)
    {
        if (Vector3.DistanceSquared(actual, expected) > 1e-8f)
            throw new InvalidDataException($"glTF {label} self-test failed: {actual} instead of {expected}.");
    }
}
