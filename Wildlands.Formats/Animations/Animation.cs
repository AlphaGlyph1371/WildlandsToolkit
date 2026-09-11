using System.IO;
using Wildlands.Formats.Data;

namespace Wildlands.Formats.Models;

public sealed class AnimationObject
{
    public ulong Id { get; init; }
    public uint ClassHash { get; init; }
    public byte[] Payload { get; init; } = [];
}

public sealed class AnimationTrack
{
    public uint Bone { get; init; }
    public byte Tag { get; init; }
    public uint Size { get; init; }
    public int KeyCount { get; init; }
    public byte Mode { get; init; }
    public byte[] Times { get; init; } = [];
    public byte[] Values { get; init; } = [];

    public int ValueFormat => (Tag - Animation.FirstTag) / 3;
    public int TimeFormat => (Tag - Animation.FirstTag) % 3;
    public int Stride => Animation.StrideOf(ValueFormat);
}

public sealed class AnimationAsset
{
    public ulong Id { get; init; }
    public byte Version { get; init; }
    public float Duration { get; init; }
    public uint BoneSet { get; init; }
    public byte[] HeaderTail { get; init; } = [];
    public byte[] TailHeader { get; init; } = [];
    public List<AnimationObject> Objects { get; } = [];
    public List<AnimationTrack> Tracks { get; } = [];
}

public static class Animation
{
    public const uint ClassHash = 0x0FA3067F;
    public const uint TrackClassHash = 0x653CAA76;
    public const int FirstTag = 9;
    public const int HeaderSize = 34;

    const int HeaderTailSize = 13;
    const int TailHeaderSize = 6;
    const int MaximumKeys = 1 << 20;

    public static int StrideOf(int valueFormat) => valueFormat switch
    {
        0 => 4,
        1 => 6,
        2 => 8,
        4 => 2,
        5 => 12,
        6 => 12,
        8 => 6,
        9 => 4,
        10 => 1,
        11 => 2,
        13 => 1,
        15 => 4,
        16 => 4,
        _ => throw new NotSupportedException($"Animation value format {valueFormat} is unknown."),
    };

    public static AnimationAsset Read(byte[] resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        using var stream = new MemoryStream(resource, writable: false);
        using var reader = new BinaryReader(stream);
        var header = ScimitarHeader.Read(reader, ClassHash);
        byte version = ReadByte(reader, "version");
        float duration = ReadSingle(reader, "duration");
        uint boneSet = ReadUInt32(reader, "bone set");
        byte[] headerTail = ReadBytes(reader, HeaderTailSize, "header tail");

        var objects = AnvilObjects.ScanSequence(resource, HeaderSize);
        if (objects.Count == 0)
            throw new InvalidDataException("The Animation holds no objects.");
        if (objects[^1].ClassHash != TrackClassHash)
            throw new InvalidDataException($"The last Animation object is class 0x{objects[^1].ClassHash:X8} "
                + "instead of a track.");

        int tailStart = objects[^1].Offset + 16;
        var bones = new List<uint>();
        var parsed = new List<AnimationObject>(objects.Count);
        for (int i = 0; i < objects.Count; i++)
        {
            int payload = (i + 1 < objects.Count ? objects[i + 1].Offset : tailStart) - objects[i].Offset - 12;
            if (payload < 0 || objects[i].Offset + 12 + payload > resource.Length)
                throw new InvalidDataException($"Animation object {i} does not fit the resource.");
            parsed.Add(new AnimationObject
            {
                Id = objects[i].Id,
                ClassHash = objects[i].ClassHash,
                Payload = resource.AsSpan(objects[i].Offset + 12, payload).ToArray(),
            });

            if (objects[i].ClassHash == TrackClassHash)
                bones.Add(BitConverter.ToUInt32(resource, objects[i].Offset + 12));
        }

        stream.Position = tailStart;
        byte[] tailHeader = ReadBytes(reader, TailHeaderSize, "tail header");
        int tracks = ReadCount(reader, "track", bones.Count);
        if (tracks != bones.Count)
            throw new InvalidDataException($"The Animation announces {tracks} track(s) but carries "
                + $"{bones.Count} track object(s).");

        var asset = new AnimationAsset
        {
            Id = header.Id,
            Version = version,
            Duration = duration,
            BoneSet = boneSet,
            HeaderTail = headerTail,
            TailHeader = tailHeader,
        };
        asset.Objects.AddRange(parsed);
        for (int i = 0; i < tracks; i++)
            asset.Tracks.Add(ReadTrack(reader, bones[i], i));

        if (stream.Position != resource.Length)
            throw new InvalidDataException($"The Animation has {resource.Length - stream.Position} "
                + "byte(s) left after its last track.");
        return asset;
    }

    public static byte[] Write(AnimationAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.HeaderTail.Length != HeaderTailSize)
            throw new InvalidDataException($"The Animation header tail holds {asset.HeaderTail.Length} "
                + $"byte(s) instead of {HeaderTailSize}.");
        if (asset.TailHeader.Length != TailHeaderSize)
            throw new InvalidDataException($"The Animation tail header holds {asset.TailHeader.Length} "
                + $"byte(s) instead of {TailHeaderSize}.");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(asset.Id);
        writer.Write(ClassHash);
        writer.Write(asset.Version);
        writer.Write(asset.Duration);
        writer.Write(asset.BoneSet);
        writer.Write(asset.HeaderTail);

        foreach (AnimationObject item in asset.Objects)
        {
            writer.Write(item.Id);
            writer.Write(item.ClassHash);
            writer.Write(item.Payload);
        }

        writer.Write(asset.TailHeader);
        writer.Write(asset.Tracks.Count);
        foreach (AnimationTrack track in asset.Tracks)
        {
            writer.Write(track.Tag);
            writer.Write(track.Size);
            writer.Write(track.KeyCount);
            if (track.TimeFormat == 2)
                writer.Write(track.Mode);
            writer.Write(track.Times);
            writer.Write(track.Values);
        }

        return stream.ToArray();
    }

    static AnimationTrack ReadTrack(BinaryReader reader, uint bone, int index)
    {
        byte tag = ReadByte(reader, $"track {index} tag");
        if (tag < FirstTag)
            throw new InvalidDataException($"Track {index} uses tag 0x{tag:X2}.");
        uint size = ReadUInt32(reader, $"track {index} size");
        int keys = ReadCount(reader, $"track {index} key", MaximumKeys);
        if (keys == 0)
            throw new InvalidDataException($"Track {index} holds no keys.");

        int format = (tag - FirstTag) % 3;
        byte mode = format == 2 ? ReadByte(reader, $"track {index} time mode") : (byte)0;
        int times = format switch
        {
            0 => keys - 1,
            1 => 2 * (keys - 1),
            _ => mode & 0x0F,
        };

        return new AnimationTrack
        {
            Bone = bone,
            Tag = tag,
            Size = size,
            KeyCount = keys,
            Mode = mode,
            Times = ReadBytes(reader, times, $"track {index} times"),
            Values = ReadBytes(reader, keys * StrideOf((tag - FirstTag) / 3), $"track {index} values"),
        };
    }

    static int ReadCount(BinaryReader reader, string field, int maximum)
    {
        int value = ReadInt32(reader, field + " count");
        if (value < 0 || value > maximum)
            throw new InvalidDataException($"Invalid {field} count {value}.");
        return value;
    }

    static byte ReadByte(BinaryReader reader, string field)
    {
        Ensure(reader, 1, field);
        return reader.ReadByte();
    }

    static int ReadInt32(BinaryReader reader, string field)
    {
        Ensure(reader, 4, field);
        return reader.ReadInt32();
    }

    static uint ReadUInt32(BinaryReader reader, string field)
    {
        Ensure(reader, 4, field);
        return reader.ReadUInt32();
    }

    static float ReadSingle(BinaryReader reader, string field)
    {
        Ensure(reader, 4, field);
        return reader.ReadSingle();
    }

    static byte[] ReadBytes(BinaryReader reader, int count, string field)
    {
        Ensure(reader, count, field);
        return reader.ReadBytes(count);
    }

    static void Ensure(BinaryReader reader, int count, string field)
    {
        if (count < 0 || reader.BaseStream.Position > reader.BaseStream.Length - count)
            throw new EndOfStreamException($"Unexpected end of Animation while reading {field} "
                + $"at 0x{reader.BaseStream.Position:X}.");
    }
}
