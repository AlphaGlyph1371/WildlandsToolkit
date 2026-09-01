using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Models;
using Wildlands.Formats.Materials;
using Wildlands.Formats.Textures;

namespace Wildlands.Toolkit;

public sealed record MeshSurface(Brush? Texture, bool TwoSided, bool Translucent);

public static class MeshSurfaces
{
    public static List<MeshSurface> Load(Mesh mesh, List<Resource> siblings, ArchiveSet? archives, bool withTextures)
        => Load(mesh, siblings, archives, withTextures, out _);

    public static List<MeshSurface> Load(Mesh mesh, List<Resource> siblings, ArchiveSet? archives, bool withTextures,
        out string? borrowed)
    {
        var ranges = new List<MeshSurface>();
        var from = new SortedSet<string>();
        borrowed = null;

        var brushes = new Dictionary<(ulong, bool), Brush?>();

        foreach (ulong materialId in mesh.Materials.Select(m => m.MaterialId))
        {
            ulong setId = 0;
            bool twoSided = false, translucent = false;

            ulong directId = 0;

            if (Find(siblings, archives, materialId, Material.ClassHash, mesh.Id, from) is { } resource)
            {
                var material = Material.Read(resource.Data);
                setId = material.TextureSetId;
                twoSided = material.Flags.TwoSided;
                translucent = !material.IsOpaque;

                if (setId == 0)
                    directId = DiffuseParameterOf(material, siblings, archives, mesh.Id, from);
            }

            var brush = withTextures ? BrushFor(setId, directId, translucent, siblings, archives, brushes, mesh.Id, from) : null;
            ranges.Add(new MeshSurface(brush, twoSided, translucent));
        }

        while (ranges.Count < mesh.Data.Standard.Count)
            ranges.Add(new MeshSurface(null, false, false));

        if (from.Count > 0)
            borrowed = string.Join(", ", from);

        return ranges;
    }

    static Brush? BrushFor(ulong setId, ulong directId, bool translucent, List<Resource> siblings, ArchiveSet? archives, Dictionary<(ulong, bool), Brush?> cache, ulong containerId, ISet<string> from)
    {
        ulong key = setId != 0 ? setId : directId;
        if (key == 0)
            return null;

        if (cache.TryGetValue((key, translucent), out var known))
            return known;

        Brush? brush = null;
        ulong textureId = setId != 0 ? DiffuseOf(setId, siblings, archives, containerId, from) : directId;

        if (Decode(textureId, siblings, archives, containerId, from) is { } image)
            brush = Paint(image, translucent);

        cache[(key, translucent)] = brush;
        return brush;
    }

    // A quarter of all draw ranges carry no TextureSet at all, but the material names its
    // diffuse texture in its own parameter list. Measured over 2188 ranges of the WorldMap:
    // 1514 are found through the set, another 555 only this way.
    static ulong DiffuseParameterOf(Material material, List<Resource> siblings, ArchiveSet? archives, ulong containerId, ISet<string> from)
    {
        foreach (var parameter in material.Parameters)
        {
            if (parameter.TextureId == 0)
                continue;

            var texture = Find(siblings, archives, parameter.TextureId, TextureMap.ClassHash, containerId, from);
            if (texture is not null && texture.Name.Contains("Diffuse", System.StringComparison.OrdinalIgnoreCase))
                return parameter.TextureId;
        }

        return 0;
    }

    static ulong DiffuseOf(ulong setId, List<Resource> siblings, ArchiveSet? archives, ulong containerId, ISet<string> from)
    {
        if (setId == 0)
            return 0;

        return Find(siblings, archives, setId, TextureSet.ClassHash, containerId, from) is { } resource ? TextureSet.Read(resource.Data).Find("Diffuse") : 0;
    }

    static BitmapSource? Decode(ulong textureId, List<Resource> siblings, ArchiveSet? archives, ulong containerId, ISet<string> from)
    {
        if (textureId == 0 || Find(siblings, archives, textureId, TextureMap.ClassHash, containerId, from) is not { } map)
            return null;

        var texture = TextureMap.Read(map.Data);
        var view = TextureLoader.FromTexture(map.Name, texture, archives);

        return view.Focus is null ? null : TextureLoader.Decode(texture, view.Focus, out _);
    }

    static Brush Paint(BitmapSource image, bool translucent)
    {
        var painted = translucent || TextureLoader.UsesAlphaAsCutout(image) ? image : TextureLoader.WithoutAlpha(image);

        var brush = new ImageBrush(painted)
        {
            TileMode = TileMode.Tile,
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new System.Windows.Rect(0, 0, 1, 1),
        };

        brush.Freeze();
        return brush;
    }

    static Resource? Find(List<Resource> siblings, ArchiveSet? archives, ulong id, uint classHash,
        ulong containerId, ISet<string> from)
    {
        var own = siblings.FirstOrDefault(r => r.Id == id && r.ClassHash == classHash);
        if (own is not null)
            return own;

        var found = archives?.FindResource(id, classHash, containerId);
        if (found is null)
            return null;

        from.Add(found.File.ArchiveName);
        return found.Resource;
    }
}
