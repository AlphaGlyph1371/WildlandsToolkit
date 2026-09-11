using System.Buffers.Binary;
using System.IO;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

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
            new AddAttachmentTemplate(templateRowIndex, displayName, internalName),
            modelPath.Length > 0, modelPath,
            true, false, false, true,
            modelPath.Length > 0 ? textures : new AttachmentTextureDraft("", "", "", ""));
        CharacterItemAddPlan plan = CharacterItemAddPipeline.BuildVest(index, configuration,
            templateRowIndex, draft, metadata.LanguagePackage, archivePaths, localResources,
            localIndexes, characterArchivePath, entry.Index, entry.Name,
            new Dictionary<(string ArchivePath, int EntryIndex, int ResourceIndex), byte[]>(),
            [], tag => metadata.ByBuildTag.GetValueOrDefault(tag),
            localizationPackages: localizationPackages);
        var duplicateResources = plan.ResourceAdditions.GroupBy(addition =>
                (Path.GetFullPath(addition.ArchivePath).ToUpperInvariant(),
                    addition.EntryIndex, addition.ResourceId))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateResources is not null)
            throw new InvalidDataException($"The vest plan adds resource 0x{duplicateResources.Key.ResourceId:X12} "
                + $"({string.Join(", ", duplicateResources.Select(addition => addition.ResourceName))}) "
                + $"{duplicateResources.Count()} times to entry {duplicateResources.Key.EntryIndex} of "
                + Path.GetFileName(duplicateResources.First().ArchivePath) + ".");
        var duplicateEntries = plan.EntryAdditions.GroupBy(addition =>
                (Path.GetFullPath(addition.ArchivePath).ToUpperInvariant(), addition.EntryId))
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateEntries is not null)
            throw new InvalidDataException($"The vest plan adds container 0x{duplicateEntries.Key.EntryId:X12} "
                + $"({string.Join(", ", duplicateEntries.Select(addition => addition.EntryName))}) "
                + $"{duplicateEntries.Count()} times to "
                + Path.GetFileName(duplicateEntries.First().ArchivePath) + ".");
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
