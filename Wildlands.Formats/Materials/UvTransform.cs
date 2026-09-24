using System;
using System.Numerics;

namespace Wildlands.Formats.Materials;

public readonly record struct UvTransform(float ScaleU, float ScaleV, float OffsetU, float OffsetV, float Rotation)
{
    public static readonly UvTransform Identity = new(1, 1, 0, 0, 0);

    public Vector2 Apply(Vector2 uv)
    {
        float u = uv.X * ScaleU;
        float v = uv.Y * ScaleV;

        if (Rotation != 0)
        {
            float cos = MathF.Cos(Rotation);
            float sin = MathF.Sin(Rotation);
            (u, v) = (u * cos - v * sin, u * sin + v * cos);
        }

        return new Vector2(u + OffsetU, v + OffsetV);
    }
}
