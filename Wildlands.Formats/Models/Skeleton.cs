using System.IO;
using System.Numerics;

namespace Wildlands.Formats.Models;

public sealed class SkeletonBone
{
    public const uint ClassHash = 0x95741049;

    public ulong Id { get; init; }
    public uint Name { get; init; }
    public ulong ParentId { get; init; }

    public Vector3 LocalPosition { get; init; }
    public Quaternion LocalRotation { get; init; }
    public Vector3 GlobalPosition { get; init; }
    public Quaternion GlobalRotation { get; init; }

    public int Index { get; set; }
    public int ChildrenCount { get; set; }
    public int ParentIndex { get; set; } = -1;
}

public static class Skeleton
{
    public const uint ClassHash = 0x24AECB7C;

    public static List<SkeletonBone> Read(byte[] resource)
    {
        using var stream = new MemoryStream(resource);
        using var reader = new BinaryReader(stream);

        ScimitarHeader.Read(reader, ClassHash);

        reader.ReadByte();
        reader.ReadUInt32(); // skeleton type

        int count = reader.ReadInt32();
        var bones = new List<SkeletonBone>(count);

        for (int i = 0; i < count; i++)
        {
            reader.ReadByte();
            bones.Add(ReadBone(reader));

            if (i + 1 < count)
                SkipToNextBone(reader);
        }

        LinkParents(bones);
        return bones;
    }

    static SkeletonBone ReadBone(BinaryReader reader)
    {
        var header = ScimitarHeader.Read(reader, SkeletonBone.ClassHash);

        uint name = reader.ReadUInt32();
        ulong parentId = ReadObjectPointer(reader);
        ReadObjectPointer(reader); // mirror bone

        var bone = new SkeletonBone
        {
            Id = header.Id,
            Name = name,
            ParentId = parentId,
            GlobalPosition = ReadPosition(reader),
            GlobalRotation = ReadRotation(reader),
            LocalPosition = ReadPosition(reader),
            LocalRotation = ReadRotation(reader),
        };

        reader.ReadByte(); // solving priority
        reader.ReadInt32(); // mirroring type

        if (reader.ReadInt32() != 0)
            return bone;

        int dependencies = reader.ReadInt32();
        for (int i = 0; i < dependencies; i++)
            reader.ReadUInt32();

        reader.ReadInt32(); // wrinkle category
        reader.ReadSingle(); // wrinkle factor

        bone.Index = reader.ReadUInt16();
        bone.ChildrenCount = reader.ReadUInt16();
        return bone;
    }

    static void SkipToNextBone(BinaryReader reader)
    {
        var stream = reader.BaseStream;
        long start = stream.Position;

        for (long position = start; position + 13 <= stream.Length; position++)
        {
            stream.Position = position + 9;
            if (reader.ReadUInt32() != SkeletonBone.ClassHash)
                continue;

            stream.Position = position;
            return;
        }

        stream.Position = start;
    }

    static ulong ReadObjectPointer(BinaryReader reader)
    {
        byte tag = reader.ReadByte();

        return tag switch
        {
            1 or 2 => reader.ReadUInt64(),
            3 => 0,
            _ => throw new InvalidDataException($"Unexpected object pointer tag {tag}."),
        };
    }

    static void LinkParents(List<SkeletonBone> bones)
    {
        var byId = new Dictionary<ulong, int>();
        for (int i = 0; i < bones.Count; i++)
            byId[bones[i].Id] = i;

        foreach (var bone in bones)
        {
            if (byId.TryGetValue(bone.ParentId, out int parent))
                bone.ParentIndex = parent;
        }
    }

    static Vector3 ReadPosition(BinaryReader reader)
    {
        var value = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        reader.ReadSingle();
        return value;
    }

    static Quaternion ReadRotation(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
