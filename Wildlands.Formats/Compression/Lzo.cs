using System;
using System.IO;

namespace Wildlands.Formats.Compression;

public static class Lzo
{
    const int NearDistance = 0x0800;
    const int MidDistance = 0x4000;
    const int MaxDistance = 0xBFFF;

    const int HashBits = 14;
    const int SearchDepth = 16;

    enum Step
    {
        Token,
        ShortMatch,
        Match,
        Trailing,
    }

    public static byte[] Decompress(ReadOnlySpan<byte> input, int outputSize)
    {
        var output = new byte[outputSize];
        Decompress(input, output);
        return output;
    }

    public static byte[] Compress(ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length + data.Length / 7 + 16];
        var recent = new int[1 << HashBits];
        var previous = new int[data.Length];

        int op = 0;
        int ip = 0;
        int anchor = 0;

        while (ip + 3 <= data.Length)
        {
            int length = FindMatch(data, recent, previous, ip, out int distance);
            if (length == 0)
            {
                ip++;
                continue;
            }

            WriteLiterals(data[anchor..ip], output, ref op, anchor == 0);
            WriteMatch(output, ref op, distance, length);

            for (int i = ip + 1; i + 3 <= data.Length && i < ip + length; i++)
            {
                int slot = Hash(data, i);
                previous[i] = recent[slot];
                recent[slot] = i + 1;
            }

            ip += length;
            anchor = ip;
        }

        WriteLiterals(data[anchor..], output, ref op, anchor == 0);

        output[op++] = 0x11;
        output[op++] = 0;
        output[op++] = 0;

        return output.AsSpan(0, op).ToArray();
    }

    static int FindMatch(ReadOnlySpan<byte> data, int[] recent, int[] previous, int position, out int distance)
    {
        int slot = Hash(data, position);
        int candidate = recent[slot] - 1;
        previous[position] = recent[slot];
        recent[slot] = position + 1;

        int best = 0;
        distance = 0;

        for (int tries = 0; tries < SearchDepth && candidate >= 0 && position - candidate <= MaxDistance; tries++)
        {
            if (StartsAlike(data, candidate, position))
            {
                int length = 3;
                while (position + length < data.Length && data[candidate + length] == data[position + length])
                    length++;

                if (length > best)
                {
                    best = length;
                    distance = position - candidate;
                }
            }

            candidate = previous[candidate] - 1;
        }

        return best;
    }

    static int Hash(ReadOnlySpan<byte> data, int position)
    {
        uint value = (uint)(data[position] | (data[position + 1] << 8) | (data[position + 2] << 16));
        return (int)(value * 2654435761u >> (32 - HashBits));
    }

    static bool StartsAlike(ReadOnlySpan<byte> data, int a, int b)
    {
        return data[a] == data[b] && data[a + 1] == data[b + 1] && data[a + 2] == data[b + 2];
    }

    static void WriteLiterals(ReadOnlySpan<byte> literals, byte[] output, ref int op, bool first)
    {
        if (literals.Length == 0)
            return;

        if (first && literals.Length <= 238)
            output[op++] = (byte)(17 + literals.Length);
        else if (literals.Length <= 3)
            output[op - 2] |= (byte)literals.Length;
        else if (literals.Length <= 18)
            output[op++] = (byte)(literals.Length - 3);
        else
        {
            output[op++] = 0;
            WriteLongLength(output, ref op, literals.Length - 18);
        }

        literals.CopyTo(output.AsSpan(op));
        op += literals.Length;
    }

    static void WriteMatch(byte[] output, ref int op, int distance, int length)
    {
        if (length <= 8 && distance <= NearDistance)
        {
            int offset = distance - 1;
            output[op++] = (byte)(((length - 1) << 5) | ((offset & 7) << 2));
            output[op++] = (byte)(offset >> 3);
            return;
        }

        if (distance <= MidDistance)
        {
            int offset = distance - 1;
            if (length <= 33)
                output[op++] = (byte)(32 | (length - 2));
            else
            {
                output[op++] = 32;
                WriteLongLength(output, ref op, length - 33);
            }
            output[op++] = (byte)(offset << 2);
            output[op++] = (byte)(offset >> 6);
            return;
        }

        int far = distance - MidDistance;
        if (length <= 9)
            output[op++] = (byte)(16 | ((far & 0x4000) >> 11) | (length - 2));
        else
        {
            output[op++] = (byte)(16 | ((far & 0x4000) >> 11));
            WriteLongLength(output, ref op, length - 9);
        }
        output[op++] = (byte)(far << 2);
        output[op++] = (byte)(far >> 6);
    }

    static void WriteLongLength(byte[] output, ref int op, int value)
    {
        while (value > 255)
        {
            output[op++] = 0;
            value -= 255;
        }
        output[op++] = (byte)value;
    }

    public static int Decompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        int ip = 0;
        int op = 0;
        int token = 0;
        var step = Step.Token;

        if (input[ip] > 17)
        {
            int count = input[ip++] - 17;
            CopyLiterals(input, ref ip, output, ref op, count);
            if (count < 4)
            {
                token = input[ip++];
                step = Step.Match;
            }
            else
            {
                step = Step.ShortMatch;
            }
        }

        while (true)
        {
            switch (step)
            {
                case Step.Token:
                {
                    token = input[ip++];
                    if (token >= 16)
                    {
                        step = Step.Match;
                        break;
                    }

                    int count = token == 0 ? 15 + ReadLongLength(input, ref ip) : token;
                    CopyLiterals(input, ref ip, output, ref op, count + 3);
                    step = Step.ShortMatch;
                    break;
                }

                case Step.ShortMatch:
                {
                    token = input[ip++];
                    if (token >= 16)
                    {
                        step = Step.Match;
                        break;
                    }

                    int distance = 0x0801 + (token >> 2) + (input[ip++] << 2);
                    CopyMatch(output, ref op, distance, 3);
                    step = Step.Trailing;
                    break;
                }

                case Step.Match:
                {
                    int distance;
                    int length;

                    if (token >= 64)
                    {
                        distance = 1 + ((token >> 2) & 7) + (input[ip++] << 3);
                        length = (token >> 5) + 1;
                    }
                    else if (token >= 32)
                    {
                        length = token & 31;
                        if (length == 0)
                            length = 31 + ReadLongLength(input, ref ip);
                        length += 2;
                        distance = 1 + (ReadUInt16(input, ip) >> 2);
                        ip += 2;
                    }
                    else if (token >= 16)
                    {
                        distance = (token & 8) << 11;
                        length = token & 7;
                        if (length == 0)
                            length = 7 + ReadLongLength(input, ref ip);
                        length += 2;
                        distance += ReadUInt16(input, ip) >> 2;
                        ip += 2;
                        if (distance == 0)
                            return op;
                        distance += 0x4000;
                    }
                    else
                    {
                        distance = 1 + (token >> 2) + (input[ip++] << 2);
                        length = 2;
                    }

                    CopyMatch(output, ref op, distance, length);
                    step = Step.Trailing;
                    break;
                }

                case Step.Trailing:
                {
                    int count = input[ip - 2] & 3;
                    if (count == 0)
                    {
                        step = Step.Token;
                        break;
                    }

                    CopyLiterals(input, ref ip, output, ref op, count);
                    token = input[ip++];
                    step = Step.Match;
                    break;
                }
            }
        }
    }

    static int ReadLongLength(ReadOnlySpan<byte> input, ref int ip)
    {
        int extra = 0;
        while (input[ip] == 0)
        {
            extra += 255;
            ip++;
        }
        return extra + input[ip++];
    }

    static ushort ReadUInt16(ReadOnlySpan<byte> input, int offset)
    {
        return (ushort)(input[offset] | (input[offset + 1] << 8));
    }

    static void CopyLiterals(ReadOnlySpan<byte> input, ref int ip, Span<byte> output, ref int op, int count)
    {
        input.Slice(ip, count).CopyTo(output.Slice(op, count));
        ip += count;
        op += count;
    }

    static void CopyMatch(Span<byte> output, ref int op, int distance, int length)
    {
        int from = op - distance;
        if (from < 0)
            throw new InvalidDataException("LZO match points before the start of the output.");

        for (int i = 0; i < length; i++)
            output[op + i] = output[from + i];

        op += length;
    }
}
