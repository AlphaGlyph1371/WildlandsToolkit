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
                    _ = BuildResourcePatch(archive.ReadEntry(entry), data, entry,
                        out bool removesResources);
                    if (removesResources)
                    {
                        reason = $"{entry.Name} removes one or more resources. An addon archive "
                            + "cannot hide resources from an older archive, so this has to be written in place.";
                        return false;
                    }
                }
                catch (Exception ex) when (ex is InvalidDataException or OverflowException)
                {
                    reason = $"{entry.Name} cannot be written as a resource-level addon: {ex.Message}";
                    return false;
                }
            }
        }

        reason = "";
        return true;
    }

    public static IReadOnlyList<string> Write(IReadOnlyList<ArchiveWork> plans, string identity,
        IProgress<string>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(plans);
        if (!CanWrite(plans, out string reason))
            throw new InvalidOperationException(reason);

        var written = new List<string>();
        foreach (IGrouping<string, ArchiveWork> family in plans.GroupBy(work => FamilyOf(work.Path),
                     StringComparer.OrdinalIgnoreCase))
        {
            var entries = new Dictionary<ulong, ForgeNewEntry>();
            foreach (ArchiveWork work in family.OrderByDescending(work => SlotOf(work.Path)))
            {
                progress?.Report($"Collecting {work.Name}...");
                CollectEntries(work, entries);
            }

            if (entries.Count == 0)
                continue;

            int slot = NextSlot(family.First().Path);
            string target = SlotPath(family.Key, slot);
            progress?.Report($"Writing {Path.GetFileName(target)}...");
            ForgeArchive.Create(target, entries.Values.ToList(), identity,
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

    static Dictionary<ulong, string> Basis(string family, int below, IEnumerable<ulong> ids)
    {
        var wanted = new HashSet<ulong>(ids);
        var basis = new Dictionary<ulong, string>();
        for (int slot = below - 1; slot >= 0 && wanted.Count > 0; slot--)
        {
            string path = slot == 0 ? family + ".forge" : SlotPath(family, slot);
            if (!File.Exists(path))
                continue;

            using ForgeArchive archive = ForgeArchive.Open(path);
            foreach (ForgeEntry entry in archive.Entries)
            {
                if (wanted.Remove(entry.Id))
                    basis[entry.Id] = Convert.ToHexString(SHA1.HashData(archive.ReadEntry(entry)));
            }
        }

        return basis;
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
            var entries = new Dictionary<ulong, ForgeNewEntry>();
            foreach (ArchiveWork work in family.OrderByDescending(work => SlotOf(work.Path)))
                CollectEntries(work, entries);
            total += entries.Values.Sum(entry => (long)entry.Data.Length);
        }

        return total;
    }

    static void CollectEntries(ArchiveWork work, Dictionary<ulong, ForgeNewEntry> entries)
    {
        using ForgeArchive archive = ForgeArchive.Open(work.Path);
        ForgeEntry? prefetchEntry = archive.Entries.FirstOrDefault(entry => entry.Id == 145);
        byte[] prefetch = prefetchEntry is null ? [] : archive.ReadEntry(prefetchEntry);

        foreach ((int index, byte[] data) in work.Entries)
        {
            ForgeEntry entry = archive.Entries.FirstOrDefault(candidate => candidate.Index == index)
                ?? throw new InvalidDataException($"{work.Name} no longer holds container {index}.");
            if (entry.Id is 16 or 145)
                continue;
            byte[] patch = BuildResourcePatch(archive.ReadEntry(entry), data, entry,
                out bool removesResources);
            if (removesResources)
                throw new InvalidOperationException($"{entry.Name} removes one or more resources. "
                    + "An addon archive cannot represent resource deletion.");
            if (entries.TryGetValue(entry.Id, out ForgeNewEntry? preferred))
            {
                entries[entry.Id] = preferred with
                {
                    Data = MergeResourcePatches(preferred.Data, patch, entry.Id),
                };
                continue;
            }
            entries.Add(entry.Id, new ForgeNewEntry(entry.Id, entry.Name, entry.Extension,
                entry.UmacHash, patch, PrefetchingFileInfos.ReadObjectBlock(prefetch, entry.Id)));
        }

        foreach (ForgeEntryAddition addition in work.EntryAdditions)
        {
            if (entries.ContainsKey(addition.Id))
                continue;
            entries.Add(addition.Id, new ForgeNewEntry(addition.Id, addition.Name,
                addition.Extension, BitConverter.ToUInt64(addition.InfoTemplate, 4),
                addition.Data, addition.PrefetchBlock));
        }
    }

    /// <summary>
    /// Builds the smallest valid overlay for an existing multi-resource container. The first
    /// resource is the container identity and must stay first; the game then resolves the other
    /// resources by their own ids across the archive family.
    /// </summary>
    internal static byte[] BuildResourcePatch(byte[] originalData, byte[] rebuiltData,
        ForgeEntry entry, out bool removesResources)
    {
        using var originalStream = new MemoryStream(originalData, writable: false);
        using var rebuiltStream = new MemoryStream(rebuiltData, writable: false);
        DataFile original = DataFile.Read(originalStream);
        DataFile rebuilt = DataFile.Read(rebuiltStream);
        if (rebuilt.Resources.Count == 0 || rebuilt.Resources[0].Id != entry.Id)
            throw new InvalidDataException(
                "The first resource no longer matches the Forge container id.");

        var originalById = new Dictionary<ulong, Resource>();
        foreach (Resource resource in original.Resources)
        {
            if (!originalById.TryAdd(resource.Id, resource))
                throw new InvalidDataException($"The original container contains resource id "
                    + $"0x{resource.Id:X16} more than once.");
        }

        var rebuiltIds = new HashSet<ulong>();
        foreach (Resource resource in rebuilt.Resources)
        {
            if (!rebuiltIds.Add(resource.Id))
                throw new InvalidDataException($"The rebuilt container contains resource id "
                    + $"0x{resource.Id:X16} more than once.");
        }
        removesResources = originalById.Keys.Any(id => !rebuiltIds.Contains(id));

        Resource root = rebuilt.Resources[0];
        List<Resource> changed = rebuilt.Resources.Skip(1)
            .Where(resource => !originalById.TryGetValue(resource.Id, out Resource? previous)
                || resource.ClassHash != previous.ClassHash
                || !resource.Header.AsSpan().SequenceEqual(previous.Header)
                || !resource.Data.AsSpan().SequenceEqual(previous.Data))
            .ToList();

        rebuilt.Resources.Clear();
        rebuilt.Resources.Add(root);
        rebuilt.Resources.AddRange(changed);

        using var output = new MemoryStream();
        rebuilt.Write(output);
        return output.ToArray();
    }

    static byte[] MergeResourcePatches(byte[] preferredData, byte[] fallbackData,
        ulong containerId)
    {
        using var preferredStream = new MemoryStream(preferredData, writable: false);
        using var fallbackStream = new MemoryStream(fallbackData, writable: false);
        DataFile preferred = DataFile.Read(preferredStream);
        DataFile fallback = DataFile.Read(fallbackStream);
        if (preferred.Resources.Count == 0 || fallback.Resources.Count == 0
            || preferred.Resources[0].Id != containerId
            || fallback.Resources[0].Id != containerId)
            throw new InvalidDataException(
                "Resource overlays do not share the Forge container identity.");

        var ids = preferred.Resources.Select(resource => resource.Id).ToHashSet();
        foreach (Resource resource in fallback.Resources.Skip(1))
        {
            if (ids.Add(resource.Id))
                preferred.Resources.Add(resource);
        }

        using var output = new MemoryStream();
        preferred.Write(output);
        return output.ToArray();
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
        while (slot <= LastSlot && File.Exists(SlotPath(family, slot)))
            slot++;
        return slot;
    }
}
