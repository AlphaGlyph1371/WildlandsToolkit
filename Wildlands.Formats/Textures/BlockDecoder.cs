using System;

namespace Wildlands.Formats.Textures;

// Decodes compressed blocks into BGRA32

public static class BlockDecoder
{
    public static byte[] Decode(ReadOnlySpan<byte> data, PixelFormat format, int width, int height)
    {
        var output = new byte[width * height * 4];

        switch (format)
        {
            case PixelFormat.Bc1:
                DecodeBlocks(data, output, width, height, 8, DecodeBc1Block);
                break;
            case PixelFormat.Bc2:
                DecodeBlocks(data, output, width, height, 16, DecodeBc2Block);
                break;
            case PixelFormat.Bc3:
                DecodeBlocks(data, output, width, height, 16, DecodeBc3Block);
                break;
            case PixelFormat.Bc7:
                DecodeBlocks(data, output, width, height, 16, Bc7.DecodeBlock);
                break;
            case PixelFormat.B8G8R8A8:
                data[..Math.Min(data.Length, output.Length)].CopyTo(output);
                break;
            case PixelFormat.R8G8B8A8Signed:
                // Signed channels run from -128 to 127 -> the middle is grey
                for (int i = 0; i < width * height && (i + 1) * 4 <= data.Length; i++)
                {
                    output[i * 4 + 0] = (byte)((sbyte)data[i * 4 + 2] + 128);
                    output[i * 4 + 1] = (byte)((sbyte)data[i * 4 + 1] + 128);
                    output[i * 4 + 2] = (byte)((sbyte)data[i * 4 + 0] + 128);
                    output[i * 4 + 3] = (byte)((sbyte)data[i * 4 + 3] + 128);
                }
                break;
            case PixelFormat.R8:
                for (int i = 0; i < width * height && i < data.Length; i++)
                {
                    output[i * 4 + 0] = data[i];
                    output[i * 4 + 1] = data[i];
                    output[i * 4 + 2] = data[i];
                    output[i * 4 + 3] = 255;
                }
                break;
            case PixelFormat.R16G16B16A16Float:
                for (int i = 0; i < width * height && (i + 1) * 8 <= data.Length; i++)
                {
                    output[i * 4 + 0] = FromHalf(data, i * 8 + 4);
                    output[i * 4 + 1] = FromHalf(data, i * 8 + 2);
                    output[i * 4 + 2] = FromHalf(data, i * 8 + 0);
                    output[i * 4 + 3] = FromHalf(data, i * 8 + 6);
                }
                break;
            default:
                throw new NotSupportedException($"Cannot decode {format} yet.");
        }

        return output;
    }

    public static bool CanDecode(PixelFormat format) => format switch
    {
        PixelFormat.Bc1 or PixelFormat.Bc2 or PixelFormat.Bc3 or PixelFormat.Bc7
            or PixelFormat.B8G8R8A8 or PixelFormat.R8G8B8A8Signed or PixelFormat.R8
            or PixelFormat.R16G16B16A16Float => true,
        _ => false,
    };

    static byte FromHalf(ReadOnlySpan<byte> data, int offset)
    {
        float value = (float)BitConverter.ToHalf(data[offset..]);
        return (byte)Math.Clamp(value * 255f, 0f, 255f);
    }

    delegate void BlockWriter(ReadOnlySpan<byte> block, Span<byte> pixels);

    static void DecodeBlocks(ReadOnlySpan<byte> data, byte[] output, int width, int height,
        int blockSize, BlockWriter writer)
    {
        int blocksX = Math.Max(1, (width + 3) / 4);
        int blocksY = Math.Max(1, (height + 3) / 4);
        Span<byte> pixels = stackalloc byte[16 * 4];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                int offset = (by * blocksX + bx) * blockSize;
                if (offset + blockSize > data.Length)
                    return;

                pixels.Clear();
                writer(data.Slice(offset, blockSize), pixels);

                for (int y = 0; y < 4; y++)
                {
                    int targetY = by * 4 + y;
                    if (targetY >= height)
                        break;

                    for (int x = 0; x < 4; x++)
                    {
                        int targetX = bx * 4 + x;
                        if (targetX >= width)
                            break;

                        int from = (y * 4 + x) * 4;
                        int to = (targetY * width + targetX) * 4;
                        output[to + 0] = pixels[from + 0];
                        output[to + 1] = pixels[from + 1];
                        output[to + 2] = pixels[from + 2];
                        output[to + 3] = pixels[from + 3];
                    }
                }
            }
        }
    }

    static void DecodeBc1Block(ReadOnlySpan<byte> block, Span<byte> pixels)
    {
        DecodeColorBlock(block, pixels, allowTransparency: true);
    }

    static void DecodeBc2Block(ReadOnlySpan<byte> block, Span<byte> pixels)
    {
        DecodeColorBlock(block[8..], pixels, allowTransparency: false);

        for (int i = 0; i < 16; i++)
        {
            int nibble = (block[i / 2] >> ((i % 2) * 4)) & 0xF;
            pixels[i * 4 + 3] = (byte)(nibble * 17);
        }
    }

    static void DecodeBc3Block(ReadOnlySpan<byte> block, Span<byte> pixels)
    {
        DecodeColorBlock(block[8..], pixels, allowTransparency: false);

        Span<byte> alpha = stackalloc byte[8];
        alpha[0] = block[0];
        alpha[1] = block[1];

        if (alpha[0] > alpha[1])
        {
            for (int i = 1; i < 7; i++)
                alpha[i + 1] = (byte)(((7 - i) * alpha[0] + i * alpha[1]) / 7);
        }
        else
        {
            for (int i = 1; i < 5; i++)
                alpha[i + 1] = (byte)(((5 - i) * alpha[0] + i * alpha[1]) / 5);
            alpha[6] = 0;
            alpha[7] = 255;
        }

        ulong bits = 0;
        for (int i = 0; i < 6; i++)
            bits |= (ulong)block[2 + i] << (8 * i);

        for (int i = 0; i < 16; i++)
            pixels[i * 4 + 3] = alpha[(int)((bits >> (3 * i)) & 7)];
    }

    static void DecodeColorBlock(ReadOnlySpan<byte> block, Span<byte> pixels, bool allowTransparency)
    {
        ushort c0 = (ushort)(block[0] | (block[1] << 8));
        ushort c1 = (ushort)(block[2] | (block[3] << 8));

        Span<byte> colors = stackalloc byte[16];
        WriteRgb565(colors, 0, c0);
        WriteRgb565(colors, 1, c1);

        bool fourColor = c0 > c1 || !allowTransparency;

        for (int channel = 0; channel < 3; channel++)
        {
            byte a = colors[channel];
            byte b = colors[4 + channel];

            if (fourColor)
            {
                colors[8 + channel] = (byte)((2 * a + b) / 3);
                colors[12 + channel] = (byte)((a + 2 * b) / 3);
            }
            else
            {
                colors[8 + channel] = (byte)((a + b) / 2);
                colors[12 + channel] = 0;
            }
        }

        colors[3] = colors[7] = colors[11] = 255;
        colors[15] = (byte)(fourColor ? 255 : 0);

        uint indices = (uint)(block[4] | (block[5] << 8) | (block[6] << 16) | (block[7] << 24));

        for (int i = 0; i < 16; i++)
        {
            int index = (int)((indices >> (2 * i)) & 3);
            pixels[i * 4 + 0] = colors[index * 4 + 0];
            pixels[i * 4 + 1] = colors[index * 4 + 1];
            pixels[i * 4 + 2] = colors[index * 4 + 2];
            pixels[i * 4 + 3] = colors[index * 4 + 3];
        }
    }

    // Writes one RGB565 colour as BGRA at slot * 4
    static void WriteRgb565(Span<byte> target, int slot, ushort value)
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
