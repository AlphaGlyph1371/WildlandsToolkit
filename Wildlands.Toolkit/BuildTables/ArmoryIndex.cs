using System.Buffers.Binary;
using System.IO;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public sealed class ArmoryIndex
{
    const uint FileMagic = 0x41524D31; // ARM1
    const int FormatVersion = 4;

    readonly List<IndexedResource> _databaseResources = [];
    readonly List<AvailabilityListCandidate> _availabilityLists = [];
    readonly Dictionary<string, Dictionary<ulong, string>> _strings = new(StringComparer.OrdinalIgnoreCase);

    public string Fingerprint { get; private set; } = "";
    public int DatabaseResourceCount => _databaseResources.Count;
    public IReadOnlyList<string> LanguagePackages => _strings.Keys
        .OrderBy(name => string.Equals(name, "English(US)", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
        .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    internal IReadOnlyList<IndexedResource> DatabaseResources => _databaseResources;
    internal IReadOnlyList<AvailabilityListCandidate> AvailabilityLists => _availabilityLists;

    public IReadOnlyList<ArmoryRegistryEvidence> FindRegistryEvidence(ulong memberId)
    {
        return _availabilityLists
        .Where(list => list.RecordIds.Contains(memberId))
        .Select(list =>
        {
            IndexedResource owner = _databaseResources[list.OwnerResourceIndex];
            return new ArmoryRegistryEvidence(owner.Id, owner.Name, owner.ClassHash, owner.ArchivePath, owner.EntryIndex, owner.EntryName,
                owner.ResourceIndex, list.CountOffset, list.EntryStride, list.ValueOffset, list.RecordIds);
        })
        .OrderBy(evidence => evidence.ArchivePath, StringComparer.OrdinalIgnoreCase)
        .ThenBy(evidence => evidence.EntryIndex)
        .ThenBy(evidence => evidence.ResourceIndex)
        .ThenBy(evidence => evidence.CountOffset)
        .ToList();
    }

    public IReadOnlyList<ArmoryRegistryEvidence> FindUnlockableRegistryEvidence(ulong memberId)
    {
        return GunsmithAvailability.FindUnlockRegistries(this, memberId)
            .Select(list => new ArmoryRegistryEvidence(list.Owner.Id, list.Owner.Name, list.Owner.ClassHash, list.Owner.ArchivePath, list.Owner.EntryIndex,
                list.Owner.EntryName, list.Owner.ResourceIndex, list.CountOffset, list.EntryStride, list.ValueOffset, list.RecordIds))
            .ToList();
    }

    public IReadOnlyList<ArmoryRegistryEvidence> FindVestRegistryEvidence(ulong memberId)
    {
        return GunsmithAvailability.FindVestRegistries(this, memberId)
            .Select(list => new ArmoryRegistryEvidence(list.Owner.Id, list.Owner.Name, list.Owner.ClassHash, list.Owner.ArchivePath, list.Owner.EntryIndex,
                list.Owner.EntryName, list.Owner.ResourceIndex, list.CountOffset, list.EntryStride, list.ValueOffset, list.RecordIds))
            .ToList();
    }

    public IReadOnlyList<ArmoryObjectListEvidence> FindCharacterSmithRegistryEvidence(ulong memberId)
    {
        return CharacterSmithRegistry.Find(this, memberId)
            .Select(list => new ArmoryObjectListEvidence(list.Owner.Id, list.Owner.Name, list.Owner.ClassHash, list.Owner.ArchivePath, list.Owner.EntryIndex,
                list.Owner.EntryName, list.Owner.ResourceIndex, list.HeaderOffset, list.CountOffset, list.Entries.Count,
                CharacterSmithRegistry.StructuredEntryClassHash))
            .ToList();
    }

    public ArmoryDatabaseResourceChange CreateCharacterSmithInsertion(ulong templateRecordId, ulong newRecordId)
    {
        if (newRecordId == 0 || _databaseResources.Any(resource => resource.Id == newRecordId))
            throw new InvalidOperationException($"CharacterSmith record ID 0x{newRecordId:X12} is zero or already used.");
        CharacterSmithRegistryList list = CharacterSmithRegistry.Find(this, templateRecordId).Single();
        byte[] data = CharacterSmithRegistry.InsertAfter(list, templateRecordId, newRecordId);
        return new ArmoryDatabaseResourceChange(list.Owner.ArchivePath, list.Owner.EntryIndex, list.Owner.EntryName, list.Owner.ResourceIndex, list.Owner.Name, data);
    }

    public IReadOnlyList<ArmoryDatabaseResourceChange> CreateVestRegistryInsertions(ulong templateRecordId, ulong newRecordId)
    {
        if (newRecordId == 0 || _databaseResources.Any(resource => resource.Id == newRecordId))
            throw new InvalidOperationException($"Vest record ID 0x{newRecordId:X12} is zero or already used.");

        GunsmithAvailabilityList vests = GunsmithAvailability.FindVestRegistries(this, templateRecordId).Single();
        GunsmithAvailabilityList databaseContainer = GunsmithAvailability.FindDatabaseContainerRegistries(this, templateRecordId).Single();
        GunsmithAvailabilityList unlockables = GunsmithAvailability.FindUnlockRegistries(this, templateRecordId).Single();
        var changes = new List<ArmoryDatabaseResourceChange> { CreateCharacterSmithInsertion(templateRecordId, newRecordId) };
        changes.AddRange(GunsmithAvailability.InsertAfterTemplates([(vests, templateRecordId, newRecordId)]));
        changes.AddRange(GunsmithAvailability.InsertAfterTemplates([(databaseContainer, templateRecordId, newRecordId)]));
        changes.AddRange(GunsmithAvailability.InsertAfterTemplates([(unlockables, templateRecordId, newRecordId)]));

        if (changes.Count != 4 || changes.Select(change => (change.ArchivePath, change.EntryIndex, change.ResourceIndex)).Distinct().Count() != changes.Count)
            throw new InvalidDataException("The vest registry insertion did not produce four independent resource changes.");
        return changes;
    }

    internal IReadOnlyList<ArmoryDatabaseResourceChange> CreateVestRegistryInsertions(
        ulong templateRecordId, ulong newRecordId, ulong templateLootId, ulong newLootId)
    {
        if (newRecordId == 0 || newLootId == 0
            || _databaseResources.Any(resource => resource.Id == newRecordId || resource.Id == newLootId))
            throw new InvalidOperationException("The new vest record or loot-configuration ID is zero or already used.");

        GunsmithAvailabilityList vests = GunsmithAvailability.FindVestRegistries(this, templateRecordId).Single();
        GunsmithAvailabilityList recordDatabase = GunsmithAvailability.FindDatabaseContainerRegistries(this, templateRecordId).Single();
        GunsmithAvailabilityList lootDatabase = GunsmithAvailability.FindDatabaseContainerRegistries(this, templateLootId).Single();
        GunsmithAvailabilityList unlockables = GunsmithAvailability.FindUnlockRegistries(this, templateRecordId).Single();
        var insertions = new List<(GunsmithAvailabilityList List, ulong TemplateId, ulong NewId)>
        {
            (vests, templateRecordId, newRecordId),
            (recordDatabase, templateRecordId, newRecordId),
            (lootDatabase, templateLootId, newLootId),
            (unlockables, templateRecordId, newRecordId),
        };

        var changes = new List<ArmoryDatabaseResourceChange>
        {
            CreateCharacterSmithInsertion(templateRecordId, newRecordId),
        };
        changes.AddRange(GunsmithAvailability.InsertAfterTemplates(insertions));
        if (changes.Select(change => (change.ArchivePath, change.EntryIndex, change.ResourceIndex)).Distinct().Count() != changes.Count)
            throw new InvalidDataException("The complete vest registry insertion produced overlapping resource changes.");
        return changes;
    }

    public ArmoryDatabaseResourceChange CreateStoreRegistryInsertion(ulong templateInfoId, ulong newInfoId)
    {
        if (newInfoId == 0 || _databaseResources.Any(resource => resource.Id == newInfoId))
            throw new InvalidOperationException($"StoreObjectInfo ID 0x{newInfoId:X12} is zero or already used.");
        GunsmithAvailabilityList store = GunsmithAvailability.FindStoreRegistries(this, templateInfoId).Single();
        return GunsmithAvailability.InsertAfterTemplates([(store, templateInfoId, newInfoId)]).Single();
    }

    internal ArmoryDatabaseResourceChange CreateDatabaseContainerInsertion(ulong templateId, ulong newId)
    {
        if (newId == 0 || _databaseResources.Any(resource => resource.Id == newId))
            throw new InvalidOperationException($"Database resource ID 0x{newId:X12} is zero or already used.");
        GunsmithAvailabilityList container = GunsmithAvailability.FindDatabaseContainerRegistries(this, templateId).Single();
        return GunsmithAvailability.InsertAfterTemplates([(container, templateId, newId)]).Single();
    }

    internal ArmoryDatabaseResourceChange CreateStoreRegistryReplacement(ulong oldInfoId, ulong newInfoId)
    {
        if (newInfoId == 0 || _databaseResources.Any(resource => resource.Id == newInfoId))
            throw new InvalidOperationException($"StoreObjectInfo ID 0x{newInfoId:X12} is zero or already used.");
        GunsmithAvailabilityList store = GunsmithAvailability.FindStoreRegistries(this, oldInfoId).Single();
        var members = store.RecordIds.Select(id => id == oldInfoId ? newInfoId : id).ToList();
        if (members.Count(id => id == newInfoId) != 1 || members.Contains(oldInfoId))
            throw new InvalidDataException("The StoreDB replacement did not produce exactly one corrected StoreObjectInfo reference.");
        byte[] data = GunsmithAvailability.RewriteMembers(store, members);
        return new ArmoryDatabaseResourceChange(store.Owner.ArchivePath, store.Owner.EntryIndex,
            store.Owner.EntryName, store.Owner.ResourceIndex, store.Owner.Name, data);
    }

    public IReadOnlyList<ArmoryDatabaseResourceChange> CreateBuildTagColumnMapInsertions(uint templateTag, uint newTag)
        => AttachmentAddPipeline.BuildTagColumnMapChanges(this, templateTag, newTag);

    public byte[] CreateStoreObjectInfoClone(ulong templateInfoId, ulong newInfoId, ulong expectedTemplateRecordId, ulong newRecordId, string newName)
    {
        if (newInfoId == 0 || _databaseResources.Any(resource => resource.Id == newInfoId))
            throw new InvalidOperationException($"StoreObjectInfo ID 0x{newInfoId:X12} is zero or already used.");
        if (newRecordId == 0 || _databaseResources.Any(resource => resource.Id == newRecordId))
            throw new InvalidOperationException($"Gameplay-record ID 0x{newRecordId:X12} is zero or already used.");

        IndexedResource template = _databaseResources.LastOrDefault(resource => resource.Id == templateInfoId && resource.ClassHash == StoreObjectInfo.ClassHash)
            ?? throw new InvalidOperationException($"StoreObjectInfo 0x{templateInfoId:X12} was not found in the indexed game database.");
        StoreObjectInfo info = StoreObjectInfo.Parse(template.Data, templateInfoId);
        if (info.RecordId != expectedTemplateRecordId)
            throw new InvalidDataException($"StoreObjectInfo 0x{templateInfoId:X12} references 0x{info.RecordId:X12}, not 0x{expectedTemplateRecordId:X12}.");
        return info.Rewrite(template.Data, newInfoId, newRecordId, newName);
    }

    public IReadOnlyList<ArmoryRegistryEvidence> FindDatabaseContainerRegistryEvidence(ulong memberId) => ToRegistryEvidence(GunsmithAvailability.FindDatabaseContainerRegistries(this, memberId));

    public IReadOnlyList<ArmoryRegistryEvidence> FindLootRegistryEvidence(ulong memberId) => ToRegistryEvidence(GunsmithAvailability.FindLootRegistries(this, memberId));

    public IReadOnlyList<ArmoryRegistryEvidence> FindStoreRegistryEvidence(ulong storeObjectInfoId) => ToRegistryEvidence(GunsmithAvailability.FindStoreRegistries(this, storeObjectInfoId));

    static IReadOnlyList<ArmoryRegistryEvidence> ToRegistryEvidence(IEnumerable<GunsmithAvailabilityList> lists)
    {
        return lists.Select(list => new ArmoryRegistryEvidence(list.Owner.Id, list.Owner.Name, list.Owner.ClassHash, list.Owner.ArchivePath, list.Owner.EntryIndex,
            list.Owner.EntryName, list.Owner.ResourceIndex, list.CountOffset, list.EntryStride, list.ValueOffset, list.RecordIds)).ToList();
    }

    public static ArmoryIndex Build(IReadOnlyList<string> archivePaths, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var index = new ArmoryIndex { Fingerprint = CreateFingerprint(archivePaths) };
        var localizedResources = new List<(string Package, byte[] Data)>();
        var wantedStrings = new HashSet<ulong>();

        for (int archiveIndex = 0; archiveIndex < archivePaths.Count; archiveIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string archivePath = archivePaths[archiveIndex];
            progress?.Report($"Reading armory data, {archiveIndex + 1} of {archivePaths.Count} archives…");

            try
            {
                using var archive = ForgeArchive.Open(archivePath);
                foreach (var entry in archive.Entries.Where(entry => BuildTableGameMetadataResolver.IsGameDatabaseContainer(entry.Name) || BuildTableGameMetadataResolver.TryGetLanguagePackage(entry.Name) is not null))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var stream = new MemoryStream(archive.ReadEntry(entry));
                    var file = DataFile.Read(stream);
                    string? package = BuildTableGameMetadataResolver.TryGetLanguagePackage(entry.Name);

                    if (package is not null)
                    {
                        localizedResources.AddRange(file.Resources
                            .Where(resource => resource.ClassHash == LocalizationPackage.ClassHash)
                            .Select(resource => (package, resource.Data)));
                        continue;
                    }

                    for (int resourceIndex = 0; resourceIndex < file.Resources.Count; resourceIndex++)
                    {
                        var resource = file.Resources[resourceIndex];
                        int indexedResource = index._databaseResources.Count;
                        index._databaseResources.Add(new IndexedResource(resource.Id, resource.Name, resource.ClassHash, resource.Data, archivePath, entry.Index, entry.Name, resourceIndex));
                        BuildTableGameMetadataResolver.CollectLocalizedStringIds(resource.Data, wantedStrings);
                        index._availabilityLists.AddRange(ReadAvailabilityLists(resource.Data, indexedResource, cancellationToken));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Could not index armory data from {archivePath}.", ex);
            }
        }

        foreach (var (package, data) in localizedResources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!index._strings.TryGetValue(package, out var strings))
                    index._strings[package] = strings = [];

                foreach (var pair in LocalizationPackage.Read(data).Strings)
                    if (wantedStrings.Contains(pair.Key))
                        strings[pair.Key] = pair.Value;
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Could not parse the installed {package} localization package.", ex);
            }
        }

        progress?.Report($"BuildTable index ready: {index.DatabaseResourceCount:N0} game records and {index.LanguagePackages.Count} language packages.");
        return index;
    }

    internal IReadOnlyDictionary<ulong, string> StringsFor(string preferredPackage)
    {
        var combined = new Dictionary<ulong, string>();
        if (_strings.TryGetValue("English(US)", out var english))
            foreach (var pair in english)
                combined[pair.Key] = pair.Value;
        if (_strings.TryGetValue(preferredPackage, out var preferred))
            foreach (var pair in preferred)
                combined[pair.Key] = pair.Value;
        return combined;
    }

    internal ArmoryIndex CreateWorkingCopy()
    {
        var copy = new ArmoryIndex { Fingerprint = Fingerprint };
        copy._databaseResources.AddRange(_databaseResources);
        copy._availabilityLists.AddRange(_availabilityLists);
        foreach (var language in _strings)
            copy._strings[language.Key] = new Dictionary<ulong, string>(language.Value);
        return copy;
    }

    internal void ApplyChanges(IReadOnlyList<ArmoryDatabaseResourceChange> changes)
    {
        foreach (var change in changes)
        {
            int ownerIndex = _databaseResources.FindIndex(resource =>
                string.Equals(resource.ArchivePath, change.ArchivePath, StringComparison.OrdinalIgnoreCase)
                && resource.EntryIndex == change.EntryIndex
                && resource.ResourceIndex == change.ResourceIndex);
            if (ownerIndex < 0)
                continue;

            var owner = _databaseResources[ownerIndex];
            _databaseResources[ownerIndex] = owner with { Data = (byte[])change.Data.Clone() };
            _availabilityLists.RemoveAll(candidate => candidate.OwnerResourceIndex == ownerIndex);
            _availabilityLists.AddRange(ReadAvailabilityLists(change.Data, ownerIndex, CancellationToken.None));
        }
    }

    internal void ApplyAdditions(IReadOnlyList<ArmoryArchiveResourceAddition> additions)
    {
        foreach (var addition in additions.Where(addition => BuildTableGameMetadataResolver.IsGameDatabaseContainer(addition.EntryName)))
        {
            if (_databaseResources.Any(resource => resource.Id == addition.ResourceId))
                continue;

            int syntheticResourceIndex = -1;
            while (_databaseResources.Any(resource => string.Equals(resource.ArchivePath, addition.ArchivePath, StringComparison.OrdinalIgnoreCase) && resource.EntryIndex == addition.EntryIndex && resource.ResourceIndex == syntheticResourceIndex))
                syntheticResourceIndex--;

            int indexedResource = _databaseResources.Count;
            _databaseResources.Add(new IndexedResource(addition.ResourceId, addition.ResourceName, addition.ClassHash, (byte[])addition.Data.Clone(), addition.ArchivePath, addition.EntryIndex, addition.EntryName, syntheticResourceIndex));
            _availabilityLists.AddRange(ReadAvailabilityLists(addition.Data, indexedResource, CancellationToken.None));
        }
    }

    internal void RefreshFingerprint(IReadOnlyList<string> archivePaths) => Fingerprint = CreateFingerprint(archivePaths);

    public void Save(string path)
    {
        string partial = PartialPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        using (var stream = File.Create(partial))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(FileMagic);
            writer.Write(FormatVersion);
            writer.Write(Fingerprint);

            writer.Write(_strings.Count);
            foreach (var language in _strings.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                writer.Write(language.Key);
                writer.Write(language.Value.Count);
                foreach (var pair in language.Value)
                {
                    writer.Write(pair.Key);
                    writer.Write(pair.Value);
                }
            }

            writer.Write(_databaseResources.Count);
            foreach (var resource in _databaseResources)
            {
                writer.Write(resource.Id);
                writer.Write(resource.Name);
                writer.Write(resource.ClassHash);
                writer.Write(resource.ArchivePath);
                writer.Write(resource.EntryIndex);
                writer.Write(resource.EntryName);
                writer.Write(resource.ResourceIndex);
                writer.Write(resource.Data.Length);
                writer.Write(resource.Data);
            }

            writer.Write(_availabilityLists.Count);
            foreach (var list in _availabilityLists)
            {
                writer.Write(list.OwnerResourceIndex);
                writer.Write(list.CountOffset);
                writer.Write(list.EntryStride);
                writer.Write(list.ValueOffset);
                writer.Write(list.RecordIds.Length);
                foreach (ulong id in list.RecordIds)
                    writer.Write(id);
            }
        }

        File.Move(partial, path, overwrite: true);
    }

    public static ArmoryIndex? Load(string path, IReadOnlyList<string> archivePaths)
    {
        if (!File.Exists(path))
            return null;

        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != FileMagic || reader.ReadInt32() != FormatVersion)
                return null;

            var index = new ArmoryIndex { Fingerprint = reader.ReadString() };
            if (!string.Equals(index.Fingerprint, CreateFingerprint(archivePaths), StringComparison.Ordinal))
                return null;

            int languageCount = reader.ReadInt32();
            if (languageCount < 0 || languageCount > 100)
                return null;
            for (int languageIndex = 0; languageIndex < languageCount; languageIndex++)
            {
                string package = reader.ReadString();
                int stringCount = reader.ReadInt32();
                if (stringCount < 0 || stringCount > 2_000_000)
                    return null;
                var strings = new Dictionary<ulong, string>(stringCount);
                for (int stringIndex = 0; stringIndex < stringCount; stringIndex++)
                    strings.Add(reader.ReadUInt64(), reader.ReadString());
                index._strings.Add(package, strings);
            }

            int resourceCount = reader.ReadInt32();
            if (resourceCount < 0 || resourceCount > 2_000_000)
                return null;
            for (int resourceIndex = 0; resourceIndex < resourceCount; resourceIndex++)
            {
                ulong id = reader.ReadUInt64();
                string name = reader.ReadString();
                uint classHash = reader.ReadUInt32();
                string archivePath = reader.ReadString();
                int entryIndex = reader.ReadInt32();
                string entryName = reader.ReadString();
                int dataResourceIndex = reader.ReadInt32();
                int length = reader.ReadInt32();
                if (length < 0 || length > 64 << 20)
                    return null;
                index._databaseResources.Add(new IndexedResource(id, name, classHash, reader.ReadBytes(length), archivePath, entryIndex, entryName, dataResourceIndex));
            }

            int availabilityCount = reader.ReadInt32();
            if (availabilityCount < 0 || availabilityCount > 2_000_000)
                return null;
            for (int listIndex = 0; listIndex < availabilityCount; listIndex++)
            {
                int ownerResourceIndex = reader.ReadInt32();
                int countOffset = reader.ReadInt32();
                int entryStride = reader.ReadInt32();
                int valueOffset = reader.ReadInt32();
                int recordCount = reader.ReadInt32();
                if ((uint)ownerResourceIndex >= (uint)index._databaseResources.Count || countOffset < 0 || entryStride is < 8 or > 16 || valueOffset < 0 || valueOffset + sizeof(ulong) > entryStride || recordCount is < 1 or > 512)
                    return null;
                var ids = new ulong[recordCount];
                for (int recordIndex = 0; recordIndex < ids.Length; recordIndex++)
                    ids[recordIndex] = reader.ReadUInt64();
                index._availabilityLists.Add(new AvailabilityListCandidate(ownerResourceIndex, countOffset, entryStride, valueOffset, ids));
            }

            return index;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsCurrent(string path, IReadOnlyList<string> archivePaths) => Load(path, archivePaths) is not null;

    public bool MatchesArchives(IReadOnlyList<string> archivePaths) => string.Equals(Fingerprint, CreateFingerprint(archivePaths), StringComparison.Ordinal);

    public static string PartialPath(string path) => path + ".partial";

    static string CreateFingerprint(IEnumerable<string> archivePaths) => string.Join("|", archivePaths
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .Select(path =>
        {
            var file = new FileInfo(path);
            return $"{Path.GetFullPath(path)}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
        }));

    static List<AvailabilityListCandidate> ReadAvailabilityLists(ReadOnlySpan<byte> data, int ownerResourceIndex, CancellationToken cancellationToken)
    {
        const int maxEntries = 512;
        var results = new List<AvailabilityListCandidate>();
        for (int offset = 0; offset + sizeof(uint) <= data.Length; offset++)
        {
            if ((offset & 0xFFFF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(data[offset..]);
            if (count is < 1 or > maxEntries || offset + sizeof(uint) + (long)count * sizeof(ulong) > data.Length)
                continue;

            TryAddLayout(data, ownerResourceIndex, offset, (int)count, sizeof(ulong), 0, results);

            TryAddLayout(data, ownerResourceIndex, offset, (int)count, sizeof(byte) + sizeof(ulong), sizeof(byte), results, requireZeroMarker: true);
        }
        return results;
    }

    static void TryAddLayout(ReadOnlySpan<byte> data, int ownerResourceIndex, int countOffset, int count, int entryStride, int valueOffset, List<AvailabilityListCandidate> results, bool requireZeroMarker = false)
    {
        int entriesOffset = countOffset + sizeof(uint);
        if (entriesOffset + (long)count * entryStride > data.Length)
            return;

        var ids = new ulong[count];
        for (int item = 0; item < ids.Length; item++)
        {
            int entryOffset = entriesOffset + item * entryStride;
            if (requireZeroMarker && data[entryOffset] != 0)
                return;
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data[(entryOffset + valueOffset)..]);
            if (id == 0)
                return;
            ids[item] = id;
        }

        if (ids.Distinct().Count() == ids.Length)
            results.Add(new AvailabilityListCandidate(ownerResourceIndex, countOffset, entryStride, valueOffset, ids));
    }

    internal sealed record IndexedResource(ulong Id, string Name, uint ClassHash, byte[] Data, string ArchivePath, int EntryIndex, string EntryName, int ResourceIndex);
    internal sealed record AvailabilityListCandidate(int OwnerResourceIndex, int CountOffset, int EntryStride, int ValueOffset, ulong[] RecordIds);
}

public sealed record ArmoryDatabaseResourceChange(
    string ArchivePath,
    int EntryIndex,
    string EntryName,
    int ResourceIndex,
    string ResourceName,
    byte[] Data);

public sealed record ArmoryRegistryEvidence(
    ulong OwnerResourceId,
    string OwnerName,
    uint OwnerClassHash,
    string ArchivePath,
    int EntryIndex,
    string EntryName,
    int ResourceIndex,
    int CountOffset,
    int EntryStride,
    int ValueOffset,
    IReadOnlyList<ulong> Members);

public sealed record ArmoryObjectListEvidence(
    ulong OwnerResourceId,
    string OwnerName,
    uint OwnerClassHash,
    string ArchivePath,
    int EntryIndex,
    string EntryName,
    int ResourceIndex,
    int HeaderOffset,
    int CountOffset,
    int Count,
    uint EntryClassHash);

public sealed record ArmoryArchiveResourceAddition(
    string ArchivePath,
    int EntryIndex,
    string EntryName,
    ulong ResourceId,
    uint ClassHash,
    string ResourceName,
    byte[] Header,
    byte[] Data,
    ulong InsertAfterResourceId = 0);

public sealed record ArmoryArchiveEntryAddition(
    string ArchivePath,
    ulong EntryId,
    string EntryName,
    uint Extension,
    byte[] InfoTemplate,
    byte[] Data,
    byte[] PrefetchBlock);
