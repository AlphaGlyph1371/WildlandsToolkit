using System.IO;

namespace Wildlands.Formats.Models;

public sealed class ClusteredMeshData
{
    public const uint ClassHash = 0xC351EE43;

    public int DataVersion { get; private set; }
    public int VertexFormat { get; private set; }
    public int VertexStride { get; private set; }
    public int ClusterCount { get; private set; }

    public float[] Center { get; } = new float[3];
    public float[] HalfExtent { get; } = new float[3];

    public int DrawPrimitiveCount { get; private set; }
    public int[] ClustersPerDrawPrimitive { get; private set; } = [];
    public int[] VertexOffsetPerDrawPrimitive { get; private set; } = [];
    public bool FixedClusterSize { get; private set; }

    public byte[] VertexBuffer { get; private set; } = [];
    public byte[] IndexBuffer { get; private set; } = [];
    public byte[] PrimitiveDescriptions { get; private set; } = [];

    public int VertexCount => VertexStride > 0 ? VertexBuffer.Length / VertexStride : 0;

    public static ClusteredMeshData Read(BinaryReader reader)
    {
        var data = new ClusteredMeshData
        {
            DataVersion = reader.ReadInt32(),
            VertexFormat = reader.ReadByte(),
            VertexStride = reader.ReadInt32(),
            ClusterCount = reader.ReadInt32(),
        };

        for (int i = 0; i < 3; i++) data.Center[i] = reader.ReadSingle();
        for (int i = 0; i < 3; i++) data.HalfExtent[i] = reader.ReadSingle();

        data.DrawPrimitiveCount = reader.ReadInt32();
        data.ClustersPerDrawPrimitive = ReadInts(reader);
        data.VertexOffsetPerDrawPrimitive = ReadInts(reader);
        data.FixedClusterSize = reader.ReadBoolean();

        data.VertexBuffer = reader.ReadBytes(reader.ReadInt32());
        data.IndexBuffer = reader.ReadBytes(reader.ReadInt32());
        data.PrimitiveDescriptions = reader.ReadBytes(reader.ReadInt32());

        return data;
    }

    static int[] ReadInts(BinaryReader reader)
    {
        var values = new int[reader.ReadInt32()];

        for (int i = 0; i < values.Length; i++)
            values[i] = reader.ReadInt32();

        return values;
    }
}
