using System.IO;

namespace Wildlands.Toolkit;

public sealed record Location(string ArchivePath, int EntryIndex = -1, string EntryName = "")
{
    public bool IsArchiveRoot => EntryIndex < 0;

    public Location Parent => new(ArchivePath);

    public string Display => IsArchiveRoot ? Path.GetFileName(ArchivePath) : $"{Path.GetFileName(ArchivePath)}  >  {EntryName}";
}
