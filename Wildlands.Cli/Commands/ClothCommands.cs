using System.IO;
using Wildlands.Formats.Data;
using Wildlands.Formats.Models;

static partial class Commands
{
    internal static int CycleCloth(string folder)
    {
        int read = 0, same = 0, differ = 0, broken = 0;
        var classes = new Dictionary<uint, int>();
        var reasons = new Dictionary<string, int>();
        long faces = 0, links = 0;

        foreach (string path in Directory.EnumerateFiles(folder, "*.data"))
        {
            DataFile file;
            try
            {
                using var stream = File.OpenRead(path);
                file = DataFile.Read(stream);
            }
            catch
            {
                continue;
            }

            foreach (Resource resource in file.Resources.Where(item => item.ClassHash == Cloth.ClassHash))
            {
                read++;
                try
                {
                    ClothAsset asset = Cloth.Read(resource.Data);
                    foreach (ClothObject item in asset.Objects)
                        classes[item.ClassHash] = classes.GetValueOrDefault(item.ClassHash) + 1;
                    faces += asset.Faces.Count();
                    links += asset.Links.Count();
                    if (Cloth.Write(asset).AsSpan().SequenceEqual(resource.Data))
                        same++;
                    else
                        differ++;
                }
                catch (Exception error)
                {
                    broken++;
                    reasons[error.Message] = reasons.GetValueOrDefault(error.Message) + 1;
                }
            }
        }

        Console.WriteLine($"{read} Cloth: {same} byte for byte, {differ} different, {broken} unreadable");
        Console.WriteLine($"{faces} face(s), {links} link(s)");
        foreach ((uint hash, int count) in classes.OrderByDescending(pair => pair.Value))
            Console.WriteLine($"  class 0x{hash:X8} x{count}");
        foreach ((string reason, int count) in reasons.OrderByDescending(pair => pair.Value).Take(5))
            Console.WriteLine($"  x{count} {reason}");
        return same == read ? 0 : 1;
    }
}
