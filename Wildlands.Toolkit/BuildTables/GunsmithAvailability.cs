using System.Buffers.Binary;
using System.IO;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

internal sealed record GunsmithAvailabilityList(
    ArmoryIndex.IndexedResource Owner, int CountOffset, int EntryStride, int ValueOffset,
    IReadOnlyList<ulong> RecordIds, IReadOnlyList<ulong> CanonicalRecordIds)
{
    public string OwnerName => Owner.Name;
    public int IndexOf(ulong recordId)
    {
        for (int index = 0; index < RecordIds.Count; index++)
            if (RecordIds[index] == recordId)
                return index;
        return -1;
    }
}

internal static class GunsmithAvailability
{
    public static IReadOnlyList<GunsmithAvailabilityList> FindNamedRegistries(ArmoryIndex index, ulong templateRecordId, string ownerName)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (templateRecordId == 0 || string.IsNullOrWhiteSpace(ownerName))
            return [];

        var effectiveOwnerIndexes = index.DatabaseResources
            .Select((resource, resourceIndex) => (resource, resourceIndex))
            .Where(item => string.Equals(item.resource.Name, ownerName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.resource.Id)
            .Select(group => group.Last().resourceIndex)
            .ToHashSet();
        var results = new List<GunsmithAvailabilityList>();
        foreach (int ownerIndex in effectiveOwnerIndexes)
        {
            ArmoryIndex.IndexedResource owner = index.DatabaseResources[ownerIndex];
            var candidates = index.AvailabilityLists
                .Where(candidate => candidate.OwnerResourceIndex == ownerIndex && candidate.RecordIds.Contains(templateRecordId) && MatchesIndexedBytes(owner.Data, candidate))
                .OrderByDescending(candidate => candidate.RecordIds.Length)
                .ToList();
            if (candidates.Count == 0)
                continue;
            if (candidates.Count > 1 && candidates[0].RecordIds.Length == candidates[1].RecordIds.Length)
                throw new InvalidOperationException($"{ownerName} contains two equally likely lists for the selected item.");

            ArmoryIndex.AvailabilityListCandidate selected = candidates[0];
            results.Add(new GunsmithAvailabilityList(owner, selected.CountOffset, selected.EntryStride, selected.ValueOffset, selected.RecordIds, selected.RecordIds));
        }
        return results;
    }

    public static IReadOnlyList<GunsmithAvailabilityList> FindAttachmentTypeRegistries(ArmoryIndex index, ulong templateRecordId, uint templateRecordClassHash)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (templateRecordId == 0 || templateRecordClassHash == 0)
            return [];

        var effectiveResourceIndexes = index.DatabaseResources
            .Select((resource, resourceIndex) => (resource, resourceIndex))
            .GroupBy(item => item.resource.Id)
            .Select(group => group.Last().resourceIndex)
            .ToHashSet();
        var effectiveResources = effectiveResourceIndexes
            .Select(resourceIndex => index.DatabaseResources[resourceIndex])
            .GroupBy(resource => resource.Id)
            .ToDictionary(group => group.Key, group => group.Last());

        var matches = index.AvailabilityLists
            .Where(candidate => effectiveResourceIndexes.Contains(candidate.OwnerResourceIndex))
            .Where(candidate =>
            {
                ArmoryIndex.IndexedResource owner = index.DatabaseResources[candidate.OwnerResourceIndex];
                return owner.Name.StartsWith("WPN_AT_", StringComparison.OrdinalIgnoreCase) && candidate.RecordIds.Contains(templateRecordId)
                    && candidate.RecordIds.All(id => effectiveResources.TryGetValue(id, out var resource) && resource.ClassHash == templateRecordClassHash)
                    && MatchesIndexedBytes(owner.Data, candidate);
            })
            .GroupBy(candidate =>
            {
                ArmoryIndex.IndexedResource owner = index.DatabaseResources[candidate.OwnerResourceIndex];
                return (owner.ArchivePath, owner.EntryIndex, owner.ResourceIndex);
            })
            .ToList();

        var results = new List<GunsmithAvailabilityList>();
        foreach (var group in matches)
        {
            var candidates = group.ToList();
            if (candidates.Count != 1)
                throw new InvalidOperationException("The template attachment belongs to an ambiguous WPN_AT registry and cannot be cloned safely.");

            ArmoryIndex.AvailabilityListCandidate candidate = candidates[0];
            ArmoryIndex.IndexedResource owner = index.DatabaseResources[candidate.OwnerResourceIndex];
            results.Add(new GunsmithAvailabilityList(owner, candidate.CountOffset, candidate.EntryStride, candidate.ValueOffset, candidate.RecordIds, candidate.RecordIds));
        }
        return results;
    }

    public static IReadOnlyList<GunsmithAvailabilityList> FindUnlockRegistries(
        ArmoryIndex index, ulong templateRecordId)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (templateRecordId == 0)
            return [];

        var knownIds = index.DatabaseResources.Select(resource => resource.Id).ToHashSet();
        var effectiveOwners = index.DatabaseResources
            .Select((resource, resourceIndex) => (resource, resourceIndex))
            .Where(item => string.Equals(item.resource.Name, "DBUnlockables_default", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.resource.Id)
            .Select(group => group.Last())
            .ToList();

        var results = new List<GunsmithAvailabilityList>();
        foreach (var (owner, _) in effectiveOwners)
        {
            var candidates = ReadTaggedHandleLists(owner.Data, templateRecordId, knownIds, maxEntries: 8_192)
                .OrderByDescending(candidate => candidate.RecordIds.Count)
                .ToList();
            if (candidates.Count == 0)
                continue;
            if (candidates.Count > 1
                && candidates[0].RecordIds.Count == candidates[1].RecordIds.Count)
                throw new InvalidOperationException("DBUnlockables_default contains an ambiguous attachment registry and cannot be cloned safely.");

            var selected = candidates[0];
            results.Add(new GunsmithAvailabilityList(owner, selected.CountOffset, 9, 1, selected.RecordIds, selected.RecordIds));
        }
        return results;
    }

    public static IReadOnlyList<GunsmithAvailabilityList> FindLootRegistries(ArmoryIndex index, ulong templateRecordId)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (templateRecordId == 0)
            return [];

        var knownIds = index.DatabaseResources.Select(resource => resource.Id).ToHashSet();
        var effectiveOwners = index.DatabaseResources
            .Where(resource => resource.Name.StartsWith("DBLootConfig_", StringComparison.OrdinalIgnoreCase) && !resource.Name.EndsWith("_DEBUG", StringComparison.OrdinalIgnoreCase))
            .GroupBy(resource => resource.Id)
            .Select(group => group.Last())
            .ToList();

        var results = new List<GunsmithAvailabilityList>();
        foreach (var owner in effectiveOwners)
        {
            var candidates = ReadTaggedHandleLists(owner.Data, templateRecordId, knownIds, maxEntries: 8_192)
                .OrderByDescending(candidate => candidate.RecordIds.Count)
                .ToList();
            if (candidates.Count == 0)
                continue;
            if (candidates.Count > 1
                && candidates[0].RecordIds.Count == candidates[1].RecordIds.Count)
                throw new InvalidOperationException($"{owner.Name} contains an ambiguous loot registry and cannot be extended safely.");

            var selected = candidates[0];
            results.Add(new GunsmithAvailabilityList(owner, selected.CountOffset,
                9, 1, selected.RecordIds, selected.RecordIds));
        }
        return results;
    }
    
    public static IReadOnlyList<GunsmithAvailabilityList> FindStoreRegistries(ArmoryIndex index, ulong templateInfoId)
    {
        ArgumentNullException.ThrowIfNull(index);
        var knownIds = index.DatabaseResources.Select(resource => resource.Id).ToHashSet();
        var owners = index.DatabaseResources
            .Where(resource => resource.Name.StartsWith("StoreDBEntry_", StringComparison.OrdinalIgnoreCase))
            .GroupBy(resource => resource.Id)
            .Select(group => group.Last())
            .ToList();

        var results = new List<GunsmithAvailabilityList>();
        foreach (var owner in owners)
        {
            var candidates = ReadContainerLists(owner.Data, templateInfoId, knownIds)
                .OrderByDescending(candidate => candidate.RecordIds.Count)
                .ToList();
            if (candidates.Count == 0)
                continue;
            if (candidates.Count > 1 && candidates[0].RecordIds.Count == candidates[1].RecordIds.Count)
                throw new InvalidOperationException($"{owner.Name} holds an ambiguous store list and cannot be extended safely.");
            results.Add(new GunsmithAvailabilityList(owner, candidates[0].CountOffset, 10, 2, candidates[0].RecordIds, candidates[0].RecordIds));
        }
        return results;
    }

    static IReadOnlyList<(int CountOffset, IReadOnlyList<ulong> RecordIds)> ReadContainerLists(ReadOnlySpan<byte> data, ulong requiredId, IReadOnlySet<ulong> knownIds)
    {
        const int entryStride = 10;
        var results = new List<(int, IReadOnlyList<ulong>)>();
        for (int offset = 0; offset + sizeof(uint) <= data.Length; offset++)
        {
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            if (count is < 2 or > 65_535 || offset + sizeof(uint) + (long)count * entryStride > data.Length)
                continue;

            int entriesOffset = offset + sizeof(uint);
            var ids = new ulong[count];
            bool valid = true, containsRequired = false;
            for (int item = 0; item < ids.Length; item++)
            {
                int entryOffset = entriesOffset + item * entryStride;
                if (data[entryOffset] != 1 || data[entryOffset + 1] != 0) { valid = false; break; }
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data[(entryOffset + 2)..]);
                if (!knownIds.Contains(id)) { valid = false; break; }
                ids[item] = id;
                containsRequired |= id == requiredId;
            }
            if (valid && containsRequired && ids.Distinct().Count() == ids.Length)
                results.Add((offset, ids));
        }
        return results;
    }

    public static IReadOnlyList<GunsmithAvailabilityList> FindDatabaseContainerRegistries(ArmoryIndex index, ulong templateRecordId)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (templateRecordId == 0)
            return [];

        var knownIds = index.DatabaseResources.Select(resource => resource.Id).ToHashSet();
        var effectiveOwners = index.DatabaseResources
            .Select((resource, resourceIndex) => (resource, resourceIndex))
            .Where(item => item.resource.Name.StartsWith("DBContainerEntry_", StringComparison.OrdinalIgnoreCase))
            .GroupBy(item => item.resource.Id)
            .Select(group => group.Last())
            .ToList();

        var results = new List<GunsmithAvailabilityList>();
        foreach (var (owner, _) in effectiveOwners)
        {
            if (!TryReadDatabaseContainerList(owner.Data, templateRecordId, knownIds, out var recordIds))
                continue;

            results.Add(new GunsmithAvailabilityList(owner, 13, 10, 2, recordIds, recordIds));
        }
        return results;
    }

    static bool TryReadDatabaseContainerList(ReadOnlySpan<byte> data, ulong requiredId, IReadOnlySet<ulong> knownIds, out IReadOnlyList<ulong> recordIds)
    {
        const int countOffset = 13;
        const int entriesOffset = countOffset + sizeof(uint);
        const int entryStride = 10;
        const int valueOffset = 2;

        recordIds = [];
        if (data.Length < entriesOffset)
            return false;

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[countOffset..]);
        if (count is < 1 or > 65_535 || entriesOffset + (long)count * entryStride != data.Length)
            return false;

        var ids = new ulong[count];
        bool containsRequired = false;
        for (int item = 0; item < ids.Length; item++)
        {
            int entryOffset = entriesOffset + item * entryStride;
            if (data[entryOffset] != 1 || data[entryOffset + 1] != 0)
                return false;

            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data[(entryOffset + valueOffset)..]);
            if (!knownIds.Contains(id))
                return false;
            ids[item] = id;
            containsRequired |= id == requiredId;
        }

        if (!containsRequired || ids.Distinct().Count() != ids.Length)
            return false;

        recordIds = ids;
        return true;
    }

    static IReadOnlyList<(int CountOffset, IReadOnlyList<ulong> RecordIds)> ReadTaggedHandleLists(ReadOnlySpan<byte> data, ulong requiredId, IReadOnlySet<ulong> knownIds, int maxEntries)
    {
        var results = new List<(int, IReadOnlyList<ulong>)>();
        for (int offset = 0; offset + sizeof(uint) <= data.Length; offset++)
        {
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            if (count is < 1 || count > maxEntries || offset + sizeof(uint) + (long)count * 9 > data.Length)
                continue;

            int entriesOffset = offset + sizeof(uint);
            var ids = new ulong[count];
            bool containsRequired = false;
            bool valid = true;
            for (int item = 0; item < ids.Length; item++)
            {
                int entryOffset = entriesOffset + item * 9;
                if (data[entryOffset] != 0)
                {
                    valid = false;
                    break;
                }
                ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data[(entryOffset + 1)..]);
                if (!knownIds.Contains(id))
                {
                    valid = false;
                    break;
                }
                ids[item] = id;
                containsRequired |= id == requiredId;
            }

            if (valid && containsRequired && ids.Distinct().Count() == ids.Length)
                results.Add((offset, ids));
        }
        return results;
    }

    public static IReadOnlyList<GunsmithAvailabilityList> Find(ArmoryIndex? index, BuildTableAsset table, BuildTableGameMetadata metadata,
        IReadOnlyDictionary<uint, BuildTableOptionMetadata>? addedMetadata = null, IReadOnlyDictionary<ulong, IReadOnlyList<string>>? addedOwnersByRecordId = null)
    {
        if (index is null || table.Rows.Count == 0 || metadata.OwnerRecordIds.Count == 0)
            return [];

        var expected = new HashSet<ulong>();
        foreach (var row in table.Rows)
        {
            BuildTableOptionMetadata? option = metadata.ByBuildTag.GetValueOrDefault(row.Tag) ?? addedMetadata?.GetValueOrDefault(row.Tag);
            if (option is null || option.RecordId == 0 || !expected.Add(option.RecordId))
                return [];
        }

        var effectiveOwnerIndexes = index.DatabaseResources
            .Select((resource, resourceIndex) => (resource, resourceIndex))
            .Where(item => metadata.OwnerRecordIds.Contains(item.resource.Id))
            .GroupBy(item => item.resource.Id)
            .Select(group => group.Last().resourceIndex)
            .ToHashSet();

        var lists = new List<GunsmithAvailabilityList>();
        foreach (var candidate in index.AvailabilityLists)
        {
            var owner = index.DatabaseResources[candidate.OwnerResourceIndex];
            if (!effectiveOwnerIndexes.Contains(candidate.OwnerResourceIndex))
                continue;

            var currentForOwner = expected.Where(id => (metadata.OwnersByRecordId.TryGetValue(id, out var ownerNames) && ownerNames.Contains(owner.Name, StringComparer.OrdinalIgnoreCase))
                    || (addedOwnersByRecordId?.TryGetValue(id, out var addedOwnerNames) == true && addedOwnerNames.Contains(owner.Name, StringComparer.OrdinalIgnoreCase))).ToHashSet();
            if (currentForOwner.Count == 0 || candidate.RecordIds.Any(id => !currentForOwner.Contains(id)))
                continue;

            if (!MatchesIndexedBytes(owner.Data, candidate))
                continue;

            var canonical = index.AvailabilityLists
                .Where(other => other.OwnerResourceIndex != candidate.OwnerResourceIndex)
                .Select(other => (Candidate: other, Owner: index.DatabaseResources[other.OwnerResourceIndex]))
                .Where(other => other.Owner.Id == owner.Id && other.Candidate.RecordIds.Length == currentForOwner.Count
                    && other.Candidate.RecordIds.All(currentForOwner.Contains) && MatchesIndexedBytes(other.Owner.Data, other.Candidate))
                .Select(other => (IReadOnlyList<ulong>)other.Candidate.RecordIds)
                .FirstOrDefault() ?? [];
            lists.Add(new GunsmithAvailabilityList(owner, candidate.CountOffset, candidate.EntryStride, candidate.ValueOffset, candidate.RecordIds, canonical));
        }

        return lists;
    }

    public static IReadOnlyList<ArmoryDatabaseResourceChange> Rewrite(IReadOnlyList<GunsmithAvailabilityList> lists, IReadOnlySet<ulong> removals,
        IReadOnlySet<ulong> additions, IReadOnlyList<ulong> canonicalOrder)
    {
        if (lists.Count == 0)
            throw new InvalidOperationException("No confirmed Gunsmith lists were found.");
        if (removals.Count == 0 && additions.Count == 0)
            return [];

        var changes = new List<ArmoryDatabaseResourceChange>();
        foreach (var grouped in lists.GroupBy(list => (list.Owner.ArchivePath, list.Owner.EntryIndex, list.Owner.ResourceIndex)))
        {
            var ownerLists = grouped.ToList();
            if (ownerLists.Count != 1)
                throw new InvalidOperationException($"{ownerLists[0].OwnerName} contains more than one matching list. It is left unchanged.");

            var list = ownerLists[0];
            IReadOnlyList<ulong> insertionOrder = list.CanonicalRecordIds.Count > 0 && additions.All(list.CanonicalRecordIds.Contains)
                    ? list.CanonicalRecordIds
                    : canonicalOrder;
            if (removals.Any(id => !list.RecordIds.Contains(id)))
                throw new InvalidOperationException("One selected option is not part of this confirmed Gunsmith list.");
            if (additions.Any(id => !insertionOrder.Contains(id)))
                throw new InvalidOperationException("One selected option has no confirmed position in this BuildTable.");

            var rewritten = list.RecordIds.Where(id => !removals.Contains(id)).ToList();
            foreach (ulong id in additions.OrderBy(id => IndexOf(insertionOrder, id)))
            {
                if (rewritten.Contains(id))
                    continue;
                InsertInCanonicalPosition(rewritten, id, insertionOrder);
            }

            if (rewritten.Count < 1)
                throw new InvalidOperationException("The selected option cannot be removed from this confirmed list safely.");

            byte[] updated = RewriteList(list.Owner.Data, list.CountOffset, list.EntryStride, list.ValueOffset, list.RecordIds, rewritten);
            ValidateList(updated, list.CountOffset, list.EntryStride, list.ValueOffset, rewritten);
            changes.Add(new ArmoryDatabaseResourceChange(list.Owner.ArchivePath, list.Owner.EntryIndex, list.Owner.EntryName, list.Owner.ResourceIndex, list.Owner.Name, updated));
        }
        return changes;
    }

    public static IReadOnlyList<ArmoryDatabaseResourceChange> InsertAfterTemplates(IReadOnlyList<(GunsmithAvailabilityList List, ulong TemplateId, ulong NewId)> insertions)
    {
        if (insertions.Count == 0)
            return [];

        var changes = new List<ArmoryDatabaseResourceChange>();
        foreach (var ownerGroup in insertions.GroupBy(item => (item.List.Owner.ArchivePath, item.List.Owner.EntryIndex, item.List.Owner.ResourceIndex)))
        {
            GunsmithAvailabilityList ownerList = ownerGroup.First().List;
            byte[] updated = (byte[])ownerList.Owner.Data.Clone();
            foreach (var listGroup in ownerGroup.GroupBy(item => (item.List.CountOffset, item.List.EntryStride, item.List.ValueOffset)).OrderByDescending(group => group.Key.CountOffset))
            {
                var items = listGroup.ToList();
                GunsmithAvailabilityList list = items[0].List;
                if (items.Any(item => !item.List.RecordIds.SequenceEqual(list.RecordIds)))
                    throw new InvalidOperationException($"{list.OwnerName} was resolved to incompatible copies of the same registry list.");

                var rewritten = list.RecordIds.ToList();
                foreach (var item in items)
                {
                    if (rewritten.Contains(item.NewId))
                        continue;
                    int templatePosition = rewritten.IndexOf(item.TemplateId);
                    if (templatePosition < 0)
                        throw new InvalidOperationException($"{list.OwnerName} no longer contains the registry template 0x{item.TemplateId:X}.");
                    rewritten.Insert(templatePosition + 1, item.NewId);
                }

                if (string.Equals(list.OwnerName, "DBUnlockables_default", StringComparison.OrdinalIgnoreCase) && list.EntryStride == 9 && list.ValueOffset == 1)
                {
                    updated = RewriteUnlockableRows(updated, list, items);
                    continue;
                }

                updated = RewriteList(updated, list.CountOffset, list.EntryStride, list.ValueOffset, list.RecordIds, rewritten);
                ValidateList(updated, list.CountOffset, list.EntryStride, list.ValueOffset, rewritten);
            }

            changes.Add(new ArmoryDatabaseResourceChange(ownerList.Owner.ArchivePath, ownerList.Owner.EntryIndex, ownerList.Owner.EntryName,
                ownerList.Owner.ResourceIndex, ownerList.Owner.Name, updated));
        }
        return changes;
    }

    static byte[] RewriteUnlockableRows(byte[] source, GunsmithAvailabilityList list, IReadOnlyList<(GunsmithAvailabilityList List, ulong TemplateId, ulong NewId)> insertions)
    {
        int originalCount = list.RecordIds.Count;
        if (originalCount < 1)
            throw new InvalidDataException("DBUnlockables_default has an empty unlockable table.");

        int byteColumn1Offset = checked(list.CountOffset - (sizeof(uint) * 3 + originalCount * (sizeof(byte) + sizeof(uint) + sizeof(byte))));
        int uintColumnOffset = checked(byteColumn1Offset + sizeof(uint) + originalCount);
        int byteColumn2Offset = checked(uintColumnOffset + sizeof(uint) + originalCount * sizeof(uint));
        int handleColumnOffset = checked(byteColumn2Offset + sizeof(uint) + originalCount);
        int oldEnd = checked(list.CountOffset + sizeof(uint) + originalCount * 9);

        if (byteColumn1Offset < 0
            || handleColumnOffset != list.CountOffset
            || oldEnd > source.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(byteColumn1Offset)) != originalCount
            || BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(uintColumnOffset)) != originalCount
            || BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(byteColumn2Offset)) != originalCount
            || BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(handleColumnOffset)) != originalCount)
            throw new InvalidDataException(
                "DBUnlockables_default has mismatched parallel column counts. Restore the clean archives "
                + "and rebuild the addons with the current toolkit.");

        var column1 = source.AsSpan(byteColumn1Offset + sizeof(uint), originalCount).ToArray().ToList();
        var column2 = new List<uint>(originalCount);
        int uintValuesOffset = uintColumnOffset + sizeof(uint);
        for (int index = 0; index < originalCount; index++)
            column2.Add(BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(uintValuesOffset + index * sizeof(uint))));
        var column3 = source.AsSpan(byteColumn2Offset + sizeof(uint), originalCount).ToArray().ToList();
        var ids = list.RecordIds.ToList();

        foreach (var insertion in insertions)
        {
            if (ids.Contains(insertion.NewId))
                continue;
            int templatePosition = ids.IndexOf(insertion.TemplateId);
            if (templatePosition < 0)
                throw new InvalidOperationException($"{list.OwnerName} no longer contains unlock template 0x{insertion.TemplateId:X}.");
            int insertAt = templatePosition + 1;
            ids.Insert(insertAt, insertion.NewId);
            column1.Insert(insertAt, column1[templatePosition]);
            column2.Insert(insertAt, column2[templatePosition]);
            column3.Insert(insertAt, column3[templatePosition]);
        }

        int newBlockLength = checked(sizeof(uint) * 4 + ids.Count * (sizeof(byte) + sizeof(uint) + sizeof(byte) + 9));
        int oldBlockLength = oldEnd - byteColumn1Offset;
        byte[] output = new byte[checked(source.Length + newBlockLength - oldBlockLength)];
        source.AsSpan(0, byteColumn1Offset).CopyTo(output);
        int write = byteColumn1Offset;

        WriteCount(output, ref write, ids.Count);
        column1.ToArray().CopyTo(output, write);
        write += column1.Count;

        WriteCount(output, ref write, ids.Count);
        foreach (uint value in column2)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(write), value);
            write += sizeof(uint);
        }

        WriteCount(output, ref write, ids.Count);
        column3.ToArray().CopyTo(output, write);
        write += column3.Count;

        int writtenHandleCountOffset = write;
        WriteCount(output, ref write, ids.Count);
        foreach (ulong id in ids)
        {
            output[write++] = 0;
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(write), id);
            write += sizeof(ulong);
        }

        source.AsSpan(oldEnd).CopyTo(output.AsSpan(write));
        ValidateList(output, writtenHandleCountOffset, 9, 1, ids);
        if (write != byteColumn1Offset + newBlockLength)
            throw new InvalidDataException("DBUnlockables_default row clone wrote an invalid table length.");
        return output;
    }

    static void WriteCount(byte[] output, ref int offset, int count)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(offset), checked((uint)count));
        offset += sizeof(uint);
    }

    public static byte[] RewriteMembers(GunsmithAvailabilityList list, IReadOnlyList<ulong> members)
    {
        ArgumentNullException.ThrowIfNull(list);
        if (members.Count == 0 || members.Any(id => id == 0) || members.Distinct().Count() != members.Count)
            throw new InvalidOperationException("An attachment-type registry must contain unique, non-zero members.");

        byte[] updated = RewriteList(list.Owner.Data, list.CountOffset, list.EntryStride, list.ValueOffset, list.RecordIds, members);
        ValidateList(updated, list.CountOffset, list.EntryStride, list.ValueOffset, members);
        return updated;
    }

    static void InsertInCanonicalPosition(List<ulong> current, ulong id, IReadOnlyList<ulong> canonicalOrder)
    {
        int wanted = IndexOf(canonicalOrder, id);
        for (int index = wanted + 1; index < canonicalOrder.Count; index++)
        {
            int before = current.IndexOf(canonicalOrder[index]);
            if (before >= 0)
            {
                current.Insert(before, id);
                return;
            }
        }

        for (int index = wanted - 1; index >= 0; index--)
        {
            int after = current.IndexOf(canonicalOrder[index]);
            if (after >= 0)
            {
                current.Insert(after + 1, id);
                return;
            }
        }

        current.Add(id);
    }

    static int IndexOf(IReadOnlyList<ulong> values, ulong wanted)
    {
        for (int index = 0; index < values.Count; index++)
            if (values[index] == wanted)
                return index;
        return -1;
    }

    static byte[] RewriteList(byte[] source, int countOffset, int entryStride, int valueOffset, IReadOnlyList<ulong> original, IReadOnlyList<ulong> rewritten)
    {
        int entriesOffset = checked(countOffset + sizeof(uint));
        int sizeDelta = checked(entryStride * (rewritten.Count - original.Count));
        var output = new byte[checked(source.Length + sizeDelta)];
        Buffer.BlockCopy(source, 0, output, 0, countOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(countOffset), (uint)rewritten.Count);

        var originalEntries = new Dictionary<ulong, byte[]>();
        for (int item = 0; item < original.Count; item++)
        {
            int sourceOffset = entriesOffset + item * entryStride;
            originalEntries[original[item]] = source.AsSpan(sourceOffset, entryStride).ToArray();
        }

        for (int item = 0; item < rewritten.Count; item++)
        {
            ulong id = rewritten[item];
            int destination = entriesOffset + item * entryStride;
            if (originalEntries.TryGetValue(id, out byte[]? entry))
                entry.CopyTo(output, destination);
            else
            {
                originalEntries[original[0]].CopyTo(output, destination);
                BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(destination + valueOffset, sizeof(ulong)), id);
            }
        }

        int sourceAfter = entriesOffset + original.Count * entryStride;
        int destinationAfter = entriesOffset + rewritten.Count * entryStride;
        Buffer.BlockCopy(source, sourceAfter, output, destinationAfter, source.Length - sourceAfter);
        return output;
    }

    static void ValidateList(byte[] data, int countOffset, int entryStride, int valueOffset, IReadOnlyList<ulong> expected)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(countOffset)) != expected.Count)
            throw new InvalidOperationException("The Gunsmith list count did not update correctly.");

        int at = countOffset + sizeof(uint);
        for (int index = 0; index < expected.Count; index++)
            if (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(at + index * entryStride + valueOffset)) != expected[index])
                throw new InvalidOperationException("The rewritten Gunsmith list did not preserve its expected order.");
    }

    static bool MatchesIndexedBytes(ReadOnlySpan<byte> data, ArmoryIndex.AvailabilityListCandidate candidate)
    {
        int offset = candidate.CountOffset;
        if (offset < 0 || offset + sizeof(uint) + candidate.RecordIds.Length * candidate.EntryStride > data.Length
            || BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]) != candidate.RecordIds.Length)
            return false;

        for (int item = 0; item < candidate.RecordIds.Length; item++)
            if (BinaryPrimitives.ReadUInt64LittleEndian(data[(offset + sizeof(uint) + item * candidate.EntryStride + candidate.ValueOffset)..]) != candidate.RecordIds[item])
                return false;
        return true;
    }
}
