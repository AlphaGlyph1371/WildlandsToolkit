using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public sealed record BuildTableTarget(
    ulong Id,
    string Name,
    uint ClassHash,
    string Type,
    string Container,
    string Archive)
{
    public string Location => Container.Length > 0
        && !string.Equals(Container, Name, StringComparison.OrdinalIgnoreCase)
            ? $"{Archive}  ›  {Container}"
            : Archive;

    public string Details
    {
        get
        {
            string type = Type switch
            {
                "LODSelector" => "Variant selector (LODSelector)",
                "BuildTable" => "BuildTable",
                "Skeleton" => "Resource (Skeleton)",
                "" => "Archive asset",
                _ => Type,
            };
            return $"{type}  ·  {Location}  ·  0x{Id:X}";
        }
    }
}

public sealed class BuildTableTargetCatalog
{
    public Dictionary<ulong, BuildTableTarget> ById { get; } = [];
    public IReadOnlyList<BuildTableTarget> Targets { get; internal set; } = [];
}

public static class BuildTableTargetResolver
{
    sealed record EntryLocation(string Path, int EntryIndex, string EntryName);

    sealed class InstalledIndex
    {
        public Dictionary<ulong, BuildTableTarget> ById { get; } = [];
        public Dictionary<ulong, EntryLocation> Locations { get; } = [];
        public List<BuildTableTarget> Targets { get; } = [];
    }

    static readonly object CacheLock = new();
    static readonly ConcurrentDictionary<ulong, EntryLocation> GeneratedLocations = new();
    static string _cachedKey = "";
    static InstalledIndex? _cachedIndex;

    public static BuildTableTarget? ResolveOne(
        IReadOnlyList<string> archivePaths,
        ulong id,
        CancellationToken cancellationToken = default)
    {
        if (id == 0)
            return null;

        var installed = Installed(archivePaths, null, cancellationToken);
        installed.ById.TryGetValue(id, out var fallback);
        if (!installed.Locations.TryGetValue(id, out var location)
            && !TryFindGeneratedLocation(archivePaths, id, cancellationToken, out location))
            return fallback;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = ForgeArchive.Open(location.Path);
            var entry = archive.Entries.FirstOrDefault(entry => entry.Index == location.EntryIndex);
            if (entry is null)
                return fallback;

            using var stream = new MemoryStream(archive.ReadEntry(entry));
            var file = DataFile.Read(stream);
            var resource = file.Resources.FirstOrDefault(resource => resource.Id == id);
            return resource is null
                ? fallback
                : new BuildTableTarget(resource.Id, resource.Name, resource.ClassHash,
                    ResourceTypes.NameOf(resource.ClassHash), entry.Name, Path.GetFileName(location.Path));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return fallback;
        }
    }

    public static Resource? LoadResource(
        IReadOnlyList<string> archivePaths,
        ulong id,
        CancellationToken cancellationToken = default)
        => LoadResourceGroup(archivePaths, id, cancellationToken)
            .FirstOrDefault(resource => resource.Id == id);

    public static IReadOnlyList<Resource> LoadResourceGroup(
        IReadOnlyList<string> archivePaths,
        ulong id,
        CancellationToken cancellationToken = default)
    {
        if (id == 0)
            return [];

        var installed = Installed(archivePaths, null, cancellationToken);
        if (installed.Locations.TryGetValue(id, out var exact)
            && TryLoadResourceGroup(exact.Path, exact.EntryIndex, id, cancellationToken) is { } exactGroup)
            return exactGroup;
        if (TryFindGeneratedLocation(archivePaths, id, cancellationToken, out var generated)
            && TryLoadResourceGroup(generated.Path, generated.EntryIndex, id, cancellationToken) is { } generatedGroup)
            return generatedGroup;

        // A BuildTable can point at a resource nested in a data container whose
        // top-level forge ID is different. Resolve those lazily for card previews;
        // this work always runs off the UI thread.
        foreach (string path in archivePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var archive = ForgeArchive.Open(path);
                var entry = archive.FindContaining(id);
                if (entry is null)
                    continue;
                using var stream = new MemoryStream(archive.ReadEntry(entry));
                var file = DataFile.Read(stream);
                if (file.Resources.Any(candidate => candidate.Id == id))
                    return file.Resources;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Continue with the remaining installed archives.
            }
        }

        return [];
    }

    static bool TryFindGeneratedLocation(IReadOnlyList<string> archivePaths, ulong id,
        CancellationToken cancellationToken, out EntryLocation location)
    {
        if (GeneratedLocations.TryGetValue(id, out location!))
            return true;

        // Toolkit-created resources are added to entries in archives for which Apply
        // already made an .original backup. Comparing entry lengths with that backup
        // narrows a full installation scan to the handful of entries actually edited.
        foreach (string path in archivePaths.Where(ArchiveBackup.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var archive = ForgeArchive.Open(path);
                using var original = ForgeArchive.Open(ArchiveBackup.PathFor(path));
                var originalLengths = original.Entries.ToDictionary(entry => entry.Index, entry => entry.Length);
                foreach (var entry in archive.Entries.Where(entry =>
                    !originalLengths.TryGetValue(entry.Index, out int originalLength)
                    || originalLength != entry.Length))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (!archive.ContainsResourceId(entry, id))
                            continue;
                        location = new EntryLocation(path, entry.Index, entry.Name);
                        GeneratedLocations[id] = location;
                        return true;
                    }
                    catch
                    {
                        // Some Forge entries are raw payloads rather than DataFiles.
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // One damaged or unavailable backup must not block other archives.
            }
        }

        location = null!;
        return false;
    }

    static IReadOnlyList<Resource>? TryLoadResourceGroup(string path, int entryIndex, ulong id,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = ForgeArchive.Open(path);
            var entry = archive.Entries.FirstOrDefault(candidate => candidate.Index == entryIndex);
            if (entry is null)
                return null;
            using var stream = new MemoryStream(archive.ReadEntry(entry));
            var resources = DataFile.Read(stream).Resources;
            return resources.Any(resource => resource.Id == id) ? resources : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    public static BuildTableTargetCatalog Build(
        IReadOnlyList<string> archivePaths,
        IReadOnlyList<BuildTableTarget> localTargets,
        IReadOnlyCollection<ulong> wantedIds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installed = Installed(archivePaths, progress, cancellationToken);
        var catalog = new BuildTableTargetCatalog();
        var localIds = new HashSet<ulong>();

        foreach (var target in localTargets)
        {
            if (target.Id == 0)
                continue;

            localIds.Add(target.Id);
            catalog.ById[target.Id] = target;
        }

        foreach (ulong id in wantedIds.Where(id => id != 0 && !localIds.Contains(id)))
        {
            if (installed.ById.TryGetValue(id, out var target))
            {
                catalog.ById[id] = target;
                continue;
            }

            if (TryFindGeneratedLocation(archivePaths, id, cancellationToken, out var generated)
                && TryLoadResourceGroup(generated.Path, generated.EntryIndex, id, cancellationToken)
                    is { } generatedGroup
                && generatedGroup.FirstOrDefault(resource => resource.Id == id) is { } resource)
            {
                catalog.ById[id] = new BuildTableTarget(resource.Id, resource.Name,
                    resource.ClassHash, ResourceTypes.NameOf(resource.ClassHash),
                    generated.EntryName,
                    Path.GetFileName(generated.Path));
            }
        }

        var locations = wantedIds
            .Where(id => id != 0 && !localIds.Contains(id) && installed.Locations.ContainsKey(id))
            .GroupBy(id => installed.Locations[id].Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int archiveNumber = 0;
        foreach (var perArchive in locations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report($"Reading referenced assets, {++archiveNumber} of {locations.Count} archives…");

            try
            {
                using var archive = ForgeArchive.Open(perArchive.Key);
                string archiveName = Path.GetFileName(perArchive.Key);

                foreach (var perEntry in perArchive
                    .GroupBy(id => installed.Locations[id].EntryIndex)
                    .Select(group => (Index: group.Key, Ids: group.ToHashSet())))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.Entries.FirstOrDefault(entry => entry.Index == perEntry.Index);
                    if (entry is not null)
                        TryReadTargets(archive, entry, perEntry.Ids, catalog, archiveName);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // The header-level name remains useful if the payload cannot be decoded.
            }
        }

        // The current data file is authoritative and may contain resources that have no
        // top-level forge entry of their own.
        foreach (var target in localTargets)
            if (target.Id != 0)
                catalog.ById[target.Id] = target;

        // Installed targets are shared by every editor window; only the small local tail is new.
        catalog.Targets = installed.Targets
            .Select(installedTarget => catalog.ById.GetValueOrDefault(installedTarget.Id) ?? installedTarget)
            .Concat(localTargets.Where(local => !installed.ById.ContainsKey(local.Id)))
            .ToList();
        return catalog;
    }

    static InstalledIndex Installed(IReadOnlyList<string> archivePaths,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        string key = CacheKey(archivePaths);
        lock (CacheLock)
            if (_cachedIndex is not null && _cachedKey == key)
                return _cachedIndex;

        var built = new InstalledIndex();
        for (int archiveIndex = 0; archiveIndex < archivePaths.Count; archiveIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = archivePaths[archiveIndex];
            progress?.Report($"Indexing asset names, {archiveIndex + 1} of {archivePaths.Count} archives…");

            try
            {
                using var archive = ForgeArchive.Open(path);
                string archiveName = Path.GetFileName(path);

                foreach (var entry in archive.Entries)
                {
                    if (entry.Id == 0 || built.ById.ContainsKey(entry.Id))
                        continue;

                    var target = new BuildTableTarget(entry.Id, entry.Name, 0, "", entry.Name, archiveName);
                    built.ById.Add(entry.Id, target);
                    built.Locations.Add(entry.Id, new EntryLocation(path, entry.Index, entry.Name));
                }
            }
            catch
            {
                // One unreadable archive must not prevent names from every other archive.
            }
        }

        built.Targets.AddRange(built.ById.Values
            .OrderBy(target => target.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(target => target.Id));

        lock (CacheLock)
        {
            if (_cachedIndex is null || _cachedKey != key)
            {
                _cachedKey = key;
                _cachedIndex = built;
                GeneratedLocations.Clear();
            }
            return _cachedIndex;
        }
    }

    static string CacheKey(IReadOnlyList<string> archivePaths)
    {
        return string.Join('|', archivePaths
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
        {
            try
            {
                var file = new FileInfo(path);
                return $"{Path.GetFullPath(path)}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
            }
            catch
            {
                return Path.GetFullPath(path);
            }
        }));
    }

    static void TryReadTargets(ForgeArchive archive, ForgeEntry entry,
        IReadOnlySet<ulong> wantedIds, BuildTableTargetCatalog catalog, string archiveName)
    {
        try
        {
            using var stream = new MemoryStream(archive.ReadEntry(entry));
            var file = DataFile.Read(stream);

            foreach (var resource in file.Resources.Where(resource => wantedIds.Contains(resource.Id)))
            {
                string type = ResourceTypes.NameOf(resource.ClassHash);
                catalog.ById[resource.Id] = new BuildTableTarget(
                    resource.Id, resource.Name, resource.ClassHash, type, entry.Name, archiveName);
            }
        }
        catch
        {
            // The forge entry name and id still provide a useful fallback target.
        }
    }
}
