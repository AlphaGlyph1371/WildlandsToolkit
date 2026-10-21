using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Wildlands.Formats.Data;

namespace Wildlands.Formats.Forge;

public sealed record SourceFile(byte[] Data, string Name, string ArchiveName, string ArchivePath, int EntryIndex);

public sealed record FoundResource(SourceFile File, Resource Resource, int ResourceIndex);

public sealed class ArchiveSet : IDisposable
{
    readonly ForgeArchive _archive;
    readonly Task<List<ForgeArchive>> _siblings;
    readonly CancellationTokenSource _stopOpening = new();
    readonly Dictionary<string, IReadOnlyList<FoundResource>> _resourceCopies = [];
    int _disposed;

    public ArchiveSet(ForgeArchive archive)
    {
        _archive = archive;
        _siblings = Task.Run(OpenSiblings);
    }

    public IEnumerable<SourceFile> Candidates(ulong id)
    {
        foreach (var archive in All())
        {
            var entry = archive.FindById(id);
            if (entry is not null)
                yield return Describe(archive, entry);
        }

        foreach (var archive in All())
        {
            var exact = archive.FindById(id);
            var entry = archive.FindContaining(id);

            if (entry is not null && entry != exact)
                yield return Describe(archive, entry);
        }
    }

    public FoundResource? FindResource(ulong id, uint classHash) => FindResource(id, classHash, 0);

    // Exact lookup for operations which must find every containing data file, not just
    // the nearest container ID. Scan all requested IDs together and cache this snapshot.
    public IReadOnlyList<FoundResource> FindResourceCopies(IReadOnlyCollection<ulong> ids,
        CancellationToken cancellationToken = default)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopOpening.Token);
        cancellationToken = stop.Token;
        cancellationToken.ThrowIfCancellationRequested();
        string key = string.Join(",", ids.Distinct().Order());
        lock (_resourceCopies)
        {
            if (_resourceCopies.TryGetValue(key, out var cached))
                return cached;
            var wanted = ids.ToHashSet();
            var found = new List<FoundResource>();
            foreach (string path in All().Select(archive => archive.FilePath).ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Separate readers keep this background scan independent of the viewers.
                using var archive = ForgeArchive.Open(path);
                foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    DataFile file;
                    byte[] bytes;
                    try
                    {
                        if (!archive.ReadResourceIndex(entry).Any(resource => wanted.Contains(resource.Id)))
                            continue;
                        bytes = archive.ReadEntry(entry);
                        file = DataFile.Read(new MemoryStream(bytes, writable: false));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
                    {
                        continue;
                    }
                    var source = new SourceFile(bytes, entry.Name + entry.FileExtension,
                        Path.GetFileName(path), path, entry.Index);
                    for (int index = 0; index < file.Resources.Count; index++)
                        if (wanted.Contains(file.Resources[index].Id))
                            found.Add(new FoundResource(source, file.Resources[index], index));
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            // Retain only the last query; full texture containers can be large.
            _resourceCopies.Clear();
            _resourceCopies.Add(key, found);
            return found;
        }
    }

    public FoundResource? FindResource(ulong id, uint classHash, ulong containerId)
    {
        foreach (var found in Candidates(id))
        {
            DataFile file;
            try
            {
                using var stream = new MemoryStream(found.Data);
                file = DataFile.Read(stream);
            }
            catch
            {
                continue;
            }

            int index = file.Resources.FindIndex(r => r.Id == id && r.ClassHash == classHash);
            if (index >= 0)
                return new FoundResource(found, file.Resources[index], index);
        }

        return containerId == 0 ? null : FindInContainer(id, classHash, containerId);
    }

    FoundResource? FindInContainer(ulong id, uint classHash, ulong containerId)
    {
        foreach (var archive in All())
        {
            var entry = archive.FindById(containerId);
            if (entry is null)
                continue;

            DataFile file;
            try
            {
                using var stream = new MemoryStream(archive.ReadEntry(entry));
                file = DataFile.Read(stream);
            }
            catch
            {
                continue;
            }

            int index = file.Resources.FindIndex(r => r.Id == id && r.ClassHash == classHash);
            if (index >= 0)
                return new FoundResource(Describe(archive, entry), file.Resources[index], index);
        }

        return null;
    }

    public IEnumerable<ForgeArchive> All()
    {
        return _siblings.Result.Append(_archive).OrderByDescending(x => Path.GetFileName(x.FilePath), StringComparer.OrdinalIgnoreCase);
    }

    static SourceFile Describe(ForgeArchive archive, ForgeEntry entry)
    {
        return new SourceFile(archive.ReadEntry(entry), entry.Name + entry.FileExtension, Path.GetFileName(archive.FilePath), archive.FilePath, entry.Index);
    }

    List<ForgeArchive> OpenSiblings()
    {
        var opened = new List<ForgeArchive>();
        List<string> paths;

        try
        {
            paths = ArchiveLocator.Siblings(_archive.FilePath);
        }
        catch
        {
            return opened;
        }

        foreach (string path in paths)
        {
            if (_stopOpening.IsCancellationRequested)
                break;

            try
            {
                var archive = ForgeArchive.Open(path);
                if (_stopOpening.IsCancellationRequested)
                {
                    archive.Dispose();
                    break;
                }

                opened.Add(archive);
            }
            catch
            {
                // An archive we cannot read is simply not searched
            }
        }

        return opened;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _stopOpening.Cancel();
        // A cancelled background lookup must release its readers before Apply writes.
        lock (_resourceCopies) { }
        foreach (var sibling in _siblings.Result)
            sibling.Dispose();
        _stopOpening.Dispose();
    }
}
