using System;
using System.IO;
using System.Numerics;

namespace Wildlands.Formats.Materials;

public sealed class MaterialFlags
{
    public const int Count = 21;

    readonly bool[] _values;

    public MaterialFlags(bool[] values) => _values = values;

    public bool this[int index] => _values[index];

    public bool TwoSided => _values[17];
}

public sealed record MaterialParameter(uint Name, uint Kind, uint ObjectClass, ulong TextureSetId = 0, ulong TextureId = 0, Vector4 Value = default, UvTransform Transform = default);

public sealed class Material
{
    public const uint ClassHash = 0x85C817C3;
    public const uint TextureSelectorHash = 0x7D08460D;
    public const uint UvTransformHash = 0xC52E2125;

    public ulong Id { get; private set; }
    public ulong TemplateId { get; private set; }
    public ulong TextureSetId { get; private set; }
    public ulong BackfaceMaterialId { get; private set; }

    public int BlendMode { get; private set; }
    public int AlphaDisplayMode { get; private set; }

    public bool IsOpaque => BlendMode == 0;
    public byte AlphaTestValue { get; private set; }
    public MaterialFlags Flags { get; private set; } = new(new bool[MaterialFlags.Count]);
    public UInt128 FlagMask { get; private set; }

    public List<MaterialParameter> Parameters { get; } = [];

    public string Note { get; private set; } = "";

    public long TrailingBytes { get; private set; }

    public static Material Read(byte[] resource)
    {
        using var stream = new MemoryStream(resource);
        using var reader = new BinaryReader(stream);

        var material = new Material();

        material.Id = ReadHeader(reader, ClassHash);

        reader.ReadByte();
        material.TemplateId = ReadReference(reader);
        material.TextureSetId = ReadReference(reader);

        ReadHeader(reader, MaskHash);
        reader.ReadByte();

        material.BlendMode = reader.ReadInt32();
        material.AlphaDisplayMode = reader.ReadInt32();
        material.AlphaTestValue = reader.ReadByte();
        material.Flags = ReadFlags(reader);

        material.BackfaceMaterialId = ReadReference(reader);

        ReadHeader(reader, FlagMaskHash);
        material.FlagMask = ReadFlagMask(reader);
        reader.ReadInt32();

        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var parameter = ReadParameter(reader, out string note);
            if (note.Length > 0)
            {
                material.Note = note;
                break;
            }

            material.Parameters.Add(parameter!);
        }

        material.TrailingBytes = stream.Length - stream.Position;
        return material;
    }

    static MaterialParameter? ReadParameter(BinaryReader reader, out string note)
    {
        note = "";

        uint name = reader.ReadUInt32();
        uint dataType = reader.ReadUInt32();
        uint type = reader.ReadUInt32();
        reader.ReadUInt32();

        uint? kind = KindOf(type) ?? KindOf(dataType);
        if (kind is null)
        {
            note = $"parameter {name:X8} has no readable type";
            return null;
        }

        if (kind != ObjectKind)
        {
            int size = FixedSize(kind.Value);
            if (size < 0)
            {
                note = $"value kind 0x{kind:X2} has no known length";
                return null;
            }

            long at = reader.BaseStream.Position;
            var value = kind == FloatKind ? new Vector4(reader.ReadSingle(), 0, 0, 0)
                : kind == VectorKind ? new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())
                : Vector4.Zero;

            reader.BaseStream.Position = at + size;
            return new MaterialParameter(name, kind.Value, 0, Value: value);
        }

        long start = reader.BaseStream.Position;
        reader.ReadUInt64();
        uint objectClass = reader.ReadUInt32();

        if (objectClass == TextureSelectorHash)
            return ReadTextureSelector(reader, start, name, kind.Value, out note);

        if (objectClass == UvTransformHash)
        {
            var transform = ReadUvTransform(reader);
            reader.BaseStream.Position = start + UvTransformSize;
            return new MaterialParameter(name, kind.Value, objectClass, Transform: transform);
        }

        int objectSize = ObjectSize(objectClass);
        if (objectSize < 0)
        {
            note = $"object class 0x{objectClass:X8} has no known length";
            return null;
        }

        reader.BaseStream.Position = start + objectSize;
        return new MaterialParameter(name, kind.Value, objectClass);
    }

    static MaterialParameter? ReadTextureSelector(BinaryReader reader, long start, uint name, uint kind, out string note)
    {
        note = "";
        reader.BaseStream.Position = start + TextureSelectorReferences;

        ulong setId = ReadReference(reader);
        ulong textureId = ReadReference(reader);

        int names = reader.ReadInt32();
        for (int i = 0; i < names; i++)
        {
            int length = reader.ReadInt32();
            if (length < 0 || reader.BaseStream.Position + length + 1 > reader.BaseStream.Length)
            {
                note = $"texture selector claims a {length} byte name";
                return null;
            }

            reader.BaseStream.Position += length + 1;
        }

        reader.ReadUInt32();
        return new MaterialParameter(name, kind, TextureSelectorHash, setId, textureId);
    }

    public static UvTransform ReadUvTransform(BinaryReader reader)
    {
        var values = new float[8];
        for (int i = 0; i < values.Length; i++)
            values[i] = reader.ReadSingle();

        return new UvTransform(values[0], values[1], values[2], values[3], values[6]);
    }

    const uint FloatKind = 0x0A;
    const uint VectorKind = 0x0D;
    const uint ObjectKind = 0x13;
    const int TextureSelectorReferences = 25;

    const int UvTransformSize = 46;

    static uint? KindOf(uint value) => (value & 0xFFFF) == 0 && (value >> 16) <= 0x1C ? value >> 16 : null;

    // Only three of these ever turn up in the game: 0x0A float, 0x0D vector and 0x07
    static int FixedSize(uint kind) => kind switch
    {
        0x00 or 0x01 or 0x02 or 0x03 => 1,
        0x04 or 0x05 => 2,
        0x06 or 0x07 or 0x0A => 4,
        0x08 or 0x09 or 0x0B or 0x11 => 8,
        0x0C => 12,
        0x0D or 0x0E => 16,
        0x0F => 36,
        0x10 => 64,
        _ => -1,
    };

    static int ObjectSize(uint objectClass) => objectClass switch
    {
        0xECE5D96C => 36,
        _ => -1,
    };

    const uint MaskHash = 0xDF5D6C0E;
    const uint FlagMaskHash = 0x92B95F74;

    public static UInt128 ReadFlagMask(BinaryReader reader)
    {
        ulong low = reader.ReadUInt64();
        ulong high = reader.ReadUInt64();
        return new UInt128(high, low);
    }

    static ulong ReadHeader(BinaryReader reader, uint expected)
    {
        ulong id = reader.ReadUInt64();
        uint hash = reader.ReadUInt32();

        if (hash != expected)
            throw new InvalidDataException($"Expected class 0x{expected:X8}, found 0x{hash:X8}.");

        return id;
    }

    static ulong ReadReference(BinaryReader reader)
    {
        reader.ReadByte();
        reader.ReadByte();
        return reader.ReadUInt64();
    }

    static MaterialFlags ReadFlags(BinaryReader reader)
    {
        var values = new bool[MaterialFlags.Count];
        for (int i = 0; i < values.Length; i++)
            values[i] = reader.ReadBoolean();

        return new MaterialFlags(values);
    }
}
