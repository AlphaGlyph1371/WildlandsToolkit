using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public sealed partial class ModProject
{
    public const int CurrentFormatVersion = 1;
    public const string ManifestFileName = "project.wlproj";

    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Untitled mod";
    public string Author { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string? LastDeployedHash { get; set; }
    public string? AssetFolder { get; set; }
    public List<ModOperation> Operations { get; set; } = [];
    public List<ModOperationUndo> PendingUndo { get; set; } = [];

    [JsonIgnore]
    public string FilePath { get; private set; } = "";

    [JsonIgnore]
    public string AssetDirectory => Path.Combine(Path.GetDirectoryName(FilePath)!,
        AssetFolder ?? Path.GetFileNameWithoutExtension(FilePath) + ".assets");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public IReadOnlyList<string> Record(IReadOnlyList<PendingChange> changes, string gameFolder,
        string operationGroupId, string operationLabel)
    {
        EnsureAttached();
        if (changes.Count == 0)
            return [];

        string gameRoot = Path.GetFullPath(gameFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var archiveCache = new Dictionary<string, ForgeArchive>(StringComparer.OrdinalIgnoreCase);
        var entryCache = new Dictionary<(string Archive, int Entry), (ForgeEntry Entry, DataFile File)>();
        var original = Operations.Select(Clone).ToList();
        var originalUndo = PendingUndo.Select(Clone).ToList();
        var keys = new List<string>(changes.Count);

        try
        {
            foreach (PendingChange change in changes)
                keys.Add(Record(change, gameRoot, archiveCache, entryCache,
                    operationGroupId, operationLabel));
            Save();
            return keys;
        }
        catch
        {
            Operations = original;
            PendingUndo = originalUndo;
            try { RemoveUnusedPayloads(); }
            catch { }
            throw;
        }
        finally
        {
            foreach (ForgeArchive archive in archiveCache.Values)
                archive.Dispose();
        }
    }

    public string ContentHash()
    {
        EnsureAttached();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Add(hash, FormatVersion.ToString());

        foreach (ModOperation operation in Operations.OrderBy(operation => operation.Key,
                     StringComparer.OrdinalIgnoreCase))
        {
            ModOperation identity = Clone(operation);
            identity.Id = "";
            identity.DeployedSha256 = null;
            identity.Payload = "";
            identity.HeaderPayload = null;
            identity.EntryInfoPayload = null;
            identity.PrefetchPayload = null;
            Add(hash, JsonSerializer.Serialize(identity, JsonOptions));
            Add(hash, ReadPayload(operation.Payload));
            AddOptional(hash, operation.HeaderPayload);
            AddOptional(hash, operation.EntryInfoPayload);
            AddOptional(hash, operation.PrefetchPayload);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void MarkDeployed()
    {
        foreach (ModOperation operation in Operations)
            operation.DeployedSha256 = Hash(ReadPayload(operation.Payload));
        PendingUndo.Clear();
        LastDeployedHash = ContentHash();
        Save();
    }

    public void RevertPending(IEnumerable<string> keys)
    {
        EnsureAttached();
        var wanted = keys.Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
            return;

        var original = Operations.Select(Clone).ToList();
        var originalUndo = PendingUndo.Select(Clone).ToList();
        try
        {
            foreach (string key in wanted)
            {
                ModOperation? current = Operations.FirstOrDefault(operation =>
                    operation.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                ModOperationUndo? undo = PendingUndo.FirstOrDefault(candidate =>
                    candidate.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
                if (current is not null)
                    Operations.Remove(current);
                if (undo?.Previous is not null)
                    Operations.Add(Clone(undo.Previous));
                if (undo is not null)
                    PendingUndo.Remove(undo);
            }
            Save();
        }
        catch
        {
            Operations = original;
            PendingUndo = originalUndo;
            throw;
        }
    }

    string Record(PendingChange change, string gameRoot,
        Dictionary<string, ForgeArchive> archives,
        Dictionary<(string Archive, int Entry), (ForgeEntry Entry, DataFile File)> entries,
        string operationGroupId, string operationLabel)
    {
        string archivePath = Path.GetFullPath(change.ArchivePath);
        EnsureBelow(gameRoot, archivePath);
        string relativeArchive = Path.GetRelativePath(gameRoot, archivePath)
            .Replace(Path.DirectorySeparatorChar, '/');

        var operation = new ModOperation
        {
            Archive = relativeArchive,
            EntryName = change.EntryName,
            ResourceName = change.ResourceName,
            OperationGroupId = operationGroupId,
            OperationLabel = operationLabel,
        };

        if (change.EntryAddition is { } entryAddition)
        {
            operation.Kind = ModOperationKind.AddForgeEntry;
            operation.EntryId = entryAddition.Id;
            operation.EntryExtension = entryAddition.Extension;
            SetOrUpdate(operation, change.Data, null, entryAddition.InfoTemplate,
                entryAddition.PrefetchBlock);
            return operation.Key;
        }

        if (!archives.TryGetValue(archivePath, out ForgeArchive? archive))
        {
            archive = ForgeArchive.Open(archivePath);
            archives.Add(archivePath, archive);
        }

        ForgeEntry archiveEntry = archive.Entries.FirstOrDefault(candidate =>
            candidate.Index == change.EntryIndex)
            ?? throw new InvalidDataException(
                $"Entry index {change.EntryIndex} is outside {Path.GetFileName(archivePath)}.");
        if (change.EntryRemoval is { } entryRemoval)
        {
            if (archiveEntry.Id != entryRemoval.Id)
                throw new InvalidDataException(
                    $"{change.EntryName} no longer identifies the Forge entry queued for deletion.");
            operation.Kind = ModOperationKind.RemoveForgeEntry;
            operation.EntryId = archiveEntry.Id;
            operation.EntryName = archiveEntry.Name;
            operation.BaseSha256 = Hash(archive.ReadEntry(archiveEntry));
            SetOrUpdate(operation, [], null, null, null);
            return operation.Key;
        }

        if (!entries.TryGetValue((archivePath, change.EntryIndex), out var entryFile))
        {
            entryFile = (archiveEntry,
                DataFile.Read(new MemoryStream(archive.ReadEntry(archiveEntry))));
            entries.Add((archivePath, change.EntryIndex), entryFile);
        }

        operation.EntryId = entryFile.Entry.Id;
        operation.EntryName = entryFile.Entry.Name;

        ModOperation? addedEntry = Operations.FirstOrDefault(existing =>
            existing.Kind == ModOperationKind.AddForgeEntry
            && existing.Archive.Equals(relativeArchive, StringComparison.OrdinalIgnoreCase)
            && existing.EntryId == entryFile.Entry.Id);
        if (addedEntry is not null)
        {
            UpdateAddedEntry(addedEntry, change, entryFile.File,
                operationGroupId, operationLabel);
            return addedEntry.Key;
        }

        if (change.Addition is { } addition)
        {
            operation.Kind = ModOperationKind.AddResource;
            operation.ResourceId = addition.Id;
            operation.ResourceClassHash = addition.ClassHash;
            SetOrUpdate(operation, change.Data, addition.Header, null, null);
            return operation.Key;
        }

        if ((uint)change.ResourceIndex >= (uint)entryFile.File.Resources.Count)
            throw new InvalidDataException(
                $"Resource index {change.ResourceIndex} is outside {entryFile.Entry.Name}.");
        Resource resource = entryFile.File.Resources[change.ResourceIndex];
        if (change.Removal is { } removal)
        {
            if (resource.Id != removal.Id || resource.ClassHash != removal.ClassHash)
                throw new InvalidDataException(
                    $"{change.ResourceName} no longer identifies the resource queued for deletion.");
            operation.Kind = ModOperationKind.RemoveResource;
            operation.ResourceId = resource.Id;
            operation.ResourceClassHash = resource.ClassHash;
            operation.BaseSha256 = Hash(resource.Data);
            SetOrUpdate(operation, [], null, null, null);
            return operation.Key;
        }

        operation.Kind = ModOperationKind.ReplaceResource;
        operation.ResourceId = resource.Id;
        operation.ResourceClassHash = resource.ClassHash;
        operation.BaseSha256 = Hash(resource.Data);
        SetOrUpdate(operation, change.Data, null, null, null);
        return operation.Key;
    }

    void SetOrUpdate(ModOperation operation, byte[] data, byte[]? header,
        byte[]? entryInfo, byte[]? prefetch)
    {
        ModOperation? old = Operations.FirstOrDefault(existing =>
            existing.Key.Equals(operation.Key, StringComparison.OrdinalIgnoreCase));
        CaptureUndo(operation.Key, old);
        if (old is not null)
        {
            operation.BaseSha256 ??= old.BaseSha256;
            operation.DeployedSha256 = old.DeployedSha256;
            if (old.Kind == ModOperationKind.AddResource
                && operation.Kind == ModOperationKind.ReplaceResource)
            {
                operation.Kind = ModOperationKind.AddResource;
                operation.BaseSha256 = null;
                operation.HeaderPayload = old.HeaderPayload;
            }
        }

        operation.Payload = WritePayload(operation.Id, "data", data);
        if (header is not null)
            operation.HeaderPayload = WritePayload(operation.Id, "header", header);
        if (entryInfo is not null)
            operation.EntryInfoPayload = WritePayload(operation.Id, "entry", entryInfo);
        if (prefetch is not null)
            operation.PrefetchPayload = WritePayload(operation.Id, "prefetch", prefetch);
        if (old is not null)
            Operations.Remove(old);
        Operations.Add(operation);
    }

    void CaptureUndo(string key, ModOperation? previous)
    {
        if (PendingUndo.Any(undo => undo.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
            return;
        PendingUndo.Add(new ModOperationUndo
        {
            Key = key,
            Previous = previous is null ? null : Clone(previous),
        });
    }

    void UpdateAddedEntry(ModOperation entryOperation, PendingChange change, DataFile currentFile,
        string operationGroupId, string operationLabel)
    {
        var desired = DataFile.Read(new MemoryStream(ReadPayload(entryOperation.Payload)));
        ulong resourceId;
        uint classHash;
        byte[] header;

        if (change.Addition is { } addition)
        {
            resourceId = addition.Id;
            classHash = addition.ClassHash;
            header = addition.Header;
        }
        else
        {
            if ((uint)change.ResourceIndex >= (uint)currentFile.Resources.Count)
                throw new InvalidDataException(
                    $"Resource index {change.ResourceIndex} is outside {entryOperation.EntryName}.");
            Resource current = currentFile.Resources[change.ResourceIndex];
            resourceId = current.Id;
            classHash = current.ClassHash;
            header = current.Header;
        }

        Resource? target = desired.Resources.FirstOrDefault(resource =>
            resource.Id == resourceId && resource.ClassHash == classHash);
        if (change.Removal is not null)
        {
            if (target is null)
                throw new InvalidDataException(
                    $"{change.ResourceName} is not present in the added container.");
            desired.Resources.Remove(target);
        }
        else if (target is null)
        {
            desired.Resources.Add(new Resource
            {
                Id = resourceId,
                ClassHash = classHash,
                Name = change.ResourceName,
                Header = (byte[])header.Clone(),
                Data = (byte[])change.Data.Clone(),
            });
        }
        else
        {
            target.Data = (byte[])change.Data.Clone();
        }

        using var rebuilt = new MemoryStream();
        desired.Write(rebuilt);
        var replacement = Clone(entryOperation);
        replacement.Id = Guid.NewGuid().ToString("N");
        replacement.OperationGroupId = operationGroupId;
        replacement.OperationLabel = operationLabel;
        SetOrUpdate(replacement, rebuilt.ToArray(), null,
            ReadPayload(entryOperation.EntryInfoPayload!),
            ReadPayload(entryOperation.PrefetchPayload!));
    }

    string WritePayload(string id, string type, byte[] data)
    {
        Directory.CreateDirectory(AssetDirectory);
        string name = $"{SafeName(id)}.{type}.bin";
        File.WriteAllBytes(PayloadPath(name), data);
        return name;
    }

    byte[] ReadPayload(string name) => File.ReadAllBytes(PayloadPath(name));

    string PayloadPath(string name)
    {
        string full = Path.GetFullPath(Path.Combine(AssetDirectory, name));
        EnsureBelow(AssetDirectory, full);
        return full;
    }

    IEnumerable<string> PayloadNames(bool includeUndo = true)
    {
        IEnumerable<ModOperation> operations = Operations;
        if (includeUndo)
            operations = operations.Concat(PendingUndo
                .Select(undo => undo.Previous)
                .OfType<ModOperation>());
        return operations.SelectMany(operation => new[]
        {
            operation.Payload,
            operation.HeaderPayload,
            operation.EntryInfoPayload,
            operation.PrefetchPayload,
        })
        .Where(name => !string.IsNullOrWhiteSpace(name))
        .Select(name => name!)
        .Distinct(StringComparer.OrdinalIgnoreCase);
    }


   void RemoveUnusedPayloads()
    {
        if (!Directory.Exists(AssetDirectory))
            return;
        var used = PayloadNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.EnumerateFiles(AssetDirectory, "*.bin",
                     SearchOption.TopDirectoryOnly))
            if (!used.Contains(Path.GetFileName(file)))
                File.Delete(file);
    }

    static void EnsureBelow(string root, string path)
    {
        string fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path lies outside its allowed folder: {path}");
    }

    static string SafeName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        safe = safe.Trim().TrimEnd('.');
        return safe.Length == 0 ? "mod" : safe;
    }

    static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    static bool IsHash(string? value) => value is { Length: 64 }
        && value.All(character => Uri.IsHexDigit(character));

    static bool Matches(string? expectedHash, byte[] data) =>
        expectedHash is { Length: > 0 }
        && Hash(data).Equals(expectedHash, StringComparison.OrdinalIgnoreCase);

    static bool Matches(string? expectedHash, string actualHash) =>
        expectedHash is { Length: > 0 }
        && actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase);

    static void Add(IncrementalHash hash, string value) => Add(hash, Encoding.UTF8.GetBytes(value));

    static void Add(IncrementalHash hash, byte[] value)
    {
        hash.AppendData(BitConverter.GetBytes(value.Length));
        hash.AppendData(value);
    }

    void AddOptional(IncrementalHash hash, string? payload)
    {
        if (payload is null)
            Add(hash, []);
        else
            Add(hash, ReadPayload(payload));
    }

    void EnsureAttached()
    {
        if (string.IsNullOrWhiteSpace(FilePath))
            throw new InvalidOperationException("The project is not attached to a file.");
    }

    static ModOperation Clone(ModOperation source) => new()
    {
        Id = source.Id,
        Kind = source.Kind,
        Archive = source.Archive,
        EntryId = source.EntryId,
        EntryName = source.EntryName,
        ResourceId = source.ResourceId,
        ResourceClassHash = source.ResourceClassHash,
        ResourceName = source.ResourceName,
        BaseSha256 = source.BaseSha256,
        DeployedSha256 = source.DeployedSha256,
        Payload = source.Payload,
        HeaderPayload = source.HeaderPayload,
        EntryExtension = source.EntryExtension,
        EntryInfoPayload = source.EntryInfoPayload,
        PrefetchPayload = source.PrefetchPayload,
        OperationGroupId = source.OperationGroupId,
        OperationLabel = source.OperationLabel,
    };

    static string GroupId(ModOperation operation) =>
        string.IsNullOrWhiteSpace(operation.OperationGroupId) ? operation.Id : operation.OperationGroupId;

    static string GroupLabel(ModOperation operation) =>
        string.IsNullOrWhiteSpace(operation.OperationLabel)
            ? operation.Kind switch
            {
                ModOperationKind.AddForgeEntry => $"Add asset container {operation.EntryName}",
                ModOperationKind.AddResource => $"Add resource {operation.ResourceName}",
                ModOperationKind.RemoveForgeEntry => $"Delete asset container {operation.EntryName}",
                ModOperationKind.RemoveResource => $"Delete resource {operation.ResourceName}",
                _ => $"Replace {operation.ResourceName}",
            }
            : operation.OperationLabel;

    static ModOperationUndo Clone(ModOperationUndo source) => new()
    {
        Key = source.Key,
        Previous = source.Previous is null ? null : Clone(source.Previous),
    };
}
