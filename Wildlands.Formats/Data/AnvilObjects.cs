using System.Buffers.Binary;

namespace Wildlands.Formats.Data;

public readonly record struct AnvilObject(int Offset, ulong Id, uint ClassHash);

public static class AnvilObjects
{
    public const ulong FirstLocalId = 0xF8000000;
    public const ulong LastLocalId = 0xF8FFFFFF;

    public static IReadOnlyList<AnvilObject> Scan(byte[] data, ulong rootId = 0)
    {
        ArgumentNullException.ThrowIfNull(data);
        var found = new List<AnvilObject>();
        for (int offset = 0; offset + 12 <= data.Length; offset++)
        {
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
            if (!IsLocalId(id) && (offset != 0 || rootId == 0 || id != rootId))
                continue;
            found.Add(new AnvilObject(offset, id,
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 8))));
            offset += 11;
        }

        return found;
    }

    public static IReadOnlyList<AnvilObject> ScanSequence(byte[] data, int start)
    {
        ArgumentNullException.ThrowIfNull(data);
        var found = new List<AnvilObject>();
        int offset = start;
        for (ulong id = FirstLocalId; offset + 12 <= data.Length; id++)
        {
            int at = found.Count == 0
                ? (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset)) == id ? offset : -1)
                : Find(data, offset, id);
            if (at < 0)
                break;
            found.Add(new AnvilObject(at, id,
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 8))));
            offset = at + 12;
        }

        return found;
    }

    public static bool IsLocalId(ulong id) => id is >= FirstLocalId and <= LastLocalId;

    static int Find(byte[] data, int from, ulong id)
    {
        Span<byte> wanted = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(wanted, id);
        for (int offset = from; offset + 12 <= data.Length; offset++)
            if (data.AsSpan(offset, 8).SequenceEqual(wanted))
                return offset;
        return -1;
    }

    public static bool IsContiguous(IReadOnlyList<AnvilObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var local = objects.Where(item => IsLocalId(item.Id)).ToList();
        for (int index = 0; index < local.Count; index++)
            if (local[index].Id != FirstLocalId + (ulong)index)
                return false;
        return local.Count > 0;
    }
}
