using System.IO;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Models;

namespace Wildlands.Formats;

public sealed class SkeletonIndex
{
    const uint FileMagic = 0x534B4C31; // "SKL1"

    readonly List<List<SkeletonBone>> _skeletons = [];
    readonly Dictionary<uint, List<int>> _byBoneHash = [];

    public int SkeletonCount => _skeletons.Count;

    public static SkeletonIndex Build(IEnumerable<ForgeArchive> archives, Action<string>? onArchive = null)
    {
        var index = new SkeletonIndex();

        foreach (var archive in archives)
        {
            onArchive?.Invoke(Path.GetFileName(archive.FilePath));

            foreach (var entry in archive.Entries)
            {
                if (entry.FileExtension != ".data") continue;

                List<Resource> resources;
                try
                {
                    using var stream = new MemoryStream(archive.ReadEntry(entry));
                    resources = DataFile.Read(stream).Resources;
                }
                catch { continue; }

                foreach (var resource in resources)
                {
                    if (resource.ClassHash != Skeleton.ClassHash) continue;

                    List<SkeletonBone> bones;
                    try { bones = Skeleton.Read(resource.Data); } catch { continue; }

                    if (bones.Count > 0)
                        index.Add(bones);
                }
            }
        }

        return index;
    }

    void Add(List<SkeletonBone> bones)
    {
        int skeletonIndex = _skeletons.Count;
        _skeletons.Add(bones);

        foreach (var bone in bones)
        {
            if (!_byBoneHash.TryGetValue(bone.Name, out var list))
                _byBoneHash[bone.Name] = list = [];
            list.Add(skeletonIndex);
        }
    }

    public List<SkeletonBone>? FindBest(IEnumerable<uint> meshBoneHashes)
    {
        var votes = new Dictionary<int, int>();

        foreach (uint hash in meshBoneHashes)
        {
            if (!_byBoneHash.TryGetValue(hash, out var skeletons))
                continue;

            foreach (int s in skeletons)
                votes[s] = votes.GetValueOrDefault(s) + 1;
        }

        if (votes.Count == 0)
            return null;

        int best = votes.MaxBy(kv => kv.Value).Key;
        return _skeletons[best];
    }

    public static string PartialPath(string path) => path + ".partial";

    public void Save(string path)
    {
        string partial = PartialPath(path);

        using (var stream = File.Create(partial))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(FileMagic);
            writer.Write(_skeletons.Count);

            foreach (var bones in _skeletons)
            {
                writer.Write(bones.Count);
                foreach (var bone in bones)
                {
                    writer.Write(bone.Name);
                    writer.Write(bone.ParentIndex);
                }
            }
        }

        File.Move(partial, path, overwrite: true);
    }

    public static SkeletonIndex? Load(string path)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);

            if (reader.ReadUInt32() != FileMagic)
                return null;

            var index = new SkeletonIndex();
            int skeletonCount = reader.ReadInt32();

            for (int s = 0; s < skeletonCount; s++)
            {
                int boneCount = reader.ReadInt32();
                var bones = new List<SkeletonBone>(boneCount);

                for (int b = 0; b < boneCount; b++)
                    bones.Add(new SkeletonBone { Name = reader.ReadUInt32(), ParentIndex = reader.ReadInt32() });

                index.Add(bones);
            }

            return index;
        }
        catch
        {
            return null;
        }
    }
}
