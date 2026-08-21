using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Wildlands.Formats.Textures;

public static class DdsWriter
{
    const uint Magic = 0x20534444;      // "DDS "
    const uint HeaderSize = 124;
    const uint PixelFormatSize = 32;
    const uint FourCcDx10 = 0x30315844; // "DX10"

    // Header flags
    const uint HasCaps = 0x1;
    const uint HasHeight = 0x2;
    const uint HasWidth = 0x4;
    const uint HasPitch = 0x8;
    const uint HasPixelFormat = 0x1000;
    const uint HasMipCount = 0x20000;
    const uint HasLinearSize = 0x80000;

    // Caps flags
    const uint IsComplex = 0x8;
    const uint IsTexture = 0x1000;
    const uint HasMipMap = 0x400000;

    public static void Write(Stream stream, PixelFormat format, IReadOnlyList<TextureMipLevel> levels)
    {
        if (levels.Count == 0)
            throw new ArgumentException("A dds file needs at least one mip level.", nameof(levels));

        var top = levels[0];
        bool compressed = format.IsBlockCompressed();

        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        writer.Write(Magic);
        writer.Write(HeaderSize);

        uint flags = HasCaps | HasHeight | HasWidth | HasPixelFormat
            | (compressed ? HasLinearSize : HasPitch);

        if (levels.Count > 1)
            flags |= HasMipCount;

        writer.Write(flags);
        writer.Write((uint)top.Height);
        writer.Write((uint)top.Width);

        writer.Write(compressed ? (uint)format.LevelSize(top.Width, top.Height) : (uint)(top.Width * format.BitsPerPixel() / 8));

        writer.Write(0u);                   // depth
        writer.Write((uint)levels.Count);

        for (int i = 0; i < 11; i++)
            writer.Write(0u);               // reserved

        writer.Write(PixelFormatSize);
        writer.Write(0x4u);                 // DDPF_FOURCC
        writer.Write(FourCcDx10);
        writer.Write(0u);                   // bit count
        writer.Write(0u);                   // red mask
        writer.Write(0u);                   // green mask
        writer.Write(0u);                   // blue mask
        writer.Write(0u);                   // alpha mask

        uint caps = IsTexture;
        if (levels.Count > 1)
            caps |= IsComplex | HasMipMap;

        writer.Write(caps);
        writer.Write(0u);                   // caps2
        writer.Write(0u);                   // caps3
        writer.Write(0u);                   // caps4
        writer.Write(0u);                   // reserved

        writer.Write((uint)format);         // dxgi format
        writer.Write(3u);                   // resource dimension: 2d texture
        writer.Write(0u);                   // misc flags
        writer.Write(1u);                   // array size
        writer.Write(0u);                   // alpha mode: unknown

        foreach (var level in levels)
        {
            int size = format.LevelSize(level.Width, level.Height);
            if (level.Pixels.Length < size)
                throw new InvalidDataException($"Mip level {level.Level} is shorter than its format needs.");

            writer.Write(level.Pixels, 0, size);
        }
    }
}
