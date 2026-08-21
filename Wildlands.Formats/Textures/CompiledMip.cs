using System;
using System.IO;
using System.Text;

namespace Wildlands.Formats.Textures;

public sealed class CompiledMip
{
    public const uint ClassHash = 0x1D4B87A3;

    public ulong Id { get; private set; }
    public ulong TextureMapId { get; private set; }
    public uint Level { get; private set; }
    public byte[] Pixels { get; private set; } = [];

    public int PixelOffset { get; private set; }

    public static CompiledMip Read(byte[] resource)
    {
        using var stream = new MemoryStream(resource);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        var mip = new CompiledMip();

        mip.Id = reader.ReadUInt64();

        uint hash = reader.ReadUInt32();
        if (hash != ClassHash)
            throw new InvalidDataException($"Not a CompiledMip (class 0x{hash:X8}).");

        reader.ReadByte();
        int length = reader.ReadInt32();
        mip.PixelOffset = (int)stream.Position;
        mip.Pixels = reader.ReadBytes(length);

        reader.ReadByte();
        mip.TextureMapId = reader.ReadUInt64();
        mip.Level = reader.ReadUInt32();

        return mip;
    }
}
