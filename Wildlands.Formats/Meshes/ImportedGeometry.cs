using System.Collections.Generic;

namespace Wildlands.Formats.Models;

public sealed class ImportedGroup
{
    public string Name { get; set; } = "";
    public List<MeshVertex> Vertices { get; } = [];
    public List<int> Indices { get; } = [];
    public bool HasTangents { get; set; }
}

public sealed class ImportedGeometry
{
    public List<ImportedGroup> Groups { get; } = [];

    public bool HasSkinning { get; set; }
    public bool HasTangents { get; set; }
    public int UvSets { get; set; }
    public bool HasColour { get; set; }

    public List<uint> JointNames { get; } = [];
}
