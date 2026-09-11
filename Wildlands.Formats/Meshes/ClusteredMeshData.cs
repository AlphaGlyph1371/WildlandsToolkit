using System.IO;

namespace Wildlands.Formats.Models;

public sealed class ClusteredMeshData
{
    public const uint ClassHash = 0xC351EE43;
    const int MaximumItems = 1_000_000;

    public int DataVersion { get; set; }
    public byte VertexFormat { get; set; }
    public int VertexStride { get; set; }
    public int ClusterCount { get; set; }

    public float[] Center { get; } = new float[3];
    public float[] HalfExtent { get; } = new float[3];

    public int DrawPrimitiveCount { get; set; }
    public int[] ClustersPerDrawPrimitive { get; set; } = [];
    public int[] VertexOffsetPerDrawPrimitive { get; set; } = [];
    public bool FixedClusterSize { get; set; }

    public byte[] VertexBuffer { get; set; } = [];
    public byte[] IndexBuffer { get; set; } = [];
    public byte[] PrimitiveDescriptions { get; set; } = [];

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
        if (data.VertexStride < 0)
            throw new InvalidDataException("The clustered mesh has a negative vertex stride.");
        if (data.ClusterCount < 0 || data.ClusterCount > MaximumItems)
            throw new InvalidDataException($"The cluster count {data.ClusterCount} is outside the supported range.");

        for (int i = 0; i < 3; i++) data.Center[i] = reader.ReadSingle();
        for (int i = 0; i < 3; i++) data.HalfExtent[i] = reader.ReadSingle();

        data.DrawPrimitiveCount = MeshBinaryReader.ReadCount(reader, "draw primitive");
        data.ClustersPerDrawPrimitive = ReadInts(reader, "clusters-per-draw-primitive");
        data.VertexOffsetPerDrawPrimitive = ReadInts(reader, "vertex-offset-per-draw-primitive");
        data.FixedClusterSize = reader.ReadBoolean();

        data.VertexBuffer = MeshBinaryReader.ReadBytes(reader, "clustered vertex buffer");
        data.IndexBuffer = MeshBinaryReader.ReadBytes(reader, "clustered index buffer");
        data.PrimitiveDescriptions = MeshBinaryReader.ReadBytes(reader, "primitive descriptions");

        if (data.VertexStride <= 0 && data.VertexBuffer.Length > 0)
            throw new InvalidDataException("The clustered mesh has vertex data but no positive vertex stride.");
        if (data.VertexStride > 0 && data.VertexBuffer.Length % data.VertexStride != 0)
            throw new InvalidDataException("The clustered vertex buffer is not aligned to its stride.");
        if (data.DrawPrimitiveCount != data.ClustersPerDrawPrimitive.Length || data.DrawPrimitiveCount != data.VertexOffsetPerDrawPrimitive.Length)
            throw new InvalidDataException("The clustered mesh draw tables have different lengths.");

        return data;
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(DataVersion);
        writer.Write(VertexFormat);
        writer.Write(VertexStride);
        writer.Write(ClusterCount);

        for (int i = 0; i < 3; i++) writer.Write(Center[i]);
        for (int i = 0; i < 3; i++) writer.Write(HalfExtent[i]);

        writer.Write(DrawPrimitiveCount);
        WriteInts(writer, ClustersPerDrawPrimitive);
        WriteInts(writer, VertexOffsetPerDrawPrimitive);
        writer.Write(FixedClusterSize);

        writer.Write(VertexBuffer.Length);
        writer.Write(VertexBuffer);
        writer.Write(IndexBuffer.Length);
        writer.Write(IndexBuffer);
        writer.Write(PrimitiveDescriptions.Length);
        writer.Write(PrimitiveDescriptions);
    }

    static int[] ReadInts(BinaryReader reader, string label)
    {
        int count = MeshBinaryReader.ReadCount(reader, label);
        MeshBinaryReader.EnsureRemaining(reader, (long)count * sizeof(int), label);
        var values = new int[count];

        for (int i = 0; i < values.Length; i++)
            values[i] = reader.ReadInt32();

        return values;
    }

    static void WriteInts(BinaryWriter writer, int[] values)
    {
        writer.Write(values.Length);

        foreach (int value in values)
            writer.Write(value);
    }
}
