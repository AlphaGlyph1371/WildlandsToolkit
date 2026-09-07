using System;
using System.IO;
using Wildlands.Formats.Compression;

namespace Wildlands.Formats.Data;

public struct CompressionInfo
{
    public short Version;
    public byte Algorithm;
    public ushort MaxUncompressedBlockSize;
    public ushort MaxCompressedBlockSize;

    public static CompressionInfo Read(BinaryReader reader) => new()
    {
        Version = reader.ReadInt16(),
        Algorithm = reader.ReadByte(),
        MaxUncompressedBlockSize = reader.ReadUInt16(),
        MaxCompressedBlockSize = reader.ReadUInt16(),
    };

    public void Write(BinaryWriter writer)
    {
        writer.Write(Version);
        writer.Write(Algorithm);
        writer.Write(MaxUncompressedBlockSize);
        writer.Write(MaxCompressedBlockSize);
    }

    // Version 1 containers store block sizes as UInt16 pairs, version 2 and up as Int32 pairs
    public bool HasWideBlockInfo => Version >= 2;
}

public static class CompressedBlob
{
    public const ulong Magic = 0x1004FA9957FBAA33;

    public static byte[] Read(BinaryReader reader) => Read(reader, out _, out _);

    public static byte[] Read(BinaryReader reader, out CompressionInfo info) => Read(reader, out info, out _);

    public static byte[] Read(BinaryReader reader, out CompressionInfo info, out byte[][] blocks)
    {
        if (reader.ReadUInt64() != Magic)
            throw new InvalidDataException("Not a compressed block: magic mismatch.");

        info = CompressionInfo.Read(reader);
        int blockCount = reader.ReadInt32();

        var sizes = new (int Uncompressed, int Compressed)[blockCount];
        for (int i = 0; i < blockCount; i++)
        {
            sizes[i] = info.HasWideBlockInfo ? (reader.ReadInt32(), reader.ReadInt32()) : (reader.ReadUInt16(), reader.ReadUInt16());
        }

        int total = 0;
        foreach (var size in sizes)
            total += size.Uncompressed;

        var output = new byte[total];
        int written = 0;
        blocks = new byte[blockCount][];

        for (int i = 0; i < blockCount; i++)
        {
            var (uncompressed, compressed) = sizes[i];

            uint expected = reader.ReadUInt32();
            var block = reader.ReadBytes(compressed);

            uint actual = Checksum(block);
            if (actual != expected)
                throw new InvalidDataException($"Block checksum is 0x{actual:X8}, the file says 0x{expected:X8}.");

            if (compressed == uncompressed)
                block.CopyTo(output, written);
            else
                Lzo.Decompress(block, output.AsSpan(written, uncompressed));

            blocks[i] = block;
            written += uncompressed;
        }

        return output;
    }

    public static void Write(BinaryWriter writer, byte[] data, CompressionInfo info) => Write(writer, data, info, [], []);

    public static void Write(BinaryWriter writer, byte[] data, CompressionInfo info, byte[] previous, byte[][] previousBlocks)
    {
        int blockSize = info.MaxUncompressedBlockSize;
        int blockCount = Math.Max(1, (data.Length + blockSize - 1) / blockSize);

        var blocks = new byte[blockCount][];
        var lengths = new int[blockCount];

        for (int i = 0; i < blockCount; i++)
        {
            int offset = i * blockSize;
            lengths[i] = Math.Min(blockSize, data.Length - offset);

            var block = data.AsSpan(offset, lengths[i]);
            var stream = Lzo.Compress(block);

            blocks[i] = stream.Length >= lengths[i] ? block.ToArray() : stream;

            if (i < previousBlocks.Length && previousBlocks[i].Length < blocks[i].Length && SameBlock(data, offset, lengths[i], previous, blockSize))
                blocks[i] = previousBlocks[i];
        }

        info.MaxCompressedBlockSize = info.MaxUncompressedBlockSize;

        writer.Write(Magic);
        info.Write(writer);
        writer.Write(blockCount);

        for (int i = 0; i < blockCount; i++)
        {
            if (info.HasWideBlockInfo)
            {
                writer.Write(lengths[i]);
                writer.Write(blocks[i].Length);
            }
            else
            {
                writer.Write((ushort)lengths[i]);
                writer.Write((ushort)blocks[i].Length);
            }
        }

        foreach (var block in blocks)
        {
            writer.Write(Checksum(block));
            writer.Write(block);
        }
    }

    static bool SameBlock(byte[] data, int offset, int length, byte[] previous, int blockSize)
    {
        return offset + length <= previous.Length
            && Math.Min(blockSize, previous.Length - offset) == length
            && data.AsSpan(offset, length).SequenceEqual(previous.AsSpan(offset, length));
    }

    // By default it uses a = 1 but that seems to be wrong and a = 0 works fine so i guess we use that :3
    static uint Checksum(ReadOnlySpan<byte> data)
    {
        uint a = 0, b = 0;
        foreach (byte value in data)
        {
            a = (a + value) % 65521;
            b = (b + a) % 65521;
        }
        return (b << 16) | a;
    }
}
