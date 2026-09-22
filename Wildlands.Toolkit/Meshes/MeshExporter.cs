using System.Collections.Generic;
using System.IO;
using System.Windows;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public enum MeshExportFormat
{
    Glb,
    Fbx,
    Obj,
}

public static class MeshExporter
{
    public static string Save(Window owner, Mesh mesh, string name, List<Resource> siblings,
        SkeletonIndex? skeletonIndex, AppSettings settings,
        List<SkeletonBone>? chosenSkeleton = null)
    {
        var dialog = new MeshExportWindow(mesh, name, siblings, skeletonIndex, settings,
            chosenSkeleton) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog.Summary : "";
    }

    public static string Export(Mesh mesh, string name, string path, MeshExportFormat format,
        bool includeSkin, List<SkeletonBone>? skeleton)
    {
        var layout = VertexLayout.For(mesh.VertexFormat, mesh.VertexStride);
        bool skinned = includeSkin && layout.IsSkinned && mesh.Bones.Count > 0;

        if (format == MeshExportFormat.Obj)
        {
            ObjFile.Write(mesh, name, path);
            return $"Wrote {Path.GetFileName(path)} ({mesh.Data.Standard.Count} draw range(s), "
                + "positions, normals and one uv set; OBJ cannot store a skeleton)";
        }

        if (format == MeshExportFormat.Glb)
        {
            GltfFile.Write(mesh, name, path, skeleton, includeSkin);
            return $"Wrote {Path.GetFileName(path)} ({mesh.Data.Standard.Count} primitive(s), "
                + $"{layout.UvCount} uv set(s)" + (layout.HasColor ? ", vertex colours" : "")
                + (skinned
                    ? $", skin over {mesh.Bones.Count} bones"
                        + (skeleton is null ? " with a flat hierarchy" : " with their hierarchy")
                    : "") + ")";
        }

        FbxWriter.Write(mesh, name, path, skeleton, includeSkin: includeSkin);
        string how = !skinned ? "static mesh"
            : skeleton is null ? $"skin over {mesh.Bones.Count} bones with a flat hierarchy"
            : $"skin over {mesh.Bones.Count} bones with their hierarchy";
        return $"Wrote {Path.GetFileName(path)} ({how})";
    }
}
