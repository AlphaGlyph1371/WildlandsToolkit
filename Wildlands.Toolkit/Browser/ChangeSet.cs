using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public sealed record PendingChange(
    string ArchivePath,
    int EntryIndex,
    string EntryName,
    int ResourceIndex,
    string ResourceName,
    byte[] Data);

public sealed class ArchiveWork
{
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public Dictionary<int, byte[]> Entries { get; } = [];
    public List<string> EntryNames { get; } = [];
    public bool NeedsRebuild { get; set; }

    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed class ChangeSet
{
    readonly List<PendingChange> _changes = [];

    public int Count => _changes.Count;
    public IReadOnlyList<PendingChange> Changes => _changes;

    public void Set(PendingChange change)
    {
        _changes.RemoveAll(x => x.ArchivePath == change.ArchivePath
            && x.EntryIndex == change.EntryIndex
            && x.ResourceIndex == change.ResourceIndex);

        _changes.Add(change);
    }

    public bool Contains(string archivePath, int entryIndex, int resourceIndex) =>
        _changes.Any(x => x.ArchivePath == archivePath
            && x.EntryIndex == entryIndex
            && x.ResourceIndex == resourceIndex);

    public void Clear() => _changes.Clear();

    // Builds every data file that changed and works out whether it still fits its place in
    // the archive. It does not write anything, so the caller can say what is about to happen
    // before it happens.
    public List<ArchiveWork> Plan(IProgress<string>? progress = null)
    {
        var plans = new List<ArchiveWork>();

        foreach (var perArchive in _changes.GroupBy(x => x.ArchivePath))
        {
            using var archive = ForgeArchive.Open(perArchive.Key);
            var work = new ArchiveWork { Path = perArchive.Key, Size = new FileInfo(perArchive.Key).Length };

            foreach (var perEntry in perArchive.GroupBy(x => x.EntryIndex))
            {
                var entry = archive.Entries.First(x => x.Index == perEntry.Key);
                progress?.Report($"Building {entry.Name}...");

                using var source = new MemoryStream(archive.ReadEntry(entry));
                var file = DataFile.Read(source);

                foreach (var change in perEntry)
                    file.Resources[change.ResourceIndex].Data = change.Data;

                using var rebuilt = new MemoryStream();
                file.Write(rebuilt);

                var data = rebuilt.ToArray();
                work.Entries[entry.Index] = data;
                work.EntryNames.Add(entry.Name);

                if (data.Length > archive.RoomFor(entry))
                    work.NeedsRebuild = true;
            }

            plans.Add(work);
        }

        return plans;
    }

    public void Write(IReadOnlyList<ArchiveWork> plans, IProgress<string>? progress = null)
    {
        foreach (var work in plans)
        {
            if (!ArchiveBackup.Exists(work.Path))
            {
                progress?.Report($"Backing up {work.Name}...");
                ArchiveBackup.Ensure(work.Path);
            }

            if (work.NeedsRebuild)
                Rebuild(work, progress);
            else
                PatchInPlace(work, progress);
        }

        _changes.Clear();
    }

    static void PatchInPlace(ArchiveWork work, IProgress<string>? progress)
    {
        using var archive = ForgeArchive.OpenForUpdate(work.Path);

        foreach (var (index, data) in work.Entries)
        {
            var entry = archive.Entries.First(x => x.Index == index);
            progress?.Report($"Writing {entry.Name}...");
            archive.ReplaceEntry(entry, data);
        }
    }

    static void Rebuild(ArchiveWork work, IProgress<string>? progress)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(work.Path))!);
        if (drive.AvailableFreeSpace < work.Size + (1L << 30))
            throw new IOException($"Rebuilding {work.Name} needs about {work.Size / (1L << 30) + 1} GB free on "
                + $"{drive.Name}, but only {drive.AvailableFreeSpace / (1L << 30)} GB are left.");

        string temp = work.Path + ".rebuild";

        using (var archive = ForgeArchive.Open(work.Path))
            archive.Rebuild(temp, work.Entries, progress);

        File.Move(temp, work.Path, overwrite: true);
    }
}
