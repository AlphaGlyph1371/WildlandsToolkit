using System.IO;
using System.Windows;
using Microsoft.Win32;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public static class SkeletonInterchange
{
    public static string Export(Window owner, byte[] resource, string name,
        AppSettings settings)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export Skeleton for Blender",
            FileName = name,
            AddExtension = true,
            DefaultExt = ".glb",
            Filter = "glTF binary (*.glb)|*.glb",
        };
        if (settings.ExportFolder.Length > 0 && Directory.Exists(settings.ExportFolder))
            dialog.InitialDirectory = settings.ExportFolder;
        if (dialog.ShowDialog(owner) != true)
            return "";

        SkeletonAsset skeleton = Skeleton.ReadAsset(resource);
        SkeletonGltf.Write(skeleton, name, dialog.FileName);
        settings.ExportFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
        settings.Save();
        return $"Wrote {Path.GetFileName(dialog.FileName)} with {skeleton.Bones.Count} bones";
    }

    public static byte[]? Import(Window owner, byte[] resource, string name,
        AppSettings settings, out string summary)
    {
        summary = "";
        var dialog = new OpenFileDialog
        {
            Title = $"Import edited Skeleton for {name}",
            Filter = "glTF binary (*.glb)|*.glb",
        };
        if (settings.ExportFolder.Length > 0 && Directory.Exists(settings.ExportFolder))
            dialog.InitialDirectory = settings.ExportFolder;
        if (dialog.ShowDialog(owner) != true)
            return null;

        try
        {
            SkeletonAsset skeleton = Skeleton.ReadAsset(resource);
            SkeletonGltfImportResult result = SkeletonGltf.Apply(skeleton, dialog.FileName);
            byte[] rebuilt = Skeleton.Write(skeleton);
            _ = Skeleton.ReadAsset(rebuilt);

            settings.ExportFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
            settings.Save();
            summary = $"{result.ChangedBones} of {result.BoneCount} bones changed; "
                + $"largest move {result.MaximumTranslation:0.####} m, "
                + $"largest rotation {result.MaximumRotationDegrees:0.###}°";
            return rebuilt;
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, $"Could not import {name}",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
    }
}
