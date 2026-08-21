using System.Collections.Generic;
using System.Windows.Media;

namespace Wildlands.Toolkit;

public static class TypeIcon
{
    static readonly Dictionary<string, Geometry> ByType = new()
    {
        ["Mesh"] = Parse("M 8,1 L 15,4.5 L 15,11.5 L 8,15 L 1,11.5 L 1,4.5 Z M 8,8 L 1,4.5 M 8,8 L 15,4.5 M 8,8 L 8,15"),

        ["TextureMap"] = Parse("M 1.5,3 H 14.5 V 13 H 1.5 Z M 1.5,10.5 L 5.5,6.5 L 8,9 L 10,7 L 14.5,11.5 M 10,5.5 A 1.3,1.3 0 1 1 12.6,5.5 A 1.3,1.3 0 1 1 10,5.5"),

        ["TextureSet"] = Parse("M 4.5,2.5 H 14.5 V 11 M 1.5,5.5 H 11.5 V 14 H 1.5 Z M 1.5,12 L 4,9.5 L 6,11.5 L 8,9.5 L 11.5,13"),

        ["CompiledMip"] = Parse("M 1.5,2.5 H 9.5 V 10.5 H 1.5 Z M 10.5,2.5 H 14.5 V 6.5 H 10.5 Z M 10.5,7.5 H 12.5 V 9.5 H 10.5 Z"),

        ["Material"] = Parse("M 1.5,8 A 6.5,6.5 0 1 1 14.5,8 A 6.5,6.5 0 1 1 1.5,8 M 4.5,6 A 4,4 0 0 1 9,4"),

        ["Animation"] = Parse("M 1.5,8 H 14.5 M 4,5 V 11 M 8,3.5 V 12.5 M 12,5 V 11"),

        ["BuildTable"] = Parse("M 1.5,3 H 14.5 V 13 H 1.5 Z M 1.5,6.5 H 14.5 M 6,6.5 V 13 M 10.5,6.5 V 13"),

        ["Entity"] = Parse("M 4.5,4.5 H 11.5 V 11.5 H 4.5 Z M 6.5,1.5 V 4.5 M 9.5,1.5 V 4.5 M 6.5,11.5 V 14.5 M 9.5,11.5 V 14.5 M 1.5,6.5 H 4.5 M 1.5,9.5 H 4.5 M 11.5,6.5 H 14.5 M 11.5,9.5 H 14.5"),

        ["EntityBuilder"] = Parse("M 4.5,4.5 H 11.5 V 11.5 H 4.5 Z M 6.5,1.5 V 4.5 M 9.5,1.5 V 4.5 M 6.5,11.5 V 14.5 M 9.5,11.5 V 14.5 M 1.5,6.5 H 4.5 M 1.5,9.5 H 4.5 M 11.5,6.5 H 14.5 M 11.5,9.5 H 14.5"),

        ["World"] = Parse("M 1.5,8 A 6.5,6.5 0 1 1 14.5,8 A 6.5,6.5 0 1 1 1.5,8 M 1.5,8 H 14.5 M 8,1.5 A 5,6.5 0 0 1 8,14.5 A 5,6.5 0 0 1 8,1.5"),

        ["data"] = Parse("M 1.5,3.5 H 6 L 7.5,5.5 H 14.5 V 13 H 1.5 Z"),
    };

    static readonly Geometry Sheet = Parse("M 3.5,1.5 H 9.5 L 12.5,4.5 V 14.5 H 3.5 Z M 9.5,1.5 V 4.5 H 12.5");

    public static Geometry For(string type) => ByType.TryGetValue(type, out var icon) ? icon : Sheet;

    static Geometry Parse(string path)
    {
        var geometry = Geometry.Parse(path);
        geometry.Freeze();
        return geometry;
    }
}
