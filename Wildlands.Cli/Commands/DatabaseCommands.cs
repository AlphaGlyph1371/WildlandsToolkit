using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Wildlands.Formats.Models;
using Wildlands.Formats.Materials;
using Wildlands.Formats.Textures;
using Wildlands.Formats.Weather;
using Wildlands.Toolkit;

static partial class Commands
{
    internal static int InspectDatabaseLists(string archivePath, string containerFilter, ulong resourceId,
        IReadOnlyList<ulong> wanted)
    {
        using var archive = ForgeArchive.Open(archivePath);
        foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"
                     && entry.Name.Contains(containerFilter, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
            Resource? resource = file.Resources.FirstOrDefault(candidate => candidate.Id == resourceId);
            if (resource is null)
                continue;
    
            byte[] data = resource.Data;
            Console.WriteLine($"{entry.Name} | 0x{resource.Id:X12} {resource.Name}, {data.Length} bytes");
            for (int stride = 8; stride <= 12; stride++)
            for (int valueOffset = 0; valueOffset + sizeof(ulong) <= stride; valueOffset++)
            {
                for (int offset = 0; offset + sizeof(uint) <= data.Length; offset++)
                {
                    uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
                    if (count is < 3 or > 65_535
                        || offset + sizeof(uint) + (long)count * stride > data.Length)
                        continue;
    
                    var ids = new ulong[count];
                    bool valid = true;
                    for (int item = 0; item < ids.Length && valid; item++)
                    {
                        int entryOffset = offset + sizeof(uint) + item * stride;
                        ulong id = BinaryPrimitives.ReadUInt64LittleEndian(
                            data.AsSpan(entryOffset + valueOffset));
                        valid = id is > 0xFFFF and <= 0xFFFFFFFFFFFF;
                        ids[item] = id;
                    }
                    if (!valid || ids.Distinct().Count() != ids.Length)
                        continue;
                    int present = wanted.Count(id => ids.Contains(id));
                    if (wanted.Count > 0 && present == 0)
                        continue;
                    Console.WriteLine($"  count 0x{offset:X} = {count} | stride {stride} "
                        + $"| value at +{valueOffset} | {present}/{wanted.Count} wanted "
                        + $"| first 0x{ids[0]:X12} last 0x{ids[^1]:X12}");
                }
            }
        }
        return 0;
    }

    internal static int FindReferences(string path, string what)
    {
        var file = DataFile.Read(path);
    
        ulong id;
        if (what.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            id = Convert.ToUInt64(what[2..], 16);
        else
        {
            var target = file.Resources.FirstOrDefault(r => r.Name.Equals(what, StringComparison.OrdinalIgnoreCase));
            if (target is null)
            {
                Console.WriteLine("no resource named " + what);
                return 1;
            }
            id = target.Id;
        }
    
        var needle = BitConverter.GetBytes(id);
        int found = 0;
    
        Console.WriteLine($"0x{id:X12}  held by:");
    
        foreach (var resource in file.Resources)
        {
            if (resource.Id == id) continue;
    
            var data = resource.Data.AsSpan();
            for (int i = 0; i + 8 <= data.Length; i++)
            {
                if (!data.Slice(i, 8).SequenceEqual(needle)) continue;
    
                Console.WriteLine($"  {ResourceTypes.NameOf(resource.ClassHash),-32} at {i,7}  {resource.Name}");
                found++;
                break;
            }
        }
    
        Console.WriteLine(found == 0 ? "  nothing" : $"  {found} resources");
        return 0;
    }

    internal static int FindForgeReferences(string archivePath, string containerFilter,
        IReadOnlyList<string> texts)
    {
        ulong[] wanted = texts.Select(text => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt64(text[2..], 16)
                : Convert.ToUInt64(text))
            .Distinct()
            .ToArray();
        int found = 0;
    
        using var archive = ForgeArchive.Open(archivePath);
        foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"
                     && entry.Name.Contains(containerFilter, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
            foreach (Resource resource in file.Resources)
            {
                foreach (ulong value in wanted)
                {
                    byte[] needle = BitConverter.GetBytes(value);
                    int start = 0;
                    while (start <= resource.Data.Length - needle.Length)
                    {
                        int relative = resource.Data.AsSpan(start).IndexOf(needle);
                        if (relative < 0)
                            break;
                        int offset = start + relative;
                        int contextStart = Math.Max(0, offset - 24);
                        int contextLength = Math.Min(resource.Data.Length - contextStart, 56);
                        Console.WriteLine($"0x{value:X12}  {resource.Name}  "
                            + $"{ResourceTypes.NameOf(resource.ClassHash)}  offset 0x{offset:X}  "
                            + Convert.ToHexString(resource.Data.AsSpan(contextStart, contextLength)));
                        found++;
                        start = offset + 1;
                    }
                }
            }
        }
    
        Console.WriteLine($"{found} exact occurrence(s) in containers matching {containerFilter}");
        return found == 0 ? 1 : 0;
    }

    internal static int FindForge32BitValues(string archivePath, string containerFilter,
        IReadOnlyList<string> texts)
    {
        uint[] wanted = texts.Select(text => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt32(text[2..], 16)
                : Convert.ToUInt32(text))
            .Distinct()
            .ToArray();
        int found = 0;
    
        using var archive = ForgeArchive.Open(archivePath);
        foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"
                     && entry.Name.Contains(containerFilter, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
            foreach (Resource resource in file.Resources)
            {
                foreach (uint value in wanted)
                {
                    byte[] needle = BitConverter.GetBytes(value);
                    var offsets = new List<int>();
                    int start = 0;
                    while (start <= resource.Data.Length - needle.Length)
                    {
                        int relative = resource.Data.AsSpan(start).IndexOf(needle);
                        if (relative < 0)
                            break;
                        offsets.Add(start + relative);
                        start += relative + 1;
                    }
                    if (offsets.Count == 0)
                        continue;
                    Console.WriteLine($"0x{value:X8}  {entry.Name}  {resource.Name}  "
                        + $"{ResourceTypes.NameOf(resource.ClassHash)}  "
                        + $"{offsets.Count} occurrence(s) at "
                        + string.Join(",", offsets.Select(offset => $"0x{offset:X}")));
                    found += offsets.Count;
                }
            }
        }
    
        Console.WriteLine($"{found} exact occurrence(s) in containers matching {containerFilter}");
        return found == 0 ? 1 : 0;
    }

    internal static int FindForgeResourceNames(string archivePath, string containerFilter,
        string resourceFilter)
    {
        int found = 0;
        using var archive = ForgeArchive.Open(archivePath);
        foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"
                     && entry.Name.Contains(containerFilter, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
            foreach (Resource resource in file.Resources.Where(resource =>
                         resource.Name.Contains(resourceFilter, StringComparison.OrdinalIgnoreCase)))
            {
                Console.WriteLine($"0x{resource.Id:X12}  {ResourceTypes.NameOf(resource.ClassHash),-32} "
                    + $"{resource.Name}  in [{entry.Index}] {entry.Name}");
                found++;
            }
        }
    
        Console.WriteLine($"{found} matching resource(s)");
        return found == 0 ? 1 : 0;
    }

    internal static int CompareDatabaseResources(string archivePath, string containerFilter,
        string baselineText, IReadOnlyList<string> candidateTexts)
    {
        ulong baselineId = ParseResourceId(baselineText);
        ulong[] candidateIds = candidateTexts.Select(ParseResourceId).Distinct().ToArray();
        var wanted = candidateIds.Append(baselineId).ToHashSet();
        var resources = new Dictionary<ulong, Resource>();
        var knownIds = new HashSet<ulong>();
    
        using var archive = ForgeArchive.Open(archivePath);
        foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"
                     && entry.Name.Contains(containerFilter, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
            knownIds.UnionWith(file.Resources.Select(resource => resource.Id));
            foreach (Resource resource in file.Resources.Where(resource => wanted.Contains(resource.Id)))
                resources[resource.Id] = resource;
        }
    
        if (!resources.TryGetValue(baselineId, out Resource? baseline))
        {
            Console.WriteLine($"baseline 0x{baselineId:X12} was not found");
            return 1;
        }
    
        Console.WriteLine($"baseline 0x{baseline.Id:X12} {baseline.Name}: {baseline.Data.Length:N0} bytes");
        HashSet<ulong> baselineReferences = KnownResourceReferences(baseline.Data, knownIds, baseline.Id);
        foreach (ulong candidateId in candidateIds)
        {
            if (!resources.TryGetValue(candidateId, out Resource? candidate))
            {
                Console.WriteLine($"0x{candidateId:X12}: not found");
                continue;
            }
    
            int compared = Math.Min(baseline.Data.Length, candidate.Data.Length);
            int equal = 0;
            for (int offset = 0; offset < compared; offset++)
                if (baseline.Data[offset] == candidate.Data[offset])
                    equal++;
            HashSet<ulong> candidateReferences = KnownResourceReferences(candidate.Data, knownIds, candidate.Id);
            int sharedReferences = baselineReferences.Intersect(candidateReferences).Count();
            int combinedReferences = baselineReferences.Union(candidateReferences).Count();
            int subsequence = LongestCommonSubsequenceLength(baseline.Data, candidate.Data);
            HashSet<ulong> baselineWindows = ByteWindows(baseline.Data, 8);
            HashSet<ulong> candidateWindows = ByteWindows(candidate.Data, 8);
            int sharedWindows = baselineWindows.Intersect(candidateWindows).Count();
            int combinedWindows = baselineWindows.Union(candidateWindows).Count();
            Console.WriteLine($"0x{candidate.Id:X12} {candidate.Name}: {candidate.Data.Length:N0} bytes, "
                + $"{equal:N0}/{compared:N0} aligned bytes equal ({equal * 100d / compared:N2}%), "
                + $"LCS {subsequence:N0}/{compared:N0} ({subsequence * 100d / compared:N2}%), "
                + $"8-byte windows {sharedWindows:N0}/{combinedWindows:N0}, "
                + $"{sharedReferences}/{combinedReferences} known references shared");
        }
        return 0;
    }

    internal static int PrintDatabaseResource(string archivePath, string containerFilter, string idText,
        int count, int startOffset)
    {
        ulong id = ParseResourceId(idText);
        using var archive = ForgeArchive.Open(archivePath);
        foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"
                     && entry.Name.Contains(containerFilter, StringComparison.OrdinalIgnoreCase)))
        {
            if (!TryReadDataFile(archive, entry, out DataFile file))
                continue;
    
            Resource? resource = file.Resources.FirstOrDefault(resource => resource.Id == id);
            if (resource is null)
                continue;
            int start = Math.Clamp(startOffset, 0, resource.Data.Length);
            int length = Math.Min(Math.Max(count, 0), resource.Data.Length - start);
            Console.WriteLine($"0x{resource.Id:X12} {resource.Name}, {resource.Data.Length:N0} bytes");
            for (int relative = 0; relative < length; relative += 16)
            {
                int lineLength = Math.Min(16, length - relative);
                int offset = start + relative;
                Console.WriteLine($"{offset:X4}  {Convert.ToHexString(resource.Data.AsSpan(offset, lineLength))}");
            }
            return 0;
        }
        Console.WriteLine($"0x{id:X12} was not found in containers matching {containerFilter}");
        return 1;
    }

    internal static int FindLocalizedString(string gameFolder, string idText)
    {
        ulong id = ParseResourceId(idText);
        int found = 0;
        foreach (string path in ArchiveLocator.Find(gameFolder))
        {
            using var archive = ForgeArchive.Open(path);
            foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"
                         && entry.Name.StartsWith("LocalizationPackage_", StringComparison.OrdinalIgnoreCase)))
            {
                DataFile file;
                try
                {
                    using var stream = new MemoryStream(archive.ReadEntry(entry));
                    file = DataFile.Read(stream);
                }
                catch
                {
                    continue;
                }
    
                foreach (Resource resource in file.Resources.Where(resource =>
                             resource.ClassHash == LocalizationPackage.ClassHash))
                {
                    try
                    {
                        if (!LocalizationPackage.Read(resource.Data).Strings.TryGetValue(id,
                                out string? value))
                            continue;
                        Console.WriteLine($"{Path.GetFileName(path)} | {entry.Name} | "
                            + $"0x{resource.Id:X12} {resource.Name} | {value}");
                        found++;
                    }
                    catch
                    {
                    }
                }
            }
        }
        Console.WriteLine($"{found} localization package match(es) for 0x{id:X8}");
        return found == 0 ? 1 : 0;
    }

    internal static int MeasureLocalizedStringIds(string gameFolder)
    {
        int packages = 0;
        long strings = 0;
        long highBit = 0;
        ulong maximum = 0;
        foreach (string path in ArchiveLocator.Find(gameFolder))
        {
            using var archive = ForgeArchive.Open(path);
            foreach (ForgeEntry entry in archive.Entries.Where(entry => entry.FileExtension == ".data"
                         && entry.Name.StartsWith("LocalizationPackage_", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    using var stream = new MemoryStream(archive.ReadEntry(entry));
                    foreach (Resource resource in DataFile.Read(stream).Resources.Where(resource =>
                                 resource.ClassHash == LocalizationPackage.ClassHash))
                    {
                        LocalizationPackageAsset package = LocalizationPackage.Read(resource.Data);
                        packages++;
                        strings += package.Strings.Count;
                        highBit += package.Strings.Keys.Count(id => id > int.MaxValue);
                        if (package.Strings.Count > 0)
                            maximum = Math.Max(maximum, package.Strings.Keys.Max());
                    }
                }
                catch
                {
                }
            }
        }
        Console.WriteLine($"{packages:N0} packages, {strings:N0} strings, {highBit:N0} ids above "
            + $"0x7FFFFFFF, maximum 0x{maximum:X8}");
        return packages == 0 ? 1 : 0;
    }

    internal static int Find64BitValues(string path, IReadOnlyList<string> values)
    {
        var wanted = values.Select(value => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt64(value[2..], 16)
                : Convert.ToUInt64(value))
            .Distinct()
            .ToArray();
        string[] files = Directory.Exists(path)
            ? Directory.GetFiles(path, "*.data", SearchOption.AllDirectories)
            : [path];
        var hits = new ConcurrentBag<string>();
        int failed = 0;
    
        Parallel.ForEach(files, new ParallelOptions { MaxDegreeOfParallelism = 4 }, filePath =>
        {
            DataFile file;
            try { file = DataFile.Read(filePath); }
            catch
            {
                Interlocked.Increment(ref failed);
                return;
            }
    
            foreach (var resource in file.Resources)
            {
                var data = resource.Data.AsSpan();
                foreach (ulong value in wanted)
                {
                    byte[] needle = BitConverter.GetBytes(value);
                    int offset = data.IndexOf(needle);
                    if (offset < 0)
                        continue;
                    hits.Add($"{Path.GetFileName(filePath)} | {resource.Name} | "
                        + $"{ResourceTypes.NameOf(resource.ClassHash)} (0x{resource.ClassHash:X8}) | "
                        + $"resource 0x{resource.Id:X} | offset 0x{offset:X} | value {value} (0x{value:X})");
                }
            }
        });
    
        foreach (string hit in hits.Order(StringComparer.OrdinalIgnoreCase))
            Console.WriteLine(hit);
        Console.WriteLine($"scanned {files.Length} container(s), {hits.Count} hit(s), {failed} unreadable");
        return failed == files.Length ? 1 : 0;
    }

    internal static int InspectArmoryMetadata(string gameFolder, IReadOnlyList<string> values)
    {
        var tags = values.Select(value => value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToUInt32(value[2..], 16)
                : Convert.ToUInt32(value))
            .Distinct()
            .ToArray();
        var archivePaths = ArchiveLocator.Find(gameFolder);
        if (archivePaths.Count == 0)
        {
            Console.WriteLine("no forge archives in " + gameFolder);
            return 1;
        }
    
        ArmoryIndex index = ArmoryIndex.Load(AppSettings.ArmoryCachePath, archivePaths)
            ?? ArmoryIndex.Build(archivePaths);
        BuildTableGameMetadata metadata = BuildTableGameMetadataResolver.Build(index, [],
            preferredLanguagePackage: "English(US)", buildTags: tags);
        foreach (uint tag in tags)
        {
            if (metadata.ByBuildTag.TryGetValue(tag, out BuildTableOptionMetadata? record))
            {
                Console.WriteLine($"0x{tag:X8}  0x{record.RecordId:X12}  "
                    + $"class 0x{record.RecordClassHash:X8}  {record.RecordName}  |  "
                    + record.DisplayName);
            }
            else if (metadata.AmbiguousBuildTags.Contains(tag))
            {
                Console.WriteLine($"0x{tag:X8}  ambiguous");
            }
            else
            {
                Console.WriteLine($"0x{tag:X8}  no localized gameplay record");
            }
        }
        return 0;
    }

    internal static int InspectArmoryRegistries(string gameFolder, string value)
    {
        ulong memberId = ParseResourceId(value);
        var archivePaths = ArchiveLocator.Find(gameFolder);
        if (archivePaths.Count == 0)
        {
            Console.WriteLine("no forge archives in " + gameFolder);
            return 1;
        }
    
        ArmoryIndex index = ArmoryIndex.Load(AppSettings.ArmoryCachePath, archivePaths) ?? ArmoryIndex.Build(archivePaths);
        IReadOnlyList<ArmoryObjectListEvidence> characterSmith = index.FindCharacterSmithRegistryEvidence(memberId);
        foreach (ArmoryObjectListEvidence list in characterSmith)
            Console.WriteLine($"verified CharacterSmith: 0x{list.OwnerResourceId:X12} class 0x{list.OwnerClassHash:X8} {list.OwnerName} | "
                + $"{Path.GetFileName(list.ArchivePath)}/{list.EntryName} resource {list.ResourceIndex} | list header 0x{list.HeaderOffset:X}, "
                + $"count offset 0x{list.CountOffset:X}, {list.Count} objects of class 0x{list.EntryClassHash:X8}");
    
        IReadOnlyList<ArmoryRegistryEvidence> vests = index.FindVestRegistryEvidence(memberId);
        foreach (ArmoryRegistryEvidence list in vests)
            Console.WriteLine($"verified vests: 0x{list.OwnerResourceId:X12} class 0x{list.OwnerClassHash:X8} {list.OwnerName} | "
                + $"{Path.GetFileName(list.ArchivePath)}/{list.EntryName} resource {list.ResourceIndex} | count offset 0x{list.CountOffset:X}, "
                + $"{list.Members.Count} rows, layout {list.EntryStride}/+{list.ValueOffset}");
    
        IReadOnlyList<ArmoryRegistryEvidence> unlockables = index.FindUnlockableRegistryEvidence(memberId);
        foreach (ArmoryRegistryEvidence list in unlockables)
            Console.WriteLine($"verified unlockables: 0x{list.OwnerResourceId:X12} class 0x{list.OwnerClassHash:X8} {list.OwnerName} | "
                + $"{Path.GetFileName(list.ArchivePath)}/{list.EntryName} resource {list.ResourceIndex} | count offset 0x{list.CountOffset:X}, "
                + $"{list.Members.Count} rows, layout {list.EntryStride}/+{list.ValueOffset}");
    
        ReportVerifiedRegistry("database container", index.FindDatabaseContainerRegistryEvidence(memberId));
        ReportVerifiedRegistry("loot", index.FindLootRegistryEvidence(memberId));
        IReadOnlyList<ArmoryRegistryEvidence> store = index.FindStoreRegistryEvidence(memberId);
        ReportVerifiedRegistry("store-object", store);
    
        IReadOnlyList<ArmoryRegistryEvidence> evidence = index.FindRegistryEvidence(memberId);
        foreach (var group in evidence.GroupBy(list => (list.OwnerResourceId, list.OwnerClassHash, list.OwnerName, list.ArchivePath, list.EntryIndex,
                     list.EntryName, list.ResourceIndex)))
        {
            var candidates = group.ToList();
            string layouts = string.Join(", ", candidates.Select(list => $"{list.EntryStride}/+{list.ValueOffset}").Distinct());
            Console.WriteLine($"0x{group.Key.OwnerResourceId:X12} class 0x{group.Key.OwnerClassHash:X8} {group.Key.OwnerName} | "
                + $"{Path.GetFileName(group.Key.ArchivePath)}/{group.Key.EntryName} resource {group.Key.ResourceIndex} | "
                + $"{candidates.Count} candidate(s), layouts {layouts}, counts {candidates.Min(list => list.Members.Count)}-{candidates.Max(list => list.Members.Count)}");
        }
        Console.WriteLine($"{characterSmith.Count} verified CharacterSmith list(s), {vests.Count} verified vests table(s), {unlockables.Count} verified unlockables table(s), "
            + $"{store.Count} verified store-object table(s), and "
            + $"{evidence.Count} unverified binary list candidate(s) contain 0x{memberId:X12}");
        return characterSmith.Count == 0 && vests.Count == 0 && unlockables.Count == 0 && store.Count == 0 && evidence.Count == 0 ? 1 : 0;
    
        static void ReportVerifiedRegistry(string role, IReadOnlyList<ArmoryRegistryEvidence> lists)
        {
            foreach (ArmoryRegistryEvidence list in lists)
                Console.WriteLine($"verified {role}: 0x{list.OwnerResourceId:X12} class 0x{list.OwnerClassHash:X8} {list.OwnerName} | "
                    + $"{Path.GetFileName(list.ArchivePath)}/{list.EntryName} resource {list.ResourceIndex} | count offset 0x{list.CountOffset:X}, "
                    + $"{list.Members.Count} rows, layout {list.EntryStride}/+{list.ValueOffset}");
        }
    }

    internal static int CheckCharacterSmithInsertion(string gameFolder, string value)
    {
        ulong templateRecordId = ParseResourceId(value);
        var archivePaths = ArchiveLocator.Find(gameFolder);
        ArmoryIndex index = ArmoryIndex.Load(AppSettings.ArmoryCachePath, archivePaths) ?? ArmoryIndex.Build(archivePaths);
        ulong newRecordId = 0x00FFFF000001;
    
        ArmoryDatabaseResourceChange change = index.CreateCharacterSmithInsertion(templateRecordId, newRecordId);
        Console.WriteLine($"0x{templateRecordId:X12} -> 0x{newRecordId:X12}: CharacterSmith insertion is structurally stable in memory; "
            + $"{change.Data.Length:N0} bytes, no archive written");
        return 0;
    }

    internal static int CheckVestRegistryInsertions(string gameFolder, string value)
    {
        ulong templateRecordId = ParseResourceId(value);
        var archivePaths = ArchiveLocator.Find(gameFolder);
        ArmoryIndex index = ArmoryIndex.Load(AppSettings.ArmoryCachePath, archivePaths) ?? ArmoryIndex.Build(archivePaths);
        const ulong newRecordId = 0x00FFFF000001;
        IReadOnlyList<ArmoryDatabaseResourceChange> changes = index.CreateVestRegistryInsertions(templateRecordId, newRecordId);
        foreach (ArmoryDatabaseResourceChange change in changes)
            Console.WriteLine($"verified in memory: {Path.GetFileName(change.ArchivePath)}/{change.EntryName} resource {change.ResourceIndex} {change.ResourceName}, {change.Data.Length:N0} bytes");
        Console.WriteLine($"{changes.Count} proven registry resources accept 0x{newRecordId:X12}; no archive written");
        return 0;
    }

    internal static int CheckVestAddPlan(string gameFolder, string archivePath, string modelPath,
        string diffusePath, string normalPath, string mask1Path, int rowIndex)
    {
        CharacterVestAddValidationResult result = CharacterVestAddValidator.Validate(gameFolder,
            archivePath, rowIndex, "Virtus validation vest", "CODEX_VirtusValidation", modelPath,
            new AttachmentTextureDraft(diffusePath, normalPath, "", mask1Path));
        Console.WriteLine($"complete in-memory vest plan: {result.ConfigurationRows} configuration rows, "
            + $"{result.LocalChanges} local compatibility changes, {result.DatabaseChanges} database changes, "
            + $"{result.ResourceAdditions} data resources, {result.EntryAdditions} asset containers, "
            + $"BuildTag 0x{result.GameplayTag:X8}, record 0x{result.GameplayRecordId:X12}; no archive written");
        return 0;
    }

    internal static int CreateVestPackage(string gameFolder, string archivePath, string modelPath,
        string diffusePath, string normalPath, string mask1Path, string projectFolder,
        string packagePath, int rowIndex, string internalName, string displayName)
    {
        if (modelPath == "-")
            modelPath = "";
        if (diffusePath == "-")
            diffusePath = "";
        if (normalPath == "-")
            normalPath = "";
        if (mask1Path == "-")
            mask1Path = "";
        CharacterVestPackageResult result = CharacterVestAddValidator.CreatePackage(gameFolder,
            archivePath, rowIndex, displayName, internalName, modelPath,
            new AttachmentTextureDraft(diffusePath, normalPath, "", mask1Path),
            projectFolder, packagePath, "Wildlands Toolkit", "1.0.0", "German");
        CharacterVestAddValidationResult validation = result.Validation;
        Console.WriteLine($"created {result.PackagePath}");
        Console.WriteLine($"project: {result.ProjectPath}");
        Console.WriteLine($"{result.Operations} package operations compile to {result.PlannedChanges} changes in "
            + $"{result.Archives} archives; {validation.ConfigurationRows} vest rows, "
            + $"BuildTag 0x{validation.GameplayTag:X8}, record 0x{validation.GameplayRecordId:X12}; nothing installed");
        return 0;
    }

    internal static int CheckStoreRegistryInsertion(string gameFolder, string value)
    {
        ulong templateInfoId = ParseResourceId(value);
        var archivePaths = ArchiveLocator.Find(gameFolder);
        ArmoryIndex index = ArmoryIndex.Load(AppSettings.ArmoryCachePath, archivePaths) ?? ArmoryIndex.Build(archivePaths);
        const ulong newInfoId = 0x00FFFF000002;
        ArmoryDatabaseResourceChange change = index.CreateStoreRegistryInsertion(templateInfoId, newInfoId);
        Console.WriteLine($"0x{templateInfoId:X12} -> 0x{newInfoId:X12}: StoreObjectInfo registry insertion is structurally stable in memory; "
            + $"{change.Data.Length:N0} bytes, no archive written");
        return 0;
    }

    internal static int CheckStoreObjectInfoClone(string gameFolder, string infoValue, string recordValue)
    {
        ulong templateInfoId = ParseResourceId(infoValue);
        ulong templateRecordId = ParseResourceId(recordValue);
        var archivePaths = ArchiveLocator.Find(gameFolder);
        ArmoryIndex index = ArmoryIndex.Load(AppSettings.ArmoryCachePath, archivePaths) ?? ArmoryIndex.Build(archivePaths);
        const ulong newInfoId = 0x00FFFF000002;
        const ulong newRecordId = 0x00FFFF000001;
        byte[] data = index.CreateStoreObjectInfoClone(templateInfoId, newInfoId, templateRecordId, newRecordId, "CodexStoreObjectInfoProbe");
        Console.WriteLine($"0x{templateInfoId:X12}/0x{templateRecordId:X12} -> 0x{newInfoId:X12}/0x{newRecordId:X12}: "
            + $"StoreObjectInfo clone is structurally stable in memory; {data.Length:N0} bytes, no archive written");
        return 0;
    }

    internal static HashSet<ulong> KnownResourceReferences(ReadOnlySpan<byte> data,
        IReadOnlySet<ulong> knownIds, ulong self)
    {
        var references = new HashSet<ulong>();
        for (int offset = 0; offset <= data.Length - sizeof(ulong); offset++)
        {
            ulong id = BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]);
            if (id != self && knownIds.Contains(id))
                references.Add(id);
        }
        return references;
    }

    internal static int LongestCommonSubsequenceLength(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        if (right.Length > left.Length)
            return LongestCommonSubsequenceLength(right, left);
    
        int[] previous = new int[right.Length + 1];
        int[] current = new int[right.Length + 1];
        for (int leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            for (int rightIndex = 1; rightIndex <= right.Length; rightIndex++)
                current[rightIndex] = left[leftIndex - 1] == right[rightIndex - 1]
                    ? previous[rightIndex - 1] + 1
                    : Math.Max(previous[rightIndex], current[rightIndex - 1]);
            (previous, current) = (current, previous);
            Array.Clear(current);
        }
        return previous[^1];
    }

    internal static HashSet<ulong> ByteWindows(ReadOnlySpan<byte> data, int size)
    {
        var windows = new HashSet<ulong>();
        for (int offset = 0; offset <= data.Length - size; offset++)
            windows.Add(BinaryPrimitives.ReadUInt64LittleEndian(data[offset..]));
        return windows;
    }
}
