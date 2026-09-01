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
    public static string Save(Window owner, Mesh mesh, string name, List<Resource> siblings, SkeletonIndex? skeletonIndex, AppSettings settings, List<SkeletonBone>? chosenSkeleton = null)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export mesh",
            FileName = name + ".glb",
            Filter = "glTF binary (*.glb)|*.glb|FBX model (*.fbx)|*.fbx|Wavefront OBJ (*.obj)|*.obj|All files (*.*)|*.*",
        };

        if (settings.ExportFolder.Length > 0 && Directory.Exists(settings.ExportFolder))
            dialog.InitialDirectory = settings.ExportFolder;

        if (dialog.ShowDialog(owner) != true)
            return "";

        settings.ExportFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
        settings.Save();

        string extension = Path.GetExtension(dialog.FileName);
        bool wantsBones = !extension.Equals(".obj", System.StringComparison.OrdinalIgnoreCase);
        var rig = wantsBones ? chosenSkeleton ?? SkeletonFinder.Find(siblings, mesh.Bones, skeletonIndex) : null;

        if (extension.Equals(".obj", System.StringComparison.OrdinalIgnoreCase))
        {
            ObjFile.Write(mesh, name, dialog.FileName);
            return $"Wrote {Path.GetFileName(dialog.FileName)} ({mesh.Data.Standard.Count} draw range(s), positions, normals and one uv set)";
        }

        if (extension.Equals(".glb", System.StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gltf", System.StringComparison.OrdinalIgnoreCase))
        {
            GltfFile.Write(mesh, name, dialog.FileName, rig);
            var layout = VertexLayout.For(mesh.VertexFormat, mesh.VertexStride);

            return $"Wrote {Path.GetFileName(dialog.FileName)} ({mesh.Data.Standard.Count} primitive(s), "
                + $"{layout.UvCount} uv set(s)" + (layout.HasColor ? ", vertex colours" : "")
                + (layout.IsSkinned && mesh.Bones.Count > 0
                    ? $", skin over {mesh.Bones.Count} bones" + (rig is null ? " without a hierarchy" : " with their hierarchy")
                    : "") + ")";
        }

        string picked = "";
        var skeleton = rig;

        if (skeleton is null && Ask(owner, "No skeleton found for this mesh, so its bones come out unconnected.\n\nPick one yourself?"))
        {
            skeleton = SkeletonPicker.Choose(owner, out picked);
        }

        FbxWriter.Write(mesh, name, dialog.FileName, skeleton);

        string how = skeleton is null ? "flat bone list"
            : picked.Length > 0 ? $"skeleton {picked}"
            : $"skeleton with {skeleton.Count} bones";

        return $"Wrote {Path.GetFileName(dialog.FileName)} ({how})";
    }

    static bool Ask(Window owner, string question) => MessageBox.Show(owner, question, "Export mesh", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}
