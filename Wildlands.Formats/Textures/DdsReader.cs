using System;
using System.IO;
using System.Text;

namespace Wildlands.Formats.Textures;

public sealed class DdsImage
{
    public PixelFormat Format { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int MipCount { get; init; }
    public byte[] Pixels { get; init; } = [];

    public byte[] Level(int level)
    {
        int width = Width;
        int height = Height;
        int offset = 0;

        for (int i = 0; i < level; i++)
        {
            offset += Format.LevelSize(width, height);
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        int size = Format.LevelSize(width, height);
        if (offset + size > Pixels.Length)
            throw new InvalidDataException($"The dds file stops before mip level {level}.");

        return Pixels.AsSpan(offset, size).ToArray();
    }
}

public static class DdsReader
{
    const uint Magic = 0x20534444;
    const uint FourCcDx10 = 0x30315844;
    const uint FourCcDxt1 = 0x31545844;
    const uint FourCcDxt3 = 0x33545844;
    const uint FourCcDxt5 = 0x35545844;

    const uint HasFourCc = 0x4;
    const uint HasRgb = 0x40;
    const uint HasLuminance = 0x20000;

    public static DdsImage Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a dds file.");

        if (reader.ReadUInt32() != 124)
            throw new InvalidDataException("The dds header has an unexpected size.");

        reader.ReadUInt32();
        int height = (int)reader.ReadUInt32();
        int width = (int)reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();
        int mipCount = Math.Max(1, (int)reader.ReadUInt32());

        for (int i = 0; i < 11; i++)
            reader.ReadUInt32();

        reader.ReadUInt32();
        uint pixelFlags = reader.ReadUInt32();
        uint fourCc = reader.ReadUInt32();
        uint bitCount = reader.ReadUInt32();
        uint redMask = reader.ReadUInt32();
        reader.ReadUInt32();
        uint blueMask = reader.ReadUInt32();
        uint alphaMask = reader.ReadUInt32();

        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();
        reader.ReadUInt32();

        var format = ReadFormat(reader, pixelFlags, fourCc, bitCount, redMask, blueMask, alphaMask);

        int total = 0;
        int levelWidth = width;
        int levelHeight = height;

        for (int i = 0; i < mipCount; i++)
        {
            total += format.LevelSize(levelWidth, levelHeight);
            levelWidth = Math.Max(1, levelWidth / 2);
            levelHeight = Math.Max(1, levelHeight / 2);
        }

        var pixels = reader.ReadBytes(total);
        if (pixels.Length < format.LevelSize(width, height))
            throw new InvalidDataException("The dds file does not even hold its top mip level.");

        return new DdsImage
        {
            Format = format,
            Width = width,
            Height = height,
            MipCount = mipCount,
            Pixels = pixels,
        };
    }

    static PixelFormat ReadFormat(BinaryReader reader, uint pixelFlags, uint fourCc, uint bitCount, uint redMask, uint blueMask, uint alphaMask)
    {
        if ((pixelFlags & HasFourCc) != 0)
        {
            if (fourCc == FourCcDx10)
            {
                uint dxgi = reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt32();

                var format = (PixelFormat)dxgi;
                if (!Enum.IsDefined(format) || format == PixelFormat.Unknown)
                    throw new InvalidDataException($"The dds file uses DXGI format {dxgi}, which this tool does not know.");

                return format;
            }

            return fourCc switch
            {
                FourCcDxt1 => PixelFormat.Bc1,
                FourCcDxt3 => PixelFormat.Bc2,
                FourCcDxt5 => PixelFormat.Bc3,
                _ => throw new InvalidDataException($"The dds file uses the compression '{FourCcName(fourCc)}', which this tool does not know."),
            };
        }

        if ((pixelFlags & HasRgb) != 0 && bitCount == 32)
        {
            bool bgra = blueMask == 0x000000FF && redMask == 0x00FF0000;
            if (!bgra)
                throw new InvalidDataException("Only 32 bit dds files in BGRA channel order can be read.");

            return alphaMask == 0 ? PixelFormat.B8G8R8A8 : PixelFormat.B8G8R8A8;
        }

        if ((pixelFlags & (HasLuminance | HasRgb)) != 0 && bitCount == 8)
            return PixelFormat.R8;

        throw new InvalidDataException("The dds file has a pixel layout this tool does not know.");
    }

    static string FourCcName(uint value)
    {
        var bytes = BitConverter.GetBytes(value);
        var text = new StringBuilder();

        foreach (byte b in bytes)
            text.Append(b >= 32 && b < 127 ? (char)b : '?');

        return text.ToString();
    }
}
