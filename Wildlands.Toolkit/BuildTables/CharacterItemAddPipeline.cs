using System.Buffers.Binary;
using System.IO;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

internal sealed record VestTemplate(
    int ConfigurationRowIndex,
    uint ConfigurationTag,
    uint GameplayTag,
    ulong ItemTableId,
    ulong TagTableId,
    ulong ModelTableId,
    IReadOnlyList<ulong> ModelSelectorIds,
    BuildTableOptionMetadata Metadata);

internal sealed record CharacterItemAddPlan(
    byte[] BuildTableData,
    IReadOnlyList<BuildTableResourceChange> LocalChanges,
    BuildTableOptionMetadata Metadata,
    IReadOnlyList<ArmoryDatabaseResourceChange> DatabaseChanges,
    IReadOnlyList<ArmoryArchiveResourceAddition> ResourceAdditions,
    IReadOnlyList<ArmoryArchiveEntryAddition> EntryAdditions,
    IReadOnlyList<Resource> PreviewResources,
    IReadOnlyDictionary<ulong, ulong> ModelSelectors);

internal sealed record VestBranchMirrorPlan(
    uint TemplateConfigurationTag,
    uint ConfigurationTag,
    uint TemplateGameplayTag,
    uint GameplayTag,
    ulong TemplateItemTableId,
    ulong TemplateTagTableId,
    ulong TemplateModelTableId,
    ulong ItemTableId,
    ulong TagTableId,
    ulong ModelTableId,
    IReadOnlyList<Resource> BranchResources,
    IReadOnlyDictionary<uint, uint> RowTagMap);

internal sealed record VestBranchMirrorResult(
    IReadOnlyList<ArmoryDatabaseResourceChange> Changes,
    IReadOnlyList<ArmoryArchiveResourceAddition> Additions);

internal sealed record CharacterBuilderTagMirrorResult(
    IReadOnlyList<ArmoryDatabaseResourceChange> Changes,
    int TargetResources);

/// <summary>
/// Creates a distinct character vest from the exact BuildTable branch and registries used by
/// an installed vest. No resource name or prefix participates in selecting a write target.
/// </summary>
internal static class CharacterItemAddPipeline
{
    internal const ulong MediumVestConfigurationTableId = 0x001E9ED02A7B;
    internal static readonly uint EntityBuilderClassHash = ResourceTypes.Crc32("EntityBuilder");
    const uint LootConfigurationClassHash = 0xB2C08E53;

    public static IReadOnlyList<VestTemplate> FindVestTemplates(
        BuildTableAsset configurationTable,
        IReadOnlyDictionary<ulong, Resource> localResources,
        Func<uint, BuildTableOptionMetadata?> metadataForTag)
    {
        if (configurationTable.Id != MediumVestConfigurationTableId)
            return [];

        var templates = new List<VestTemplate>();
        for (int rowIndex = 0; rowIndex < configurationTable.Rows.Count; rowIndex++)
        {
            try
            {
                templates.Add(ReadVestTemplate(configurationTable, rowIndex, localResources, metadataForTag));
            }
            catch (InvalidDataException)
            {
                // A row which does not match the proven vest branch is not offered as a write
                // template. BuildVest performs the same validation again before changing data.
            }
        }
        return templates;
    }

    public static CharacterItemAddPlan BuildVest(
        ArmoryIndex index,
        BuildTableAsset configurationTable,
        int templateRowIndex,
        AddAttachmentDraft draft,
        string languagePackage,
        IReadOnlyList<string> archivePaths,
        IReadOnlyDictionary<ulong, Resource> localResources,
        IReadOnlyDictionary<ulong, int> localResourceIndexes,
        string localArchivePath,
        int localEntryIndex,
        string localEntryName,
        IReadOnlyDictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]> preparedResourceData,
        IReadOnlyCollection<ulong> preparedIds,
        Func<uint, BuildTableOptionMetadata?> metadataForTag,
        IProgress<string>? progress = null,
        IReadOnlyList<string>? localizationPackages = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        if (index.DatabaseResources.Any(resource => string.Equals(resource.Name, draft.InternalName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A gameplay resource named {draft.InternalName} already exists.");

        progress?.Report("Validating the selected vest branch and character registries…");
        VestTemplate template = ReadVestTemplate(configurationTable, templateRowIndex, localResources, metadataForTag);
        ValidateRegistries(index, template.Metadata.RecordId);

        var used64 = index.DatabaseResources.Select(resource => resource.Id)
            .Concat(localResources.Keys)
            .Concat(preparedIds)
            .Where(id => id != 0)
            .ToHashSet();
        progress?.Report("Checking generated IDs against the installed archives…");
        ReserveInstalledResourceIds(archivePaths, used64);
        var usedStrings = index.LanguagePackages
            .SelectMany(package => index.StringsFor(package).Keys)
            .Where(id => id is > 0 and <= uint.MaxValue)
            .Select(id => (uint)id)
            .ToHashSet();
        usedStrings.UnionWith(preparedIds.Where(id => id is > 0 and <= uint.MaxValue).Select(id => (uint)id));
        var usedTags = localResources.Values
            .Where(resource => resource.ClassHash == BuildTable.ClassHash)
            .SelectMany(resource => BuildTable.Read(resource.Data).Rows.SelectMany(row => row.PossibleTags.Append(row.Tag)))
            .ToHashSet();

        uint configurationTag = AttachmentAddPipeline.Allocate32("character-config:" + draft.InternalName, usedTags);
        uint gameplayTag = AttachmentAddPipeline.Allocate32("character-gameplay:" + draft.InternalName, usedTags);
        uint stringId = AttachmentAddPipeline.Allocate32("localized-name:" + draft.InternalName, usedStrings);
        ulong recordId = AttachmentAddPipeline.Allocate64("gameplay-record:" + draft.InternalName, used64);

        var entryAdditions = new List<ArmoryArchiveEntryAddition>();
        var previewResources = new List<Resource>();
        var selectorMap = new Dictionary<ulong, ulong>();
        var sharedTextureIds = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        for (int branch = 0; draft.ImportModel && branch < template.ModelSelectorIds.Count; branch++)
        {
            progress?.Report(branch == 0
                ? "Building the male model, materials and textures…"
                : "Building the female model, materials and textures…");
            ulong selectorId = template.ModelSelectorIds[branch];
            AddAttachmentDraft modelDraft = draft with
            {
                InternalName = draft.InternalName + (branch == 0 ? "_Male" : "_Female")
            };
            AttachmentAddPipeline.ModelClonePlan model = AttachmentAddPipeline.CloneModelResources(
                modelDraft, selectorId, archivePaths, localArchivePath, localEntryIndex, used64, sharedTextureIds);
            selectorMap.Add(selectorId, model.SelectorId);
            entryAdditions.AddRange(model.EntryAdditions);
            previewResources.AddRange(model.Resources);
        }

        progress?.Report("Cloning the item, gameplay-tag and gender BuildTables…");
        ulong[] branchIds = [template.ItemTableId, template.TagTableId, template.ModelTableId];
        var tableIdMap = branchIds.ToDictionary(id => id,
            id => AttachmentAddPipeline.Allocate64($"character-buildtable:{draft.InternalName}:{id:X}", used64));
        var resourceAdditions = new List<ArmoryArchiveResourceAddition>();
        var branchResources = new List<Resource>();
        var rowTagMap = new Dictionary<uint, uint>();
        foreach (ulong oldId in branchIds)
        {
            Resource source = GetLocalBuildTable(localResources, oldId);
            BuildTableAsset clone = BuildTable.Read(source.Data);
            BuildTableReference identity = clone.References.Single(reference => reference.Kind == BuildTableReferenceKind.TableIdentity);
            if (identity.Value != oldId)
                throw new InvalidDataException($"BuildTable 0x{oldId:X12} does not carry its expected identity.");
            identity.Value = tableIdMap[oldId];
            foreach (BuildTableReference reference in clone.References.Where(reference => reference.Kind != BuildTableReferenceKind.TableIdentity))
            {
                if (tableIdMap.TryGetValue(reference.Value, out ulong newTableId))
                    reference.Value = newTableId;
                else if (selectorMap.TryGetValue(reference.Value, out ulong newSelectorId))
                    reference.Value = newSelectorId;
            }
            if (oldId == template.TagTableId)
                clone.SetRowTag(0, gameplayTag);
            else
                for (int row = 0; row < clone.Rows.Count; row++)
                {
                    uint rowTag = AttachmentAddPipeline.Allocate32(
                        $"character-row:{draft.InternalName}:{oldId:X}:{row}", usedTags);
                    rowTagMap.Add(clone.Rows[row].Tag, rowTag);
                    clone.SetRowTag(row, rowTag);
                }

            byte[] cloneData = clone.Write();
            BuildTableAsset checkedClone = BuildTable.Read(cloneData);
            if (checkedClone.Id != tableIdMap[oldId]
                || checkedClone.References.Any(reference => branchIds.Contains(reference.Value))
                || checkedClone.References.Any(reference => selectorMap.ContainsKey(reference.Value))
                || checkedClone.Rows.Select(row => row.Tag)
                    .Intersect(BuildTable.Read(source.Data).Rows.Select(row => row.Tag))
                    .Any())
                throw new InvalidDataException($"The cloned character BuildTable 0x{oldId:X12} failed validation.");

            Resource clonedResource = DataFile.CloneResource(source, tableIdMap[oldId],
                $"{draft.InternalName}_{source.Name}", cloneData);
            resourceAdditions.Add(new ArmoryArchiveResourceAddition(localArchivePath, localEntryIndex,
                localEntryName, clonedResource.Id, clonedResource.ClassHash, clonedResource.Name,
                clonedResource.Header, clonedResource.Data, oldId));
            branchResources.Add(clonedResource);
            previewResources.Add(clonedResource);
        }

        byte[] duplicated = configurationTable.DuplicateRow(templateRowIndex);
        BuildTableAsset editedConfiguration = BuildTable.Read(duplicated);
        int newRowIndex = templateRowIndex + 1;
        editedConfiguration.SetRowTag(newRowIndex, configurationTag);
        ReplaceComponentReference(editedConfiguration.Rows[newRowIndex], 8, template.ItemTableId, tableIdMap[template.ItemTableId]);
        ReplaceComponentReference(editedConfiguration.Rows[newRowIndex], 11, template.TagTableId, tableIdMap[template.TagTableId]);
        editedConfiguration.ReplacePossibleTag(newRowIndex, template.GameplayTag, gameplayTag);
        foreach ((uint templateRowTag, uint rowTag) in rowTagMap)
            if (editedConfiguration.Rows[newRowIndex].PossibleTags.Count(tag =>
                    tag == templateRowTag) == 1)
                editedConfiguration.ReplacePossibleTag(newRowIndex, templateRowTag, rowTag);
        if (!editedConfiguration.TryAppendTableBranch(tableIdMap,
                out byte[] configurationData, out bool foundConfigurationTableList)
            || !foundConfigurationTableList)
            throw new InvalidDataException("MediumVestCFG does not own the complete template BuildTable branch.");
        BuildTableAsset checkedConfiguration = BuildTable.Read(configurationData);
        if (checkedConfiguration.RowCount != configurationTable.RowCount + 1
            || checkedConfiguration.Rows[newRowIndex].Tag != configurationTag
            || checkedConfiguration.Rows[newRowIndex].PossibleTags.Count(tag => tag == gameplayTag) != 1
            || checkedConfiguration.Rows[newRowIndex].References.Any(reference => branchIds.Contains(reference.Value)))
            throw new InvalidDataException("The new vest configuration row failed validation.");

        IReadOnlyList<BuildTableResourceChange> localChanges = PropagateTags(configurationTable.Id,
            branchIds, template.ConfigurationTag, configurationTag, template.GameplayTag, gameplayTag,
            localResources, localResourceIndexes, tableIdMap);
        VestBranchMirrorResult mirroredContainers = MirrorContainerCopies(archivePaths,
            localArchivePath, localEntryIndex, new VestBranchMirrorPlan(
                template.ConfigurationTag, configurationTag, template.GameplayTag, gameplayTag,
                template.ItemTableId, template.TagTableId, template.ModelTableId,
                tableIdMap[template.ItemTableId], tableIdMap[template.TagTableId],
                tableIdMap[template.ModelTableId], branchResources, rowTagMap), preparedResourceData);
        resourceAdditions.AddRange(mirroredContainers.Additions);
        CharacterBuilderTagMirrorResult mirroredBuilders = MirrorCharacterBuilderTags(
            archivePaths, localArchivePath, localEntryIndex,
            template.ConfigurationTag, configurationTag,
            template.GameplayTag, gameplayTag, preparedResourceData, tableIdMap);

        var recordSource = AttachmentAddPipeline.LoadIndexedResource(index, template.Metadata.RecordId);
        byte[] recordData = AttachmentAddPipeline.CloneGameplayRecord(recordSource.Resource.Data,
            template.Metadata.RecordId, recordId, gameplayTag, stringId,
            draft.InternalName);
        Resource newRecord = DataFile.CloneResource(recordSource.Resource, recordId, draft.InternalName, recordData);
        resourceAdditions.Add(AttachmentAddPipeline.ToAddition(recordSource.Location, newRecord));

        ArmoryIndex.IndexedResource infoSource = FindStoreObjectInfo(index, template.Metadata.RecordId);
        ulong infoId = AttachmentAddPipeline.Allocate64("store-object-info:" + draft.InternalName, used64);
        var infoLoaded = AttachmentAddPipeline.LoadIndexedResource(index, infoSource.Id);
        StoreObjectInfo templateInfo = StoreObjectInfo.Parse(infoLoaded.Resource.Data, infoSource.Id);
        if (!infoLoaded.Resource.Name.EndsWith(templateInfo.Name, StringComparison.Ordinal))
            throw new InvalidDataException("The template StoreObjectInfo resource name does not end in its structured item name.");
        string infoResourceName = infoLoaded.Resource.Name[..^templateInfo.Name.Length] + draft.InternalName;
        byte[] infoData = index.CreateStoreObjectInfoClone(infoSource.Id, infoId,
            template.Metadata.RecordId, recordId, draft.InternalName);
        resourceAdditions.Add(AttachmentAddPipeline.ToAddition(infoLoaded.Location,
            DataFile.CloneResource(infoLoaded.Resource, infoId, infoResourceName, infoData)));

        ArmoryIndex.IndexedResource lootSource = FindLootConfiguration(index, template.Metadata.RecordId);
        ulong lootId = AttachmentAddPipeline.Allocate64("loot-configuration:" + draft.InternalName, used64);
        var lootLoaded = AttachmentAddPipeline.LoadIndexedResource(index, lootSource.Id);
        byte[] lootData = CloneLootConfiguration(lootLoaded.Resource.Data, lootSource.Id,
            lootId, template.Metadata.RecordId, recordId);
        Resource lootResource = DataFile.CloneResource(lootLoaded.Resource, lootId,
            draft.InternalName + "_LootConfig", lootData);
        resourceAdditions.Add(AttachmentAddPipeline.ToAddition(lootLoaded.Location, lootResource));

        progress?.Report("Registering the vest in CharacterSmith, VESTS, StoreDB and unlockables…");
        var databaseChanges = new List<ArmoryDatabaseResourceChange>();
        databaseChanges.AddRange(mirroredContainers.Changes);
        databaseChanges.AddRange(mirroredBuilders.Changes);
        databaseChanges.AddRange(index.CreateVestRegistryInsertions(
            template.Metadata.RecordId, recordId, lootSource.Id, lootId,
            template.TagTableId, tableIdMap[template.TagTableId]));
        databaseChanges.Add(index.CreateStoreRegistryInsertion(infoSource.Id, infoId));
        databaseChanges.AddRange(AttachmentAddPipeline.BuildTagDictionaryChanges(index,
            [(template.ConfigurationTag, configurationTag),
                (template.GameplayTag, gameplayTag),
                .. rowTagMap.Select(pair => (pair.Key, pair.Value))]));

        progress?.Report("Adding the in-game display label and validating the completed plan…");
        IReadOnlyList<string> requestedPackages = localizationPackages
            ?? ["English(US)", languagePackage];
        var packages = index.LanguagePackages.Where(package => requestedPackages.Contains(
                package, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var localizationTargets = AttachmentAddPipeline.FindLocalizationTargets(packages,
            archivePaths, preparedResourceData, template.Metadata.NameStringId);
        if (localizationTargets.Count != packages.Count)
            throw new InvalidOperationException("The template vest label was not found unambiguously in every required language package.");
        foreach (AttachmentAddPipeline.LocalizationTarget target in localizationTargets)
        {
            byte[] localizationData = LocalizationPackage.AddOrReplaceString(target.Resource.Data, stringId, draft.DisplayName);
            databaseChanges.Add(new ArmoryDatabaseResourceChange(target.Location.ArchivePath,
                target.Location.EntryIndex, target.Location.EntryName, target.ResourceIndex,
                target.Resource.Name, localizationData));
        }
        EnsureIndependentChanges(databaseChanges);

        var metadata = new BuildTableOptionMetadata(gameplayTag, stringId, draft.DisplayName, null,
            template.Metadata.DescriptionStringId, template.Metadata.Description, recordId,
            draft.InternalName, template.Metadata.RecordClassHash);
        return new CharacterItemAddPlan(configurationData, localChanges, metadata, databaseChanges,
            resourceAdditions, entryAdditions, previewResources, selectorMap);
    }

    internal static ArmoryIndex.IndexedResource FindLootConfiguration(ArmoryIndex index, ulong templateRecordId)
    {
        var matches = index.DatabaseResources
            .Where(resource => resource.ClassHash == LootConfigurationClassHash)
            .GroupBy(resource => resource.Id)
            .Select(group => group.Last())
            .Where(resource => resource.Data.Length >= 12
                && BinaryPrimitives.ReadUInt64LittleEndian(resource.Data) == resource.Id
                && BinaryPrimitives.ReadUInt32LittleEndian(resource.Data.AsSpan(8)) == LootConfigurationClassHash
                && CountUInt64(resource.Data, templateRecordId) == 1)
            .ToList();
        if (matches.Count != 1)
            throw new InvalidDataException($"The template vest is referenced by {matches.Count} effective loot configurations instead of exactly one.");

        ArmoryIndex.IndexedResource match = matches[0];
        if (GunsmithAvailability.FindDatabaseContainerRegistries(index, match.Id).Count != 1)
            throw new InvalidDataException("The template vest loot configuration is not present in exactly one confirmed database container.");
        return match;
    }

    internal static byte[] CloneLootConfiguration(byte[] source, ulong oldId, ulong newId,
        ulong oldRecordId, ulong newRecordId)
    {
        if (source.Length < 12
            || BinaryPrimitives.ReadUInt64LittleEndian(source) != oldId
            || BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(8)) != LootConfigurationClassHash)
            throw new InvalidDataException("The vest loot configuration header is invalid.");
        if (CountUInt64(source, oldRecordId) != 1)
            throw new InvalidDataException("The vest loot configuration does not reference its gameplay record exactly once.");

        byte[] result = (byte[])source.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(result, newId);
        ReplaceUInt64(result, oldRecordId, newRecordId);
        if (BinaryPrimitives.ReadUInt64LittleEndian(result) != newId
            || CountUInt64(result, oldRecordId) != 0
            || CountUInt64(result, newRecordId) != 1)
            throw new InvalidDataException("The cloned vest loot configuration failed validation.");
        return result;
    }

    static int CountUInt64(ReadOnlySpan<byte> data, ulong value)
    {
        Span<byte> encoded = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(encoded, value);
        int count = 0;
        int start = 0;
        while (start <= data.Length - encoded.Length)
        {
            int relative = data[start..].IndexOf(encoded);
            if (relative < 0)
                break;
            count++;
            start += relative + encoded.Length;
        }
        return count;
    }

    static void ReplaceUInt64(Span<byte> data, ulong oldValue, ulong newValue)
    {
        Span<byte> encoded = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(encoded, oldValue);
        int offset = data.IndexOf(encoded);
        if (offset < 0)
            throw new InvalidDataException($"Value 0x{oldValue:X12} was not found in the cloned resource.");
        BinaryPrimitives.WriteUInt64LittleEndian(data[offset..], newValue);
    }

    static VestTemplate ReadVestTemplate(BuildTableAsset configurationTable, int rowIndex,
        IReadOnlyDictionary<ulong, Resource> localResources,
        Func<uint, BuildTableOptionMetadata?> metadataForTag)
    {
        if (configurationTable.Id != MediumVestConfigurationTableId)
            throw new InvalidDataException("This is not the verified medium-vest configuration table.");
        if ((uint)rowIndex >= (uint)configurationTable.Rows.Count)
            throw new InvalidDataException("The selected vest row does not exist.");

        BuildTableRow configurationRow = configurationTable.Rows[rowIndex];
        ulong itemTableId = RequiredComponentReference(configurationRow, 8);
        ulong tagTableId = RequiredComponentReference(configurationRow, 11);
        BuildTableAsset itemTable = BuildTable.Read(GetLocalBuildTable(localResources, itemTableId).Data);
        BuildTableAsset tagTable = BuildTable.Read(GetLocalBuildTable(localResources, tagTableId).Data);
        if (itemTable.Rows.Count != 1 || tagTable.Rows.Count != 1)
            throw new InvalidDataException("The vest branch does not contain one item row and one gameplay-tag row.");

        ulong modelTableId = RequiredComponentReference(itemTable.Rows[0], 7);
        BuildTableAsset modelTable = BuildTable.Read(GetLocalBuildTable(localResources, modelTableId).Data);
        if (modelTable.Rows.Count != 2)
            throw new InvalidDataException("The vest model table does not contain exactly two gender branches.");
        var selectorIds = modelTable.Rows.Select(row => RequiredComponentReference(row, 1,
                BuildTableReferenceKind.Handle)).ToList();
        if (selectorIds.Distinct().Count() != 2)
            throw new InvalidDataException("The two vest gender branches do not use distinct model selectors.");

        uint gameplayTag = tagTable.Rows[0].Tag;
        if (configurationRow.PossibleTags.Count(tag => tag == gameplayTag) != 1)
            throw new InvalidDataException("The vest configuration row does not link exactly once to its gameplay BuildTag.");
        BuildTableOptionMetadata metadata = metadataForTag(gameplayTag)
            ?? throw new InvalidDataException("The vest gameplay BuildTag has no confirmed installed gameplay record.");
        return new VestTemplate(rowIndex, configurationRow.Tag, gameplayTag, itemTableId,
            tagTableId, modelTableId, selectorIds, metadata);
    }

    static ulong RequiredComponentReference(BuildTableRow row, int componentIndex,
        BuildTableReferenceKind kind = BuildTableReferenceKind.FileReference)
    {
        var matches = row.References.Where(reference => reference.ComponentIndex == componentIndex
            && reference.Kind == kind && reference.Value != 0).ToList();
        if (matches.Count != 1)
            throw new InvalidDataException($"Expected exactly one {kind} in component {componentIndex}, found {matches.Count}.");
        return matches[0].Value;
    }

    static void ReplaceComponentReference(BuildTableRow row, int componentIndex, ulong expected, ulong replacement)
    {
        BuildTableReference reference = row.References.Single(candidate => candidate.ComponentIndex == componentIndex
            && candidate.Kind == BuildTableReferenceKind.FileReference && candidate.Value == expected);
        reference.Value = replacement;
    }

    static Resource GetLocalBuildTable(IReadOnlyDictionary<ulong, Resource> resources, ulong id)
    {
        if (!resources.TryGetValue(id, out Resource? resource) || resource.ClassHash != BuildTable.ClassHash)
            throw new InvalidDataException($"Required local BuildTable 0x{id:X12} is missing.");
        return resource;
    }

    static void ValidateRegistries(ArmoryIndex index, ulong recordId)
    {
        if (CharacterSmithRegistry.Find(index, recordId).Count != 1
            || GunsmithAvailability.FindVestRegistries(index, recordId).Count != 1
            || GunsmithAvailability.FindDatabaseContainerRegistries(index, recordId).Count != 1
            || GunsmithAvailability.FindUnlockRegistries(index, recordId).Count != 1)
            throw new InvalidOperationException("The template vest does not resolve to one confirmed CharacterSmith, VESTS, database-container and unlockable registration each.");
        ArmoryIndex.IndexedResource info = FindStoreObjectInfo(index, recordId);
        if (GunsmithAvailability.FindStoreRegistries(index, info.Id).Count != 1)
            throw new InvalidOperationException("The template vest does not resolve to one confirmed StoreDB registration.");
    }

    internal static ArmoryIndex.IndexedResource FindStoreObjectInfo(ArmoryIndex index, ulong recordId)
    {
        var matches = index.DatabaseResources
            .Where(resource => resource.ClassHash == StoreObjectInfo.ClassHash
                && StoreObjectInfo.TryParse(resource.Data, resource.Id, out StoreObjectInfo info)
                && info.RecordId == recordId)
            .GroupBy(resource => resource.Id)
            .Select(group => group.Last())
            .ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException($"The template vest resolves to {matches.Count} StoreObjectInfo resources instead of one.");
    }

    internal static VestBranchMirrorResult MirrorContainerCopies(
        IReadOnlyList<string> archivePaths,
        string sourceArchivePath,
        int sourceEntryIndex,
        VestBranchMirrorPlan branch,
        IReadOnlyDictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]> preparedResourceData)
    {
        ulong containerId;
        using (var opened = ForgeArchive.Open(sourceArchivePath))
            containerId = opened.Entries.FirstOrDefault(entry => entry.Index == sourceEntryIndex)?.Id
                ?? throw new InvalidOperationException("The source character container moved inside its Forge archive.");

        var changes = new List<ArmoryDatabaseResourceChange>();
        var additions = new List<ArmoryArchiveResourceAddition>();
        string sourceFullPath = Path.GetFullPath(sourceArchivePath);
        foreach (string path in archivePaths
                     .DistinctBy(Path.GetFullPath, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(Path.GetFullPath(path), sourceFullPath,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            using var archive = ForgeArchive.Open(path);
            ForgeEntry? entry = archive.Entries.FirstOrDefault(candidate => candidate.Id == containerId);
            if (entry is null)
                continue;

            using var stream = new MemoryStream(archive.ReadEntry(entry));
            DataFile file = DataFile.Read(stream);
            AttachmentAddPipeline.ApplyPreparedResourceData(file, path, entry.Index,
                preparedResourceData);
            int configurationIndex = file.Resources.FindIndex(resource =>
                resource.Id == MediumVestConfigurationTableId
                && resource.ClassHash == BuildTable.ClassHash);
            if (configurationIndex < 0)
                throw new InvalidOperationException($"The copy of {entry.Name} in {Path.GetFileName(path)} has no verified MediumVestCFG resource.");

            BuildTableAsset configuration = BuildTable.Read(
                file.Resources[configurationIndex].Data);
            var installedRows = configuration.Rows.Where(row =>
                    row.PossibleTags.Count(tag => tag == branch.GameplayTag) == 1)
                .ToList();
            if (installedRows.Count > 1)
                throw new InvalidDataException($"The copy of MediumVestCFG in {Path.GetFileName(path)} contains the addon BuildTag more than once.");

            if (installedRows.Count == 0)
            {
                var templates = configuration.Rows
                    .Select((row, index) => (row, index))
                    .Where(item => item.row.Tag == branch.TemplateConfigurationTag
                        && item.row.PossibleTags.Count(tag => tag == branch.TemplateGameplayTag) == 1
                        && HasComponentReference(item.row, 8, branch.TemplateItemTableId)
                        && HasComponentReference(item.row, 11, branch.TemplateTagTableId))
                    .ToList();
                if (templates.Count != 1)
                    throw new InvalidDataException($"The copy of MediumVestCFG in {Path.GetFileName(path)} contains {templates.Count} exact template rows instead of one.");

                byte[] duplicated = configuration.DuplicateRow(templates[0].index);
                BuildTableAsset edited = BuildTable.Read(duplicated);
                int newRowIndex = templates[0].index + 1;
                edited.SetRowTag(newRowIndex, branch.ConfigurationTag);
                ReplaceComponentReference(edited.Rows[newRowIndex], 8,
                    branch.TemplateItemTableId, branch.ItemTableId);
                ReplaceComponentReference(edited.Rows[newRowIndex], 11,
                    branch.TemplateTagTableId, branch.TagTableId);
                edited.ReplacePossibleTag(newRowIndex, branch.TemplateGameplayTag,
                    branch.GameplayTag);
                foreach ((uint templateRowTag, uint rowTag) in branch.RowTagMap)
                    if (edited.Rows[newRowIndex].PossibleTags.Count(tag =>
                            tag == templateRowTag) == 1)
                        edited.ReplacePossibleTag(newRowIndex, templateRowTag, rowTag);
                var tableMap = new Dictionary<ulong, ulong>
                {
                    [branch.TemplateItemTableId] = branch.ItemTableId,
                    [branch.TemplateTagTableId] = branch.TagTableId,
                    [branch.TemplateModelTableId] = branch.ModelTableId,
                };
                if (!edited.TryAppendTableBranch(tableMap, out byte[] data,
                        out bool foundConfigurationTableList)
                    || !foundConfigurationTableList)
                    throw new InvalidDataException($"MediumVestCFG in {Path.GetFileName(path)} does not own the complete template BuildTable branch.");
                BuildTableAsset checkedTable = BuildTable.Read(data);
                if (checkedTable.Rows[newRowIndex].Tag != branch.ConfigurationTag
                    || checkedTable.Rows[newRowIndex].PossibleTags.Count(tag =>
                        tag == branch.GameplayTag) != 1
                    || !HasComponentReference(checkedTable.Rows[newRowIndex], 8,
                        branch.ItemTableId)
                    || !HasComponentReference(checkedTable.Rows[newRowIndex], 11,
                        branch.TagTableId))
                    throw new InvalidDataException("The mirrored vest configuration row failed validation.");
                changes.Add(new ArmoryDatabaseResourceChange(path, entry.Index, entry.Name,
                    configurationIndex, file.Resources[configurationIndex].Name, data));
            }

            var resources = file.Resources.ToDictionary(resource => resource.Id);
            var resourceIndexes = file.Resources.Select((resource, index) =>
                    (resource.Id, index)).ToDictionary(item => item.Id, item => item.index);
            ulong[] templateBranches = [branch.TemplateItemTableId,
                branch.TemplateTagTableId, branch.TemplateModelTableId];
            foreach (BuildTableResourceChange change in PropagateTags(
                         MediumVestConfigurationTableId, templateBranches,
                         branch.TemplateConfigurationTag, branch.ConfigurationTag,
                         branch.TemplateGameplayTag, branch.GameplayTag,
                         resources, resourceIndexes,
                         new Dictionary<ulong, ulong>
                         {
                             [branch.TemplateItemTableId] = branch.ItemTableId,
                             [branch.TemplateTagTableId] = branch.TagTableId,
                             [branch.TemplateModelTableId] = branch.ModelTableId,
                         }))
                changes.Add(new ArmoryDatabaseResourceChange(path, entry.Index, entry.Name,
                    change.ResourceIndex, change.Name, change.Data));

            foreach (Resource resource in branch.BranchResources)
            {
                Resource? existing = file.Resources.FirstOrDefault(candidate =>
                    candidate.Id == resource.Id);
                if (existing is not null)
                {
                    if (existing.ClassHash != resource.ClassHash
                        || !existing.Header.AsSpan().SequenceEqual(resource.Header)
                        || !existing.Data.AsSpan().SequenceEqual(resource.Data))
                        throw new InvalidDataException($"Resource 0x{resource.Id:X12} already exists with different data in {Path.GetFileName(path)}.");
                    continue;
                }
                additions.Add(new ArmoryArchiveResourceAddition(path, entry.Index,
                    entry.Name, resource.Id, resource.ClassHash, resource.Name,
                    resource.Header, resource.Data,
                    resource.Id == branch.ItemTableId
                        ? branch.TemplateItemTableId
                        : resource.Id == branch.TagTableId
                            ? branch.TemplateTagTableId
                            : resource.Id == branch.ModelTableId
                                ? branch.TemplateModelTableId
                                : 0));
            }
        }
        return new VestBranchMirrorResult(changes, additions);
    }

    /// <summary>
    /// Mirrors a vest's two BuildTags into sibling character EntityBuilders. Targets are
    /// discovered exclusively by parsing structured BuildTags lists which contain the exact
    /// template tags; container or resource names are never used to select a write target.
    /// The source character container is excluded because PropagateTags handles it already.
    /// </summary>
    internal static CharacterBuilderTagMirrorResult MirrorCharacterBuilderTags(
        IReadOnlyList<string> archivePaths,
        string sourceArchivePath,
        int sourceEntryIndex,
        uint oldConfigurationTag,
        uint newConfigurationTag,
        uint oldGameplayTag,
        uint newGameplayTag,
        IReadOnlyDictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]> preparedResourceData,
        IReadOnlyDictionary<ulong, ulong>? tableMap = null)
    {
        ulong sourceContainerId;
        var targets = new HashSet<(ulong EntryId, ulong ResourceId)>();
        using (var sourceArchive = ForgeArchive.Open(sourceArchivePath))
        {
            sourceContainerId = sourceArchive.Entries
                .Single(entry => entry.Index == sourceEntryIndex).Id;
            foreach (ForgeEntry entry in sourceArchive.Entries.Where(entry =>
                         entry.FileExtension == ".data" && entry.Id != sourceContainerId))
            {
                using var stream = new MemoryStream(sourceArchive.ReadEntry(entry));
                DataFile file = DataFile.Read(stream);
                AttachmentAddPipeline.ApplyPreparedResourceData(file, sourceArchivePath,
                    entry.Index, preparedResourceData);
                foreach (Resource resource in file.Resources.Where(resource =>
                             resource.ClassHash == BuildTable.ClassHash
                             || resource.ClassHash == EntityBuilderClassHash))
                {
                    if (CanMirrorTagPair(resource, oldConfigurationTag,
                            newConfigurationTag, oldGameplayTag, newGameplayTag, tableMap))
                        targets.Add((entry.Id, resource.Id));
                }
            }
        }

        if (targets.Count == 0)
            throw new InvalidOperationException("No sibling character builder contains the exact template vest BuildTags.");

        var changes = new List<ArmoryDatabaseResourceChange>();
        int registeredBuilders = 0;
        foreach (string path in archivePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var archive = ForgeArchive.Open(path);
            foreach (IGrouping<ulong, (ulong EntryId, ulong ResourceId)> group in
                     targets.GroupBy(target => target.EntryId))
            {
                ForgeEntry? entry = archive.Entries.FirstOrDefault(candidate =>
                    candidate.Id == group.Key);
                if (entry is null)
                    continue;

                using var stream = new MemoryStream(archive.ReadEntry(entry));
                DataFile file = DataFile.Read(stream);
                AttachmentAddPipeline.ApplyPreparedResourceData(file, path, entry.Index,
                    preparedResourceData);
                foreach ((ulong _, ulong resourceId) in group)
                {
                    int resourceIndex = file.Resources.FindIndex(resource =>
                        resource.Id == resourceId);
                    if (resourceIndex < 0)
                        throw new InvalidDataException($"The matching copy of {entry.Name} in {Path.GetFileName(path)} is missing character-builder resource 0x{resourceId:X12}.");
                    Resource source = file.Resources[resourceIndex];
                    if (!TryMirrorTagPair(source, oldConfigurationTag,
                            newConfigurationTag, oldGameplayTag, newGameplayTag,
                            tableMap, out byte[] updated))
                        continue;
                    if (tableMap is { Count: > 0 }
                        && source.ClassHash == EntityBuilderClassHash
                        && tableMap.Values.All(id => CountUInt64(updated, id) == 1))
                        registeredBuilders++;
                    changes.Add(new ArmoryDatabaseResourceChange(path, entry.Index,
                        entry.Name, resourceIndex, source.Name, updated));
                }
            }
        }

        if (changes.Count == 0)
            throw new InvalidOperationException("The sibling character builders already contain the addon BuildTags or no installed copy can be updated.");
        if (tableMap is { Count: > 0 } && registeredBuilders == 0)
            throw new InvalidOperationException("The cloned vest BuildTables could not be registered in the sibling character EntityBuilders.");
        return new CharacterBuilderTagMirrorResult(changes, targets.Count);
    }

    static bool CanMirrorTagPair(Resource source, uint oldConfigurationTag,
        uint newConfigurationTag, uint oldGameplayTag, uint newGameplayTag,
        IReadOnlyDictionary<ulong, ulong>? tableMap)
        => TryMirrorTagPair(source, oldConfigurationTag, newConfigurationTag,
            oldGameplayTag, newGameplayTag, tableMap, out _);

    static bool TryMirrorTagPair(Resource source, uint oldConfigurationTag,
        uint newConfigurationTag, uint oldGameplayTag, uint newGameplayTag,
        IReadOnlyDictionary<ulong, ulong>? tableMap, out byte[] updated)
    {
        byte[] data = source.Data;
        bool changed = false;
        Resource current = CopyResource(source, data);
        if (AttachmentAddPipeline.TryAddBuildTagToAll(current, oldConfigurationTag,
                newConfigurationTag, out byte[] withConfiguration))
        {
            data = withConfiguration;
            changed = true;
            current = CopyResource(source, data);
        }
        if (AttachmentAddPipeline.TryAddBuildTagToAll(current, oldGameplayTag,
                newGameplayTag, out byte[] withGameplay))
        {
            data = withGameplay;
            changed = true;
            current = CopyResource(source, data);
        }
        if (tableMap is { Count: > 0 }
            && source.ClassHash == EntityBuilderClassHash
            && TryRegisterEntityBuilderReferences(current.Data, tableMap,
                out byte[] withReferences, out _))
        {
            data = withReferences;
            changed = true;
        }
        else if (tableMap is { Count: > 0 }
            && source.ClassHash == BuildTable.ClassHash
            && BuildTable.Read(current.Data).TryAppendTableBranch(tableMap,
                out byte[] withBranch, out _))
        {
            data = withBranch;
            changed = true;
        }
        updated = data;
        return changed;
    }

    internal static IReadOnlyList<BuildTableResourceChange> PropagateTags(ulong configurationId,
        IReadOnlyCollection<ulong> branchIds, uint oldConfigurationTag, uint newConfigurationTag,
        uint oldGameplayTag, uint newGameplayTag, IReadOnlyDictionary<ulong, Resource> resources,
        IReadOnlyDictionary<ulong, int> resourceIndexes,
        IReadOnlyDictionary<ulong, ulong>? entityBuilderReferenceMap = null)
    {
        var changes = new List<BuildTableResourceChange>();
        int configurationParents = 0;
        int gameplayParents = 0;
        int registeredTableLists = 0;
        int registeredBuilderLists = 0;
        foreach (Resource source in resources.Values)
        {
            if (source.Id == configurationId || branchIds.Contains(source.Id)
                || source.ClassHash != BuildTable.ClassHash && source.ClassHash != EntityBuilderClassHash)
                continue;

            byte[] data = source.Data;
            bool changed = false;
            Resource current = CopyResource(source, data);
            if (AttachmentAddPipeline.TryAddBuildTagToAll(current, oldConfigurationTag, newConfigurationTag, out byte[] withConfiguration))
            {
                data = withConfiguration;
                configurationParents++;
                changed = true;
                current = CopyResource(source, data);
            }
            if (AttachmentAddPipeline.TryAddBuildTagToAll(current, oldGameplayTag, newGameplayTag, out byte[] withGameplay))
            {
                data = withGameplay;
                gameplayParents++;
                changed = true;
            }
            if (source.ClassHash == EntityBuilderClassHash
                && entityBuilderReferenceMap is { Count: > 0 })
            {
                current = CopyResource(source, data);
                if (TryRegisterEntityBuilderReferences(current.Data,
                        entityBuilderReferenceMap, out byte[] withReferences,
                        out bool foundReferenceList))
                {
                    data = withReferences;
                    changed = true;
                }
                if (foundReferenceList)
                    registeredBuilderLists++;
            }
            else if (source.ClassHash == BuildTable.ClassHash
                && entityBuilderReferenceMap is { Count: > 0 })
            {
                current = CopyResource(source, data);
                BuildTableAsset table = BuildTable.Read(current.Data);
                if (table.TryAppendTableBranch(entityBuilderReferenceMap,
                        out byte[] withReferences, out bool foundReferenceList))
                {
                    data = withReferences;
                    changed = true;
                }
                if (foundReferenceList)
                    registeredTableLists++;
            }
            if (!changed)
                continue;
            if (!resourceIndexes.TryGetValue(source.Id, out int resourceIndex))
                throw new InvalidOperationException($"{source.Name} has no stable resource index in the character container.");
            changes.Add(new BuildTableResourceChange(resourceIndex, source.Name, data));
        }
        if (configurationParents == 0 || gameplayParents == 0)
            throw new InvalidOperationException("The vest tags could not be propagated through the confirmed character compatibility tables.");
        if (entityBuilderReferenceMap is { Count: > 0 }
            && registeredBuilderLists == 0)
            throw new InvalidOperationException("The cloned vest BuildTables could not be registered in the character EntityBuilder.");
        if (entityBuilderReferenceMap is { Count: > 0 }
            && registeredTableLists == 0)
            throw new InvalidOperationException("The cloned vest BuildTables could not be registered in the character BuildTable graph.");
        return changes;
    }

    /// <summary>
    /// Extends the structured EntityBuilder handle list which already owns all of
    /// the template branch tables. A FileReference in MediumVestCFG is not sufficient on
    /// its own: the game only resolves BuildTables which are also present in this owner
    /// list. Weapon additions avoid this requirement because they extend an existing,
    /// already-owned leaf table.
    /// </summary>
    internal static bool TryRegisterEntityBuilderReferences(
        byte[] source, IReadOnlyDictionary<ulong, ulong> referenceMap,
        out byte[] updated, out bool found)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(referenceMap);
        found = false;
        updated = source;
        if (referenceMap.Count == 0)
            return false;
        if (referenceMap.Any(pair => pair.Key == 0 || pair.Value == 0
                || pair.Key == pair.Value)
            || referenceMap.Keys.Distinct().Count() != referenceMap.Count
            || referenceMap.Values.Distinct().Count() != referenceMap.Count)
            throw new ArgumentException("EntityBuilder reference replacements must be distinct and non-zero.", nameof(referenceMap));

        var candidates = new List<(int CountOffset, int EntriesOffset, int Count,
            IReadOnlyList<ulong> References)>();
        for (int countOffset = 0; countOffset <= source.Length - 5; countOffset++)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(countOffset));
            if (count is < 1 or > 32_768)
                continue;
            int entriesOffset = countOffset + sizeof(int);
            long end = entriesOffset + (long)count * 9;
            if (end > source.Length)
                continue;

            var references = new ulong[count];
            bool valid = true;
            for (int index = 0; index < count; index++)
            {
                int entryOffset = entriesOffset + index * 9;
                if (source[entryOffset] != 0)
                {
                    valid = false;
                    break;
                }
                ulong value = BinaryPrimitives.ReadUInt64LittleEndian(
                    source.AsSpan(entryOffset + 1));
                if (value == 0)
                {
                    valid = false;
                    break;
                }
                references[index] = value;
            }
            if (!valid || referenceMap.Keys.Any(template =>
                    references.Count(value => value == template) != 1))
                continue;
            candidates.Add((countOffset, entriesOffset, count, references));
        }

        if (candidates.Count == 0)
            return false;
        int maximumCount = candidates.Max(candidate => candidate.Count);
        candidates = candidates.Where(candidate =>
            candidate.Count == maximumCount).ToList();
        if (candidates.Count != 1)
            throw new InvalidDataException($"The EntityBuilder contains {candidates.Count} maximal reference lists owning the complete vest template branch instead of one.");

        found = true;
        var candidate = candidates[0];
        var existingNewReferences = referenceMap.Values
            .Where(value => candidate.References.Contains(value))
            .ToList();
        if (existingNewReferences.Count != 0)
        {
            if (existingNewReferences.Count != referenceMap.Count)
                throw new InvalidDataException("The EntityBuilder contains only part of the cloned vest branch.");
            return false;
        }

        var expanded = new List<ulong>(candidate.Count + referenceMap.Count);
        expanded.AddRange(candidate.References);
        expanded.AddRange(referenceMap
            .OrderBy(pair => candidate.References.ToList().IndexOf(pair.Key))
            .Select(pair => pair.Value));

        int oldEnd = candidate.EntriesOffset + candidate.Count * 9;
        int newLength = checked(source.Length + referenceMap.Count * 9);
        updated = new byte[newLength];
        source.AsSpan(0, candidate.EntriesOffset).CopyTo(updated);
        BinaryPrimitives.WriteInt32LittleEndian(
            updated.AsSpan(candidate.CountOffset), expanded.Count);
        for (int index = 0; index < expanded.Count; index++)
        {
            int entryOffset = candidate.EntriesOffset + index * 9;
            updated[entryOffset] = 0;
            BinaryPrimitives.WriteUInt64LittleEndian(
                updated.AsSpan(entryOffset + 1), expanded[index]);
        }
        source.AsSpan(oldEnd).CopyTo(updated.AsSpan(
            candidate.EntriesOffset + expanded.Count * 9));

        if (!TryReadEntityBuilderReferenceList(updated, candidate.CountOffset,
                referenceMap, out IReadOnlyList<ulong> checkedReferences))
            throw new InvalidDataException("The cloned vest BuildTable references did not survive EntityBuilder validation.");
        List<ulong> checkedList = checkedReferences.ToList();
        if (checkedList.Count != candidate.Count + referenceMap.Count
            || referenceMap.Any(pair =>
                checkedList.Count(value => value == pair.Value) != 1)
            || !checkedList.Skip(candidate.Count).SequenceEqual(referenceMap
                .OrderBy(pair => candidate.References.ToList().IndexOf(pair.Key))
                .Select(pair => pair.Value)))
            throw new InvalidDataException("The cloned vest BuildTable references did not survive EntityBuilder validation.");
        return true;
    }

    static bool TryReadEntityBuilderReferenceList(byte[] data, int countOffset,
        IReadOnlyDictionary<ulong, ulong> referenceMap,
        out IReadOnlyList<ulong> references)
    {
        references = [];
        if (countOffset < 0 || countOffset + sizeof(int) > data.Length)
            return false;
        int count = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(countOffset));
        int entriesOffset = countOffset + sizeof(int);
        if (count is < 1 or > 32_768
            || entriesOffset + (long)count * 9 > data.Length)
            return false;
        var values = new ulong[count];
        for (int index = 0; index < count; index++)
        {
            int entryOffset = entriesOffset + index * 9;
            if (data[entryOffset] != 0)
                return false;
            values[index] = BinaryPrimitives.ReadUInt64LittleEndian(
                data.AsSpan(entryOffset + 1));
        }
        if (referenceMap.Keys.Any(template =>
                values.Count(value => value == template) != 1))
            return false;
        references = values;
        return true;
    }

    static bool HasComponentReference(BuildTableRow row, int componentIndex,
        ulong value) => row.References.Count(reference =>
            reference.Kind == BuildTableReferenceKind.FileReference
            && reference.ComponentIndex == componentIndex
            && reference.Value == value) == 1;

    static Resource CopyResource(Resource source, byte[] data) => new()
    {
        Id = source.Id,
        ClassHash = source.ClassHash,
        Name = source.Name,
        Header = source.Header,
        Data = data,
    };

    static void EnsureIndependentChanges(IReadOnlyList<ArmoryDatabaseResourceChange> changes)
    {
        var duplicate = changes.GroupBy(change => (Path.GetFullPath(change.ArchivePath).ToUpperInvariant(),
                change.EntryIndex, change.ResourceIndex))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"Two vest registrations would independently edit {duplicate.First().ResourceName}; the operation was not queued.");
    }

    internal static void ReserveInstalledResourceIds(IReadOnlyList<string> archivePaths, HashSet<ulong> ids)
    {
        foreach (string path in archivePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var archive = ForgeArchive.Open(path);
            ids.UnionWith(archive.Entries.Select(entry => entry.Id));
            foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"))
                ids.UnionWith(archive.ReadResourceIndex(entry).Select(resource => resource.Id));
        }
    }
}
