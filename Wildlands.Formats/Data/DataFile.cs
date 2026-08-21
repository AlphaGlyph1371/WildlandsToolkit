using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Wildlands.Formats.Data;

public sealed class Resource
{
    public ulong Id { get; set; }
    public uint ClassHash { get; set; }
    public string Name { get; set; } = "";

    public byte[] Header { get; set; } = [];

    public byte[] Data { get; set; } = [];
}

public sealed class DataFile
{
    const int IndexEntrySize = 14;

    public List<Resource> Resources { get; } = [];

    public CompressionInfo IndexCompression { get; private set; }
    public CompressionInfo PayloadCompression { get; private set; }

    byte[] _index = [];
    byte[][] _indexBlocks = [];
    byte[] _payload = [];
    byte[][] _payloadBlocks = [];

    public ulong Id => Resources.Count > 0 ? Resources[0].Id : 0;

    public static DataFile Read(string path)
    {
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static DataFile Read(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);

        var indexBlob = CompressedBlob.Read(reader, out var indexCompression, out var indexBlocks);
        var payload = CompressedBlob.Read(reader, out var payloadCompression, out var payloadBlocks);
        var index = ReadIndex(indexBlob);

        var file = new DataFile
        {
            IndexCompression = indexCompression,
            PayloadCompression = payloadCompression,
            _index = indexBlob,
            _indexBlocks = indexBlocks,
            _payload = payload,
            _payloadBlocks = payloadBlocks,
        };

        int offset = 0;

        foreach (var (id, size) in index)
        {
            file.Resources.Add(ReadResource(payload, offset, size, id));
            offset += size;
        }

        return file;
    }

    public void Write(string path)
    {
        using var stream = File.Create(path);
        Write(stream);
    }

    public void Write(Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        var index = new byte[2 + Resources.Count * IndexEntrySize];
        BitConverter.TryWriteBytes(index, (ushort)Resources.Count);

        int total = 0;
        foreach (var resource in Resources)
            total += resource.Header.Length + resource.Data.Length;

        var payload = new byte[total];
        int at = 0;

        for (int i = 0; i < Resources.Count; i++)
        {
            var resource = Resources[i];
            int size = resource.Header.Length + resource.Data.Length;

            int entry = 2 + i * IndexEntrySize;
            BitConverter.TryWriteBytes(index.AsSpan(entry), resource.Id);
            BitConverter.TryWriteBytes(index.AsSpan(entry + 8), (uint)size);

            resource.Header.CopyTo(payload, at);
            BitConverter.TryWriteBytes(payload.AsSpan(at + 4), resource.Data.Length);
            resource.Data.CopyTo(payload, at + resource.Header.Length);

            at += size;
        }

        CompressedBlob.Write(writer, index, IndexCompression, _index, _indexBlocks);
        CompressedBlob.Write(writer, payload, PayloadCompression, _payload, _payloadBlocks);
    }

    static List<(ulong Id, int Size)> ReadIndex(byte[] blob)
    {
        int count = BitConverter.ToUInt16(blob, 0);
        var index = new List<(ulong, int)>(count);

        for (int i = 0; i < count; i++)
        {
            int at = 2 + i * IndexEntrySize;
            index.Add((BitConverter.ToUInt64(blob, at), (int)BitConverter.ToUInt32(blob, at + 8)));
        }

        return index;
    }

    static Resource ReadResource(byte[] payload, int offset, int size, ulong id)
    {
        uint classHash = BitConverter.ToUInt32(payload, offset);
        int dataLength = BitConverter.ToInt32(payload, offset + 4);
        int nameLength = BitConverter.ToInt32(payload, offset + 8);
        int headerLength = size - dataLength;

        return new Resource
        {
            Id = id,
            ClassHash = classHash,
            Name = Encoding.UTF8.GetString(payload, offset + 12, nameLength),
            Header = payload.AsSpan(offset, headerLength).ToArray(),
            Data = payload.AsSpan(offset + headerLength, dataLength).ToArray(),
        };
    }
}
