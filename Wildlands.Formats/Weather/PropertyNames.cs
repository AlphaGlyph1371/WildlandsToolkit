using System.Collections.Generic;

namespace Wildlands.Formats.Weather;

public static class PropertyNames
{
    static readonly Dictionary<uint, string> ByHash = new()
    {
        // one and two words
        { 0x038BCCD9, "VignetteStrength" },
        { 0x03A211ED, "RayleighScale" },
        { 0x03B82433, "Lightning" },
        { 0x06A53E0A, "ThresholdEnd" },
        { 0x09A9D2D8, "LayerThickness" },
        { 0x0F155366, "Density" },
        { 0x0F4E8BAB, "GlobalWind" },
        { 0x162604DE, "Turbidity" },
        { 0x17602593, "Rain" },
        { 0x186DC519, "NoiseThreshold" },
        { 0x21F28A62, "HorizontalRange" },
        { 0x258CB89A, "VolumetricFog" },
        { 0x2694592A, "Top" },
        { 0x2AAB060F, "Albedo" },
        { 0x30810F65, "WorldAO" },
        { 0x3DFF9203, "MieScattering" },
        { 0x419B0342, "NoiseScale" },
        { 0x49318F0D, "MaxHeight" },
        { 0x4CC9A9C1, "Cover" },
        { 0x5523A145, "RayleighDensity" },
        { 0x643B7402, "MediumDesc" },
        { 0x57F28B54, "Size" },
        { 0x67C069F2, "NoiseTurbidity" },
        { 0x6B433A31, "Intensity" },
        { 0x727CF15E, "MieDensity" },
        { 0x8139FBC0, "Wind" },
        { 0x89BDD306, "AbsorptionFactor" },
        { 0x8EF37792, "Bottom" },
        { 0x9CDD69B6, "MieHeight" },
        { 0x9EBA0571, "AOIntensity" },
        { 0x9EDA3D4C, "MinHeight" },
        { 0xA48E3AB0, "Frequency" },
        { 0xA5A3ECDB, "MoonLightIntensity" },
        { 0xAEBD252C, "EmissiveCurve" },
        { 0xBAE5ABF3, "Fog" },
        { 0xC0A9C490, "Vector4" },
        { 0xC1CCD8C7, "RayleighHeight" },
        { 0xCE49C69F, "Stretch" },
        { 0xD0DE1EA9, "LayerAltitude" },
        { 0xD4ABF1D9, "DenseFog" },
        { 0xD568579C, "NoiseFrequency" },
        { 0xD942A33D, "BlendFactor" },
        { 0xE5AA00B2, "ColorVibranceIntensity" },
        { 0xDA707488, "HeightFade" },
        { 0xE48A738F, "PhaseFunction" },
        { 0xE6F95818, "ThresholdStart" },
        { 0xF4A03832, "DayTime" },
        { 0xFE751847, "MoonlightColor" },

        // three words, kept where the value backs the name up
        { 0x02DA1D54, "VolCloudsParams" },
        { 0x16E80C76, "SunFlareSize" },
        { 0x1705F833, "AOMinValue" },
        { 0x1E35604C, "MieColorMultiplier" },
        { 0x1F1FEA3D, "WorldSplashConfig" },
        { 0x2864AEC7, "StaticWetnessIntensity" },
        { 0x3673A50A, "CirrusLayerThickness" },
        { 0x39899589, "SunElevationAngle" },
        { 0x3DD6A243, "MoonTextureAttenuation" },
        { 0x41415F05, "DenseFogDensity" },
        { 0x52C2E4E6, "AutoExposureBias" },
        { 0x60255396, "MoonElevationAngle" },
        { 0x7BAC4FEB, "CharacterSplashConfig" },
        { 0x8D292753, "HorizonFadeEnd" },
        { 0x9208EDB0, "RayleighColorMultiplier" },
        { 0xA29225E7, "CloudShadowDensity" },
        { 0xAFB2BC2A, "SkyTonemapBoost" },
        { 0xB8EE5932, "MieHazyFactor" },
        { 0xB9A908C5, "SkylightAnisotropyDepth" },
        { 0xBFAF110F, "CirrusLayerAltitude" },
        { 0xCBEF7544, "SunTonemapBoost" },
        { 0xDBA257F3, "MoonAzimuthAngle" },
        { 0xE86FFDDD, "DenseFogTop" },
        { 0xE9EB9D49, "HorizonFadeStart" },
        { 0xFA294520, "SunAzimuthAngle" },
    };

    // not certain: four words over a small vocabulary, shown with a question mark
    static readonly Dictionary<uint, string> Guessed = new()
    {
        { 0x612ACAE7, "DayNightBlendFactor" },
        { 0x7F7810CA, "FogLightingGlobalMultiplier" },
        { 0x9BFF34EB, "SpaceFlareAdaptationFactor" },
    };

    public static string Name(uint hash)
    {
        if (TryName(hash, out string? name))
            return name!;
        return ResourceTypes.NameOf(hash);
    }

    // The command-line census needs to distinguish a proven property name from a
    // convenient, but explicitly uncertain, display guess. Keep that distinction
    // here so discovery tools never promote a guess into a confirmed result.
    public static bool TryName(uint hash, out string? name)
    {
        if (ByHash.TryGetValue(hash, out name))
            return true;

        if (Guessed.TryGetValue(hash, out string? guess))
        {
            name = guess + "?";
            return true;
        }

        if (ResourceTypes.TryName(hash, out name))
            return true;

        return false;
    }

    public static bool IsConfirmed(uint hash) => ByHash.ContainsKey(hash) || ResourceTypes.IsKnown(hash);
}
