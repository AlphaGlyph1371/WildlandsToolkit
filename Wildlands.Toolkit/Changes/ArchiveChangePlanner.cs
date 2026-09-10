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
            HashSet<int> deletedEntries = perArchive
                .Where(change => change.EntryRemoval is not null)
                .Select(change => change.EntryIndex)
                .ToHashSet();

            foreach (IGrouping<int, PendingChange> perEntry in perArchive
                         .Where(change => change.EntryAddition is null
                             && change.EntryRemoval is null
                             && !deletedEntries.Contains(change.EntryIndex))
                         .GroupBy(change => change.EntryIndex))
            {
                ForgeEntry entry = archive.Entries.First(candidate =>
                    candidate.Index == perEntry.Key);
                progress?.Report($"Building {entry.Name}...");

                using var source = new MemoryStream(archive.ReadEntry(entry));
                DataFile file = DataFile.Read(source);

                foreach (PendingChange change in perEntry.Where(change =>
                             change.Addition is null && change.Removal is null))
                {
                    if ((uint)change.ResourceIndex >= (uint)file.Resources.Count)
                        throw new InvalidDataException(
                            $"Resource index {change.ResourceIndex} is outside {entry.Name}.");
                    file.Resources[change.ResourceIndex].Data = change.Data;
                }

                foreach (PendingChange change in perEntry.Where(change => change.Removal is not null)
                             .OrderByDescending(change => change.ResourceIndex))
                {
                    PendingResourceRemoval removal = change.Removal!;
                    if ((uint)change.ResourceIndex >= (uint)file.Resources.Count)
                        throw new InvalidDataException(
                            $"Resource index {change.ResourceIndex} is outside {entry.Name}.");
                    Resource resource = file.Resources[change.ResourceIndex];
                    if (resource.Id != removal.Id || resource.ClassHash != removal.ClassHash)
                        throw new InvalidDataException(
                            $"{change.ResourceName} no longer identifies the resource queued for deletion.");
                    file.Resources.RemoveAt(change.ResourceIndex);
                }

                foreach (PendingChange change in perEntry.Where(change => change.Addition is not null))
                {
                    PendingResourceAddition addition = change.Addition!;
                    if (file.Resources.Any(resource => resource.Id == addition.Id))
                        throw new InvalidDataException(
                            $"{entry.Name} already contains resource 0x{addition.Id:X}.");
                    int insertionIndex = file.Resources.Count;
                    if (addition.InsertAfterResourceId != 0)
                    {
                        int templateIndex = file.Resources.FindIndex(resource =>
                            resource.Id == addition.InsertAfterResourceId);
                        if (templateIndex < 0)
                            throw new InvalidDataException(
                                $"{entry.Name} does not contain insertion anchor 0x{addition.InsertAfterResourceId:X} for {change.ResourceName}.");
                        insertionIndex = templateIndex + 1;
                    }
                    file.Resources.Insert(insertionIndex, new Resource
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


            foreach (PendingChange change in perArchive.Where(change =>
                         change.EntryRemoval is not null))
            {
                ForgeEntry entry = archive.Entries.FirstOrDefault(candidate =>
                    candidate.Index == change.EntryIndex
                    && candidate.Id == change.EntryRemoval!.Id)
                    ?? throw new InvalidDataException(
                        $"{change.EntryName} no longer identifies the Forge entry queued for deletion.");
                work.EntryRemovals.Add(entry.Index);
                work.EntryNames.Add(entry.Name);
                work.NeedsRebuild = true;
            }

            if (work.EntryAdditions.Count > 0 && work.EntryRemovals.Any(index =>
                    archive.Entries[index].Id is 16 or 145))
                throw new InvalidOperationException(
                    "GlobalMetaFile or PrefetchingFileInfos cannot be deleted in the same Apply operation that adds a Forge container. Apply the additions or deletions separately.");

            work.OutputSize = work.NeedsRebuild
                ? archive.EstimateRebuiltSize(work.Entries, work.EntryAdditions,
                    work.EntryRemovals)
                : work.Size;
            if (work.EntryAdditions.Count > 0 && work.EntryRemovals.Count > 0)
                work.IntermediateSize = archive.EstimateRebuiltSize(work.Entries, [],
                    work.EntryRemovals);
            plans.Add(work);
        }

        return plans;
    }

}
