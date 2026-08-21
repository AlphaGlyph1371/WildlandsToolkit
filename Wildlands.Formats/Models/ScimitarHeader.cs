using System.IO;

namespace Wildlands.Formats.Models;

// Every embedded object starts with its own id and the hash of its class
public readonly record struct ScimitarHeader(ulong Id, uint ClassHash)
{
    public static ScimitarHeader Read(BinaryReader reader, uint expected = 0)
    {
        var header = new ScimitarHeader(reader.ReadUInt64(), reader.ReadUInt32());

        if (expected != 0 && header.ClassHash != expected)
            throw new InvalidDataException($"Expected class 0x{expected:X8}, found 0x{header.ClassHash:X8}.");

        return header;
    }
}
