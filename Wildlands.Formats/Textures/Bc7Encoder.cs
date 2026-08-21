using System;

namespace Wildlands.Formats.Textures;

public static class Bc7Encoder
{
    static readonly int[] Weights = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    public static void EncodeBlock(ReadOnlySpan<byte> pixels, Span<byte> block)
    {
        Span<byte> low = stackalloc byte[4];
        Span<byte> high = stackalloc byte[4];
        low[0] = low[1] = low[2] = low[3] = 255;

        for (int i = 0; i < 16; i++)
        {
            for (int c = 0; c < 4; c++)
            {
                byte value = pixels[i * 4 + c];
                if (value < low[c]) low[c] = value;
                if (value > high[c]) high[c] = value;
            }
        }

        Span<float> start = stackalloc float[4];
        Span<float> end = stackalloc float[4];

        for (int c = 0; c < 4; c++)
        {
            start[c] = low[c];
            end[c] = high[c];
        }

        Span<byte> e0 = stackalloc byte[4];
        Span<byte> e1 = stackalloc byte[4];
        Span<byte> palette = stackalloc byte[16 * 4];
        Span<byte> indices = stackalloc byte[16];
        int p0 = 0;
        int p1 = 0;

        for (int pass = 0; pass < 3; pass++)
        {
            p0 = SharedBit(start);
            p1 = SharedBit(end);

            for (int c = 0; c < 4; c++)
            {
                e0[c] = Quantize(start[c], p0);
                e1[c] = Quantize(end[c], p1);
            }

            BuildPalette(palette, e0, e1);

            for (int i = 0; i < 16; i++)
                indices[i] = Nearest(palette, pixels[(i * 4)..]);

            if (pass < 2)
                Refit(pixels, indices, start, end);
        }

        if (indices[0] >= 8)
        {
            for (int c = 0; c < 4; c++)
                (e0[c], e1[c]) = (e1[c], e0[c]);

            (p0, p1) = (p1, p0);

            for (int i = 0; i < 16; i++)
                indices[i] = (byte)(15 - indices[i]);
        }

        Write(block, e0, e1, p0, p1, indices);
    }

    static void Refit(ReadOnlySpan<byte> pixels, ReadOnlySpan<byte> indices, Span<float> start, Span<float> end)
    {
        float aa = 0, ab = 0, bb = 0;

        for (int i = 0; i < 16; i++)
        {
            float b = Weights[indices[i]] / 64f;
            float a = 1f - b;

            aa += a * a;
            ab += a * b;
            bb += b * b;
        }

        float determinant = aa * bb - ab * ab;
        if (Math.Abs(determinant) < 1e-6f)
            return;

        for (int c = 0; c < 4; c++)
        {
            float ap = 0, bp = 0;

            for (int i = 0; i < 16; i++)
            {
                float b = Weights[indices[i]] / 64f;
                float value = pixels[i * 4 + c];

                ap += (1f - b) * value;
                bp += b * value;
            }

            start[c] = Math.Clamp((ap * bb - bp * ab) / determinant, 0f, 255f);
            end[c] = Math.Clamp((bp * aa - ap * ab) / determinant, 0f, 255f);
        }
    }

    static int SharedBit(ReadOnlySpan<float> color)
    {
        int ones = 0;
        for (int c = 0; c < 4; c++)
            ones += (int)MathF.Round(color[c]) & 1;

        return ones >= 2 ? 1 : 0;
    }

    // A mode 6 endpoint is seven bits plus the shared bit below them
    static byte Quantize(float value, int shared)
    {
        int seven = Math.Clamp((int)MathF.Round((value - shared) / 2f), 0, 127);
        return (byte)((seven << 1) | shared);
    }

    static void BuildPalette(Span<byte> palette, ReadOnlySpan<byte> e0, ReadOnlySpan<byte> e1)
    {
        for (int i = 0; i < 16; i++)
        {
            int w = Weights[i];

            for (int c = 0; c < 4; c++)
                palette[i * 4 + c] = (byte)(((64 - w) * e0[c] + w * e1[c] + 32) >> 6);
        }
    }

    static byte Nearest(ReadOnlySpan<byte> palette, ReadOnlySpan<byte> pixel)
    {
        int best = 0;
        int bestError = int.MaxValue;

        for (int i = 0; i < 16; i++)
        {
            int error = 0;
            for (int c = 0; c < 4; c++)
            {
                int diff = palette[i * 4 + c] - pixel[c];
                error += diff * diff;
            }

            if (error < bestError)
            {
                bestError = error;
                best = i;
            }
        }

        return (byte)best;
    }

    static void Write(Span<byte> block, ReadOnlySpan<byte> e0, ReadOnlySpan<byte> e1, int p0, int p1,
        ReadOnlySpan<byte> indices)
    {
        block.Clear();
        int at = 0;

        Put(block, ref at, 1u << 6, 7);

        for (int c = 0; c < 3; c++)
        {
            Put(block, ref at, (uint)(e0[2 - c] >> 1), 7);
            Put(block, ref at, (uint)(e1[2 - c] >> 1), 7);
        }

        Put(block, ref at, (uint)(e0[3] >> 1), 7);
        Put(block, ref at, (uint)(e1[3] >> 1), 7);

        Put(block, ref at, (uint)p0, 1);
        Put(block, ref at, (uint)p1, 1);

        Put(block, ref at, indices[0], 3);
        for (int i = 1; i < 16; i++)
            Put(block, ref at, indices[i], 4);
    }

    static void Put(Span<byte> block, ref int at, uint value, int bits)
    {
        for (int i = 0; i < bits; i++)
        {
            if (((value >> i) & 1) != 0)
                block[(at + i) / 8] |= (byte)(1 << ((at + i) % 8));
        }

        at += bits;
    }
}
