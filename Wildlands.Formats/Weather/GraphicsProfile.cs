using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Wildlands.Formats.Weather;

public enum GraphicsProfileOperation
{
    Multiply,
    Add,
    Set,
}

public sealed class GraphicsProfile
{
    public int FormatVersion { get; set; } = 1;
    public string Name { get; set; } = "Unnamed graphics profile";
    public string Description { get; set; } = "";
    public bool Experimental { get; set; } = true;
    public List<string> ResourceNames { get; set; } = [];
    public List<GraphicsProfileRule> Rules { get; set; } = [];

    public static GraphicsProfile Load(string path)
    {
        var options = new JsonSerializerOptions
        {
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            PropertyNameCaseInsensitive = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());

        var profile = JsonSerializer.Deserialize<GraphicsProfile>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("The graphics profile is empty.");

        if (profile.FormatVersion != 1)
            throw new InvalidDataException($"Unsupported graphics profile version {profile.FormatVersion}.");
        if (profile.Rules.Count == 0)
            throw new InvalidDataException("The graphics profile has no rules.");

        return profile;
    }

    public GraphicsProfileResult Apply(TimeCycle cycle, string? resourceName = null)
    {
        if (ResourceNames.Count > 0 && (resourceName is null
            || !ResourceNames.Contains(resourceName, StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException($"{Name} is intended for {string.Join(", ", ResourceNames)}, "
                + $"not {resourceName ?? "an unnamed controller"}.");

        var result = new GraphicsProfileResult(Name, Experimental);

        foreach (var rule in Rules.Where(rule => rule.Enabled))
        {
            string wantedPath = NormalizePath(rule.Path);
            var matches = cycle.Entries
                .Where(entry => HashPath(entry).Equals(wantedPath, StringComparison.Ordinal))
                .ToList();

            if (rule.Required && matches.Count == 0)
                throw new InvalidDataException($"{rule.Label}: required path {wantedPath} is absent.");
            if (matches.Count > rule.MaximumMatches)
                throw new InvalidDataException($"{rule.Label}: path {wantedPath} matched {matches.Count} curves; "
                    + $"the profile allows at most {rule.MaximumMatches}.");

            foreach (var entry in matches)
            {
                if (rule.Component is int component && (component < 0 || component >= entry.Components))
                    throw new InvalidDataException($"{rule.Label}: component {component} does not exist on "
                        + $"a {entry.Kind} curve.");
            }

            if (!float.IsFinite(rule.Value)
                || rule.ClampMinimum is float minimum && !float.IsFinite(minimum)
                || rule.ClampMaximum is float maximum && !float.IsFinite(maximum)
                || rule.ClampMinimum > rule.ClampMaximum)
                throw new InvalidDataException($"{rule.Label}: invalid value or clamp range.");

            HashSet<ushort>? selectedTimes = null;
            if (rule.KeyTimes.Count > 0)
            {
                selectedTimes = [];
                foreach (string keyTime in rule.KeyTimes)
                {
                    if (!TimeCycleText.TryAxis(keyTime, cycle.IsWeather, out ushort position))
                        throw new InvalidDataException($"{rule.Label}: {keyTime} is not a valid "
                            + (cycle.IsWeather ? "weather position." : "time of day."));
                    selectedTimes.Add(position);
                }
            }

            foreach (var entry in matches)
            {
                if (selectedTimes is not null)
                {
                    var present = entry.Times.Where(selectedTimes.Contains).ToHashSet();
                    if (present.Count != selectedTimes.Count)
                    {
                        var absent = selectedTimes.Where(position => !present.Contains(position))
                            .Select(position => TimeCycleText.Axis(position, cycle.IsWeather));
                        throw new InvalidDataException($"{rule.Label}: curve {wantedPath} has no exact key at "
                            + string.Join(", ", absent) + ".");
                    }
                }

                result.Changes.Add(ApplyRule(entry, wantedPath, rule, selectedTimes));
            }
        }

        if (result.Changes.Count == 0)
            throw new InvalidDataException("None of the profile rules apply to this graphics controller.");

        return result;
    }

    static GraphicsProfileChange ApplyRule(TimeCycleEntry entry, string path, GraphicsProfileRule rule,
        HashSet<ushort>? selectedTimes)
    {
        float beforeMinimum = float.PositiveInfinity;
        float beforeMaximum = float.NegativeInfinity;
        float afterMinimum = float.PositiveInfinity;
        float afterMaximum = float.NegativeInfinity;
        int changed = 0;

        for (int keyIndex = 0; keyIndex < entry.Values.Count; keyIndex++)
        {
            if (selectedTimes is not null && !selectedTimes.Contains(entry.Times[keyIndex]))
                continue;

            float[] key = entry.Values[keyIndex];
            int first = rule.Component ?? 0;
            int last = rule.Component ?? (key.Length - 1);

            for (int component = first; component <= last; component++)
            {
                float before = key[component];
                float after = rule.Operation switch
                {
                    GraphicsProfileOperation.Multiply => before * rule.Value,
                    GraphicsProfileOperation.Add => before + rule.Value,
                    GraphicsProfileOperation.Set => rule.Value,
                    _ => throw new InvalidDataException($"Unsupported operation {rule.Operation}."),
                };

                if (rule.ClampMinimum is float minimum)
                    after = Math.Max(after, minimum);
                if (rule.ClampMaximum is float maximum)
                    after = Math.Min(after, maximum);
                if (!float.IsFinite(after))
                    throw new InvalidDataException($"{rule.Label}: the operation produced a non-finite value.");

                beforeMinimum = Math.Min(beforeMinimum, before);
                beforeMaximum = Math.Max(beforeMaximum, before);
                afterMinimum = Math.Min(afterMinimum, after);
                afterMaximum = Math.Max(afterMaximum, after);

                if (BitConverter.SingleToInt32Bits(before) != BitConverter.SingleToInt32Bits(after))
                {
                    key[component] = after;
                    changed++;
                }
            }
        }

        return new GraphicsProfileChange(rule.Label, path, entry.Kind, changed,
            beforeMinimum, beforeMaximum, afterMinimum, afterMaximum);
    }

    public static string HashPath(TimeCycleEntry entry) => string.Join('.', entry.Path.Select(step =>
        $"0x{step.NameHash:X8}" + (step.IsArrayElement ? $"[{step.ArrayIndex}]" : "")));

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException("A graphics profile rule has no path.");

        var normalized = new List<string>();
        foreach (string part in path.Split('.', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            Match match = Regex.Match(part, @"^0x([0-9a-fA-F]{8})(?:\[([0-9]+)\])?$");
            if (!match.Success)
                throw new InvalidDataException($"Invalid graphics property path component: {part}");

            uint hash = uint.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            string index = "";
            if (match.Groups[2].Success)
            {
                ushort parsed = ushort.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                index = $"[{parsed}]";
            }
            normalized.Add($"0x{hash:X8}{index}");
        }

        return string.Join('.', normalized);
    }
}

public sealed class GraphicsProfileRule
{
    public string Label { get; set; } = "Unnamed rule";
    public string Path { get; set; } = "";
    public GraphicsProfileOperation Operation { get; set; }
    public float Value { get; set; }
    public int? Component { get; set; }
    public float? ClampMinimum { get; set; }
    public float? ClampMaximum { get; set; }
    public bool Required { get; set; }
    public int MaximumMatches { get; set; } = 1;
    public bool Enabled { get; set; } = true;
    public List<string> KeyTimes { get; set; } = [];
}

public sealed class GraphicsProfileResult(string name, bool experimental)
{
    public string Name { get; } = name;
    public bool Experimental { get; } = experimental;
    public List<GraphicsProfileChange> Changes { get; } = [];
    public int ChangedValues => Changes.Sum(change => change.ChangedValues);
}

public sealed record GraphicsProfileChange(string Label, string Path, CurveKind Kind, int ChangedValues,
    float BeforeMinimum, float BeforeMaximum, float AfterMinimum, float AfterMaximum);
