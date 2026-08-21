using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using Wildlands.Formats.Textures;

namespace Wildlands.Toolkit;

public enum ExportFormat
{
    Png,
    Jpeg,
    Bmp,
    Tiff,

    // Keeps the pixels exactly as the game stores them, block compression and all
    Dds,
}

public static class TextureExporter
{
    public static string ExtensionOf(ExportFormat format) => format switch
    {
        ExportFormat.Png => ".png",
        ExportFormat.Jpeg => ".jpg",
        ExportFormat.Bmp => ".bmp",
        ExportFormat.Tiff => ".tif",
        _ => ".dds",
    };

    public static int FileCount(ExportFormat format, int levelCount)
    {
        return format == ExportFormat.Dds ? 1 : levelCount;
    }

    public static string Export(TextureView view, IReadOnlyList<TextureMipLevel> levels, ExportFormat format, string folder, string name)
    {
        Directory.CreateDirectory(folder);

        if (format == ExportFormat.Dds)
        {
            string path = Path.Combine(folder, name + ".dds");

            using var file = File.Create(path);
            DdsWriter.Write(file, view.Texture.Format, levels);

            return $"Wrote {Path.GetFileName(path)} with {levels.Count} mip level(s)";
        }

        int written = 0;
        string? last = null;

        foreach (var level in levels)
        {
            var bitmap = TextureLoader.Decode(view.Texture, level, out string note);
            if (bitmap is null)
                continue;

            string fileName = levels.Count == 1 ? name : $"{name}_mip{level.Level}";
            string path = Path.Combine(folder, fileName + ExtensionOf(format));

            using (var file = File.Create(path))
                EncoderFor(format, bitmap).Save(file);

            last = Path.GetFileName(path);
            written++;
        }

        if (written == 0)
            return "Nothing could be decoded, so nothing was written";

        return written == 1 ? $"Wrote {last}" : $"Wrote {written} files to {folder}";
    }

    static BitmapEncoder EncoderFor(ExportFormat format, BitmapSource bitmap)
    {
        BitmapEncoder encoder = format switch
        {
            ExportFormat.Jpeg => new JpegBitmapEncoder(),
            ExportFormat.Bmp => new BmpBitmapEncoder(),
            ExportFormat.Tiff => new TiffBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };

        if (format == ExportFormat.Jpeg)
            bitmap = new FormatConvertedBitmap(bitmap, System.Windows.Media.PixelFormats.Bgr24, null, 0);

        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        return encoder;
    }
}
