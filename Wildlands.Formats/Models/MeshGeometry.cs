using System;
using System.Collections.Generic;
using System.Numerics;

namespace Wildlands.Formats.Models;

public readonly record struct MeshVertex(Vector3 Position, Vector3 Normal, Vector2 Uv, byte[] JointIndices, byte[] JointWeights);

public static class MeshGeometry
{
    public static MeshVertex[] ReadVertices(Mesh mesh)
    {
        int stride = mesh.VertexStride;
        var buffer = mesh.VertexBuffer;

        if (stride < 16 || buffer.Length < stride)
            return [];

        var (jointsAt, weightsAt, jointCount) = SkinLayout(stride);
        var vertices = new MeshVertex[buffer.Length / stride];

        for (int i = 0; i < vertices.Length; i++)
        {
            var vertex = buffer.AsSpan(i * stride, stride);

            float scale = stride >= 32
                ? mesh.QuantizationFactor / 32767f
                : BitConverter.ToInt16(vertex[6..]) / 32767f;

            var position = new Vector3(
                BitConverter.ToInt16(vertex) * scale,
                BitConverter.ToInt16(vertex[2..]) * scale,
                BitConverter.ToInt16(vertex[4..]) * scale);

            var normal = new Vector3(
                (vertex[8] - 127) / 127f,
                (vertex[9] - 127) / 127f,
                (vertex[10] - 127) / 127f);

            int uv = stride switch
            {
                16 => 12,
                20 or 24 => 16,
                _ => 20,
            };
            var texture = new Vector2(
                BitConverter.ToInt16(vertex[uv..]) / 32767f * mesh.UvQuantizationFactor,
                BitConverter.ToInt16(vertex[(uv + 2)..]) / 32767f * mesh.UvQuantizationFactor);

            byte[] joints = [];
            byte[] weights = [];

            if (jointCount > 0)
            {
                joints = vertex.Slice(jointsAt, jointCount).ToArray();
                weights = vertex.Slice(weightsAt, jointCount).ToArray();
            }

            vertices[i] = new MeshVertex(position, normal, texture, joints, weights);
        }

        return vertices;
    }

    static (int JointsAt, int WeightsAt, int Count) SkinLayout(int stride) => stride switch
    {
        32 => (24, 28, 4),
        40 => (24, 32, 8),
        44 => (32, 36, 4),
        _ => (0, 0, 0),
    };

    public static int[] ReadTriangles(Mesh mesh)
    {
        if (mesh.Data is null)
            return [];

        return [.. mesh.Data.Standard.SelectMany(range => ReadTriangles(mesh, range))];
    }

    public static int[] ReadTriangles(Mesh mesh, MeshPrimitive range)
    {
        int offset = mesh.Geometry == GeometryKind.Clustered ? range.MinIndex : 0;
        var indices = new int[range.TriangleCount * 3];

        for (int i = 0; i < indices.Length; i++)
            indices[i] = offset + ReadIndex(mesh, range.StartIndex + i);

        return indices;
    }

    static int ReadIndex(Mesh mesh, int position) => mesh.Data!.Indices32Bit ? BitConverter.ToInt32(mesh.IndexBuffer, position * 4) : BitConverter.ToUInt16(mesh.IndexBuffer, position * 2);
}
