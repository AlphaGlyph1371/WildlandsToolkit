namespace Wildlands.Toolkit;

public sealed class ChangeSet
{
    readonly List<PendingChange> _changes = [];

    public int Count => _changes.Count;
    public IReadOnlyList<PendingChange> Changes => _changes;

    public void Set(PendingChange change)
    {
        _changes.RemoveAll(existing => existing.ArchivePath == change.ArchivePath
            && (change.EntryAddition is not null
                ? existing.EntryAddition?.Id == change.EntryAddition.Id
                : existing.EntryIndex == change.EntryIndex && (change.Addition is not null
                    ? existing.Addition?.Id == change.Addition.Id
                    : existing.Addition is null && existing.EntryAddition is null
                        && existing.ResourceIndex == change.ResourceIndex)));
        _changes.Add(change);
    }

    public bool Contains(string archivePath, int entryIndex, int resourceIndex) =>
        _changes.Any(change => change.ArchivePath == archivePath
            && change.EntryIndex == entryIndex
            && change.EntryAddition is null
            && change.ResourceIndex == resourceIndex);

    public PendingChange? Find(string archivePath, int entryIndex, int resourceIndex) =>
        _changes.LastOrDefault(change => change.ArchivePath == archivePath
            && change.EntryIndex == entryIndex
            && change.EntryAddition is null
            && change.Addition is null
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
