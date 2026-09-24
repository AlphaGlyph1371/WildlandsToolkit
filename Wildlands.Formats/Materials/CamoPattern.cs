using System.IO;
using System.Text;

namespace Wildlands.Formats.Materials;

public sealed class CamoPattern
{
    public const uint ClassHash = 0x59F72F67;

    public ulong Id { get; private set; }
    public ulong TextureId { get; private set; }
    public string DisplayName { get; private set; } = "";

    public static CamoPattern Read(byte[] resource)
    {
        using var stream = new MemoryStream(resource);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        var pattern = new CamoPattern();

        pattern.Id = reader.ReadUInt64();

        uint hash = reader.ReadUInt32();
        if (hash != ClassHash)
            throw new InvalidDataException($"Not a camo pattern (class 0x{hash:X8}).");

        reader.ReadByte();
        reader.ReadUInt64();
        reader.ReadUInt32();
        reader.ReadUInt32();

        reader.ReadByte();
        reader.ReadByte();
        pattern.TextureId = reader.ReadUInt64();

        int length = reader.ReadInt32();
        pattern.DisplayName = Encoding.UTF8.GetString(reader.ReadBytes(length));

        return pattern;
    }
}
