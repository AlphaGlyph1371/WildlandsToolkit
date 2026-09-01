using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace Wildlands.Formats.Models;

public sealed class ObjGroup
{
    public string Name { get; set; } = "";
    public List<int> Indices { get; } = [];
}

public sealed class ObjFile
{
    public List<Vector3> Positions { get; } = [];
    public List<Vector3> Normals { get; } = [];
    public List<Vector2> TextureCoordinates { get; } = [];
    public List<ObjGroup> Groups { get; } = [];

    public static ObjFile Read(string path)
    {
        var obj = new ObjFile();
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var textures = new List<Vector2>();
        var byTriplet = new Dictionary<(int, int, int), int>();
        ObjGroup? group = null;
        int line = 0;

        foreach (string raw in File.ReadLines(path))
        {
            line++;
            string text = raw.Trim();
            if (text.Length == 0 || text[0] == '#')
                continue;

            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            switch (parts[0])
            {
                case "v":
                    positions.Add(Vector(parts, line));
                    break;
                case "vn":
                    normals.Add(Vector(parts, line));
                    break;
                case "vt":
                    textures.Add(new Vector2(Number(parts, 1, line), Number(parts, 2, line)));
                    break;
                case "usemtl":
                case "g":
                case "o":
                    group = new ObjGroup { Name = parts.Length > 1 ? parts[1] : "" };
                    obj.Groups.Add(group);
                    break;
                case "f":
                    group ??= AddDefaultGroup(obj);
                    AddFace(obj, group, parts, positions, normals, textures, byTriplet, line);
                    break;
            }
        }

        if (obj.Positions.Count == 0)
            throw new InvalidDataException($"{Path.GetFileName(path)} holds no faces.");

        obj.Groups.RemoveAll(g => g.Indices.Count == 0);
        return obj;
    }

    static ObjGroup AddDefaultGroup(ObjFile obj)
    {
        var group = new ObjGroup { Name = "default" };
        obj.Groups.Add(group);
        return group;
    }

    static void AddFace(ObjFile obj, ObjGroup group, string[] parts, List<Vector3> positions, List<Vector3> normals,
        List<Vector2> textures, Dictionary<(int, int, int), int> byTriplet, int line)
    {
        if (parts.Length < 4)
            throw new InvalidDataException($"Line {line} has a face with {parts.Length - 1} corner(s).");

        var corners = new int[parts.Length - 1];
        for (int i = 1; i < parts.Length; i++)
            corners[i - 1] = Corner(obj, parts[i], positions, normals, textures, byTriplet, line);

        for (int i = 1; i + 1 < corners.Length; i++)
        {
            group.Indices.Add(corners[0]);
            group.Indices.Add(corners[i]);
            group.Indices.Add(corners[i + 1]);
        }
    }

    static int Corner(ObjFile obj, string text, List<Vector3> positions, List<Vector3> normals,
        List<Vector2> textures, Dictionary<(int, int, int), int> byTriplet, int line)
    {
        var fields = text.Split('/');
        int position = Reference(fields, 0, positions.Count, line);
        int texture = Reference(fields, 1, textures.Count, line);
        int normal = Reference(fields, 2, normals.Count, line);

        var key = (position, texture, normal);
        if (byTriplet.TryGetValue(key, out int known))
            return known;

        int index = obj.Positions.Count;
        obj.Positions.Add(positions[position]);
        obj.Normals.Add(normal >= 0 ? normals[normal] : Vector3.UnitZ);
        obj.TextureCoordinates.Add(texture >= 0 ? textures[texture] : Vector2.Zero);
        byTriplet[key] = index;
        return index;
    }

    static int Reference(string[] fields, int slot, int count, int line)
    {
        if (slot >= fields.Length || fields[slot].Length == 0)
            return slot == 0 ? throw new InvalidDataException($"Line {line} has a face corner without a vertex.") : -1;

        int value = int.Parse(fields[slot], CultureInfo.InvariantCulture);
        int index = value > 0 ? value - 1 : count + value;

        if (index < 0 || index >= count)
            throw new InvalidDataException($"Line {line} refers to element {value}, which does not exist.");

        return index;
    }

    static Vector3 Vector(string[] parts, int line) =>
        new(Number(parts, 1, line), Number(parts, 2, line), Number(parts, 3, line));

    static float Number(string[] parts, int slot, int line)
    {
        if (slot >= parts.Length || !float.TryParse(parts[slot], NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            throw new InvalidDataException($"Line {line} is missing a number in position {slot}.");

        return value;
    }

    public ImportedGeometry ToGeometry()
    {
        var geometry = new ImportedGeometry { UvSets = 1 };

        foreach (var group in Groups)
        {
            var built = new ImportedGroup { Name = group.Name };
            var localOf = new Dictionary<int, int>();

            foreach (int corner in group.Indices)
            {
                if (!localOf.TryGetValue(corner, out int local))
                {
                    local = built.Vertices.Count;
                    localOf[corner] = local;

                    var uv = TextureCoordinates[corner];
                    built.Vertices.Add(new MeshVertex
                    {
                        Position = Positions[corner],
                        Normal = Normals[corner],
                        Uv = [new Vector2(uv.X, 1 - uv.Y)],
                    });
                }

                built.Indices.Add(local);
            }

            geometry.Groups.Add(built);
        }

        return geometry;
    }

    public static void Write(Mesh mesh, string name, string path)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var vertices = MeshGeometry.ReadVertices(mesh);
        using var writer = new StreamWriter(path, false, new UTF8Encoding(false));

        writer.WriteLine("# " + name + " exported by Wildlands Toolkit");
        writer.WriteLine("o " + name);

        foreach (var vertex in vertices)
            writer.WriteLine($"v {F(vertex.Position.X)} {F(vertex.Position.Y)} {F(vertex.Position.Z)}");

        foreach (var vertex in vertices)
        {
            var uv = vertex.Uv.Length > 0 ? vertex.Uv[0] : Vector2.Zero;
            writer.WriteLine($"vt {F(uv.X)} {F(1 - uv.Y)}");
        }

        foreach (var vertex in vertices)
            writer.WriteLine($"vn {F(vertex.Normal.X)} {F(vertex.Normal.Y)} {F(vertex.Normal.Z)}");

        for (int r = 0; r < mesh.Data.Standard.Count; r++)
        {
            writer.WriteLine("usemtl " + MaterialName(mesh, r));
            var triangles = MeshGeometry.ReadTriangles(mesh, mesh.Data.Standard[r]);

            for (int i = 0; i + 2 < triangles.Length; i += 3)
            {
                int a = triangles[i] + 1, b = triangles[i + 1] + 1, c = triangles[i + 2] + 1;
                writer.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
            }
        }
    }

    public static string MaterialName(Mesh mesh, int range) => range < mesh.Materials.Count
        ? "Material_" + mesh.Materials[range].MaterialId.ToString("X16")
        : "Material_" + range;

    static string F(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
