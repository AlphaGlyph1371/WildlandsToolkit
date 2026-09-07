using System.IO;
using System.Numerics;

namespace Wildlands.Formats.Models;

public sealed class SkeletonBoneModifier
{
    public const uint RollBoneModifierHash = 0x76D082F6;
    public const uint MaxLookAtBoneModifierHash = 0x49F4CA3E;
    public const uint OrientationConstraintBoneModifierHash = 0x75116750;
    public const uint BallJointBoneModifierHash = 0x4BF66801;
    public const uint SpringBoxModifierHash = 0xF9D25748;
    public const uint PendulumBoneModifierHash = 0x0544175B;
    public const uint RotationPasteModifierHash = 0x711759AF;

    public byte Prefix { get; init; }
    public ulong Id { get; init; }
    public uint Type { get; init; }
    public byte OwnerPointerTag { get; init; }
    public ulong OwnerId { get; init; }
    public float BlendWeight { get; init; }
    public int LimitLodLevel { get; init; }
    public byte UseModifiedBoneMatrix { get; init; }
    public byte[] TypeData { get; init; } = [];

    public string TypeName => Type switch
    {
        RollBoneModifierHash => "RollBoneModifier",
        MaxLookAtBoneModifierHash => "MaxLookAtBoneModifier",
        OrientationConstraintBoneModifierHash => "OrientationConstraintBoneModifier",
        BallJointBoneModifierHash => "BallJointBoneModifier",
        SpringBoxModifierHash => "SpringBoxModifier",
        PendulumBoneModifierHash => "PendulumBoneModifier",
        RotationPasteModifierHash => "RotationPasteModifier",
        _ => $"0x{Type:X8}",
    };
}

public sealed class SkeletonBone
{
    public const uint ClassHash = 0x95741049;

    public byte Prefix { get; init; }
    public ulong Id { get; init; }
    public uint Name { get; init; }
    public byte ParentPointerTag { get; init; }
    public ulong ParentId { get; init; }
    public byte MirrorPointerTag { get; init; }
    public ulong MirrorId { get; init; }

    public Vector3 LocalPosition { get; set; }
    public Quaternion LocalRotation { get; set; }
    public Vector3 GlobalPosition { get; set; }
    public Quaternion GlobalRotation { get; set; }

    public float GlobalPositionW { get; init; }
    public float LocalPositionW { get; init; }
    public byte SolvingPriority { get; init; }
    public int MirroringType { get; init; }
    public List<SkeletonBoneModifier> Modifiers { get; } = [];
    public List<uint> Dependencies { get; } = [];
    public int WrinkleCategory { get; set; }
    public float WrinkleFactor { get; set; }

    public int Index { get; set; }
    public int ChildrenCount { get; set; }
    public int ParentIndex { get; set; } = -1;
}

public sealed class SkeletonAsset
{
    public ulong Id { get; init; }
    public byte Prefix { get; init; }
    public uint SkeletonType { get; init; }
    public List<SkeletonBone> Bones { get; } = [];

    public byte[] TailData { get; init; } = [];
}

public static class Skeleton
{
    public const uint ClassHash = 0x24AECB7C;

    const uint ConstraintTargetHash = 0xDF638110;
    const int MaximumBones = ushort.MaxValue;
    const int MaximumListItems = 1_000_000;

    public static List<SkeletonBone> Read(byte[] resource) => ReadAsset(resource).Bones;

    public static SkeletonAsset ReadAsset(byte[] resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        using var stream = new MemoryStream(resource, writable: false);
        using var reader = new BinaryReader(stream);

        var header = ReadHeader(reader, ClassHash, "Skeleton");
        byte prefix = ReadByte(reader, "skeleton prefix");
        uint skeletonType = ReadUInt32(reader, "skeleton type");
        int count = ReadCount(reader, "bone", MaximumBones);
        var bones = new List<SkeletonBone>(count);

        for (int i = 0; i < count; i++)
            bones.Add(ReadBone(reader, i));

        LinkAndValidate(bones);
        byte[] tail = reader.ReadBytes(CheckedRemaining(reader, "skeleton tail"));
        if (tail.Length == 0)
            throw new InvalidDataException("The Skeleton ends before its root and hierarchy data.");

        var asset = new SkeletonAsset
        {
            Id = header.Id,
            Prefix = prefix,
            SkeletonType = skeletonType,
            TailData = tail,
        };
        asset.Bones.AddRange(bones);
        return asset;
    }

    public static byte[] Write(SkeletonAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        LinkAndValidate(asset.Bones);
        if (asset.TailData.Length == 0)
            throw new InvalidDataException("The Skeleton has no root and hierarchy tail.");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(asset.Id);
        writer.Write(ClassHash);
        writer.Write(asset.Prefix);
        writer.Write(asset.SkeletonType);
        writer.Write(asset.Bones.Count);
        for (int i = 0; i < asset.Bones.Count; i++)
            WriteBone(writer, asset.Bones[i], i);
        writer.Write(asset.TailData);
        return stream.ToArray();
    }

    static SkeletonBone ReadBone(BinaryReader reader, int expectedIndex)
    {
        long start = reader.BaseStream.Position;
        byte prefix = ReadByte(reader, $"bone {expectedIndex} prefix");
        var header = ReadHeader(reader, SkeletonBone.ClassHash, $"bone {expectedIndex}");
        uint name = ReadUInt32(reader, $"bone {expectedIndex} name");
        var parent = ReadLinkPointer(reader, $"bone {expectedIndex} parent");
        var mirror = ReadLinkPointer(reader, $"bone {expectedIndex} mirror");
        var (globalPosition, globalW) = ReadPosition(reader, $"bone {expectedIndex} global position");
        Quaternion globalRotation = ReadRotation(reader, $"bone {expectedIndex} global rotation");
        var (localPosition, localW) = ReadPosition(reader, $"bone {expectedIndex} local position");
        Quaternion localRotation = ReadRotation(reader, $"bone {expectedIndex} local rotation");
        byte priority = ReadByte(reader, $"bone {expectedIndex} solving priority");
        int mirroring = ReadInt32(reader, $"bone {expectedIndex} mirroring type");

        var bone = new SkeletonBone
        {
            Prefix = prefix,
            Id = header.Id,
            Name = name,
            ParentPointerTag = parent.Tag,
            ParentId = parent.Id,
            MirrorPointerTag = mirror.Tag,
            MirrorId = mirror.Id,
            GlobalPosition = globalPosition,
            GlobalPositionW = globalW,
            GlobalRotation = globalRotation,
            LocalPosition = localPosition,
            LocalPositionW = localW,
            LocalRotation = localRotation,
            SolvingPriority = priority,
            MirroringType = mirroring,
        };

        int modifierCount = ReadCount(reader, $"bone {expectedIndex} modifier", MaximumListItems);
        for (int i = 0; i < modifierCount; i++)
            bone.Modifiers.Add(ReadModifier(reader, expectedIndex, i));

        int dependencyCount = ReadCount(reader, $"bone {expectedIndex} dependency", MaximumListItems);
        for (int i = 0; i < dependencyCount; i++)
            bone.Dependencies.Add(ReadUInt32(reader, $"bone {expectedIndex} dependency {i}"));

        bone.WrinkleCategory = ReadInt32(reader, $"bone {expectedIndex} wrinkle category");
        bone.WrinkleFactor = ReadSingle(reader, $"bone {expectedIndex} wrinkle factor");
        bone.Index = ReadUInt16(reader, $"bone {expectedIndex} index");
        bone.ChildrenCount = ReadUInt16(reader, $"bone {expectedIndex} children count");

        if (bone.Index != expectedIndex)
            throw new InvalidDataException($"Bone {expectedIndex} at 0x{start:X} stores index {bone.Index}; the Skeleton is desynchronized or corrupt.");

        return bone;
    }

    static SkeletonBoneModifier ReadModifier(BinaryReader reader, int boneIndex, int modifierIndex)
    {
        byte prefix = ReadByte(reader, $"bone {boneIndex} modifier {modifierIndex} prefix");
        var header = ReadHeader(reader, 0, $"bone {boneIndex} modifier {modifierIndex}");
        var owner = ReadLinkPointer(reader, $"bone {boneIndex} modifier {modifierIndex} owner");
        float blendWeight = ReadSingle(reader, $"bone {boneIndex} modifier {modifierIndex} blend weight");
        int limitLodLevel = ReadInt32(reader, $"bone {boneIndex} modifier {modifierIndex} LOD limit");
        byte useModified = ReadByte(reader, $"bone {boneIndex} modifier {modifierIndex} matrix flag");

        long typeDataStart = reader.BaseStream.Position;
        switch (header.ClassHash)
        {
            case SkeletonBoneModifier.RollBoneModifierHash:
                ReadSingle(reader, "roll factor");
                ReadLinkPointer(reader, "roll source bone");
                break;

            case SkeletonBoneModifier.MaxLookAtBoneModifierHash:
                ReadLinkPointer(reader, "look-at up node");
                ReadLinkPointer(reader, "look-at bone");
                ReadBytes(reader, 16, "look-at position offset");
                ReadBytes(reader, 16, "look-at axes");
                break;

            case SkeletonBoneModifier.OrientationConstraintBoneModifierHash:
                int targets = ReadCount(reader, "orientation target", MaximumListItems);
                for (int i = 0; i < targets; i++)
                {
                    if (ReadByte(reader, $"orientation target {i} pointer tag") != 0)
                        throw new InvalidDataException($"Orientation target {i} is not an embedded object.");
                    ReadHeader(reader, ConstraintTargetHash, $"orientation target {i}");
                    ReadLinkPointer(reader, $"orientation target {i} bone");
                    ReadSingle(reader, $"orientation target {i} weight");
                }
                ReadByte(reader, "orientation keep-initial-offset flag");
                ReadBytes(reader, 16, "orientation offset");
                break;

            case SkeletonBoneModifier.BallJointBoneModifierHash:
                ReadInt32(reader, "ball-joint constraint-angle type");
                ReadBytes(reader, 40, "ball-joint scalar settings");
                ReadInt32(reader, "ball-joint gravity mode");
                ReadBytes(reader, 16, "ball-joint gravity");
                ReadBytes(reader, 12, "ball-joint local offsets");
                ReadFileReference(reader, "ball-joint common data");
                ReadLinkPointer(reader, "ball-joint orientation bone");
                ReadFileReference(reader, "ball-joint lite ragdoll");
                break;

            case SkeletonBoneModifier.SpringBoxModifierHash:
            case SkeletonBoneModifier.PendulumBoneModifierHash:
                ReadBytes(reader, 60, "physics modifier settings");
                break;

            case SkeletonBoneModifier.RotationPasteModifierHash:
                ReadLinkPointer(reader, "rotation paste linked bone");
                ReadByte(reader, "rotation paste parent-rotation flag");
                break;

            default:
                throw new NotSupportedException($"Skeleton bone {boneIndex} has unsupported modifier 0x{header.ClassHash:X8} at 0x{typeDataStart:X}. ");
        }

        long end = reader.BaseStream.Position;
        reader.BaseStream.Position = typeDataStart;
        byte[] typeData = ReadBytes(reader, checked((int)(end - typeDataStart)), "modifier data");

        return new SkeletonBoneModifier
        {
            Prefix = prefix,
            Id = header.Id,
            Type = header.ClassHash,
            OwnerPointerTag = owner.Tag,
            OwnerId = owner.Id,
            BlendWeight = blendWeight,
            LimitLodLevel = limitLodLevel,
            UseModifiedBoneMatrix = useModified,
            TypeData = typeData,
        };
    }

    static void WriteBone(BinaryWriter writer, SkeletonBone bone, int expectedIndex)
    {
        if (bone.Index != expectedIndex)
            throw new InvalidDataException($"Bone {expectedIndex} stores index {bone.Index}.");
        if ((uint)bone.ChildrenCount > ushort.MaxValue)
            throw new InvalidDataException($"Bone {expectedIndex} has invalid children count {bone.ChildrenCount}.");

        writer.Write(bone.Prefix);
        writer.Write(bone.Id);
        writer.Write(SkeletonBone.ClassHash);
        writer.Write(bone.Name);
        WriteLinkPointer(writer, bone.ParentPointerTag, bone.ParentId, "parent");
        WriteLinkPointer(writer, bone.MirrorPointerTag, bone.MirrorId, "mirror");
        WritePosition(writer, bone.GlobalPosition, bone.GlobalPositionW);
        WriteRotation(writer, bone.GlobalRotation);
        WritePosition(writer, bone.LocalPosition, bone.LocalPositionW);
        WriteRotation(writer, bone.LocalRotation);
        writer.Write(bone.SolvingPriority);
        writer.Write(bone.MirroringType);
        writer.Write(bone.Modifiers.Count);

        foreach (var modifier in bone.Modifiers)
        {
            writer.Write(modifier.Prefix);
            writer.Write(modifier.Id);
            writer.Write(modifier.Type);
            WriteLinkPointer(writer, modifier.OwnerPointerTag, modifier.OwnerId, "modifier owner");
            writer.Write(modifier.BlendWeight);
            writer.Write(modifier.LimitLodLevel);
            writer.Write(modifier.UseModifiedBoneMatrix);
            writer.Write(modifier.TypeData);
        }

        writer.Write(bone.Dependencies.Count);
        foreach (uint dependency in bone.Dependencies)
            writer.Write(dependency);
        writer.Write(bone.WrinkleCategory);
        writer.Write(bone.WrinkleFactor);
        writer.Write((ushort)bone.Index);
        writer.Write((ushort)bone.ChildrenCount);
    }

    static void LinkAndValidate(List<SkeletonBone> bones)
    {
        if (bones.Count > MaximumBones)
            throw new InvalidDataException($"Skeleton has {bones.Count} bones; maximum is {MaximumBones}.");

        var byId = new Dictionary<ulong, int>(bones.Count);
        for (int i = 0; i < bones.Count; i++)
        {
            if (bones[i].Index != i)
                throw new InvalidDataException($"Bone {i} stores index {bones[i].Index}.");
            bones[i].ParentIndex = -1;
            if (!byId.TryAdd(bones[i].Id, i))
                throw new InvalidDataException($"Skeleton contains duplicate bone id 0x{bones[i].Id:X16}.");
        }

        for (int i = 0; i < bones.Count; i++)
        {
            var bone = bones[i];
            if (bone.ParentId == 0)
                continue;
            if (!byId.TryGetValue(bone.ParentId, out int parent))
                throw new InvalidDataException($"Bone {i} references missing parent 0x{bone.ParentId:X16}.");
            if (parent == i)
                throw new InvalidDataException($"Bone {i} is its own parent.");
            bone.ParentIndex = parent;
        }

        for (int i = 0; i < bones.Count; i++)
        {
            int current = i;
            for (int steps = 0; current >= 0; steps++)
            {
                if (steps > bones.Count)
                    throw new InvalidDataException($"Skeleton hierarchy contains a cycle through bone {i}.");
                current = bones[current].ParentIndex;
            }
        }

        var descendants = new int[bones.Count];
        for (int i = bones.Count - 1; i >= 0; i--)
        {
            int parent = bones[i].ParentIndex;
            if (parent >= 0)
                descendants[parent] += descendants[i] + 1;
        }

        for (int i = 0; i < bones.Count; i++)
            if (bones[i].ChildrenCount != descendants[i])
                throw new InvalidDataException($"Bone {i} states {bones[i].ChildrenCount} descendant(s) but the hierarchy holds {descendants[i]}.");
    }

    static (byte Tag, ulong Id) ReadLinkPointer(BinaryReader reader, string field)
    {
        byte tag = ReadByte(reader, field + " tag");
        return tag switch
        {
            2 => (tag, ReadUInt64(reader, field + " id")),
            3 => (tag, 0),
            _ => throw new InvalidDataException($"{field} uses unsupported object-pointer tag {tag}."),
        };
    }

    static void ReadFileReference(BinaryReader reader, string field)
    {
        byte tag = ReadByte(reader, field + " tag");
        if (tag is not (1 or 3))
            throw new InvalidDataException($"{field} uses unsupported file-reference tag {tag}.");
        ReadByte(reader, field + " global flag");
        ReadUInt64(reader, field + " id");
    }

    static void WriteLinkPointer(BinaryWriter writer, byte tag, ulong id, string field)
    {
        if (tag is not (2 or 3))
            throw new InvalidDataException($"{field} uses unsupported object-pointer tag {tag}.");
        if (tag == 3 && id != 0)
            throw new InvalidDataException($"Null {field} pointer carries id 0x{id:X16}.");
        writer.Write(tag);
        if (tag == 2)
            writer.Write(id);
    }

    static ScimitarHeader ReadHeader(BinaryReader reader, uint expected, string field)
    {
        Ensure(reader, 12, field);
        return ScimitarHeader.Read(reader, expected);
    }

    static (Vector3 Value, float W) ReadPosition(BinaryReader reader, string field)
    {
        Ensure(reader, 16, field);
        return (new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()), reader.ReadSingle());
    }

    static Quaternion ReadRotation(BinaryReader reader, string field)
    {
        Ensure(reader, 16, field);
        return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    static void WritePosition(BinaryWriter writer, Vector3 value, float w)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(w);
    }

    static void WriteRotation(BinaryWriter writer, Quaternion value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
        writer.Write(value.W);
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

    static ushort ReadUInt16(BinaryReader reader, string field)
    {
        Ensure(reader, 2, field);
        return reader.ReadUInt16();
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

    static ulong ReadUInt64(BinaryReader reader, string field)
    {
        Ensure(reader, 8, field);
        return reader.ReadUInt64();
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

    static int CheckedRemaining(BinaryReader reader, string field)
    {
        long remaining = reader.BaseStream.Length - reader.BaseStream.Position;
        if (remaining < 0 || remaining > int.MaxValue)
            throw new InvalidDataException($"Invalid {field} length {remaining}.");
        return (int)remaining;
    }

    static void Ensure(BinaryReader reader, int count, string field)
    {
        if (count < 0 || reader.BaseStream.Position > reader.BaseStream.Length - count)
            throw new EndOfStreamException($"Unexpected end of Skeleton while reading {field} at 0x{reader.BaseStream.Position:X}.");
    }
}
