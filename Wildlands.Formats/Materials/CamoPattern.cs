using System.IO;
using System.Text;

namespace Wildlands.Formats.Materials;

// One camouflage pattern: the texture it paints on and the name the game shows
// for it. The class has no known name, only its hash - what it holds was read
// off the bytes, and the display name inside it is what makes it recognisable
// ("US 3-Color Desert", "MultiCam Alpine").
//
// A material does not point at one of these. Its Camo parameter holds a
// TextureSelector whose references are empty in every material in the game. What
// a material does carry is a CamoPattern parameter, and that one names a texture
// outright - the default the model wears. Which pattern replaces it is decided
// by number: a camo option is called "37-AtacsAT-X" and its texture
// "37-A-TacsAT-X_DiffuseMap". "wlcli camo" matches the two lists up.
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
        reader.ReadUInt32(); // an inline object, 0xB332698E, whose body is four bytes
        reader.ReadUInt32();

        reader.ReadByte();
        reader.ReadByte();
        pattern.TextureId = reader.ReadUInt64();

        int length = reader.ReadInt32();
        pattern.DisplayName = Encoding.UTF8.GetString(reader.ReadBytes(length));

        return pattern;
    }
}
