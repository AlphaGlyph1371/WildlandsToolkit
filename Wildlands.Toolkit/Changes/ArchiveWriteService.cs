using System.IO;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public static class ArchiveWriteService
{
    public static void Write(IReadOnlyList<ArchiveWork> plans,
        IProgress<string>? progress = null)
    {
        DiskSpacePreflight.Validate(plans);

        foreach (ArchiveWork work in plans)
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
    }

    static void PatchInPlace(ArchiveWork work, IProgress<string>? progress)
    {
        using ForgeArchive archive = ForgeArchive.OpenForUpdate(work.Path);
        foreach ((int index, byte[] data) in work.Entries)
        {
            ForgeEntry entry = archive.Entries.First(candidate => candidate.Index == index);
            progress?.Report($"Writing {entry.Name}...");
            archive.ReplaceEntry(entry, data);
        }
    }

    static void Rebuild(ArchiveWork work, IProgress<string>? progress)
    {
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(work.Path))!);
        long required = checked(work.OutputSize + DiskSpacePreflight.SafetyMargin);
        if (drive.AvailableFreeSpace < required)
            throw new IOException($"Rebuilding {work.Name} needs "
                + $"{DiskSpacePreflight.FormatBytes(required)} free on {drive.Name}, but only "
                + $"{DiskSpacePreflight.FormatBytes(drive.AvailableFreeSpace)} are available.");

        string temporary = work.Path + ".rebuild";
        using (ForgeArchive archive = ForgeArchive.Open(work.Path))
            archive.Rebuild(temporary, work.Entries, work.EntryAdditions, progress);
        File.Move(temporary, work.Path, overwrite: true);
    }
}
