using System.Buffers.Binary;
using System.Numerics;

namespace Wildlands.Formats.Models;

public enum AnimationChannel
{
    Rotation,
    Translation,
    Scalar,
    Unknown,
}

public static class AnimationValues
{
    public const int TimeUnitsPerSecond = 60;

    public static AnimationChannel ChannelOf(int valueFormat) => valueFormat switch
    {
        0 or 1 or 2 or 5 => AnimationChannel.Rotation,
        6 => AnimationChannel.Translation,
        4 => AnimationChannel.Scalar,
        _ => AnimationChannel.Unknown,
    };

    public static bool CanRead(int valueFormat) => valueFormat is 0 or 1 or 5 or 6;

    public static Quaternion ReadRotation(ReadOnlySpan<byte> key, int valueFormat)
    {
        switch (valueFormat)
        {
            case 0:
            {
                uint word = BinaryPrimitives.ReadUInt32LittleEndian(key);
                float Field(int shift) => (((word >> shift) & 0x3FF) - 511f) * MathF.Sqrt(2f) / 1024f;
                return Rebuild(Field(20), Field(10), Field(0), (int)(word >> 30));
            }

            case 1:
            {
                ushort first = BinaryPrimitives.ReadUInt16LittleEndian(key);
                ushort second = BinaryPrimitives.ReadUInt16LittleEndian(key[2..]);
                ushort third = BinaryPrimitives.ReadUInt16LittleEndian(key[4..]);
                static float Field(ushort slot) => ((slot & 0x7FFF) - 16383f) * MathF.Sqrt(2f) / 32768f;
                return Rebuild(Field(first), Field(second), Field(third),
                    (first >> 15 & 1) | (second >> 15 & 1) << 1);
            }

            case 5:
            {
                uint first = BinaryPrimitives.ReadUInt32LittleEndian(key);
                uint second = BinaryPrimitives.ReadUInt32LittleEndian(key[4..]);
                uint third = BinaryPrimitives.ReadUInt32LittleEndian(key[8..]);
                static float Field(uint bits) => BitConverter.UInt32BitsToSingle(bits & ~1u);
                return Rebuild(Field(first), Field(second), Field(third),
                    (int)(first & 1) | (int)(second & 1) << 1);
            }

            default:
                throw new NotSupportedException($"Animation value format {valueFormat} is not a readable rotation.");
        }
    }

    public static Vector3 ReadTranslation(ReadOnlySpan<byte> key, int valueFormat)
    {
        if (valueFormat != 6)
            throw new NotSupportedException($"Animation value format {valueFormat} is not a readable translation.");
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(key),
            BinaryPrimitives.ReadSingleLittleEndian(key[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(key[8..]));
    }

    public static IReadOnlyList<int> ReadTimes(AnimationTrack track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var times = new int[track.KeyCount];
        switch (track.TimeFormat)
        {
            case 0:
                for (int i = 1; i < track.KeyCount; i++)
                    times[i] = track.Times[i - 1];
                break;
            case 1:
                for (int i = 1; i < track.KeyCount; i++)
                    times[i] = BinaryPrimitives.ReadUInt16LittleEndian(track.Times.AsSpan((i - 1) * 2));
                break;
            default:
                throw new NotSupportedException($"Animation time format {track.TimeFormat} is not readable.");
        }

        return times;
    }

    static Quaternion Rebuild(float first, float second, float third, int omitted)
    {
        float rest = 1f - (first * first + second * second + third * third);
        float value = rest > 0f ? MathF.Sqrt(rest) : 0f;
        return omitted switch
        {
            0 => new Quaternion(value, first, second, third),
            1 => new Quaternion(first, value, second, third),
            2 => new Quaternion(first, second, value, third),
            _ => new Quaternion(first, second, third, value),
        };
    }
}
