using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Wildlands.Formats.Data;

namespace Wildlands.Formats.Forge;

public sealed record SourceFile(byte[] Data, string Name, string ArchiveName, string ArchivePath, int EntryIndex);

public sealed record FoundResource(SourceFile File, Resource Resource, int ResourceIndex);

public sealed class ArchiveSet : IDisposable
{
    readonly ForgeArchive _archive;
    readonly Task<List<ForgeArchive>> _siblings;

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

    public FoundResource? FindResource(ulong id, uint classHash)
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

        return null;
    }

    public IEnumerable<ForgeArchive> All()
    {
        yield return _archive;

        foreach (var sibling in _siblings.Result)
            yield return sibling;
    }

    static SourceFile Describe(ForgeArchive archive, ForgeEntry entry)
    {
        return new SourceFile(archive.ReadEntry(entry), entry.Name + entry.FileExtension,
            Path.GetFileName(archive.FilePath), archive.FilePath, entry.Index);
    }

    List<ForgeArchive> OpenSiblings()
    {
        var opened = new List<ForgeArchive>();

        string? folder = Path.GetDirectoryName(Path.GetFullPath(_archive.FilePath));
        if (folder is null)
            return opened;

        foreach (string path in Directory.GetFiles(folder, "*.forge"))
        {
            if (string.Equals(path, _archive.FilePath, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                opened.Add(ForgeArchive.Open(path));
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
        foreach (var sibling in _siblings.Result)
            sibling.Dispose();
    }
}
