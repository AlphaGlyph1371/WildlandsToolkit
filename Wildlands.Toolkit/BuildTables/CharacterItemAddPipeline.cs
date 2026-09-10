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
    IReadOnlyList<Resource> BranchResources);

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
        if (!draft.ImportModel)
            throw new InvalidOperationException("A new vest needs an imported model. The original game vest remains untouched.");
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
        for (int branch = 0; branch < template.ModelSelectorIds.Count; branch++)
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

            byte[] cloneData = clone.Write();
            BuildTableAsset checkedClone = BuildTable.Read(cloneData);
            if (checkedClone.Id != tableIdMap[oldId]
                || checkedClone.References.Any(reference => branchIds.Contains(reference.Value))
                || checkedClone.References.Any(reference => selectorMap.ContainsKey(reference.Value)))
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
        byte[] configurationData = editedConfiguration.Write();
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
                tableIdMap[template.ModelTableId], branchResources), preparedResourceData);
        resourceAdditions.AddRange(mirroredContainers.Additions);
        CharacterBuilderTagMirrorResult mirroredBuilders = MirrorCharacterBuilderTags(
            archivePaths, localArchivePath, localEntryIndex,
            template.ConfigurationTag, configurationTag,
            template.GameplayTag, gameplayTag, preparedResourceData);

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
            template.Metadata.RecordId, recordId, lootSource.Id, lootId));
        databaseChanges.Add(index.CreateStoreRegistryInsertion(infoSource.Id, infoId));
        databaseChanges.AddRange(AttachmentAddPipeline.BuildTagDictionaryChanges(index,
            [(template.ConfigurationTag, configurationTag),
                (template.GameplayTag, gameplayTag)]));

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
        foreach (string path in archivePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(path, sourceArchivePath, StringComparison.OrdinalIgnoreCase))
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
                byte[] data = edited.Write();
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
        IReadOnlyDictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]> preparedResourceData)
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
                            newConfigurationTag, oldGameplayTag, newGameplayTag))
                        targets.Add((entry.Id, resource.Id));
                }
            }
        }

        if (targets.Count == 0)
            throw new InvalidOperationException("No sibling character builder contains the exact template vest BuildTags.");

        var changes = new List<ArmoryDatabaseResourceChange>();
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
                            out byte[] updated))
                        continue;
                    changes.Add(new ArmoryDatabaseResourceChange(path, entry.Index,
                        entry.Name, resourceIndex, source.Name, updated));
                }
            }
        }

        if (changes.Count == 0)
            throw new InvalidOperationException("The sibling character builders already contain the addon BuildTags or no installed copy can be updated.");
        return new CharacterBuilderTagMirrorResult(changes, targets.Count);
    }

    static bool CanMirrorTagPair(Resource source, uint oldConfigurationTag,
        uint newConfigurationTag, uint oldGameplayTag, uint newGameplayTag)
        => TryMirrorTagPair(source, oldConfigurationTag, newConfigurationTag,
            oldGameplayTag, newGameplayTag, out _);

    static bool TryMirrorTagPair(Resource source, uint oldConfigurationTag,
        uint newConfigurationTag, uint oldGameplayTag, uint newGameplayTag,
        out byte[] updated)
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
        return changes;
    }

    /// <summary>
    /// Extends the structured EntityBuilder file-reference list which already owns all of
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
        foreach (ulong value in candidate.References)
        {
            expanded.Add(value);
            if (referenceMap.TryGetValue(value, out ulong addition))
                expanded.Add(addition);
        }

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
                checkedList.Count(value => value == pair.Value) != 1
                || checkedList.IndexOf(pair.Value)
                    != checkedList.IndexOf(pair.Key) + 1))
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

public sealed record CharacterVestAddValidationResult(
    int ConfigurationRows,
    int LocalChanges,
    int DatabaseChanges,
    int ResourceAdditions,
    int EntryAdditions,
    uint GameplayTag,
    ulong GameplayRecordId);

public sealed record CharacterVestPackageResult(
    string ProjectPath,
    string PackagePath,
    int Operations,
    int PlannedChanges,
    int Archives,
    CharacterVestAddValidationResult Validation);

public sealed record CharacterVestRepairPackageResult(
    string ProjectPath,
    string PackagePath,
    int Operations,
    int PlannedChanges,
    int Archives,
    ulong GameplayRecordId,
    uint DisplayStringId,
    ulong StoreObjectInfoId,
    ulong LootConfigurationId);

public sealed record CharacterVestModelMirrorPackageResult(
    string ProjectPath,
    string PackagePath,
    int Operations,
    int PlannedChanges,
    int Archives,
    int MirroredContainers,
    int MirroredBranchResources,
    ulong GameplayRecordId,
    uint GameplayTag);

public sealed record CharacterVestBuilderTagPackageResult(
    string ProjectPath,
    string PackagePath,
    int Operations,
    int PlannedChanges,
    int Archives,
    int TargetBuilders,
    ulong GameplayRecordId,
    uint ConfigurationTag,
    uint GameplayTag);

public sealed record CharacterVestSelectorProbePackageResult(
    string ProjectPath,
    string PackagePath,
    int Operations,
    int PlannedChanges,
    int Archives,
    ulong GameplayRecordId,
    ulong MaleSelectorId,
    ulong FemaleSelectorId);

public sealed record CharacterVestTagDictionaryPackageResult(
    string ProjectPath,
    string PackagePath,
    int Operations,
    int PlannedChanges,
    int Archives,
    ulong GameplayRecordId,
    uint ConfigurationTag,
    uint GameplayTag);

public sealed record CharacterVestEntityBuilderPackageResult(
    string ProjectPath,
    string PackagePath,
    int Operations,
    int PlannedChanges,
    int Archives,
    int RegisteredContainers,
    int RestoredModelTables,
    ulong GameplayRecordId,
    ulong MaleSelectorId,
    ulong FemaleSelectorId);

public sealed record CharacterVestResourceOrderPackageResult(
    string ProjectPath,
    string PackagePath,
    int Operations,
    int PlannedChanges,
    int Archives,
    ulong GameplayRecordId,
    ulong ItemTableId,
    ulong TagTableId,
    ulong ModelTableId);

public sealed record CharacterVestGameplayIdentityPackageResult(
    string ProjectPath,
    string PackagePath,
    int Operations,
    int PlannedChanges,
    int Archives,
    int TargetCopies,
    ulong GameplayRecordId,
    string PreviousInternalName,
    string InternalName);

public static class CharacterVestAddValidator
{
    public static CharacterVestAddValidationResult Validate(
        string gameFolder,
        string characterArchivePath,
        int templateRowIndex,
        string displayName,
        string internalName,
        string modelPath,
        AttachmentTextureDraft textures)
    {
        PreparedVestPlan prepared = Prepare(gameFolder, characterArchivePath, templateRowIndex,
            displayName, internalName, modelPath, textures, "English(US)", null);
        return Describe(prepared.Plan);
    }

    public static CharacterVestPackageResult CreatePackage(
        string gameFolder,
        string characterArchivePath,
        int templateRowIndex,
        string displayName,
        string internalName,
        string modelPath,
        AttachmentTextureDraft textures,
        string projectFolder,
        string packagePath,
        string author,
        string version,
        string languagePackage)
    {
        PreparedVestPlan prepared = Prepare(gameFolder, characterArchivePath, templateRowIndex,
            displayName, internalName, modelPath, textures, languagePackage,
            ["English(US)", languagePackage]);
        CharacterItemAddPlan plan = prepared.Plan;
        List<PendingChange> changes = ToPendingChanges(prepared);
        string groupId = "character-vest-" + internalName.Trim().ToLowerInvariant();
        string label = $"Add {displayName} as a selectable vest";

        ModProject project = ModProject.CreateInFolder(projectFolder, displayName, author, version);
        project.Record(changes, gameFolder, groupId, label);

        ModCompileResult compiled = project.Compile(gameFolder);
        if (compiled.Problems.Count != 0)
            throw new InvalidDataException("The generated vest package did not compile cleanly: "
                + string.Join("; ", compiled.Problems));
        if (compiled.AlreadyApplied != 0)
            throw new InvalidDataException("The generated vest package unexpectedly contains changes which are already installed.");
        List<ArchiveWork> archivePlans = ArchiveChangePlanner.Plan(compiled.Changes);
        if (archivePlans.Count == 0)
            throw new InvalidDataException("The generated vest package did not produce an archive plan.");

        project.ExportPackage(packagePath);
        ModPackageInfo inspected = ModProject.InspectPackage(packagePath);
        if (inspected.Operations.Count != project.Operations.Count)
            throw new InvalidDataException("The exported vest package lost one or more operations.");

        return new CharacterVestPackageResult(project.FilePath, Path.GetFullPath(packagePath),
            project.Operations.Count, compiled.Changes.Count, archivePlans.Count, Describe(plan));
    }

    /// <summary>
    /// Builds a dependency update for an already installed vest addon created by an older
    /// pipeline. It adds the missing localized name, replaces its StoreDB metadata with a
    /// correctly named clone and registers a private clone of the template loot configuration.
    /// Existing game archives are only read; the result is exported through the normal mod flow.
    /// </summary>
    public static CharacterVestRepairPackageResult CreateInstalledItemRepairPackage(
        string gameFolder,
        ulong templateRecordId,
        string installedRecordName,
        string displayName,
        string languagePackage,
        string projectFolder,
        string packagePath,
        string author,
        string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedRecordName);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(languagePackage);

        IReadOnlyList<string> archivePaths = ArchiveLocator.Find(gameFolder);
        ArmoryIndex index = ArmoryIndex.Load(AppSettings.ArmoryCachePath, archivePaths)
            ?? ArmoryIndex.Build(archivePaths);
        ArmoryIndex.IndexedResource templateRecord = EffectiveResources(index)
            .Single(resource => resource.Id == templateRecordId);
        var installedRecords = EffectiveResources(index)
            .Where(resource => resource.ClassHash == templateRecord.ClassHash
                && string.Equals(resource.Name, installedRecordName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (installedRecords.Count != 1)
            throw new InvalidOperationException($"Found {installedRecords.Count} effective gameplay records named {installedRecordName} instead of one.");
        ArmoryIndex.IndexedResource installedRecord = installedRecords[0];

        uint templateStringId = AttachmentAddPipeline.ReadGameplayDisplayStringId(
            templateRecord.Data, templateRecord.Id);
        uint installedStringId = AttachmentAddPipeline.ReadGameplayDisplayStringId(
            installedRecord.Data, installedRecord.Id);
        IReadOnlyList<AttachmentAddPipeline.LocalizationTarget> localizationTargets =
            AttachmentAddPipeline.FindLocalizationTargets([languagePackage], archivePaths,
                new Dictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]>(),
                templateStringId);
        if (localizationTargets.Count != 1)
            throw new InvalidOperationException($"The installed {languagePackage} localization package could not be resolved unambiguously.");
        AttachmentAddPipeline.LocalizationTarget localization = localizationTargets[0];
        byte[] localizationData = LocalizationPackage.AddOrReplaceString(
            localization.Resource.Data, installedStringId, displayName);

        ArmoryIndex.IndexedResource templateInfo = CharacterItemAddPipeline.FindStoreObjectInfo(index, templateRecord.Id);
        ArmoryIndex.IndexedResource installedInfo = CharacterItemAddPipeline.FindStoreObjectInfo(index, installedRecord.Id);
        var usedIds = EffectiveResources(index).Select(resource => resource.Id).ToHashSet();
        CharacterItemAddPipeline.ReserveInstalledResourceIds(archivePaths, usedIds);

        ulong correctedInfoId = AttachmentAddPipeline.Allocate64(
            "store-object-info-repair:" + installedRecordName, usedIds);
        var loadedTemplateInfo = AttachmentAddPipeline.LoadIndexedResource(index, templateInfo.Id);
        StoreObjectInfo parsedTemplateInfo = StoreObjectInfo.Parse(
            loadedTemplateInfo.Resource.Data, templateInfo.Id);
        if (!loadedTemplateInfo.Resource.Name.EndsWith(parsedTemplateInfo.Name,
                StringComparison.Ordinal))
            throw new InvalidDataException("The template StoreObjectInfo resource name does not end in its structured item name.");
        string infoResourceName = loadedTemplateInfo.Resource.Name[..^parsedTemplateInfo.Name.Length]
            + installedRecordName;
        byte[] infoData = parsedTemplateInfo.Rewrite(loadedTemplateInfo.Resource.Data,
            correctedInfoId, installedRecord.Id, installedRecordName);
        Resource infoResource = DataFile.CloneResource(loadedTemplateInfo.Resource,
            correctedInfoId, infoResourceName, infoData);

        ArmoryIndex.IndexedResource templateLoot = CharacterItemAddPipeline.FindLootConfiguration(
            index, templateRecord.Id);
        ulong lootId = AttachmentAddPipeline.Allocate64(
            "loot-configuration-repair:" + installedRecordName, usedIds);
        var loadedTemplateLoot = AttachmentAddPipeline.LoadIndexedResource(index, templateLoot.Id);
        byte[] lootData = CharacterItemAddPipeline.CloneLootConfiguration(
            loadedTemplateLoot.Resource.Data, templateLoot.Id, lootId,
            templateRecord.Id, installedRecord.Id);
        Resource lootResource = DataFile.CloneResource(loadedTemplateLoot.Resource, lootId,
            installedRecordName + "_LootConfig", lootData);

        var databaseChanges = new List<ArmoryDatabaseResourceChange>
        {
            new(localization.Location.ArchivePath, localization.Location.EntryIndex,
                localization.Location.EntryName, localization.ResourceIndex,
                localization.Resource.Name, localizationData),
            index.CreateStoreRegistryReplacement(installedInfo.Id, correctedInfoId),
            index.CreateDatabaseContainerInsertion(templateLoot.Id, lootId),
        };
        if (databaseChanges.GroupBy(change => (Path.GetFullPath(change.ArchivePath).ToUpperInvariant(),
                change.EntryIndex, change.ResourceIndex)).Any(group => group.Count() > 1))
            throw new InvalidOperationException("The vest repair would apply two independent edits to the same database resource.");

        var changes = databaseChanges.Select(change => new PendingChange(
                change.ArchivePath, change.EntryIndex, change.EntryName,
                change.ResourceIndex, change.ResourceName, change.Data))
            .ToList();
        AddResource(changes, loadedTemplateInfo.Location, infoResource);
        AddResource(changes, loadedTemplateLoot.Location, lootResource);

        string groupId = "character-vest-repair-" + installedRecordName.Trim().ToLowerInvariant();
        string label = $"Repair the name and selection metadata for {displayName}";
        ModProject project = ModProject.CreateInFolder(projectFolder,
            displayName + " UI metadata fix", author, version);
        project.Record(changes, gameFolder, groupId, label);
        ModCompileResult compiled = project.Compile(gameFolder);
        if (compiled.Problems.Count != 0)
            throw new InvalidDataException("The generated vest repair package did not compile cleanly: "
                + string.Join("; ", compiled.Problems));
        if (compiled.AlreadyApplied != 0)
            throw new InvalidDataException("The generated vest repair package unexpectedly contains changes which are already installed.");
        List<ArchiveWork> archivePlans = ArchiveChangePlanner.Plan(compiled.Changes);
        if (archivePlans.Count == 0)
            throw new InvalidDataException("The generated vest repair package did not produce an archive plan.");

        project.ExportPackage(packagePath);
        ModPackageInfo inspected = ModProject.InspectPackage(packagePath);
        if (inspected.Operations.Count != project.Operations.Count)
            throw new InvalidDataException("The exported vest repair package lost one or more operations.");
        return new CharacterVestRepairPackageResult(project.FilePath,
            Path.GetFullPath(packagePath), project.Operations.Count, compiled.Changes.Count,
            archivePlans.Count, installedRecord.Id, installedStringId, correctedInfoId, lootId);

        static IReadOnlyList<ArmoryIndex.IndexedResource> EffectiveResources(ArmoryIndex armory)
            => armory.DatabaseResources.GroupBy(resource => resource.Id)
                .Select(group => group.Last()).ToList();

        static void AddResource(List<PendingChange> pending,
            AttachmentAddPipeline.ResourceLocation location,
            Resource resource)
            => pending.Add(new PendingChange(location.ArchivePath, location.EntryIndex,
                location.EntryName, -1, resource.Name, resource.Data,
                new PendingResourceAddition(resource.Id, resource.ClassHash, resource.Header),
                ResourceClassHash: resource.ClassHash));
    }

    public static CharacterVestModelMirrorPackageResult CreateInstalledModelMirrorPackage(
        string gameFolder,
        string sourceCharacterArchivePath,
        ulong templateRecordId,
        string installedRecordName,
        string displayName,
        string projectFolder,
        string packagePath,
        string author,
        string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedRecordName);
        InstalledVestBranch installed = ResolveInstalledVestBranch(gameFolder,
            sourceCharacterArchivePath, templateRecordId, installedRecordName);
        BuildTableRow templateRow = installed.TemplateRow;
        BuildTableRow installedRow = installed.InstalledRow;
        DataFile sourceFile = installed.SourceFile;

        ulong templateItemId = RequiredComponent(templateRow, 8);
        ulong templateTagTableId = RequiredComponent(templateRow, 11);
        ulong itemId = RequiredComponent(installedRow, 8);
        ulong tagTableId = RequiredComponent(installedRow, 11);
        BuildTableAsset templateItem = BuildTable.Read(sourceFile.Resources.Single(resource =>
            resource.Id == templateItemId && resource.ClassHash == BuildTable.ClassHash).Data);
        BuildTableAsset installedItem = BuildTable.Read(sourceFile.Resources.Single(resource =>
            resource.Id == itemId && resource.ClassHash == BuildTable.ClassHash).Data);
        if (templateItem.Rows.Count != 1 || installedItem.Rows.Count != 1)
            throw new InvalidDataException("The source character item branches do not contain exactly one row.");
        ulong templateModelId = RequiredComponent(templateItem.Rows[0], 7);
        ulong modelId = RequiredComponent(installedItem.Rows[0], 7);
        ulong[] branchIds = [itemId, tagTableId, modelId];
        var branchResources = branchIds.Select(id => sourceFile.Resources.Single(resource =>
                resource.Id == id && resource.ClassHash == BuildTable.ClassHash))
            .ToList();

        VestBranchMirrorResult mirrored = CharacterItemAddPipeline.MirrorContainerCopies(
            installed.ArchivePaths, sourceCharacterArchivePath, installed.SourceEntry.Index,
            new VestBranchMirrorPlan(templateRow.Tag, installedRow.Tag,
                installed.TemplateGameplayTag, installed.GameplayTag, templateItemId, templateTagTableId,
                templateModelId, itemId, tagTableId, modelId, branchResources),
            new Dictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]>());
        if (mirrored.Changes.Count == 0)
            throw new InvalidOperationException("No missing character-container copy was found for the installed vest.");

        var changes = mirrored.Changes.Select(change => new PendingChange(
                change.ArchivePath, change.EntryIndex, change.EntryName,
                change.ResourceIndex, change.ResourceName, change.Data))
            .ToList();
        changes.AddRange(mirrored.Additions.Select(addition => new PendingChange(
            addition.ArchivePath, addition.EntryIndex, addition.EntryName, -1,
            addition.ResourceName, addition.Data,
            new PendingResourceAddition(addition.ResourceId, addition.ClassHash,
                addition.Header, addition.InsertAfterResourceId),
            ResourceClassHash: addition.ClassHash)));

        string groupId = "character-vest-model-mirror-"
            + installedRecordName.Trim().ToLowerInvariant();
        ModProject project = ModProject.CreateInFolder(projectFolder,
            displayName + " model activation fix", author, version);
        project.Record(changes, gameFolder, groupId,
            $"Mirror the {displayName} model branch into every installed character container");
        ModCompileResult compiled = project.Compile(gameFolder);
        if (compiled.Problems.Count != 0)
            throw new InvalidDataException("The generated model-activation package did not compile cleanly: "
                + string.Join("; ", compiled.Problems));
        if (compiled.AlreadyApplied != 0)
            throw new InvalidDataException("The generated model-activation package contains changes which are already installed.");
        List<ArchiveWork> plans = ArchiveChangePlanner.Plan(compiled.Changes);
        if (plans.Count == 0)
            throw new InvalidDataException("The model-activation package did not produce an archive plan.");
        project.ExportPackage(packagePath);
        ModPackageInfo inspected = ModProject.InspectPackage(packagePath);
        if (inspected.Operations.Count != project.Operations.Count)
            throw new InvalidDataException("The exported model-activation package lost one or more operations.");
        int containers = mirrored.Changes.Select(change =>
                (Path.GetFullPath(change.ArchivePath), change.EntryIndex))
            .Concat(mirrored.Additions.Select(addition =>
                (Path.GetFullPath(addition.ArchivePath), addition.EntryIndex)))
            .Distinct().Count();
        return new CharacterVestModelMirrorPackageResult(project.FilePath,
            Path.GetFullPath(packagePath), project.Operations.Count,
            compiled.Changes.Count, plans.Count, containers,
            mirrored.Additions.Count, installed.InstalledRecord.Id,
            installed.GameplayTag);

        static ulong RequiredComponent(BuildTableRow row, int componentIndex)
        {
            var references = row.References.Where(reference =>
                    reference.Kind == BuildTableReferenceKind.FileReference
                    && reference.ComponentIndex == componentIndex
                    && reference.Value != 0)
                .ToList();
            return references.Count == 1
                ? references[0].Value
                : throw new InvalidDataException($"Expected one file reference in component {componentIndex}, found {references.Count}.");
        }
    }

    public static CharacterVestSelectorProbePackageResult
        CreateInstalledSelectorIsolationPackage(
            string gameFolder,
            string sourceCharacterArchivePath,
            ulong templateRecordId,
            string installedRecordName,
            string displayName,
            string projectFolder,
            string packagePath,
            string author,
            string version)
    {
        InstalledVestBranch installed = ResolveInstalledVestBranch(gameFolder,
            sourceCharacterArchivePath, templateRecordId, installedRecordName);
        ulong templateItemId = RequiredFileReference(installed.TemplateRow, 8);
        ulong installedItemId = RequiredFileReference(installed.InstalledRow, 8);
        BuildTableAsset templateItem = BuildTable.Read(installed.SourceFile.Resources
            .Single(resource => resource.Id == templateItemId
                && resource.ClassHash == BuildTable.ClassHash).Data);
        BuildTableAsset installedItem = BuildTable.Read(installed.SourceFile.Resources
            .Single(resource => resource.Id == installedItemId
                && resource.ClassHash == BuildTable.ClassHash).Data);
        if (templateItem.Rows.Count != 1 || installedItem.Rows.Count != 1)
            throw new InvalidDataException("The vest item branches do not each contain exactly one row.");
        ulong templateModelId = RequiredFileReference(templateItem.Rows[0], 7);
        ulong installedModelId = RequiredFileReference(installedItem.Rows[0], 7);
        BuildTableAsset templateModel = BuildTable.Read(installed.SourceFile.Resources
            .Single(resource => resource.Id == templateModelId
                && resource.ClassHash == BuildTable.ClassHash).Data);
        if (templateModel.Rows.Count != 2)
            throw new InvalidDataException("The template vest model table does not contain exactly two gender rows.");
        ulong[] templateSelectors = templateModel.Rows.Select((row, rowIndex) =>
            RequiredModelHandle(row, rowIndex)).ToArray();

        ulong sourceContainerId = installed.SourceEntry.Id;
        var changes = new List<PendingChange>();
        foreach (string path in installed.ArchivePaths.Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            using var archive = ForgeArchive.Open(path);
            ForgeEntry? entry = archive.Entries.FirstOrDefault(candidate =>
                candidate.Id == sourceContainerId);
            if (entry is null)
                continue;
            using var stream = new MemoryStream(archive.ReadEntry(entry));
            DataFile file = DataFile.Read(stream);
            int resourceIndex = file.Resources.FindIndex(resource =>
                resource.Id == installedModelId
                && resource.ClassHash == BuildTable.ClassHash);
            if (resourceIndex < 0)
                continue;
            Resource resource = file.Resources[resourceIndex];
            BuildTableAsset model = BuildTable.Read(resource.Data);
            if (model.Rows.Count != templateSelectors.Length)
                throw new InvalidDataException($"The installed vest model table in {Path.GetFileName(path)} does not match the template's gender rows.");
            for (int rowIndex = 0; rowIndex < model.Rows.Count; rowIndex++)
            {
                BuildTableReference handle = model.Rows[rowIndex].References.Single(reference =>
                    reference.Kind == BuildTableReferenceKind.Handle
                    && reference.ComponentIndex == 1 && reference.Value != 0);
                handle.Value = templateSelectors[rowIndex];
            }
            byte[] data = model.Write();
            BuildTableAsset checkedModel = BuildTable.Read(data);
            for (int rowIndex = 0; rowIndex < checkedModel.Rows.Count; rowIndex++)
            {
                if (RequiredModelHandle(checkedModel.Rows[rowIndex], rowIndex)
                    != templateSelectors[rowIndex])
                    throw new InvalidDataException("The selector-isolation table failed validation.");
            }
            changes.Add(new PendingChange(path, entry.Index, entry.Name,
                resourceIndex, resource.Name, data,
                ResourceClassHash: BuildTable.ClassHash));
        }
        if (changes.Count == 0)
            throw new InvalidOperationException("No installed copy of the private vest model table was found.");

        string groupId = "character-vest-selector-probe-"
            + installedRecordName.Trim().ToLowerInvariant();
        ModProject project = ModProject.CreateInFolder(projectFolder,
            displayName + " selector isolation test", author, version);
        project.Record(changes, gameFolder, groupId,
            $"Temporarily route {displayName} through the verified template selectors");
        ModCompileResult compiled = project.Compile(gameFolder);
        if (compiled.Problems.Count != 0)
            throw new InvalidDataException("The selector-isolation package did not compile cleanly: "
                + string.Join("; ", compiled.Problems));
        if (compiled.AlreadyApplied != 0)
            throw new InvalidDataException("The selector-isolation package contains changes which are already installed.");
        List<ArchiveWork> plans = ArchiveChangePlanner.Plan(compiled.Changes);
        if (plans.Count == 0)
            throw new InvalidDataException("The selector-isolation package did not produce an archive plan.");
        project.ExportPackage(packagePath);
        ModPackageInfo inspected = ModProject.InspectPackage(packagePath);
        if (inspected.Operations.Count != project.Operations.Count)
            throw new InvalidDataException("The exported selector-isolation package lost one or more operations.");
        return new CharacterVestSelectorProbePackageResult(project.FilePath,
            Path.GetFullPath(packagePath), project.Operations.Count,
            compiled.Changes.Count, plans.Count, installed.InstalledRecord.Id,
            templateSelectors[0], templateSelectors[1]);

        static ulong RequiredFileReference(BuildTableRow row, int componentIndex)
        {
            var references = row.References.Where(reference =>
                    reference.Kind == BuildTableReferenceKind.FileReference
                    && reference.ComponentIndex == componentIndex
                    && reference.Value != 0)
                .ToList();
            return references.Count == 1
                ? references[0].Value
                : throw new InvalidDataException($"Expected one file reference in component {componentIndex}, found {references.Count}.");
        }

        static ulong RequiredModelHandle(BuildTableRow row, int rowIndex)
        {
            var references = row.References.Where(reference =>
                    reference.Kind == BuildTableReferenceKind.Handle
                    && reference.ComponentIndex == 1 && reference.Value != 0)
                .ToList();
            return references.Count == 1
                ? references[0].Value
                : throw new InvalidDataException($"Expected one model handle in gender row {rowIndex}, found {references.Count}.");
        }
    }

    public static CharacterVestBuilderTagPackageResult CreateInstalledBuilderTagPackage(
        string gameFolder,
        string sourceCharacterArchivePath,
        ulong templateRecordId,
        string installedRecordName,
        string displayName,
        string projectFolder,
        string packagePath,
        string author,
        string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedRecordName);
        InstalledVestBranch installed = ResolveInstalledVestBranch(gameFolder,
            sourceCharacterArchivePath, templateRecordId, installedRecordName);
        CharacterBuilderTagMirrorResult mirrored =
            CharacterItemAddPipeline.MirrorCharacterBuilderTags(
                installed.ArchivePaths, sourceCharacterArchivePath,
                installed.SourceEntry.Index, installed.TemplateRow.Tag,
                installed.InstalledRow.Tag, installed.TemplateGameplayTag,
                installed.GameplayTag,
                new Dictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]>());

        var changes = mirrored.Changes.Select(change => new PendingChange(
                change.ArchivePath, change.EntryIndex, change.EntryName,
                change.ResourceIndex, change.ResourceName, change.Data))
            .ToList();
        string groupId = "character-vest-builder-tags-"
            + installedRecordName.Trim().ToLowerInvariant();
        ModProject project = ModProject.CreateInFolder(projectFolder,
            displayName + " character builder fix", author, version);
        project.Record(changes, gameFolder, groupId,
            $"Register {displayName} in the exact sibling character builders used by its template");
        ModCompileResult compiled = project.Compile(gameFolder);
        if (compiled.Problems.Count != 0)
            throw new InvalidDataException("The generated character-builder package did not compile cleanly: "
                + string.Join("; ", compiled.Problems));
        if (compiled.AlreadyApplied != 0)
            throw new InvalidDataException("The generated character-builder package contains changes which are already installed.");
        List<ArchiveWork> plans = ArchiveChangePlanner.Plan(compiled.Changes);
        if (plans.Count == 0)
            throw new InvalidDataException("The character-builder package did not produce an archive plan.");
        project.ExportPackage(packagePath);
        ModPackageInfo inspected = ModProject.InspectPackage(packagePath);
        if (inspected.Operations.Count != project.Operations.Count)
            throw new InvalidDataException("The exported character-builder package lost one or more operations.");
        return new CharacterVestBuilderTagPackageResult(project.FilePath,
            Path.GetFullPath(packagePath), project.Operations.Count,
            compiled.Changes.Count, plans.Count, mirrored.TargetResources,
            installed.InstalledRecord.Id, installed.InstalledRow.Tag,
            installed.GameplayTag);
    }

    public static CharacterVestTagDictionaryPackageResult
        CreateInstalledTagDictionaryPackage(
            string gameFolder,
            string sourceCharacterArchivePath,
            ulong templateRecordId,
            string installedRecordName,
            string displayName,
            string projectFolder,
            string packagePath,
            string author,
            string version)
    {
        InstalledVestBranch installed = ResolveInstalledVestBranch(gameFolder,
            sourceCharacterArchivePath, templateRecordId, installedRecordName);
        IReadOnlyList<ArmoryDatabaseResourceChange> dictionaryChanges =
            AttachmentAddPipeline.BuildTagDictionaryChanges(installed.Index,
                [(installed.TemplateRow.Tag, installed.InstalledRow.Tag),
                    (installed.TemplateGameplayTag, installed.GameplayTag)]);
        if (dictionaryChanges.Count == 0)
            throw new InvalidOperationException("The installed vest already has complete global TagDictionnaries entries.");
        var changes = dictionaryChanges.Select(change => new PendingChange(
                change.ArchivePath, change.EntryIndex, change.EntryName,
                change.ResourceIndex, change.ResourceName, change.Data))
            .ToList();

        string groupId = "character-vest-tag-dictionary-"
            + installedRecordName.Trim().ToLowerInvariant();
        ModProject project = ModProject.CreateInFolder(projectFolder,
            displayName + " tag dictionary fix", author, version);
        project.Record(changes, gameFolder, groupId,
            $"Complete the global TagDescriptor and TagGroupDescriptor entries for {displayName}");
        ModCompileResult compiled = project.Compile(gameFolder);
        if (compiled.Problems.Count != 0)
            throw new InvalidDataException("The TagDictionnaries package did not compile cleanly: "
                + string.Join("; ", compiled.Problems));
        if (compiled.AlreadyApplied != 0)
            throw new InvalidDataException("The TagDictionnaries package contains changes which are already installed.");
        List<ArchiveWork> plans = ArchiveChangePlanner.Plan(compiled.Changes);
        if (plans.Count == 0)
            throw new InvalidDataException("The TagDictionnaries package did not produce an archive plan.");
        project.ExportPackage(packagePath);
        ModPackageInfo inspected = ModProject.InspectPackage(packagePath);
        if (inspected.Operations.Count != project.Operations.Count)
            throw new InvalidDataException("The exported TagDictionnaries package lost one or more operations.");
        return new CharacterVestTagDictionaryPackageResult(project.FilePath,
            Path.GetFullPath(packagePath), project.Operations.Count,
            compiled.Changes.Count, plans.Count, installed.InstalledRecord.Id,
            installed.InstalledRow.Tag, installed.GameplayTag);
    }

    public static CharacterVestEntityBuilderPackageResult
        CreateInstalledEntityBuilderReferencePackage(
            string gameFolder,
            string sourceCharacterArchivePath,
            ulong templateRecordId,
            string installedRecordName,
            string displayName,
            ulong maleSelectorId,
            ulong femaleSelectorId,
            string projectFolder,
            string packagePath,
            string author,
            string version)
    {
        if (maleSelectorId == 0 || femaleSelectorId == 0
            || maleSelectorId == femaleSelectorId)
            throw new ArgumentException("The two addon model selectors must be distinct and non-zero.");

        InstalledVestBranch installed = ResolveInstalledVestBranch(gameFolder,
            sourceCharacterArchivePath, templateRecordId, installedRecordName);
        ulong templateItemId = RequiredFileReference(installed.TemplateRow, 8);
        ulong templateTagId = RequiredFileReference(installed.TemplateRow, 11);
        ulong installedItemId = RequiredFileReference(installed.InstalledRow, 8);
        ulong installedTagId = RequiredFileReference(installed.InstalledRow, 11);
        ulong templateModelId = ModelTableId(installed.SourceFile, templateItemId);
        ulong installedModelId = ModelTableId(installed.SourceFile, installedItemId);
        var referenceMap = new Dictionary<ulong, ulong>
        {
            [templateItemId] = installedItemId,
            [templateTagId] = installedTagId,
            [templateModelId] = installedModelId,
        };

        var installedEntryIds = new HashSet<ulong>();
        foreach (string path in installed.ArchivePaths.Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            using var archive = ForgeArchive.Open(path);
            installedEntryIds.UnionWith(archive.Entries.Select(entry => entry.Id));
        }
        if (!installedEntryIds.Contains(maleSelectorId)
            || !installedEntryIds.Contains(femaleSelectorId))
            throw new InvalidDataException("One or both addon model selectors are not installed Forge entries.");

        var changes = new List<PendingChange>();
        int registeredContainers = 0;
        int restoredModelTables = 0;
        foreach (string path in installed.ArchivePaths.Distinct(
                     StringComparer.OrdinalIgnoreCase))
        {
            using var archive = ForgeArchive.Open(path);
            ForgeEntry? entry = archive.Entries.FirstOrDefault(candidate =>
                candidate.Id == installed.SourceEntry.Id);
            if (entry is null)
                continue;
            using var stream = new MemoryStream(archive.ReadEntry(entry));
            DataFile file = DataFile.Read(stream);
            if (referenceMap.Values.Any(id => !file.Resources.Any(resource =>
                    resource.Id == id && resource.ClassHash == BuildTable.ClassHash)))
                continue;

            int matchedBuilders = 0;
            foreach ((Resource resource, int resourceIndex) in file.Resources
                         .Select((resource, index) => (resource, index))
                         .Where(item => item.resource.ClassHash ==
                             CharacterItemAddPipeline.EntityBuilderClassHash))
            {
                if (!CharacterItemAddPipeline.TryRegisterEntityBuilderReferences(
                        resource.Data, referenceMap, out byte[] data,
                        out bool found))
                {
                    if (found)
                        matchedBuilders++;
                    continue;
                }
                matchedBuilders++;
                changes.Add(new PendingChange(path, entry.Index, entry.Name,
                    resourceIndex, resource.Name, data,
                    ResourceClassHash: resource.ClassHash));
            }
            if (matchedBuilders != 1)
                throw new InvalidDataException($"{Path.GetFileName(path)}/{entry.Name} contains {matchedBuilders} EntityBuilder reference owners for the vest branch instead of one.");
            registeredContainers++;

            int modelIndex = file.Resources.FindIndex(resource =>
                resource.Id == installedModelId
                && resource.ClassHash == BuildTable.ClassHash);
            BuildTableAsset model = BuildTable.Read(file.Resources[modelIndex].Data);
            if (model.Rows.Count != 2)
                throw new InvalidDataException("The installed vest model table does not contain its two gender rows.");
            ulong[] selectors = [maleSelectorId, femaleSelectorId];
            bool modelChanged = false;
            for (int rowIndex = 0; rowIndex < model.Rows.Count; rowIndex++)
            {
                BuildTableReference handle = model.Rows[rowIndex].References
                    .Single(reference => reference.Kind ==
                            BuildTableReferenceKind.Handle
                        && reference.ComponentIndex == 1
                        && reference.Value != 0);
                if (handle.Value == selectors[rowIndex])
                    continue;
                handle.Value = selectors[rowIndex];
                modelChanged = true;
            }
            if (modelChanged)
            {
                byte[] modelData = model.Write();
                BuildTableAsset checkedModel = BuildTable.Read(modelData);
                for (int rowIndex = 0; rowIndex < selectors.Length; rowIndex++)
                {
                    ulong selector = checkedModel.Rows[rowIndex].References
                        .Single(reference => reference.Kind ==
                                BuildTableReferenceKind.Handle
                            && reference.ComponentIndex == 1
                            && reference.Value != 0).Value;
                    if (selector != selectors[rowIndex])
                        throw new InvalidDataException("The restored addon model selector failed validation.");
                }
                changes.Add(new PendingChange(path, entry.Index, entry.Name,
                    modelIndex, file.Resources[modelIndex].Name, modelData,
                    ResourceClassHash: BuildTable.ClassHash));
                restoredModelTables++;
            }
        }
        if (registeredContainers == 0)
            throw new InvalidOperationException("No installed character container owns the complete private vest branch.");
        if (changes.Count == 0)
            throw new InvalidOperationException("The installed vest is already registered in every EntityBuilder and uses the requested selectors.");

        string groupId = "character-vest-entitybuilder-"
            + installedRecordName.Trim().ToLowerInvariant();
        ModProject project = ModProject.CreateInFolder(projectFolder,
            displayName + " EntityBuilder fix", author, version);
        project.Record(changes, gameFolder, groupId,
            $"Register the private BuildTable branch and restore the addon model selectors for {displayName}");
        ModCompileResult compiled = project.Compile(gameFolder);
        if (compiled.Problems.Count != 0)
            throw new InvalidDataException("The EntityBuilder repair package did not compile cleanly: "
                + string.Join("; ", compiled.Problems));
        if (compiled.AlreadyApplied != 0)
            throw new InvalidDataException("The EntityBuilder repair package contains changes which are already installed.");
        List<ArchiveWork> plans = ArchiveChangePlanner.Plan(compiled.Changes);
        if (plans.Count == 0)
            throw new InvalidDataException("The EntityBuilder repair package did not produce an archive plan.");
        project.ExportPackage(packagePath);
        ModPackageInfo inspected = ModProject.InspectPackage(packagePath);
        if (inspected.Operations.Count != project.Operations.Count)
            throw new InvalidDataException("The exported EntityBuilder repair package lost one or more operations.");
        return new CharacterVestEntityBuilderPackageResult(project.FilePath,
            Path.GetFullPath(packagePath), project.Operations.Count,
            compiled.Changes.Count, plans.Count, registeredContainers,
            restoredModelTables, installed.InstalledRecord.Id,
            maleSelectorId, femaleSelectorId);

        static ulong RequiredFileReference(BuildTableRow row,
            int componentIndex) => row.References.Single(reference =>
                reference.Kind == BuildTableReferenceKind.FileReference
                && reference.ComponentIndex == componentIndex
                && reference.Value != 0).Value;

        static ulong ModelTableId(DataFile file, ulong itemTableId)
        {
            BuildTableAsset item = BuildTable.Read(file.Resources.Single(resource =>
                resource.Id == itemTableId
                && resource.ClassHash == BuildTable.ClassHash).Data);
            if (item.Rows.Count != 1)
                throw new InvalidDataException("The vest item table does not contain exactly one row.");
            return RequiredFileReference(item.Rows[0], 7);
        }
    }

    public static CharacterVestResourceOrderPackageResult
        CreateInstalledOrderedBranchPackage(
            string gameFolder,
            string sourceCharacterArchivePath,
            ulong templateRecordId,
            string installedRecordName,
            string displayName,
            string projectFolder,
            string packagePath,
            string author,
            string version)
    {
        InstalledVestBranch installed = ResolveInstalledVestBranch(gameFolder,
            sourceCharacterArchivePath, templateRecordId, installedRecordName);
        DataFile sourceFile = installed.SourceFile;
        ulong templateItemId = RequiredFileReference(installed.TemplateRow, 8);
        ulong templateTagId = RequiredFileReference(installed.TemplateRow, 11);
        ulong installedItemId = RequiredFileReference(installed.InstalledRow, 8);
        ulong installedTagId = RequiredFileReference(installed.InstalledRow, 11);
        ulong templateModelId = ModelTableId(sourceFile, templateItemId);
        ulong installedModelId = ModelTableId(sourceFile, installedItemId);

        var usedIds = new HashSet<ulong>();
        CharacterItemAddPipeline.ReserveInstalledResourceIds(
            installed.ArchivePaths, usedIds);
        ulong orderedItemId = AttachmentAddPipeline.Allocate64(
            $"character-ordered-item:{installedRecordName}", usedIds);
        ulong orderedTagId = AttachmentAddPipeline.Allocate64(
            $"character-ordered-tag:{installedRecordName}", usedIds);
        ulong orderedModelId = AttachmentAddPipeline.Allocate64(
            $"character-ordered-model:{installedRecordName}", usedIds);

        Resource installedItem = RequireBuildTable(sourceFile, installedItemId);
        Resource installedTag = RequireBuildTable(sourceFile, installedTagId);
        Resource installedModel = RequireBuildTable(sourceFile, installedModelId);
        Resource orderedModel = CloneTable(installedModel, orderedModelId,
            $"{installedRecordName}_OrderedModel", new Dictionary<ulong, ulong>());
        Resource orderedItem = CloneTable(installedItem, orderedItemId,
            $"{installedRecordName}_OrderedItem",
            new Dictionary<ulong, ulong> { [installedModelId] = orderedModelId });
        Resource orderedTag = CloneTable(installedTag, orderedTagId,
            $"{installedRecordName}_OrderedTag", new Dictionary<ulong, ulong>());

        Resource configurationResource = sourceFile.Resources.Single(resource =>
            resource.Id == CharacterItemAddPipeline.MediumVestConfigurationTableId
            && resource.ClassHash == BuildTable.ClassHash);
        int configurationIndex = sourceFile.Resources.IndexOf(configurationResource);
        BuildTableAsset configuration = BuildTable.Read(configurationResource.Data);
        BuildTableRow addonRow = configuration.Rows.Single(row =>
            row.PossibleTags.Count(tag => tag == installed.GameplayTag) == 1);
        ReplaceReference(addonRow, 8, installedItemId, orderedItemId);
        ReplaceReference(addonRow, 11, installedTagId, orderedTagId);
        byte[] configurationData = configuration.Write();
        BuildTableRow checkedRow = BuildTable.Read(configurationData).Rows.Single(row =>
            row.PossibleTags.Count(tag => tag == installed.GameplayTag) == 1);
        if (RequiredFileReference(checkedRow, 8) != orderedItemId
            || RequiredFileReference(checkedRow, 11) != orderedTagId)
            throw new InvalidDataException("The ordered vest branch did not retain its configuration references.");

        var referenceMap = new Dictionary<ulong, ulong>
        {
            [templateItemId] = orderedItemId,
            [templateTagId] = orderedTagId,
            [templateModelId] = orderedModelId,
        };
        var changes = new List<PendingChange>
        {
            new(sourceCharacterArchivePath, installed.SourceEntry.Index,
                installed.SourceEntry.Name, configurationIndex,
                configurationResource.Name, configurationData,
                ResourceClassHash: BuildTable.ClassHash),
        };
        int builderMatches = 0;
        foreach ((Resource resource, int index) in sourceFile.Resources
                     .Select((resource, index) => (resource, index))
                     .Where(item => item.resource.ClassHash ==
                         CharacterItemAddPipeline.EntityBuilderClassHash))
        {
            if (!CharacterItemAddPipeline.TryRegisterEntityBuilderReferences(
                    resource.Data, referenceMap, out byte[] builderData,
                    out bool found))
            {
                if (found)
                    builderMatches++;
                continue;
            }
            builderMatches++;
            changes.Add(new PendingChange(sourceCharacterArchivePath,
                installed.SourceEntry.Index, installed.SourceEntry.Name,
                index, resource.Name, builderData,
                ResourceClassHash: resource.ClassHash));
        }
        if (builderMatches != 1)
            throw new InvalidDataException($"The source character container contains {builderMatches} matching EntityBuilder table lists instead of one.");

        foreach (Resource resource in new[] { orderedItem, orderedTag, orderedModel })
        {
            ulong insertionAnchor = resource.Id == orderedItemId
                ? templateItemId
                : resource.Id == orderedTagId
                    ? templateTagId
                    : templateModelId;
            changes.Add(new PendingChange(sourceCharacterArchivePath,
                installed.SourceEntry.Index, installed.SourceEntry.Name, -1,
                resource.Name, resource.Data,
                new PendingResourceAddition(resource.Id, resource.ClassHash,
                    resource.Header, insertionAnchor),
                ResourceClassHash: resource.ClassHash));
        }

        string groupId = "character-vest-resource-order-"
            + installedRecordName.Trim().ToLowerInvariant();
        ModProject project = ModProject.CreateInFolder(projectFolder,
            displayName + " BuildTable loading fix", author, version);
        project.Record(changes, gameFolder, groupId,
            $"Place the private {displayName} BuildTables in the character table TOC and route the installed selection through them");
        ModCompileResult compiled = project.Compile(gameFolder);
        if (compiled.Problems.Count != 0)
            throw new InvalidDataException("The ordered BuildTable package did not compile cleanly: "
                + string.Join("; ", compiled.Problems));
        if (compiled.AlreadyApplied != 0)
            throw new InvalidDataException("The ordered BuildTable package unexpectedly contains changes which are already installed.");
        List<ArchiveWork> plans = ArchiveChangePlanner.Plan(compiled.Changes);
        if (plans.Count != 1
            || !plans[0].Entries.TryGetValue(installed.SourceEntry.Index,
                out byte[]? rebuiltBytes))
            throw new InvalidDataException("The ordered BuildTable package did not produce the expected character-container rebuild.");

        DataFile rebuilt = DataFile.Read(new MemoryStream(rebuiltBytes));
        ulong[] orderedIds = [orderedItemId, orderedTagId, orderedModelId];
        ulong[] templateIds = [templateItemId, templateTagId, templateModelId];
        for (int pair = 0; pair < orderedIds.Length; pair++)
        {
            int templatePosition = rebuilt.Resources.FindIndex(resource =>
                resource.Id == templateIds[pair]
                && resource.ClassHash == BuildTable.ClassHash);
            int orderedPosition = rebuilt.Resources.FindIndex(resource =>
                resource.Id == orderedIds[pair]
                && resource.ClassHash == BuildTable.ClassHash);
            if (templatePosition < 0 || orderedPosition != templatePosition + 1)
                throw new InvalidDataException("The new vest table was not placed directly after its original TOC anchor: "
                    + $"template 0x{templateIds[pair]:X12} at {templatePosition}, clone 0x{orderedIds[pair]:X12} at {orderedPosition}.");
        }

        project.ExportPackage(packagePath);
        ModPackageInfo inspected = ModProject.InspectPackage(packagePath);
        if (inspected.Operations.Count != project.Operations.Count)
            throw new InvalidDataException("The exported ordered BuildTable package lost one or more operations.");
        return new CharacterVestResourceOrderPackageResult(project.FilePath,
            Path.GetFullPath(packagePath), project.Operations.Count,
            compiled.Changes.Count, plans.Count, installed.InstalledRecord.Id,
            orderedItemId, orderedTagId, orderedModelId);

        static ulong RequiredFileReference(BuildTableRow row,
            int componentIndex) => row.References.Single(reference =>
                reference.Kind == BuildTableReferenceKind.FileReference
                && reference.ComponentIndex == componentIndex
                && reference.Value != 0).Value;

        static ulong ModelTableId(DataFile file, ulong itemTableId)
        {
            BuildTableAsset item = BuildTable.Read(
                RequireBuildTable(file, itemTableId).Data);
            if (item.Rows.Count != 1)
                throw new InvalidDataException("The vest item table does not contain exactly one row.");
            return RequiredFileReference(item.Rows[0], 7);
        }

        static Resource RequireBuildTable(DataFile file, ulong id) =>
            file.Resources.Single(resource => resource.Id == id
                && resource.ClassHash == BuildTable.ClassHash);

        static void ReplaceReference(BuildTableRow row, int componentIndex,
            ulong expected, ulong replacement)
        {
            BuildTableReference reference = row.References.Single(candidate =>
                candidate.Kind == BuildTableReferenceKind.FileReference
                && candidate.ComponentIndex == componentIndex
                && candidate.Value == expected);
            reference.Value = replacement;
        }

        static Resource CloneTable(Resource source, ulong newId, string name,
            IReadOnlyDictionary<ulong, ulong> replacements)
        {
            BuildTableAsset table = BuildTable.Read(source.Data);
            BuildTableReference identity = table.References.Single(reference =>
                reference.Kind == BuildTableReferenceKind.TableIdentity);
            if (identity.Value != source.Id)
                throw new InvalidDataException("The installed vest table has a mismatched identity.");
            identity.Value = newId;
            foreach (BuildTableReference reference in table.References.Where(reference =>
                         reference.Kind != BuildTableReferenceKind.TableIdentity
                         && replacements.ContainsKey(reference.Value)))
                reference.Value = replacements[reference.Value];
            byte[] data = table.Write();
            BuildTableAsset checkedTable = BuildTable.Read(data);
            if (checkedTable.Id != newId
                || replacements.Any(pair => checkedTable.References.Any(reference =>
                    reference.Value == pair.Key)))
                throw new InvalidDataException("The ordered vest table clone failed validation.");
            return DataFile.CloneResource(source, newId, name, data);
        }
    }

    public static CharacterVestGameplayIdentityPackageResult
        CreateInstalledGameplayIdentityPackage(
            string gameFolder,
            string sourceCharacterArchivePath,
            ulong templateRecordId,
            string installedRecordName,
            string displayName,
            string projectFolder,
            string packagePath,
            string author,
            string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installedRecordName);
        InstalledVestBranch installed = ResolveInstalledVestBranch(gameFolder,
            sourceCharacterArchivePath, templateRecordId, installedRecordName);
        string previousName = AttachmentAddPipeline.ReadGameplayInternalName(
            installed.InstalledRecord.Data, installed.InstalledRecord.Id);
        if (string.Equals(previousName, installedRecordName,
                StringComparison.Ordinal))
            throw new InvalidOperationException("The installed vest gameplay record already has its unique internal name.");

        var targets = installed.Index.DatabaseResources.Where(resource =>
                resource.Id == installed.InstalledRecord.Id
                && resource.ClassHash == installed.InstalledRecord.ClassHash)
            .OrderBy(resource => resource.ArchivePath,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(resource => resource.EntryIndex)
            .ThenBy(resource => resource.ResourceIndex)
            .ToList();
        if (targets.Count == 0)
            throw new InvalidOperationException("No installed gameplay-record copies were found.");

        var changes = new List<PendingChange>(targets.Count);
        foreach (ArmoryIndex.IndexedResource target in targets)
        {
            string currentName = AttachmentAddPipeline.ReadGameplayInternalName(
                target.Data, target.Id);
            if (!string.Equals(currentName, previousName,
                    StringComparison.Ordinal))
                throw new InvalidDataException($"The installed gameplay-record copies disagree on their internal name: {previousName} and {currentName}.");
            byte[] data = AttachmentAddPipeline.RewriteGameplayInternalName(
                target.Data, target.Id, installedRecordName);
            changes.Add(new PendingChange(target.ArchivePath, target.EntryIndex,
                target.EntryName, target.ResourceIndex, target.Name, data,
                ResourceClassHash: target.ClassHash));
        }

        string groupId = "character-vest-gameplay-identity-"
            + installedRecordName.Trim().ToLowerInvariant();
        ModProject project = ModProject.CreateInFolder(projectFolder,
            displayName + " gameplay identity fix", author, version);
        project.Record(changes, gameFolder, groupId,
            $"Give {displayName} its own internal gameplay selection identity");
        ModCompileResult compiled = project.Compile(gameFolder);
        if (compiled.Problems.Count != 0)
            throw new InvalidDataException("The gameplay-identity package did not compile cleanly: "
                + string.Join("; ", compiled.Problems));
        if (compiled.AlreadyApplied != 0)
            throw new InvalidDataException("The gameplay-identity package contains changes which are already installed.");
        List<ArchiveWork> plans = ArchiveChangePlanner.Plan(compiled.Changes);
        if (plans.Count == 0)
            throw new InvalidDataException("The gameplay-identity package did not produce an archive plan.");
        project.ExportPackage(packagePath);
        ModPackageInfo inspected = ModProject.InspectPackage(packagePath);
        if (inspected.Operations.Count != project.Operations.Count)
            throw new InvalidDataException("The exported gameplay-identity package lost one or more operations.");
        return new CharacterVestGameplayIdentityPackageResult(project.FilePath,
            Path.GetFullPath(packagePath), project.Operations.Count,
            compiled.Changes.Count, plans.Count, targets.Count,
            installed.InstalledRecord.Id, previousName, installedRecordName);
    }

    static InstalledVestBranch ResolveInstalledVestBranch(string gameFolder,
        string sourceCharacterArchivePath, ulong templateRecordId,
        string installedRecordName)
    {
        IReadOnlyList<string> archivePaths = ArchiveLocator.Find(gameFolder);
        ArmoryIndex index = ArmoryIndex.Load(AppSettings.ArmoryCachePath, archivePaths)
            ?? ArmoryIndex.Build(archivePaths);
        IReadOnlyList<ArmoryIndex.IndexedResource> effective = index.DatabaseResources
            .GroupBy(resource => resource.Id).Select(group => group.Last()).ToList();
        ArmoryIndex.IndexedResource templateRecord = effective.Single(resource =>
            resource.Id == templateRecordId);
        var installedRecords = effective.Where(resource =>
                resource.ClassHash == templateRecord.ClassHash
                && string.Equals(resource.Name, installedRecordName,
                    StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (installedRecords.Count != 1)
            throw new InvalidOperationException($"Found {installedRecords.Count} effective gameplay records named {installedRecordName} instead of one.");
        ArmoryIndex.IndexedResource installedRecord = installedRecords[0];
        uint templateGameplayTag = AttachmentAddPipeline.ReadGameplayBuildTag(
            templateRecord.Data, templateRecord.Id);
        uint gameplayTag = AttachmentAddPipeline.ReadGameplayBuildTag(
            installedRecord.Data, installedRecord.Id);

        using var sourceArchive = ForgeArchive.Open(sourceCharacterArchivePath);
        var sourceEntries = sourceArchive.Entries.Where(entry =>
                entry.FileExtension == ".data"
                && sourceArchive.ReadResourceIndex(entry).Any(resource =>
                    resource.Id == CharacterItemAddPipeline.MediumVestConfigurationTableId))
            .ToList();
        if (sourceEntries.Count != 1)
            throw new InvalidOperationException($"Found {sourceEntries.Count} source character containers instead of one.");
        ForgeEntry sourceEntry = sourceEntries[0];
        using var sourceStream = new MemoryStream(sourceArchive.ReadEntry(sourceEntry));
        DataFile sourceFile = DataFile.Read(sourceStream);
        Resource configurationResource = sourceFile.Resources.Single(resource =>
            resource.Id == CharacterItemAddPipeline.MediumVestConfigurationTableId
            && resource.ClassHash == BuildTable.ClassHash);
        BuildTableAsset configuration = BuildTable.Read(configurationResource.Data);
        var templateRows = configuration.Rows.Where(row =>
                row.PossibleTags.Count(tag => tag == templateGameplayTag) == 1)
            .ToList();
        var installedRows = configuration.Rows.Where(row =>
                row.PossibleTags.Count(tag => tag == gameplayTag) == 1)
            .ToList();
        if (templateRows.Count != 1 || installedRows.Count != 1)
            throw new InvalidDataException("The source MediumVestCFG does not contain exactly one template and one installed addon row.");
        return new InstalledVestBranch(archivePaths, index, installedRecord,
            templateGameplayTag, gameplayTag, sourceEntry, sourceFile,
            templateRows[0], installedRows[0]);
    }

    static PreparedVestPlan Prepare(
        string gameFolder,
        string characterArchivePath,
        int templateRowIndex,
        string displayName,
        string internalName,
        string modelPath,
        AttachmentTextureDraft textures,
        string languagePackage,
        IReadOnlyList<string>? localizationPackages)
    {
        IReadOnlyList<string> archivePaths = ArchiveLocator.Find(gameFolder);
        ArmoryIndex index = ArmoryIndex.Load(AppSettings.ArmoryCachePath, archivePaths)
            ?? ArmoryIndex.Build(archivePaths);

        using var archive = ForgeArchive.Open(characterArchivePath);
        var candidates = archive.Entries.Where(entry => entry.FileExtension == ".data"
                && archive.ReadResourceIndex(entry).Any(resource =>
                    resource.Id == CharacterItemAddPipeline.MediumVestConfigurationTableId))
            .ToList();
        if (candidates.Count != 1)
            throw new InvalidOperationException($"Found {candidates.Count} character containers with the verified medium-vest table instead of one.");
        ForgeEntry entry = candidates[0];
        using var stream = new MemoryStream(archive.ReadEntry(entry));
        DataFile file = DataFile.Read(stream);
        var localResources = file.Resources.ToDictionary(resource => resource.Id);
        var localIndexes = file.Resources.Select((resource, resourceIndex) => (resource.Id, resourceIndex))
            .ToDictionary(item => item.Id, item => item.resourceIndex);
        BuildTableAsset configuration = BuildTable.Read(localResources[
            CharacterItemAddPipeline.MediumVestConfigurationTableId].Data);
        IReadOnlyCollection<uint> tags = configuration.Rows
            .SelectMany(row => row.PossibleTags.Append(row.Tag)).Distinct().ToList();
        BuildTableGameMetadata metadata = BuildTableGameMetadataResolver.Build(index, [],
            preferredLanguagePackage: languagePackage, buildTags: tags);
        var draft = new AddAttachmentDraft(displayName, internalName,
            new AddAttachmentTemplate(templateRowIndex, displayName, internalName), true, modelPath,
            true, false, false, true, textures);
        CharacterItemAddPlan plan = CharacterItemAddPipeline.BuildVest(index, configuration,
            templateRowIndex, draft, metadata.LanguagePackage, archivePaths, localResources,
            localIndexes, characterArchivePath, entry.Index, entry.Name,
            new Dictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]>(),
            [], tag => metadata.ByBuildTag.GetValueOrDefault(tag),
            localizationPackages: localizationPackages);
        if (plan.ResourceAdditions.GroupBy(addition =>
                (Path.GetFullPath(addition.ArchivePath).ToUpperInvariant(),
                    addition.EntryIndex, addition.ResourceId))
                .Any(group => group.Count() > 1)
            || plan.EntryAdditions.GroupBy(addition =>
                (Path.GetFullPath(addition.ArchivePath).ToUpperInvariant(),
                    addition.EntryId)).Any(group => group.Count() > 1))
            throw new InvalidDataException("The vest plan allocated the same ID more than once in one archive container.");
        return new PreparedVestPlan(plan, characterArchivePath, entry.Index, entry.Name,
            localIndexes[CharacterItemAddPipeline.MediumVestConfigurationTableId]);
    }

    static List<PendingChange> ToPendingChanges(PreparedVestPlan prepared)
    {
        CharacterItemAddPlan plan = prepared.Plan;
        var changes = new List<PendingChange>();
        changes.AddRange(plan.LocalChanges.Select(change => new PendingChange(
            prepared.ArchivePath, prepared.EntryIndex, prepared.EntryName,
            change.ResourceIndex, change.Name, change.Data,
            ResourceClassHash: BuildTable.ClassHash)));
        changes.Add(new PendingChange(prepared.ArchivePath, prepared.EntryIndex,
            prepared.EntryName, prepared.ConfigurationResourceIndex, "MediumVestCFG",
            plan.BuildTableData, ResourceClassHash: BuildTable.ClassHash));
        changes.AddRange(plan.DatabaseChanges.Select(change => new PendingChange(
            change.ArchivePath, change.EntryIndex, change.EntryName,
            change.ResourceIndex, change.ResourceName, change.Data)));
        changes.AddRange(plan.ResourceAdditions.Select(addition => new PendingChange(
            addition.ArchivePath, addition.EntryIndex, addition.EntryName, -1,
            addition.ResourceName, addition.Data,
            new PendingResourceAddition(addition.ResourceId, addition.ClassHash,
                addition.Header, addition.InsertAfterResourceId),
            ResourceClassHash: addition.ClassHash)));
        changes.AddRange(plan.EntryAdditions.Select(addition => new PendingChange(
            addition.ArchivePath, -1, addition.EntryName, -1, addition.EntryName,
            addition.Data, EntryAddition: new PendingForgeEntryAddition(addition.EntryId,
                addition.EntryName, addition.Extension, addition.InfoTemplate,
                addition.PrefetchBlock))));
        return changes;
    }

    static CharacterVestAddValidationResult Describe(CharacterItemAddPlan plan) => new(
            BuildTable.Read(plan.BuildTableData).RowCount,
            plan.LocalChanges.Count, plan.DatabaseChanges.Count, plan.ResourceAdditions.Count,
            plan.EntryAdditions.Count, plan.Metadata.BuildTag, plan.Metadata.RecordId);

    sealed record PreparedVestPlan(
        CharacterItemAddPlan Plan,
        string ArchivePath,
        int EntryIndex,
        string EntryName,
        int ConfigurationResourceIndex);

    sealed record InstalledVestBranch(
        IReadOnlyList<string> ArchivePaths,
        ArmoryIndex Index,
        ArmoryIndex.IndexedResource InstalledRecord,
        uint TemplateGameplayTag,
        uint GameplayTag,
        ForgeEntry SourceEntry,
        DataFile SourceFile,
        BuildTableRow TemplateRow,
        BuildTableRow InstalledRow);
}
