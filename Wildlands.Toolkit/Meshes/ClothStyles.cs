using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Materials;

namespace Wildlands.Toolkit;

public static class ClothStyles
{
    static readonly ConditionalWeakTable<ArchiveSet, Task<ClothStyleIndex>> Indexes = new();

    public static async Task<List<ClothStyle>> For(string meshName, IReadOnlyList<Material> materials, ArchiveSet archives)
    {
        var sources = new List<(string Path, long Offset, int Length)>();
        var wanted = new HashSet<string>(ClothStyleIndex.Containers);
        string model = Regex.Replace(meshName, "_LOD\\d+$", "", RegexOptions.IgnoreCase);
        var modelIds = new HashSet<ulong>();

        foreach (var archive in archives.All())
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.Name == model)
                    modelIds.Add(entry.Id);

                if (entry.FileExtension == ".data" && wanted.Remove(entry.Name))
                    sources.Add((archive.FilePath, entry.Offset, entry.Length));
            }
        }

        var index = await Indexes.GetValue(archives, _ => Task.Run(() => Build(sources)));
        return await Task.Run(() => index.ForModel(modelIds, materials));
    }

    static ClothStyleIndex Build(List<(string Path, long Offset, int Length)> sources)
    {
        var index = new ClothStyleIndex();

        foreach (var (path, offset, length) in sources)
        {
            var data = new byte[length];
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                file.Position = offset;
                file.ReadExactly(data);
            }

            using var stream = new MemoryStream(data);
            foreach (var resource in DataFile.Read(stream).Resources)
                index.Add(resource);
        }

        return index;
    }
}
