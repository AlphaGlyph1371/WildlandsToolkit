using System.Collections.Generic;
using System.IO;

namespace Wildlands.Formats.Models;

public sealed class MeshPrimitive
{
    public const uint ClassHash = 2775812079;

    public int MinIndex { get; set; }
    public int UsesDepthOnlyBuffers { get; set; }
    public int VertexCount { get; set; }
    public int StartIndex { get; set; }
    public int TriangleCount { get; set; }
    public int Type { get; set; }
}

public sealed class MeshData
{
    public const uint ClassHash = 105229237;

    public bool Indices32Bit { get; private set; }
    public int VertexFormat { get; private set; }
    public byte VertexStride { get; private set; }

    public List<MeshPrimitive> Standard { get; } = [];
    public List<MeshPrimitive> Shadow { get; } = [];

    public byte[] VertexBuffer { get; private set; } = [];
    public byte[] IndexBuffer { get; private set; } = [];

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

        data.VertexBuffer = reader.ReadBytes(reader.ReadInt32());
        data.IndexBuffer = reader.ReadBytes(reader.ReadInt32());

        return data;
    }

    static void ReadPrimitives(BinaryReader reader, List<MeshPrimitive> target)
    {
        int count = reader.ReadInt32();

        for (int i = 0; i < count; i++)
        {
            ScimitarHeader.Read(reader, MeshPrimitive.ClassHash);

            target.Add(new MeshPrimitive
            {
                MinIndex = reader.ReadInt32(),
                UsesDepthOnlyBuffers = reader.ReadInt32(),
                VertexCount = reader.ReadInt32(),
                StartIndex = reader.ReadInt32(),
                TriangleCount = reader.ReadInt32(),
                Type = reader.ReadInt32(),
            });
        }
    }
}
