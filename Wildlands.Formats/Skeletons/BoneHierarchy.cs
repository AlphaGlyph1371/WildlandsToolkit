using System.Collections.Generic;
using System.Numerics;

namespace Wildlands.Formats.Models;

public static class BoneHierarchy
{
    public static int[] Resolve(List<MeshBone> bones, List<SkeletonBone>? skeleton)
    {
        var parents = new int[bones.Count];
        Array.Fill(parents, -1);

        if (skeleton is null)
            return parents;

        var meshIndexByHash = new Dictionary<uint, int>();
        for (int i = 0; i < bones.Count; i++)
            meshIndexByHash[bones[i].Name] = i;

        var skeletonIndexByHash = new Dictionary<uint, int>();
        for (int i = 0; i < skeleton.Count; i++)
            skeletonIndexByHash[skeleton[i].Name] = i;

        for (int i = 0; i < bones.Count; i++)
        {
            if (!skeletonIndexByHash.TryGetValue(bones[i].Name, out int skeletonIndex))
                continue;

            int ancestor = skeleton[skeletonIndex].ParentIndex;
            while (ancestor >= 0)
            {
                if (meshIndexByHash.TryGetValue(skeleton[ancestor].Name, out int meshIndex) && meshIndex != i)
                {
                    parents[i] = meshIndex;
                    break;
                }

                ancestor = skeleton[ancestor].ParentIndex;
            }
        }

        return parents;
    }

    public static Vector3 WorldPosition(MeshBone bone)
    {
        Matrix4x4.Invert(bone.Matrix, out var boneToMesh);
        return boneToMesh.Translation;
    }
}
