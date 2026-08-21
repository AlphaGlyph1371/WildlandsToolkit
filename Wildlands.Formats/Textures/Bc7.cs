using System;
using System.Linq;

namespace Wildlands.Formats.Textures;

public static class Bc7
{
    readonly record struct Mode(
        int Subsets, int PartitionBits, int RotationBits, int IndexSelectionBits,
        int ColorBits, int AlphaBits, int EndpointPBits, int SharedPBits,
        int IndexBits, int IndexBits2);

    static readonly Mode[] Modes =
    [
        new(3, 4, 0, 0, 4, 0, 1, 0, 3, 0),
        new(2, 6, 0, 0, 6, 0, 0, 1, 3, 0),
        new(3, 6, 0, 0, 5, 0, 0, 0, 2, 0),
        new(2, 6, 0, 0, 7, 0, 1, 0, 2, 0),
        new(1, 0, 2, 1, 5, 6, 0, 0, 2, 3),
        new(1, 0, 2, 0, 7, 8, 0, 0, 2, 2),
        new(1, 0, 0, 0, 7, 7, 1, 0, 4, 0),
        new(2, 6, 0, 0, 5, 5, 1, 0, 2, 0),
    ];

    static readonly int[] Weights2 = [0, 21, 43, 64];
    static readonly int[] Weights3 = [0, 9, 18, 27, 37, 46, 55, 64];
    static readonly int[] Weights4 = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    static readonly byte[][] Partitions2 = BuildPartitions2();
    static readonly byte[][] Partitions3 = BuildPartitions3();

    // The anchor of a subset is its first pixel, so these follow from the tables above.
    static readonly byte[] Anchors2 = BuildAnchors(Partitions2, 1);
    static readonly byte[] Anchors3Second = BuildAnchors(Partitions3, 1);
    static readonly byte[] Anchors3Third = BuildAnchors(Partitions3, 2);

    static byte[] BuildAnchors(byte[][] partitions, int subset)
    {
        var anchors = new byte[partitions.Length];
        for (int p = 0; p < partitions.Length; p++)
            anchors[p] = (byte)Array.IndexOf(partitions[p], (byte)subset);
        return anchors;
    }

    public static void DecodeBlock(ReadOnlySpan<byte> block, Span<byte> pixels)
    {
        var bits = new BitReader(block);

        int mode = 0;
        while (mode < 8 && bits.Read(1) == 0)
            mode++;

        if (mode >= 8)
        {
            pixels.Clear();
            return;
        }

        var m = Modes[mode];

        int partition = m.PartitionBits > 0 ? bits.Read(m.PartitionBits) : 0;
        int rotation = m.RotationBits > 0 ? bits.Read(m.RotationBits) : 0;
        int indexSelection = m.IndexSelectionBits > 0 ? bits.Read(m.IndexSelectionBits) : 0;

        int endpointCount = m.Subsets * 2;
        var red = new int[endpointCount];
        var green = new int[endpointCount];
        var blue = new int[endpointCount];
        var alpha = new int[endpointCount];

        for (int i = 0; i < endpointCount; i++) red[i] = bits.Read(m.ColorBits);
        for (int i = 0; i < endpointCount; i++) green[i] = bits.Read(m.ColorBits);
        for (int i = 0; i < endpointCount; i++) blue[i] = bits.Read(m.ColorBits);

        if (m.AlphaBits > 0)
            for (int i = 0; i < endpointCount; i++) alpha[i] = bits.Read(m.AlphaBits);
        else
            for (int i = 0; i < endpointCount; i++) alpha[i] = 255;

        var pbit = new int[endpointCount];
        if (m.EndpointPBits > 0)
        {
            for (int i = 0; i < endpointCount; i++)
                pbit[i] = bits.Read(1);
        }
        else if (m.SharedPBits > 0)
        {
            for (int i = 0; i < m.Subsets; i++)
            {
                int shared = bits.Read(1);
                pbit[i * 2] = shared;
                pbit[i * 2 + 1] = shared;
            }
        }

        int colorBits = m.ColorBits + (m.EndpointPBits + m.SharedPBits > 0 ? 1 : 0);
        int alphaBits = m.AlphaBits + (m.AlphaBits > 0 && m.EndpointPBits + m.SharedPBits > 0 ? 1 : 0);

        for (int i = 0; i < endpointCount; i++)
        {
            if (m.EndpointPBits + m.SharedPBits > 0)
            {
                red[i] = (red[i] << 1) | pbit[i];
                green[i] = (green[i] << 1) | pbit[i];
                blue[i] = (blue[i] << 1) | pbit[i];
                if (m.AlphaBits > 0)
                    alpha[i] = (alpha[i] << 1) | pbit[i];
            }

            red[i] = Expand(red[i], colorBits);
            green[i] = Expand(green[i], colorBits);
            blue[i] = Expand(blue[i], colorBits);
            if (m.AlphaBits > 0)
                alpha[i] = Expand(alpha[i], alphaBits);
        }

        var subsetOf = m.Subsets switch
        {
            2 => Partitions2[partition],
            3 => Partitions3[partition],
            _ => null,
        };

        var indices = ReadIndices(ref bits, m.IndexBits, m.Subsets, partition, subsetOf);
        var indices2 = m.IndexBits2 > 0
            ? ReadIndices(ref bits, m.IndexBits2, 1, 0, null)
            : null;

        int[] weights = WeightsFor(m.IndexBits);
        int[] weights2 = m.IndexBits2 > 0 ? WeightsFor(m.IndexBits2) : weights;

        // With two index sets the selection bit decides which one drives colour.
        var colorIndices = indices;
        var colorWeights = weights;
        var alphaIndices = indices;
        var alphaWeights = weights;

        if (indices2 is not null)
        {
            if (indexSelection == 0)
            {
                alphaIndices = indices2;
                alphaWeights = weights2;
            }
            else
            {
                colorIndices = indices2;
                colorWeights = weights2;
            }
        }

        for (int i = 0; i < 16; i++)
        {
            int subset = subsetOf?[i] ?? 0;
            int e0 = subset * 2;
            int e1 = e0 + 1;

            int cw = colorWeights[colorIndices[i]];
            int aw = alphaWeights[alphaIndices[i]];

            byte r = (byte)Interpolate(red[e0], red[e1], cw);
            byte g = (byte)Interpolate(green[e0], green[e1], cw);
            byte b = (byte)Interpolate(blue[e0], blue[e1], cw);
            byte a = (byte)Interpolate(alpha[e0], alpha[e1], aw);

            if (rotation == 1) (r, a) = (a, r);
            else if (rotation == 2) (g, a) = (a, g);
            else if (rotation == 3) (b, a) = (a, b);

            pixels[i * 4 + 0] = b;
            pixels[i * 4 + 1] = g;
            pixels[i * 4 + 2] = r;
            pixels[i * 4 + 3] = a;
        }
    }

    static int[] ReadIndices(ref BitReader bits, int indexBits, int subsets, int partition, byte[]? subsetOf)
    {
        var indices = new int[16];

        for (int i = 0; i < 16; i++)
        {
            bool anchor = i == 0
                || (subsets == 2 && i == Anchors2[partition])
                || (subsets == 3 && (i == Anchors3Second[partition] || i == Anchors3Third[partition]));

            indices[i] = bits.Read(anchor ? indexBits - 1 : indexBits);
        }

        return indices;
    }

    static int[] WeightsFor(int indexBits) => indexBits switch
    {
        2 => Weights2,
        3 => Weights3,
        _ => Weights4,
    };

    static int Interpolate(int a, int b, int weight)
    {
        return (a * (64 - weight) + b * weight + 32) >> 6;
    }

    static int Expand(int value, int bits)
    {
        if (bits >= 8)
            return value;

        return (value << (8 - bits)) | (value >> (2 * bits - 8 > 0 ? 2 * bits - 8 : 0));
    }

    ref struct BitReader(ReadOnlySpan<byte> data)
    {
        readonly ReadOnlySpan<byte> _data = data;
        int _position;

        public int Read(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int index = _position >> 3;
                if (index >= _data.Length)
                    break;

                value |= ((_data[index] >> (_position & 7)) & 1) << i;
                _position++;
            }
            return value;
        }
    }

    static byte[][] BuildPartitions2()
    {
        string[] rows =
        [
            "0000000011111111", "0001000100010001", "0111011101110111", "0001001100111111",
            "0000000100011111", "0011011101111111", "0001001101111111", "0000000100111111",
            "0000000000011111", "0011011111111111", "0000000101111111", "0000000000011111",
            "0001011111111111", "0000000011111111", "0000111111111111", "0000000000001111",
            "0000100011101111", "0111000100000000", "0000000001110111", "0011000100010000",
            "0000100011001110", "0000000010001100", "0111001100010000", "0011000100000000",
            "0000100010001100", "0011011001101100", "0001011111101000", "0000111100110011",
            "0110110011001001", "0011110011000011", "0111111010000001", "0001111111111000",
            "0000111111110000", "0111000110001110", "0011100110011100", "0101010101010101",
            "0000111100001111", "0101101001011010", "0011001111001100", "0011110000111100",
            "0101010110101010", "0110100101101001", "0101101010100101", "0111001111001110",
            "0001001111001000", "0011001001001100", "0011101111011100", "0110100110010110",
            "0110011010010110", "0000011001100000", "0100111001000000", "0010011100100000",
            "0000001001110010", "0000010011100100", "0110110010010011", "0011011011001001",
            "0110001110011100", "0011100111000110", "0110110011001001", "0110001100111001",
            "0111111010000001", "0001100011100111", "0000111100110011", "0011001111110000",
        ];

        return rows.Select(ToDigits).ToArray();
    }

    static byte[][] BuildPartitions3()
    {
        string[] rows =
        [
            "0001110122222222", "0000011101111122", "0000000001112222", "0011001100110022",
            "0000112201122222", "0011001122002200", "0000112201110122", "0011001101122200",
            "0000111100112222", "0000000011112222", "0000000000111122", "0011001122220000",
            "0001010101011122", "0000011101122222", "0000000122221111", "0011001100112200",
            "0001110001111222", "0000110011002200", "0000112211220011", "0000112200112211",
            "0011001100220022", "0000001122110000", "0011001122001100", "0011110000112200",
            "0011001100001122", "0001220001220012", "0000111102220222", "0000222201111222",
            "0011002211000022", "0011220011001122", "0011002200112200", "0000111102221111",
            "0011001111220022", "0000012201122200", "0022110000112200", "0022001102201100",
            "0011220000112200", "0011002211220000", "0001112201122200", "0000112201220012",
            "0000112211220011", "0011001100002222", "0011001122220011", "0000222211110000",
            "0000000022221111", "0011001100002222", "0011002200220011", "0000112211001122",
            "0011002200112200", "0000001111222222", "0000111122220000", "0011001122001122",
            "0000112201122200", "0011002211000022", "0001112200011122", "0000111102220111",
            "0011220011002200", "0022001122001122", "0011001100112222", "0000222200111122",
            "0000011102221111", "0011001100220022", "0022110022110011", "0011002200112200",
        ];

        return rows.Select(ToDigits).ToArray();
    }

    static byte[] ToDigits(string row)
    {
        var result = new byte[16];
        for (int i = 0; i < 16; i++)
            result[i] = (byte)(row[i] - '0');
        return result;
    }
}
