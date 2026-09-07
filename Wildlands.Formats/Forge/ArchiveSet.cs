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
        foreach (var sibling in _siblings.Result)
            sibling.Dispose();
        _stopOpening.Dispose();
    }
}
