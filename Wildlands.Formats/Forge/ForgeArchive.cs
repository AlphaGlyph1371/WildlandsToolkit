using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Wildlands.Formats.Data;

namespace Wildlands.Formats.Forge;

public sealed class ForgeArchive : IDisposable
{
    const string Magic = "scimitar";
    const int SupportedVersion = 27;
    const long FileAlignment = 32768;

    readonly Stream _stream;
    readonly BinaryReader _reader;
    readonly List<ForgeFileSet> _fileSets = [];
    long _totalEntryCountOffset;
    long _totalEntryLimitOffset;
    int _totalEntryLimit;

    ForgeArchive(Stream stream, string path)
    {
        _stream = stream;
        _reader = new BinaryReader(stream);
        FilePath = path;
        Entries = new List<ForgeEntry>();
    }

    public string FilePath { get; }
    public uint Version { get; private set; }
    public List<ForgeEntry> Entries { get; }

    public static ForgeArchive Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 18);
        var archive = new ForgeArchive(stream, path);
        archive.ReadHeader();
        return archive;
    }

    public static ForgeArchive OpenForUpdate(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1 << 18);
        var archive = new ForgeArchive(stream, path);
        archive.ReadHeader();
        return archive;
    }

    public void ReplaceEntry(ForgeEntry entry, byte[] data)
    {
        long room = RoomFor(entry);
        if (data.Length > room)
            throw new InvalidOperationException($"{entry.Name} would be {data.Length} bytes and no longer fits the {room} bytes it has in the archive. Moving it to the end of the file crashes the game, so it is not written.");

        _stream.Position = entry.Offset;
        _stream.Write(data, 0, data.Length);

        var writer = new BinaryWriter(_stream);

        _stream.Position = entry.LocationOffset + 16;
        writer.Write(data.Length);

        _stream.Position = entry.InfoOffset;
        writer.Write(data.Length);
        _stream.Flush();

        entry.Length = data.Length;
    }

    public void Rebuild(string outputPath, IReadOnlyDictionary<int, byte[]> replacements, IProgress<string>? progress = null) =>
        Rebuild(outputPath, replacements, [], [], progress);

    public void Rebuild(string outputPath, IReadOnlyDictionary<int, byte[]> replacements, IReadOnlyList<ForgeEntryAddition> additions, IProgress<string>? progress = null) =>
        Rebuild(outputPath, replacements, additions, [], progress);

    public void Rebuild(string outputPath, IReadOnlyDictionary<int, byte[]> replacements, IReadOnlyList<ForgeEntryAddition> additions, IReadOnlyCollection<int> removals, IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(additions);
        ArgumentNullException.ThrowIfNull(removals);
        if (additions.Count > 0 && removals.Count > 0)
            throw new InvalidOperationException("Forge entry additions and deletions must be written in separate rebuild phases.");
        HashSet<int> removed = ValidateRemovals(removals);
        var effectiveReplacements = PrepareReplacements(replacements, additions, removed);
        var ordered = Entries.OrderBy(e => e.Offset).ToList();
        if (ordered.Count == 0)
            throw new InvalidDataException("The archive has no entries to rebuild.");

        long prefixLength = ordered[0].Offset;

        foreach (var entry in Entries)
        {
            if (entry.LocationOffset + 20 > prefixLength || entry.InfoOffset + 4 > prefixLength)
                throw new InvalidDataException($"The tables of {entry.Name} lie behind the first entry, so this archive cannot be rebuilt.");
        }

        _stream.Position = 0;
        var prefix = _reader.ReadBytes((int)prefixLength);
        var locationOffsets = Entries.ToDictionary(entry => entry.Index, entry => entry.LocationOffset);
        var infoOffsets = Entries.ToDictionary(entry => entry.Index, entry => entry.InfoOffset);
        AdditionTableLayout? additionLayout = additions.Count > 0 ? PrepareAdditionTables(prefix, additions, locationOffsets, infoOffsets) : null;
        if (removed.Count > 0)
            PrepareRemovalTables(prefix, removed, locationOffsets, infoOffsets);

        long tailStart = ordered[^1].Offset + ordered[^1].Length;
        _stream.Position = tailStart;
        var tail = _reader.ReadBytes((int)(_stream.Length - tailStart));

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        output.Position = prefixLength;

        int done = 0;
        bool additionsWritten = false;
        foreach (var entry in ordered)
        {
            if (removed.Contains(entry.Index))
                continue;

            if (additionLayout is not null && entry.Index == additionLayout.InsertEntryIndex)
            {
                WriteAdditions(output, prefix, additionLayout, additions, progress);
                additionsWritten = true;
            }

            var data = effectiveReplacements.TryGetValue(entry.Index, out var replacement) ? replacement : ReadEntry(entry);

            long offset = output.Position;
            output.Write(data, 0, data.Length);

            BitConverter.TryWriteBytes(prefix.AsSpan((int)locationOffsets[entry.Index]), offset);
            BitConverter.TryWriteBytes(prefix.AsSpan((int)locationOffsets[entry.Index] + 16), data.Length);
            BitConverter.TryWriteBytes(prefix.AsSpan((int)infoOffsets[entry.Index]), data.Length);

            entry.Offset = offset;
            entry.Length = data.Length;

            if (++done % 2000 == 0)
                progress?.Report($"Rebuilding {Path.GetFileName(FilePath)}, {done} of {ordered.Count} entries");
        }

        if (additionLayout is not null && !additionsWritten && additionLayout.InsertEntryIndex == Entries.Count)
            WriteAdditions(output, prefix, additionLayout, additions, progress);

        output.Write(tail, 0, tail.Length);
        output.SetLength(Align(output.Position));

        output.Position = 0;
        output.Write(prefix, 0, prefix.Length);
    }

    public long EstimateRebuiltSize(IReadOnlyDictionary<int, byte[]> replacements, IReadOnlyList<ForgeEntryAddition> additions) =>
        EstimateRebuiltSize(replacements, additions, []);

    public long EstimateRebuiltSize(IReadOnlyDictionary<int, byte[]> replacements, IReadOnlyList<ForgeEntryAddition> additions, IReadOnlyCollection<int> removals)
    {
        ArgumentNullException.ThrowIfNull(additions);
        ArgumentNullException.ThrowIfNull(removals);
        HashSet<int> removed = ValidateRemovals(removals);
        Dictionary<int, byte[]> effectiveReplacements = PrepareReplacements(replacements, additions, removed);
        List<ForgeEntry> ordered = Entries.OrderBy(entry => entry.Offset).ToList();
        if (ordered.Count == 0)
            throw new InvalidDataException("The archive has no entries to rebuild.");

        ValidateTableChanges(ordered[0].Offset, additions, removed);

        long size = ordered[0].Offset;
        foreach (ForgeEntry entry in ordered)
        {
            if (removed.Contains(entry.Index))
                continue;
            size = checked(size + (effectiveReplacements.TryGetValue(entry.Index, out byte[]? data) ? data.Length : entry.Length));
        }
        foreach (ForgeEntryAddition addition in additions)
            size = checked(size + addition.Data.Length);

        long tailStart = checked(ordered[^1].Offset + ordered[^1].Length);
        size = checked(size + (_stream.Length - tailStart));
        return Align(size);
    }

    void ValidateTableChanges(long prefixLength, IReadOnlyList<ForgeEntryAddition> additions, IReadOnlySet<int> removals)
    {
        if (additions.Count == 0 && removals.Count == 0)
            return;
        if (prefixLength > int.MaxValue)
            throw new InvalidDataException("The Forge header is too large to rebuild.");

        _stream.Position = 0;
        byte[] original = _reader.ReadBytes((int)prefixLength);
        if (original.Length != prefixLength)
            throw new InvalidDataException("The Forge header is truncated.");

        if (additions.Count > 0)
        {
            byte[] prefix = (byte[])original.Clone();
            var locations = Entries.ToDictionary(entry => entry.Index, entry => entry.LocationOffset);
            var infos = Entries.ToDictionary(entry => entry.Index, entry => entry.InfoOffset);
            _ = PrepareAdditionTables(prefix, additions, locations, infos);
        }
        if (removals.Count > 0)
        {
            byte[] prefix = (byte[])original.Clone();
            var locations = Entries.ToDictionary(entry => entry.Index, entry => entry.LocationOffset);
            var infos = Entries.ToDictionary(entry => entry.Index, entry => entry.InfoOffset);
            PrepareRemovalTables(prefix, removals, locations, infos);
        }
    }

    Dictionary<int, byte[]> PrepareReplacements(IReadOnlyDictionary<int, byte[]> replacements, IReadOnlyList<ForgeEntryAddition> additions, IReadOnlySet<int> removals)
    {
        var effectiveReplacements = replacements.ToDictionary(item => item.Key, item => item.Value);
        if (effectiveReplacements.Keys.Any(removals.Contains))
            throw new InvalidDataException("A deleted Forge entry cannot also be replaced.");

        if (additions.Count > 0 || removals.Count > 0)
        {
            ForgeEntry? prefetch = Entries.SingleOrDefault(entry => entry.Id == 145);
            if (prefetch is null && additions.Count > 0)
                throw new InvalidDataException("The Forge archive has no PrefetchingFileInfos registry for new entries.");
            if (prefetch is null)
                return effectiveReplacements;
            if (!removals.Contains(prefetch.Index))
            {
                byte[] currentPrefetch = effectiveReplacements.TryGetValue(prefetch.Index, out byte[]? replacement) ? replacement : ReadEntry(prefetch);
                if (removals.Count > 0)
                    currentPrefetch = PrefetchingFileInfos.RemoveObjects(currentPrefetch, removals.Select(index => Entries[index].Id).ToArray());
                if (additions.Count > 0)
                    currentPrefetch = PrefetchingFileInfos.AddObjects(currentPrefetch, additions.Select(addition => (addition.Id, addition.PrefetchBlock)).ToList());
                effectiveReplacements[prefetch.Index] = currentPrefetch;
            }
        }
        return effectiveReplacements;
    }

    HashSet<int> ValidateRemovals(IReadOnlyCollection<int> removals)
    {
        var result = removals.ToHashSet();
        if (result.Any(index => (uint)index >= (uint)Entries.Count))
            throw new InvalidDataException("A Forge entry deletion points outside the archive.");
        if (result.Count == Entries.Count)
            throw new InvalidOperationException("Deleting every entry would no longer produce a valid Forge archive.");
        return result;
    }

    static long Align(long value) => checked((value + FileAlignment - 1) / FileAlignment * FileAlignment);

    AdditionTableLayout PrepareAdditionTables(byte[] prefix, IReadOnlyList<ForgeEntryAddition> additions, Dictionary<int, long> locationOffsets, Dictionary<int, long> infoOffsets)
    {
        if (_fileSets.Count == 0)
            throw new InvalidDataException("The Forge archive has no file set for new entries.");
        if (additions.Any(addition => addition.Id == 0 || addition.Data.Length == 0 || addition.PrefetchBlock.Length == 0 || addition.InfoTemplate.Length != ForgeEntry.InfoSize))
            throw new InvalidDataException("A new Forge entry has incomplete data or metadata.");
        var duplicate = Entries.Select(entry => entry.Id)
            .Concat(additions.Select(addition => addition.Id))
            .Where(id => id != 0)
            .GroupBy(id => id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"Forge entry ID 0x{duplicate.Key:X} is already in use.");
        var duplicateUmac = Entries.Select(entry => entry.UmacHash)
            .Concat(additions.Select(addition => BitConverter.ToUInt64(addition.InfoTemplate, 4)))
            .GroupBy(hash => hash)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateUmac is not null)
            throw new InvalidDataException($"Forge entry UMAC 0x{duplicateUmac.Key:X16} is already in use.");

        ForgeFileSet set = _fileSets[^1];
        int insertLocalIndex = FindAdditionInsertionIndex(set);
        int newSetCount = checked(set.EntryCount + additions.Count);
        int newTotal = checked(Entries.Count + additions.Count);
        long oldLocationEnd = checked(set.LocationTable + (long)set.EntryCount * 20);
        long preservedGap = Math.Max(0, set.InfoTable - oldLocationEnd);
        long newLocationEnd = checked(set.LocationTable + (long)newSetCount * 20);
        long newInfoTable = checked(newLocationEnd + preservedGap);
        long oldEntryInfoEnd = checked(set.InfoTable + (long)set.EntryCount * ForgeEntry.InfoSize);
        long newEntryInfoEnd = checked(newInfoTable + (long)newSetCount * ForgeEntry.InfoSize);
        if (set.InfoEnd < oldEntryInfoEnd || newEntryInfoEnd > prefix.Length)
            throw new InvalidOperationException("The Forge header has no room to register another entry without moving game data.");

        long tableGrowth = checked(newEntryInfoEnd - oldEntryInfoEnd);
        if (tableGrowth <= 0 || tableGrowth > prefix.Length - oldEntryInfoEnd)
            throw new InvalidDataException("The Forge entry tables have an invalid growth range.");

        ReadOnlySpan<byte> discarded = prefix.AsSpan(checked(prefix.Length - (int)tableGrowth), checked((int)tableGrowth));
        if (discarded.IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidOperationException("The Forge header has no empty tail room for another registered entry.");
        byte[] trailer = prefix.AsSpan(checked((int)oldEntryInfoEnd), checked(prefix.Length - (int)oldEntryInfoEnd - (int)tableGrowth)).ToArray();
        prefix.AsSpan(checked((int)oldEntryInfoEnd)).Clear();
        trailer.CopyTo(prefix.AsSpan(checked((int)newEntryInfoEnd)));

        byte[] oldLocations = prefix.AsSpan((int)set.LocationTable, checked(set.EntryCount * 20)).ToArray();
        byte[] oldInfos = prefix.AsSpan((int)set.InfoTable, checked(set.EntryCount * ForgeEntry.InfoSize)).ToArray();

        int sentinel = BitConverter.ToInt32(oldInfos, checked((set.EntryCount - 1) * ForgeEntry.InfoSize + 28)) == -1 ? -1 : newSetCount;

        prefix.AsSpan((int)set.LocationTable, checked(newSetCount * 20)).Clear();
        oldLocations.AsSpan(0, checked(insertLocalIndex * 20))
            .CopyTo(prefix.AsSpan((int)set.LocationTable));
        oldLocations.AsSpan(checked(insertLocalIndex * 20))
            .CopyTo(prefix.AsSpan(checked((int)set.LocationTable + (insertLocalIndex + additions.Count) * 20)));

        prefix.AsSpan((int)newInfoTable, checked(newSetCount * ForgeEntry.InfoSize)).Clear();
        oldInfos.AsSpan(0, checked(insertLocalIndex * ForgeEntry.InfoSize))
            .CopyTo(prefix.AsSpan((int)newInfoTable));
        oldInfos.AsSpan(checked(insertLocalIndex * ForgeEntry.InfoSize))
            .CopyTo(prefix.AsSpan(checked((int)newInfoTable + (insertLocalIndex + additions.Count) * ForgeEntry.InfoSize)));

        for (int i = 0; i < set.EntryCount; i++)
        {
            int shiftedIndex = i < insertLocalIndex ? i : i + additions.Count;
            long locationOffset = set.LocationTable + (long)shiftedIndex * 20;
            long infoOffset = newInfoTable + (long)shiftedIndex * ForgeEntry.InfoSize;
            locationOffsets[set.FirstEntryIndex + i] = locationOffset;
            infoOffsets[set.FirstEntryIndex + i] = infoOffset;
            WriteInt32(prefix, infoOffset + 28, shiftedIndex + 1 < newSetCount ? shiftedIndex + 1 : sentinel);
            WriteInt32(prefix, infoOffset + 32, shiftedIndex - 1);
        }

        WriteInt32(prefix, _totalEntryCountOffset, newTotal);
        WriteInt32(prefix, set.EntryCountOffset, newSetCount);
        WriteInt32(prefix, _totalEntryLimitOffset, checked(_totalEntryLimit + additions.Count));
        WriteInt32(prefix, set.DuplicateCountOffset, checked(set.DuplicateCount + additions.Count));
        WriteInt64(prefix, set.InfoTablePointerOffset, newInfoTable);
        WriteInt64(prefix, set.InfoEndPointerOffset, checked(set.InfoEnd + tableGrowth));

        return new AdditionTableLayout(set.LocationTable, newInfoTable, insertLocalIndex, additions.Count, set.FirstEntryIndex + insertLocalIndex, sentinel);
    }

    void PrepareRemovalTables(byte[] prefix, IReadOnlySet<int> removals, Dictionary<int, long> locationOffsets, Dictionary<int, long> infoOffsets)
    {
        WriteInt32(prefix, _totalEntryCountOffset, checked(Entries.Count - removals.Count));
        WriteInt32(prefix, _totalEntryLimitOffset, checked(_totalEntryLimit - removals.Count));

        foreach (ForgeFileSet set in _fileSets)
        {
            List<int> kept = Enumerable.Range(0, set.EntryCount)
                .Where(local => !removals.Contains(set.FirstEntryIndex + local))
                .ToList();
            int removedFromSet = set.EntryCount - kept.Count;
            if (removedFromSet == 0)
                continue;

            byte[] oldLocations = prefix.AsSpan((int)set.LocationTable, checked(set.EntryCount * 20)).ToArray();
            byte[] oldInfos = prefix.AsSpan((int)set.InfoTable, checked(set.EntryCount * ForgeEntry.InfoSize)).ToArray();
            int sentinel = set.EntryCount > 0 && BitConverter.ToInt32(oldInfos, checked((set.EntryCount - 1) * ForgeEntry.InfoSize + 28)) == -1 ? -1 : kept.Count;

            prefix.AsSpan((int)set.LocationTable, checked(set.EntryCount * 20)).Clear();
            prefix.AsSpan((int)set.InfoTable, checked(set.EntryCount * ForgeEntry.InfoSize)).Clear();

            for (int newLocal = 0; newLocal < kept.Count; newLocal++)
            {
                int oldLocal = kept[newLocal];
                int entryIndex = set.FirstEntryIndex + oldLocal;
                long locationOffset = set.LocationTable + (long)newLocal * 20;
                long infoOffset = set.InfoTable + (long)newLocal * ForgeEntry.InfoSize;
                oldLocations.AsSpan(oldLocal * 20, 20)
                    .CopyTo(prefix.AsSpan((int)locationOffset, 20));
                oldInfos.AsSpan(oldLocal * ForgeEntry.InfoSize, ForgeEntry.InfoSize)
                    .CopyTo(prefix.AsSpan((int)infoOffset, ForgeEntry.InfoSize));
                WriteInt32(prefix, infoOffset + 28, newLocal + 1 < kept.Count ? newLocal + 1 : sentinel);
                WriteInt32(prefix, infoOffset + 32, newLocal - 1);
                locationOffsets[entryIndex] = locationOffset;
                infoOffsets[entryIndex] = infoOffset;
            }

            WriteInt32(prefix, set.EntryCountOffset, kept.Count);
            WriteInt32(prefix, set.DuplicateCountOffset, checked(set.DuplicateCount - removedFromSet));
        }
    }

    int FindAdditionInsertionIndex(ForgeFileSet set)
    {
        int index = set.EntryCount;
        if (index > 0 && Entries[set.FirstEntryIndex + index - 1] is
            { Id: 145, Name: "PrefetchingFileInfos" })
            index--;
        if (index > 0 && Entries[set.FirstEntryIndex + index - 1] is
            { Id: 16, Name: "GlobalMetaFile" })
            index--;
        if (index == set.EntryCount)
            throw new InvalidDataException("New Forge entries require the original trailing GlobalMetaFile and PrefetchingFileInfos entries.");
        return index;
    }

    void WriteAdditions(Stream output, byte[] prefix, AdditionTableLayout layout, IReadOnlyList<ForgeEntryAddition> additions, IProgress<string>? progress)
    {
        for (int i = 0; i < additions.Count; i++)
        {
            ForgeEntryAddition addition = additions[i];
            long offset = output.Position;
            output.Write(addition.Data, 0, addition.Data.Length);
            WriteAddition(prefix, layout, i, addition, offset);
            progress?.Report($"Adding {addition.Name} to {Path.GetFileName(FilePath)}...");
        }
    }

    void WriteAddition(byte[] prefix, AdditionTableLayout layout, int index, ForgeEntryAddition addition, long offset)
    {
        long locationOffset = layout.LocationTable + (long)(layout.InsertLocalIndex + index) * 20;
        WriteInt64(prefix, locationOffset, offset);
        WriteUInt64(prefix, locationOffset + 8, addition.Id);
        WriteInt32(prefix, locationOffset + 16, addition.Data.Length);

        byte[] info = (byte[])addition.InfoTemplate.Clone();
        WriteInt32(info, 0, addition.Data.Length);
        WriteUInt32(info, 16, addition.Extension);
        int entryIndex = layout.InsertLocalIndex + index;
        bool hasNext = index + 1 < layout.AdditionCount || layout.InsertEntryIndex < Entries.Count;
        WriteInt32(info, 28, hasNext ? entryIndex + 1 : layout.Sentinel);
        WriteInt32(info, 32, entryIndex - 1);
        byte[] name = Encoding.UTF8.GetBytes(addition.Name);
        if (name.Length >= 128)
            throw new InvalidDataException("A new Forge entry name must be shorter than 128 UTF-8 bytes.");
        Array.Clear(info, 44, 128);
        name.CopyTo(info.AsSpan(44));
        long infoOffset = layout.InfoTable + (long)(layout.InsertLocalIndex + index) * ForgeEntry.InfoSize;
        info.CopyTo(prefix.AsSpan((int)infoOffset));
    }

    static void WriteInt32(byte[] target, long offset, int value) =>
        BitConverter.TryWriteBytes(target.AsSpan(checked((int)offset)), value);

    static void WriteUInt32(byte[] target, long offset, uint value) =>
        BitConverter.TryWriteBytes(target.AsSpan(checked((int)offset)), value);

    static void WriteInt64(byte[] target, long offset, long value) =>
        BitConverter.TryWriteBytes(target.AsSpan(checked((int)offset)), value);

    static void WriteUInt64(byte[] target, long offset, ulong value) =>
        BitConverter.TryWriteBytes(target.AsSpan(checked((int)offset)), value);

    public long RoomFor(ForgeEntry entry)
    {
        long next = _stream.Length;

        foreach (var other in Entries)
        {
            if (other.Offset > entry.Offset && other.Offset < next)
                next = other.Offset;
        }

        return next - entry.Offset;
    }

    void ReadHeader()
    {
        if (ReadNullTerminatedString() != Magic)
            throw new InvalidDataException("Not a forge archive.");

        Version = _reader.ReadUInt32();
        if (Version != SupportedVersion)
            throw new NotSupportedException($"Forge version {Version} is not supported, expected {SupportedVersion}.");

        ulong headerSize = _reader.ReadUInt64();
        _stream.Position += (long)headerSize - 21;

        _totalEntryCountOffset = _stream.Position;
        _reader.ReadUInt32();
        _reader.ReadUInt32();
        _stream.Position += 12;
        _reader.ReadInt64();
        _totalEntryLimitOffset = _stream.Position;
        _totalEntryLimit = _reader.ReadInt32();

        int fileSetCount = _reader.ReadInt32();
        long next = _reader.ReadInt64();

        for (int i = 0; i < fileSetCount && next >= 0; i++)
        {
            _stream.Position = next;
            next = ReadFileSet(i);
        }
    }

    long ReadFileSet(int setIndex)
    {
        int firstEntryIndex = Entries.Count;
        long entryCountOffset = _stream.Position;
        int entryCount = _reader.ReadInt32();
        _reader.ReadInt32();
        _reader.ReadInt64();

        long nextSet = _reader.ReadInt64();
        _reader.ReadInt32();
        long duplicateCountOffset = _stream.Position;
        int duplicateCount = _reader.ReadInt32();
        long infoTablePointerOffset = _stream.Position;
        long infoTable = _reader.ReadInt64();
        long infoEndPointerOffset = _stream.Position;
        long infoEnd = _reader.ReadInt64();

        long locationTable = _stream.Position;
        _fileSets.Add(new ForgeFileSet(firstEntryIndex, entryCount, entryCountOffset, duplicateCountOffset, duplicateCount, infoTablePointerOffset, infoEndPointerOffset, locationTable, infoTable, infoEnd));

        var locations = new byte[checked(entryCount * 20)];
        _stream.Position = locationTable;
        _stream.ReadExactly(locations);

        var infos = new byte[checked(entryCount * ForgeEntry.InfoSize)];
        _stream.Position = infoTable;
        _stream.ReadExactly(infos);

        using var locationReader = new BinaryReader(new MemoryStream(locations, writable: false));
        using var infoReader = new BinaryReader(new MemoryStream(infos, writable: false));

        for (int i = 0; i < entryCount; i++)
        {
            var entry = new ForgeEntry
            {
                LocationOffset = locationTable + i * 20,
                InfoOffset = infoTable + (long)i * ForgeEntry.InfoSize,
            };

            entry.ReadLocation(locationReader);
            entry.ReadInfo(infoReader);

            entry.Index = Entries.Count;
            Entries.Add(entry);
        }

        return nextSet;
    }

    public byte[] ReadEntry(ForgeEntry entry)
    {
        _stream.Position = entry.Offset;
        return _reader.ReadBytes(entry.Length);
    }

    public byte[] ReadEntryInfo(ForgeEntry entry)
    {
        _stream.Position = entry.InfoOffset;
        return _reader.ReadBytes(ForgeEntry.InfoSize);
    }

    public IReadOnlyList<ResourceIndexEntry> ReadResourceIndex(ForgeEntry entry)
    {
        _stream.Position = entry.Offset;
        return DataFile.ReadResourceIndex(_stream);
    }

    public bool ContainsResourceId(ForgeEntry entry, ulong id)
    {
        _stream.Position = entry.Offset;
        return DataFile.ContainsResourceId(_stream, id);
    }

    Dictionary<ulong, ForgeEntry>? _byId;
    ForgeEntry[]? _sortedById;

    public ForgeEntry? FindById(ulong id)
    {
        _byId ??= BuildIdLookup();
        return _byId.GetValueOrDefault(id);
    }

    public ForgeEntry? FindContaining(ulong id)
    {
        _sortedById ??= BuildSortedIds();

        int low = 0;
        int high = _sortedById.Length - 1;
        ForgeEntry? best = null;

        while (low <= high)
        {
            int middle = (low + high) / 2;
            if (_sortedById[middle].Id <= id)
            {
                best = _sortedById[middle];
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return best;
    }

    Dictionary<ulong, ForgeEntry> BuildIdLookup()
    {
        var lookup = new Dictionary<ulong, ForgeEntry>(Entries.Count);
        foreach (var entry in Entries)
            lookup.TryAdd(entry.Id, entry);
        return lookup;
    }

    ForgeEntry[] BuildSortedIds()
    {
        var sorted = Entries.ToArray();
        Array.Sort(sorted, (a, b) => a.Id.CompareTo(b.Id));
        return sorted;
    }

    public void ExtractAll(string outputDirectory, IProgress<int>? progress = null)
    {
        Directory.CreateDirectory(outputDirectory);

        for (int i = 0; i < Entries.Count; i++)
        {
            var entry = Entries[i];
            string name = $"{entry.Index}_-_{Path.GetFileNameWithoutExtension(entry.Name)}{entry.FileExtension}";
            File.WriteAllBytes(Path.Combine(outputDirectory, name), ReadEntry(entry));
            progress?.Report(i + 1);
        }
    }

    string ReadNullTerminatedString()
    {
        var builder = new StringBuilder();
        byte b;
        while ((b = _reader.ReadByte()) != 0)
            builder.Append((char)b);
        return builder.ToString();
    }

    public void Dispose()
    {
        _reader.Dispose();
        _stream.Dispose();
    }

    sealed record ForgeFileSet(int FirstEntryIndex, int EntryCount, long EntryCountOffset, long DuplicateCountOffset, int DuplicateCount,
        long InfoTablePointerOffset, long InfoEndPointerOffset, long LocationTable, long InfoTable, long InfoEnd);

    sealed record AdditionTableLayout(long LocationTable, long InfoTable,
        int InsertLocalIndex, int AdditionCount, int InsertEntryIndex, int Sentinel);
}
