using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Wildlands.Formats.Models;

public sealed class LocalizationPackageAsset
{
    public ulong Id { get; internal set; }
    public int Type { get; internal set; }
    public uint Language { get; internal set; }
    public Dictionary<ulong, string> Strings { get; } = [];
}

public static class LocalizationPackage
{
    public const uint ClassHash = 0x6E3C9C6F;
    const uint IndexedDataMarker = 0xD28389B5;
    const int MaximumItems = 16 << 20;

    public static LocalizationPackageAsset Read(byte[] resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        using var reader = new BinaryReader(new MemoryStream(resource, writable: false));

        var result = new LocalizationPackageAsset
        {
            Id = ReadUInt64(reader, "LocalizationPackage id"),
        };
        Expect(ReadUInt32(reader, "LocalizationPackage class"), ClassHash, "LocalizationPackage class");

        _ = ReadByte(reader, "LocalizationPackage flag");
        result.Type = ReadInt32(reader, "LocalizationPackage type");
        result.Language = ReadUInt32(reader, "LocalizationPackage language");
        ReadBytes(reader, 12, "LocalizationPackage reserved data");
        Expect(ReadUInt32(reader, "indexed-data marker"), IndexedDataMarker, "indexed-data marker");
        int dataLength = ReadCount(reader, "indexed-data byte");
        byte[] indexedBytes = ReadBytes(reader, dataLength, "indexed data");

        if (reader.BaseStream.Position != reader.BaseStream.Length)
            throw new InvalidDataException($"LocalizationPackage has {reader.BaseStream.Length - reader.BaseStream.Position} unexplained byte(s)." );

        ReadIndexedData(indexedBytes, result);
        return result;
    }

    public static byte[] CreateSingleString(byte[] templateResource, ulong resourceId, uint stringId, string value)
    {
        ArgumentNullException.ThrowIfNull(templateResource);
        ArgumentNullException.ThrowIfNull(value);
        if (templateResource.Length < 41)
            throw new InvalidDataException("The localization template is too short.");
        if (value.Length == 0 || value.Length > ushort.MaxValue)
            throw new InvalidDataException("The localized name must contain between 1 and 65,535 characters.");
        if (value.Any(char.IsSurrogate) || value.Contains('\0'))
            throw new InvalidDataException("The localized name contains a character this game package cannot encode safely.");

        var characters = value.Distinct().ToList();
        if (characters.Count > 254)
            throw new InvalidDataException("The localized name contains too many distinct characters.");

        int fragmentCount = characters.Count + 1;
        int tableOffset = checked(4 + fragmentCount * 4);
        int entriesOffset = checked(tableOffset + 2 + 12);
        int textOffset = entriesOffset + 4;
        var indexed = new byte[checked(textOffset + value.Length)];
        WriteUInt16BigEndian(indexed, 0, 255);
        WriteUInt16BigEndian(indexed, 2, (ushort)fragmentCount);
        int at = 4;
        at += 4; // Empty fragment: right=0, left=0.
        var fragmentByCharacter = new Dictionary<char, byte>();
        for (int index = 0; index < characters.Count; index++)
        {
            char character = characters[index];
            WriteUInt16BigEndian(indexed, at, character);
            WriteUInt16BigEndian(indexed, at + 2, 0);
            at += 4;
            fragmentByCharacter[character] = (byte)index;
        }

        WriteUInt16BigEndian(indexed, tableOffset, 1);
        WriteUInt32BigEndian(indexed, tableOffset + 2, stringId);
        WriteUInt32BigEndian(indexed, tableOffset + 6, (uint)textOffset);
        WriteUInt32BigEndian(indexed, tableOffset + 10, (uint)entriesOffset);
        WriteUInt16BigEndian(indexed, entriesOffset, 0);
        WriteUInt16BigEndian(indexed, entriesOffset + 2, (ushort)value.Length);
        for (int index = 0; index < value.Length; index++)
            indexed[textOffset + index] = fragmentByCharacter[value[index]];

        var result = new byte[checked(41 + indexed.Length)];
        templateResource.AsSpan(0, 41).CopyTo(result);
        BinaryPrimitives.WriteUInt64LittleEndian(result, resourceId);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), ClassHash);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(33), IndexedDataMarker);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(37), indexed.Length);
        indexed.CopyTo(result.AsSpan(41));

        var checkedPackage = Read(result);
        if (!checkedPackage.Strings.TryGetValue(stringId, out string? checkedValue) || !string.Equals(checkedValue, value, StringComparison.Ordinal))
            throw new InvalidDataException("The generated localization package did not read back exactly.");
        return result;
    }

    public static byte[] AddOrReplaceString(byte[] resource, uint stringId, string value)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(value);
        if (stringId == 0)
            throw new ArgumentOutOfRangeException(nameof(stringId));
        if (value.Length == 0 || value.Length > ushort.MaxValue)
            throw new InvalidDataException("The localized name must contain between 1 and 65,535 characters.");

        LocalizationPackageAsset package = Read(resource);
        var strings = package.Strings.ToDictionary(pair => checked((uint)pair.Key), pair => pair.Value);
        strings[stringId] = value;
        byte[] indexed = WriteIndexedData(strings);

        var result = new byte[checked(41 + indexed.Length)];
        resource.AsSpan(0, 41).CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(33), IndexedDataMarker);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(37), indexed.Length);
        indexed.CopyTo(result.AsSpan(41));

        LocalizationPackageAsset checkedPackage = Read(result);
        if (checkedPackage.Id != package.Id || checkedPackage.Type != package.Type || checkedPackage.Language != package.Language || checkedPackage.Strings.Count != strings.Count
            || strings.Any(pair => !checkedPackage.Strings.TryGetValue(pair.Key, out string? checkedValue) || !string.Equals(checkedValue, pair.Value, StringComparison.Ordinal)))
            throw new InvalidDataException("The extended localization package did not read back exactly.");
        return result;
    }

    static byte[] WriteIndexedData(IReadOnlyDictionary<uint, string> strings)
    {
        const ushort directFragmentCount = 128;
        const int twoByteBias = directFragmentCount * byte.MaxValue;

        var frequency = new Dictionary<char, int>();
        foreach (string value in strings.Values)
        {
            if (value.Contains('\0') || value.Any(char.IsSurrogate))
                throw new InvalidDataException("The localization package contains text that cannot be rebuilt safely.");
            foreach (char character in value)
                frequency[character] = frequency.GetValueOrDefault(character) + 1;
        }

        List<char> characters = frequency
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Select(pair => pair.Key)
            .ToList();
        if (characters.Count >= 32_640 || characters.Count + 1 > ushort.MaxValue)
            throw new InvalidDataException("The localization package contains too many distinct characters.");

        var characterIndexes = characters
            .Select((character, index) => (character, index))
            .ToDictionary(item => item.character, item => item.index);
        var encodedStrings = strings
            .OrderBy(pair => pair.Key)
            .Select(pair => new EncodedString(pair.Key, EncodeText(pair.Value, characterIndexes, directFragmentCount, twoByteBias)))
            .ToList();

        var tables = new List<EncodedTable>();
        foreach (EncodedString item in encodedStrings)
        {
            if (item.Text.Length > ushort.MaxValue)
                throw new InvalidDataException($"Localized string 0x{item.Id:X8} is too long to encode.");

            EncodedTable? table = tables.LastOrDefault();
            if (table is null
                || item.Id - table.Strings[0].Id > ushort.MaxValue
                || table.TextLength + item.Text.Length > ushort.MaxValue
                || table.Strings.Count > ushort.MaxValue)
            {
                table = new EncodedTable();
                tables.Add(table);
            }
            table.Strings.Add(item);
            table.TextLength += item.Text.Length;
        }

        if (tables.Count > ushort.MaxValue)
            throw new InvalidDataException("The localization package needs too many string tables.");

        int fragmentCount = characters.Count + 1;
        int headerLength = checked(4 + fragmentCount * 4 + 2 + tables.Count * 12);
        int totalLength = headerLength;
        foreach (EncodedTable table in tables)
        {
            table.EntriesOffset = totalLength;
            totalLength = checked(totalLength + table.Strings.Count * 4);
            table.TextOffset = totalLength;
            totalLength = checked(totalLength + table.TextLength);
        }

        var data = new byte[totalLength];
        WriteUInt16BigEndian(data, 0, directFragmentCount);
        WriteUInt16BigEndian(data, 2, checked((ushort)fragmentCount));
        int at = 4;
        at += 4; // Empty fragment sentinel
        foreach (char character in characters)
        {
            WriteUInt16BigEndian(data, at, character);
            WriteUInt16BigEndian(data, at + 2, 0);
            at += 4;
        }

        WriteUInt16BigEndian(data, at, checked((ushort)tables.Count));
        at += 2;
        foreach (EncodedTable table in tables)
        {
            WriteUInt32BigEndian(data, at, table.Strings[0].Id);
            WriteUInt32BigEndian(data, at + 4, checked((uint)table.TextOffset));
            WriteUInt32BigEndian(data, at + 8, checked((uint)table.EntriesOffset));
            at += 12;

            int entryAt = table.EntriesOffset;
            WriteUInt16BigEndian(data, entryAt, checked((ushort)(table.Strings.Count - 1)));
            entryAt += 2;
            int textAt = table.TextOffset;
            int consumed = 0;
            uint firstId = table.Strings[0].Id;
            for (int index = 0; index < table.Strings.Count; index++)
            {
                EncodedString item = table.Strings[index];
                if (index > 0)
                {
                    WriteUInt16BigEndian(data, entryAt, checked((ushort)(item.Id - firstId)));
                    entryAt += 2;
                }
                item.Text.CopyTo(data, textAt);
                textAt += item.Text.Length;
                consumed += item.Text.Length;
                WriteUInt16BigEndian(data, entryAt, checked((ushort)consumed));
                entryAt += 2;
            }
        }
        return data;
    }

    static byte[] EncodeText(string value, IReadOnlyDictionary<char, int> characterIndexes, int directFragmentCount, int twoByteBias)
    {
        using var output = new MemoryStream(value.Length);
        foreach (char character in value)
        {
            int index = characterIndexes[character];
            if (index < directFragmentCount)
            {
                output.WriteByte((byte)index);
                continue;
            }

            int encoded = checked(index + twoByteBias);
            int first = encoded >> 8;
            if (first >= byte.MaxValue)
                throw new InvalidDataException("A localization fragment index exceeds the compact encoding.");
            output.WriteByte((byte)first);
            output.WriteByte((byte)encoded);
        }
        return output.ToArray();
    }

    static void WriteUInt16BigEndian(Span<byte> data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(data[offset..], value);

    static void WriteUInt32BigEndian(Span<byte> data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data[offset..], value);

    static void ReadIndexedData(byte[] data, LocalizationPackageAsset result)
    {
        var reader = new BigEndianReader(data);
        ushort maxIndexSize = reader.ReadUInt16("maximum short index");
        int indexMask = maxIndexSize * 255;
        int fragmentCount = reader.ReadUInt16("string fragment count");
        CheckCount(fragmentCount, "string fragment");

        var fragments = new Fragment[fragmentCount];
        for (int i = 0; i < fragments.Length; i++)
            fragments[i] = new Fragment(
                reader.ReadUInt16($"fragment {i} right value"),
                reader.ReadUInt16($"fragment {i} left value"));

        var decoded = new string?[fragmentCount];
        var visiting = new bool[fragmentCount];
        for (int i = 0; i < fragments.Length; i++)
            DecodeFragment(i, fragments, decoded, visiting);

        int tableCount = reader.ReadUInt16("string table count");
        CheckCount(tableCount, "string table");
        var tables = new Table[tableCount];
        for (int i = 0; i < tables.Length; i++)
            tables[i] = new Table(
                reader.ReadUInt32($"string table {i} first id"),
                reader.ReadUInt32($"string table {i} text offset"),
                reader.ReadUInt32($"string table {i} entry offset"));

        foreach (var table in tables)
            ReadTable(reader, table, maxIndexSize, indexMask, decoded, result.Strings);
    }

    static void ReadTable(BigEndianReader reader, Table table, ushort maxIndexSize, int indexMask, IReadOnlyList<string?> fragments, Dictionary<ulong, string> output)
    {
        reader.Position = table.EntriesOffset;
        int additionalCount = reader.ReadUInt16("additional string entry count");
        CheckCount(additionalCount, "string entry");
        var entries = new Entry[additionalCount + 1];
        entries[0] = new Entry(table.FirstId, reader.ReadUInt16("first string end offset"));
        for (int i = 1; i < entries.Length; i++)
            entries[i] = new Entry(table.FirstId + reader.ReadUInt16($"string {i} id delta"), reader.ReadUInt16($"string {i} end offset"));

        reader.Position = table.TextOffset;
        int consumedCodes = 0;
        foreach (var entry in entries)
        {
            if (entry.EndOffset < consumedCodes)
                throw new InvalidDataException("Localization string offsets are not monotonic.");

            var text = new StringBuilder();
            while (consumedCodes < entry.EndOffset)
            {
                byte first = reader.ReadByte("string fragment index");
                consumedCodes++;
                int index;
                if (first < maxIndexSize)
                {
                    index = first;
                }
                else if (first == byte.MaxValue)
                {
                    index = reader.ReadInt16("long string fragment index");
                    consumedCodes += 2;
                }
                else
                {
                    index = ((first << 8) | reader.ReadByte("wide string fragment index")) - indexMask;
                    consumedCodes++;
                }

                int fragmentIndex = checked(index + 1);
                if ((uint)fragmentIndex >= (uint)fragments.Count)
                    throw new InvalidDataException($"String fragment index {fragmentIndex} is outside the table.");
                text.Append(fragments[fragmentIndex]);
            }
            output[entry.Id] = text.ToString();
        }
    }

    static string DecodeFragment(int index, IReadOnlyList<Fragment> fragments, string?[] decoded, bool[] visiting)
    {
        if (decoded[index] is not null)
            return decoded[index]!;
        if (visiting[index])
            throw new InvalidDataException($"Localization fragment {index} contains a cycle.");
        visiting[index] = true;

        var fragment = fragments[index];
        string value;
        if (fragment.Left == 0 && fragment.Right == 0)
            value = "";
        else if (fragment.Left == 0)
            value = char.ConvertFromUtf32(fragment.Right);
        else
        {
            if (fragment.Left >= fragments.Count || fragment.Right >= fragments.Count)
                throw new InvalidDataException($"Localization fragment {index} points outside the fragment table.");
            value = DecodeFragment(fragment.Left, fragments, decoded, visiting) + DecodeFragment(fragment.Right, fragments, decoded, visiting);
        }

        visiting[index] = false;
        decoded[index] = value;
        return value;
    }

    static void CheckCount(int count, string field)
    {
        if (count < 0 || count > MaximumItems)
            throw new InvalidDataException($"Invalid {field} count {count}.");
    }

    static byte ReadByte(BinaryReader reader, string field)
    {
        if (reader.BaseStream.Position >= reader.BaseStream.Length)
            throw new EndOfStreamException($"Unexpected end while reading {field}.");
        return reader.ReadByte();
    }

    static byte[] ReadBytes(BinaryReader reader, int count, string field)
    {
        if (count < 0 || reader.BaseStream.Length - reader.BaseStream.Position < count)
            throw new EndOfStreamException($"Unexpected end while reading {field}.");
        return reader.ReadBytes(count);
    }

    static int ReadCount(BinaryReader reader, string field)
    {
        int value = ReadInt32(reader, field + " count");
        CheckCount(value, field);
        return value;
    }

    static int ReadInt32(BinaryReader reader, string field) =>
        BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(reader, 4, field));
    static uint ReadUInt32(BinaryReader reader, string field) =>
        BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(reader, 4, field));
    static ulong ReadUInt64(BinaryReader reader, string field) =>
        BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(reader, 8, field));

    static void Expect(uint actual, uint expected, string field)
    {
        if (actual != expected)
            throw new InvalidDataException($"{field} is 0x{actual:X8}, expected 0x{expected:X8}.");
    }

    readonly record struct Fragment(ushort Right, ushort Left);
    readonly record struct Table(uint FirstId, uint TextOffset, uint EntriesOffset);
    readonly record struct Entry(ulong Id, ushort EndOffset);
    sealed record EncodedString(uint Id, byte[] Text);
    sealed class EncodedTable
    {
        public List<EncodedString> Strings { get; } = [];
        public int TextLength { get; set; }
        public int EntriesOffset { get; set; }
        public int TextOffset { get; set; }
    }

    sealed class BigEndianReader
    {
        readonly byte[] _data;
        public BigEndianReader(byte[] data) => _data = data;
        public long Position
        {
            get => _position;
            set
            {
                if (value < 0 || value > _data.Length)
                    throw new InvalidDataException($"Localization offset 0x{value:X} is outside the indexed data.");
                _position = checked((int)value);
            }
        }
        int _position;

        public byte ReadByte(string field)
        {
            if (_position >= _data.Length)
                throw new EndOfStreamException($"Unexpected end while reading {field}.");
            return _data[_position++];
        }
        public ushort ReadUInt16(string field)
        {
            Ensure(2, field);
            ushort result = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(_position, 2));
            _position += 2;
            return result;
        }
        public short ReadInt16(string field) => unchecked((short)ReadUInt16(field));
        public uint ReadUInt32(string field)
        {
            Ensure(4, field);
            uint result = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(_position, 4));
            _position += 4;
            return result;
        }
        void Ensure(int count, string field)
        {
            if (_data.Length - _position < count)
                throw new EndOfStreamException($"Unexpected end while reading {field}.");
        }
    }
}
