using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public sealed class MeshPart
{
    public required MeshGeometry3D Geometry { get; init; }
    public required int VertexCount { get; init; }
    public required int TriangleCount { get; init; }
}

public static class MeshScene
{
    public static List<MeshPart> Build(Mesh mesh)
    {
        var parts = new List<MeshPart>();

        if (mesh.Data is null)
            return parts;

        var vertices = MeshGeometry.ReadVertices(mesh);

        var positions = new Point3DCollection(vertices.Length);
        var normals = new Vector3DCollection(vertices.Length);
        var texture = new PointCollection(vertices.Length);

        foreach (var vertex in vertices)
        {
            positions.Add(new Point3D(vertex.Position.X, vertex.Position.Y, vertex.Position.Z));
            normals.Add(new Vector3D(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z));
            texture.Add(new System.Windows.Point(vertex.Uv.X, vertex.Uv.Y));
        }

        positions.Freeze();
        normals.Freeze();
        texture.Freeze();

        foreach (var range in mesh.Data.Standard)
        {
            var indices = new Int32Collection(MeshGeometry.ReadTriangles(mesh, range));
            indices.Freeze();

            var geometry = new MeshGeometry3D
            {
                Positions = positions,
                Normals = normals,
                TextureCoordinates = texture,
                TriangleIndices = indices,
            };
            geometry.Freeze();

            parts.Add(new MeshPart
            {
                Geometry = geometry,
                VertexCount = range.VertexCount,
                TriangleCount = range.TriangleCount,
            });
        }

        return parts;
    }

    public static Rect3D Bounds(List<MeshPart> parts)
    {
        var bounds = Rect3D.Empty;

        foreach (var part in parts)
            bounds.Union(part.Geometry.Bounds);

        return bounds;
    }
}
