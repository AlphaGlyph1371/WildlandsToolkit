using System.Collections.Generic;
using System.Linq;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public static class SkeletonFinder
{
    public static List<SkeletonBone>? Find(List<Resource> siblings, List<MeshBone> meshBones, SkeletonIndex? index)
    {
        var meshHashes = meshBones.Select(b => b.Name).ToHashSet();

        List<SkeletonBone>? best = null;
        int bestOverlap = 0;

        foreach (var candidate in siblings.Where(r => r.ClassHash == Skeleton.ClassHash))
        {
            List<SkeletonBone> bones;
            try { bones = Skeleton.Read(candidate.Data); }
            catch { continue; }

            int overlap = bones.Count(b => meshHashes.Contains(b.Name));
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = bones;
            }
        }

        return best ?? index?.FindBest(meshHashes);
    }
}
