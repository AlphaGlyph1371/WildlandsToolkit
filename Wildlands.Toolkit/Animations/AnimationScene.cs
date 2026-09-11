using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public static class AnimationScene
{
    const double JointSize = 0.012;
    const double BoneWidth = 0.008;

    public static MeshGeometry3D Build(IReadOnlyList<Matrix4x4> pose, List<SkeletonBone> bones)
    {
        var positions = new Point3DCollection();
        var indices = new Int32Collection();

        for (int i = 0; i < pose.Count; i++)
        {
            Vector3 point = pose[i].Translation;
            AddBox(positions, indices, point, point, JointSize);
            int parent = bones[i].ParentIndex;
            if (parent >= 0 && parent < pose.Count)
                AddBox(positions, indices, pose[parent].Translation, point, BoneWidth);
        }

        var geometry = new MeshGeometry3D { Positions = positions, TriangleIndices = indices };
        geometry.Freeze();
        return geometry;
    }

    public static Rect3D Bounds(IReadOnlyList<Matrix4x4> pose)
    {
        if (pose.Count == 0)
            return Rect3D.Empty;

        var points = pose.Select(matrix => matrix.Translation).ToList();
        Vector3 centre = points.Aggregate(Vector3.Zero, (sum, point) => sum + point) / points.Count;
        var reach = points.Select(point => (point - centre).Length()).Order().ToList();
        float limit = reach[Math.Min(reach.Count - 1, reach.Count * 95 / 100)] * 1.3f;

        var kept = points.Where(point => (point - centre).Length() <= limit).ToList();
        if (kept.Count == 0)
            kept = points;

        Vector3 low = kept[0], high = kept[0];
        foreach (Vector3 point in kept)
        {
            low = Vector3.Min(low, point);
            high = Vector3.Max(high, point);
        }

        return new Rect3D(low.X, low.Y, low.Z, high.X - low.X, high.Y - low.Y, high.Z - low.Z);
    }

    static void AddBox(Point3DCollection positions, Int32Collection indices,
        Vector3 from, Vector3 to, double width)
    {
        Vector3 axis = to - from;
        if (axis.Length() < 1e-6f)
            axis = new Vector3(0, 0, 1e-4f);
        Vector3 forward = Vector3.Normalize(axis);
        Vector3 helper = MathF.Abs(forward.Z) > 0.9f ? new Vector3(1, 0, 0) : new Vector3(0, 0, 1);
        Vector3 right = Vector3.Normalize(Vector3.Cross(forward, helper)) * (float)width;
        Vector3 up = Vector3.Normalize(Vector3.Cross(forward, right)) * (float)width;

        int start = positions.Count;
        foreach (Vector3 corner in Corners(from, right, up))
            positions.Add(new Point3D(corner.X, corner.Y, corner.Z));
        foreach (Vector3 corner in Corners(to, right, up))
            positions.Add(new Point3D(corner.X, corner.Y, corner.Z));

        int[] quads =
        [
            0, 1, 2, 3,
            7, 6, 5, 4,
            0, 4, 5, 1,
            1, 5, 6, 2,
            2, 6, 7, 3,
            3, 7, 4, 0,
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

    static IEnumerable<Vector3> Corners(Vector3 centre, Vector3 right, Vector3 up)
    {
        yield return centre - right - up;
        yield return centre + right - up;
        yield return centre + right + up;
        yield return centre - right + up;
    }
}
