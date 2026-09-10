using System.Text.Json.Serialization;

namespace Wildlands.Toolkit;

public enum ModOperationKind
{
    ReplaceResource,
    AddResource,
    AddForgeEntry,
    RemoveResource,
    RemoveForgeEntry,
}

public sealed class ModOperation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ModOperationKind Kind { get; set; }
    public string Archive { get; set; } = "";
    public ulong EntryId { get; set; }
    public string EntryName { get; set; } = "";
    public ulong ResourceId { get; set; }
    public uint ResourceClassHash { get; set; }
    public string ResourceName { get; set; } = "";
    public ulong InsertAfterResourceId { get; set; }
    public string? BaseSha256 { get; set; }
    public string? DeployedSha256 { get; set; }
    public string Payload { get; set; } = "";
    public string? HeaderPayload { get; set; }
    public uint EntryExtension { get; set; }
    public string? EntryInfoPayload { get; set; }
    public string? PrefetchPayload { get; set; }
    public string OperationGroupId { get; set; } = "";
    public string OperationLabel { get; set; } = "";

    [JsonIgnore]
    public string Key => Kind switch
    {
        ModOperationKind.AddForgeEntry or ModOperationKind.RemoveForgeEntry =>
            $"{Archive}|entry|{EntryId:X16}",
        _ => $"{Archive}|{EntryId:X16}|{ResourceClassHash:X8}|{ResourceId:X16}",
    };
}

public sealed class ModOperationUndo
{
    public string Key { get; set; } = "";
    public ModOperation? Previous { get; set; }
}

public sealed record ModCompileResult(
    IReadOnlyList<PendingChange> Changes,
    IReadOnlyList<string> Problems,
    int AlreadyApplied);

public sealed record ModPackageInfo(
    string Path,
    string Name,
    string Author,
    string Version,
    int FormatVersion,
    long Size,
    IReadOnlyList<ModOperation> Operations);

static class ModStringExtensions
{
    public static string Default(this string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
