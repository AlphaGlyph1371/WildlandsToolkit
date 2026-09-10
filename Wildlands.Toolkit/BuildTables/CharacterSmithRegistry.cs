using System.Buffers.Binary;
using System.IO;

namespace Wildlands.Toolkit;

internal sealed record CharacterSmithRegistryList(
    ArmoryIndex.IndexedResource Owner,
    int HeaderOffset,
    int CountOffset,
    IReadOnlyList<CharacterSmithRegistryEntry> Entries);

internal sealed record CharacterSmithRegistryEntry(
    int HeaderOffset,
    int EndOffset,
    uint LocalObjectId,
    ulong CategoryId,
    ulong RecordId,
    IReadOnlyList<ulong> Conditions);

internal static class CharacterSmithRegistry
{
    const ulong ResourceId = 0x003CCFF6F111;
    const uint ResourceClassHash = 0x3BCA112F;
    const uint ListClassHash = 0xBDE47DC8;
    const uint EntryClassHash = 0x5B3784E8;
    const uint LocalObjectType = 0xF8000000;
    const int MaximumEntries = 8_192;

    public static IReadOnlyList<CharacterSmithRegistryList> Find(ArmoryIndex index, ulong recordId)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (recordId == 0)
            return [];

        var results = new List<CharacterSmithRegistryList>();
        foreach (ArmoryIndex.IndexedResource owner in index.DatabaseResources
            .Where(resource => resource.Id == ResourceId && resource.ClassHash == ResourceClassHash)
            .GroupBy(resource => resource.Id)
            .Select(group => group.Last()))
        {
            var matches = ReadLists(owner)
                .Where(list => list.Entries.Any(entry => entry.RecordId == recordId))
                .ToList();
            if (matches.Count > 1)
                throw new InvalidDataException($"CharacterSmith resource 0x{owner.Id:X12} contains {matches.Count} structured lists for record 0x{recordId:X12}.");
            results.AddRange(matches);
        }
        return results;
    }

    public static byte[] InsertAfter(CharacterSmithRegistryList list, ulong templateRecordId, ulong newRecordId)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (newRecordId == 0 || newRecordId == templateRecordId)
            throw new ArgumentOutOfRangeException(nameof(newRecordId), "The new CharacterSmith record ID must be distinct and non-zero.");

        var matchingEntries = list.Entries.Where(entry => entry.RecordId == templateRecordId).ToList();
        if (matchingEntries.Count != 1 || list.Entries.Any(entry => entry.RecordId == newRecordId))
            throw new InvalidDataException("The CharacterSmith template must occur exactly once and the new record ID must be unused in its list.");

        CharacterSmithRegistryEntry template = matchingEntries[0];
        IReadOnlyList<LocalObjectHeader> headers = ReadLocalObjectHeaders(list.Owner.Data, template.LocalObjectId);
        if (!headers.Any(header => header.Offset == list.HeaderOffset) || !headers.Any(header => header.Offset == template.HeaderOffset))
            throw new InvalidDataException("The CharacterSmith list or template row is not part of the validated local-object sequence.");
        if (headers.Any(header => header.ObjectId == 0x00FFFFFF))
            throw new InvalidDataException("The CharacterSmith local-object ID range is exhausted.");

        byte[] shifted = (byte[])list.Owner.Data.Clone();
        foreach (LocalObjectHeader header in headers.Where(header => header.ObjectId > template.LocalObjectId))
            BinaryPrimitives.WriteUInt64LittleEndian(shifted.AsSpan(header.Offset), LocalObjectType | (header.ObjectId + 1UL));
        BinaryPrimitives.WriteUInt32LittleEndian(shifted.AsSpan(list.CountOffset), checked((uint)list.Entries.Count + 1));

        byte[] insertedRow = list.Owner.Data.AsSpan(template.HeaderOffset, template.EndOffset - template.HeaderOffset).ToArray();
        BinaryPrimitives.WriteUInt64LittleEndian(insertedRow, LocalObjectType | (template.LocalObjectId + 1UL));
        const int recordValueOffset = 12 + 9 + 1;
        BinaryPrimitives.WriteUInt64LittleEndian(insertedRow.AsSpan(recordValueOffset), newRecordId);

        byte[] output = new byte[checked(shifted.Length + insertedRow.Length)];
        shifted.AsSpan(0, template.EndOffset).CopyTo(output);
        insertedRow.CopyTo(output, template.EndOffset);
        shifted.AsSpan(template.EndOffset).CopyTo(output.AsSpan(template.EndOffset + insertedRow.Length));

        IReadOnlyList<LocalObjectHeader> writtenHeaders = ReadLocalObjectHeaders(output, template.LocalObjectId);
        if (writtenHeaders.Count != headers.Count + 1)
            throw new InvalidDataException("The CharacterSmith insertion did not add exactly one local object.");

        var writtenOwner = list.Owner with { Data = output };
        CharacterSmithRegistryList writtenList = ReadLists(writtenOwner)
            .Single(candidate => candidate.HeaderOffset == list.HeaderOffset && candidate.Entries.Any(entry => entry.RecordId == templateRecordId));
        int templatePosition = writtenList.Entries.ToList().FindIndex(entry => entry.RecordId == templateRecordId);
        if (writtenList.Entries.Count != list.Entries.Count + 1 || templatePosition < 0
            || writtenList.Entries[templatePosition + 1].RecordId != newRecordId
            || writtenList.Entries[templatePosition + 1].CategoryId != template.CategoryId
            || !writtenList.Entries[templatePosition + 1].Conditions.SequenceEqual(template.Conditions))
            throw new InvalidDataException("The CharacterSmith insertion failed its parse/write/parse validation.");
        return output;
    }

    static IReadOnlyList<CharacterSmithRegistryList> ReadLists(ArmoryIndex.IndexedResource owner)
    {
        ReadOnlySpan<byte> data = owner.Data;
        var lists = new List<CharacterSmithRegistryList>();
        for (int offset = 0; offset + 16 <= data.Length; offset++)
        {
            if (!TryReadLocalHeader(data, offset, ListClassHash, out uint listObjectId))
                continue;

            uint rawCount = BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 12)..]);
            if (rawCount is < 1 or > MaximumEntries)
                continue;

            if (offset + 17 > data.Length || data[offset + 16] != 0)
                continue;

            int at = offset + 17;
            var entries = new List<CharacterSmithRegistryEntry>((int)rawCount);
            bool valid = true;
            for (int entryIndex = 0; entryIndex < rawCount; entryIndex++)
            {
                if (!TryReadEntry(data, at, out CharacterSmithRegistryEntry entry)
                    || entry.LocalObjectId != listObjectId + entryIndex + 1)
                {
                    valid = false;
                    break;
                }

                entries.Add(entry);
                at = entry.EndOffset;
            }

            if (valid)
                lists.Add(new CharacterSmithRegistryList(owner, offset, offset + 12, entries));
        }
        return lists;
    }

    static bool TryReadEntry(ReadOnlySpan<byte> data, int offset, out CharacterSmithRegistryEntry entry)
    {
        entry = null!;
        if (!TryReadLocalHeader(data, offset, EntryClassHash, out uint localObjectId))
            return false;

        int at = offset + 12;
        if (!TryReadHandle(data, ref at, out ulong categoryId) || !TryReadHandle(data, ref at, out ulong recordId) || at + sizeof(uint) > data.Length)
            return false;

        uint conditionCount = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
        at += sizeof(uint);
        if (conditionCount > MaximumEntries)
            return false;

        var conditions = new ulong[conditionCount];
        for (int index = 0; index < conditions.Length; index++)
            if (!TryReadHandle(data, ref at, out conditions[index]))
                return false;

        if (at >= data.Length || data[at++] != 0)
            return false;

        entry = new CharacterSmithRegistryEntry(offset, at, localObjectId, categoryId, recordId, conditions);
        return true;
    }

    static bool TryReadHandle(ReadOnlySpan<byte> data, ref int offset, out ulong value)
    {
        value = 0;
        if (offset + 9 > data.Length || data[offset] != 0)
            return false;
        value = BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + 1)..]);
        offset += 9;
        return value != 0;
    }

    static bool TryReadLocalHeader(ReadOnlySpan<byte> data, int offset, uint expectedClassHash, out uint objectId)
    {
        objectId = 0;
        if (offset < 0 || offset + 12 > data.Length)
            return false;

        ulong rawId = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
        if (rawId > uint.MaxValue || ((uint)rawId & 0xFF000000) != LocalObjectType
            || BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]) != expectedClassHash)
            return false;

        objectId = (uint)rawId & 0x00FFFFFF;
        return objectId != 0;
    }

    static IReadOnlyList<LocalObjectHeader> ReadLocalObjectHeaders(ReadOnlySpan<byte> data, uint requireUniqueAbove)
    {
        var headers = new List<LocalObjectHeader>();
        for (int offset = 0; offset + 12 <= data.Length; offset++)
        {
            ulong rawId = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
            if (rawId > uint.MaxValue || ((uint)rawId & 0xFF000000) != LocalObjectType
                || BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 8)..]) == 0)
                continue;
            headers.Add(new LocalObjectHeader(offset, (uint)rawId & 0x00FFFFFF));
        }

        if (headers.Count == 0 || headers[0].ObjectId != 0)
            throw new InvalidDataException("The CharacterSmith resource has no complete local-object sequence.");
        Span<byte> encodedId = stackalloc byte[sizeof(uint)];
        for (int index = 0; index < headers.Count; index++)
        {
            uint expected = checked((uint)index);
            if (headers[index].ObjectId != expected)
                throw new InvalidDataException($"The CharacterSmith local-object sequence jumps from the expected ID {expected} to {headers[index].ObjectId}.");

            if (expected <= requireUniqueAbove)
                continue;

            BinaryPrimitives.WriteUInt32LittleEndian(encodedId, LocalObjectType | expected);
            int occurrences = 0;
            for (int offset = 0; offset <= data.Length - encodedId.Length; offset++)
                if (data.Slice(offset, encodedId.Length).SequenceEqual(encodedId))
                    occurrences++;
            if (occurrences != 1)
                throw new InvalidDataException($"CharacterSmith local-object ID {expected} occurs {occurrences} times and cannot be renumbered safely.");
        }
        return headers;
    }

    public const uint StructuredEntryClassHash = EntryClassHash;

    readonly record struct LocalObjectHeader(int Offset, uint ObjectId);
}
