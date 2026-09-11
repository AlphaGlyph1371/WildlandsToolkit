using System.IO;
using Wildlands.Formats.Data;

namespace Wildlands.Formats.Models;

public sealed class LayeredSkyLayer
{
    public float Hour { get; init; }
    public uint Hash { get; init; }
    public int RowPitch { get; init; }
    public int Height { get; init; }
    public int Width { get; init; }
    public int Unknown { get; init; }
    public int BytesPerTexel { get; init; }
    public byte[] HeaderTail { get; init; } = [];
    public byte[] Texels { get; init; } = [];
    public byte[] Padding { get; init; } = [];

    public int Depth => BytesPerTexel > 0 && Width > 0 && Height > 0
        ? Texels.Length / BytesPerTexel / Width / Height
        : 0;
}

public sealed class LayeredSkyAsset
{
    public ulong Id { get; init; }
    public ulong FirstLayerId { get; init; }
    public byte[] Prologue { get; init; } = [];
    public List<LayeredSkyLayer> Layers { get; } = [];
}

public static class LayeredSky
{
    public const uint ClassHash = 0x24D32CF8;
    public const uint LayerClassHash = 0x5CDF6DE9;

    const int FirstObject = 0x41;
    const int LayerHeaderSize = 37;
    const int LayerHeaderTailSize = 9;
    const int MaximumTexels = 1 << 24;

    public static LayeredSkyAsset Read(byte[] resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Length < FirstObject + 12)
            throw new InvalidDataException("The LayeredSky is too short to hold an object.");
        if (BitConverter.ToUInt32(resource, 8) != ClassHash)
            throw new InvalidDataException("The resource is not a LayeredSky.");

        var objects = AnvilObjects.ScanSequence(resource, FirstObject);
        if (objects.Count == 0)
            throw new InvalidDataException("The LayeredSky holds no objects.");

        int first = 0;
        while (first < objects.Count && objects[first].ClassHash != LayerClassHash)
            first++;
        if (first == objects.Count)
            throw new InvalidDataException("The LayeredSky holds no layers.");
        if (objects.Skip(first).Any(item => item.ClassHash != LayerClassHash))
            throw new InvalidDataException("The LayeredSky layers are not the last objects.");

        var asset = new LayeredSkyAsset
        {
            Id = BitConverter.ToUInt64(resource, 0),
            FirstLayerId = objects[first].Id,
            Prologue = resource.AsSpan(0, objects[first].Offset).ToArray(),
        };

        for (int i = first; i < objects.Count; i++)
        {
            int start = objects[i].Offset + 12;
            int end = i + 1 < objects.Count ? objects[i + 1].Offset : resource.Length;
            int size = end - start - LayerHeaderSize;
            if (size < 0)
                throw new InvalidDataException($"LayeredSky layer {i - first} is too short.");

            int pitch = BitConverter.ToInt32(resource, start + 8);
            int height = BitConverter.ToInt32(resource, start + 12);
            int width = BitConverter.ToInt32(resource, start + 16);
            int bytes = BitConverter.ToInt32(resource, start + 24);
            if (width <= 0 || height <= 0 || bytes <= 0 || width > MaximumTexels || height > MaximumTexels)
                throw new InvalidDataException($"LayeredSky layer {i - first} has invalid dimensions.");

            int texels = size / (width * height * bytes) * (width * height * bytes);
            asset.Layers.Add(new LayeredSkyLayer
            {
                Hour = BitConverter.ToSingle(resource, start),
                Hash = BitConverter.ToUInt32(resource, start + 4),
                RowPitch = pitch,
                Height = height,
                Width = width,
                Unknown = BitConverter.ToInt32(resource, start + 20),
                BytesPerTexel = bytes,
                HeaderTail = resource.AsSpan(start + 28, LayerHeaderTailSize).ToArray(),
                Texels = resource.AsSpan(start + LayerHeaderSize, texels).ToArray(),
                Padding = resource.AsSpan(start + LayerHeaderSize + texels, size - texels).ToArray(),
            });
        }

        return asset;
    }

    public static byte[] Write(LayeredSkyAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(asset.Prologue);

        for (int i = 0; i < asset.Layers.Count; i++)
        {
            LayeredSkyLayer layer = asset.Layers[i];
            writer.Write(asset.FirstLayerId + (ulong)i);
            writer.Write(LayerClassHash);
            writer.Write(layer.Hour);
            writer.Write(layer.Hash);
            writer.Write(layer.RowPitch);
            writer.Write(layer.Height);
            writer.Write(layer.Width);
            writer.Write(layer.Unknown);
            writer.Write(layer.BytesPerTexel);
            writer.Write(layer.HeaderTail);
            writer.Write(layer.Texels);
            writer.Write(layer.Padding);
        }

        return stream.ToArray();
    }
}
