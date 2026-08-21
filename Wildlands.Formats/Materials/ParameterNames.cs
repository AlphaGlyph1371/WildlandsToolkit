using System.Collections.Generic;

namespace Wildlands.Formats.Materials;

public static class ParameterNames
{
    static readonly string[] Known =
    [
        "Albedo",
        "Alpha",
        "BaseNormalMap",
        "BurnAmount",
        "BurnRadius",
        "Camo",
        "CamoPattern",
        "CamoTransform",
        "ClipAlpha",
        "Color",
        "ColorBlend",
        "Diffuse",
        "DiffuseColor",
        "DiffuseMultiplier",
        "Distortion",
        "DustColor",
        "Emissive",
        "FogLight",
        "FrontBack",
        "Gloss",
        "HairPaint",
        "Mask2",
        "MaskTexture",
        "MudColor",
        "Multiplier",
        "Normal",
        "Offset",
        "PaintGloss",
        "ParallaxBias",
        "ParallaxScale",
        "ReflectionColor",
        "ReflectionIntensity",
        "RotationPivot",
        "Roughness",
        "RustDiffuse",
        "SkinDirtMask",
        "SkinDirtNormal",
        "Specular",
        "Texture",
        "UVTransform",
        "WindAmplitude",
        "WindFrequency",
    ];

    static readonly Dictionary<uint, string> ByHash = Build();

    static Dictionary<uint, string> Build()
    {
        var map = new Dictionary<uint, string>(Known.Length);
        foreach (var name in Known)
            map[ResourceTypes.Crc32(name)] = name;
        return map;
    }

    public static string NameOf(uint hash) => ByHash.TryGetValue(hash, out var name) ? name : $"0x{hash:X8}";
}
