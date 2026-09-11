using System.IO;
using Wildlands.Formats.Data;

namespace Wildlands.Formats.Models;

public sealed class ClothObject
{
    public ulong Id { get; init; }
    public uint ClassHash { get; init; }
    public byte[] Payload { get; init; } = [];
}

public sealed class ClothFace
{
    public uint First { get; init; }
    public uint Second { get; init; }
    public uint Third { get; init; }
    public uint Fourth { get; init; }
}

public sealed class ClothLink
{
    public byte Flag { get; init; }
    public ushort Anchor { get; init; }
    public ushort First { get; init; }
    public ushort Second { get; init; }
    public ushort Third { get; init; }
    public float ValueA { get; init; }
    public float ValueB { get; init; }
    public float ValueC { get; init; }
}

public sealed class ClothAsset
{
    public ulong Id { get; init; }
    public byte[] Header { get; init; } = [];
    public List<ClothObject> Objects { get; } = [];

    public IEnumerable<ClothFace> Faces => Objects
        .Where(item => item.ClassHash == Cloth.FaceClassHash && item.Payload.Length == 16)
        .Select(item => new ClothFace
        {
            First = BitConverter.ToUInt32(item.Payload, 0),
            Second = BitConverter.ToUInt32(item.Payload, 4),
            Third = BitConverter.ToUInt32(item.Payload, 8),
            Fourth = BitConverter.ToUInt32(item.Payload, 12),
        });

    public IEnumerable<ClothLink> Links => Objects
        .Where(item => item.ClassHash == Cloth.LinkClassHash && item.Payload.Length == 25)
        .Select(item => new ClothLink
        {
            Flag = item.Payload[0],
            Anchor = BitConverter.ToUInt16(item.Payload, 1),
            First = BitConverter.ToUInt16(item.Payload, 7),
            Second = BitConverter.ToUInt16(item.Payload, 9),
            Third = BitConverter.ToUInt16(item.Payload, 11),
            ValueA = BitConverter.ToSingle(item.Payload, 13),
            ValueB = BitConverter.ToSingle(item.Payload, 17),
            ValueC = BitConverter.ToSingle(item.Payload, 21),
        });
}

public static class Cloth
{
    public const uint ClassHash = 0xE33044BA;
    public const uint FaceClassHash = 0xCCC97813;
    public const uint LinkClassHash = 0x62BA80E1;
    public const uint FrameClassHash = 0x9EF0E7A1;
    public const uint PieceClassHash = 0x295583EF;
    public const int HeaderSize = 46;

    public static ClothAsset Read(byte[] resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Length < HeaderSize + 12)
            throw new InvalidDataException("The Cloth is too short to hold an object.");
        if (BitConverter.ToUInt32(resource, 8) != ClassHash)
            throw new InvalidDataException("The resource is not a Cloth.");

        var objects = AnvilObjects.ScanSequence(resource, HeaderSize);
        if (objects.Count == 0)
            throw new InvalidDataException("The Cloth holds no objects.");

        var asset = new ClothAsset
        {
            Id = BitConverter.ToUInt64(resource, 0),
            Header = resource.AsSpan(0, HeaderSize).ToArray(),
        };

        for (int i = 0; i < objects.Count; i++)
        {
            int start = objects[i].Offset + 12;
            int end = i + 1 < objects.Count ? objects[i + 1].Offset : resource.Length;
            asset.Objects.Add(new ClothObject
            {
                Id = objects[i].Id,
                ClassHash = objects[i].ClassHash,
                Payload = resource.AsSpan(start, end - start).ToArray(),
            });
        }

        return asset;
    }

    public static byte[] Write(ClothAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Header.Length != HeaderSize)
            throw new InvalidDataException($"The Cloth header holds {asset.Header.Length} "
                + $"byte(s) instead of {HeaderSize}.");

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(asset.Header);
        foreach (ClothObject item in asset.Objects)
        {
            writer.Write(item.Id);
            writer.Write(item.ClassHash);
            writer.Write(item.Payload);
        }

        return stream.ToArray();
    }
}
