using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public readonly record struct SkyAdjust(float Exposure, float Red, float Green, float Blue, float Saturation)
{
    public static SkyAdjust None { get; } = new(1f, 1f, 1f, 1f, 1f);

    public bool IsNone => Exposure == 1f && Red == 1f && Green == 1f && Blue == 1f && Saturation == 1f;

    public (float Red, float Green, float Blue) Apply(float red, float green, float blue)
    {
        red *= Exposure * Red;
        green *= Exposure * Green;
        blue *= Exposure * Blue;
        if (Saturation == 1f)
            return (red, green, blue);

        float grey = 0.2126f * red + 0.7152f * green + 0.0722f * blue;
        return (grey + (red - grey) * Saturation,
            grey + (green - grey) * Saturation,
            grey + (blue - grey) * Saturation);
    }
}

public static class SkyImage
{
    public static int SliceCount(LayeredSkyLayer layer) => Math.Max(1, layer.Depth);

    public static BitmapSource Render(LayeredSkyLayer layer, int slice, SkyAdjust adjust, int zoom)
    {
        ArgumentNullException.ThrowIfNull(layer);
        int width = layer.Width, height = layer.Height;
        var pixels = new byte[width * zoom * height * zoom * 4];
        int stride = width * zoom * 4;

        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            (float red, float green, float blue) = ReadTexel(layer, slice, x, y);
            (red, green, blue) = adjust.Apply(red, green, blue);
            byte r = ToDisplay(red), g = ToDisplay(green), b = ToDisplay(blue);
            for (int dy = 0; dy < zoom; dy++)
            for (int dx = 0; dx < zoom; dx++)
            {
                int at = (y * zoom + dy) * stride + (x * zoom + dx) * 4;
                pixels[at] = b;
                pixels[at + 1] = g;
                pixels[at + 2] = r;
                pixels[at + 3] = 255;
            }
        }

        var bitmap = BitmapSource.Create(width * zoom, height * zoom, 96, 96,
            PixelFormats.Bgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }

    public static void Bake(LayeredSkyLayer layer, SkyAdjust adjust)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (adjust.IsNone)
            return;

        for (int texel = 0; texel + layer.BytesPerTexel <= layer.Texels.Length;
             texel += layer.BytesPerTexel)
        {
            float red = (float)BitConverter.ToHalf(layer.Texels, texel);
            float green = (float)BitConverter.ToHalf(layer.Texels, texel + 2);
            float blue = (float)BitConverter.ToHalf(layer.Texels, texel + 4);
            (red, green, blue) = adjust.Apply(red, green, blue);
            BitConverter.TryWriteBytes(layer.Texels.AsSpan(texel), (Half)red);
            BitConverter.TryWriteBytes(layer.Texels.AsSpan(texel + 2), (Half)green);
            BitConverter.TryWriteBytes(layer.Texels.AsSpan(texel + 4), (Half)blue);
        }
    }

    public static float Brightness(LayeredSkyLayer layer, int slice)
    {
        double sum = 0;
        int count = layer.Width * layer.Height;
        for (int y = 0; y < layer.Height; y++)
        for (int x = 0; x < layer.Width; x++)
        {
            (float red, float green, float blue) = ReadTexel(layer, slice, x, y);
            sum += 0.2126 * red + 0.7152 * green + 0.0722 * blue;
        }

        return count == 0 ? 0f : (float)(sum / count);
    }

    static (float Red, float Green, float Blue) ReadTexel(LayeredSkyLayer layer, int slice, int x, int y)
    {
        int texel = ((slice * layer.Height + y) * layer.Width + x) * layer.BytesPerTexel;
        if (texel + 6 > layer.Texels.Length)
            return (0f, 0f, 0f);
        return ((float)BitConverter.ToHalf(layer.Texels, texel),
            (float)BitConverter.ToHalf(layer.Texels, texel + 2),
            (float)BitConverter.ToHalf(layer.Texels, texel + 4));
    }

    static byte ToDisplay(float value) =>
        (byte)Math.Clamp(MathF.Pow(Math.Max(value, 0f), 1f / 2.2f) * 255f, 0f, 255f);
}
