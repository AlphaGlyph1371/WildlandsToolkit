using System.Buffers.Binary;
using Wildlands.Formats.Compression;

namespace Wildlands.Formats.Forge;

public static class PrefetchingFileInfos
{
    const ulong Magic = 0x1004FA9957FBAA33;
    const int RecordSize = 16;

    public static byte[] ReadObjectBlock(byte[] data, ulong id)
    {
        ArgumentNullException.ThrowIfNull(data);
        PrefetchFile file = Read(data);
        PrefetchObject item = file.Objects.SingleOrDefault(item => item.Id == id) ?? throw new InvalidDataException($"PrefetchingFileInfos has no object for Forge entry 0x{id:X16}.");
        return file.BlockData.AsSpan(item.Offset, item.Size).ToArray();
    }

    public static byte[] AddObjects(byte[] data, IReadOnlyList<(ulong Id, byte[] Block)> additions)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(additions);
        if (additions.Count == 0)
            return (byte[])data.Clone();

        PrefetchFile file = Read(data);
        var ids = file.Objects.Select(item => item.Id).ToHashSet();
        foreach ((ulong id, byte[] block) in additions)
        {
            if (id == 0 || block is null || block.Length == 0)
                throw new InvalidDataException("A PrefetchingFileInfos addition is incomplete.");
            if (!ids.Add(id))
                throw new InvalidDataException($"PrefetchingFileInfos already contains object 0x{id:X16}.");
        }

        using var blockData = new MemoryStream(file.BlockData.Length + additions.Sum(item => item.Block.Length));
        blockData.Write(file.BlockData);
        var objects = new List<PrefetchObject>(file.Objects);
        foreach ((ulong id, byte[] block) in additions)
        {
            int offset = checked((int)blockData.Position);
            blockData.Write(block);
            objects.Add(new PrefetchObject(id, block.Length, offset));
        }

        using var decompressed = new MemoryStream();
        using (var writer = new BinaryWriter(decompressed, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(objects.Count);
            foreach (PrefetchObject item in objects)
            {
                writer.Write(item.Id);
                writer.Write(item.Size);
                writer.Write(item.Offset);
            }

            byte[] blocks = blockData.ToArray();
            writer.Write(blocks.Length);
            writer.Write(blocks);
        }

        return Write(file.Version, file.Algorithm, file.BlockSize, decompressed.ToArray());
    }

    public static byte[] RemoveObjects(byte[] data, IReadOnlyCollection<ulong> ids)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
            return (byte[])data.Clone();

        PrefetchFile file = Read(data);
        HashSet<ulong> removed = ids.ToHashSet();
        List<PrefetchObject> kept = file.Objects.Where(item => !removed.Contains(item.Id)).ToList();
        if (kept.Count == file.Objects.Count)
            return (byte[])data.Clone();

        using var blocks = new MemoryStream();
        var rewritten = new List<PrefetchObject>(kept.Count);
        foreach (PrefetchObject item in kept)
        {
            int offset = checked((int)blocks.Position);
            blocks.Write(file.BlockData, item.Offset, item.Size);
            rewritten.Add(new PrefetchObject(item.Id, item.Size, offset));
        }

        using var decompressed = new MemoryStream();
        using (var writer = new BinaryWriter(decompressed, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(rewritten.Count);
            foreach (PrefetchObject item in rewritten)
            {
                writer.Write(item.Id);
                writer.Write(item.Size);
                writer.Write(item.Offset);
            }

            byte[] blockData = blocks.ToArray();
            writer.Write(blockData.Length);
            writer.Write(blockData);
        }

        return Write(file.Version, file.Algorithm, file.BlockSize, decompressed.ToArray());
    }

    static PrefetchFile Read(ReadOnlySpan<byte> data)
    {
        if (data.Length < 15)
            throw new InvalidDataException("PrefetchingFileInfos is truncated.");
        if (BinaryPrimitives.ReadUInt64LittleEndian(data) != Magic)
            throw new InvalidDataException("PrefetchingFileInfos has an invalid magic value.");

        short version = BinaryPrimitives.ReadInt16LittleEndian(data[8..]);
        byte algorithm = data[10];
        int blockSize = BinaryPrimitives.ReadInt32LittleEndian(data[11..]);
        if (blockSize <= 0 || blockSize > 16 * 1024 * 1024)
            throw new InvalidDataException($"PrefetchingFileInfos has invalid block size {blockSize}.");

        using var decoded = new MemoryStream();
        int position = 15;
        while (position < data.Length)
        {
            bool compressed = ReadByte(data, ref position) != 0;
            int storedSize = ReadInt32(data, ref position);
            if (storedSize < 0 || storedSize > data.Length - position)
                throw new InvalidDataException("A PrefetchingFileInfos block is truncated.");

            if (compressed)
            {
                int unpackedSize = ReadInt32(data, ref position);
                ReadUInt32(data, ref position);
                if (storedSize > data.Length - position || unpackedSize < 0 || unpackedSize > blockSize)
                    throw new InvalidDataException("A compressed PrefetchingFileInfos block is invalid.");
                byte[] unpacked = Lzo.Decompress(data.Slice(position, storedSize), unpackedSize);
                decoded.Write(unpacked);
            }
            else
            {
                decoded.Write(data.Slice(position, storedSize));
            }
            position += storedSize;
        }

        ReadOnlySpan<byte> body = decoded.ToArray();
        int bodyPosition = 0;
        int count = ReadInt32(body, ref bodyPosition);
        if (count < 0 || count > (body.Length - bodyPosition) / RecordSize)
            throw new InvalidDataException("PrefetchingFileInfos has an invalid object count.");

        var objects = new List<PrefetchObject>(count);
        var ids = new HashSet<ulong>();
        for (int i = 0; i < count; i++)
        {
            ulong id = ReadUInt64(body, ref bodyPosition);
            int size = ReadInt32(body, ref bodyPosition);
            int offset = ReadInt32(body, ref bodyPosition);
            if (!ids.Add(id))
                throw new InvalidDataException($"PrefetchingFileInfos contains duplicate object 0x{id:X16}.");
            objects.Add(new PrefetchObject(id, size, offset));
        }

        int dataLength = ReadInt32(body, ref bodyPosition);
        if (dataLength < 0 || dataLength != body.Length - bodyPosition)
            throw new InvalidDataException("PrefetchingFileInfos has an invalid block-data length.");
        byte[] blockData = body.Slice(bodyPosition, dataLength).ToArray();
        foreach (PrefetchObject item in objects)
        {
            if (item.Size < 0 || item.Offset < 0 || item.Offset > blockData.Length - item.Size)
                throw new InvalidDataException($"Prefetch object 0x{item.Id:X16} points outside the block-data area.");
        }

        return new PrefetchFile(version, algorithm, blockSize, objects, blockData);
    }

    static byte[] Write(short version, byte algorithm, int blockSize, byte[] data)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(Magic);
        writer.Write(version);
        writer.Write(algorithm);
        writer.Write(blockSize);

        for (int offset = 0; offset < data.Length; offset += blockSize)
        {
            ReadOnlySpan<byte> block = data.AsSpan(offset, Math.Min(blockSize, data.Length - offset));
            byte[] compressed = Lzo.Compress(block);
            if (compressed.Length < block.Length)
            {
                writer.Write(true);
                writer.Write(compressed.Length);
                writer.Write(block.Length);
                writer.Write(Crc32(compressed));
                writer.Write(compressed);
            }
            else
            {
                writer.Write(false);
                writer.Write(block.Length);
                writer.Write(block);
            }
        }

        return output.ToArray();
    }

    static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(crc & 1));
        }
        return ~crc;
    }

    static byte ReadByte(ReadOnlySpan<byte> data, ref int position)
    {
        Ensure(data, position, 1);
        return data[position++];
    }

    static int ReadInt32(ReadOnlySpan<byte> data, ref int position)
    {
        Ensure(data, position, sizeof(int));
        int value = BinaryPrimitives.ReadInt32LittleEndian(data[position..]);
        position += sizeof(int);
        return value;
    }

    static uint ReadUInt32(ReadOnlySpan<byte> data, ref int position)
    {
        Ensure(data, position, sizeof(uint));
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data[position..]);
        position += sizeof(uint);
        return value;
    }

    static ulong ReadUInt64(ReadOnlySpan<byte> data, ref int position)
    {
        Ensure(data, position, sizeof(ulong));
        ulong value = BinaryPrimitives.ReadUInt64LittleEndian(data[position..]);
        position += sizeof(ulong);
        return value;
    }

    static void Ensure(ReadOnlySpan<byte> data, int position, int count)
    {
        if (position < 0 || count < 0 || position > data.Length - count)
            throw new InvalidDataException("PrefetchingFileInfos is truncated.");
    }

    sealed record PrefetchFile(short Version, byte Algorithm, int BlockSize, List<PrefetchObject> Objects, byte[] BlockData);
    sealed record PrefetchObject(ulong Id, int Size, int Offset);
}
