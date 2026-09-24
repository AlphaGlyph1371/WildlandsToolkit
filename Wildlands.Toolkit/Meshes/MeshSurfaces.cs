using System.Collections.Generic;
using System.Linq;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Models;
using Wildlands.Formats.Materials;
using Wildlands.Formats.Textures;

namespace Wildlands.Toolkit;

public sealed record MeshSurface(RenderMaterial? Look, bool TwoSided, bool Translucent, Material? Cloth);

public sealed class SurfaceSources
{
    public required List<Resource> Siblings { get; init; }
    public required ArchiveSet? Archives { get; init; }
    public IReadOnlyDictionary<ulong, byte[]>? Pending { get; init; }
    public Dictionary<ulong, TexturePixels?> Decoded { get; } = [];
    public SortedSet<string> Borrowed { get; } = new();
    public bool UsedPending { get; set; }
}

public static class MeshSurfaces
{
    static readonly string[] SpecularGlossTemplates = ["SHD_Weapon_InGame", "SHD_Weapon_Gunsmith"];

    public static List<MeshSurface> Load(Mesh mesh, SurfaceSources sources, bool withTextures, ClothStyle? style)
    {
        var ranges = new List<MeshSurface>();

        for (int range = 0; range < mesh.Materials.Count; range++)
        {
            ulong materialId = mesh.Materials[range].MaterialId;
            bool twoSided = false, translucent = false;
            Material? cloth = null;
            RenderMaterial? look = null;

            if (Find(sources, materialId, Material.ClassHash, mesh.Id) is { } resource)
            {
                var material = Material.Read(resource.Data);
                twoSided = material.Flags.TwoSided;
                translucent = !material.IsOpaque;

                string template = TemplateName(material.TemplateId, sources.Archives);
                if (ClothParameters.IsCloth(template) && range < mesh.Data.Standard.Count)
                    cloth = material;

                if (withTextures)
                {
                    look = cloth is not null
                        ? ClothLook(ClothParameters.From(material, template).With(style), sources, mesh.Id)
                        : PlainLook(material, template, translucent, sources, mesh.Id);
                }
            }

            ranges.Add(new MeshSurface(look, twoSided, translucent, cloth));
        }

        while (ranges.Count < mesh.Data.Standard.Count)
            ranges.Add(new MeshSurface(null, false, false, null));

        return ranges;
    }

    static RenderMaterial? ClothLook(ClothParameters parameters, SurfaceSources sources, ulong containerId)
    {
        var diffuse = Pixels(parameters.Texture("3:Layer0_Diffuse"), sources, containerId);
        if (diffuse is null)
            return null;

        return new RenderMaterial
        {
            Diffuse = diffuse,
            Normal = Pixels(parameters.Texture("3:Layer0_Normal"), sources, containerId),
            Mask = Pixels(parameters.Texture("3:Layer0_Mask1"), sources, containerId),
            Camo = Pixels(parameters.Texture("CamoPattern"), sources, containerId),
            DetailDiffuse = Pixels(parameters.Texture("6:DetailDiffuse_Cotton"), sources, containerId),
            DetailCotton = Pixels(parameters.Texture("7:Layer1DetailNormal_Cotton"), sources, containerId),
            DetailLeather = Pixels(parameters.Texture("7:Layer1DetailNormal_Leather"), sources, containerId),
            DetailMetal = Pixels(parameters.Texture("7:Layer1DetailNormal_Metal"), sources, containerId),
            Cloth = parameters,
        };
    }

    static RenderMaterial? PlainLook(Material material, string template, bool translucent, SurfaceSources sources, ulong containerId)
    {
        TextureSet? set = material.TextureSetId != 0 && Find(sources, material.TextureSetId, TextureSet.ClassHash, containerId) is { } found
            ? TextureSet.Read(found.Data)
            : null;

        var diffuse = Pixels(Slot(set, "Diffuse", material, sources, containerId), sources, containerId);
        if (diffuse is null)
            return null;

        bool specularGloss = SpecularGlossTemplates.Contains(template);
        var normal = set is null && !specularGloss ? null : Pixels(Slot(set, "Normal", material, sources, containerId), sources, containerId);

        return new RenderMaterial
        {
            Diffuse = diffuse,
            Normal = normal,
            Specular = specularGloss ? Pixels(Slot(set, "Specular", material, sources, containerId), sources, containerId) : null,
            GlossInNormal = specularGloss && normal is not null,
            AlphaTest = !specularGloss && !translucent && UsesAlphaAsCutout(diffuse),
        };
    }

    static ulong Slot(TextureSet? set, string slot, Material material, SurfaceSources sources, ulong containerId)
    {
        ulong id = set?.Find(slot) ?? 0;
        return id != 0 ? id : ParameterTextureOf(material, slot, sources, containerId);
    }

    // A quarter of all draw ranges carry no TextureSet at all, but the material names its
    // diffuse texture in its own parameter list. Measured over 2188 ranges of the WorldMap:
    // 1514 are found through the set, another 555 only this way.
    static ulong ParameterTextureOf(Material material, string kind, SurfaceSources sources, ulong containerId)
    {
        foreach (var parameter in material.Parameters)
        {
            if (parameter.TextureId == 0)
                continue;

            var texture = Find(sources, parameter.TextureId, TextureMap.ClassHash, containerId);
            if (texture is not null && texture.Name.Contains(kind, System.StringComparison.OrdinalIgnoreCase))
                return parameter.TextureId;
        }

        return 0;
    }

    static string TemplateName(ulong templateId, ArchiveSet? archives)
    {
        if (templateId == 0 || archives is null)
            return "";

        foreach (var archive in archives.All())
        {
            if (archive.FindById(templateId) is { } entry)
                return entry.Name;
        }

        return "";
    }

    static TexturePixels? Pixels(ulong textureId, SurfaceSources sources, ulong containerId)
    {
        if (textureId == 0)
            return null;

        if (sources.Decoded.TryGetValue(textureId, out var known))
            return known;

        var map = Find(sources, textureId, TextureMap.ClassHash, containerId);
        if (map is null && Find(sources, textureId, TextureMapSpec.ClassHash, containerId) is { } spec)
            map = Find(sources, TextureMapSpec.TextureOf(spec.Data), TextureMap.ClassHash, containerId);

        TexturePixels? pixels = null;
        if (map is not null)
        {
            var texture = TextureMap.Read(map.Data);
            var level = TextureLoader.FromTexture(map.Name, texture, sources.Archives, sources.Pending).Focus;

            if (level is not null && BlockDecoder.CanDecode(texture.Format))
            {
                int size = texture.Format.LevelSize(level.Width, level.Height);
                if (level.Pixels.Length >= size)
                {
                    var bgra = BlockDecoder.Decode(level.Pixels.AsSpan(0, size), texture.Format, level.Width, level.Height);
                    pixels = new TexturePixels(level.Width, level.Height, bgra, texture.GammaSettings == 1);
                }
            }

            if (level?.SourceName == "replacement")
                sources.UsedPending = true;
        }

        sources.Decoded[textureId] = pixels;
        return pixels;
    }

    static bool UsesAlphaAsCutout(TexturePixels pixels)
    {
        int middle = 0, clear = 0, total = 0;

        for (int i = 3; i < pixels.Bgra.Length; i += 4)
        {
            total++;
            if (pixels.Bgra[i] < 16) clear++;
            else if (pixels.Bgra[i] < 239) middle++;
        }

        return total > 0 && clear > 0 && (double)middle / total <= 0.15;
    }

    public static Resource? Find(SurfaceSources sources, ulong id, uint classHash, ulong containerId)
    {
        Resource? found = sources.Siblings.FirstOrDefault(r => r.Id == id && r.ClassHash == classHash);

        if (found is null && sources.Archives?.FindResource(id, classHash, containerId) is { } elsewhere)
        {
            sources.Borrowed.Add(elsewhere.File.ArchiveName);
            found = elsewhere.Resource;
        }

        if (sources.Pending is not null && sources.Pending.TryGetValue(id, out var pending)
            && pending.Length >= 12 && System.BitConverter.ToUInt32(pending, 8) == classHash)
        {
            sources.UsedPending = true;
            return new Resource { Id = id, ClassHash = classHash, Name = found?.Name ?? "", Data = pending };
        }

        return found;
    }
}
