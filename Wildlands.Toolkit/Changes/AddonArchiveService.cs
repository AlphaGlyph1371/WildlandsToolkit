using System.IO;
using System.Security.Cryptography;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public sealed record StaleAddon(string ArchivePath, IReadOnlyList<string> Containers);

public static class AddonArchiveService
{
    const int FirstSlot = 2;
    const int LastSlot = 99;
    const string BasisExtension = ".basis";

    public static bool CanWrite(IReadOnlyList<ArchiveWork> plans, out string reason)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (plans.Count == 0)
        {
            reason = "There is nothing to install.";
            return false;
        }

        ArchiveWork? removing = plans.FirstOrDefault(work => work.EntryRemovals.Count > 0);
        if (removing is not null)
        {
            reason = $"{removing.Name} removes {removing.EntryRemovals.Count} container(s). "
                + "An addon archive can only add or replace, so this has to be written in place.";
            return false;
        }

        var combined = new Dictionary<(string Family, ulong Container), ContainerChanges>();
        foreach (ArchiveWork work in plans)
        {
            if (FamilyOf(work.Path).Length == 0)
            {
                reason = $"{work.Name} is not a patchable archive.";
                return false;
            }

            if (NextSlot(work.Path) > LastSlot)
            {
                reason = $"{Path.GetFileName(FamilyOf(work.Path))} already uses every patch number.";
                return false;
            }

            using var archive = ForgeArchive.Open(work.Path);
            foreach ((int index, byte[] data) in work.Entries)
            {
                ForgeEntry? entry = archive.Entries.FirstOrDefault(candidate =>
                    candidate.Index == index);
                if (entry is null)
                {
                    reason = $"{work.Name} no longer holds container {index}.";
                    return false;
                }

                try
                {
                    ContainerChanges change = ContainerChanges.Between(archive.ReadEntry(entry), data, entry.Id);
                    var key = (FamilyOf(work.Path).ToUpperInvariant(), entry.Id);
                    if (combined.TryGetValue(key, out ContainerChanges? previous))
                        previous.Include(change);
                    else
                        combined.Add(key, change);
                }
                catch (Exception ex) when (ex is InvalidDataException or OverflowException)
                {
                    reason = $"{entry.Name} cannot be written as an addon: {ex.Message}";
                    return false;
                }
            }
        }

        reason = "";
        return true;
    }

    public static IReadOnlyList<string> Write(IReadOnlyList<ArchiveWork> plans,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (!CanWrite(plans, out string reason))
            throw new InvalidOperationException(reason);

        var written = new List<string>();
        foreach (IGrouping<string, ArchiveWork> family in plans.GroupBy(work => FamilyOf(work.Path),
                     StringComparer.OrdinalIgnoreCase))
        {
            int slot = NextSlot(family.First().Path);
            Dictionary<ulong, ForgeNewEntry> entries = Collect(family, family.Key, slot, progress);
            if (entries.Count == 0)
                continue;

            string target = SlotPath(family.Key, slot);
            progress?.Report($"Writing {Path.GetFileName(target)}...");
            ForgeArchive.Create(target, entries.Values.ToList(), Guid.NewGuid().ToString(),
                (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            written.Add(target);

            progress?.Report($"Noting which game containers {Path.GetFileName(target)} covers...");
            File.WriteAllLines(target + BasisExtension, Basis(family.Key, slot, entries.Keys)
                .OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key} {pair.Value}"));
        }

        return written;
    }

    public static List<StaleAddon> FindStale(IEnumerable<string> archivePaths)
    {
        var stale = new List<StaleAddon>();
        foreach (string path in archivePaths)
        {
            string basisPath = path + BasisExtension;
            if (!File.Exists(basisPath))
                continue;

            var recorded = new Dictionary<ulong, string>();
            foreach (string line in File.ReadAllLines(basisPath))
            {
                string[] parts = line.Split(' ');
                if (parts.Length == 2 && ulong.TryParse(parts[0], out ulong id))
                    recorded[id] = parts[1];
            }

            Dictionary<ulong, string> current = Basis(FamilyOf(path), SlotOf(path), recorded.Keys);
            var changed = recorded
                .Where(pair => !current.TryGetValue(pair.Key, out string? hash) || hash != pair.Value)
                .Select(pair => pair.Key)
                .ToHashSet();
            if (changed.Count == 0)
                continue;

            using ForgeArchive archive = ForgeArchive.Open(path);
            stale.Add(new StaleAddon(path, archive.Entries
                .Where(entry => changed.Contains(entry.Id))
                .Select(entry => entry.Name)
                .ToList()));
        }

        return stale;
    }

    static Dictionary<ulong, string> Basis(string family, int below, IEnumerable<ulong> ids) =>
        Current(family, below, ids).ToDictionary(pair => pair.Key,
            pair => Convert.ToHexString(SHA1.HashData(pair.Value.Data)));

    static Dictionary<ulong, (byte[] Data, byte[] Prefetch)> Current(string family, int below,
        IEnumerable<ulong> ids)
    {
        var wanted = new HashSet<ulong>(ids);
        var current = new Dictionary<ulong, (byte[] Data, byte[] Prefetch)>();
        for (int slot = below - 1; slot >= 0 && wanted.Count > 0; slot--)
        {
            string path = slot == 0 ? family + ".forge" : SlotPath(family, slot);
            if (!File.Exists(path))
                continue;

            using ForgeArchive archive = ForgeArchive.Open(path);
            if (!archive.Entries.Any(entry => wanted.Contains(entry.Id)))
                continue;

            ForgeEntry? prefetchEntry = archive.Entries.FirstOrDefault(entry => entry.Id == 145);
            byte[] prefetch = prefetchEntry is null ? [] : archive.ReadEntry(prefetchEntry);
            foreach (ForgeEntry entry in archive.Entries)
            {
                if (wanted.Remove(entry.Id))
                    current[entry.Id] = (archive.ReadEntry(entry),
                        PrefetchingFileInfos.ReadObjectBlock(prefetch, entry.Id));
            }
        }

        return current;
    }

    public static long EstimateSize(IReadOnlyList<ArchiveWork> plans)
    {
        ArgumentNullException.ThrowIfNull(plans);
        long total = 0;
        foreach (IGrouping<string, ArchiveWork> family in plans.GroupBy(work => FamilyOf(work.Path),
                     StringComparer.OrdinalIgnoreCase))
        {
            if (family.Key.Length == 0)
                continue;
            total += Collect(family, family.Key, NextSlot(family.First().Path), null)
                .Values.Sum(entry => (long)entry.Data.Length);
        }

        return total;
    }

    static Dictionary<ulong, ForgeNewEntry> Collect(IEnumerable<ArchiveWork> works, string family,
        int slot, IProgress<string>? progress)
    {
        var entries = new Dictionary<ulong, ForgeNewEntry>();
        var changes = new Dictionary<ulong, (ForgeEntry Entry, ContainerChanges Changes)>();
        foreach (ArchiveWork work in works.OrderByDescending(work => SlotOf(work.Path)))
        {
            progress?.Report($"Collecting {work.Name}...");
            using ForgeArchive archive = ForgeArchive.Open(work.Path);
            foreach ((int index, byte[] data) in work.Entries)
            {
                ForgeEntry entry = archive.Entries.FirstOrDefault(candidate => candidate.Index == index)
                    ?? throw new InvalidDataException($"{work.Name} no longer holds container {index}.");
                if (entry.Id is 16 or 145)
                    continue;
                ContainerChanges change = ContainerChanges.Between(archive.ReadEntry(entry), data, entry.Id);
                if (changes.TryGetValue(entry.Id, out var previous))
                    previous.Changes.Include(change);
                else
                    changes.Add(entry.Id, (entry, change));
            }

            foreach (ForgeEntryAddition addition in work.EntryAdditions)
            {
                entries.TryAdd(addition.Id, new ForgeNewEntry(addition.Id, addition.Name,
                    addition.Extension, BitConverter.ToUInt64(addition.InfoTemplate, 4),
                    addition.Data, addition.PrefetchBlock));
            }
        }

        foreach (var (id, current) in Current(family, slot, changes.Keys))
        {
            var (entry, change) = changes[id];
            foreach (string name in change.Overwrites(current.Data))
                progress?.Report($"{entry.Name}: {name} replaces the version of an earlier addon");
            entries[id] = new ForgeNewEntry(id, entry.Name, entry.Extension, entry.UmacHash,
                change.ApplyTo(current.Data), current.Prefetch);
        }

        return entries;
    }

    sealed class ContainerChanges
    {
        readonly Dictionary<ulong, Resource> _before = [];
        readonly List<(Resource Resource, ulong After)> _written = [];
        readonly HashSet<ulong> _removed = [];

        public static ContainerChanges Between(byte[] originalData, byte[] rebuiltData, ulong containerId)
        {
            DataFile original = Read(originalData);
            DataFile rebuilt = Read(rebuiltData);
            if (rebuilt.Resources.Count == 0 || rebuilt.Resources[0].Id != containerId)
                throw new InvalidDataException(
                    "The first resource no longer matches the Forge container id.");

            var changes = new ContainerChanges();
            foreach (Resource resource in original.Resources)
            {
                if (!changes._before.TryAdd(resource.Id, resource))
                    throw new InvalidDataException($"The original container contains resource id "
                        + $"0x{resource.Id:X16} more than once.");
            }

            var kept = new HashSet<ulong>();
            ulong after = 0;
            foreach (Resource resource in rebuilt.Resources)
            {
                if (!kept.Add(resource.Id))
                    throw new InvalidDataException($"The rebuilt container contains resource id "
                        + $"0x{resource.Id:X16} more than once.");
                if (!changes._before.TryGetValue(resource.Id, out Resource? previous) || !Same(resource, previous))
                    changes._written.Add((resource, after));
                after = resource.Id;
            }

            changes._removed.UnionWith(changes._before.Keys.Where(id => !kept.Contains(id)));
            return changes;
        }

        public IEnumerable<string> Overwrites(byte[] currentData)
        {
            var touched = _written.Select(written => written.Resource.Id).Concat(_removed).ToHashSet();
            foreach (Resource resource in Read(currentData).Resources)
            {
                if (touched.Contains(resource.Id) && _before.TryGetValue(resource.Id, out Resource? before)
                    && !Same(resource, before))
                    yield return resource.Name;
            }
        }

        public void Include(ContainerChanges other)
        {
            foreach (var written in other._written)
            {
                ulong id = written.Resource.Id;
                int existing = _written.FindIndex(item => item.Resource.Id == id);
                if (_removed.Contains(id) || existing >= 0 && !Same(_written[existing].Resource, written.Resource))
                    throw new InvalidDataException($"Resource 0x{id:X16} has conflicting changes in different archive copies.");
                if (existing >= 0)
                    continue;
                _written.Add(written);
                CopyBaseline(other, id);
            }
            foreach (ulong id in other._removed)
            {
                if (_written.Any(item => item.Resource.Id == id))
                    throw new InvalidDataException($"Resource 0x{id:X16} is both replaced and removed in different archive copies.");
                if (_removed.Add(id))
                    CopyBaseline(other, id);
            }
        }

        void CopyBaseline(ContainerChanges other, ulong id)
        {
            if (other._before.TryGetValue(id, out Resource? before))
                _before[id] = before;
            else
                _before.Remove(id);
        }

        public byte[] ApplyTo(byte[] currentData)
        {
            DataFile current = Read(currentData);
            List<Resource> resources = current.Resources;
            resources.RemoveAll(resource => _removed.Contains(resource.Id));
            foreach (var (resource, after) in _written)
            {
                int existing = resources.FindIndex(candidate => candidate.Id == resource.Id);
                if (existing >= 0)
                {
                    resources[existing] = resource;
                    continue;
                }

                int previous = resources.FindIndex(candidate => candidate.Id == after);
                resources.Insert(previous >= 0 ? previous + 1 : resources.Count, resource);
            }

            using var output = new MemoryStream();
            current.Write(output);
            return output.ToArray();
        }

        static DataFile Read(byte[] data)
        {
            using var stream = new MemoryStream(data, writable: false);
            return DataFile.Read(stream);
        }

        static bool Same(Resource left, Resource right) =>
            left.ClassHash == right.ClassHash
            && left.Header.AsSpan().SequenceEqual(right.Header)
            && left.Data.AsSpan().SequenceEqual(right.Data);
    }

    internal static string FamilyOf(string archivePath)
    {
        string directory = Path.GetDirectoryName(archivePath) ?? "";
        string name = Path.GetFileNameWithoutExtension(archivePath);
        int patch = name.LastIndexOf("_patch_", StringComparison.OrdinalIgnoreCase);
        if (patch >= 0)
            name = name[..patch];
        return name.Length == 0 ? "" : Path.Combine(directory, name);
    }

    static int SlotOf(string archivePath)
    {
        string name = Path.GetFileNameWithoutExtension(archivePath);
        int patch = name.LastIndexOf("_patch_", StringComparison.OrdinalIgnoreCase);
        return patch >= 0 && int.TryParse(name[(patch + 7)..], out int slot) ? slot : 0;
    }

    static string SlotPath(string family, int slot) => $"{family}_patch_{slot:00}.forge";

    internal static int NextSlot(string archivePath)
    {
        string family = FamilyOf(archivePath);
        int slot = FirstSlot;
        for (int existing = FirstSlot; existing <= LastSlot; existing++)
            if (File.Exists(SlotPath(family, existing)))
                slot = existing + 1;
        return slot;
    }
}
