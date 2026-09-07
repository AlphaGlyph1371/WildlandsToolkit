using System.Buffers.Binary;
using System.IO;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

internal sealed record CharacterItemAddPlan(
    byte[] BuildTableData,
    IReadOnlyList<BuildTableResourceChange> LocalChanges,
    BuildTableOptionMetadata Metadata,
    IReadOnlyList<ArmoryDatabaseResourceChange> DatabaseChanges,
    IReadOnlyList<ArmoryArchiveResourceAddition> ResourceAdditions,
    IReadOnlyList<ArmoryArchiveEntryAddition> EntryAdditions,
    IReadOnlyList<Resource> PreviewResources,
    IReadOnlyDictionary<ulong, ulong> ModelSelectors);

/// <summary>
/// Adds a character-customization choice without taking over an installed choice. Character
/// choices are a small BuildTable branch (configuration, gameplay tag and gender model table)
/// plus registrations in Game Bootstrap Settings. This is deliberately separate from the
/// weapon attachment pipeline: the two graphs have different registries and invariants.
/// </summary>
internal static class CharacterItemAddPipeline
{
    const uint StoreObjectInfoClassHash = 0x36C9CB38;
    static readonly uint EntityBuilderClassHash = ResourceTypes.Crc32("EntityBuilder");

    public static CharacterItemAddPlan BuildVest(
        ArmoryIndex index,
        BuildTableAsset configurationTable,
        int templateRowIndex,
        BuildTableOptionMetadata templateMetadata,
        AddAttachmentDraft draft,
        IReadOnlyList<ulong> templateModelSelectorIds,
        string languagePackage,
        IReadOnlyList<string> archivePaths,
        IReadOnlyDictionary<ulong, Resource> localResources,
        IReadOnlyDictionary<ulong, int> localResourceIndexes,
        string localArchivePath,
        int localEntryIndex,
        string localEntryName,
        IReadOnlyDictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]>
            preparedResourceData,
        IReadOnlyCollection<ulong> preparedIds)
    {
        ArgumentNullException.ThrowIfNull(index);
        if ((uint)templateRowIndex >= (uint)configurationTable.Rows.Count)
            throw new InvalidOperationException("The selected vest template no longer exists.");
        if (!draft.ImportModel)
            throw new InvalidOperationException(
                "A new vest currently needs an imported model file. Existing game assets can still be selected directly in the BuildTable editor.");
        if (templateModelSelectorIds.Count != 2)
            throw new InvalidOperationException(
                $"The selected vest must resolve to one male and one female model, but {templateModelSelectorIds.Count} model selectors were found.");
        if (index.DatabaseResources.Any(resource =>
                string.Equals(resource.Name, draft.InternalName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException(
                $"A gameplay resource named {draft.InternalName} already exists.");

        var used64 = index.DatabaseResources.Select(resource => resource.Id)
            .Concat(localResources.Keys)
            .Concat(preparedIds)
            .Where(id => id != 0)
            .ToHashSet();
        var usedStrings = index.LanguagePackages
            .SelectMany(package => index.StringsFor(package).Keys)
            .Where(id => id is > 0 and <= uint.MaxValue)
            .Select(id => (uint)id)
            .ToHashSet();
        usedStrings.UnionWith(preparedIds.Where(id => id is > 0 and <= uint.MaxValue)
            .Select(id => (uint)id));
        var usedTags = localResources.Values
            .Where(resource => resource.ClassHash == BuildTable.ClassHash)
            .SelectMany(resource =>
            {
                BuildTableAsset table = BuildTable.Read(resource.Data);
                return table.Rows.SelectMany(row => row.PossibleTags.Append(row.Tag));
            })
            .ToHashSet();

        uint configurationTag = AttachmentAddPipeline.Allocate32(
            "character-config:" + draft.InternalName, usedTags);
        uint gameplayTag = AttachmentAddPipeline.Allocate32(
            "character-gameplay:" + draft.InternalName, usedTags);
        uint stringId = AttachmentAddPipeline.Allocate32(
            "localized-name:" + draft.InternalName, usedStrings);
        ulong recordId = AttachmentAddPipeline.Allocate64(
            "gameplay-record:" + draft.InternalName, used64);

        IReadOnlySet<ulong> branchIds = FindPrivateBranch(configurationTable.Rows[templateRowIndex],
            templateMetadata.BuildTag, templateModelSelectorIds, localResources);
        if (branchIds.Count < 3)
            throw new InvalidOperationException(
                "The selected vest does not have a complete private BuildTable branch to clone.");

        var entryAdditions = new List<ArmoryArchiveEntryAddition>();
        var previewResources = new List<Resource>();
        var selectorMap = new Dictionary<ulong, ulong>();
        var sharedTextureIds = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        foreach ((ulong selectorId, int indexInPair) in templateModelSelectorIds
                     .Select((id, indexInPair) => (id, indexInPair)))
        {
            AddAttachmentDraft modelDraft = draft with
            {
                InternalName = draft.InternalName + (indexInPair == 0 ? "_Male" : "_Female")
            };
            AttachmentAddPipeline.ModelClonePlan model =
                AttachmentAddPipeline.CloneModelResources(modelDraft, selectorId, archivePaths,
                    localArchivePath, localEntryIndex, used64, sharedTextureIds);
            selectorMap[selectorId] = model.SelectorId;
            entryAdditions.AddRange(model.EntryAdditions);
            previewResources.AddRange(model.Resources);
        }

        var tableIdMap = branchIds.ToDictionary(id => id,
            id => AttachmentAddPipeline.Allocate64(
                $"character-buildtable:{draft.InternalName}:{id:X}", used64));
        var resourceAdditions = new List<ArmoryArchiveResourceAddition>();
        foreach (ulong oldId in branchIds)
        {
            Resource source = localResources[oldId];
            BuildTableAsset clone = BuildTable.Read(source.Data);
            foreach (BuildTableReference reference in clone.References)
            {
                if (reference.Value == oldId)
                    reference.Value = tableIdMap[oldId];
                else if (tableIdMap.TryGetValue(reference.Value, out ulong tableId))
                    reference.Value = tableId;
                else if (selectorMap.TryGetValue(reference.Value, out ulong selectorId))
                    reference.Value = selectorId;
            }
            foreach (BuildTableRow row in clone.Rows.Where(row =>
                         row.Tag == templateMetadata.BuildTag))
                clone.SetRowTag(row.Index, gameplayTag);

            byte[] cloneData = clone.Write();
            BuildTableAsset checkedClone = BuildTable.Read(cloneData);
            if (checkedClone.Id != tableIdMap[oldId]
                || checkedClone.References.Any(reference => reference.Value == oldId)
                || checkedClone.References.Any(reference => selectorMap.ContainsKey(reference.Value)))
                throw new InvalidDataException(
                    $"The cloned character table {source.Name} did not retain its new references.");

            string name = draft.InternalName + "_" + source.Name;
            Resource clonedResource = DataFile.CloneResource(source, tableIdMap[oldId], name,
                cloneData);
            resourceAdditions.Add(new ArmoryArchiveResourceAddition(localArchivePath,
                localEntryIndex, localEntryName, clonedResource.Id, clonedResource.ClassHash,
                clonedResource.Name, clonedResource.Header, clonedResource.Data));
            previewResources.Add(clonedResource);
        }

        byte[] duplicated = configurationTable.DuplicateRow(templateRowIndex);
        BuildTableAsset editedConfiguration = BuildTable.Read(duplicated);
        int newRowIndex = templateRowIndex + 1;
        editedConfiguration.SetRowTag(newRowIndex, configurationTag);
        foreach (BuildTableReference reference in editedConfiguration.Rows[newRowIndex].References)
            if (tableIdMap.TryGetValue(reference.Value, out ulong newId))
                reference.Value = newId;
        byte[] configurationData = editedConfiguration.Write();
        configurationData = ReplaceTagInRow(configurationData, newRowIndex,
            templateMetadata.BuildTag, gameplayTag);
        BuildTableAsset checkedConfiguration = BuildTable.Read(configurationData);
        if (checkedConfiguration.RowCount != configurationTable.RowCount + 1
            || checkedConfiguration.Rows[newRowIndex].Tag != configurationTag
            || !checkedConfiguration.Rows[newRowIndex].PossibleTags.Contains(gameplayTag)
            || checkedConfiguration.Rows[newRowIndex].References
                .Any(reference => branchIds.Contains(reference.Value)))
            throw new InvalidDataException(
                "The new vest configuration row did not read back exactly.");

        var localChanges = PropagateTags(configurationTable.Id, branchIds,
            configurationTable.Rows[templateRowIndex].Tag, configurationTag,
            templateMetadata.BuildTag, gameplayTag, localResources, localResourceIndexes);

        var recordSource = AttachmentAddPipeline.LoadIndexedResource(index,
            templateMetadata.RecordId);
        byte[] recordData = AttachmentAddPipeline.CloneGameplayRecord(
            recordSource.Resource.Data, templateMetadata.RecordId, recordId, gameplayTag, stringId);
        Resource newRecord = DataFile.CloneResource(recordSource.Resource, recordId,
            draft.InternalName, recordData);
        resourceAdditions.Add(AttachmentAddPipeline.ToAddition(recordSource.Location, newRecord));

        ArmoryIndex.IndexedResource infoSource = index.DatabaseResources.LastOrDefault(resource =>
                resource.ClassHash == StoreObjectInfoClassHash
                && AttachmentAddPipeline.Names(resource.Data, templateMetadata.RecordId))
            ?? throw new InvalidOperationException(
                "The selected vest has no StoreObjectInfo beside its gameplay record.");
        ulong infoId = AttachmentAddPipeline.Allocate64(
            "store-object-info:" + draft.InternalName, used64);
        var infoLoaded = AttachmentAddPipeline.LoadIndexedResource(index, infoSource.Id);
        byte[] infoData = AttachmentAddPipeline.CloneRecordInfo(infoLoaded.Resource.Data,
            infoSource.Id, infoId, templateMetadata.RecordId, recordId, draft.InternalName);
        resourceAdditions.Add(AttachmentAddPipeline.ToAddition(infoLoaded.Location,
            DataFile.CloneResource(infoLoaded.Resource, infoId, draft.InternalName, infoData)));

        var databaseChanges = new List<ArmoryDatabaseResourceChange>();
        var packages = index.LanguagePackages.Where(package =>
                string.Equals(package, "English(US)", StringComparison.OrdinalIgnoreCase)
                || string.Equals(package, languagePackage, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var localizationTargets = AttachmentAddPipeline.FindLocalizationTargets(packages,
            archivePaths, preparedResourceData);
        if (localizationTargets.Count == 0)
            throw new InvalidOperationException("No writable installed localization package was found.");
        foreach (AttachmentAddPipeline.LocalizationTarget target in localizationTargets)
        {
            byte[] localizationData = LocalizationPackage.AddOrReplaceString(
                target.Resource.Data, stringId, draft.DisplayName);
            databaseChanges.Add(new ArmoryDatabaseResourceChange(target.Location.ArchivePath,
                target.Location.EntryIndex, target.Location.EntryName, target.ResourceIndex,
                target.Resource.Name, localizationData));
        }

        AddRequiredRegistry(databaseChanges,
            GunsmithAvailability.FindStoreRegistries(index, infoSource.Id), infoSource.Id, infoId,
            "The selected vest is not present in StoreDBEntry_default.");
        AddRequiredRegistry(databaseChanges,
            GunsmithAvailability.FindDatabaseContainerRegistries(index,
                templateMetadata.RecordId), templateMetadata.RecordId, recordId,
            "The selected vest is not present in a gameplay database container.");
        AddRequiredRegistry(databaseChanges,
            GunsmithAvailability.FindNamedRegistries(index, templateMetadata.RecordId,
                "CharacterSmithDBEntry_default"), templateMetadata.RecordId, recordId,
            "The selected vest is not present in CharacterSmithDBEntry_default.");
        AddRequiredRegistry(databaseChanges,
            GunsmithAvailability.FindNamedRegistries(index, templateMetadata.RecordId, "VESTS"),
            templateMetadata.RecordId, recordId,
            "The selected vest is not present in the VESTS registry.");
        AddRequiredRegistry(databaseChanges,
            GunsmithAvailability.FindUnlockRegistries(index, templateMetadata.RecordId),
            templateMetadata.RecordId, recordId,
            "The selected vest is not present in DBUnlockables_default.");

        foreach (GunsmithAvailabilityList registry in
                 GunsmithAvailability.FindLootRegistries(index, templateMetadata.RecordId))
        {
            var order = registry.RecordIds.ToList();
            int position = order.IndexOf(templateMetadata.RecordId);
            order.Insert(position >= 0 ? position + 1 : order.Count, recordId);
            databaseChanges.AddRange(GunsmithAvailability.Rewrite([registry],
                new HashSet<ulong>(), new HashSet<ulong> { recordId }, order));
        }
        databaseChanges.AddRange(AttachmentAddPipeline.BuildTagColumnMapChanges(index,
            configurationTable.Rows[templateRowIndex].Tag, configurationTag));

        var metadata = new BuildTableOptionMetadata(gameplayTag, stringId, draft.DisplayName,
            null, templateMetadata.DescriptionStringId, templateMetadata.Description, recordId,
            draft.InternalName, templateMetadata.RecordClassHash);
        return new CharacterItemAddPlan(configurationData, localChanges, metadata,
            MergeChanges(databaseChanges), resourceAdditions, entryAdditions,
            previewResources, selectorMap);
    }

    static IReadOnlySet<ulong> FindPrivateBranch(BuildTableRow root,
        uint gameplayTag, IReadOnlyCollection<ulong> modelSelectorIds,
        IReadOnlyDictionary<ulong, Resource> localResources)
    {
        var memo = new Dictionary<ulong, bool>();
        var branch = new HashSet<ulong>();
        bool ReachesPrivateData(ulong id, HashSet<ulong> active)
        {
            if (memo.TryGetValue(id, out bool known))
                return known;
            if (!active.Add(id) || !localResources.TryGetValue(id, out Resource? resource)
                || resource.ClassHash != BuildTable.ClassHash)
                return false;
            BuildTableAsset table = BuildTable.Read(resource.Data);
            bool reaches = table.Rows.Any(row => row.Tag == gameplayTag)
                || table.References.Any(reference => modelSelectorIds.Contains(reference.Value));
            foreach (ulong child in table.References
                         .Where(reference => reference.Value != id
                             && localResources.TryGetValue(reference.Value, out Resource? nested)
                             && nested.ClassHash == BuildTable.ClassHash)
                         .Select(reference => reference.Value).Distinct())
                reaches |= ReachesPrivateData(child, active);
            active.Remove(id);
            memo[id] = reaches;
            if (reaches)
                branch.Add(id);
            return reaches;
        }

        foreach (ulong id in root.References.Select(reference => reference.Value).Distinct())
            ReachesPrivateData(id, []);
        return branch;
    }

    static byte[] ReplaceTagInRow(byte[] source, int rowIndex, uint oldTag, uint newTag)
    {
        BuildTableAsset table = BuildTable.Read(source);
        BuildTableRow row = table.Rows[rowIndex];
        byte[] result = (byte[])source.Clone();
        int changed = 0;
        for (int offset = row.Offset; offset <= row.Offset + row.Length - sizeof(uint); offset++)
        {
            if (offset == row.TagOffset
                || BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(offset)) != oldTag)
                continue;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset), newTag);
            changed++;
            offset += sizeof(uint) - 1;
        }
        if (changed == 0)
            throw new InvalidDataException(
                "The copied vest row does not carry its gameplay BuildTag.");
        return result;
    }

    static IReadOnlyList<BuildTableResourceChange> PropagateTags(ulong configurationId,
        IReadOnlySet<ulong> clonedBranchIds, uint oldConfigurationTag, uint newConfigurationTag,
        uint oldGameplayTag, uint newGameplayTag,
        IReadOnlyDictionary<ulong, Resource> resources,
        IReadOnlyDictionary<ulong, int> resourceIndexes)
    {
        var changes = new List<BuildTableResourceChange>();
        int configurationParents = 0;
        int gameplayParents = 0;
        foreach (Resource resource in resources.Values)
        {
            if (resource.Id == configurationId || clonedBranchIds.Contains(resource.Id)
                || resource.ClassHash != BuildTable.ClassHash
                    && resource.ClassHash != EntityBuilderClassHash)
                continue;
            byte[] data = resource.Data;
            bool changed = false;
            if (AttachmentAddPipeline.TryAddBuildTag(data, oldConfigurationTag,
                    newConfigurationTag, out byte[] withConfiguration))
            {
                data = withConfiguration;
                configurationParents++;
                changed = true;
            }
            if (AttachmentAddPipeline.TryAddBuildTag(data, oldGameplayTag,
                    newGameplayTag, out byte[] withGameplay))
            {
                data = withGameplay;
                gameplayParents++;
                changed = true;
            }
            if (!changed)
                continue;
            if (!resourceIndexes.TryGetValue(resource.Id, out int resourceIndex))
                throw new InvalidOperationException(
                    $"{resource.Name} has no stable index in the character container.");
            changes.Add(new BuildTableResourceChange(resourceIndex, resource.Name, data));
        }
        if (configurationParents == 0 || gameplayParents == 0)
            throw new InvalidOperationException(
                "The selected vest tags could not be propagated through the character compatibility tables.");
        return changes;
    }

    static void AddRequiredRegistry(List<ArmoryDatabaseResourceChange> destination,
        IReadOnlyList<GunsmithAvailabilityList> registries, ulong templateId, ulong newId,
        string missingMessage)
    {
        if (registries.Count == 0)
            throw new InvalidOperationException(missingMessage);
        destination.AddRange(GunsmithAvailability.InsertAfterTemplates(
            registries.Select(registry => (registry, templateId, newId)).ToList()));
    }

    static IReadOnlyList<ArmoryDatabaseResourceChange> MergeChanges(
        IReadOnlyList<ArmoryDatabaseResourceChange> changes)
    {
        // Separate helpers can touch different lists in the same database resource. Rebuild
        // such resources in one deterministic sequence instead of letting the last change win.
        var result = new List<ArmoryDatabaseResourceChange>();
        foreach (var group in changes.GroupBy(change =>
                     (Path.GetFullPath(change.ArchivePath).ToUpperInvariant(), change.EntryIndex,
                         change.ResourceIndex)))
        {
            var items = group.ToList();
            if (items.Count == 1)
            {
                result.Add(items[0]);
                continue;
            }
            // Multiple independently rewritten snapshots cannot be combined byte-wise. This is
            // rejected rather than silently dropping one registration.
            throw new InvalidOperationException(
                $"Two character registrations would edit {items[0].ResourceName} independently.");
        }
        return result;
    }
}
