using System.IO;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public static class ArchiveChangePlanner
{
    public static List<ArchiveWork> Plan(IReadOnlyList<PendingChange> changes,
        IProgress<string>? progress = null)
    {
        var plans = new List<ArchiveWork>();

        foreach (IGrouping<string, PendingChange> perArchive in changes.GroupBy(
                     change => change.ArchivePath))
        {
            using var archive = ForgeArchive.Open(perArchive.Key);
            var work = new ArchiveWork
            {
                Path = perArchive.Key,
                Size = new FileInfo(perArchive.Key).Length,
            };

            foreach (IGrouping<int, PendingChange> perEntry in perArchive
                         .Where(change => change.EntryAddition is null)
                         .GroupBy(change => change.EntryIndex))
            {
                ForgeEntry entry = archive.Entries.First(candidate =>
                    candidate.Index == perEntry.Key);
                progress?.Report($"Building {entry.Name}...");

                using var source = new MemoryStream(archive.ReadEntry(entry));
                DataFile file = DataFile.Read(source);

                foreach (PendingChange change in perEntry.Where(change => change.Addition is null))
                {
                    if ((uint)change.ResourceIndex >= (uint)file.Resources.Count)
                        throw new InvalidDataException(
                            $"Resource index {change.ResourceIndex} is outside {entry.Name}.");
                    file.Resources[change.ResourceIndex].Data = change.Data;
                }

                foreach (PendingChange change in perEntry.Where(change => change.Addition is not null))
                {
                    PendingResourceAddition addition = change.Addition!;
                    if (file.Resources.Any(resource => resource.Id == addition.Id))
                        throw new InvalidDataException(
                            $"{entry.Name} already contains resource 0x{addition.Id:X}.");
                    file.Resources.Add(new Resource
                    {
                        Id = addition.Id,
                        ClassHash = addition.ClassHash,
                        Name = change.ResourceName,
                        Header = (byte[])addition.Header.Clone(),
                        Data = (byte[])change.Data.Clone(),
                    });
                }

                using var rebuilt = new MemoryStream();
                file.Write(rebuilt);
                byte[] data = rebuilt.ToArray();
                work.Entries[entry.Index] = data;
                work.EntryNames.Add(entry.Name);

                if (data.Length > archive.RoomFor(entry))
                    work.NeedsRebuild = true;
            }

            foreach (PendingChange change in perArchive.Where(change =>
                         change.EntryAddition is not null))
            {
                PendingForgeEntryAddition addition = change.EntryAddition!;
                work.EntryAdditions.Add(new ForgeEntryAddition(addition.Id, addition.Name,
                    addition.Extension, (byte[])addition.InfoTemplate.Clone(),
                    (byte[])change.Data.Clone(), (byte[])addition.PrefetchBlock.Clone()));
                work.EntryNames.Add(addition.Name);
                work.NeedsRebuild = true;
            }

            work.OutputSize = work.NeedsRebuild
                ? archive.EstimateRebuiltSize(work.Entries, work.EntryAdditions)
                : work.Size;
            plans.Add(work);
        }

        return plans;
    }
}
