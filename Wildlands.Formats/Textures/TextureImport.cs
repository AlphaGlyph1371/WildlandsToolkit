using System;
using System.Collections.Generic;
using System.IO;

namespace Wildlands.Formats.Textures;

public static class TextureImport
{
    public static List<byte[]> EncodeLevels(TextureMap texture, byte[] bgra, int width, int height)
    {
        if (texture.Faces != 1)
            throw new NotSupportedException("Cube maps cannot be replaced yet.");

        if (texture.Depth > 1)
            throw new NotSupportedException($"This is a volume texture with {texture.Depth} slices, which cannot be replaced yet.");

        var levels = new List<byte[]>();
        var current = bgra;
        int levelWidth = width;
        int levelHeight = height;

        while (true)
        {
            levels.Add(BlockEncoder.Encode(current, texture.Format, levelWidth, levelHeight));

            if (levelWidth == 1 && levelHeight == 1)
                break;

            current = Downsample(current, levelWidth, levelHeight);
            levelWidth = Math.Max(1, levelWidth / 2);
            levelHeight = Math.Max(1, levelHeight / 2);
        }

        if (levels.Count < texture.MipCount)
            throw new InvalidDataException($"The image is {width} x {height} and its mip chain has {levels.Count} levels, where the texture needs {texture.MipCount}. Use an image at least as large as the texture.");

        return levels;
    }

    public static byte[] EmbeddedChain(TextureMap texture, IReadOnlyList<byte[]> levels, int width, int height)
    {
        int start = texture.FirstStoredLevel();
        if (start < 0)
            return [];

        if (width != (int)texture.Width || height != (int)texture.Height)
        {
            var grown = new List<byte>();
            for (int level = start; level < texture.MipCount; level++)
                grown.AddRange(levels[level]);

            return grown.ToArray();
        }

        var chain = new byte[texture.Pixels.Length];
        int offset = 0;

        for (int level = start; level < levels.Count; level++)
        {
            var pixels = levels[level];
            if (pixels.Length == 0 || offset + pixels.Length > chain.Length)
                break;

            pixels.CopyTo(chain, offset);
            offset += pixels.Length;
        }

        if (offset != chain.Length)
            throw new InvalidDataException($"The rebuilt mip chain is {offset} bytes where the texture stores {chain.Length}.");

        return chain;
    }

    public static byte[] ReplacePixels(byte[] resource, int offset, byte[] pixels)
    {
        int previous = BitConverter.ToInt32(resource, offset - 4);
        int tail = resource.Length - offset - previous;

        if (tail < 0)
            throw new InvalidDataException("The pixel block reaches past the end of its resource.");

        var output = new byte[offset + pixels.Length + tail];

        resource.AsSpan(0, offset).CopyTo(output);
        pixels.CopyTo(output.AsSpan(offset));
        resource.AsSpan(offset + previous).CopyTo(output.AsSpan(offset + pixels.Length));

        BitConverter.TryWriteBytes(output.AsSpan(offset - 4), pixels.Length);
        return output;
    }

    public static byte[] Resize(TextureMap texture, byte[] resource, int width, int height, byte[] pixels)
    {
        var output = ReplacePixels(resource, texture.PixelOffset, pixels);

        BitConverter.TryWriteBytes(output.AsSpan(13), (uint)width);
        BitConverter.TryWriteBytes(output.AsSpan(17), (uint)height);
        BitConverter.TryWriteBytes(output.AsSpan(texture.CompiledSizeOffset), (uint)width);
        BitConverter.TryWriteBytes(output.AsSpan(texture.CompiledSizeOffset + 4), (uint)height);

        return output;
    }

    public static byte[] Downsample(byte[] bgra, int width, int height)
    {
        int halfWidth = Math.Max(1, width / 2);
        int halfHeight = Math.Max(1, height / 2);

        var output = new byte[halfWidth * halfHeight * 4];

        for (int y = 0; y < halfHeight; y++)
        {
            int y0 = Math.Min(y * 2, height - 1);
            int y1 = Math.Min(y * 2 + 1, height - 1);

            for (int x = 0; x < halfWidth; x++)
            {
                int x0 = Math.Min(x * 2, width - 1);
                int x1 = Math.Min(x * 2 + 1, width - 1);

                int a = (y0 * width + x0) * 4;
                int b = (y0 * width + x1) * 4;
                int c = (y1 * width + x0) * 4;
                int d = (y1 * width + x1) * 4;
                int to = (y * halfWidth + x) * 4;

                for (int channel = 0; channel < 4; channel++)
                {
                    output[to + channel] = (byte)((bgra[a + channel] + bgra[b + channel]
                        + bgra[c + channel] + bgra[d + channel] + 2) / 4);
                }
            }
        }

        return output;
    }
}
