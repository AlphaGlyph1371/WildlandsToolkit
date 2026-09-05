using System.Collections.Generic;
using System.Text;

namespace Wildlands.Formats;

// Note: Class hashes are the CRC32 of the type name, so the lookup builds itself from the names we know
// Unknown hashes are shown as hex

public static class ResourceTypes
{
    static readonly string[] KnownNames =
    [
        "AccomplishmentManager",
        "Animation",
        "AtomReplaceSet",
        "BuildTable",
        "Cloth",
        "Color",
        "CompiledMip",
        "CompiledTextureMap",
        "DLCUniverseComponent",
        "EngineOptions",
        "Entity",
        "EntityBuilder",
        "FloorHeightData",
        "FX",
        "GRNSoundSettings",
        "LayeredSky",
        "LensFlareSettings",
        "LightingDescriptor",
        "LocalizationPackage",
        "LODSelector",
        "Mask",
        "Material",
        "Mesh",
        "MetaFile",
        "MinimapTextures",
        "PostEffectSettings",
        "PrefetchInfo",
        "PrefetchingFileInfos",
        "PropertyControllerEntry",
        "PropertyPath",
        "PropertyPathNode",
        "RawFile",
        "RegionLayout",
        "RoadPathContainer",
        "RoadPathNetwork",
        "Season",
        "Skeleton",
        "SmallLifeZoneSpawnDescriptor",
        "SoundActionOnStateConfig",
        "SoundDynEchoGlobalSettings",
        "SoundInitSettings",
        "SoundMicroBehaviorTransitionConfig",
        "SoundPointsConfig",
        "SoundRadioCommercialTrack",
        "SoundRadioSettings",
        "SoundRadioTrackSequence",
        "SoundRadioVoiceoverTrack",
        "SoundSettings",
        "SoundWindContextSoundSet",
        "SplashFX",
        "TagRules",
        "TextureMap",
        "TextureMapSpec",
        "TextureSelector",
        "TextureSet",
        "TimeOfDayPropertyControllerData",
        "UILightingSetup",
        "Universe",
        "UVTransform",
        "WeatherPropertyControllerData",
        "WindPropertyControllerData",
        "World",
        "WorldAmbiance",
        "WorldParticlesDescriptor",
        "WorldTransitionComponent",
        "WorldTransitionPortal",
    ];

    static readonly Dictionary<uint, string> ByHash = Build();

    static Dictionary<uint, string> Build()
    {
        var map = new Dictionary<uint, string>(KnownNames.Length);
        foreach (var name in KnownNames)
            map[Crc32(name)] = name;
        return map;
    }

    public static string NameOf(uint hash)
    {
        return TryName(hash, out var name) ? name! : $"0x{hash:X8}";
    }

    public static bool TryName(uint hash, out string? name) => ByHash.TryGetValue(hash, out name);

    public static bool IsKnown(uint hash) => ByHash.ContainsKey(hash);

    public static uint Crc32(string text)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in Encoding.UTF8.GetBytes(text))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc >> 1) ^ (0xEDB88320 & (uint)-(crc & 1));
        }
        return ~crc;
    }
}
