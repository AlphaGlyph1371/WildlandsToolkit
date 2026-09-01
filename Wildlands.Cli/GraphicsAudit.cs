using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Weather;

internal static class GraphicsAudit
{
    public static int Write(string forgePath, string outputPath)
    {
        using var archive = ForgeArchive.Open(forgePath);
        var report = new GraphicsAuditReport
        {
            Archive = Path.GetFullPath(forgePath),
            ArchiveVersion = archive.Version,
        };

        foreach (var entry in archive.Entries)
        {
            if (entry.FileExtension != ".data")
                continue;

            DataFile file;
            try
            {
                using var stream = new MemoryStream(archive.ReadEntry(entry));
                file = DataFile.Read(stream);
            }
            catch (Exception ex)
            {
                report.UnreadableDataFiles.Add(new AuditProblem(entry.Index,
                    entry.Name + entry.FileExtension, ex.Message));
                continue;
            }

            for (int resourceIndex = 0; resourceIndex < file.Resources.Count; resourceIndex++)
            {
                var resource = file.Resources[resourceIndex];
                if (!TimeCycle.IsTimeCycle(resource.ClassHash))
                    continue;

                TimeCycle cycle;
                try
                {
                    cycle = TimeCycle.Read(resource.Data);
                }
                catch (Exception ex)
                {
                    report.UnreadableControllers.Add(new AuditProblem(entry.Index,
                        resource.Name, ex.Message));
                    continue;
                }

                var controller = new GraphicsControllerAudit
                {
                    EntryIndex = entry.Index,
                    EntryName = entry.Name + entry.FileExtension,
                    ResourceIndex = resourceIndex,
                    ResourceId = $"0x{resource.Id:X}",
                    ResourceName = resource.Name,
                    ResourceType = ResourceTypes.NameOf(resource.ClassHash),
                    Axis = cycle.IsWeather ? "weather-percent" : "time-of-day",
                };

                for (int curveIndex = 0; curveIndex < cycle.Entries.Count; curveIndex++)
                    controller.Curves.Add(DescribeCurve(cycle, cycle.Entries[curveIndex], curveIndex));

                report.Controllers.Add(controller);
            }
        }

        report.Controllers.Sort((a, b) => string.Compare(a.ResourceName, b.ResourceName,
            StringComparison.OrdinalIgnoreCase));

        string? folder = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (folder is not null)
            Directory.CreateDirectory(folder);

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        File.WriteAllText(outputPath, JsonSerializer.Serialize(report, options));

        var allCurves = report.Controllers.SelectMany(controller => controller.Curves).ToList();
        int unknownLeaves = allCurves.Count(curve => curve.Leaf.Source == PropertyNameSource.Unknown);
        int unknownLeafHashes = allCurves
            .Where(curve => curve.Leaf.Source == PropertyNameSource.Unknown)
            .Select(curve => curve.Leaf.Hash)
            .Distinct()
            .Count();

        Console.WriteLine($"{report.Controllers.Count} controller(s), {allCurves.Count} curve(s), "
            + $"{unknownLeaves} curve(s) across {unknownLeafHashes} unknown leaf hash(es) -> {outputPath}");
        if (report.UnreadableDataFiles.Count > 0 || report.UnreadableControllers.Count > 0)
            Console.WriteLine($"{report.UnreadableDataFiles.Count} unreadable data file(s), "
                + $"{report.UnreadableControllers.Count} unreadable controller(s)");

        return report.UnreadableControllers.Count == 0 ? 0 : 1;
    }

    static GraphicsCurveAudit DescribeCurve(TimeCycle cycle, TimeCycleEntry curve, int index)
    {
        var steps = curve.Path.Select(step =>
        {
            var match = PropertyNames.Identify(step.NameHash);
            return new GraphicsPathStepAudit
            {
                Hash = $"0x{step.NameHash:X8}",
                Name = match.Name,
                Source = match.Source,
                ArrayIndex = step.IsArrayElement ? step.ArrayIndex : null,
            };
        }).ToList();

        int components = curve.Components;
        var minimum = Enumerable.Repeat(float.PositiveInfinity, components).ToArray();
        var maximum = Enumerable.Repeat(float.NegativeInfinity, components).ToArray();

        for (int key = 0; key < curve.Values.Count; key++)
        {
            for (int component = 0; component < components; component++)
            {
                minimum[component] = Math.Min(minimum[component], curve.Values[key][component]);
                maximum[component] = Math.Max(maximum[component], curve.Values[key][component]);
            }
        }

        return new GraphicsCurveAudit
        {
            Index = index,
            Path = curve.PathText(),
            Kind = curve.Kind,
            Steps = steps,
            Leaf = steps[^1],
            Components = components,
            KeyCount = curve.Values.Count,
            IsConstant = curve.Values.All(value => value.AsSpan().SequenceEqual(curve.Values[0])),
            Minimum = minimum,
            Maximum = maximum,
            Keys = curve.Values.Select((value, key) => new GraphicsKeyAudit
            {
                Position = curve.Times[key],
                Axis = TimeCycleText.Axis(curve.Times[key], cycle.IsWeather),
                Values = value,
            }).ToList(),
        };
    }
}

internal sealed class GraphicsAuditReport
{
    public string Archive { get; init; } = "";
    public uint ArchiveVersion { get; init; }
    public List<GraphicsControllerAudit> Controllers { get; } = [];
    public List<AuditProblem> UnreadableDataFiles { get; } = [];
    public List<AuditProblem> UnreadableControllers { get; } = [];
}

internal sealed record AuditProblem(int EntryIndex, string Name, string Error);

internal sealed class GraphicsControllerAudit
{
    public int EntryIndex { get; init; }
    public string EntryName { get; init; } = "";
    public int ResourceIndex { get; init; }
    public string ResourceId { get; init; } = "";
    public string ResourceName { get; init; } = "";
    public string ResourceType { get; init; } = "";
    public string Axis { get; init; } = "";
    public List<GraphicsCurveAudit> Curves { get; } = [];
}

internal sealed class GraphicsCurveAudit
{
    public int Index { get; init; }
    public string Path { get; init; } = "";
    public CurveKind Kind { get; init; }
    public List<GraphicsPathStepAudit> Steps { get; init; } = [];
    public GraphicsPathStepAudit Leaf { get; init; } = new();
    public int Components { get; init; }
    public int KeyCount { get; init; }
    public bool IsConstant { get; init; }
    public float[] Minimum { get; init; } = [];
    public float[] Maximum { get; init; } = [];
    public List<GraphicsKeyAudit> Keys { get; init; } = [];
}

internal sealed class GraphicsPathStepAudit
{
    public string Hash { get; init; } = "";
    public string Name { get; init; } = "";
    public PropertyNameSource Source { get; init; }
    public ushort? ArrayIndex { get; init; }
}

internal sealed class GraphicsKeyAudit
{
    public ushort Position { get; init; }
    public string Axis { get; init; } = "";
    public float[] Values { get; init; } = [];
}
