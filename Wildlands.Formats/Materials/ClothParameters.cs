using System.Collections.Generic;
using System.Numerics;

namespace Wildlands.Formats.Materials;

public sealed class ClothParameters
{
    public const string DetailTemplate = "CHR_cloth_Detail";
    public const string SimplifiedTemplate = "CHR_cloth_simplified";

    readonly Dictionary<uint, Vector4> _values = [];
    readonly Dictionary<uint, UvTransform> _transforms = [];
    readonly Dictionary<uint, ulong> _textures = [];

    Material _material = null!;

    public bool Simplified { get; private init; }

    public static bool IsCloth(string template) => template == DetailTemplate || template == SimplifiedTemplate;

    public static ClothParameters From(Material material, string template)
    {
        var parameters = new ClothParameters { Simplified = template == SimplifiedTemplate, _material = material };

        foreach (var parameter in material.Parameters)
        {
            if (parameter.ObjectClass == Material.UvTransformHash)
                parameters._transforms[parameter.Name] = parameter.Transform;
            else if (parameter.ObjectClass == Material.TextureSelectorHash)
                parameters._textures[parameter.Name] = parameter.TextureId;
            else
                parameters._values[parameter.Name] = parameter.Value;
        }

        return parameters;
    }

    public ClothParameters With(ClothStyle? style)
    {
        var copy = new ClothParameters { Simplified = Simplified, _material = _material };

        foreach (var (name, value) in _values)
            copy._values[name] = value;
        foreach (var (name, transform) in _transforms)
            copy._transforms[name] = transform;
        foreach (var (name, texture) in _textures)
            copy._textures[name] = texture;

        if (style is null)
            return copy;

        foreach (var (name, value, target) in style.Values)
        {
            if (!target.AppliesTo(_material))
                continue;

            if (value.Transform is { } transform)
                copy._transforms[name] = transform;
            else if (_textures.ContainsKey(name))
                copy._textures[name] = value.Id;
            else
                copy._values[name] = value.Number;
        }

        return copy;
    }

    public float Float(string name, float fallback = 0) => _values.TryGetValue(ResourceTypes.Crc32(name), out var value) ? value.X : fallback;

    public Vector3 Vector(string name, Vector3 fallback) => _values.TryGetValue(ResourceTypes.Crc32(name), out var value) ? new Vector3(value.X, value.Y, value.Z) : fallback;

    public UvTransform Transform(string name) => _transforms.TryGetValue(ResourceTypes.Crc32(name), out var transform) ? transform : UvTransform.Identity;

    public ulong Texture(string name) => _textures.TryGetValue(ResourceTypes.Crc32(name), out var texture) ? texture : 0;
}
