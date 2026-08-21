using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Wildlands.Formats.Forge;

public sealed class ForgeArchive : IDisposable
{
    const string Magic = "scimitar";
    const int SupportedVersion = 27;
    const int EntriesPerFileSet = 5000;
    const long FileAlignment = 32768;

    readonly Stream _stream;
    readonly BinaryReader _reader;

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
            throw new InvalidOperationException(
                $"{entry.Name} would be {data.Length} bytes and no longer fits the {room} bytes it has "
                + "in the archive. Moving it to the end of the file crashes the game, so it is not written.");

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

    // Writes every entry again, in the order they lie on disk, so that one of them may grow.
    // Everything before the first entry is kept byte for byte and only the two length fields
    // and the offset of each entry are pulled behind, which is why rebuilding an archive that
    // nothing replaced gives the same bytes back.
    public void Rebuild(string outputPath, IReadOnlyDictionary<int, byte[]> replacements,
        IProgress<string>? progress = null)
    {
        var ordered = Entries.OrderBy(e => e.Offset).ToList();
        if (ordered.Count == 0)
            throw new InvalidDataException("The archive has no entries to rebuild.");

        long prefixLength = ordered[0].Offset;

        foreach (var entry in Entries)
        {
            if (entry.LocationOffset + 20 > prefixLength || entry.InfoOffset + 4 > prefixLength)
                throw new InvalidDataException(
                    $"The tables of {entry.Name} lie behind the first entry, so this archive cannot be rebuilt.");
        }

        _stream.Position = 0;
        var prefix = _reader.ReadBytes((int)prefixLength);

        long tailStart = ordered[^1].Offset + ordered[^1].Length;
        _stream.Position = tailStart;
        var tail = _reader.ReadBytes((int)(_stream.Length - tailStart));

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        output.Position = prefixLength;

        int done = 0;
        foreach (var entry in ordered)
        {
            var data = replacements.TryGetValue(entry.Index, out var replacement) ? replacement : ReadEntry(entry);

            long offset = output.Position;
            output.Write(data, 0, data.Length);

            BitConverter.TryWriteBytes(prefix.AsSpan((int)entry.LocationOffset), offset);
            BitConverter.TryWriteBytes(prefix.AsSpan((int)entry.LocationOffset + 16), data.Length);
            BitConverter.TryWriteBytes(prefix.AsSpan((int)entry.InfoOffset), data.Length);

            entry.Offset = offset;
            entry.Length = data.Length;

            if (++done % 2000 == 0)
                progress?.Report($"Rebuilding {Path.GetFileName(FilePath)}, {done} of {ordered.Count} entries");
        }

        output.Write(tail, 0, tail.Length);
        output.SetLength((output.Position + FileAlignment - 1) / FileAlignment * FileAlignment);

        output.Position = 0;
        output.Write(prefix, 0, prefix.Length);
    }

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

        _reader.ReadUInt32();
        _reader.ReadUInt32();
        _stream.Position += 12;
        _reader.ReadInt64();
        _reader.ReadUInt32();

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
        int entryCount = _reader.ReadInt32();
        _reader.ReadInt32();
        _reader.ReadInt64();

        long nextSet = _reader.ReadInt64();
        _reader.ReadInt32();
        _reader.ReadInt32();
        long infoTable = _reader.ReadInt64();
        _reader.ReadInt64();

        long locationTable = _stream.Position;

        for (int i = 0; i < entryCount; i++)
        {
            var entry = new ForgeEntry
            {
                LocationOffset = locationTable + i * 20,
                InfoOffset = infoTable + (long)i * ForgeEntry.InfoSize,
            };

            _stream.Position = entry.LocationOffset;
            entry.ReadLocation(_reader);

            _stream.Position = entry.InfoOffset;
            entry.ReadInfo(_reader);

            entry.Index = setIndex * EntriesPerFileSet + i;
            Entries.Add(entry);
        }

        return nextSet;
    }

    public byte[] ReadEntry(ForgeEntry entry)
    {
        _stream.Position = entry.Offset;
        return _reader.ReadBytes(entry.Length);
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
}
