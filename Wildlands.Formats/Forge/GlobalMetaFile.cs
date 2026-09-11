using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Wildlands.Formats.Forge;

public enum GlobalMetaFieldKind
{
    Number = 0,
    Text = 1,
    Block = 2,
    LongNumber = 4,
    Table = 6,
}

public sealed class GlobalMetaField
{
    public GlobalMetaField(uint tag, GlobalMetaFieldKind kind, ulong number, string text, byte[] block)
    {
        Tag = tag;
        Kind = kind;
        Number = number;
        Text = text;
        Block = block;
    }

    public uint Tag { get; }
    public GlobalMetaFieldKind Kind { get; }
    public ulong Number { get; set; }
    public string Text { get; set; }
    public byte[] Block { get; set; }
}

public sealed class GlobalMetaFileAsset
{
    public List<GlobalMetaField> Fields { get; } = [];
    public uint Trailer { get; internal set; }

    public GlobalMetaField? Find(uint tag) => Fields.FirstOrDefault(field => field.Tag == tag);
}

public static class GlobalMetaFile
{
    public const ulong EntryId = 16;
    public const uint IdentityTag = 19;

    public static GlobalMetaFileAsset Read(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 4)
            throw new InvalidDataException("GlobalMetaFile is truncated.");

        var asset = new GlobalMetaFileAsset();
        int limit = data.Length - 4;
        int offset = 0;
        while (offset + 8 <= limit)
        {
            uint tag = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            uint kind = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            offset += 8;
            switch ((GlobalMetaFieldKind)kind)
            {
                case GlobalMetaFieldKind.Number:
                    asset.Fields.Add(new GlobalMetaField(tag, GlobalMetaFieldKind.Number,
                        ReadUInt32(data, ref offset), "", []));
                    break;
                case GlobalMetaFieldKind.LongNumber:
                    ulong value = ReadUInt64(data, ref offset);
                    if (ReadByte(data, ref offset) != 0)
                        throw new InvalidDataException($"GlobalMetaFile field 0x{tag:X} has a non-zero long-number terminator.");
                    asset.Fields.Add(new GlobalMetaField(tag, GlobalMetaFieldKind.LongNumber, value, "", []));
                    break;
                case GlobalMetaFieldKind.Text:
                    byte[] text = ReadBlock(data, ref offset);
                    if (ReadByte(data, ref offset) != 0)
                        throw new InvalidDataException($"GlobalMetaFile field 0x{tag:X} is not a terminated string.");
                    asset.Fields.Add(new GlobalMetaField(tag, GlobalMetaFieldKind.Text, 0,
                        Encoding.Latin1.GetString(text), []));
                    break;
                case GlobalMetaFieldKind.Block:
                case GlobalMetaFieldKind.Table:
                    asset.Fields.Add(new GlobalMetaField(tag, (GlobalMetaFieldKind)kind, 0, "",
                        ReadBlock(data, ref offset)));
                    break;
                default:
                    throw new InvalidDataException($"GlobalMetaFile field 0x{tag:X} has unknown kind {kind}.");
            }
        }

        if (offset != limit)
            throw new InvalidDataException($"GlobalMetaFile has {limit - offset} unexplained byte(s) at 0x{offset:X}.");
        asset.Trailer = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(limit));
        return asset;
    }

    public static byte[] Write(GlobalMetaFileAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Latin1, leaveOpen: true);
        foreach (GlobalMetaField field in asset.Fields)
        {
            writer.Write(field.Tag);
            writer.Write((uint)field.Kind);
            switch (field.Kind)
            {
                case GlobalMetaFieldKind.Number:
                    writer.Write((uint)field.Number);
                    break;
                case GlobalMetaFieldKind.LongNumber:
                    writer.Write(field.Number);
                    writer.Write((byte)0);
                    break;
                case GlobalMetaFieldKind.Text:
                    byte[] text = Encoding.Latin1.GetBytes(field.Text);
                    writer.Write(text.Length);
                    writer.Write(text);
                    writer.Write((byte)0);
                    break;
                default:
                    writer.Write(field.Block.Length);
                    writer.Write(field.Block);
                    break;
            }
        }

        writer.Write(asset.Trailer);
        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] CreatePatchMetaFile(byte[] template, string identity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        GlobalMetaFileAsset asset = Read(template);
        GlobalMetaField field = asset.Find(IdentityTag)
            ?? throw new InvalidDataException("The template GlobalMetaFile carries no identity field.");
        if (field.Kind != GlobalMetaFieldKind.Text || field.Text.Length != identity.Length)
            throw new InvalidDataException("The template GlobalMetaFile identity is not a string of the expected length.");
        field.Text = identity;
        byte[] written = Write(asset);
        if (written.Length != template.Length)
            throw new InvalidDataException("The rewritten GlobalMetaFile changed length.");
        return written;
    }

    static byte[] ReadBlock(byte[] data, ref int offset)
    {
        int length = checked((int)ReadUInt32(data, ref offset));
        if (length < 0 || offset + length > data.Length)
            throw new InvalidDataException("A GlobalMetaFile block is truncated.");
        byte[] block = data.AsSpan(offset, length).ToArray();
        offset += length;
        return block;
    }

    static uint ReadUInt32(byte[] data, ref int offset)
    {
        if (offset + sizeof(uint) > data.Length)
            throw new InvalidDataException("GlobalMetaFile is truncated.");
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        offset += sizeof(uint);
        return value;
    }

    static ulong ReadUInt64(byte[] data, ref int offset)
    {
        if (offset + sizeof(ulong) > data.Length)
            throw new InvalidDataException("GlobalMetaFile is truncated.");
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
        offset += sizeof(ulong);
        return value;
    }

    static byte ReadByte(byte[] data, ref int offset)
    {
        if (offset >= data.Length)
            throw new InvalidDataException("GlobalMetaFile is truncated.");
        return data[offset++];
    }
}
