using System.Windows.Media;
using System.Windows.Media.Media3D;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

sealed record BuildTableMeshPreview(
    Model3D Scene,
    string MeshName,
    int Triangles,
    double LargestDimension)
{
    public Model3D AtFamilyScale(double familyLargestDimension)
    {
        if (!double.IsFinite(familyLargestDimension) || familyLargestDimension <= 0 || Math.Abs(familyLargestDimension - LargestDimension) < 0.000001)
            return Scene;

        double relativeScale = LargestDimension / familyLargestDimension;
        var result = new Model3DGroup
        {
            Transform = new ScaleTransform3D(relativeScale, relativeScale, relativeScale),
        };
        result.Children.Add(Scene);
        result.Freeze();
        return result;
    }
}

static class BuildTableMeshPreviewBuilder
{
    public static BuildTableMeshPreview? Build(Mesh mesh, string meshName)
    {
        var parts = MeshScene.Build(mesh);
        Rect3D bounds = MeshScene.Bounds(parts);
        if (parts.Count == 0 || bounds.IsEmpty)
            return null;

        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(0x62, 0x66, 0x70)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(0xFF, 0xFC, 0xF2), new Vector3D(-0.7, 1, -0.8)));
        scene.Children.Add(new DirectionalLight(Color.FromRgb(0x69, 0x8D, 0xB8), new Vector3D(0.8, -0.5, 0.25)));

        var frontBrush = new SolidColorBrush(Color.FromRgb(0xA9, 0xB8, 0xCA));
        var backBrush = new SolidColorBrush(Color.FromRgb(0x55, 0x61, 0x70));
        frontBrush.Freeze();
        backBrush.Freeze();
        var front = new DiffuseMaterial(frontBrush);
        var back = new DiffuseMaterial(backBrush);
        front.Freeze();
        back.Freeze();

        int triangles = 0;
        foreach (var part in parts)
        {
            var model = new GeometryModel3D(part.Geometry, front) { BackMaterial = back };
            scene.Children.Add(model);
            triangles += part.TriangleCount;
        }

        double largest = Math.Max(bounds.SizeX, Math.Max(bounds.SizeY, bounds.SizeZ));
        if (!double.IsFinite(largest) || largest <= 0)
            return null;

        var transform = new Transform3DGroup();
        transform.Children.Add(new TranslateTransform3D(
            -(bounds.X + bounds.SizeX / 2),
            -(bounds.Y + bounds.SizeY / 2),
            -(bounds.Z + bounds.SizeZ / 2)));
        double scale = 1.65 / largest;
        transform.Children.Add(new ScaleTransform3D(scale, scale, scale));
        transform.Freeze();
        scene.Transform = transform;
        scene.Freeze();
        return new BuildTableMeshPreview(scene, meshName, triangles, largest);
    }
}
