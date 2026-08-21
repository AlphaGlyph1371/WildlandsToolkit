using System;
using System.IO;
using System.Text;

namespace Wildlands.Formats.Textures;

public sealed class TextureMap
{
    public const uint ClassHash = 0xA2B7E917;
    const int CubeMapFormat = 2;
    const uint CompiledClassHash = 0x13237FE9;

    public ulong Id { get; private set; }
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public uint Depth { get; private set; }
    public uint MipCount { get; private set; }
    public PixelFormat Format { get; private set; }
    public int TextureFormat { get; private set; }
    public int GammaSettings { get; private set; }
    public int MapType { get; private set; }

    // Mip levels thta are in diffreent resources instead of inside this one (CompiledMap's)
    public ulong[] StreamedMips { get; private set; } = [];

    public byte[] Pixels { get; private set; } = [];

    public int PixelOffset { get; private set; }

    public int CompiledOffset { get; private set; }

    public int CompiledSizeOffset { get; private set; }

    public uint TopMipSize { get; private set; }
    public uint TotalTextureSize { get; private set; }
    public uint Alignment { get; private set; }

    public bool HasPixels => Pixels.Length > 0;

    public static TextureMap Read(byte[] resource)
    {
        using var stream = new MemoryStream(resource);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        var texture = new TextureMap();

        texture.Id = reader.ReadUInt64();

        uint hash = reader.ReadUInt32();
        if (hash != ClassHash)
            throw new InvalidDataException($"Not a TextureMap (class 0x{hash:X8}).");

        reader.ReadBoolean();
        texture.Width = reader.ReadUInt32();
        texture.Height = reader.ReadUInt32();
        texture.Depth = reader.ReadUInt32();
        texture.Format = PixelFormats.FromIndex(reader.ReadInt32());
        texture.TextureFormat = reader.ReadInt32();
        texture.GammaSettings = reader.ReadInt32();
        // Some textures leave the count at zero although they carry a top level
        texture.MipCount = Math.Max(1, reader.ReadUInt32());
        texture.MapType = reader.ReadInt32();

        reader.ReadBoolean(); // dynamic
        reader.ReadBoolean(); // ignore skip mips
        reader.ReadUInt32();  // user category
        reader.ReadUInt32();  // extra field (Wildlands only?)

        int streamed = reader.ReadInt32();
        texture.StreamedMips = new ulong[streamed];
        for (int i = 0; i < streamed; i++)
        {
            reader.ReadByte();
            texture.StreamedMips[i] = reader.ReadUInt64();
        }

        if (reader.ReadByte() != 0)
            throw new InvalidDataException("Texture carries no compiled data.");

        texture.ReadCompiled(reader);
        return texture;
    }

    void ReadCompiled(BinaryReader reader)
    {
        CompiledOffset = (int)reader.BaseStream.Position;
        reader.ReadUInt64();

        uint hash = reader.ReadUInt32();
        if (hash != CompiledClassHash)
            throw new InvalidDataException($"Expected CompiledTextureMap, found 0x{hash:X8}.");

        reader.ReadUInt32(); // platform version
        reader.ReadUInt32(); // sdk version
        CompiledSizeOffset = (int)reader.BaseStream.Position;
        reader.ReadUInt32(); // width, same as above
        reader.ReadUInt32(); // height
        reader.ReadUInt32(); // depth
        reader.ReadUInt32(); // mip count
        reader.ReadInt32();  // pixel format
        reader.ReadInt32();  // texture format
        reader.ReadInt32();  // gamma
        TopMipSize = reader.ReadUInt32();
        TotalTextureSize = reader.ReadUInt32();
        Alignment = reader.ReadUInt32();

        int length = reader.ReadInt32();
        PixelOffset = (int)reader.BaseStream.Position;
        Pixels = reader.ReadBytes(length);
    }

    public (int Width, int Height) SizeOfLevel(int level)
    {
        int width = (int)Width;
        int height = (int)Height;

        for (int i = 0; i < level; i++)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        return (width, height);
    }

    public int FirstStoredLevel()
    {
        for (int level = 0; level < MipCount; level++)
        {
            if (ChainSize(level) <= Pixels.Length)
                return level;
        }

        return -1;
    }
    // A cube map stores six faces one after another
    public int Faces => TextureFormat == CubeMapFormat ? 6 : 1;


    int ChainSize(int level)
    {
        var (width, height) = SizeOfLevel(level);
        int total = 0;

        for (int i = level; i < MipCount; i++)
        {
            total += Format.LevelSize(width, height);
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
        }

        return total * Faces;
    }
}
