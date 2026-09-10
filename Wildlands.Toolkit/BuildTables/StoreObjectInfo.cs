using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Wildlands.Toolkit;

internal sealed record StoreObjectInfo(
    ulong ResourceId,
    ulong RecordId,
    string Name,
    int NameCountOffset,
    int NameOffset,
    int NameSuffixOffset)
{
    public const uint ClassHash = 0x36C9CB38;

    const uint LocalizedValueMarker = 0x81A7045D;
    const int HeaderSize = 12;
    const int FooterSize = 11;
    const int MaximumNameCharacters = 1_024;

    public static StoreObjectInfo Parse(ReadOnlySpan<byte> data, ulong expectedResourceId)
    {
        if (data.Length < HeaderSize + FooterSize
            || BinaryPrimitives.ReadUInt64LittleEndian(data) != expectedResourceId
            || BinaryPrimitives.ReadUInt32LittleEndian(data[8..]) != ClassHash)
            throw new InvalidDataException("The StoreObjectInfo header does not match its expected resource ID and class.");

        IReadOnlyList<NameField> populatedNames = FindNameFields(data).Where(field => field.Characters > 0).ToList();
        if (populatedNames.Count != 1)
            throw new InvalidDataException($"The StoreObjectInfo contains {populatedNames.Count} populated structured name fields instead of exactly one.");
        NameField nameField = populatedNames[0];

        int recordOffset = data.Length - 10;
        if (data[recordOffset - 1] != 0 || data[^2] != 0 || data[^1] != 0)
            throw new InvalidDataException("The StoreObjectInfo does not have the expected tagged-record footer.");

        ulong recordId = BinaryPrimitives.ReadUInt64LittleEndian(data[recordOffset..]);
        if (recordId == 0)
            throw new InvalidDataException("The StoreObjectInfo record reference is zero.");

        string name = Encoding.Unicode.GetString(data.Slice(nameField.Offset, nameField.Characters * sizeof(char)));
        return new StoreObjectInfo(expectedResourceId, recordId, name, nameField.CountOffset, nameField.Offset, nameField.SuffixOffset);
    }

    public static bool TryParse(ReadOnlySpan<byte> data, ulong expectedResourceId, out StoreObjectInfo info)
    {
        try
        {
            info = Parse(data, expectedResourceId);
            return true;
        }
        catch (InvalidDataException)
        {
            info = null!;
            return false;
        }
        catch (OverflowException)
        {
            info = null!;
            return false;
        }
    }

    public byte[] Rewrite(ReadOnlySpan<byte> source, ulong newResourceId, ulong newRecordId, string newName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(newName);
        if (newResourceId == 0 || newResourceId == ResourceId)
            throw new ArgumentOutOfRangeException(nameof(newResourceId), "The new StoreObjectInfo ID must be distinct and non-zero.");
        if (newRecordId == 0 || newRecordId == RecordId)
            throw new ArgumentOutOfRangeException(nameof(newRecordId), "The new gameplay-record ID must be distinct and non-zero.");

        StoreObjectInfo current = Parse(source, ResourceId);
        if (current != this)
            throw new InvalidDataException("The StoreObjectInfo source changed after it was parsed.");

        byte[] encodedName = Encoding.Unicode.GetBytes(newName);
        if (encodedName.Length / sizeof(char) > MaximumNameCharacters)
            throw new ArgumentOutOfRangeException(nameof(newName), "The StoreObjectInfo name is too long.");

        byte[] output = new byte[checked(NameOffset + encodedName.Length + 1 + source.Length - NameSuffixOffset)];
        source[..NameOffset].CopyTo(output);
        BinaryPrimitives.WriteUInt64LittleEndian(output, newResourceId);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(NameCountOffset), encodedName.Length / sizeof(char));
        encodedName.CopyTo(output.AsSpan(NameOffset));
        source[NameSuffixOffset..].CopyTo(output.AsSpan(NameOffset + encodedName.Length + 1));
        BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(output.Length - 10), newRecordId);

        StoreObjectInfo written = Parse(output, newResourceId);
        if (written.RecordId != newRecordId || written.Name != newName)
            throw new InvalidDataException("The StoreObjectInfo clone failed its parse/write/parse validation.");
        return output;
    }

    static IReadOnlyList<NameField> FindNameFields(ReadOnlySpan<byte> data)
    {
        Span<byte> encoded = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(encoded, LocalizedValueMarker);
        var fields = new List<NameField>();
        int searchOffset = HeaderSize;
        while (searchOffset <= data.Length - encoded.Length)
        {
            int relative = data[searchOffset..].IndexOf(encoded);
            if (relative < 0)
                break;

            int markerOffset = searchOffset + relative;
            int countOffset = markerOffset + 8;
            int fieldOffset = countOffset + sizeof(int);
            if (fieldOffset <= data.Length - FooterSize)
            {
                int characters = BinaryPrimitives.ReadInt32LittleEndian(data[countOffset..]);
                if (characters is >= 0 and <= MaximumNameCharacters)
                {
                    int encodedLength = checked(characters * sizeof(char));
                    int suffixOffset = checked(fieldOffset + encodedLength + 1);
                    if (suffixOffset <= data.Length - FooterSize && data[suffixOffset - 1] == 0)
                        fields.Add(new NameField(countOffset, fieldOffset, suffixOffset, characters));
                }
            }
            searchOffset = markerOffset + sizeof(uint);
        }
        return fields;
    }

    readonly record struct NameField(int CountOffset, int Offset, int SuffixOffset, int Characters);
}
