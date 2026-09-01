using System.IO;
using System.Text.Json;
using Wildlands.Formats;

internal static class PropertyGuesser
{
    // Terms visible in Anvil tooling, already known property names, and conservative
    // rendering vocabulary. An optional text file can extend this without promoting
    // any generated combination to a known name.
    static readonly string[] BuiltInTerms =
    [
        "Absorption", "Adaptation", "Albedo", "Ambient", "AO", "Atmosphere",
        "Atmospheric", "Auto", "Background", "Bias", "Black", "Blend", "Bloom",
        "Blue", "Bottom", "Brightness", "Camera", "Cirrus", "Cloud", "Color",
        "Contrast", "Cover", "Curve", "Day", "Density", "Depth", "Diffuse", "Down",
        "Distance", "Dither", "Emissive", "End", "Exposure", "Extinction", "Eye", "Fade",
        "Falloff", "Far", "Film", "Flare", "Fog", "Frequency", "Gamma", "Global",
        "Grain", "Green", "Gray", "Haze", "Hazy", "Height", "Highlight", "Histogram", "Horizontal",
        "Intensity", "Layer", "Light", "Lighting", "Luminance", "LUT", "Max",
        "Medium", "Mie", "Min", "Moon", "Multi", "Near", "Night", "Noise",
        "Normal", "Occlusion", "Offset", "Phase", "Post", "Range", "Rate", "Rayleigh",
        "Red", "Reflection", "Saturation", "Scale", "Scattering", "Shadow",
        "Sharpness", "Sky", "Skylight", "Space", "Specular", "Sprite", "Start",
        "Shoulder", "Speed", "Strength", "Sun", "Target", "Threshold", "Tint", "Toe", "Tonemap", "Top", "Turbidity",
        "Up", "Value", "Vector", "Vibrance", "Vignette", "White", "Wind", "World",

        // Compounds keep three-part generation useful for names that contain four or
        // more conceptual words.
        "AdaptationSpeed", "AutoExposure", "AverageLuminance", "CloudShadow", "ColorGrading", "DayNight", "DenseFog",
        "DepthOfField", "FogLighting", "GlobalMultiplier", "LightIntensity",
        "EyeAdaptation", "ExposureBias", "ExposureMax", "ExposureMin", "GrayPoint",
        "MaxExposure", "MaximumExposure", "MieScattering", "MiddleGray", "MinExposure",
        "MinimumExposure", "MultiScattering", "PostEffect", "RayleighScattering",
        "SkyIntensity", "SunLight", "ToneMapping", "VolClouds", "VolumetricFog",
        "WhiteBalance", "WhitePoint",
    ];

    public static int Run(string auditPath, string? wordPath, int maximumParts)
    {
        if (maximumParts is < 1 or > 4)
        {
            Console.WriteLine("parts must be between 1 and 4");
            return 1;
        }

        var targets = ReadUnknownProperties(auditPath);
        var terms = BuiltInTerms.ToHashSet(StringComparer.Ordinal);

        if (wordPath is not null)
        {
            foreach (string line in File.ReadLines(wordPath))
            {
                string term = line.Trim();
                if (term.Length > 0 && term[0] != '#')
                    terms.Add(term);
            }
        }

        var orderedTerms = terms.Order(StringComparer.Ordinal).ToArray();
        var matches = new Dictionary<uint, SortedSet<string>>();
        long examined = 0;

        Generate("", 0);

        Console.WriteLine($"examined {examined:N0} candidate name(s) from {orderedTerms.Length} term(s), "
            + $"matched {matches.Count} of {targets.Count} unknown property hash(es)");

        foreach (var (hash, names) in matches.OrderBy(pair => pair.Key))
        {
            Console.WriteLine();
            Console.WriteLine($"0x{hash:X8}  {string.Join(" | ", names)}");
            foreach (string path in targets[hash].Take(4))
                Console.WriteLine("  " + path);
        }

        return 0;

        void Generate(string prefix, int depth)
        {
            if (depth >= maximumParts)
                return;

            foreach (string term in orderedTerms)
            {
                string candidate = prefix + term;
                examined++;

                uint hash = ResourceTypes.Crc32(candidate);
                if (targets.ContainsKey(hash))
                {
                    if (!matches.TryGetValue(hash, out var names))
                        matches[hash] = names = [];
                    names.Add(candidate);
                }

                Generate(candidate, depth + 1);
            }
        }
    }

    static Dictionary<uint, SortedSet<string>> ReadUnknownProperties(string auditPath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(auditPath));
        var targets = new Dictionary<uint, SortedSet<string>>();

        foreach (var controller in document.RootElement.GetProperty("Controllers").EnumerateArray())
        {
            foreach (var curve in controller.GetProperty("Curves").EnumerateArray())
            {
                foreach (var step in curve.GetProperty("Steps").EnumerateArray())
                {
                    if (step.GetProperty("Source").GetString() != "Unknown")
                        continue;

                    string text = step.GetProperty("Hash").GetString()!;
                    uint hash = Convert.ToUInt32(text[2..], 16);
                    if (!targets.TryGetValue(hash, out var paths))
                        targets[hash] = paths = [];
                    paths.Add(curve.GetProperty("Path").GetString()!);
                }
            }
        }

        return targets;
    }
}
