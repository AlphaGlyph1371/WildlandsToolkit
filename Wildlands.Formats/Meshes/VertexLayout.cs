using System.IO;

namespace Wildlands.Formats.Models;

public sealed class VertexLayout
{
    public const int PositionAt = 0;
    public const int ScaleAt = 6;
    public const int NormalAt = 8;
    public const int TangentAt = 12;

    VertexLayout(int format, int stride, int binormalAt, int colorAt, int[] uvAt, int jointsAt, int weightsAt, int jointsPerVertex)
    {
        Format = format;
        Stride = stride;
        BinormalAt = binormalAt;
        ColorAt = colorAt;
        UvAt = uvAt;
        JointsAt = jointsAt;
        WeightsAt = weightsAt;
        JointsPerVertex = jointsPerVertex;
    }

    public int Format { get; }
    public int Stride { get; }
    public int BinormalAt { get; }
    public int ColorAt { get; }
    public int[] UvAt { get; }
    public int JointsAt { get; }
    public int WeightsAt { get; }
    public int JointsPerVertex { get; }

    public bool HasBinormal => BinormalAt >= 0;
    public bool HasColor => ColorAt >= 0;
    public bool IsSkinned => JointsAt >= 0;
    public int UvCount => UvAt.Length;

    public static VertexLayout For(int format, int stride)
    {
        var layout = For(format);

        if (layout.Stride != stride)
            throw new InvalidDataException($"Vertex format {format} always has stride {layout.Stride}, but this mesh states {stride}.");

        return layout;
    }

    public static VertexLayout For(int format) => format switch
    {
        0 => new VertexLayout(0, 32, binormalAt: 16, colorAt: -1, uvAt: [20], jointsAt: 24, weightsAt: 28, jointsPerVertex: 4),
        1 => new VertexLayout(1, 40, binormalAt: 16, colorAt: -1, uvAt: [20], jointsAt: 24, weightsAt: 32, jointsPerVertex: 8),
        2 => new VertexLayout(2, 44, binormalAt: 16, colorAt: 40, uvAt: [20, 24, 28], jointsAt: 32, weightsAt: 36, jointsPerVertex: 4),
        3 => new VertexLayout(3, 52, binormalAt: 16, colorAt: 48, uvAt: [20, 24, 28], jointsAt: 32, weightsAt: 40, jointsPerVertex: 8),
        6 => new VertexLayout(6, 24, binormalAt: -1, colorAt: 16, uvAt: [20], jointsAt: -1, weightsAt: -1, jointsPerVertex: 0),
        7 => new VertexLayout(7, 28, binormalAt: -1, colorAt: 16, uvAt: [20, 24], jointsAt: -1, weightsAt: -1, jointsPerVertex: 0),
        8 => new VertexLayout(8, 20, binormalAt: -1, colorAt: -1, uvAt: [16], jointsAt: -1, weightsAt: -1, jointsPerVertex: 0),
        9 => new VertexLayout(9, 24, binormalAt: -1, colorAt: -1, uvAt: [16, 20], jointsAt: -1, weightsAt: -1, jointsPerVertex: 0),
        _ => throw new NotSupportedException($"Vertex format {format} has not been decoded yet."),
    };

    public static bool IsKnown(int format) => format is 0 or 1 or 2 or 3 or 6 or 7 or 8 or 9;
}
