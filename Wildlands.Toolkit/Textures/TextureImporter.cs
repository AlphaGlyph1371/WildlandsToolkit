using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Textures;

namespace Wildlands.Toolkit;

public static class TextureImporter
{
    public static string Filter =>
        "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tif;*.tiff;*.dds|"
        + "PNG|*.png|JPEG|*.jpg;*.jpeg|Bitmap|*.bmp|TIFF|*.tif;*.tiff|DDS|*.dds|All files|*.*";

    public static List<string> Targets(TextureMap texture, Location location, string resourceName,
        ArchiveSet? archives)
    {
        var list = new List<string> { Describe(location.ArchivePath, location.EntryName, resourceName) };

        foreach (ulong id in texture.StreamedMips)
        {
            var found = archives?.FindResource(id, CompiledMip.ClassHash);

            list.Add(found is null
                ? $"not found, left alone   streamed mip 0x{id:X}"
                : Describe(found.File.ArchivePath, found.File.Name, found.Resource.Name));
        }

        return list;
    }

    static string Describe(string archivePath, string entryName, string resourceName) =>
        $"{Path.GetFileName(archivePath)}  >  {entryName}  >  {resourceName}";

    public static List<PendingChange> Build(string path, TextureMap texture, byte[] resource,
        Location location, int resourceIndex, string resourceName, ArchiveSet? archives,
        bool generateMips, out string summary)
    {
        var levels = LevelsFor(path, texture, generateMips, out string how, out int width, out int height);

        var chain = TextureImport.EmbeddedChain(texture, levels, width, height);

        var changes = new List<PendingChange>
        {
            new(location.ArchivePath, location.EntryIndex, location.EntryName, resourceIndex, resourceName,
                TextureImport.Resize(texture, resource, width, height, chain),
                ResourceClassHash: TextureMap.ClassHash),
        };

        int streamed = 0;
        int missing = 0;

        foreach (ulong id in texture.StreamedMips)
        {
            var found = archives?.FindResource(id, CompiledMip.ClassHash);
            if (found is null)
            {
                missing++;
                continue;
            }

            var mip = CompiledMip.Read(found.Resource.Data);
            if (mip.Level >= levels.Count)
            {
                missing++;
                continue;
            }

            var pixels = levels[(int)mip.Level];

            changes.Add(new PendingChange(found.File.ArchivePath, found.File.EntryIndex, found.File.Name,
                found.ResourceIndex, found.Resource.Name,
                TextureImport.ReplacePixels(found.Resource.Data, mip.PixelOffset, pixels),
                ResourceClassHash: CompiledMip.ClassHash));

            streamed++;
        }

        string size = width == (int)texture.Width && height == (int)texture.Height
            ? $"{width} x {height}"
            : $"{texture.Width} x {texture.Height} -> {width} x {height}";

        summary = $"{size} {texture.Format}, {how}, "
            + $"{changes.Count} resource(s): the texture and {streamed} streamed mip(s)";

        if (missing > 0)
            summary += $", {missing} streamed mip(s) not found and left alone";

        return changes;
    }

    internal static List<byte[]> LevelsFor(string path, TextureMap texture, bool generateMips, out string how,
        out int width, out int height)
    {
        if (Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(path);
            var dds = DdsReader.Read(file);

            width = dds.Width;
            height = dds.Height;

            if (!generateMips && dds.Format == texture.Format && dds.MipCount >= texture.MipCount)
            {
                var copied = new List<byte[]>();
                for (int level = 0; level < dds.MipCount; level++)
                    copied.Add(dds.Level(level));

                how = $"taken over unchanged, {copied.Count} mip level(s)";
                return Extend(copied, texture, dds.Width, dds.Height);
            }

            var (pixels, _, _) = DecodeDds(dds);
            how = dds.Format == texture.Format
                ? $"re-encoded, the dds has {dds.MipCount} of {texture.MipCount} mip levels"
                : $"converted from {dds.Format}";

            return TextureImport.EncodeLevels(texture, pixels, width, height);
        }

        var image = LoadImage(path);
        width = image.Width;
        height = image.Height;
        how = $"encoded to {texture.Format}";
        return TextureImport.EncodeLevels(texture, image.Pixels, width, height);
    }

    static List<byte[]> Extend(List<byte[]> levels, TextureMap texture, int width, int height)
    {
        for (int i = 1; i < levels.Count; i++)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        var pixels = BlockDecoder.Decode(levels[^1], texture.Format, width, height);

        while (width > 1 || height > 1)
        {
            pixels = TextureImport.Downsample(pixels, width, height);
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);

            levels.Add(BlockEncoder.Encode(pixels, texture.Format, width, height));
        }

        return levels;
    }

    static (byte[] Pixels, int Width, int Height) DecodeDds(DdsImage dds)
    {
        var pixels = BlockDecoder.Decode(dds.Level(0), dds.Format, dds.Width, dds.Height);
        return (pixels, dds.Width, dds.Height);
    }

    public static (byte[] Pixels, int Width, int Height) LoadPreview(string path)
    {
        if (!Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase))
            return LoadImage(path);

        using var file = File.OpenRead(path);
        return DecodeDds(DdsReader.Read(file));
    }

    public static (byte[] Pixels, int Width, int Height) LoadImage(string path)
    {
        var decoder = BitmapDecoder.Create(new Uri(Path.GetFullPath(path)), BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;

        var pixels = new byte[width * height * 4];
        converted.CopyPixels(pixels, width * 4, 0);

        return (pixels, width, height);
    }
}
