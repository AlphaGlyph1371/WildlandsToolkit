using System.Diagnostics;
using System.Buffers.Binary;
using System.IO;
using System.Text.Json;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Materials;
using Wildlands.Formats.Models;
using Wildlands.Formats.Textures;
using Wildlands.Formats.Weather;

static partial class Commands
{
    sealed class ParserAuditReport
    {
        public string GameFolder { get; set; } = "";
        public DateTimeOffset StartedUtc { get; set; }
        public double ElapsedSeconds { get; set; }
        public int Archives { get; set; }
        public long ForgeEntries { get; set; }
        public long DataContainers { get; set; }
        public long Resources { get; set; }
        public long TotalFailures { get; set; }
        public Dictionary<string, ParserAuditStat> Parsers { get; set; } = [];
        public Dictionary<string, long> ResourceClasses { get; set; } = [];
        public Dictionary<string, long> FailureReasons { get; set; } = [];
        public List<ParserAuditFailure> Failures { get; set; } = [];
    }

    sealed class ParserAuditStat
    {
        public string Mode { get; set; } = "";
        public long Seen { get; set; }
        public long Passed { get; set; }
        public long ByteExact { get; set; }
        public long Different { get; set; }
        public long Failed { get; set; }
        public Dictionary<string, long> Notes { get; set; } = [];
    }

    sealed record ParserAuditFailure(string Archive, string Container, string Resource,
        string Parser, string Error);

    internal static int AuditAllParsers(string gameFolder, string? outputPath, bool fast)
    {
        gameFolder = Path.GetFullPath(gameFolder);
        if (!Directory.Exists(gameFolder))
            throw new DirectoryNotFoundException(gameFolder);

        string[] archives = Directory.EnumerateFiles(gameFolder, "*.forge", SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar)
                .Any(part => part.Equals("Backups", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (archives.Length == 0)
            throw new InvalidDataException("The folder contains no Forge archives.");

        var report = new ParserAuditReport
        {
            GameFolder = gameFolder,
            StartedUtc = DateTimeOffset.UtcNow,
        };
        var timer = Stopwatch.StartNew();
        for (int archiveIndex = 0; archiveIndex < archives.Length; archiveIndex++)
        {
            string archivePath = archives[archiveIndex];
            Console.WriteLine($"[{archiveIndex + 1}/{archives.Length}] {Path.GetRelativePath(gameFolder, archivePath)}");
            try
            {
                AuditArchive(archivePath, report, fast);
                report.Archives++;
            }
            catch (Exception error)
            {
                Fail(report, archivePath, "", "", "ForgeArchive", error);
            }
            Console.WriteLine($"  {report.DataContainers:N0} container(s), {report.Resources:N0} resource(s), "
                + $"{report.TotalFailures:N0} failure(s), {timer.Elapsed:hh\\:mm\\:ss}");
        }

        timer.Stop();
        report.ElapsedSeconds = timer.Elapsed.TotalSeconds;
        PrintParserAudit(report);

        outputPath ??= Path.Combine(Environment.CurrentDirectory, "parser-audit.json");
        outputPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"report -> {outputPath}");

        bool failed = report.TotalFailures > 0
            || report.Parsers.Values.Any(stat => stat.Mode == "byte-exact writer" && stat.Different > 0);
        return failed ? 1 : 0;
    }

    static void AuditArchive(string archivePath, ParserAuditReport report, bool fast)
    {
        using var archive = ForgeArchive.Open(archivePath);
        report.ForgeEntries += archive.Entries.Count;
        byte[]? prefetchBytes = null;
        string prefetchName = "";
        var dataIds = new HashSet<ulong>();
        foreach (ForgeEntry entry in archive.Entries)
        {
            byte[] bytes;
            try
            {
                bytes = archive.ReadEntry(entry);
            }
            catch (Exception error)
            {
                Fail(report, archivePath, entry.Name, "", "ForgeEntry", error);
                continue;
            }

            if (entry.Id == GlobalMetaFile.EntryId)
            {
                CheckExact(report, "GlobalMetaFile", archivePath, entry.Name, entry.Name, bytes,
                    () => GlobalMetaFile.Write(GlobalMetaFile.Read(bytes)));
                continue;
            }
            if (entry.Id == 145)
            {
                prefetchBytes = bytes;
                prefetchName = entry.Name;
                continue;
            }
            if (entry.Id is 0 or 193 or 4090)
                continue;

            if (bytes.Length < sizeof(ulong)
                || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != CompressedBlob.Magic)
                continue;

            DataFile file;
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                file = DataFile.Read(input);
            }
            catch (Exception error)
            {
                if (LooksLikeStrictDataFile(bytes))
                    Fail(report, archivePath, entry.Name, "", "DataFile.Read", error);
                continue;
            }

            report.DataContainers++;
            dataIds.Add(entry.Id);
            if (!fast)
                CheckDataFile(report, archivePath, entry.Name, bytes, file);
            foreach (Resource resource in file.Resources)
            {
                report.Resources++;
                string className = ResourceTypes.NameOf(resource.ClassHash);
                string censusKey = $"{className} (0x{resource.ClassHash:X8})";
                report.ResourceClasses[censusKey] = report.ResourceClasses.GetValueOrDefault(censusKey) + 1;
                AuditResource(report, archivePath, entry.Name, resource);
            }
        }

        if (prefetchBytes is not null)
            CheckPrefetch(report, archivePath, prefetchName, prefetchBytes, dataIds);
    }

    static bool LooksLikeStrictDataFile(byte[] bytes)
    {
        if (bytes.Length < sizeof(ulong)
            || BinaryPrimitives.ReadUInt64LittleEndian(bytes) != CompressedBlob.Magic)
            return false;

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            byte[] index = CompressedBlob.Read(reader);
            if (index.Length < sizeof(ushort))
                return false;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(index);
            if (index.Length != sizeof(ushort) + count * 14)
                return false;
            _ = CompressedBlob.Read(reader);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    static void CheckDataFile(ParserAuditReport report, string archive, string container,
        byte[] originalBytes, DataFile original)
    {
        ParserAuditStat stat = Stat(report, "DataFile", "structural writer");
        stat.Seen++;
        try
        {
            using var output = new MemoryStream(originalBytes.Length);
            original.Write(output);
            bool hasBuffer = output.TryGetBuffer(out ArraySegment<byte> buffer);
            bool byteExact = output.Length == originalBytes.Length && hasBuffer
                && originalBytes.AsSpan().SequenceEqual(buffer.AsSpan(0, checked((int)output.Length)));
            output.Position = 0;
            DataFile again = DataFile.Read(output);
            if (!SameResources(original.Resources, again.Resources, out string difference))
                throw new InvalidDataException(difference);
            stat.Passed++;
            if (byteExact) stat.ByteExact++;
            else stat.Different++;
        }
        catch (Exception error)
        {
            stat.Failed++;
            Fail(report, archive, container, "", "DataFile", error, countParser: false);
        }
    }

    static bool SameResources(IReadOnlyList<Resource> left, IReadOnlyList<Resource> right,
        out string difference)
    {
        if (left.Count != right.Count)
        {
            difference = $"resource count changed from {left.Count} to {right.Count}";
            return false;
        }
        for (int index = 0; index < left.Count; index++)
        {
            Resource a = left[index];
            Resource b = right[index];
            if (a.Id == b.Id && a.ClassHash == b.ClassHash && a.Name == b.Name
                && a.Header.AsSpan().SequenceEqual(b.Header)
                && a.Data.AsSpan().SequenceEqual(b.Data))
                continue;
            difference = $"resource {index} ({a.Name}) changed after DataFile write/read";
            return false;
        }
        difference = "";
        return true;
    }

    static void AuditResource(ParserAuditReport report, string archive, string container,
        Resource resource)
    {
        byte[] bytes = resource.Data;
        switch (resource.ClassHash)
        {
            case Mesh.ClassHash:
                CheckExact(report, "Mesh", archive, container, resource.Name, bytes, () =>
                {
                    Mesh mesh = Mesh.Read(bytes);
                    if (mesh.IsOpaque)
                        Note(Stat(report, "Mesh", "byte-exact writer"), "opaque object-pointer variant");
                    if (mesh.Geometry != GeometryKind.None)
                    {
                        _ = MeshGeometry.ReadVertices(mesh);
                        _ = MeshGeometry.ReadTriangles(mesh);
                    }
                    return mesh.Write();
                });
                break;
            case Skeleton.ClassHash:
                CheckExact(report, "Skeleton", archive, container, resource.Name, bytes, () =>
                {
                    SkeletonAsset skeleton = Skeleton.ReadAsset(bytes);
                    if (skeleton.IsOpaque)
                        Note(Stat(report, "Skeleton", "byte-exact writer"), "opaque modifier variant");
                    return Skeleton.Write(skeleton);
                });
                break;
            case BuildTable.ClassHash:
                CheckExact(report, "BuildTable", archive, container, resource.Name, bytes,
                    () => BuildTable.Read(bytes).Write());
                break;
            case Animation.ClassHash:
                CheckExact(report, "Animation", archive, container, resource.Name, bytes, () =>
                {
                    AnimationAsset animation = Animation.Read(bytes);
                    ValidateAnimationValues(animation);
                    return Animation.Write(animation);
                });
                break;
            case Cloth.ClassHash:
                CheckExact(report, "Cloth", archive, container, resource.Name, bytes, () =>
                {
                    ClothAsset cloth = Cloth.Read(bytes);
                    _ = cloth.Faces.Count();
                    _ = cloth.Links.Count();
                    return Cloth.Write(cloth);
                });
                break;
            case SoftBodySettings.ClassHash:
                CheckExact(report, "SoftBodySettings", archive, container, resource.Name, bytes,
                    () => SoftBodySettings.Write(SoftBodySettings.Read(bytes)));
                break;
            case LayeredSky.ClassHash:
                CheckExact(report, "LayeredSky", archive, container, resource.Name, bytes,
                    () => LayeredSky.Write(LayeredSky.Read(bytes)));
                break;
            case TimeCycle.ClassHash:
            case TimeCycle.WeatherClassHash:
                CheckExact(report, ResourceTypes.NameOf(resource.ClassHash), archive, container,
                    resource.Name, bytes, () => TimeCycle.Read(bytes).Write());
                break;
            case LocalizationPackage.ClassHash:
                CheckRead(report, "LocalizationPackage", archive, container, resource.Name,
                    () => _ = LocalizationPackage.Read(bytes));
                break;
            case TextureMap.ClassHash:
                CheckRead(report, "TextureMap", archive, container, resource.Name,
                    () => _ = TextureMap.Read(bytes));
                break;
            case CompiledMip.ClassHash:
                CheckRead(report, "CompiledMip", archive, container, resource.Name,
                    () => _ = CompiledMip.Read(bytes));
                break;
            case Material.ClassHash:
                CheckRead(report, "Material", archive, container, resource.Name, () =>
                {
                    Material material = Material.Read(bytes);
                    ParserAuditStat stat = Stat(report, "Material", "reader");
                    if (material.Note.Length > 0)
                        Note(stat, material.Note);
                    if (material.TrailingBytes > 0)
                        Note(stat, "reader leaves trailing bytes");
                });
                break;
            case TextureSet.ClassHash:
                CheckRead(report, "TextureSet", archive, container, resource.Name,
                    () => _ = TextureSet.Read(bytes));
                break;
            case CamoPattern.ClassHash:
                CheckRead(report, "CamoPattern", archive, container, resource.Name,
                    () => _ = CamoPattern.Read(bytes));
                break;
        }
    }

    static void ValidateAnimationValues(AnimationAsset animation)
    {
        foreach (AnimationTrack track in animation.Tracks)
        {
            if (track.TimeFormat is 0 or 1)
            {
                IReadOnlyList<int> times = AnimationValues.ReadTimes(track);
                for (int index = 1; index < times.Count; index++)
                    if (times[index] < times[index - 1])
                        throw new InvalidDataException("Animation key times are not ordered.");
            }

            int stride = track.Stride;
            if (track.ValueFormat is 0 or 1 or 5)
            {
                for (int offset = 0; offset < track.Values.Length; offset += stride)
                {
                    var value = AnimationValues.ReadRotation(track.Values.AsSpan(offset, stride), track.ValueFormat);
                    if (!float.IsFinite(value.X + value.Y + value.Z + value.W))
                        throw new InvalidDataException("Animation contains a non-finite rotation.");
                }
            }
            else if (track.ValueFormat == 6)
            {
                for (int offset = 0; offset < track.Values.Length; offset += stride)
                {
                    var value = AnimationValues.ReadTranslation(track.Values.AsSpan(offset, stride), track.ValueFormat);
                    if (!float.IsFinite(value.X + value.Y + value.Z))
                        throw new InvalidDataException("Animation contains a non-finite translation.");
                }
            }
        }
    }

    static void CheckPrefetch(ParserAuditReport report, string archive, string container, byte[] bytes,
        IReadOnlySet<ulong> dataIds)
    {
        ParserAuditStat fileStat = Stat(report, "PrefetchingFileInfos", "structural writer");
        fileStat.Seen++;
        try
        {
            IReadOnlyList<(ulong Id, byte[] Block)> objects = PrefetchingFileInfos.ReadObjects(bytes);
            foreach ((ulong id, byte[] blockBytes) in objects.Where(item => dataIds.Contains(item.Id)))
                CheckExact(report, "PrefetchBlock", archive, container, $"0x{id:X16}", blockBytes,
                    () =>
                    {
                        PrefetchBlock block = PrefetchingFileInfos.ReadBlock(blockBytes);
                        if (block.IsOpaque)
                            Note(Stat(report, "PrefetchBlock", "byte-exact writer"), "opaque alternate layout");
                        return PrefetchingFileInfos.WriteBlock(block);
                    });

            byte[] rebuilt = PrefetchingFileInfos.Create(objects);
            IReadOnlyList<(ulong Id, byte[] Block)> again = PrefetchingFileInfos.ReadObjects(rebuilt);
            if (objects.Count != again.Count || objects.Where((item, index) =>
                    item.Id != again[index].Id || !item.Block.AsSpan().SequenceEqual(again[index].Block)).Any())
                throw new InvalidDataException("PrefetchingFileInfos changed after create/read.");
            fileStat.Passed++;
            if (rebuilt.AsSpan().SequenceEqual(bytes)) fileStat.ByteExact++;
            else fileStat.Different++;
        }
        catch (Exception error)
        {
            fileStat.Failed++;
            Fail(report, archive, container, "", "PrefetchingFileInfos", error, countParser: false);
        }
    }

    static void CheckExact(ParserAuditReport report, string parser, string archive,
        string container, string resource, byte[] original, Func<byte[]> cycle)
    {
        ParserAuditStat stat = Stat(report, parser, "byte-exact writer");
        stat.Seen++;
        try
        {
            byte[] rebuilt = cycle();
            stat.Passed++;
            if (rebuilt.AsSpan().SequenceEqual(original))
                stat.ByteExact++;
            else
            {
                stat.Different++;
                string at = FirstDifference(original, rebuilt).ToString("X");
                Fail(report, archive, container, resource, parser,
                    new InvalidDataException($"roundtrip differs at 0x{at} ({original.Length} -> {rebuilt.Length} bytes)"),
                    countParser: false);
            }
        }
        catch (Exception error)
        {
            stat.Failed++;
            Fail(report, archive, container, resource, parser, error, countParser: false);
        }
    }

    static void CheckRead(ParserAuditReport report, string parser, string archive,
        string container, string resource, Action read)
    {
        ParserAuditStat stat = Stat(report, parser, "reader");
        stat.Seen++;
        try
        {
            read();
            stat.Passed++;
        }
        catch (Exception error)
        {
            stat.Failed++;
            Fail(report, archive, container, resource, parser, error, countParser: false);
        }
    }

    static ParserAuditStat Stat(ParserAuditReport report, string parser, string mode)
    {
        if (!report.Parsers.TryGetValue(parser, out ParserAuditStat? stat))
            report.Parsers[parser] = stat = new ParserAuditStat { Mode = mode };
        return stat;
    }

    static void Note(ParserAuditStat stat, string note) =>
        stat.Notes[note] = stat.Notes.GetValueOrDefault(note) + 1;

    static void Fail(ParserAuditReport report, string archive, string container,
        string resource, string parser, Exception error, bool countParser = true)
    {
        if (countParser)
        {
            ParserAuditStat stat = Stat(report, parser, "reader");
            stat.Seen++;
            stat.Failed++;
        }
        report.TotalFailures++;
        string reason = parser + ": " + error.GetType().Name + ": " + error.Message;
        report.FailureReasons[reason] = report.FailureReasons.GetValueOrDefault(reason) + 1;
        if (report.Failures.Count(item => item.Parser == parser) < 50)
            report.Failures.Add(new ParserAuditFailure(Path.GetRelativePath(report.GameFolder, archive),
                container, resource, parser, error.GetType().Name + ": " + error.Message));
    }

    static void PrintParserAudit(ParserAuditReport report)
    {
        Console.WriteLine();
        Console.WriteLine($"{report.Archives:N0} archive(s), {report.ForgeEntries:N0} Forge entries, "
            + $"{report.DataContainers:N0} data containers, {report.Resources:N0} resources");
        Console.WriteLine("parser                         seen       passed       exact   different      failed  mode");
        foreach ((string name, ParserAuditStat stat) in report.Parsers.OrderBy(pair => pair.Key))
            Console.WriteLine($"{name,-28} {stat.Seen,10:N0} {stat.Passed,12:N0} {stat.ByteExact,11:N0} "
                + $"{stat.Different,11:N0} {stat.Failed,11:N0}  {stat.Mode}");
        Console.WriteLine($"failures: {report.TotalFailures:N0}");
        foreach (ParserAuditFailure failure in report.Failures.Take(20))
            Console.WriteLine($"  {failure.Parser}: {failure.Archive} / {failure.Container} / "
                + $"{failure.Resource}: {failure.Error}");
    }
}
