using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Wildlands.Formats.Materials;

public sealed record TextureSlot(string Name, ulong Id);

public sealed class TextureSet
{
    public const uint ClassHash = 0xD70E6670;

    // The order is the format; only their position tells the slots apart
    static readonly string[] SlotNames =
    [
        "Diffuse", "Normal", "Specular", "OffsetBump", "Emissive", "Transmission",
        "Occlusion", "Mask1", "Mask2", "Cookie", "EnvLighting", "Generic",
        "Diffuse1", "Diffuse2", "Diffuse3", "Diffuse4", "Diffuse5", "VectorDisplace",
    ];

    public ulong Id { get; private set; }

    public ulong Source { get; private set; }

    public List<TextureSlot> Textures { get; } = [];

    public ulong Find(string slot)
    {
        foreach (var texture in Textures)
        {
            if (texture.Name == slot)
                return texture.Id;
        }

        return 0;
    }

    public static TextureSet Read(byte[] resource)
    {
        using var stream = new MemoryStream(resource);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        var set = new TextureSet();

        set.Id = reader.ReadUInt64();

        uint hash = reader.ReadUInt32();
        if (hash != ClassHash)
            throw new InvalidDataException($"Not a TextureSet (class 0x{hash:X8}).");

        reader.ReadByte();

        foreach (string name in SlotNames)
        {
            reader.ReadByte();
            reader.ReadByte();
            ulong id = reader.ReadUInt64();

            if (id != 0)
                set.Textures.Add(new TextureSlot(name, id));
        }

        reader.ReadByte();
        set.Source = reader.ReadUInt64();

        return set;
    }
}
