using System.Buffers.Binary;
using System.IO;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Materials;
using Wildlands.Formats.Models;
using Wildlands.Formats.Textures;

namespace Wildlands.Toolkit;

internal sealed record AttachmentAddPlan(
    byte[] BuildTableData,
    IReadOnlyList<BuildTableResourceChange> LocalChanges,
    BuildTableOptionMetadata Metadata,
    bool AddedToGunsmith,
    IReadOnlyList<string> OwnerNames,
    IReadOnlyList<ArmoryDatabaseResourceChange> DatabaseChanges,
    IReadOnlyList<ArmoryArchiveResourceAddition> ResourceAdditions,
    IReadOnlyList<ArmoryArchiveEntryAddition> EntryAdditions,
    IReadOnlyList<Resource> PreviewResources,
    ulong ModelSelectorId);

internal static class AttachmentAddPipeline
{
    const uint BuildTagMarker = 0xB332698E;
    const uint LocalizedValueMarker = 0x81A7045D;
    const uint DynamicHandleType = 18u << 16;
    const uint DynamicObjectPointerType = 20u << 16;
    const uint StoreObjectInfoClass = 0x36C9CB38;
    const uint BuildTagColumnMapClass = 0xFFC5A970;
    const uint BuildTagColumnEntryMarker = 0xA31AA51D;

    public static AttachmentAddPlan Build(
        ArmoryIndex index,
        BuildTableAsset table,
        int templateRowIndex,
        BuildTableOptionMetadata templateMetadata,
        AddAttachmentDraft draft,
        BuildTableTarget modelSource,
        ulong templateModelSelectorId,
        IReadOnlyList<GunsmithAvailabilityList> gunsmithLists,
        IReadOnlyList<ulong> canonicalRecordOrder,
        IReadOnlyCollection<ulong> ownerRecordIds,
        string languagePackage,
        IReadOnlyList<string> archivePaths,
        IReadOnlyDictionary<ulong, Resource> localResources,
        IReadOnlyDictionary<ulong, int> localResourceIndexes,
        string localArchivePath,
        int localEntryIndex,
        string localEntryName,
        IReadOnlyDictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]> preparedResourceData,
        IReadOnlyCollection<ulong> preparedIds)
    {
        ArgumentNullException.ThrowIfNull(index);
        if ((uint)templateRowIndex >= (uint)table.Rows.Count)
            throw new InvalidOperationException("The selected attachment template no longer exists.");
        if (index.DatabaseResources.Any(resource => string.Equals(resource.Name, draft.InternalName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"A gameplay resource named {draft.InternalName} already exists.");

        var used64 = index.DatabaseResources.Select(resource => resource.Id)
            .Concat(table.References.Select(reference => reference.Value))
            .Concat(localResources.Keys)
            .Where(id => id != 0)
            .ToHashSet();
        used64.UnionWith(preparedIds);
        var usedStrings = index.LanguagePackages
            .SelectMany(package => index.StringsFor(package).Keys)
            .Where(id => id <= uint.MaxValue)
            .Select(id => (uint)id)
            .ToHashSet();
        usedStrings.UnionWith(preparedIds.Where(id => id is > 0 and <= uint.MaxValue)
            .Select(id => (uint)id));
        var usedBuildTags = index.DatabaseResources
            .Select(resource => TryBuildTag(resource.Data))
            .Where(tag => tag != 0)
            .Concat(table.Rows.Select(row => row.Tag))
            .ToHashSet();

        AttachmentCategory category = draft.ReplaceTemplate || draft.ReuseTemplateCategory || draft.CreateUniqueBuildTag
            ? new AttachmentCategory(templateMetadata.BuildTag, templateMetadata.RecordId, "template category", 1)
            : ResolveFreeCategory(index, table, templateMetadata, ownerRecordIds);
        uint buildTag = draft.CreateUniqueBuildTag
            ? Allocate32("build-tag:" + draft.InternalName, usedBuildTags)
            : category.Tag;
        uint stringId = Allocate32("localized-name:" + draft.InternalName, usedStrings);
        ulong recordId = Allocate64("gameplay-record:" + draft.InternalName, used64);

        var resourceAdditions = new List<ArmoryArchiveResourceAddition>();
        var entryAdditions = new List<ArmoryArchiveEntryAddition>();
        var previewResources = new List<Resource>();
        ulong modelSelectorId = modelSource.Id;
        ulong gunsmithModelSelectorId = 0;
        var sharedTextureIds = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        if (draft.ImportModel)
        {
            var modelPlan = CloneModelResources(draft, modelSource.Id, archivePaths, localArchivePath, localEntryIndex, used64, sharedTextureIds);
            modelSelectorId = modelPlan.SelectorId;
            entryAdditions.AddRange(modelPlan.EntryAdditions);
            previewResources.AddRange(modelPlan.Resources);

            if (!draft.ReplaceTemplate && !draft.ReuseTemplateCategory && draft.AddToGunsmith && localResources.TryGetValue(table.Id, out Resource? localLeafTable))
            {
                ulong gunsmithTemplateModelId = FindGunsmithPreviewTemplateModel(archivePaths, localEntryName, localLeafTable.Name, templateMetadata.BuildTag, preparedResourceData);
                if (gunsmithTemplateModelId != 0)
                {
                    var gunsmithDraft = draft with
                    {
                        InternalName = draft.InternalName + "_Gunsmith"
                    };
                    var gunsmithModelPlan = CloneModelResources(gunsmithDraft, gunsmithTemplateModelId, archivePaths, localArchivePath, localEntryIndex, used64, sharedTextureIds);
                    gunsmithModelSelectorId = gunsmithModelPlan.SelectorId;
                    entryAdditions.AddRange(gunsmithModelPlan.EntryAdditions);
                    previewResources.AddRange(gunsmithModelPlan.Resources);
                }
            }
        }
        else if (LoadModelFiles(archivePaths, new HashSet<ulong> { modelSelectorId }).Count == 0)
            throw new InvalidOperationException($"{modelSource.Name} is not the first resource of a Forge entry.");

        if (!draft.ReplaceTemplate)
        {
            var componentReferences = table.Rows[templateRowIndex].References
                .Where(reference => reference.Kind == BuildTableReferenceKind.FileReference && localResources.TryGetValue(reference.Value, out Resource? resource) && resource.ClassHash == Skeleton.ClassHash)
                .ToList();
            if (componentReferences.Count > 1)
                throw new InvalidOperationException("The attachment template names more than one local component Skeleton.");
        }

        if (draft.ReplaceTemplate && modelSelectorId != templateModelSelectorId)
            throw new InvalidOperationException("The replacement control must use the copied attachment's existing model asset.");

        byte[] tableData = draft.ReplaceTemplate
            ? table.Write()
            : AddOption(table, templateRowIndex, buildTag, templateModelSelectorId, modelSelectorId, 0, 0);

        var localChanges = draft.ReplaceTemplate || draft.ReuseTemplateCategory
            ? []
            : BuildParentTagChanges(table.Id, templateMetadata.BuildTag, buildTag, localResources, localResourceIndexes);

        var recordSource = LoadIndexedResource(index, category.ExemplarRecordId);
        byte[] recordData = CloneGameplayRecord(recordSource.Resource.Data, category.ExemplarRecordId, recordId, buildTag, stringId);
        string recordName = draft.InternalName;
        Resource newRecord = DataFile.CloneResource(recordSource.Resource, recordId, recordName, recordData);
        resourceAdditions.Add(ToAddition(recordSource.Location, newRecord));

        var infoSource = index.DatabaseResources.LastOrDefault(resource => resource.ClassHash == StoreObjectInfoClass && Names(resource.Data, category.ExemplarRecordId))
            ?? throw new InvalidOperationException("The chosen attachment category has no StoreObjectInfo beside its record.");
        ulong infoId = Allocate64("store-object-info:" + draft.InternalName, used64);
        var infoLoaded = LoadIndexedResource(index, infoSource.Id);
        byte[] infoData = CloneRecordInfo(infoLoaded.Resource.Data, infoSource.Id, infoId, category.ExemplarRecordId, recordId, draft.InternalName);
        resourceAdditions.Add(ToAddition(infoLoaded.Location, DataFile.CloneResource(infoLoaded.Resource, infoId, recordName, infoData)));

        var databaseChanges = new List<ArmoryDatabaseResourceChange>();
        var storeInsertions = new List<(GunsmithAvailabilityList List, ulong TemplateId, ulong NewId)>();
        var storeRegistries = GunsmithAvailability.FindStoreRegistries(index, infoSource.Id);
        if (storeRegistries.Count == 0)
            throw new InvalidOperationException("The template StoreObjectInfo is not listed in a confirmed StoreDBEntry registry.");
        foreach (GunsmithAvailabilityList registry in storeRegistries)
            storeInsertions.Add((registry, infoSource.Id, infoId));

        var wantedPackages = index.LanguagePackages.Where(package => string.Equals(package, "English(US)", StringComparison.OrdinalIgnoreCase) || string.Equals(package, languagePackage, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var localizationTargets = FindLocalizationTargets(wantedPackages, archivePaths, preparedResourceData);
        if (localizationTargets.Count == 0)
            throw new InvalidOperationException("No writable installed localization package was found.");
        foreach (var target in localizationTargets)
        {
            byte[] localizationData = LocalizationPackage.AddOrReplaceString(target.Resource.Data, stringId, draft.DisplayName);
            databaseChanges.Add(new ArmoryDatabaseResourceChange(target.Location.ArchivePath, target.Location.EntryIndex, target.Location.EntryName, target.ResourceIndex, target.Resource.Name, localizationData));
        }

        var ownerNames = gunsmithLists.Select(list => list.OwnerName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var databaseContainerInsertions = new List<(GunsmithAvailabilityList List, ulong TemplateId, ulong NewId)>();
        var databaseContainerRegistries = GunsmithAvailability.FindDatabaseContainerRegistries(index, category.ExemplarRecordId);
        if (databaseContainerRegistries.Count == 0)
            throw new InvalidOperationException("The selected template is not present in a confirmed gameplay database container.");
        foreach (GunsmithAvailabilityList registry in databaseContainerRegistries)
            databaseContainerInsertions.Add((registry, category.ExemplarRecordId, recordId));

        var attachmentTypeRegistries = GunsmithAvailability.FindAttachmentTypeRegistries(index, category.ExemplarRecordId, templateMetadata.RecordClassHash);
        if (attachmentTypeRegistries.Count == 0)
            throw new InvalidOperationException("No unambiguous WPN_AT attachment registry was found for the selected template.");

        var unlockInsertions = new List<(GunsmithAvailabilityList List, ulong TemplateId, ulong NewId)>();
        var unlockRegistries = GunsmithAvailability.FindUnlockRegistries(index, category.ExemplarRecordId);
        if (unlockRegistries.Count == 0)
            throw new InvalidOperationException("The selected template is not present in a confirmed DBUnlockables_default registry.");
        foreach (GunsmithAvailabilityList registry in unlockRegistries)
            unlockInsertions.Add((registry, category.ExemplarRecordId, recordId));

        if (draft.CreateUniqueBuildTag)
        {
            if (attachmentTypeRegistries.Count != 1)
                throw new InvalidOperationException("The unique BuildTag experiment needs exactly one WPN_AT attachment-type template.");

            GunsmithAvailabilityList typeRegistry = attachmentTypeRegistries[0];
            var typeLoaded = LoadIndexedResource(index, typeRegistry.Owner.Id);
            ulong typeId = Allocate64("attachment-type:" + draft.InternalName, used64);
            string typeName = typeRegistry.Owner.Name + "_" + draft.InternalName;
            byte[] typeData = CloneAttachmentTypeRegistry(typeRegistry, typeId, category.Tag, buildTag, recordId);
            resourceAdditions.Add(ToAddition(typeLoaded.Location, DataFile.CloneResource(typeLoaded.Resource, typeId, typeName, typeData)));

            var typeInfoSource = index.DatabaseResources.LastOrDefault(resource => resource.ClassHash == StoreObjectInfoClass && Names(resource.Data, typeRegistry.Owner.Id))
                ?? throw new InvalidOperationException("The WPN_AT attachment-type template has no StoreObjectInfo beside it.");
            ulong typeInfoId = Allocate64("attachment-type-info:" + draft.InternalName, used64);
            var typeInfoLoaded = LoadIndexedResource(index, typeInfoSource.Id);
            byte[] typeInfoData = CloneRecordInfo(typeInfoLoaded.Resource.Data, typeInfoSource.Id, typeInfoId, typeRegistry.Owner.Id, typeId, typeName);
            resourceAdditions.Add(ToAddition(typeInfoLoaded.Location, DataFile.CloneResource(typeInfoLoaded.Resource, typeInfoId, typeName, typeInfoData)));

            var typeStoreRegistries = GunsmithAvailability.FindStoreRegistries(index, typeInfoSource.Id);
            if (typeStoreRegistries.Count == 0)
                throw new InvalidOperationException("The WPN_AT StoreObjectInfo is not listed in a confirmed StoreDBEntry registry.");
            foreach (GunsmithAvailabilityList registry in typeStoreRegistries)
                storeInsertions.Add((registry, typeInfoSource.Id, typeInfoId));

            var typeDatabaseContainers = GunsmithAvailability.FindDatabaseContainerRegistries(index, typeRegistry.Owner.Id);
            if (typeDatabaseContainers.Count == 0)
                throw new InvalidOperationException("The WPN_AT template is not present in a confirmed gameplay database container.");
            foreach (GunsmithAvailabilityList registry in typeDatabaseContainers)
                databaseContainerInsertions.Add((registry, typeRegistry.Owner.Id, typeId));

            var typeUnlockRegistries = GunsmithAvailability.FindUnlockRegistries(index, typeRegistry.Owner.Id);
            if (typeUnlockRegistries.Count == 0)
                throw new InvalidOperationException("The WPN_AT template is not present in DBUnlockables_default.");
            foreach (GunsmithAvailabilityList registry in typeUnlockRegistries)
                unlockInsertions.Add((registry, typeRegistry.Owner.Id, typeId));
        }
        else
        {
            foreach (GunsmithAvailabilityList registry in attachmentTypeRegistries)
            {
                var registryOrder = registry.RecordIds.ToList();
                int templatePosition = registryOrder.IndexOf(category.ExemplarRecordId);
                registryOrder.Insert(templatePosition >= 0 ? templatePosition + 1 : registryOrder.Count, recordId);
                databaseChanges.AddRange(GunsmithAvailability.Rewrite([registry], new HashSet<ulong>(), new HashSet<ulong> { recordId }, registryOrder));
            }
        }

        databaseChanges.AddRange(GunsmithAvailability.InsertAfterTemplates(storeInsertions));
        databaseChanges.AddRange(GunsmithAvailability.InsertAfterTemplates(databaseContainerInsertions));
        if (draft.CreateUniqueBuildTag)
            databaseChanges.AddRange(BuildTagColumnMapChanges(index, category.Tag, buildTag));

        var lootRegistries = GunsmithAvailability.FindLootRegistries(index, category.ExemplarRecordId);
        foreach (GunsmithAvailabilityList registry in lootRegistries)
        {
            var registryOrder = registry.RecordIds.ToList();
            int templatePosition = registryOrder.IndexOf(category.ExemplarRecordId);
            registryOrder.Insert(templatePosition >= 0 ? templatePosition + 1 : registryOrder.Count, recordId);
            databaseChanges.AddRange(GunsmithAvailability.Rewrite([registry], new HashSet<ulong>(), new HashSet<ulong> { recordId }, registryOrder));
        }
        databaseChanges.AddRange(GunsmithAvailability.InsertAfterTemplates(unlockInsertions));

        if (draft.AddToGunsmith)
        {
            if (gunsmithLists.Count == 0)
                throw new InvalidOperationException("No confirmed Gunsmith owner list was found for the selected slot.");
            var canonical = canonicalRecordOrder.Distinct().ToList();
            int templatePosition = canonical.IndexOf(templateMetadata.RecordId);
            var removed = new HashSet<ulong>();
            if (draft.ReplaceTemplate)
            {
                if (templatePosition < 0)
                    throw new InvalidOperationException("The copied attachment is not present in this weapon's Gunsmith list.");
                canonical[templatePosition] = recordId;
                removed.Add(templateMetadata.RecordId);
            }
            else
                canonical.Insert(templatePosition >= 0 ? templatePosition + 1 : canonical.Count, recordId);
            databaseChanges.AddRange(GunsmithAvailability.Rewrite(gunsmithLists, removed, new HashSet<ulong> { recordId }, canonical));
        }

        if (!draft.ReplaceTemplate)
        {
            var mirrored = MirrorContainerCopies(archivePaths, localArchivePath, localEntryIndex, table.Id, templateMetadata.BuildTag, buildTag, templateModelSelectorId, modelSelectorId, 0, 0, "", draft.ReuseTemplateCategory, preparedResourceData);
            databaseChanges.AddRange(mirrored.Changes);
            resourceAdditions.AddRange(mirrored.Additions);
            if (!draft.ReuseTemplateCategory)
            {
                databaseChanges.AddRange(MirrorSiblingVariants(archivePaths, localEntryName, templateMetadata.BuildTag, buildTag, templateModelSelectorId, modelSelectorId, 0, 0, preparedResourceData));
                if (localResources.TryGetValue(table.Id, out Resource? leafTableResource))
                    databaseChanges.AddRange(MirrorGunsmithPreviewFamily(archivePaths, localEntryName, leafTableResource.Name, templateMetadata.BuildTag, buildTag, gunsmithModelSelectorId, preparedResourceData));
            }
        }

        var metadata = new BuildTableOptionMetadata(buildTag, stringId, draft.DisplayName, null, templateMetadata.DescriptionStringId, templateMetadata.Description, recordId, recordName, templateMetadata.RecordClassHash);
        return new AttachmentAddPlan(tableData, localChanges, metadata, draft.AddToGunsmith, ownerNames, databaseChanges, resourceAdditions, entryAdditions, previewResources, modelSelectorId);
    }

    static AttachmentCategory ResolveFreeCategory(ArmoryIndex index, BuildTableAsset table, BuildTableOptionMetadata templateMetadata, IReadOnlyCollection<ulong> ownerRecordIds)
    {
        var effective = index.DatabaseResources
            .GroupBy(resource => resource.Id)
            .ToDictionary(group => group.Key, group => group.Last());

        var records = effective.Values
            .Where(resource => resource.ClassHash == templateMetadata.RecordClassHash)
            .ToDictionary(resource => resource.Id, resource => TryBuildTag(resource.Data));

        var templateRegistries = GunsmithAvailability.FindAttachmentTypeRegistries(index,
            templateMetadata.RecordId, templateMetadata.RecordClassHash);
        if (templateRegistries.Count != 1)
            throw new InvalidOperationException("The selected template does not belong to exactly one confirmed attachment registry, so the categories of this slot cannot be read.");
        GunsmithAvailabilityList templateRegistry = templateRegistries[0];
        uint registryClass = templateRegistry.Owner.ClassHash;

        uint ownerClass = ownerRecordIds
            .Select(id => effective.TryGetValue(id, out var owner) ? owner.ClassHash : 0u)
            .FirstOrDefault(hash => hash != 0);
        if (ownerClass == 0)
            throw new InvalidOperationException("The gameplay owners of this BuildTable are not resolved, so the weapon family is unknown.");

        var owners = effective.Values
            .Where(resource => resource.ClassHash == ownerClass)
            .Select(resource => (resource.Id, Referenced: ReferencedIds(resource.Data, resource.Id)))
            .ToList();
        var templateOwners = owners
            .Where(owner => templateRegistry.RecordIds.Any(owner.Referenced.Contains))
            .Select(owner => owner.Id)
            .ToHashSet();
        var usedTags = table.Rows.Select(row => row.Tag).ToHashSet();

        var recordIds = records.Keys.ToHashSet();
        var membersPerRegistry = effective.Values
            .Where(resource => resource.ClassHash == registryClass)
            .ToDictionary(resource => resource.Id, resource => MemberRecords(resource.Data, recordIds));
        var registryCount = membersPerRegistry.Values.SelectMany(members => members)
            .GroupBy(id => id)
            .ToDictionary(group => group.Key, group => group.Count());

        var candidates = new List<AttachmentCategory>();
        foreach (var registry in effective.Values.Where(resource => resource.ClassHash == registryClass))
        {
            if (registry.Id == templateRegistry.Owner.Id)
                continue;

            var members = membersPerRegistry[registry.Id];
            if (members.Count == 0)
                continue;
            var tags = members.Select(id => records[id]).Distinct().ToList();
            if (tags.Count != 1 || tags[0] == 0 || usedTags.Contains(tags[0]))
                continue;

            int shared = owners.Count(owner => templateOwners.Contains(owner.Id) && members.Any(owner.Referenced.Contains));
            if (shared == 0)
                continue;

            var matchingExemplars = members
                .Where(id => registryCount[id] == 1 && CategoryNeutralName(effective[id].Name) == CategoryNeutralName(templateMetadata.RecordName))
                .ToList();
            ulong exemplar = matchingExemplars.Count == 1 ? matchingExemplars[0] : 0;
            if (exemplar == 0)
                continue;
            candidates.Add(new AttachmentCategory(tags[0], exemplar, registry.Name, shared));
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException($"{templateRegistry.OwnerName} offers no attachment category that this slot does not already use.");

        var ordered = candidates.OrderByDescending(candidate => candidate.SharedOwners).ToList();
        if (ordered.Count > 1 && ordered[0].SharedOwners == ordered[1].SharedOwners)
            throw new InvalidOperationException("Two attachment categories fit this slot equally well (" + string.Join(", ", ordered.Take(2).Select(candidate => candidate.RegistryName)) + "), so the choice is not unambiguous.");
        return ordered[0];
    }

    static string CategoryNeutralName(string name)
    {
        string neutral = name
            .Replace("Short", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Medium", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Long", "", StringComparison.OrdinalIgnoreCase);
        return new string(neutral.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }

    static List<ulong> MemberRecords(byte[] registry, IReadOnlySet<ulong> recordIds)
    {
        var members = new List<ulong>();
        var seen = new HashSet<ulong>();
        for (int offset = 0; offset + sizeof(ulong) <= registry.Length; offset++)
        {
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(registry.AsSpan(offset));
            if (recordIds.Contains(id) && seen.Add(id))
                members.Add(id);
        }
        return members;
    }

    static byte[] AddOption(BuildTableAsset table, int templateRowIndex, uint newTag, ulong templateModelSelectorId, ulong modelSelectorId, ulong templateComponentId, ulong componentId)
    {
        byte[] duplicated = table.DuplicateRow(templateRowIndex);
        var editedTable = BuildTable.Read(duplicated);
        int newRowIndex = templateRowIndex + 1;
        editedTable.SetRowTag(newRowIndex, newTag);
        var templateSelectors = table.Rows[templateRowIndex].References
            .Where(reference => IsModelReference(reference) && reference.Value != 0)
            .ToList();
        var clonedSelectors = editedTable.Rows[newRowIndex].References
            .Where(reference => IsModelReference(reference) && reference.Value != 0)
            .ToList();
        if (templateSelectors.Count == 0 || clonedSelectors.Count != templateSelectors.Count)
            throw new InvalidOperationException("The template row has no stable model-selector handle that can be cloned safely.");
        int selectorIndex = templateSelectors.FindIndex(reference =>
            reference.Value == templateModelSelectorId);
        if (selectorIndex < 0)
            throw new InvalidOperationException("The confirmed template model selector moved before the row could be cloned.");
        BuildTableReference clonedSelector = clonedSelectors[selectorIndex];
        clonedSelector.Value = modelSelectorId;
        if (templateComponentId != 0)
        {
            var clonedComponents = editedTable.Rows[newRowIndex].References
                .Where(reference => reference.Kind == BuildTableReferenceKind.FileReference && reference.Value == templateComponentId)
                .ToList();
            if (clonedComponents.Count != 1 || componentId == 0)
                throw new InvalidOperationException("The cloned attachment row did not retain exactly one component Skeleton reference.");
            clonedComponents[0].Value = componentId;
        }
        byte[] tableData = editedTable.Write();
        SetModelReferenceStorage(tableData, clonedSelector.Offset, BuildTableReferenceKind.Handle);
        var checkedTable = BuildTable.Read(tableData);
        if (checkedTable.RowCount != table.RowCount + 1
            || checkedTable.Rows[newRowIndex].Tag != newTag
            || checkedTable.Rows.Zip(checkedTable.Rows.Skip(1)).Any(pair => pair.First.Id >= pair.Second.Id)
            || checkedTable.Rows[newRowIndex].References.FirstOrDefault(reference => reference.Kind == BuildTableReferenceKind.Handle && reference.ComponentIndex == clonedSelector.ComponentIndex)?.Value != modelSelectorId
            || templateComponentId != 0 && checkedTable.Rows[newRowIndex].References.Count(reference => reference.Kind == BuildTableReferenceKind.FileReference && reference.Value == componentId) != 1)
            throw new InvalidDataException("The new BuildTable option did not read back exactly.");
        return tableData;
    }

    static (List<ArmoryDatabaseResourceChange> Changes,
        List<ArmoryArchiveResourceAddition> Additions) MirrorContainerCopies(
        IReadOnlyList<string> archivePaths, string localArchivePath, int localEntryIndex,
        ulong leafTableId, uint templateTag, uint newTag,
        ulong templateModelSelectorId, ulong modelSelectorId,
        ulong templateComponentId, ulong componentId, string componentName,
        bool reuseTemplateCategory,
        IReadOnlyDictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]> preparedResourceData)
    {
        ulong containerId;
        using (var opened = ForgeArchive.Open(localArchivePath))
            containerId = opened.Entries.FirstOrDefault(entry => entry.Index == localEntryIndex)?.Id
                ?? throw new InvalidOperationException("The opened weapon container moved inside its Forge archive.");

        uint entityBuilderHash = ResourceTypes.Crc32("EntityBuilder");
        var changes = new List<ArmoryDatabaseResourceChange>();
        var additions = new List<ArmoryArchiveResourceAddition>();
        foreach (string path in archivePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(path, localArchivePath, StringComparison.OrdinalIgnoreCase))
                continue;

            using var archive = ForgeArchive.Open(path);
            ForgeEntry? entry = archive.Entries.FirstOrDefault(candidate => candidate.Id == containerId);
            if (entry is null)
                continue;

            using var stream = new MemoryStream(archive.ReadEntry(entry));
            DataFile file = DataFile.Read(stream);
            ApplyPreparedResourceData(file, path, entry.Index, preparedResourceData);
            int leafIndex = file.Resources.FindIndex(resource => resource.Id == leafTableId);
            if (leafIndex < 0)
                throw new InvalidOperationException($"The copy of {entry.Name} in {Path.GetFileName(path)} does not hold the table this option belongs to.");

            var copy = BuildTable.Read(file.Resources[leafIndex].Data);
            if (!reuseTemplateCategory && copy.Rows.Any(row => row.Tag == newTag))
                continue;
            var templateRows = copy.Rows
                .Select((row, index) => (row, index))
                .Where(item => reuseTemplateCategory
                    ? item.row.References.Any(reference => IsModelReference(reference) && reference.Value == templateModelSelectorId)
                    : item.row.Tag == templateTag)
                .ToList();
            if (templateRows.Count > 1)
                throw new InvalidOperationException($"The copy of {file.Resources[leafIndex].Name} in {Path.GetFileName(path)} has more than one matching template row.");
            int templateRow = templateRows.Count == 1 ? templateRows[0].index : -1;
            if (templateRow < 0)
                throw new InvalidOperationException($"The copy of {file.Resources[leafIndex].Name} in {Path.GetFileName(path)} has no row for the selected template.");

            changes.Add(new ArmoryDatabaseResourceChange(path, entry.Index, entry.Name, leafIndex, file.Resources[leafIndex].Name, AddOption(copy, templateRow, newTag, templateModelSelectorId, modelSelectorId, templateComponentId, componentId)));

            if (templateComponentId != 0)
            {
                Resource component = file.Resources.SingleOrDefault(resource => resource.Id == templateComponentId && resource.ClassHash == Skeleton.ClassHash)
                    ?? throw new InvalidOperationException($"The copy of {entry.Name} in {Path.GetFileName(path)} has no component Skeleton 0x{templateComponentId:X}.");
                byte[] componentData = (byte[])component.Data.Clone();
                if (componentData.Length < sizeof(ulong) || BinaryPrimitives.ReadUInt64LittleEndian(componentData) != templateComponentId)
                    throw new InvalidDataException($"{component.Name} does not start with its own resource ID.");
                BinaryPrimitives.WriteUInt64LittleEndian(componentData, componentId);
                Resource clone = DataFile.CloneResource(component, componentId, componentName, componentData);
                additions.Add(ToAddition(new ResourceLocation(path, entry.Index, entry.Name), clone));
            }

            if (reuseTemplateCategory)
                continue;

            int tagged = 0;
            for (int i = 0; i < file.Resources.Count; i++)
            {
                Resource resource = file.Resources[i];
                if (i == leafIndex || resource.ClassHash != BuildTable.ClassHash && resource.ClassHash != entityBuilderHash)
                    continue;
                if (!TryAddBuildTag(resource.Data, templateTag, newTag, out byte[] updated))
                    continue;
                changes.Add(new ArmoryDatabaseResourceChange(path, entry.Index, entry.Name, i, resource.Name, updated));
                tagged++;
            }
            if (tagged == 0)
                throw new InvalidOperationException($"The copy of {entry.Name} in {Path.GetFileName(path)} carries no BuildTags list for the selected template.");
        }
        return (changes, additions);
    }

    static IReadOnlyList<ArmoryDatabaseResourceChange> MirrorSiblingVariants(
        IReadOnlyList<string> archivePaths, string familyName,
        uint templateTag, uint newTag, ulong templateModelSelectorId, ulong modelSelectorId,
        ulong templateComponentId, ulong componentId,
        IReadOnlyDictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]> preparedResourceData)
    {
        var changes = new List<ArmoryDatabaseResourceChange>();
        foreach (string path in archivePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var archive = ForgeArchive.Open(path);
            foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.Name.StartsWith(familyName + "_", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = new MemoryStream(archive.ReadEntry(entry));
                DataFile file = DataFile.Read(stream);
                ApplyPreparedResourceData(file, path, entry.Index, preparedResourceData);
                var resources = file.Resources.ToDictionary(resource => resource.Id);
                var resourceIndexes = file.Resources
                    .Select((resource, index) => (resource.Id, index))
                    .ToDictionary(item => item.Id, item => item.index);

                for (int resourceIndex = 0; resourceIndex < file.Resources.Count; resourceIndex++)
                {
                    Resource resource = file.Resources[resourceIndex];
                    if (resource.ClassHash != BuildTable.ClassHash)
                        continue;

                    BuildTableAsset table = BuildTable.Read(resource.Data);
                    if (table.Rows.Any(row => row.Tag == newTag))
                        continue;
                    var templateRows = table.Rows
                        .Select((row, index) => (row, index))
                        .Where(item => item.row.References.Any(reference => IsModelReference(reference) && reference.Value == templateModelSelectorId))
                        .ToList();
                    if (templateRows.Count == 0)
                        continue;
                    if (templateRows.Count != 1)
                        throw new InvalidOperationException(resource.Name + " contains the template selector in more than one row.");

                    changes.Add(new ArmoryDatabaseResourceChange(path, entry.Index, entry.Name, resourceIndex, resource.Name,
                        AddOption(table, templateRows[0].index, newTag, templateModelSelectorId, modelSelectorId, templateComponentId, componentId)));
                    foreach (BuildTableResourceChange parent in BuildParentTagChanges(resource.Id, templateTag, newTag, resources, resourceIndexes))
                        changes.Add(new ArmoryDatabaseResourceChange(path, entry.Index, entry.Name, parent.ResourceIndex, parent.Name, parent.Data));
                }
            }
        }
        return changes;
    }

    static IReadOnlyList<ArmoryDatabaseResourceChange> MirrorGunsmithPreviewFamily(
        IReadOnlyList<string> archivePaths, string weaponFamilyName, string leafTableName,
        uint templateTag, uint newTag, ulong modelSelectorId,
        IReadOnlyDictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]> preparedResourceData)
    {
        if (modelSelectorId == 0)
            return [];
        if (!weaponFamilyName.StartsWith("W_", StringComparison.OrdinalIgnoreCase)
            || weaponFamilyName.StartsWith("WG_", StringComparison.OrdinalIgnoreCase)
            || !leafTableName.StartsWith(weaponFamilyName, StringComparison.OrdinalIgnoreCase))
            return [];

        string previewFamilyName = "WG_" + weaponFamilyName[2..];
        string slotSuffix = leafTableName[weaponFamilyName.Length..];
        string previewLeafPrefix = previewFamilyName + slotSuffix;
        var changes = new List<ArmoryDatabaseResourceChange>();

        foreach (string path in archivePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var archive = ForgeArchive.Open(path);
            foreach (ForgeEntry entry in archive.Entries.Where(candidate => candidate.Name.Equals(previewFamilyName, StringComparison.OrdinalIgnoreCase)
                         || candidate.Name.StartsWith(previewFamilyName + "_", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = new MemoryStream(archive.ReadEntry(entry));
                DataFile file = DataFile.Read(stream);
                ApplyPreparedResourceData(file, path, entry.Index, preparedResourceData);
                var resources = file.Resources.ToDictionary(resource => resource.Id);
                var resourceIndexes = file.Resources
                    .Select((resource, index) => (resource.Id, index))
                    .ToDictionary(item => item.Id, item => item.index);
                var leaves = file.Resources
                    .Select((resource, index) => (resource, index))
                    .Where(item => item.resource.ClassHash == BuildTable.ClassHash && (item.resource.Name.Equals(previewLeafPrefix, StringComparison.OrdinalIgnoreCase) || item.resource.Name.StartsWith(previewLeafPrefix + "_", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (leaves.Count == 0)
                    continue;
                if (leaves.Count != 1)
                    throw new InvalidOperationException($"{entry.Name} contains more than one Gunsmith preview table for {slotSuffix}.");

                var leaf = leaves[0];
                BuildTableAsset previewTable = BuildTable.Read(leaf.resource.Data);
                if (previewTable.Rows.Any(row => row.Tag == newTag))
                    continue;
                var templateRows = previewTable.Rows
                    .Select((row, index) => (row, index))
                    .Where(item => item.row.Tag == templateTag)
                    .ToList();
                
                if (templateRows.Count == 0)
                    continue;
                if (templateRows.Count > 1)
                    throw new InvalidOperationException($"{leaf.resource.Name} contains the copied BuildTag more than once.");

                BuildTableRow templateRow = templateRows[0].row;
                var modelHandles = templateRow.References
                    .Where(reference => reference.Kind == BuildTableReferenceKind.Handle && reference.ComponentIndex == 1 && reference.Value != 0)
                    .ToList();
                if (modelHandles.Count != 1)
                    throw new InvalidOperationException($"{leaf.resource.Name} does not contain exactly one primary Gunsmith model handle.");

                changes.Add(new ArmoryDatabaseResourceChange(path, entry.Index, entry.Name, leaf.index, leaf.resource.Name, AddOption(previewTable, templateRows[0].index, newTag, modelHandles[0].Value, modelSelectorId, 0, 0)));
                foreach (BuildTableResourceChange parent in BuildParentTagChanges(leaf.resource.Id, templateTag, newTag, resources, resourceIndexes))
                    changes.Add(new ArmoryDatabaseResourceChange(path, entry.Index, entry.Name, parent.ResourceIndex, parent.Name, parent.Data));
            }
        }
        return changes;
    }

    static ulong FindGunsmithPreviewTemplateModel(IReadOnlyList<string> archivePaths, string weaponFamilyName, string leafTableName, uint templateTag,
        IReadOnlyDictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]> preparedResourceData)
    {
        if (!weaponFamilyName.StartsWith("W_", StringComparison.OrdinalIgnoreCase)
            || weaponFamilyName.StartsWith("WG_", StringComparison.OrdinalIgnoreCase)
            || !leafTableName.StartsWith(weaponFamilyName, StringComparison.OrdinalIgnoreCase))
            return 0;

        string previewFamilyName = "WG_" + weaponFamilyName[2..];
        string previewLeafPrefix = previewFamilyName + leafTableName[weaponFamilyName.Length..];
        var modelIds = new HashSet<ulong>();
        foreach (string path in archivePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var archive = ForgeArchive.Open(path);
            foreach (ForgeEntry entry in archive.Entries.Where(candidate => candidate.Name.Equals(previewFamilyName, StringComparison.OrdinalIgnoreCase)
                || candidate.Name.StartsWith(previewFamilyName + "_", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = new MemoryStream(archive.ReadEntry(entry));
                DataFile file = DataFile.Read(stream);
                ApplyPreparedResourceData(file, path, entry.Index, preparedResourceData);
                foreach (Resource resource in file.Resources.Where(resource => resource.ClassHash == BuildTable.ClassHash && (resource.Name.Equals(previewLeafPrefix, StringComparison.OrdinalIgnoreCase)
                    || resource.Name.StartsWith(previewLeafPrefix + "_", StringComparison.OrdinalIgnoreCase))))
                {
                    BuildTableAsset table = BuildTable.Read(resource.Data);
                    foreach (BuildTableRow row in table.Rows.Where(row => row.Tag == templateTag))
                    {
                        var handles = row.References.Where(reference => reference.Kind == BuildTableReferenceKind.Handle && reference.ComponentIndex == 1 && reference.Value != 0)
                            .Select(reference => reference.Value)
                            .Distinct()
                            .ToList();
                        if (handles.Count != 1)
                            throw new InvalidOperationException($"{resource.Name} does not contain exactly one primary Gunsmith model handle.");
                        modelIds.Add(handles[0]);
                    }
                }
            }
        }

        return modelIds.Count switch
        {
            0 => 0,
            1 => modelIds.Single(),
            _ => throw new InvalidOperationException($"The Gunsmith preview copies of {previewLeafPrefix} disagree on their template model.")
        };
    }

    static IReadOnlyList<BuildTableResourceChange> BuildParentTagChanges(ulong leafTableId, uint templateTag, uint newTag,
        IReadOnlyDictionary<ulong, Resource> localResources, IReadOnlyDictionary<ulong, int> localResourceIndexes)
    {
        uint entityBuilderHash = ResourceTypes.Crc32("EntityBuilder");
        var changes = new List<BuildTableResourceChange>();
        int parentTables = 0;
        int entityBuilders = 0;

        foreach (Resource resource in localResources.Values)
        {
            if (resource.Id == leafTableId || resource.ClassHash != BuildTable.ClassHash && resource.ClassHash != entityBuilderHash)
                continue;
            if (!TryAddBuildTag(resource.Data, templateTag, newTag, out byte[] updated))
                continue;
            if (!localResourceIndexes.TryGetValue(resource.Id, out int resourceIndex))
                throw new InvalidOperationException($"{resource.Name} has no stable resource index in the opened weapon container.");

            changes.Add(new BuildTableResourceChange(resourceIndex, resource.Name, updated));
            if (resource.ClassHash == BuildTable.ClassHash)
                parentTables++;
            else
                entityBuilders++;
        }

        if (parentTables != 1 || entityBuilders != 1)
            throw new InvalidOperationException("The attachment template did not resolve to exactly one parent BuildTable and one EntityBuilder BuildTags list.");
        return changes;
    }

    internal static bool TryAddBuildTag(byte[] source, uint templateTag, uint newTag, out byte[] updated)
    {
        const uint buildTagsHash = 0x11BD5345;
        const uint buildTagHash = 0xB332698E;
        const int entrySize = 16;
        var matches = new List<(int CountOffset, int EntriesOffset, int Count, int TemplateIndex, IReadOnlyList<ulong> ObjectIds)>();

        for (int markerOffset = sizeof(ulong); markerOffset + 8 <= source.Length; markerOffset++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(markerOffset)) != buildTagsHash)
                continue;
            int countOffset = markerOffset + sizeof(uint);
            int count = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(countOffset));
            int entriesOffset = countOffset + sizeof(int);
            if (count is < 1 or > 10_000 || entriesOffset + (long)count * entrySize > source.Length)
                continue;

            var ids = new ulong[count];
            int templateIndex = -1;
            bool valid = true;
            for (int item = 0; item < count; item++)
            {
                int entryOffset = entriesOffset + item * entrySize;
                ulong objectId = BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(entryOffset));
                uint classHash = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(entryOffset + 8));
                if (objectId is < 0xF0000000UL or > uint.MaxValue || classHash != buildTagHash)
                {
                    valid = false;
                    break;
                }
                ids[item] = objectId;
                if (BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(entryOffset + 12)) == templateTag)
                    templateIndex = item;
            }
            if (valid && templateIndex >= 0)
                matches.Add((countOffset, entriesOffset, count, templateIndex, ids));
        }

        if (matches.Count == 0)
        {
            updated = source;
            return false;
        }
        if (matches.Count != 1)
            throw new InvalidOperationException("A parent resource contains more than one matching BuildTags list.");

        var match = matches[0];
        if (Enumerable.Range(0, match.Count).Any(item => BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(match.EntriesOffset + item * entrySize + 12)) == newTag))
            throw new InvalidOperationException("The new BuildTag already exists in a parent resource.");

        if (match.ObjectIds.Zip(match.ObjectIds.Skip(1)).Any(pair => pair.First >= pair.Second))
            throw new InvalidOperationException("The parent BuildTags list does not have ordered local object ids.");
        ulong newObjectId = match.ObjectIds[match.TemplateIndex] + 1;
        if (newObjectId is < 0xF0000000UL or > uint.MaxValue)
            throw new InvalidOperationException("The parent resource has no free local BuildTag object ID.");

        int templateOffset = match.EntriesOffset + match.TemplateIndex * entrySize;
        int insertAt = templateOffset + entrySize;
        byte[] shiftedSource = (byte[])source.Clone();
        for (int offset = 0; offset <= source.Length - sizeof(ulong); offset++)
        {
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(offset));
            if (id < newObjectId || id > uint.MaxValue)
                continue;
            ulong shifted = id + 1;
            if (shifted > uint.MaxValue)
                throw new InvalidOperationException("The parent resource has no free local object ID.");
            BinaryPrimitives.WriteUInt64LittleEndian(shiftedSource.AsSpan(offset), shifted);
            offset += sizeof(ulong) - 1;
        }
        updated = new byte[checked(source.Length + entrySize)];
        shiftedSource.AsSpan(0, insertAt).CopyTo(updated);
        shiftedSource.AsSpan(templateOffset, entrySize).CopyTo(updated.AsSpan(insertAt));
        shiftedSource.AsSpan(insertAt).CopyTo(updated.AsSpan(insertAt + entrySize));
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(match.CountOffset), match.Count + 1);
        BinaryPrimitives.WriteUInt64LittleEndian(updated.AsSpan(insertAt), newObjectId);
        BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(insertAt + 12), newTag);

        int writtenCount = BinaryPrimitives.ReadInt32LittleEndian(updated.AsSpan(match.CountOffset));
        uint writtenTag = BinaryPrimitives.ReadUInt32LittleEndian(updated.AsSpan(insertAt + 12));
        var writtenIds = new List<ulong>(writtenCount);
        for (int item = 0; item < writtenCount; item++)
            writtenIds.Add(BinaryPrimitives.ReadUInt64LittleEndian(updated.AsSpan(match.EntriesOffset + item * entrySize)));
        if (writtenCount != match.Count + 1 || writtenTag != newTag || writtenIds.Zip(writtenIds.Skip(1)).Any(pair => pair.First >= pair.Second))
            throw new InvalidDataException("The parent BuildTags list did not retain its new entry.");
        return true;
    }

    internal static IReadOnlyList<ArmoryDatabaseResourceChange> BuildTagColumnMapChanges(ArmoryIndex index, uint templateTag, uint newTag)
    {
        var changes = new List<ArmoryDatabaseResourceChange>();
        foreach (var resource in index.DatabaseResources
                     .Where(resource => resource.ClassHash == BuildTagColumnMapClass)
                     .GroupBy(resource => (resource.ArchivePath, resource.EntryIndex, resource.ResourceIndex))
                     .Select(group => group.Last()))
        {
            if (!TryAddBuildTagColumnMapEntry(resource.Data, templateTag, newTag, out byte[] updated))
                continue;
            changes.Add(new ArmoryDatabaseResourceChange(resource.ArchivePath, resource.EntryIndex, resource.EntryName, resource.ResourceIndex, resource.Name, updated));
        }

        if (changes.Count == 0)
            throw new InvalidOperationException("No global BuildTag-to-column-mask map contains the copied attachment tag.");
        return changes;
    }

    static bool TryAddBuildTagColumnMapEntry(byte[] source, uint templateTag, uint newTag, out byte[] updated)
    {
        const int entryStride = 21;
        var templateItems = FindTagColumnMapItems(source, templateTag);
        if (templateItems.Count == 0)
        {
            updated = source;
            return false;
        }
        if (templateItems.Count != 1)
            throw new InvalidOperationException("A global BuildTag-to-column-mask map contains the copied tag more than once.");
        if (FindTagColumnMapItems(source, newTag).Count != 0)
            throw new InvalidOperationException("The new BuildTag already exists in a global BuildTag-to-column-mask map.");

        int templateStart = templateItems[0];
        int runStart = templateStart;
        while (IsTagColumnMapEntry(source, runStart - entryStride))
            runStart -= entryStride;
        int runEnd = templateStart;
        while (IsTagColumnMapEntry(source, runEnd + entryStride))
            runEnd += entryStride;

        int countOffset = runStart - 24;
        if (countOffset < 1 || source[countOffset - 1] != 1 || countOffset + 16 > source.Length || BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(countOffset + 12)) != BuildTagColumnEntryMarker)
            throw new InvalidDataException("The global BuildTag-to-column-mask entry has no readable list header.");

        int regularCount = (runEnd - runStart) / entryStride + 1;
        int count = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(countOffset));
        if (count != regularCount + 1)
            throw new InvalidDataException("The global BuildTag-to-column-mask list count does not match its entries.");

        ulong maximumLocalId = 0;
        for (int offset = 0; offset <= source.Length - sizeof(ulong); offset++)
        {
            ulong candidate = BinaryPrimitives.ReadUInt64LittleEndian(source.AsSpan(offset));
            if (candidate is >= 0xF8000000UL and < 0xF9000000UL && candidate > maximumLocalId)
                maximumLocalId = candidate;
        }
        ulong newLocalId = maximumLocalId + 1;
        if (newLocalId is < 0xF8000000UL or >= 0xF9000000UL)
            throw new InvalidOperationException("The global BuildTag-to-column-mask map has no free local object ID.");

        int insertAt = runEnd + entryStride;
        updated = new byte[checked(source.Length + entryStride)];
        source.AsSpan(0, insertAt).CopyTo(updated);
        source.AsSpan(templateStart, entryStride).CopyTo(updated.AsSpan(insertAt));
        source.AsSpan(insertAt).CopyTo(updated.AsSpan(insertAt + entryStride));
        BinaryPrimitives.WriteUInt64LittleEndian(updated.AsSpan(insertAt + 1), newLocalId);
        BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(insertAt + 13), newTag);
        BinaryPrimitives.WriteInt32LittleEndian(updated.AsSpan(countOffset), count + 1);

        if (BinaryPrimitives.ReadInt32LittleEndian(updated.AsSpan(countOffset)) != count + 1
            || BinaryPrimitives.ReadUInt32LittleEndian(updated.AsSpan(insertAt + 13)) != newTag || FindTagColumnMapItems(updated, newTag).Count != 1)
            throw new InvalidDataException("The global BuildTag-to-column-mask map did not retain its new entry.");
        return true;
    }

    static List<int> FindTagColumnMapItems(ReadOnlySpan<byte> data, uint tag)
    {
        Span<byte> needle = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(needle, tag);
        var results = new List<int>();
        int search = 0;
        while (search <= data.Length - sizeof(uint))
        {
            int relative = data[search..].IndexOf(needle);
            if (relative < 0)
                break;
            int tagOffset = search + relative;
            int itemStart = tagOffset - 13;
            if (IsTagColumnMapEntry(data, itemStart))
                results.Add(itemStart);
            search = tagOffset + sizeof(uint);
        }
        return results;
    }

    static bool IsTagColumnMapEntry(ReadOnlySpan<byte> data, int offset) => offset >= 0 && offset + 21 <= data.Length && data[offset] == 1
        && BinaryPrimitives.ReadUInt32LittleEndian(data[(offset + 9)..]) == BuildTagColumnEntryMarker;

    internal static (Resource Resource, ResourceLocation Location) LoadIndexedResource(ArmoryIndex index, ulong id)
    {
        var indexed = index.DatabaseResources.LastOrDefault(resource => resource.Id == id)
            ?? throw new InvalidOperationException($"Gameplay record 0x{id:X} is not present in the Armory index.");
        using var archive = ForgeArchive.Open(indexed.ArchivePath);
        var entry = archive.Entries.FirstOrDefault(candidate => candidate.Index == indexed.EntryIndex)
            ?? throw new InvalidOperationException($"Could not reopen {indexed.EntryName}.");
        using var stream = new MemoryStream(archive.ReadEntry(entry));
        var file = DataFile.Read(stream);
        if ((uint)indexed.ResourceIndex >= (uint)file.Resources.Count)
            throw new InvalidOperationException($"Gameplay record {indexed.Name} moved inside its container.");
        Resource resource = file.Resources[indexed.ResourceIndex];
        if (resource.Id != id)
            throw new InvalidOperationException($"Gameplay record {indexed.Name} no longer has its indexed ID.");
        resource.Data = (byte[])indexed.Data.Clone();
        return (resource, new ResourceLocation(indexed.ArchivePath, indexed.EntryIndex, indexed.EntryName));
    }

    static byte[] CloneAttachmentTypeRegistry(GunsmithAvailabilityList template, ulong newId, uint oldTag, uint newTag, ulong memberId)
    {
        byte[] result = GunsmithAvailability.RewriteMembers(template, [memberId]);
        if (result.Length < sizeof(ulong) || BinaryPrimitives.ReadUInt64LittleEndian(result) != template.Owner.Id)
            throw new InvalidDataException("The WPN_AT attachment-type template does not start with its resource ID.");

        int markerOffset = template.CountOffset - 12;
        int tagOffset = template.CountOffset - 8;
        if (markerOffset < 12 || tagOffset + sizeof(uint) > result.Length || BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(markerOffset)) != BuildTagMarker
            || BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(tagOffset)) != oldTag)
            throw new InvalidDataException("The WPN_AT attachment-type template has no BuildTag immediately before its member list.");

        BinaryPrimitives.WriteUInt64LittleEndian(result, newId);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(tagOffset), newTag);
        if (BinaryPrimitives.ReadUInt64LittleEndian(result) != newId || BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(tagOffset)) != newTag)
            throw new InvalidDataException("The cloned WPN_AT attachment type did not retain its new identity.");
        return result;
    }

    internal static byte[] CloneGameplayRecord(byte[] source, ulong oldId, ulong newId, uint buildTag, uint stringId)
    {
        byte[] result = (byte[])source.Clone();
        if (result.Length < 12 || BinaryPrimitives.ReadUInt64LittleEndian(result) != oldId)
            throw new InvalidDataException("The template gameplay record does not start with its resource ID.");
        BinaryPrimitives.WriteUInt64LittleEndian(result, newId);

        int tag = IndexOfUInt32(result, BuildTagMarker, 12);
        if (tag < 0 || tag + 8 > result.Length)
            throw new InvalidDataException("The template gameplay record has no readable BuildTag.");
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(tag + 4), buildTag);

        int localized = IndexOfUInt32(result, LocalizedValueMarker, tag + 8);
        if (localized < 0 || localized + 8 > result.Length)
            throw new InvalidDataException("The template gameplay record has no localized display-name field.");
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(localized + 4), stringId);
        if (TryBuildTag(result) != buildTag)
            throw new InvalidDataException("The cloned gameplay record did not retain its new BuildTag.");
        return result;
    }

    internal static byte[] CloneRecordInfo(byte[] source, ulong oldInfoId, ulong newInfoId, ulong oldRecordId, ulong newRecordId, string name)
    {
        if (source.Length < 24 || BinaryPrimitives.ReadUInt64LittleEndian(source) != oldInfoId)
            throw new InvalidDataException("The template StoreObjectInfo does not start with its resource ID.");
        int marker = IndexOfUInt32(source, LocalizedValueMarker, 12);
        if (marker < 0 || marker + 12 > source.Length)
            throw new InvalidDataException("The template StoreObjectInfo has no readable name field.");

        int countOffset = marker + 8;
        int characters = BinaryPrimitives.ReadInt32LittleEndian(source.AsSpan(countOffset));
        int stringOffset = countOffset + sizeof(int);
        if (characters < 0 || stringOffset + characters * 2 + 1 > source.Length)
            throw new InvalidDataException("The template StoreObjectInfo has an invalid name length.");

        byte[] encoded = System.Text.Encoding.Unicode.GetBytes(name);
        int suffix = stringOffset + characters * 2 + 1;
        byte[] result = new byte[checked(stringOffset + encoded.Length + 1 + source.Length - suffix)];
        source.AsSpan(0, stringOffset).CopyTo(result);
        BinaryPrimitives.WriteUInt64LittleEndian(result, newInfoId);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(countOffset), encoded.Length / 2);
        encoded.CopyTo(result.AsSpan(stringOffset));
        source.AsSpan(suffix).CopyTo(result.AsSpan(stringOffset + encoded.Length + 1));

        Span<byte> needle = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(needle, oldRecordId);
        int at = result.AsSpan().IndexOf(needle);
        if (at < 0)
            throw new InvalidDataException("The template StoreObjectInfo does not name its own record.");
        BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(at), newRecordId);
        if (result.AsSpan(at + sizeof(ulong)).IndexOf(needle) >= 0)
            throw new InvalidDataException("The template StoreObjectInfo names its record more than once.");
        return result;
    }

    internal static bool Names(byte[] data, ulong id)
    {
        Span<byte> needle = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(needle, id);
        return data.AsSpan().IndexOf(needle) >= 0;
    }

    static bool IsModelReference(BuildTableReference reference) => reference.Kind == BuildTableReferenceKind.Handle || reference.Kind == BuildTableReferenceKind.ObjectPointer && reference.ComponentIndex is not null;

    static void SetModelReferenceStorage(byte[] tableData, int idOffset, BuildTableReferenceKind kind)
    {
        int typeOffset = idOffset - sizeof(ulong) - 1;
        int tagOffset = idOffset - 1;
        if (typeOffset < 0 || idOffset > tableData.Length - sizeof(ulong))
            throw new InvalidDataException("The cloned model reference lies outside the BuildTable.");

        uint current = BinaryPrimitives.ReadUInt32LittleEndian(tableData.AsSpan(typeOffset));
        if (current is not (DynamicHandleType or DynamicObjectPointerType))
            throw new InvalidDataException($"The cloned model reference uses unsupported storage type 0x{current:X8}.");

        switch (kind)
        {
            case BuildTableReferenceKind.ObjectPointer:
                BinaryPrimitives.WriteUInt32LittleEndian(tableData.AsSpan(typeOffset), DynamicObjectPointerType);
                tableData[tagOffset] = 1;
                break;
            case BuildTableReferenceKind.Handle:
                BinaryPrimitives.WriteUInt32LittleEndian(tableData.AsSpan(typeOffset), DynamicHandleType);
                tableData[tagOffset] = 0;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    internal static ModelClonePlan CloneModelResources(AddAttachmentDraft draft, ulong sourceId, IReadOnlyList<string> archivePaths,
        string localArchivePath, int localEntryIndex, HashSet<ulong> usedIds, IDictionary<string, ulong> sharedTextureIds)
    {
        ModelFile source = LoadModelFiles(archivePaths, new HashSet<ulong> { sourceId }).FirstOrDefault()
            ?? throw new InvalidOperationException($"Model 0x{sourceId:X} is not the first resource of a Forge entry, so it cannot be cloned into one.");
        Resource root = source.File.Resources[0];
        uint lodSelectorHash = ResourceTypes.Crc32("LODSelector");
        if (root.ClassHash != Mesh.ClassHash && root.ClassHash != lodSelectorHash)
            throw new InvalidOperationException($"{root.Name} is {ResourceTypes.NameOf(root.ClassHash)}, not a Mesh or LODSelector.");

        var files = new List<ModelFile> { source };
        if (root.ClassHash != Mesh.ClassHash)
            files.AddRange(LoadModelFiles(archivePaths, ReferencedIds(root.Data, sourceId)).Where(file => file.File.Resources[0].ClassHash == Mesh.ClassHash));

        var oldIds = files.SelectMany(file => file.File.Resources)
            .Where(resource => resource.ClassHash == Mesh.ClassHash || resource.Id == root.Id)
            .Select(resource => resource.Id)
            .Distinct()
            .ToList();
        if (oldIds.Count < 2 && root.ClassHash != Mesh.ClassHash)
            throw new InvalidOperationException($"No cloneable Mesh was found beside {root.Name}.");

        var range = ReadContainerIdRange(localArchivePath, localEntryIndex);
        foreach (ModelFile file in files)
            usedIds.UnionWith(file.File.Resources.Select(resource => resource.Id));
        ReserveInstalledIds(archivePaths, range, usedIds);

        var newIds = AllocateAssetIds(oldIds.Count, usedIds, draft.InternalName, range.Minimum, range.MaximumExclusive);
        var map = oldIds.Zip(newIds).ToDictionary(pair => pair.First, pair => pair.Second);

        ImportedGeometry geometry = Path.GetExtension(draft.ModelOrAsset)
            .Equals(".obj", StringComparison.OrdinalIgnoreCase)
                ? ObjFile.Read(draft.ModelOrAsset).ToGeometry()
                : GltfFile.Read(draft.ModelOrAsset);

        var additions = new List<ArmoryArchiveEntryAddition>();
        var customResources = new List<Resource>();
        var customMaterialIds = new List<ulong>();
        ulong customResourceOwner = 0;
        if (draft.Textures.Any)
        {
            customResources = CloneSurfaceResources(draft, geometry, files, archivePaths, localArchivePath, usedIds, sharedTextureIds, additions, out customMaterialIds, out customResourceOwner);
        }

        var meshes = new Dictionary<ulong, ClonedMesh>();
        foreach (ModelFile file in files)
        {
            foreach (Resource resource in file.File.Resources)
            {
                if (resource.ClassHash != Mesh.ClassHash || !map.TryGetValue(resource.Id, out ulong newMeshId))
                    continue;

                Mesh mesh = Mesh.Read(resource.Data);
                GpuSize before = MeasureGpu(mesh);
                mesh.Id = newMeshId;
                MeshImport.Replace(mesh, geometry);
                if (customMaterialIds.Count > 0)
                {
                    for (int i = 0; i < mesh.Materials.Count; i++)
                        mesh.Materials[i].MaterialId = customMaterialIds[Math.Min(i, customMaterialIds.Count - 1)];
                    for (int i = 0; i < mesh.Instancing.Count; i++)
                        mesh.Instancing[i].MaterialId = customMaterialIds[Math.Min(i, customMaterialIds.Count - 1)];
                }
                byte[] meshData = mesh.Write();
                BinaryPrimitives.WriteUInt64LittleEndian(meshData, newMeshId);
                Mesh.Read(meshData);
                meshes[resource.Id] = new ClonedMesh(newMeshId, meshData, before, MeasureGpu(mesh));
            }
        }

        var resources = new List<Resource>();
        foreach (ModelFile file in files)
        {
            using var sourceStream = new MemoryStream(file.Data);
            DataFile rebuilt = DataFile.Read(sourceStream);
            for (int i = 0; i < rebuilt.Resources.Count; i++)
            {
                Resource resource = rebuilt.Resources[i];
                if (!map.TryGetValue(resource.Id, out ulong newId))
                    continue;

                byte[] data;
                if (meshes.TryGetValue(resource.Id, out ClonedMesh? cloned))
                {
                    data = (byte[])cloned.Data.Clone();
                }
                else
                {
                    data = RemapResourceIds(resource.Data, map);
                    BinaryPrimitives.WriteUInt64LittleEndian(data, newId);
                    UpdateStreamedLodSizes(data, meshes.Values);
                }

                Resource clone = DataFile.CloneResource(resource, newId, Rename(resource.Name, root.Name, draft.InternalName), data);
                rebuilt.Resources[i] = clone;
                resources.Add(clone);
            }

            if (customResources.Count > 0)
            {
                rebuilt.Resources.RemoveAll(resource => resource.ClassHash == Material.ClassHash || resource.ClassHash == TextureSet.ClassHash);
                if (file.EntryId == customResourceOwner)
                {
                    rebuilt.Resources.AddRange(customResources.Select(resource => new Resource
                    {
                        Id = resource.Id,
                        ClassHash = resource.ClassHash,
                        Name = resource.Name,
                        Header = (byte[])resource.Header.Clone(),
                        Data = (byte[])resource.Data.Clone(),
                    }));
                    resources.AddRange(customResources);
                }
            }

            ulong newEntryId = map[file.EntryId];
            if (rebuilt.Resources[0].Id != newEntryId)
                throw new InvalidDataException($"The cloned container for {file.EntryName} does not start with its own resource.");

            using var output = new MemoryStream();
            rebuilt.Write(output);
            byte[] info = (byte[])file.Info.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(4), Candidate64("forge-entry:" + draft.InternalName + ":" + file.EntryName, 0));
            additions.Add(new ArmoryArchiveEntryAddition(file.ArchivePath, newEntryId, Rename(file.EntryName, source.EntryName, draft.InternalName),
                rebuilt.Resources[0].ClassHash, info, output.ToArray(), file.PrefetchBlock));
        }

        return new ModelClonePlan(map[root.Id], additions, resources);
    }

    static List<Resource> CloneSurfaceResources(AddAttachmentDraft draft, ImportedGeometry geometry, IReadOnlyList<ModelFile> files,
        IReadOnlyList<string> archivePaths, string localArchivePath, HashSet<ulong> usedIds, IDictionary<string, ulong> sharedTextureIds,
        List<ArmoryArchiveEntryAddition> additions, out List<ulong> materialIds, out ulong resourceOwner)
    {
        var sourceResources = files.SelectMany(file => file.File.Resources).ToList();
        ModelFile owner = files.FirstOrDefault(file => file.File.Resources.Any(resource => resource.ClassHash == Material.ClassHash))
            ?? throw new InvalidOperationException("The selected model template carries no clonable Material resource.");
        resourceOwner = owner.EntryId;

        Resource sourceMeshResource = sourceResources.FirstOrDefault(resource => resource.ClassHash == Mesh.ClassHash)
            ?? throw new InvalidOperationException("The model template carries no Mesh resource.");
        Mesh sourceMesh = Mesh.Read(sourceMeshResource.Data);
        if (sourceMesh.Materials.Count == 0)
            throw new InvalidOperationException("The model template has no material slots to clone.");

        var cloned = new List<Resource>();
        materialIds = [];
        int ranges = Math.Max(1, geometry.Groups.Count);
        for (int range = 0; range < ranges; range++)
        {
            ulong sourceMaterialId = sourceMesh.Materials[Math.Min(range, sourceMesh.Materials.Count - 1)].MaterialId;
            Resource sourceMaterial = sourceResources.FirstOrDefault(resource => resource.Id == sourceMaterialId && resource.ClassHash == Material.ClassHash)
                ?? sourceResources.FirstOrDefault(resource => resource.ClassHash == Material.ClassHash)
                ?? throw new InvalidOperationException($"Material 0x{sourceMaterialId:X} is not present beside the model template.");
            Material material = Material.Read(sourceMaterial.Data);
            Resource sourceSet = sourceResources.FirstOrDefault(resource => resource.Id == material.TextureSetId && resource.ClassHash == TextureSet.ClassHash)
                ?? sourceResources.FirstOrDefault(resource => resource.ClassHash == TextureSet.ClassHash)
                ?? throw new InvalidOperationException($"TextureSet 0x{material.TextureSetId:X} is not present beside the model template.");
            TextureSet set = TextureSet.Read(sourceSet.Data);

            var textureMap = new Dictionary<ulong, ulong>();
            foreach (string slot in new[] { "Diffuse", "Normal", "Specular", "Mask1" })
            {
                string path = draft.Textures.PathFor(slot);
                if (path.Length == 0)
                    continue;
                ulong templateTextureId = set.Find(slot);
                if (templateTextureId == 0)
                    throw new InvalidOperationException($"The selected gameplay template has no {slot} texture slot.");
                string key = slot + "|" + Path.GetFullPath(path);
                if (!sharedTextureIds.TryGetValue(key, out ulong textureId))
                {
                    textureId = CloneTextureEntries(draft.InternalName, slot, path, templateTextureId, archivePaths, localArchivePath, usedIds, additions);
                    sharedTextureIds[key] = textureId;
                }
                textureMap[templateTextureId] = textureId;
            }

            ulong setId = Allocate64($"texture-set:{draft.InternalName}:{range}", usedIds);
            byte[] setData = RemapResourceIds(sourceSet.Data, textureMap);
            BinaryPrimitives.WriteUInt64LittleEndian(setData, setId);
            Resource clonedSet = DataFile.CloneResource(sourceSet, setId, $"{draft.InternalName}_Set_{range + 1}", setData);

            ulong materialId = Allocate64($"material:{draft.InternalName}:{range}", usedIds);
            var materialMap = new Dictionary<ulong, ulong>(textureMap)
            {
                [material.TextureSetId] = setId,
            };
            byte[] materialData = RemapResourceIds(sourceMaterial.Data, materialMap);
            BinaryPrimitives.WriteUInt64LittleEndian(materialData, materialId);
            Resource clonedMaterial = DataFile.CloneResource(sourceMaterial, materialId, $"{draft.InternalName}_Material_{range + 1}", materialData);

            cloned.Add(clonedMaterial);
            cloned.Add(clonedSet);
            materialIds.Add(materialId);
        }
        return cloned;
    }

    static ulong CloneTextureEntries(string internalName, string slot, string imagePath, ulong templateTextureId, IReadOnlyList<string> archivePaths,
        string targetArchivePath, HashSet<ulong> usedIds, List<ArmoryArchiveEntryAddition> additions)
    {
        ModelFile source = LoadModelFiles(archivePaths, new HashSet<ulong> { templateTextureId }).FirstOrDefault()
            ?? throw new InvalidOperationException($"The template {slot} texture 0x{templateTextureId:X} has no Forge entry.");
        Resource root = source.File.Resources.FirstOrDefault(resource => resource.Id == templateTextureId && resource.ClassHash == TextureMap.ClassHash)
            ?? throw new InvalidOperationException($"The template {slot} entry does not contain a TextureMap.");
        TextureMap texture = TextureMap.Read(root.Data);
        List<byte[]> levels = TextureImporter.LevelsFor(imagePath, texture, generateMips: false, out _, out int width, out int height);
        if (levels.Count < texture.MipCount)
            throw new InvalidDataException($"The {slot} image is {width} x {height} and produces {levels.Count} mip levels, but the template needs {texture.MipCount}. Choose a larger image.");

        var oldIds = new List<ulong> { templateTextureId };
        oldIds.AddRange(texture.StreamedMips);
        var map = oldIds.ToDictionary(id => id, id => Allocate64($"texture:{internalName}:{slot}:{id:X}", usedIds));
        var files = LoadModelFiles(archivePaths, oldIds.ToHashSet());
        if (files.SelectMany(file => file.File.Resources).Count(resource => map.ContainsKey(resource.Id)) != oldIds.Count)
            throw new InvalidOperationException($"Not every streamed resource of the template {slot} texture was found.");

        string newRootName = $"{internalName}_{slot}Map_PC";
        foreach (ModelFile file in files.OrderByDescending(file => file.EntryId == templateTextureId))
        {
            using var input = new MemoryStream(file.Data);
            DataFile rebuilt = DataFile.Read(input);
            for (int i = 0; i < rebuilt.Resources.Count; i++)
            {
                Resource resource = rebuilt.Resources[i];
                if (!map.TryGetValue(resource.Id, out ulong newId))
                    continue;
                byte[] data;
                if (resource.ClassHash == TextureMap.ClassHash)
                {
                    byte[] chain = TextureImport.EmbeddedChain(texture, levels, width, height);
                    data = TextureImport.Resize(texture, resource.Data, width, height, chain);
                }
                else if (resource.ClassHash == CompiledMip.ClassHash)
                {
                    CompiledMip mip = CompiledMip.Read(resource.Data);
                    if (mip.Level >= levels.Count)
                        throw new InvalidDataException($"The {slot} image has no mip level {mip.Level}.");
                    data = TextureImport.ReplacePixels(resource.Data, mip.PixelOffset, levels[(int)mip.Level]);
                }
                else
                {
                    throw new InvalidDataException($"Texture resource 0x{resource.Id:X} is {ResourceTypes.NameOf(resource.ClassHash)}.");
                }
                data = RemapResourceIds(data, map);
                BinaryPrimitives.WriteUInt64LittleEndian(data, newId);
                rebuilt.Resources[i] = DataFile.CloneResource(resource, newId, Rename(resource.Name, root.Name, newRootName), data);
            }

            ulong newEntryId = map[file.EntryId];
            if (rebuilt.Resources[0].Id != newEntryId)
                throw new InvalidDataException($"The cloned {slot} texture container does not start with its own resource.");
            using var output = new MemoryStream();
            rebuilt.Write(output);
            byte[] info = (byte[])file.Info.Clone();
            BinaryPrimitives.WriteUInt64LittleEndian(info.AsSpan(4), Candidate64($"forge-entry:{internalName}:{slot}:{file.EntryName}", 0));
            additions.Add(new ArmoryArchiveEntryAddition(targetArchivePath, newEntryId, Rename(file.EntryName, source.EntryName, newRootName),
                rebuilt.Resources[0].ClassHash, info, output.ToArray(), file.PrefetchBlock));
        }
        return map[templateTextureId];
    }

    static List<ModelFile> LoadModelFiles(IReadOnlyList<string> archivePaths, IReadOnlySet<ulong> ids)
    {
        var found = new List<ModelFile>();
        var wanted = new HashSet<ulong>(ids);
        foreach (string path in archivePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (wanted.Count == 0)
                break;
            try
            {
                using var archive = ForgeArchive.Open(path);
                var matches = archive.Entries.Where(entry => wanted.Contains(entry.Id)).ToList();
                if (matches.Count == 0)
                    continue;
                ForgeEntry registry = archive.Entries.Single(entry => entry.Id == 145);
                byte[] prefetch = archive.ReadEntry(registry);
                foreach (ForgeEntry entry in matches)
                {
                    byte[] data = archive.ReadEntry(entry);
                    using var stream = new MemoryStream(data);
                    DataFile file = DataFile.Read(stream);
                    if (file.Resources.Count == 0 || file.Resources[0].Id != entry.Id)
                        continue;
                    found.Add(new ModelFile(path, entry.Name, entry.Id, file, data, archive.ReadEntryInfo(entry), PrefetchingFileInfos.ReadObjectBlock(prefetch, entry.Id)));
                    wanted.Remove(entry.Id);
                }
            }
            catch (Exception exception) when (exception is not InvalidDataException)
            {
                // Optional DLC archives may be absent or unreadable
            }
        }
        return found;
    }

    static HashSet<ulong> ReferencedIds(byte[] data, ulong self)
    {
        var found = new HashSet<ulong>();
        for (int offset = 0; offset <= data.Length - sizeof(ulong); offset++)
        {
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
            if (id != 0 && id != self && id <= 0x0000FFFFFFFFFFFFUL)
                found.Add(id);
        }
        return found;
    }

    static void ReserveInstalledIds(IReadOnlyList<string> archivePaths, (ulong Minimum, ulong MaximumExclusive) range, HashSet<ulong> usedIds)
    {
        foreach (string path in archivePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var archive = ForgeArchive.Open(path);
                usedIds.UnionWith(archive.Entries.Select(entry => entry.Id));
                foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.Id >= range.Minimum && entry.Id < range.MaximumExclusive && entry.FileExtension == ".data"))
                    usedIds.UnionWith(archive.ReadResourceIndex(entry).Select(resource => resource.Id));
            }
            catch (InvalidDataException)
            {
                throw;
            }
            catch
            {
                // Optional DLC archives may be absent or unreadable
            }
        }
    }

    static List<ulong> AllocateAssetIds(int count, HashSet<ulong> usedIds, string seed, ulong minimum, ulong maximumExclusive)
    {
        var result = new List<ulong>(count);
        for (ulong candidate = minimum; candidate < maximumExclusive && result.Count < count; candidate++)
            if (usedIds.Add(candidate))
                result.Add(candidate);
        while (result.Count < count)
            result.Add(Allocate64($"asset:{seed}:{result.Count}", usedIds));
        return result;
    }

    static int UpdateStreamedLodSizes(byte[] selector, IEnumerable<ClonedMesh> meshes)
    {
        int updated = 0;
        Span<byte> needle = stackalloc byte[sizeof(ulong)];
        foreach (ClonedMesh mesh in meshes)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(needle, mesh.Id);
            int start = 0;
            while (start <= selector.Length - sizeof(ulong))
            {
                int relative = selector.AsSpan(start).IndexOf(needle);
                if (relative < 0)
                    break;

                int at = start + relative + sizeof(ulong) + sizeof(uint);
                if (at + sizeof(uint) <= selector.Length)
                {
                    uint current = BinaryPrimitives.ReadUInt32LittleEndian(selector.AsSpan(at));
                    int replacement = current == (uint)mesh.Before.WithDescriptions
                        ? mesh.After.WithDescriptions
                        : current == (uint)mesh.Before.Buffers ? mesh.After.Buffers : -1;
                    if (replacement >= 0)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(selector.AsSpan(at), (uint)replacement);
                        updated++;
                    }
                }
                start += relative + 1;
            }
        }
        return updated;
    }

    static GpuSize MeasureGpu(Mesh mesh)
    {
        if (mesh.Clustered is null)
            return new GpuSize(0, 0);
        int buffers = mesh.Clustered.VertexBuffer.Length + mesh.Clustered.IndexBuffer.Length;
        return new GpuSize(buffers, buffers + mesh.Clustered.PrimitiveDescriptions.Length);
    }

    static string Rename(string name, string oldPrefix, string newPrefix) => name.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase)
            ? newPrefix + name[oldPrefix.Length..]
            : newPrefix + "_" + name;

    static (ulong Minimum, ulong MaximumExclusive) ReadContainerIdRange(string archivePath, int entryIndex)
    {
        using var archive = ForgeArchive.Open(archivePath);
        ForgeEntry entry = archive.Entries.FirstOrDefault(candidate => candidate.Index == entryIndex)
            ?? throw new InvalidOperationException("The opened weapon container moved inside its Forge archive.");
        ForgeEntry? next = archive.Entries
            .Where(candidate => candidate.Id > entry.Id)
            .MinBy(candidate => candidate.Id);
        return entry.Id == 0 || next is null ? (0, 0) : (entry.Id, next.Id);
    }

    static void ApplyPreparedResourceData(DataFile file, string archivePath, int entryIndex, IReadOnlyDictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]> preparedResourceData)
    {
        for (int resourceIndex = 0; resourceIndex < file.Resources.Count; resourceIndex++)
            if (preparedResourceData.TryGetValue((archivePath, entryIndex, resourceIndex), out byte[]? prepared))
                file.Resources[resourceIndex].Data = (byte[])prepared.Clone();
    }

    internal static List<LocalizationTarget> FindLocalizationTargets(IReadOnlyList<string> packages, IReadOnlyList<string> archivePaths,
        IReadOnlyDictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]> preparedResourceData)
    {
        var wanted = packages.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<LocalizationTarget>();
        foreach (string path in archivePaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var archive = ForgeArchive.Open(path);
                foreach (var entry in archive.Entries)
                {
                    string? package = BuildTableGameMetadataResolver.TryGetLanguagePackage(entry.Name);
                    if (package is null || !wanted.Contains(package))
                        continue;
                    using var stream = new MemoryStream(archive.ReadEntry(entry));
                    var resources = DataFile.Read(stream).Resources;
                    int resourceIndex = resources.FindIndex(candidate => candidate.ClassHash == LocalizationPackage.ClassHash);
                    if (resourceIndex < 0)
                        continue;
                    Resource resource = resources[resourceIndex];
                    if (preparedResourceData.TryGetValue((path, entry.Index, resourceIndex), out byte[]? prepared))
                        resource.Data = (byte[])prepared.Clone();
                    candidates.Add(new LocalizationTarget(package, resource, resourceIndex, new ResourceLocation(path, entry.Index, entry.Name)));
                }
            }
            catch
            {
                // Optional language archives may be absent
            }
        }
        return candidates
            .GroupBy(target => target.Package, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(target => target.Resource.Data.Length).First())
            .ToList();
    }

    internal static ArmoryArchiveResourceAddition ToAddition(ResourceLocation location, Resource resource) => new(location.ArchivePath, location.EntryIndex, location.EntryName, resource.Id, resource.ClassHash, resource.Name, resource.Header, resource.Data);

    static byte[] RemapResourceIds(byte[] source, IReadOnlyDictionary<ulong, ulong> map)
    {
        byte[] result = (byte[])source.Clone();
        Span<byte> oldBytes = stackalloc byte[sizeof(ulong)];
        foreach (var pair in map)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(oldBytes, pair.Key);
            int start = 0;
            while (start <= result.Length - sizeof(ulong))
            {
                int relative = result.AsSpan(start).IndexOf(oldBytes);
                if (relative < 0)
                    break;
                int offset = start + relative;
                BinaryPrimitives.WriteUInt64LittleEndian(result.AsSpan(offset), pair.Value);
                start = offset + sizeof(ulong);
            }
        }
        return result;
    }

    static uint TryBuildTag(ReadOnlySpan<byte> data)
    {
        int marker = IndexOfUInt32(data, BuildTagMarker, 12);
        return marker >= 0 && marker + 8 <= data.Length
            ? BinaryPrimitives.ReadUInt32LittleEndian(data[(marker + 4)..])
            : 0;
    }

    static int IndexOfUInt32(ReadOnlySpan<byte> data, uint value, int start)
    {
        Span<byte> needle = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(needle, value);
        int relative = data[start..].IndexOf(needle);
        return relative < 0 ? -1 : start + relative;
    }

    internal static uint Allocate32(string seed, HashSet<uint> used)
    {
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            uint id = ResourceTypes.Crc32(attempt == 0 ? seed : seed + ":" + attempt) & 0x7FFFFFFF;
            if (id != 0 && used.Add(id))
                return id;
        }
        throw new InvalidOperationException("Could not allocate a collision-free 32-bit game ID.");
    }

    internal static ulong Allocate64(string seed, HashSet<ulong> used)
    {
        for (int attempt = 0; attempt < 10_000; attempt++)
        {
            ulong id = Candidate64(seed, attempt);
            if (used.Add(id))
                return id;
        }
        throw new InvalidOperationException("Could not allocate a collision-free 64-bit game ID.");
    }

    static ulong Candidate64(string seed, int attempt)
    {
        string value = attempt == 0 ? seed : seed + ":" + attempt;
        ulong hash = 14695981039346656037UL;
        foreach (byte item in System.Text.Encoding.UTF8.GetBytes(value))
            hash = (hash ^ item) * 1099511628211UL;
        return 0x000000E000000000UL | (hash & 0x0000000FFFFFFFFFUL);
    }

    internal readonly record struct ResourceLocation(string ArchivePath, int EntryIndex, string EntryName);
    internal sealed record LocalizationTarget(string Package, Resource Resource, int ResourceIndex, ResourceLocation Location);
    internal sealed record ModelClonePlan(ulong SelectorId, IReadOnlyList<ArmoryArchiveEntryAddition> EntryAdditions, IReadOnlyList<Resource> Resources);
    sealed record ModelFile(string ArchivePath, string EntryName, ulong EntryId, DataFile File, byte[] Data, byte[] Info, byte[] PrefetchBlock);
    sealed record AttachmentCategory(uint Tag, ulong ExemplarRecordId, string RegistryName, int SharedOwners);
    readonly record struct GpuSize(int Buffers, int WithDescriptions);
    sealed record ClonedMesh(ulong Id, byte[] Data, GpuSize Before, GpuSize After);
}
