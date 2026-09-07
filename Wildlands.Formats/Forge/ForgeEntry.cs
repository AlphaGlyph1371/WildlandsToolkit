using System.IO;
using System.Text;

namespace Wildlands.Formats.Forge;

public sealed class ForgeEntry
{
    public const int InfoSize = 192;

    public ulong Id { get; set; }
    public long Offset { get; set; }
    public int Length { get; set; }
    public int Index { get; set; }

    public long LocationOffset { get; set; }
    public long InfoOffset { get; set; }

    public string Name { get; set; } = "";
    public uint Extension { get; set; }
    public uint Timestamp { get; set; }
    public ulong UmacHash { get; set; }
    public int EngineVersion { get; set; }
    public int RevisionData { get; set; }
    public int RevisionAttributes { get; set; }
    public int Parent { get; set; }
    public int SccStatusData { get; set; }
    public ulong MetaFileKey { get; set; }
    public int SccStatusAttributes { get; set; }
    public int IsHidden { get; set; }

    public string FileExtension => Id switch
    {
        0 or 16 => ".MetaFile",
        145 => ".PrefetchInfo",
        193 => ".PrefetchingManifest",
        4090 => ".DeletedFilesManifest",
        _ => ".data",
    };

    public void ReadLocation(BinaryReader reader)
    {
        Offset = reader.ReadInt64();
        Id = reader.ReadUInt64();
        Length = reader.ReadInt32();
    }

    public void ReadInfo(BinaryReader reader)
    {
        Length = reader.ReadInt32();
        UmacHash = reader.ReadUInt64();
        EngineVersion = reader.ReadInt32();
        Extension = reader.ReadUInt32();
        RevisionData = reader.ReadInt32();
        RevisionAttributes = reader.ReadInt32();
        reader.ReadInt32();
        reader.ReadInt32();
        Parent = reader.ReadInt32();
        Timestamp = reader.ReadUInt32();
        Name = ReadPaddedName(reader);
        SccStatusData = reader.ReadInt32();
        MetaFileKey = reader.ReadUInt64();
        SccStatusAttributes = reader.ReadInt32();
        IsHidden = reader.ReadInt32();

        if (Name.Length == 0)
            Name = Id.ToString("X16");
    }

    static string ReadPaddedName(BinaryReader reader)
    {
        var bytes = reader.ReadBytes(128);
        int end = Array.IndexOf(bytes, (byte)0);
        if (end < 0)
            end = bytes.Length;

        var name = Encoding.UTF8.GetString(bytes, 0, end);
        return SanitizeFileName(name);
    }

    static string SanitizeFileName(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            if (c >= 32 && Path.GetInvalidFileNameChars().AsSpan().IndexOf(c) < 0)
                builder.Append(c);
        }
        return builder.ToString();
    }
}

public sealed record ForgeEntryAddition(ulong Id, string Name, uint Extension, byte[] InfoTemplate, byte[] Data, byte[] PrefetchBlock);
