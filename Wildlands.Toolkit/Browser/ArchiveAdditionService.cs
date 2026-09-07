using System.Buffers.Binary;
using System.IO;
using System.Text;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public sealed record ResourceAdditionSource(
    string Path,
    byte[] Data,
    ulong Id,
    uint ClassHash,
    string Name,
    string Type);

public sealed record ContainerAdditionSource(
    string Path,
    byte[] Data,
    ulong Id,
    uint ClassHash,
    string Name,
    int ResourceCount);

public static class ArchiveAdditionService
{
    const ulong MaximumOrdinaryId = 0x0000FFFFFFFFFFFFUL;

    public static ResourceAdditionSource InspectResource(string path,
        IReadOnlyList<Resource> targetResources, Resource? preferredTemplate)
    {
        byte[] original = File.ReadAllBytes(path);
        if (original.Length < 12)
            throw new InvalidDataException("The selected file is too short to be a game resource.");

        byte[] data = original;
        uint classHash = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        bool directTypeExists = targetResources.Any(resource => resource.ClassHash == classHash);

        if (!directTypeExists && data.Length >= 13 && data[0] <= 1)
        {
            uint shiftedClass = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(9));
            Resource? shiftedTemplate = targetResources.FirstOrDefault(resource => resource.ClassHash == shiftedClass);
            if (shiftedTemplate is not null)
            {
                byte[] normalized = ResourceCheck.NormalizeExternalFile(data, shiftedClass, out _);
                data = normalized.Length == data.Length ? data.AsSpan(1).ToArray() : normalized;
                classHash = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
            }
        }

        if (!targetResources.Any(resource => resource.ClassHash == classHash))
            throw new InvalidDataException($"This is a {ResourceTypes.NameOf(classHash)} resource (0x{classHash:X8}), but the target container has no resource of that type whose binary header can be copied. Add it as part of a complete .data container instead.");

        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data);
        string name = CleanName(Path.GetFileNameWithoutExtension(path));
        if (name.Length == 0)
            name = preferredTemplate?.Name + "_Custom" ?? "CustomResource";

        return new ResourceAdditionSource(path, data, id, classHash, name, ResourceTypes.NameOf(classHash));
    }

    public static ContainerAdditionSource InspectContainer(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        DataFile file;
        try
        {
            file = DataFile.Read(new MemoryStream(data, writable: false));
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("The selected file is not a complete Wildlands .data container.", ex);
        }

        if (file.Resources.Count == 0)
            throw new InvalidDataException("The selected .data container has no resources.");
        if (file.Resources.Any(resource => resource.Id == 0))
            throw new InvalidDataException("The selected .data container contains a resource with ID 0.");
        IGrouping<ulong, Resource>? duplicate = file.Resources
            .GroupBy(resource => resource.Id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidDataException($"The selected .data container contains resource ID 0x{duplicate.Key:X16} more than once.");

        foreach (Resource resource in file.Resources)
        {
            string? complaint = ResourceCheck.Against(resource.Data, resource.Id, resource.ClassHash, resource.Name);
            if (complaint is not null)
                throw new InvalidDataException($"{resource.Name}: {complaint}");
        }

        Resource root = file.Resources[0];
        ValidateOrdinaryId(root.Id, "container");
        string name = CleanName(root.Name);
        if (name.Length == 0)
            name = CleanName(Path.GetFileNameWithoutExtension(path));
        if (name.Length == 0)
            name = $"Custom_{root.Id:X}";

        return new ContainerAdditionSource(path, data, root.Id, root.ClassHash, name, file.Resources.Count);
    }

    public static IReadOnlyList<Resource> ResourceTemplates(ResourceAdditionSource source, IReadOnlyList<Resource> resources) => resources
        .Where(resource => resource.ClassHash == source.ClassHash)
        .OrderBy(resource => resource.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static IReadOnlyList<ForgeEntry> ContainerTemplates(ContainerAdditionSource source, IReadOnlyList<ForgeEntry> entries) => entries
        .Where(entry => entry.FileExtension == ".data" && entry.Id is not 0 and not 145 && entry.Extension == source.ClassHash)
        .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static ulong SuggestedResourceId(ResourceAdditionSource source, IReadOnlyList<Resource> existing, IReadOnlyList<PendingChange> pending)
    {
        var used = existing.Select(resource => resource.Id)
            .Concat(pending.Where(change => change.Addition is not null)
                .Select(change => change.Addition!.Id))
            .ToHashSet();
        if (source.Id is > 0 and <= MaximumOrdinaryId && !used.Contains(source.Id))
            return source.Id;

        string seed = $"raw-resource:{source.Name}:{source.ClassHash:X8}:{Guid.NewGuid():N}";
        return AllocateId(seed, used);
    }

    public static PendingChange CreateResourceAddition(Location target, ResourceAdditionSource source, Resource template, string name, ulong id, IReadOnlyList<Resource> existing, IReadOnlyList<PendingChange> pending)
    {
        if (target.IsArchiveRoot)
            throw new InvalidOperationException("A resource needs a target .data container.");
        ValidateName(name, "resource");
        ValidateOrdinaryId(id, "resource");
        if (template.ClassHash != source.ClassHash)
            throw new InvalidDataException($"The selected setup is {ResourceTypes.NameOf(template.ClassHash)}, not {source.Type}.");
        if (existing.Any(resource => resource.Id == id) || pending.Any(change => change.Addition?.Id == id))
            throw new InvalidDataException($"Resource ID 0x{id:X16} is already used in this container or its pending changes.");

        byte[] payload = (byte[])source.Data.Clone();
        RemapId(payload, source.Id, id);
        BinaryPrimitives.WriteUInt64LittleEndian(payload, id);
        string? complaint = ResourceCheck.Against(payload, id, source.ClassHash, name);
        if (complaint is not null)
            throw new InvalidDataException(complaint);

        Resource clone = DataFile.CloneResource(template, id, name, payload);
        return new PendingChange(target.ArchivePath, target.EntryIndex, target.EntryName, -1, name, clone.Data,
            new PendingResourceAddition(id, source.ClassHash, clone.Header), ResourceClassHash: source.ClassHash);
    }

    public static PendingChange CreateContainerAddition(string archivePath, ContainerAdditionSource source, ulong templateId, string name, IReadOnlyList<PendingChange> pending)
    {
        using var archive = ForgeArchive.Open(archivePath);
        ForgeEntry template = archive.Entries.FirstOrDefault(entry => entry.Id == templateId) ?? throw new InvalidDataException($"The selected setup container 0x{templateId:X16} is no longer present in the archive.");
        ValidateName(name, "container");
        if (Encoding.UTF8.GetByteCount(name) >= 128)
            throw new InvalidDataException("A Forge container name must be shorter than 128 UTF-8 bytes.");
        if (template.FileExtension != ".data")
            throw new InvalidDataException("The selected setup source is not a .data container.");
        if (template.Extension != source.ClassHash)
            throw new InvalidDataException("The setup source has a different root resource type. Choose a container of the same type.");

        var pendingEntries = pending
            .Where(change => change.ArchivePath.Equals(archive.FilePath, StringComparison.OrdinalIgnoreCase) && change.EntryAddition is not null)
            .ToList();
        if (archive.Entries.Any(entry => entry.Id == source.Id) || pendingEntries.Any(change => change.EntryAddition!.Id == source.Id))
            throw new InvalidDataException($"Forge entry ID 0x{source.Id:X16} is already present in this archive or its pending changes. The IDs inside a complete .data file are preserved, so prepare the source with unused IDs first.");

        ForgeEntry registry = archive.Entries.SingleOrDefault(entry => entry.Id == 145) ?? throw new InvalidDataException("This archive has no PrefetchingFileInfos registry and cannot safely load a new container.");
        byte[] prefetch = archive.ReadEntry(registry);
        byte[] prefetchBlock = PrefetchingFileInfos.ReadObjectBlock(prefetch, template.Id);
        byte[] info = archive.ReadEntryInfo(template);

        var usedUmacs = archive.Entries.Select(entry => entry.UmacHash)
            .Concat(pendingEntries.Select(change => BinaryPrimitives.ReadUInt64LittleEndian(change.EntryAddition!.InfoTemplate.AsSpan(4))))
            .ToHashSet();
        ulong umac = AllocateUmac($"forge-entry:{source.Id:X16}:{name}", usedUmacs);
        BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(4), umac);

        var addition = new PendingForgeEntryAddition(source.Id, name, source.ClassHash, info, prefetchBlock);
        var change = new PendingChange(archive.FilePath, -1, name, -1, name, (byte[])source.Data.Clone(), EntryAddition: addition, ResourceClassHash: source.ClassHash);

        var allAdditions = pendingEntries.Select(ToForgeAddition)
            .Append(ToForgeAddition(change))
            .ToList();
        archive.EstimateRebuiltSize(new Dictionary<int, byte[]>(), allAdditions);
        return change;
    }

    static ForgeEntryAddition ToForgeAddition(PendingChange change)
    {
        PendingForgeEntryAddition addition = change.EntryAddition ?? throw new InvalidOperationException("The pending change is not a Forge entry addition.");
        return new ForgeEntryAddition(addition.Id, addition.Name, addition.Extension, addition.InfoTemplate, change.Data, addition.PrefetchBlock);
    }

    static void RemapId(byte[] data, ulong oldId, ulong newId)
    {
        if (oldId == 0 || oldId == newId)
            return;
        for (int offset = 0; offset <= data.Length - sizeof(ulong); offset++)
            if (BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset)) == oldId)
                BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(offset), newId);
    }

    static ulong AllocateId(string seed, HashSet<ulong> used)
    {
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            ulong candidate = 0x000000E000000000UL | (Fnv64(attempt == 0 ? seed : seed + ":" + attempt) & 0x0000000FFFFFFFFFUL);
            if (used.Add(candidate))
                return candidate;
        }
        throw new InvalidOperationException("Could not generate an unused game resource ID.");
    }

    static ulong AllocateUmac(string seed, HashSet<ulong> used)
    {
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            ulong candidate = Fnv64(attempt == 0 ? seed : seed + ":" + attempt);
            if (candidate != 0 && used.Add(candidate))
                return candidate;
        }
        throw new InvalidOperationException("Could not generate an unused Forge UMAC.");
    }

    static ulong Fnv64(string value)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte item in Encoding.UTF8.GetBytes(value))
            hash = (hash ^ item) * 1099511628211UL;
        return hash;
    }

    static string CleanName(string value)
    {
        string name = value.Trim();
        int marker = name.IndexOf("_-_", StringComparison.Ordinal);
        if (marker >= 0 && int.TryParse(name[..marker], out _))
            name = name[(marker + 3)..];
        return name.Trim();
    }

    static void ValidateName(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidDataException($"Enter a name for the new {kind}.");
        if (name.Length > 512 || name.Any(char.IsControl))
            throw new InvalidDataException($"The {kind} name is invalid or too long.");
    }

    static void ValidateOrdinaryId(ulong id, string kind)
    {
        if (id == 0 || id > MaximumOrdinaryId)
            throw new InvalidDataException($"The {kind} ID must be between 0x1 and 0x{MaximumOrdinaryId:X16}.");
    }
}
