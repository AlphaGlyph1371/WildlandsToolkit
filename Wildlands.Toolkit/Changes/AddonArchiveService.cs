using System.IO;
using System.Security.Cryptography;
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
        var seen = new Dictionary<string, Dictionary<ulong, long>>(StringComparer.OrdinalIgnoreCase);
        foreach (ArchiveWork work in plans)
        {
            if (FamilyOf(work.Path).Length == 0)
                continue;
            if (!seen.TryGetValue(FamilyOf(work.Path), out Dictionary<ulong, long>? sizes))
                seen[FamilyOf(work.Path)] = sizes = [];

            using ForgeArchive archive = ForgeArchive.Open(work.Path);
            foreach ((int index, byte[] data) in work.Entries)
            {
                ForgeEntry? entry = archive.Entries.FirstOrDefault(candidate => candidate.Index == index);
                if (entry is not null)
                    sizes[entry.Id] = data.Length;
            }

            foreach (ForgeEntryAddition addition in work.EntryAdditions)
                sizes[addition.Id] = addition.Data.Length;
        }

        return seen.Values.Sum(sizes => sizes.Values.Sum());
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
            if (entry.Id is 16 or 145 || entries.ContainsKey(entry.Id))
                continue;
            entries.Add(entry.Id, new ForgeNewEntry(entry.Id, entry.Name, entry.Extension,
                entry.UmacHash, data, PrefetchingFileInfos.ReadObjectBlock(prefetch, entry.Id)));
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
