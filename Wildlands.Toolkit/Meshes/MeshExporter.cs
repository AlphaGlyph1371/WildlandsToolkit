using System.Collections.Generic;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public static class MeshExporter
{
    public static string SaveFbx(Window owner, Mesh mesh, string name, List<Resource> siblings, SkeletonIndex? skeletonIndex, AppSettings settings, List<SkeletonBone>? chosenSkeleton = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export mesh",
            FileName = name + ".fbx",
            Filter = "FBX model (*.fbx)|*.fbx|All files (*.*)|*.*",
        };

        if (settings.ExportFolder.Length > 0 && Directory.Exists(settings.ExportFolder))
            dialog.InitialDirectory = settings.ExportFolder;

        if (dialog.ShowDialog(owner) != true)
            return "";

        string picked = "";
        var skeleton = chosenSkeleton ?? SkeletonFinder.Find(siblings, mesh.Bones, skeletonIndex);

        if (skeleton is null && Ask(owner, "No skeleton found for this mesh, so its bones come out unconnected.\n\nPick one yourself?"))
        {
            skeleton = SkeletonPicker.Choose(owner, out picked);
        }

        FbxWriter.Write(mesh, name, dialog.FileName, skeleton);

        settings.ExportFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
        settings.Save();

        string how = skeleton is null ? "flat bone list"
            : picked.Length > 0 ? $"skeleton {picked}"
            : $"skeleton with {skeleton.Count} bones";

        return $"Wrote {Path.GetFileName(dialog.FileName)} ({how})";
    }

    static bool Ask(Window owner, string question) => MessageBox.Show(owner, question, "Export mesh", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
