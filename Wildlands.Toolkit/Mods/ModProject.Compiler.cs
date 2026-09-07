using System.IO;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public sealed partial class ModProject
{
    public ModCompileResult Compile(string gameFolder)
    {
        EnsureAttached();
        ValidateManifest();
        ValidatePayloadPaths();
        string gameRoot = Path.GetFullPath(gameFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var changes = new ChangeSet();
        var problems = new List<string>();
        int alreadyApplied = 0;
        var archives = new Dictionary<string, ForgeArchive>(StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<(string Archive, ulong Entry), (ForgeEntry Entry, DataFile File)>();

        try
        {
            foreach (ModOperation operation in Operations)
            {
                try
                {
                    string archivePath = ResolveArchive(gameRoot, operation.Archive);
                    if (!archives.TryGetValue(archivePath, out ForgeArchive? archive))
                    {
                        archive = ForgeArchive.Open(archivePath);
                        archives.Add(archivePath, archive);
                    }

                    byte[] payload = ReadPayload(operation.Payload);
                    ForgeEntry? entry = archive.Entries.FirstOrDefault(candidate =>
                        candidate.Id == operation.EntryId);

                    if (operation.Kind == ModOperationKind.RemoveForgeEntry)
                    {
                        if (entry is null)
                        {
                            alreadyApplied++;
                            continue;
                        }

                        byte[] current = archive.ReadEntry(entry);
                        if (!Matches(operation.BaseSha256, current)
                            && !Matches(operation.DeployedSha256, current))
                            throw new InvalidDataException(
                                $"{operation.EntryName} was changed by the game or another mod.");
                        changes.Set(new PendingChange(archivePath, entry.Index, entry.Name,
                            -1, entry.Name, [],
                            EntryRemoval: new PendingForgeEntryRemoval(entry.Id),
                            ProjectOperationKey: operation.Key,
                            OperationGroupId: GroupId(operation),
                            OperationLabel: GroupLabel(operation)));
                        continue;
                    }

                    if (operation.Kind == ModOperationKind.AddForgeEntry)
                    {
                        if (entry is null)
                        {
                            changes.Set(new PendingChange(archivePath, -1, operation.EntryName,
                                -1, operation.EntryName, payload,
                                EntryAddition: new PendingForgeEntryAddition(operation.EntryId, operation.EntryName,
                                    operation.EntryExtension, ReadPayload(operation.EntryInfoPayload!),
                                    ReadPayload(operation.PrefetchPayload!)),
                                ProjectOperationKey: operation.Key,
                                OperationGroupId: GroupId(operation),
                                OperationLabel: GroupLabel(operation)));
                        }
                        else if (archive.ReadEntry(entry).AsSpan().SequenceEqual(payload))
                        {
                            alreadyApplied++;
                        }
                        else
                        {
                            byte[] current = archive.ReadEntry(entry);
                            if (!Matches(operation.DeployedSha256, current))
                                throw new InvalidDataException(
                                    $"Entry 0x{operation.EntryId:X16} ({operation.EntryName}) already exists with different data.");
                            QueueExistingEntry(changes, archivePath, entry, current, payload,
                                operation.Key, GroupId(operation), GroupLabel(operation));
                        }
                        continue;
                    }

                    if (entry is null)
                        throw new InvalidDataException(
                            $"Entry 0x{operation.EntryId:X16} ({operation.EntryName}) is missing.");

                    var cacheKey = (archivePath, entry.Id);
                    if (!files.TryGetValue(cacheKey, out var entryFile))
                    {
                        entryFile = (entry, DataFile.Read(new MemoryStream(archive.ReadEntry(entry))));
                        files.Add(cacheKey, entryFile);
                    }

                    Resource? resource = entryFile.File.Resources.FirstOrDefault(candidate =>
                        candidate.Id == operation.ResourceId
                        && candidate.ClassHash == operation.ResourceClassHash);
                    if (resource is not null)
                    {
                        if (operation.Kind == ModOperationKind.RemoveResource)
                        {
                            string currentHash = Hash(resource.Data);
                            if (!Matches(operation.BaseSha256, currentHash)
                                && !Matches(operation.DeployedSha256, currentHash))
                                throw new InvalidDataException(
                                    $"{operation.ResourceName} was changed by the game or another mod.");
                            int removalIndex = entryFile.File.Resources.IndexOf(resource);
                            changes.Set(new PendingChange(archivePath, entry.Index, entry.Name,
                                removalIndex, operation.ResourceName, [],
                                Removal: new PendingResourceRemoval(resource.Id,
                                    resource.ClassHash),
                                ProjectOperationKey: operation.Key,
                                OperationGroupId: GroupId(operation),
                                OperationLabel: GroupLabel(operation),
                                ResourceClassHash: operation.ResourceClassHash));
                            continue;
                        }

                        if (resource.Data.AsSpan().SequenceEqual(payload))
                        {
                            alreadyApplied++;
                            continue;
                        }

                        if (operation.Kind == ModOperationKind.ReplaceResource)
                        {
                            string currentHash = Hash(resource.Data);
                            if (!Matches(operation.BaseSha256, currentHash)
                                && !Matches(operation.DeployedSha256, currentHash))
                                throw new InvalidDataException(
                                    $"{operation.ResourceName} was changed by the game or another mod.");
                        }
                        else if (!Matches(operation.DeployedSha256, resource.Data))
                        {
                            throw new InvalidDataException(
                                $"Resource 0x{operation.ResourceId:X16} ({operation.ResourceName}) already exists with different data.");
                        }

                        int resourceIndex = entryFile.File.Resources.IndexOf(resource);
                        changes.Set(new PendingChange(archivePath, entry.Index, entry.Name,
                            resourceIndex, operation.ResourceName, payload,
                            ProjectOperationKey: operation.Key,
                            OperationGroupId: GroupId(operation),
                            OperationLabel: GroupLabel(operation),
                            ResourceClassHash: operation.ResourceClassHash));
                    }
                    else if (operation.Kind == ModOperationKind.AddResource)
                    {
                        changes.Set(new PendingChange(archivePath, entry.Index, entry.Name,
                            -1, operation.ResourceName, payload,
                            new PendingResourceAddition(operation.ResourceId,
                                operation.ResourceClassHash, ReadPayload(operation.HeaderPayload!)),
                            ProjectOperationKey: operation.Key,
                            OperationGroupId: GroupId(operation),
                            OperationLabel: GroupLabel(operation),
                            ResourceClassHash: operation.ResourceClassHash));
                    }
                    else if (operation.Kind == ModOperationKind.RemoveResource)
                    {
                        alreadyApplied++;
                    }
                    else
                    {
                        throw new InvalidDataException(
                            $"Resource 0x{operation.ResourceId:X16} ({operation.ResourceName}) is missing.");
                    }
                }
                catch (Exception ex)
                {
                    problems.Add($"{operation.ResourceName.Default(operation.EntryName)}: {ex.Message}");
                }
            }
        }
        finally
        {
            foreach (ForgeArchive archive in archives.Values)
                archive.Dispose();
        }

        return new ModCompileResult(changes.Changes.ToList(), problems, alreadyApplied);
    }

    static void QueueExistingEntry(ChangeSet changes, string archivePath, ForgeEntry entry,
        byte[] currentData, byte[] desiredData, string operationKey,
        string operationGroupId, string operationLabel)
    {
        DataFile current = DataFile.Read(new MemoryStream(currentData));
        DataFile desired = DataFile.Read(new MemoryStream(desiredData));
        foreach (Resource wanted in desired.Resources)
        {
            Resource? present = current.Resources.FirstOrDefault(resource =>
                resource.Id == wanted.Id && resource.ClassHash == wanted.ClassHash);
            if (present is not null)
            {
                if (!present.Data.AsSpan().SequenceEqual(wanted.Data))
                    changes.Set(new PendingChange(archivePath, entry.Index, entry.Name,
                        current.Resources.IndexOf(present), wanted.Name, wanted.Data,
                        ProjectOperationKey: operationKey,
                        OperationGroupId: operationGroupId,
                        OperationLabel: operationLabel,
                        ResourceClassHash: wanted.ClassHash));
            }
            else
            {
                changes.Set(new PendingChange(archivePath, entry.Index, entry.Name,
                    -1, wanted.Name, wanted.Data,
                    new PendingResourceAddition(wanted.Id, wanted.ClassHash, wanted.Header),
                    ProjectOperationKey: operationKey,
                    OperationGroupId: operationGroupId,
                    OperationLabel: operationLabel,
                    ResourceClassHash: wanted.ClassHash));
            }
        }
    }

    static string ResolveArchive(string gameRoot, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(gameRoot,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        EnsureBelow(gameRoot, path);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Game archive {relative} is missing.", path);
        return path;
    }
}
