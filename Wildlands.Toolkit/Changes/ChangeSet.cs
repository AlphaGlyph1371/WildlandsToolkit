namespace Wildlands.Toolkit;

public sealed class ChangeSet
{
    readonly List<PendingChange> _changes = [];

    public int Count => _changes.Count;
    public IReadOnlyList<PendingChange> Changes => _changes;

    public void Set(PendingChange change)
    {
        if (change.EntryRemoval is null && _changes.Any(existing =>
                SameArchive(existing, change)
                && existing.EntryIndex == change.EntryIndex
                && existing.EntryRemoval is not null))
            return;

        _changes.RemoveAll(existing => SameArchive(existing, change)
            && (change.EntryAddition is not null
                ? existing.EntryAddition?.Id == change.EntryAddition.Id
                : change.EntryRemoval is not null
                    ? existing.EntryIndex == change.EntryIndex
                        || existing.EntryRemoval?.Id == change.EntryRemoval.Id
                    : existing.EntryIndex == change.EntryIndex && (change.Addition is not null
                        ? existing.Addition?.Id == change.Addition.Id
                        : existing.EntryAddition is null && existing.EntryRemoval is null
                            && (existing.ResourceIndex == change.ResourceIndex
                                || change.Removal is not null
                                    && existing.Addition?.Id == change.Removal.Id))));
        _changes.Add(change);
    }

    static bool SameArchive(PendingChange left, PendingChange right) =>
        left.ArchivePath.Equals(right.ArchivePath, StringComparison.OrdinalIgnoreCase);

    public bool Contains(string archivePath, int entryIndex, int resourceIndex) =>
        _changes.Any(change => change.ArchivePath == archivePath
            && change.EntryIndex == entryIndex
            && change.EntryAddition is null
            && change.EntryRemoval is null
            && change.ResourceIndex == resourceIndex);

    public PendingChange? Find(string archivePath, int entryIndex, int resourceIndex) =>
        _changes.LastOrDefault(change => change.ArchivePath == archivePath
            && change.EntryIndex == entryIndex
            && change.EntryAddition is null
            && change.EntryRemoval is null
            && change.Addition is null
            && change.Removal is null
            && change.ResourceIndex == resourceIndex);

    public void Clear() => _changes.Clear();

    public bool Remove(PendingChange change) => _changes.Remove(change);

    public List<ArchiveWork> Plan(IProgress<string>? progress = null) =>
        ArchiveChangePlanner.Plan(_changes, progress);

    public void Write(IReadOnlyList<ArchiveWork> plans, IProgress<string>? progress = null)
    {
        ArchiveWriteService.Write(plans, progress);
        _changes.Clear();
    }
}
