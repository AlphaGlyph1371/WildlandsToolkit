using System.Collections.Generic;
using System.Windows.Media.Imaging;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Textures;

namespace Wildlands.Toolkit;

public sealed class TextureView
{
    public string Name { get; init; } = "";
    public TextureMap Texture { get; init; } = null!;
    public TextureMipSet Mips { get; init; } = null!;

    public int FocusLevel { get; set; }

    public bool FromCompiledMip { get; init; }

    public List<TextureMipLevel> Levels => Mips.Levels;

    public TextureMipLevel? Focus => Mips.Find(FocusLevel) ?? Mips.Best;

    public bool CanDraw => BlockDecoder.CanDecode(Texture.Format);
}

public static class TextureLoader
{
    public static TextureView FromTexture(string name, TextureMap texture, ArchiveSet? archives) =>
        FromTexture(name, texture, archives, null);

    public static TextureView FromTexture(string name, TextureMap texture, ArchiveSet? archives,
        IReadOnlyDictionary<ulong, byte[]>? replaced)
    {
        var mips = TextureMipSet.Collect(texture, archives, replaced);

        return new TextureView
        {
            Name = name,
            Texture = texture,
            Mips = mips,
            FocusLevel = mips.Best?.Level ?? 0,
        };
    }

    public static TextureView? FromMip(string name, CompiledMip mip, ArchiveSet? archives, out string note)
    {
        note = "";

        if (archives is null)
        {
            note = "no archive open, cannot find the parent TextureMap";
            return null;
        }

        var texture = TextureMipSet.LoadParent(mip, archives);
        if (texture is null)
        {
            note = $"parent TextureMap 0x{mip.TextureMapId:X} was not found in these archives";
            return null;
        }

        var mips = TextureMipSet.Collect(texture, archives);

        if (mips.Find((int)mip.Level) is null)
        {
            var own = TextureMipSet.Describe(texture, mip, name);
            if (own is null)
            {
                note = $"mip level {mip.Level} does not fit the parent texture";
                return null;
            }

            mips.Levels.Add(own);
            mips.Levels.Sort((a, b) => a.Level.CompareTo(b.Level));
        }

        return new TextureView
        {
            Name = name,
            Texture = texture,
            Mips = mips,
            FocusLevel = (int)mip.Level,
            FromCompiledMip = true,
        };
    }

    public static BitmapSource? Decode(TextureMap texture, TextureMipLevel level, out string note)
    {
        note = "";

        if (!BlockDecoder.CanDecode(texture.Format))
        {
            note = $"cannot draw {texture.Format} yet";
            return null;
        }

        int size = texture.Format.LevelSize(level.Width, level.Height);
        if (level.Pixels.Length < size)
        {
            note = "mip level is shorter than its format needs";
            return null;
        }

        var bgra = BlockDecoder.Decode(level.Pixels.AsSpan(0, size), texture.Format, level.Width, level.Height);

        var bitmap = BitmapSource.Create(level.Width, level.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, bgra, level.Width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static bool IsInvisibleWithAlpha(BitmapSource image)
    {
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);

        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] > 8)
                return false;
        }

        return true;
    }

    public static bool UsesAlphaAsCutout(BitmapSource image)
    {
        int stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);

        int middle = 0, clear = 0, total = 0;

        for (int i = 3; i < pixels.Length; i += 4)
        {
            total++;
            if (pixels[i] < 16) clear++;
            else if (pixels[i] < 239) middle++;
        }

        if (total == 0 || clear == 0)
            return false;

        return (double)middle / total <= 0.15;
    }

    public static BitmapSource WithoutAlpha(BitmapSource source)
    {
        int stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        for (int i = 3; i < pixels.Length; i += 4)
            pixels[i] = 255;

        var opaque = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        opaque.Freeze();
        return opaque;
    }

    public static BitmapSource IsolateChannels(BitmapSource source, bool red, bool green, bool blue)
    {
        int stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (!blue) pixels[i] = 0;
            if (!green) pixels[i + 1] = 0;
            if (!red) pixels[i + 2] = 0;
        }

        var masked = BitmapSource.Create(source.PixelWidth, source.PixelHeight, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, pixels, stride);
        masked.Freeze();
        return masked;
    }

    public static string ExplainEmpty(TextureView view)
    {
        if (!view.CanDraw)
            return $"cannot draw {view.Texture.Format} yet";

        if (view.Mips.MissingStreamed.Count > 0 && !view.Texture.HasPixels)
            return "every mip is streamed and none of them is in these archives";

        return view.Texture.HasPixels ? "mip chain does not match the stored size" : "no pixel data in this resource";
    }
}
