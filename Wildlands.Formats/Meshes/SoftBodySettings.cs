using System.IO;

namespace Wildlands.Formats.Models;

public enum SoftBodyFieldKind
{
    Byte,
    Float,
    UInt32,
}

public sealed record SoftBodyField(int Offset, SoftBodyFieldKind Kind, bool Varies)
{
    public string Name => $"{Kind.ToString().ToLowerInvariant()} at {Offset}";
}

public sealed class SoftBodySettingsAsset
{
    public ulong Id { get; init; }
    public byte[] Fixed { get; init; } = [];
    public List<uint> Bones { get; } = [];

    public float GetFloat(SoftBodyField field) =>
        BitConverter.ToSingle(Fixed, field.Offset - SoftBodySettings.FirstField);

    public void SetFloat(SoftBodyField field, float value) =>
        BitConverter.TryWriteBytes(Fixed.AsSpan(field.Offset - SoftBodySettings.FirstField), value);

    public byte GetByte(SoftBodyField field) => Fixed[field.Offset - SoftBodySettings.FirstField];

    public void SetByte(SoftBodyField field, byte value) =>
        Fixed[field.Offset - SoftBodySettings.FirstField] = value;

    public uint GetUInt32(SoftBodyField field) =>
        BitConverter.ToUInt32(Fixed, field.Offset - SoftBodySettings.FirstField);

    public void SetUInt32(SoftBodyField field, uint value) =>
        BitConverter.TryWriteBytes(Fixed.AsSpan(field.Offset - SoftBodySettings.FirstField), value);
}

public static class SoftBodySettings
{
    public const uint ClassHash = 0xF7A7E4DF;
    public const int FirstField = 12;
    public const int BoneCountOffset = 141;

    const int MaximumBones = 4096;

    public static IReadOnlyList<SoftBodyField> Fields { get; } =
    [
        new(12, SoftBodyFieldKind.Byte, false),
        new(13, SoftBodyFieldKind.Float, false),
        new(17, SoftBodyFieldKind.Byte, false),
        new(18, SoftBodyFieldKind.Byte, true),
        new(19, SoftBodyFieldKind.Byte, false),
        new(20, SoftBodyFieldKind.Byte, false),
        new(21, SoftBodyFieldKind.UInt32, false),
        new(25, SoftBodyFieldKind.Float, false),
        new(29, SoftBodyFieldKind.Float, false),
        new(33, SoftBodyFieldKind.Float, false),
        new(37, SoftBodyFieldKind.Float, false),
        new(41, SoftBodyFieldKind.Float, true),
        new(45, SoftBodyFieldKind.Float, true),
        new(49, SoftBodyFieldKind.Float, false),
        new(53, SoftBodyFieldKind.Float, false),
        new(57, SoftBodyFieldKind.Float, false),
        new(61, SoftBodyFieldKind.Float, false),
        new(65, SoftBodyFieldKind.Float, true),
        new(69, SoftBodyFieldKind.Float, false),
        new(73, SoftBodyFieldKind.Float, false),
        new(77, SoftBodyFieldKind.Byte, true),
        new(78, SoftBodyFieldKind.Float, true),
        new(82, SoftBodyFieldKind.Byte, true),
        new(83, SoftBodyFieldKind.Float, true),
        new(87, SoftBodyFieldKind.Byte, true),
        new(88, SoftBodyFieldKind.Float, true),
        new(92, SoftBodyFieldKind.Float, false),
        new(96, SoftBodyFieldKind.Byte, true),
        new(97, SoftBodyFieldKind.Byte, true),
        new(98, SoftBodyFieldKind.Byte, true),
        new(99, SoftBodyFieldKind.Byte, false),
        new(100, SoftBodyFieldKind.Float, true),
        new(104, SoftBodyFieldKind.Float, true),
        new(108, SoftBodyFieldKind.Byte, true),
        new(109, SoftBodyFieldKind.Float, false),
        new(113, SoftBodyFieldKind.Float, true),
        new(117, SoftBodyFieldKind.Byte, false),
        new(118, SoftBodyFieldKind.Float, false),
        new(122, SoftBodyFieldKind.Float, false),
        new(126, SoftBodyFieldKind.Float, true),
        new(130, SoftBodyFieldKind.Float, false),
        new(134, SoftBodyFieldKind.Float, false),
        new(138, SoftBodyFieldKind.Byte, false),
        new(139, SoftBodyFieldKind.Byte, false),
        new(140, SoftBodyFieldKind.Byte, false),
    ];

    public static SoftBodySettingsAsset Read(byte[] resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Length < BoneCountOffset + 4)
            throw new InvalidDataException("The SoftBodySettings is too short.");
        if (BitConverter.ToUInt32(resource, 8) != ClassHash)
            throw new InvalidDataException("The resource is not a SoftBodySettings.");

        int bones = BitConverter.ToInt32(resource, BoneCountOffset);
        if (bones < 0 || bones > MaximumBones)
            throw new InvalidDataException($"Invalid SoftBodySettings bone count {bones}.");
        if (resource.Length != BoneCountOffset + 4 + bones * 4)
            throw new InvalidDataException($"A SoftBodySettings with {bones} bone(s) should be "
                + $"{BoneCountOffset + 4 + bones * 4} byte(s), not {resource.Length}.");

        var asset = new SoftBodySettingsAsset
        {
            Id = BitConverter.ToUInt64(resource, 0),
            Fixed = resource.AsSpan(FirstField, BoneCountOffset - FirstField).ToArray(),
        };
        for (int i = 0; i < bones; i++)
            asset.Bones.Add(BitConverter.ToUInt32(resource, BoneCountOffset + 4 + i * 4));
        return asset;
    }

    public static byte[] Write(SoftBodySettingsAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Fixed.Length != BoneCountOffset - FirstField)
            throw new InvalidDataException($"The SoftBodySettings block holds {asset.Fixed.Length} "
                + $"byte(s) instead of {BoneCountOffset - FirstField}.");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(asset.Id);
        writer.Write(ClassHash);
        writer.Write(asset.Fixed);
        writer.Write(asset.Bones.Count);
        foreach (uint bone in asset.Bones)
            writer.Write(bone);
        return stream.ToArray();
    }
}
