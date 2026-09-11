using System.IO;

namespace Wildlands.Formats.Models;

internal static class MeshBinaryReader
{
    const int MaximumItems = 1_000_000;

    public static int ReadCount(BinaryReader reader, string label)
    {
        EnsureRemaining(reader, sizeof(int), label + " count");
        int count = reader.ReadInt32();
        if (count < 0 || count > MaximumItems)
            throw new InvalidDataException($"The {label} count {count} is outside the supported range.");
        return count;
    }

    public static byte[] ReadBytes(BinaryReader reader, string label)
    {
        EnsureRemaining(reader, sizeof(int), label + " byte count");
        int length = reader.ReadInt32();
        if (length < 0)
            throw new InvalidDataException($"The {label} has a negative byte length.");
        EnsureRemaining(reader, length, label);
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length)
            throw new EndOfStreamException($"The {label} ends after {bytes.Length} of {length} bytes.");
        return bytes;
    }

    public static void EnsureRemaining(BinaryReader reader, long length, string label)
    {
        if (length < 0 || reader.BaseStream.CanSeek && length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new EndOfStreamException($"The {label} runs past the end of the Mesh resource.");
    }
}
