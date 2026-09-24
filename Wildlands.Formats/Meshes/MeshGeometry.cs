using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace Wildlands.Formats.Models;

public sealed class MeshVertex
{
    public Vector3 Position { get; set; }
    public Vector3 Normal { get; set; }
    public Vector3 Tangent { get; set; }
    public Vector3 Binormal { get; set; }
    public Vector2[] Uv { get; set; } = [];
    public Vector4 Paint { get; set; } = Vector4.One;
    public uint Color { get; set; } = 0xFFFFFFFF;
    public byte[] JointIndices { get; set; } = [];
    public byte[] JointWeights { get; set; } = [];

    public short PositionScale { get; set; }
    public byte TangentSign { get; set; }
    public byte NormalPadding { get; set; }
}

public static class MeshGeometry
{
    public static MeshVertex[] ReadVertices(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var buffer = mesh.VertexBuffer;
        if (buffer.Length == 0)
            return [];

        var layout = VertexLayout.For(mesh.VertexFormat, mesh.VertexStride);
        int stride = layout.Stride;

        if (buffer.Length % stride != 0)
            throw new InvalidDataException($"The vertex buffer holds {buffer.Length} bytes, which is not a multiple of stride {stride}.");

        var vertices = new MeshVertex[buffer.Length / stride];

        for (int i = 0; i < vertices.Length; i++)
            vertices[i] = ReadVertex(buffer.AsSpan(i * stride, stride), layout, mesh.QuantizationFactor, mesh.UvQuantizationFactor);

        return vertices;
    }

    static MeshVertex ReadVertex(ReadOnlySpan<byte> source, VertexLayout layout, float quantization, float uvQuantization)
    {
        short scale = BitConverter.ToInt16(source[VertexLayout.ScaleAt..]);
        float factor = (layout.HasBinormal || scale >= 0 ? quantization : -quantization) / 32767f;

        var vertex = new MeshVertex
        {
            Position = new Vector3(
                BitConverter.ToInt16(source) * factor,
                BitConverter.ToInt16(source[2..]) * factor,
                BitConverter.ToInt16(source[4..]) * factor),
            Normal = Direction(source[VertexLayout.NormalAt..]),
            Tangent = Direction(source[VertexLayout.TangentAt..]),
        };

        if (layout.HasBinormal)
        {
            vertex.Binormal = Direction(source[layout.BinormalAt..]);
            vertex.Paint = new Vector4(
                source[layout.BinormalAt + 3] / 255f,
                source[VertexLayout.TangentAt + 3] / 255f,
                source[VertexLayout.NormalAt + 3] / 255f,
                scale / 32767f);
        }
        else
        {
            vertex.PositionScale = scale;
            vertex.NormalPadding = source[VertexLayout.NormalAt + 3];
            vertex.TangentSign = source[VertexLayout.TangentAt + 3];
            vertex.Binormal = Vector3.Cross(vertex.Normal, vertex.Tangent) * (vertex.TangentSign < 128 ? -1f : 1f);
        }

        if (layout.HasColor)
        {
            uint colour = BitConverter.ToUInt32(source[layout.ColorAt..]);
            if (layout.HasBinormal)
                vertex.Color = colour;
            else
                vertex.Paint = Unpack(colour);
        }

        vertex.Uv = new Vector2[layout.UvCount];
        for (int i = 0; i < layout.UvCount; i++)
        {
            int at = layout.UvAt[i];
            vertex.Uv[i] = new Vector2(
                BitConverter.ToInt16(source[at..]) / 32767f * uvQuantization,
                BitConverter.ToInt16(source[(at + 2)..]) / 32767f * uvQuantization);
        }

        if (layout.IsSkinned)
        {
            vertex.JointIndices = source.Slice(layout.JointsAt, layout.JointsPerVertex).ToArray();
            vertex.JointWeights = source.Slice(layout.WeightsAt, layout.JointsPerVertex).ToArray();
        }

        return vertex;
    }

    static Vector3 Direction(ReadOnlySpan<byte> source) => new(
        (source[0] - 127) / 127f,
        (source[1] - 127) / 127f,
        (source[2] - 127) / 127f);

    public static byte[] WriteVertices(MeshVertex[] vertices, VertexLayout layout, float quantization, float uvQuantization)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(layout);

        if (quantization <= 0)
            throw new InvalidDataException($"The quantization factor {quantization} cannot scale positions.");

        var buffer = new byte[vertices.Length * layout.Stride];

        for (int i = 0; i < vertices.Length; i++)
            WriteVertex(vertices[i], buffer.AsSpan(i * layout.Stride, layout.Stride), layout, quantization, uvQuantization, i);

        return buffer;
    }

    static void WriteVertex(MeshVertex vertex, Span<byte> target, VertexLayout layout, float quantization, float uvQuantization, int index)
    {
        short scale = layout.HasBinormal
            ? (short)Math.Clamp(MathF.Round(vertex.Paint.W * 32767f), short.MinValue, short.MaxValue)
            : vertex.PositionScale != 0 ? vertex.PositionScale : (short)32767;
        float factor = 32767f / (layout.HasBinormal || scale >= 0 ? quantization : -quantization);

        WriteInt16(target, Quantize(vertex.Position.X * factor, index, "position X"));
        WriteInt16(target[2..], Quantize(vertex.Position.Y * factor, index, "position Y"));
        WriteInt16(target[4..], Quantize(vertex.Position.Z * factor, index, "position Z"));
        WriteInt16(target[VertexLayout.ScaleAt..], scale);

        WriteDirection(target[VertexLayout.NormalAt..], vertex.Normal);
        WriteDirection(target[VertexLayout.TangentAt..], vertex.Tangent);

        if (layout.HasBinormal)
        {
            WriteDirection(target[layout.BinormalAt..], vertex.Binormal);
            target[layout.BinormalAt + 3] = Channel(vertex.Paint.X);
            target[VertexLayout.TangentAt + 3] = Channel(vertex.Paint.Y);
            target[VertexLayout.NormalAt + 3] = Channel(vertex.Paint.Z);
        }
        else
        {
            target[VertexLayout.NormalAt + 3] = vertex.NormalPadding;
            target[VertexLayout.TangentAt + 3] = vertex.TangentSign;
        }

        if (layout.HasColor)
            BitConverter.TryWriteBytes(target[layout.ColorAt..], layout.HasBinormal ? vertex.Color : Pack(vertex.Paint));

        for (int i = 0; i < layout.UvCount; i++)
        {
            var uv = i < vertex.Uv.Length ? vertex.Uv[i] : Vector2.Zero;
            int at = layout.UvAt[i];
            WriteInt16(target[at..], Quantize(uv.X / uvQuantization * 32767f, index, "texture U"));
            WriteInt16(target[(at + 2)..], Quantize(uv.Y / uvQuantization * 32767f, index, "texture V"));
        }

        if (!layout.IsSkinned)
            return;

        for (int j = 0; j < layout.JointsPerVertex; j++)
        {
            target[layout.JointsAt + j] = j < vertex.JointIndices.Length ? vertex.JointIndices[j] : (byte)0;
            target[layout.WeightsAt + j] = j < vertex.JointWeights.Length ? vertex.JointWeights[j] : (byte)0;
        }
    }

    public static uint Pack(Vector4 colour) =>
        (uint)(Channel(colour.X) | Channel(colour.Y) << 8 | Channel(colour.Z) << 16 | Channel(colour.W) << 24);

    public static Vector4 Unpack(uint colour) => new(
        (colour & 0xFF) / 255f,
        (colour >> 8 & 0xFF) / 255f,
        (colour >> 16 & 0xFF) / 255f,
        (colour >> 24) / 255f);

    static byte Channel(float value) => (byte)Math.Clamp(MathF.Round(value * 255f), 0, 255);

    static short Quantize(float value, int index, string field)
    {
        float rounded = MathF.Round(value);

        if (float.IsNaN(rounded) || rounded < short.MinValue || rounded > short.MaxValue)
            throw new InvalidDataException($"Vertex {index} has a {field} of {value:0.###} after quantization, which does not fit the mesh.");

        return (short)rounded;
    }

    static void WriteInt16(Span<byte> target, short value) => BitConverter.TryWriteBytes(target, value);

    static void WriteDirection(Span<byte> target, Vector3 value)
    {
        target[0] = Pack(value.X);
        target[1] = Pack(value.Y);
        target[2] = Pack(value.Z);
    }

    static byte Pack(float value) => (byte)Math.Clamp(MathF.Round(value * 127f) + 127f, 0f, 255f);

    public static int[] ReadTriangles(Mesh mesh)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var triangles = new List<int>();
        foreach (var range in mesh.Data.Standard)
            triangles.AddRange(ReadTriangles(mesh, range));

        return [.. triangles];
    }

    public static int[] ReadTriangles(Mesh mesh, MeshPrimitive range)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(range);

        if (range.StartIndex < 0 || range.TriangleCount < 0)
            throw new InvalidDataException($"The mesh primitive has an invalid index range (start {range.StartIndex}, triangles {range.TriangleCount}).");

        int indexSize = mesh.Indices32Bit ? 4 : 2;
        long requiredBytes = ((long)range.StartIndex + (long)range.TriangleCount * 3) * indexSize;

        if (requiredBytes > mesh.IndexBuffer.Length)
            throw new InvalidDataException($"The mesh primitive needs {requiredBytes} index-buffer bytes, but only {mesh.IndexBuffer.Length} are available.");

        int offset = mesh.Geometry == GeometryKind.Clustered ? range.MinIndex : 0;
        var indices = new int[range.TriangleCount * 3];

        for (int i = 0; i < indices.Length; i++)
            indices[i] = offset + ReadIndex(mesh, range.StartIndex + i);

        return indices;
    }

    static int ReadIndex(Mesh mesh, int position) => mesh.Indices32Bit
        ? BitConverter.ToInt32(mesh.IndexBuffer, position * 4)
        : BitConverter.ToUInt16(mesh.IndexBuffer, position * 2);
}
