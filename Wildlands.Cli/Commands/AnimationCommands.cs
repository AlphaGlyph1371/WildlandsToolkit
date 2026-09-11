using System.IO;
using Wildlands.Formats.Data;
using Wildlands.Formats.Models;

static partial class Commands
{
    internal static int ShowAnimation(string dataFile, string filter)
    {
        using var stream = File.OpenRead(dataFile);
        DataFile file = DataFile.Read(stream);
        int shown = 0;
        foreach (Resource resource in file.Resources.Where(item => item.ClassHash == Animation.ClassHash
                     && (filter.Length == 0 || item.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))))
        {
            AnimationAsset asset = Animation.Read(resource.Data);
            Console.WriteLine($"0x{asset.Id:X12} {resource.Name}  {resource.Data.Length} byte(s)");
            Console.WriteLine($"  version {asset.Version}, {asset.Duration:0.###} s "
                + $"({asset.Duration * 30:0.#} frames at 30 fps), bone set 0x{asset.BoneSet:X8}");
            Console.WriteLine($"  {asset.Objects.Count} object(s), {asset.Tracks.Count} track(s)");
            foreach (var group in asset.Objects.GroupBy(item => item.ClassHash)
                         .OrderByDescending(group => group.Count()))
                Console.WriteLine($"    class 0x{group.Key:X8} x{group.Count()}");
            foreach (AnimationTrack track in asset.Tracks.Take(24))
                Console.WriteLine($"    bone 0x{track.Bone:X8} tag 0x{track.Tag:X2} "
                    + $"(values {track.ValueFormat}, times {track.TimeFormat}) "
                    + $"{track.KeyCount} key(s) of {track.Stride} byte(s), "
                    + $"{track.Times.Length} time byte(s), mode 0x{track.Mode:X2}");
            if (asset.Tracks.Count > 24)
                Console.WriteLine($"    ... {asset.Tracks.Count - 24} more track(s)");
            shown++;
        }

        if (shown == 0)
            Console.WriteLine("no Animation matched");
        return 0;
    }

    internal static int CycleAnimations(string target)
    {
        var paths = Directory.Exists(target)
            ? Directory.EnumerateFiles(target, "*.data")
            : [target];
        int read = 0, same = 0, differ = 0, broken = 0;
        var reasons = new Dictionary<string, int>();
        var formats = new Dictionary<int, int>();
        var tags = new Dictionary<byte, int>();

        foreach (string path in paths)
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

            foreach (Resource resource in file.Resources.Where(item => item.ClassHash == Animation.ClassHash))
            {
                read++;
                try
                {
                    AnimationAsset asset = Animation.Read(resource.Data);
                    foreach (AnimationTrack track in asset.Tracks)
                    {
                        formats[track.ValueFormat * 3 + track.TimeFormat] =
                            formats.GetValueOrDefault(track.ValueFormat * 3 + track.TimeFormat) + 1;
                        tags[track.Tag] = tags.GetValueOrDefault(track.Tag) + 1;
                    }

                    byte[] again = Animation.Write(asset);
                    if (again.AsSpan().SequenceEqual(resource.Data))
                        same++;
                    else
                    {
                        differ++;
                        if (differ <= 5)
                            Console.WriteLine($"0x{resource.Id:X12} {resource.Name}: "
                                + $"{resource.Data.Length} byte(s) in, {again.Length} out, "
                                + $"first difference at 0x{FirstDifference(resource.Data, again):X}");
                    }
                }
                catch (Exception error)
                {
                    broken++;
                    string reason = error.Message;
                    reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
                    if (broken <= 5)
                        Console.WriteLine($"0x{resource.Id:X12} {resource.Name}: {reason}");
                }
            }
        }

        Console.WriteLine($"{read} Animation(s): {same} byte for byte, {differ} different, {broken} unreadable");
        foreach ((string reason, int count) in reasons.OrderByDescending(pair => pair.Value).Take(8))
            Console.WriteLine($"  x{count} {reason}");
        Console.WriteLine("tags: " + string.Join(", ", tags.OrderBy(pair => pair.Key)
            .Select(pair => $"0x{pair.Key:X2} x{pair.Value}")));
        return broken == 0 && differ == 0 ? 0 : 1;
    }

    internal static int DeriveAnimationFormats(string folder)
    {
        var seen = new Dictionary<int, Dictionary<int, int>>();
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

            foreach (Resource resource in file.Resources.Where(item => item.ClassHash == Animation.ClassHash))
                WalkForFormats(resource.Data, seen);
        }

        foreach ((int format, Dictionary<int, int> strides) in seen.OrderBy(pair => pair.Key))
            Console.WriteLine($"value format {format} (tags 0x{Animation.FirstTag + format * 3:X2}.."
                + $"0x{Animation.FirstTag + format * 3 + 2:X2}): "
                + string.Join(", ", strides.OrderByDescending(pair => pair.Value)
                    .Select(pair => $"stride {pair.Key} x{pair.Value}")));
        return 0;
    }

    static void WalkForFormats(byte[] data, Dictionary<int, Dictionary<int, int>> seen)
    {
        var objects = AnvilObjects.Scan(data);
        if (objects.Count == 0 || objects[^1].ClassHash != Animation.TrackClassHash)
            return;
        int offset = objects[^1].Offset + 16 + 6;
        if (offset + 4 > data.Length)
            return;
        int tracks = BitConverter.ToInt32(data, offset);
        offset += 4;
        for (int i = 0; i < tracks && offset + 9 <= data.Length; i++)
        {
            byte tag = data[offset];
            if (tag < Animation.FirstTag)
                return;
            int size = BitConverter.ToInt32(data, offset + 1);
            int keys = BitConverter.ToInt32(data, offset + 5);
            if (keys <= 0 || size <= 0)
                return;
            int format = (tag - Animation.FirstTag) / 3, times = (tag - Animation.FirstTag) % 3;
            int stride;
            try
            {
                stride = Animation.StrideOf(format);
            }
            catch (NotSupportedException)
            {
                foreach (int align in (int[])[4, 2, 1])
                {
                    int head = times == 0 ? keys + 1 : (times == 1 ? 2 * keys : 4);
                    int rest = size - (head + align - 1) / align * align;
                    if (rest <= 0 || rest % keys != 0)
                        continue;
                    int candidate = rest / keys;
                    if ((candidate % 4 == 0 ? 4 : candidate % 2 == 0 ? 2 : 1) != align)
                        continue;
                    if (!seen.TryGetValue(format, out Dictionary<int, int>? strides))
                        seen[format] = strides = [];
                    strides[candidate] = strides.GetValueOrDefault(candidate) + 1;
                }

                return;
            }

            int timeBytes = times switch
            {
                0 => keys - 1,
                1 => 2 * (keys - 1),
                _ => data[offset + 9] & 0x0F,
            };
            offset += 9 + timeBytes + (times == 2 ? 1 : 0) + keys * stride;
        }
    }

    internal static int TryAnimationStrides(string folder, string[] overrides)
    {
        var table = new Dictionary<int, int>();
        foreach (string entry in overrides)
        {
            string[] parts = entry.Split('=');
            table[int.Parse(parts[0])] = int.Parse(parts[1]);
        }

        int read = 0, closed = 0;
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

            foreach (Resource resource in file.Resources.Where(item => item.ClassHash == Animation.ClassHash))
            {
                read++;
                if (Closes(resource.Data, table))
                    closed++;
            }
        }

        Console.WriteLine($"{closed} of {read} Animation(s) close exactly with "
            + string.Join(" ", table.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")));
        return 0;
    }

    static bool Closes(byte[] data, Dictionary<int, int> table)
    {
        var objects = AnvilObjects.Scan(data);
        if (objects.Count == 0 || objects[^1].ClassHash != Animation.TrackClassHash)
            return false;
        int offset = objects[^1].Offset + 16 + 6;
        if (offset + 4 > data.Length)
            return false;
        int tracks = BitConverter.ToInt32(data, offset);
        offset += 4;
        for (int i = 0; i < tracks; i++)
        {
            if (offset + 9 > data.Length)
                return false;
            byte tag = data[offset];
            if (tag < Animation.FirstTag)
                return false;
            int keys = BitConverter.ToInt32(data, offset + 5);
            if (keys <= 0)
                return false;
            int format = (tag - Animation.FirstTag) / 3, times = (tag - Animation.FirstTag) % 3;
            int stride;
            if (table.TryGetValue(format, out int given))
                stride = given;
            else
            {
                try
                {
                    stride = Animation.StrideOf(format);
                }
                catch (NotSupportedException)
                {
                    return false;
                }
            }

            int timeBytes = times switch
            {
                0 => keys - 1,
                1 => 2 * (keys - 1),
                _ => data[offset + 9] & 0x0F,
            };
            offset += 9 + timeBytes + (times == 2 ? 1 : 0) + keys * stride;
        }

        return offset == data.Length;
    }
}
