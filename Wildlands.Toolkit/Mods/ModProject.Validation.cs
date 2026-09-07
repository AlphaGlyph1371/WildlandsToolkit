using System.IO;

namespace Wildlands.Toolkit;

public sealed partial class ModProject
{
    void ValidatePayloadPaths()
    {
        foreach (string payload in PayloadNames())
            if (!File.Exists(PayloadPath(payload)))
                throw new InvalidDataException($"Project payload {payload} is missing.");
    }

    void ValidateManifest()
    {
        if (!Guid.TryParseExact(Id, "N", out _))
            throw new InvalidDataException("The project has no valid id.");
        if (string.IsNullOrWhiteSpace(Name))
            throw new InvalidDataException("The project has no name.");
        if (Author is null || Version is null)
            throw new InvalidDataException("The project metadata is incomplete.");
        if (AssetFolder is not null && (string.IsNullOrWhiteSpace(AssetFolder)
                || !Path.GetFileName(AssetFolder).Equals(AssetFolder, StringComparison.Ordinal)
                || AssetFolder is "." or ".."))
            throw new InvalidDataException("The project has an invalid assets folder.");
        if (Name.Length > 200 || Author.Length > 200 || Version.Length > 100)
            throw new InvalidDataException("The project metadata is too long.");
        if (LastDeployedHash is not null && !IsHash(LastDeployedHash))
            throw new InvalidDataException("The project has an invalid deployment hash.");
        if (Operations is null)
            throw new InvalidDataException("The project has no operation list.");
        if (PendingUndo is null)
            throw new InvalidDataException("The project has no pending-operation history.");
        if (Operations.Count > 100_000)
            throw new InvalidDataException("The project contains too many operations.");
        if (Operations.Any(operation => operation is null))
            throw new InvalidDataException("The project contains an empty operation.");
        if (Operations.GroupBy(operation => operation.Key,
                StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            throw new InvalidDataException("The project contains duplicate operations for the same game resource.");
        if (Operations.GroupBy(operation => operation.Id,
                StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            throw new InvalidDataException("The project contains duplicate operation ids.");
        if (PendingUndo.GroupBy(undo => undo.Key,
                StringComparer.OrdinalIgnoreCase).Any(group => group.Count() != 1))
            throw new InvalidDataException("The project contains duplicate undo records.");

        var addedEntries = Operations.Where(operation => operation.Kind == ModOperationKind.AddForgeEntry)
            .Select(operation => $"{operation.Archive}|{operation.EntryId:X16}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var removedEntries = Operations.Where(operation => operation.Kind == ModOperationKind.RemoveForgeEntry)
            .Select(operation => $"{operation.Archive}|{operation.EntryId:X16}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ModOperation operation in Operations)
        {
            if (!Guid.TryParseExact(operation.Id, "N", out _))
                throw new InvalidDataException("The project contains an invalid operation id.");
            if (!Enum.IsDefined(operation.Kind))
                throw new InvalidDataException("The project contains an unknown operation kind.");
            if (string.IsNullOrWhiteSpace(operation.Archive) || Path.IsPathRooted(operation.Archive))
                throw new InvalidDataException("A project operation has no valid relative archive path.");
            if (operation.Archive.Contains(':') || operation.Archive
                    .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                    .Any(part => part is "." or ".."))
                throw new InvalidDataException("A project operation has an unsafe archive path.");
            if (operation.DeployedSha256 is not null && !IsHash(operation.DeployedSha256))
                throw new InvalidDataException("A project operation has an invalid deployment hash.");
            if (operation.Kind is ModOperationKind.ReplaceResource
                    or ModOperationKind.RemoveResource or ModOperationKind.RemoveForgeEntry
                && !IsHash(operation.BaseSha256))
                throw new InvalidDataException("A replace or delete operation has no valid baseline hash.");
            ValidatePayloadName(operation.Payload, "data");

            if (operation.Kind == ModOperationKind.AddResource)
                ValidatePayloadName(operation.HeaderPayload, "resource header");
            if (operation.Kind == ModOperationKind.AddForgeEntry)
            {
                ValidatePayloadName(operation.EntryInfoPayload, "entry info");
                ValidatePayloadName(operation.PrefetchPayload, "entry prefetch block");
            }
            else if (addedEntries.Contains($"{operation.Archive}|{operation.EntryId:X16}"))
            {
                throw new InvalidDataException(
                    "A newly added entry also has separate resource operations. Rebuild the project with this Toolkit version.");
            }
            if (operation.Kind != ModOperationKind.RemoveForgeEntry
                && removedEntries.Contains($"{operation.Archive}|{operation.EntryId:X16}"))
                throw new InvalidDataException(
                    "A deleted entry also has separate resource operations. Remove the resource changes before deleting the container.");
        }

        var currentKeys = Operations.Select(operation => operation.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (ModOperationUndo undo in PendingUndo)
        {
            if (string.IsNullOrWhiteSpace(undo.Key) || !currentKeys.Contains(undo.Key))
                throw new InvalidDataException("The project contains an orphaned undo record.");
            if (undo.Previous is not { } previous)
                continue;
            if (!previous.Key.Equals(undo.Key, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A project undo record points at a different operation.");
            ValidatePayloadName(previous.Payload, "undo data");
            if (previous.Kind == ModOperationKind.AddResource)
                ValidatePayloadName(previous.HeaderPayload, "undo resource header");
            if (previous.Kind == ModOperationKind.AddForgeEntry)
            {
                ValidatePayloadName(previous.EntryInfoPayload, "undo entry info");
                ValidatePayloadName(previous.PrefetchPayload, "undo entry prefetch block");
            }
        }
    }

    static void ValidatePayloadName(string? value, string kind)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Path.GetFileName(value).Equals(value, StringComparison.Ordinal)
            || !value.EndsWith(".bin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"A project operation has no valid {kind} payload name.");
    }
}
