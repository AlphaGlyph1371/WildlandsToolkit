using System.Collections.Generic;
using System.Numerics;
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

        var vertices = MeshGeometry.ReadVertices(mesh);

        var positions = new Point3DCollection(vertices.Length);
        var normals = new Vector3DCollection(vertices.Length);
        var texture = new PointCollection(vertices.Length);

        foreach (var vertex in vertices)
        {
            positions.Add(new Point3D(vertex.Position.X, vertex.Position.Y, vertex.Position.Z));
            normals.Add(new Vector3D(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z));
            texture.Add(vertex.Uv.Length > 0 ? new System.Windows.Point(vertex.Uv[0].X, vertex.Uv[0].Y) : default);
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

    public static MeshGeometry3D BuildArmature(Mesh mesh, List<SkeletonBone>? skeleton)
    {
        int[] parents = BoneHierarchy.Resolve(mesh.Bones, skeleton);
        var pose = new Matrix4x4[mesh.Bones.Count];
        var bones = new List<SkeletonBone>(mesh.Bones.Count);

        for (int index = 0; index < mesh.Bones.Count; index++)
        {
            pose[index] = Matrix4x4.CreateTranslation(BoneHierarchy.WorldPosition(mesh.Bones[index]));
            bones.Add(new SkeletonBone { Index = index, ParentIndex = parents[index] });
        }

        return SkeletonScene.BuildSkeleton(pose, bones);
    }

    public static MeshGeometry3D BuildOutline(Rect3D bounds)
    {
        var geometry = new MeshGeometry3D
        {
            Positions = [],
            TriangleIndices = [],
        };
        if (bounds.IsEmpty)
            return geometry;

        double diagonal = new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length;
        double width = Math.Clamp(diagonal * 0.0015, 0.0005, 0.02);
        bounds = new Rect3D(bounds.X - width * 2, bounds.Y - width * 2,
            bounds.Z - width * 2, bounds.SizeX + width * 4,
            bounds.SizeY + width * 4, bounds.SizeZ + width * 4);

        Point3D[] corners =
        [
            new(bounds.X, bounds.Y, bounds.Z),
            new(bounds.X + bounds.SizeX, bounds.Y, bounds.Z),
            new(bounds.X + bounds.SizeX, bounds.Y + bounds.SizeY, bounds.Z),
            new(bounds.X, bounds.Y + bounds.SizeY, bounds.Z),
            new(bounds.X, bounds.Y, bounds.Z + bounds.SizeZ),
            new(bounds.X + bounds.SizeX, bounds.Y, bounds.Z + bounds.SizeZ),
            new(bounds.X + bounds.SizeX, bounds.Y + bounds.SizeY, bounds.Z + bounds.SizeZ),
            new(bounds.X, bounds.Y + bounds.SizeY, bounds.Z + bounds.SizeZ),
        ];
        int[] edges =
        [
            0, 1, 1, 2, 2, 3, 3, 0,
            4, 5, 5, 6, 6, 7, 7, 4,
            0, 4, 1, 5, 2, 6, 3, 7,
        ];
        for (int edge = 0; edge < edges.Length; edge += 2)
            AddBox(geometry.Positions, geometry.TriangleIndices,
                corners[edges[edge]], corners[edges[edge + 1]], width);

        geometry.Freeze();
        return geometry;
    }

    static void AddBox(Point3DCollection positions, Int32Collection indices,
        Point3D from, Point3D to, double width)
    {
        Vector3D axis = to - from;
        if (axis.Length < 1e-9)
            return;
        axis.Normalize();
        Vector3D helper = Math.Abs(axis.Z) > 0.9
            ? new Vector3D(1, 0, 0)
            : new Vector3D(0, 0, 1);
        Vector3D right = Vector3D.CrossProduct(axis, helper);
        right.Normalize();
        right *= width;
        Vector3D up = Vector3D.CrossProduct(axis, right);
        up.Normalize();
        up *= width;

        int start = positions.Count;
        AddCorners(from);
        AddCorners(to);
        int[] quads =
        [
            0, 1, 2, 3, 7, 6, 5, 4, 0, 4, 5, 1,
            1, 5, 6, 2, 2, 6, 7, 3, 3, 7, 4, 0,
        ];
        for (int face = 0; face < quads.Length; face += 4)
        {
            indices.Add(start + quads[face]);
            indices.Add(start + quads[face + 1]);
            indices.Add(start + quads[face + 2]);
            indices.Add(start + quads[face]);
            indices.Add(start + quads[face + 2]);
            indices.Add(start + quads[face + 3]);
        }
        return;

        void AddCorners(Point3D centre)
        {
            positions.Add(centre - right - up);
            positions.Add(centre + right - up);
            positions.Add(centre + right + up);
            positions.Add(centre - right + up);
        }
    }
}
