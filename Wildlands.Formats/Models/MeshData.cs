using System.Collections.Generic;
using System.IO;

namespace Wildlands.Formats.Models;

public sealed class MeshPrimitive
{
    public const uint ClassHash = 0xA57387EF;

    public ulong Id { get; set; }
    public int MinIndex { get; set; }
    public int UsesDepthOnlyBuffers { get; set; }
    public int VertexCount { get; set; }
    public int StartIndex { get; set; }
    public int TriangleCount { get; set; }
    public int Type { get; set; }
}

public sealed class MeshData
{
    public const uint ClassHash = 0x0645ABB5;

    public bool Indices32Bit { get; set; }
    public byte VertexFormat { get; set; }
    public byte VertexStride { get; set; }

    public List<MeshPrimitive> Standard { get; } = [];
    public List<MeshPrimitive> Shadow { get; } = [];

    public byte[] VertexBuffer { get; set; } = [];
    public byte[] IndexBuffer { get; set; } = [];

    public int VertexCount => VertexStride > 0 ? VertexBuffer.Length / VertexStride : 0;
    public int IndexCount => IndexBuffer.Length / (Indices32Bit ? 4 : 2);

    public static MeshData Read(BinaryReader reader)
    {
        var data = new MeshData
        {
            Indices32Bit = reader.ReadBoolean(),
            VertexFormat = reader.ReadByte(),
            VertexStride = reader.ReadByte(),
        };

        ReadPrimitives(reader, data.Standard);
        ReadPrimitives(reader, data.Shadow);

        data.VertexBuffer = MeshBinaryReader.ReadBytes(reader, "plain vertex buffer");
        data.IndexBuffer = MeshBinaryReader.ReadBytes(reader, "plain index buffer");

        if (data.VertexStride == 0 && data.VertexBuffer.Length > 0)
            throw new InvalidDataException("The plain mesh has vertex data but a zero vertex stride.");
        if (data.VertexStride > 0 && data.VertexBuffer.Length % data.VertexStride != 0)
            throw new InvalidDataException("The plain vertex buffer is not aligned to its stride.");
        int indexSize = data.Indices32Bit ? 4 : 2;
        if (data.IndexBuffer.Length % indexSize != 0)
            throw new InvalidDataException("The plain index buffer is not aligned to its index size.");

        return data;
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(Indices32Bit);
        writer.Write(VertexFormat);
        writer.Write(VertexStride);

        WritePrimitives(writer, Standard);
        WritePrimitives(writer, Shadow);

        writer.Write(VertexBuffer.Length);
        writer.Write(VertexBuffer);
        writer.Write(IndexBuffer.Length);
        writer.Write(IndexBuffer);
    }

    static void ReadPrimitives(BinaryReader reader, List<MeshPrimitive> target)
    {
        int count = MeshBinaryReader.ReadCount(reader, "mesh primitive");
        MeshBinaryReader.EnsureRemaining(reader, (long)count * 36, "mesh primitive records");

        for (int i = 0; i < count; i++)
        {
            var header = ScimitarHeader.Read(reader, MeshPrimitive.ClassHash);

            target.Add(new MeshPrimitive
            {
                Id = header.Id,
                MinIndex = reader.ReadInt32(),
                UsesDepthOnlyBuffers = reader.ReadInt32(),
                VertexCount = reader.ReadInt32(),
                StartIndex = reader.ReadInt32(),
                TriangleCount = reader.ReadInt32(),
                Type = reader.ReadInt32(),
            });
        }
    }

    static void WritePrimitives(BinaryWriter writer, List<MeshPrimitive> source)
    {
        writer.Write(source.Count);

        foreach (var primitive in source)
        {
            writer.Write(primitive.Id);
            writer.Write(MeshPrimitive.ClassHash);
            writer.Write(primitive.MinIndex);
            writer.Write(primitive.UsesDepthOnlyBuffers);
            writer.Write(primitive.VertexCount);
            writer.Write(primitive.StartIndex);
            writer.Write(primitive.TriangleCount);
            writer.Write(primitive.Type);
        }
    }
}
