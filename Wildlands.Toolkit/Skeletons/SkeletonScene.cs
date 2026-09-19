using System.Collections.Generic;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

static class SkeletonScene
{
    public static IReadOnlyList<Matrix4x4> Pose(SkeletonAsset skeleton)
    {
        var pose = new Matrix4x4[skeleton.Bones.Count];
        for (int i = 0; i < skeleton.Bones.Count; i++)
        {
            SkeletonBone bone = skeleton.Bones[i];
            pose[i] = Matrix4x4.CreateFromQuaternion(bone.GlobalRotation)
                * Matrix4x4.CreateTranslation(bone.GlobalPosition);
        }
        return pose;
    }

    public static MeshGeometry3D BuildSkeleton(IReadOnlyList<Matrix4x4> pose,
        IReadOnlyList<SkeletonBone> bones)
    {
        var geometry = NewMesh();
        double lineWidth = Math.Clamp(SceneDiagonal(pose) * 0.001, 0.0006, 0.006);
        double jointSize = lineWidth * 2.2;

        for (int i = 0; i < pose.Count; i++)
        {
            Vector3 point = pose[i].Translation;
            AddCube(geometry.Positions, geometry.TriangleIndices, point, jointSize);

            int parent = bones[i].ParentIndex;
            if (parent >= 0 && parent < pose.Count)
                AddBox(geometry.Positions, geometry.TriangleIndices,
                    pose[parent].Translation, point, lineWidth);
        }

        geometry.Freeze();
        return geometry;
    }

    public static (MeshGeometry3D X, MeshGeometry3D Y, MeshGeometry3D Z) BuildAxes(
        IReadOnlyList<Matrix4x4> pose)
    {
        var x = NewMesh();
        var y = NewMesh();
        var z = NewMesh();
        double length = AxisLength(pose);
        double width = length * 0.025;

        foreach (Matrix4x4 transform in pose)
        {
            Vector3 origin = transform.Translation;
            AddBox(x.Positions, x.TriangleIndices, origin,
                Vector3.Transform(Vector3.UnitX * (float)length, transform), width);
            AddBox(y.Positions, y.TriangleIndices, origin,
                Vector3.Transform(Vector3.UnitY * (float)length, transform), width);
            AddBox(z.Positions, z.TriangleIndices, origin,
                Vector3.Transform(Vector3.UnitZ * (float)length, transform), width);
        }

        x.Freeze();
        y.Freeze();
        z.Freeze();
        return (x, y, z);
    }

    static MeshGeometry3D NewMesh() => new()
    {
        Positions = [],
        TriangleIndices = [],
    };

    static double AxisLength(IReadOnlyList<Matrix4x4> pose)
    {
        return Math.Clamp(SceneDiagonal(pose) * 0.045, 0.008, 0.25);
    }

    static double SceneDiagonal(IReadOnlyList<Matrix4x4> pose)
    {
        Rect3D bounds = AnimationScene.Bounds(pose);
        return bounds.IsEmpty
            ? 1
            : Math.Max(new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length, 0.01);
    }

    static void AddBox(Point3DCollection positions, Int32Collection indices,
        Vector3 from, Vector3 to, double width)
    {
        Vector3 axis = to - from;
        if (axis.Length() < 1e-6f)
            return;

        Vector3 forward = Vector3.Normalize(axis);
        Vector3 helper = MathF.Abs(forward.Z) > 0.9f ? Vector3.UnitX : Vector3.UnitZ;
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, helper)) * (float)width;
        Vector3 up = Vector3.Normalize(Vector3.Cross(forward, right)) * (float)width;

        int start = positions.Count;
        AddCorners(positions, from, right, up);
        AddCorners(positions, to, right, up);

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
    }

    static void AddCorners(Point3DCollection positions, Vector3 centre, Vector3 right, Vector3 up)
    {
        Add(centre - right - up);
        Add(centre + right - up);
        Add(centre + right + up);
        Add(centre - right + up);

        void Add(Vector3 point) => positions.Add(new Point3D(point.X, point.Y, point.Z));
    }

    static void AddCube(Point3DCollection positions, Int32Collection indices,
        Vector3 centre, double halfSize)
    {
        float size = (float)halfSize;
        int start = positions.Count;
        positions.Add(new Point3D(centre.X - size, centre.Y - size, centre.Z - size));
        positions.Add(new Point3D(centre.X + size, centre.Y - size, centre.Z - size));
        positions.Add(new Point3D(centre.X + size, centre.Y + size, centre.Z - size));
        positions.Add(new Point3D(centre.X - size, centre.Y + size, centre.Z - size));
        positions.Add(new Point3D(centre.X - size, centre.Y - size, centre.Z + size));
        positions.Add(new Point3D(centre.X + size, centre.Y - size, centre.Z + size));
        positions.Add(new Point3D(centre.X + size, centre.Y + size, centre.Z + size));
        positions.Add(new Point3D(centre.X - size, centre.Y + size, centre.Z + size));

        int[] quads =
        [
            0, 3, 2, 1, 4, 5, 6, 7, 0, 1, 5, 4,
            1, 2, 6, 5, 2, 3, 7, 6, 3, 0, 4, 7,
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
    }
}
