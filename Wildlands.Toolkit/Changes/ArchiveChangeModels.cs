using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public sealed record PendingChange(
    string ArchivePath,
    int EntryIndex,
    string EntryName,
    int ResourceIndex,
    string ResourceName,
    byte[] Data,
    PendingResourceAddition? Addition = null,
    PendingForgeEntryAddition? EntryAddition = null,
    string? ProjectOperationKey = null,
    string? OperationGroupId = null,
    string? OperationLabel = null,
    uint ResourceClassHash = 0);

public sealed record PendingResourceAddition(
    ulong Id,
    uint ClassHash,
    byte[] Header);

public sealed record PendingForgeEntryAddition(
    ulong Id,
    string Name,
    uint Extension,
    byte[] InfoTemplate,
    byte[] PrefetchBlock);

public sealed class ArchiveWork
{
    public string Path { get; init; } = "";
    public long Size { get; init; }
    public long OutputSize { get; set; }
    public Dictionary<int, byte[]> Entries { get; } = [];
    public List<string> EntryNames { get; } = [];
    public List<ForgeEntryAddition> EntryAdditions { get; } = [];
    public bool NeedsRebuild { get; set; }

    public string Name => System.IO.Path.GetFileName(Path);
}

public sealed record DriveSpaceRequirement(
    string DriveName,
    long RequiredBytes,
    long AvailableBytes)
{
    public bool HasEnoughSpace => AvailableBytes >= RequiredBytes;
}
