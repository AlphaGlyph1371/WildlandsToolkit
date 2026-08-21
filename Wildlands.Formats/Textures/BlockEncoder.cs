using System;

namespace Wildlands.Formats.Textures;

public static class BlockEncoder
{
    public static byte[] Encode(ReadOnlySpan<byte> bgra, PixelFormat format, int width, int height)
    {
        int expected = width * height * 4;
        if (bgra.Length < expected)
            throw new ArgumentException($"Need {expected} bytes of BGRA for {width} x {height}.", nameof(bgra));

        switch (format)
        {
            case PixelFormat.Bc1:
                return EncodeBlocks(bgra, width, height, 8, EncodeBc1Block);
            case PixelFormat.Bc2:
                return EncodeBlocks(bgra, width, height, 16, EncodeBc2Block);
            case PixelFormat.Bc3:
                return EncodeBlocks(bgra, width, height, 16, EncodeBc3Block);
            case PixelFormat.Bc7:
                return EncodeBlocks(bgra, width, height, 16, Bc7Encoder.EncodeBlock);

            case PixelFormat.B8G8R8A8:
                return bgra[..expected].ToArray();

            case PixelFormat.R8G8B8A8Signed:
            {
                var output = new byte[expected];
                for (int i = 0; i < width * height; i++)
                {
                    output[i * 4 + 0] = (byte)(sbyte)(bgra[i * 4 + 2] - 128);
                    output[i * 4 + 1] = (byte)(sbyte)(bgra[i * 4 + 1] - 128);
                    output[i * 4 + 2] = (byte)(sbyte)(bgra[i * 4 + 0] - 128);
                    output[i * 4 + 3] = (byte)(sbyte)(bgra[i * 4 + 3] - 128);
                }
                return output;
            }

            case PixelFormat.R8:
            {
                var output = new byte[width * height];
                for (int i = 0; i < output.Length; i++)
                    output[i] = bgra[i * 4 + 2];
                return output;
            }

            default:
                throw new NotSupportedException($"Cannot write {format} yet.");
        }
    }

    delegate void BlockReader(ReadOnlySpan<byte> pixels, Span<byte> block);

    static byte[] EncodeBlocks(ReadOnlySpan<byte> bgra, int width, int height, int blockSize, BlockReader reader)
    {
        int blocksX = Math.Max(1, (width + 3) / 4);
        int blocksY = Math.Max(1, (height + 3) / 4);

        var output = new byte[blocksX * blocksY * blockSize];
        Span<byte> pixels = stackalloc byte[16 * 4];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                for (int y = 0; y < 4; y++)
                {
                    int sourceY = Math.Min(by * 4 + y, height - 1);

                    for (int x = 0; x < 4; x++)
                    {
                        int sourceX = Math.Min(bx * 4 + x, width - 1);

                        int from = (sourceY * width + sourceX) * 4;
                        int to = (y * 4 + x) * 4;
                        pixels[to + 0] = bgra[from + 0];
                        pixels[to + 1] = bgra[from + 1];
                        pixels[to + 2] = bgra[from + 2];
                        pixels[to + 3] = bgra[from + 3];
                    }
                }

                int offset = (by * blocksX + bx) * blockSize;
                reader(pixels, output.AsSpan(offset, blockSize));
            }
        }

        return output;
    }

    static void EncodeBc1Block(ReadOnlySpan<byte> pixels, Span<byte> block)
    {
        bool punchThrough = false;
        for (int i = 0; i < 16; i++)
        {
            if (pixels[i * 4 + 3] < 128)
                punchThrough = true;
        }

        EncodeColorBlock(pixels, block, punchThrough);
    }

    static void EncodeBc2Block(ReadOnlySpan<byte> pixels, Span<byte> block)
    {
        for (int i = 0; i < 8; i++)
        {
            int low = pixels[(i * 2) * 4 + 3] * 15 / 255;
            int high = pixels[(i * 2 + 1) * 4 + 3] * 15 / 255;
            block[i] = (byte)(low | (high << 4));
        }

        EncodeColorBlock(pixels, block[8..], punchThrough: false);
    }

    static void EncodeBc3Block(ReadOnlySpan<byte> pixels, Span<byte> block)
    {
        EncodeAlphaBlock(pixels, block);
        EncodeColorBlock(pixels, block[8..], punchThrough: false);
    }

    static void EncodeAlphaBlock(ReadOnlySpan<byte> pixels, Span<byte> block)
    {
        byte low = 255;
        byte high = 0;

        for (int i = 0; i < 16; i++)
        {
            byte a = pixels[i * 4 + 3];
            if (a < low) low = a;
            if (a > high) high = a;
        }

        block[0] = high;
        block[1] = low;

        Span<byte> palette = stackalloc byte[8];
        palette[0] = high;
        palette[1] = low;

        if (high > low)
        {
            for (int i = 1; i < 7; i++)
                palette[i + 1] = (byte)(((7 - i) * high + i * low) / 7);
        }
        else
        {
            for (int i = 1; i < 5; i++)
                palette[i + 1] = (byte)(((5 - i) * high + i * low) / 5);
            palette[6] = 0;
            palette[7] = 255;
        }

        ulong bits = 0;
        for (int i = 0; i < 16; i++)
        {
            byte a = pixels[i * 4 + 3];
            int best = 0;
            int bestError = int.MaxValue;

            for (int p = 0; p < 8; p++)
            {
                int error = Math.Abs(palette[p] - a);
                if (error < bestError)
                {
                    bestError = error;
                    best = p;
                }
            }

            bits |= (ulong)best << (3 * i);
        }

        for (int i = 0; i < 6; i++)
            block[2 + i] = (byte)(bits >> (8 * i));
    }

    static void EncodeColorBlock(ReadOnlySpan<byte> pixels, Span<byte> block, bool punchThrough)
    {
        Span<byte> low = stackalloc byte[3];
        Span<byte> high = stackalloc byte[3];
        low[0] = low[1] = low[2] = 255;

        int opaque = 0;
        for (int i = 0; i < 16; i++)
        {
            if (punchThrough && pixels[i * 4 + 3] < 128)
                continue;

            opaque++;
            for (int c = 0; c < 3; c++)
            {
                byte value = pixels[i * 4 + c];
                if (value < low[c]) low[c] = value;
                if (value > high[c]) high[c] = value;
            }
        }

        if (opaque == 0)
        {
            low[0] = low[1] = low[2] = 0;
            high[0] = high[1] = high[2] = 0;
        }

        for (int c = 0; c < 3; c++)
        {
            int inset = (high[c] - low[c]) / 16;
            low[c] = (byte)Math.Min(255, low[c] + inset);
            high[c] = (byte)Math.Max(0, high[c] - inset);
        }

        ushort c0 = To565(high);
        ushort c1 = To565(low);

        if (punchThrough)
        {
            if (c0 > c1)
                (c0, c1) = (c1, c0);
        }
        else if (c0 <= c1)
        {
            if (c0 == c1)
            {
                WriteColorBlock(block, c0, c1, 0);
                return;
            }

            (c0, c1) = (c1, c0);
        }

        Span<byte> palette = stackalloc byte[16];
        BuildPalette(palette, c0, c1, fourColor: !punchThrough);

        uint indices = 0;
        for (int i = 0; i < 16; i++)
        {
            if (punchThrough && pixels[i * 4 + 3] < 128)
            {
                indices |= 3u << (2 * i);
                continue;
            }

            int count = punchThrough ? 3 : 4;
            int best = 0;
            int bestError = int.MaxValue;

            for (int p = 0; p < count; p++)
            {
                int error = 0;
                for (int c = 0; c < 3; c++)
                {
                    int diff = palette[p * 4 + c] - pixels[i * 4 + c];
                    error += diff * diff;
                }

                if (error < bestError)
                {
                    bestError = error;
                    best = p;
                }
            }

            indices |= (uint)best << (2 * i);
        }

        WriteColorBlock(block, c0, c1, indices);
    }

    static void BuildPalette(Span<byte> palette, ushort c0, ushort c1, bool fourColor)
    {
        From565(palette, 0, c0);
        From565(palette, 1, c1);

        for (int c = 0; c < 3; c++)
        {
            byte a = palette[c];
            byte b = palette[4 + c];

            if (fourColor)
            {
                palette[8 + c] = (byte)((2 * a + b) / 3);
                palette[12 + c] = (byte)((a + 2 * b) / 3);
            }
            else
            {
                palette[8 + c] = (byte)((a + b) / 2);
                palette[12 + c] = 0;
            }
        }
    }

    static void WriteColorBlock(Span<byte> block, ushort c0, ushort c1, uint indices)
    {
        block[0] = (byte)c0;
        block[1] = (byte)(c0 >> 8);
        block[2] = (byte)c1;
        block[3] = (byte)(c1 >> 8);
        block[4] = (byte)indices;
        block[5] = (byte)(indices >> 8);
        block[6] = (byte)(indices >> 16);
        block[7] = (byte)(indices >> 24);
    }

    static ushort To565(ReadOnlySpan<byte> bgr)
    {
        int b = (bgr[0] * 31 + 127) / 255;
        int g = (bgr[1] * 63 + 127) / 255;
        int r = (bgr[2] * 31 + 127) / 255;
        return (ushort)((r << 11) | (g << 5) | b);
    }

    static void From565(Span<byte> target, int slot, ushort value)
    {
        int r = (value >> 11) & 0x1F;
        int g = (value >> 5) & 0x3F;
        int b = value & 0x1F;

        target[slot * 4 + 0] = (byte)((b << 3) | (b >> 2));
        target[slot * 4 + 1] = (byte)((g << 2) | (g >> 4));
        target[slot * 4 + 2] = (byte)((r << 3) | (r >> 2));
        target[slot * 4 + 3] = 255;
    }
}
