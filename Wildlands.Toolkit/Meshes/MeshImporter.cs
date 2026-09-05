using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public static class MeshImporter
{
    public static byte[]? Replace(Window owner, byte[] resource, string name, AppSettings settings, out string summary)
    {
        summary = "";

        var dialog = new OpenFileDialog
        {
            Title = $"Replace {name}",
            Filter = "glTF binary (*.glb;*.gltf)|*.glb;*.gltf|Wavefront OBJ (*.obj)|*.obj|All files (*.*)|*.*",
        };

        if (settings.ExportFolder.Length > 0 && Directory.Exists(settings.ExportFolder))
            dialog.InitialDirectory = settings.ExportFolder;

        if (dialog.ShowDialog(owner) != true)
            return null;

        Mesh mesh;
        MeshImportResult result;

        try
        {
            mesh = Mesh.Read(resource);
            string extension = Path.GetExtension(dialog.FileName);

            var geometry = extension.Equals(".obj", System.StringComparison.OrdinalIgnoreCase)
                ? ObjFile.Read(dialog.FileName).ToGeometry()
                : GltfFile.Read(dialog.FileName);

            result = MeshImport.Replace(mesh, geometry);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, $"Could not replace {name}",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (result.CarriedSkinning)
        {
            bool clean = result.JointsMatched == result.JointsInFile
                && result.JointsMatched == result.BoneCount
                && result.PatchedVertices == 0;

            if (!Ask(owner, SkeletonNotice(name, result), clean ? MessageBoxImage.Information : MessageBoxImage.Warning))
                return null;
        }

        if (result.TransferredSkinning && !Ask(owner,
            $"The original mesh ({name}) is rigged to a skeleton, but the file you selected has no bone weights.\n\n"
            + "The Toolkit can copy suitable bone weights from the original mesh automatically. "
            + "This usually works well when the replacement has a similar shape, size and position.\n\n"
            + "If the new mesh is very different, some parts may move or deform incorrectly in the game.\n\n"
            + "Continue and create the bone weights automatically?"))
        {
            return null;
        }

        if (result.Cloth && !Ask(owner,
            $"{name} is marked as a dynamic/cloth mesh. Its render geometry and vertex counts can be replaced, "
            + "but the Toolkit does not rebuild the game's separate cloth simulation data.\n\n"
            + "A substantially different shape may deform incorrectly or become unstable in the game. "
            + "Continue with the replacement anyway?"))
        {
            return null;
        }

        byte[] rebuilt;

        try
        {
            rebuilt = mesh.Write();
            Mesh.Read(rebuilt);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, "The rebuilt mesh does not read back",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }

        summary = $"{result.Vertices} vertices, {result.Triangles} triangles, {result.Ranges} draw range(s), "
            + $"{resource.Length} -> {rebuilt.Length} bytes"
            + (result.RangesBefore != result.Ranges ? $" (was {result.RangesBefore} range(s), materials followed)" : "")
            + (result.CarriedSkinning ? ", weights from the file" : "")
            + (result.TransferredSkinning ? ", weights from the nearest original vertex" : "")
            + (result.PatchedVertices > 0 ? $", {result.PatchedVertices} vertex(es) fell back to the nearest original weights" : "");

        return rebuilt;
    }

    static string SkeletonNotice(string name, MeshImportResult result)
    {
        string headline = "The skeleton in this file is not imported.\n\n"
            + $"{name} keeps the bones it already has. Only the weights come across from your file, "
            + "matched up by bone name.\n\n";

        string fit = result.JointsMatched == result.JointsInFile && result.JointsMatched == result.BoneCount
            ? $"All {result.BoneCount} bones match, so nothing is lost."
            : $"{result.JointsMatched} of the {result.JointsInFile} bones in your file also sit on this mesh, "
              + $"which has {result.BoneCount}.";

        string patched = result.PatchedVertices > 0
            ? $"\n\n{result.PatchedVertices} of {result.Vertices} points found no bone of their own and use the "
              + "weights of the closest point of the old shape instead. Those spots may bend oddly."
            : "";

        return headline + fit + patched + "\n\nReplace anyway?";
    }

    static bool Ask(Window owner, string question, MessageBoxImage icon = MessageBoxImage.Warning) =>
        MessageBox.Show(owner, question, "Replace mesh", MessageBoxButton.YesNo, icon) == MessageBoxResult.Yes;
}
