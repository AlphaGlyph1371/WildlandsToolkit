using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public sealed record BuildTableOptionMetadata(
    uint BuildTag,
    ulong NameStringId,
    string DisplayName,
    int? DisplayValue,
    ulong? DescriptionStringId,
    string Description,
    ulong RecordId,
    string RecordName,
    uint RecordClassHash);

public sealed class BuildTableGameMetadata
{
    public static BuildTableGameMetadata Empty { get; } = new(new Dictionary<uint, BuildTableOptionMetadata>(), new HashSet<uint>(), Array.Empty<string>(), new HashSet<ulong>(), new Dictionary<ulong, IReadOnlyList<string>>(), 0, "");

    internal BuildTableGameMetadata(
        IReadOnlyDictionary<uint, BuildTableOptionMetadata> byBuildTag,
        IReadOnlySet<uint> ambiguousBuildTags,
        IReadOnlyList<string> ownerRecords,
        IReadOnlySet<ulong> ownerRecordIds,
        IReadOnlyDictionary<ulong, IReadOnlyList<string>> ownersByRecordId,
        int localizedStringCount,
        string languagePackage)
    {
        ByBuildTag = byBuildTag;
        AmbiguousBuildTags = ambiguousBuildTags;
        OwnerRecords = ownerRecords;
        OwnerRecordIds = ownerRecordIds;
        OwnersByRecordId = ownersByRecordId;
        LocalizedStringCount = localizedStringCount;
        LanguagePackage = languagePackage;
    }

    public IReadOnlyDictionary<uint, BuildTableOptionMetadata> ByBuildTag { get; }
    public IReadOnlySet<uint> AmbiguousBuildTags { get; }
    public IReadOnlyList<string> OwnerRecords { get; }
    public IReadOnlySet<ulong> OwnerRecordIds { get; }
    public IReadOnlyDictionary<ulong, IReadOnlyList<string>> OwnersByRecordId { get; }
    public int LocalizedStringCount { get; }
    public string LanguagePackage { get; }
}

public static class BuildTableGameMetadataResolver
{
    const uint BuildTagMarker = 0xB332698E;
    const uint LocalizedValueMarker = 0x81A7045D;
    const uint MagazineAttachmentClassHash = 0x801DEDC8;

    public static BuildTableGameMetadata Build(
        IReadOnlyList<string> archivePaths,
        IReadOnlyCollection<ulong> ownerEntityIds,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        string? preferredLanguagePackage = null,
        IReadOnlyCollection<uint>? buildTags = null)
    {
        string preferredPackage = string.IsNullOrWhiteSpace(preferredLanguagePackage)
            ? PreferredLanguagePackage()
            : preferredLanguagePackage;
        var localizationResources = new List<(int Priority, byte[] Data)>();
        var databaseResources = new List<Resource>();

        for (int archiveIndex = 0; archiveIndex < archivePaths.Count; archiveIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = archivePaths[archiveIndex];
            progress?.Report($"Reading confirmed game labels, {archiveIndex + 1} of {archivePaths.Count} archives…");

            try
            {
                using var archive = ForgeArchive.Open(path);
                foreach (var entry in archive.Entries.Where(entry => IsGameDatabaseContainer(entry.Name) || LocalizationPriority(entry.Name, preferredPackage) >= 0))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var stream = new MemoryStream(archive.ReadEntry(entry));
                    var file = DataFile.Read(stream);

                    int localizationPriority = LocalizationPriority(entry.Name, preferredPackage);
                    if (localizationPriority >= 0)
                    {
                        localizationResources.AddRange(file.Resources
                            .Where(resource => resource.ClassHash == LocalizationPackage.ClassHash)
                            .Select(resource => (localizationPriority, resource.Data)));
                    }
                    else if (IsGameDatabaseContainer(entry.Name))
                    {
                        databaseResources.AddRange(file.Resources);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Missing optional archives do not invalidate metadata read from the others
            }
        }

        var strings = new Dictionary<ulong, string>();
        foreach (var resource in localizationResources.OrderBy(resource => resource.Priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                foreach (var pair in LocalizationPackage.Read(resource.Data).Strings)
                    strings[pair.Key] = pair.Value;
            }
            catch
            {
                // A damaged language package must not create labels from partial data
            }
        }

        return BuildFromResources(databaseResources, strings, ownerEntityIds, buildTags, preferredPackage, cancellationToken);
    }

    public static BuildTableGameMetadata Build(ArmoryIndex index, IReadOnlyCollection<ulong> ownerEntityIds, string? preferredLanguagePackage = null,
        CancellationToken cancellationToken = default, IReadOnlyCollection<uint>? buildTags = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        string preferredPackage = string.IsNullOrWhiteSpace(preferredLanguagePackage)
            ? PreferredLanguagePackage()
            : preferredLanguagePackage;
        var resources = index.DatabaseResources
            .Select(resource => new Resource
            {
                Id = resource.Id,
                Name = resource.Name,
                ClassHash = resource.ClassHash,
                Data = resource.Data,
            })
            .ToList();
        return BuildFromResources(resources, index.StringsFor(preferredPackage), ownerEntityIds, buildTags, preferredPackage, cancellationToken);
    }

    static BuildTableGameMetadata BuildFromResources(IReadOnlyList<Resource> databaseResources, IReadOnlyDictionary<ulong, string> strings, IReadOnlyCollection<ulong> ownerEntityIds,
        IReadOnlyCollection<uint>? buildTags, string preferredPackage, CancellationToken cancellationToken)
    {
        var records = new List<BuildTableOptionMetadata>();
        foreach (var resource in databaseResources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadRecord(resource, strings, out var record))
                records.Add(record);
        }

        var recordIds = records.Select(record => record.RecordId).ToHashSet();
        var owners = databaseResources.Where(resource => ownerEntityIds.Any(id => ContainsUInt64(resource.Data, id))).ToList();
        var directlyLinkedIds = new HashSet<ulong>();
        var ownersByRecordId = new Dictionary<ulong, HashSet<string>>();
        foreach (var owner in owners)
            foreach (ulong recordId in recordIds)
                if (ContainsUInt64(owner.Data, recordId))
                {
                    directlyLinkedIds.Add(recordId);
                    if (!ownersByRecordId.TryGetValue(recordId, out var linkedOwners))
                        ownersByRecordId[recordId] = linkedOwners = new(StringComparer.OrdinalIgnoreCase);
                    linkedOwners.Add(owner.Name);
                }

        var requestedTags = buildTags?.ToHashSet() ?? [];
        var relevantRecords = records.Where(record => directlyLinkedIds.Contains(record.RecordId) || requestedTags.Contains(record.BuildTag)).ToList();
        var byTag = new Dictionary<uint, BuildTableOptionMetadata>();
        var ambiguous = new HashSet<uint>();
        foreach (var group in relevantRecords.GroupBy(record => record.BuildTag))
        {
            var linked = group.Where(record => directlyLinkedIds.Contains(record.RecordId)).ToList();
            var candidates = linked.Count > 0 ? linked : group.ToList();
            var names = candidates.Select(record => record.NameStringId).Distinct().ToList();
            if (names.Count != 1 || linked.Count == 0 && candidates.Select(record => record.RecordId).Distinct().Count() != 1)
            {
                ambiguous.Add(group.Key);
                continue;
            }

            byTag[group.Key] = candidates
                .OrderBy(record => record.RecordName, StringComparer.OrdinalIgnoreCase)
                .First();
        }

        var readOnlyOwners = ownersByRecordId.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value.Order(StringComparer.OrdinalIgnoreCase).ToList());
        return new BuildTableGameMetadata(byTag, ambiguous, owners.Select(owner => owner.Name).Order(StringComparer.OrdinalIgnoreCase).ToList(),
            owners.Select(owner => owner.Id).ToHashSet(), readOnlyOwners, strings.Count, strings.Count > 0 ? preferredPackage : "English(US)");
    }

    internal static bool IsGameDatabaseContainer(string name) => string.Equals(Path.GetFileNameWithoutExtension(name), "Game Bootstrap Settings", StringComparison.OrdinalIgnoreCase);

    static int LocalizationPriority(string name, string preferredPackage)
    {
        string? package = TryGetLanguagePackage(name);
        if (package is null)
            return -1;
        if (string.Equals(package, preferredPackage, StringComparison.OrdinalIgnoreCase))
            return 1;
        return string.Equals(package, "English(US)", StringComparison.OrdinalIgnoreCase) ? 0 : -1;
    }

    public static IReadOnlyList<string> FindAvailableLanguagePackages(IReadOnlyList<string> archivePaths, CancellationToken cancellationToken = default)
    {
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in archivePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var archive = ForgeArchive.Open(path);
                foreach (var entry in archive.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string? package = TryGetLanguagePackage(entry.Name);
                    if (package is not null)
                        packages.Add(package);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // An unavailable archive simply cannot contribute a language package
            }
        }

        return packages
            .OrderBy(package => string.Equals(package, "English(US)", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(package => package, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string PreferredLanguagePackage() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
    {
        "de" => "German",
        "fr" => "French(France)",
        "it" => "Italian",
        "ru" => "Russian",
        "es" => "Spanish(Spain)",
        "pt" => "Portuguese(Brazil)",
        "ja" => "Japanese",
        "ar" => "Arabic",
        "nl" => "Dutch",
        _ => "English(US)",
    };

    internal static string? TryGetLanguagePackage(string name)
    {
        string value = Path.GetFileNameWithoutExtension(name);
        const string prefix = "LocalizationPackage_";
        int index = value.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0 || value.Contains("Subtitles", StringComparison.OrdinalIgnoreCase) || value.Contains("EManual", StringComparison.OrdinalIgnoreCase))
            return null;

        string package = value[(index + prefix.Length)..];
        return string.IsNullOrWhiteSpace(package) ? null : package;
    }

    static bool TryReadRecord(Resource resource, IReadOnlyDictionary<ulong, string> strings, out BuildTableOptionMetadata record)
    {
        record = null!;
        ReadOnlySpan<byte> data = resource.Data;
        int tagMarker = IndexOfUInt32(data, BuildTagMarker, 12);
        if (tagMarker < 0 || tagMarker + 8 > data.Length)
            return false;
        uint tag = BinaryPrimitives.ReadUInt32LittleEndian(data[(tagMarker + 4)..]);

        var localized = new List<(ulong Id, string Text)>();
        int at = tagMarker + 8;
        while ((at = IndexOfUInt32(data, LocalizedValueMarker, at)) >= 0)
        {
            if (at + 8 > data.Length)
                break;
            ulong id = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            if (id != 0 && strings.TryGetValue(id, out string? text) && !string.IsNullOrWhiteSpace(text)) localized.Add((id, text));
            at += 8;
        }

        if (localized.Count == 0)
            return false;

        var name = localized[0];
        var description = localized.Skip(1).FirstOrDefault();
        int? displayValue = TryReadConfirmedMagazineCapacity(resource, name.Text, out int capacity)
            ? capacity
            : null;
        record = new BuildTableOptionMetadata(
            tag,
            name.Id,
            displayValue is int value
                ? name.Text.Replace("[VALUE]", value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                : name.Text,
            displayValue,
            description.Id == 0 ? null : description.Id,
            description.Text ?? "",
            resource.Id,
            resource.Name,
            resource.ClassHash);
        return true;
    }

    static bool TryReadConfirmedMagazineCapacity(Resource resource, string localizedName, out int capacity)
    {
        capacity = 0;
        if (resource.ClassHash != MagazineAttachmentClassHash || !localizedName.Contains("[VALUE]", StringComparison.Ordinal) || resource.Data.Length < 7)
            return false;

        ReadOnlySpan<byte> footer = resource.Data.AsSpan()[^7..];
        if (footer[0] != 0 || footer[2] != 0 || !footer[3..].SequenceEqual(new byte[] { 0xCD, 0xCC, 0xCC, 0x3D }))
            return false;

        capacity = footer[1];
        return capacity > 0;
    }

    static int IndexOfUInt32(ReadOnlySpan<byte> data, uint value, int start)
    {
        if (start < 0)
            start = 0;
        byte[] needle = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(needle, value);
        int relative = data[start..].IndexOf(needle);
        return relative < 0 ? -1 : start + relative;
    }

    static bool ContainsUInt64(ReadOnlySpan<byte> data, ulong value)
    {
        byte[] needle = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(needle, value);
        return data.IndexOf(needle) >= 0;
    }

    internal static void CollectLocalizedStringIds(ReadOnlySpan<byte> data, ISet<ulong> ids)
    {
        int tagMarker = IndexOfUInt32(data, BuildTagMarker, 12);
        if (tagMarker < 0)
            return;

        int at = tagMarker + 8;
        while ((at = IndexOfUInt32(data, LocalizedValueMarker, at)) >= 0)
        {
            if (at + 8 > data.Length)
                return;
            ulong id = BinaryPrimitives.ReadUInt32LittleEndian(data[(at + 4)..]);
            if (id != 0)
                ids.Add(id);
            at += 8;
        }
    }
}
