using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

namespace Wildlands.Formats.Models;

public static class FbxWriter
{
    const int Version = 7400;

    const string ClassSeparator = "\0\u0001";

    const long GeometryId = 1000000;
    const long ModelId = 2000000;
    const long SkeletonRootId = 2500000;
    const long BoneModelBase = 3000000;
    const long SkinId = 4000000;
    const long ClusterBase = 5000000;
    const long MaterialBase = 6000000;

    static readonly byte[] FooterId =
    [
        0xFA, 0xBC, 0xAB, 0x09, 0xD0, 0xC8, 0xD4, 0x66,
        0xB1, 0x76, 0xFB, 0x83, 0x1C, 0xF7, 0x26, 0x7E,
    ];

    static readonly byte[] FooterMagic =
    [
        0xF8, 0x5A, 0x8C, 0x6A, 0xDE, 0xF5, 0xD9, 0x7E,
        0xEC, 0xE9, 0x0C, 0xE3, 0x75, 0x8F, 0x29, 0x0B,
    ];

    public static void Write(Mesh mesh, string name, string path, List<SkeletonBone>? skeleton = null, List<string>? materialNames = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);

        var vertices = MeshGeometry.ReadVertices(mesh);
        var ranges = mesh.Data.Standard;

        var triangles = new List<int>();
        var polygonMaterial = new List<int>();

        for (int r = 0; r < ranges.Count; r++)
        {
            var range = MeshGeometry.ReadTriangles(mesh, ranges[r]);
            triangles.AddRange(range);

            for (int t = 0; t < range.Length / 3; t++)
                polygonMaterial.Add(r);
        }

        var materials = new List<string>();
        for (int r = 0; r < ranges.Count; r++)
        {
            string label = materialNames is not null && r < materialNames.Count && materialNames[r].Length > 0
                ? materialNames[r]
                : r < mesh.Materials.Count ? "Material_" + mesh.Materials[r].MaterialId.ToString("X16") : "Material_" + r;
            materials.Add(label);
        }

        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);

        writer.Write(Encoding.ASCII.GetBytes("Kaydara FBX Binary  \0"));
        writer.Write((byte)0x1A);
        writer.Write((byte)0x00);
        writer.Write((uint)Version);

        foreach (var node in BuildTree(name, vertices, [.. triangles], [.. polygonMaterial], materials, mesh.Bones, skeleton))
            node.Write(writer);

        writer.Write(new byte[FbxNode.Terminator]);
        WriteFooter(writer);
    }

    static IEnumerable<FbxNode> BuildTree(string name, MeshVertex[] vertices, int[] triangles, int[] polygonMaterial,
        List<string> materials, List<MeshBone> bones, List<SkeletonBone>? skeleton)
    {
        bool skinned = bones.Count > 0 && vertices.Length > 0 && vertices[0].JointIndices.Length > 0;
        var parents = BoneHierarchy.Resolve(bones, skeleton);

        var header = new FbxNode("FBXHeaderExtension");
        header.Add("FBXHeaderVersion").Property(1003);
        header.Add("FBXVersion").Property(Version);
        header.Add("Creator").Property("Wildlands Toolkit");
        yield return header;

        var settings = new FbxNode("GlobalSettings");
        settings.Add("Version").Property(1000);
        var settingProperties = settings.Add("Properties70");
        Setting(settingProperties, "UpAxis", 2);
        Setting(settingProperties, "UpAxisSign", 1);
        Setting(settingProperties, "FrontAxis", 1);
        Setting(settingProperties, "FrontAxisSign", -1);
        Setting(settingProperties, "CoordAxis", 0);
        Setting(settingProperties, "CoordAxisSign", 1);

        settingProperties.Add("P").Property("UnitScaleFactor").Property("double").Property("Number").Property("").Property(100.0);
        yield return settings;

        int modelCount = skinned ? 2 + bones.Count : 1;
        int deformerCount = skinned ? 1 + bones.Count : 0;

        var definitions = new FbxNode("Definitions");
        definitions.Add("Version").Property(100);
        definitions.Add("Count").Property(1 + modelCount + deformerCount + materials.Count);
        definitions.Add("ObjectType").Property("Geometry").Add("Count").Property(1);
        definitions.Add("ObjectType").Property("Model").Add("Count").Property(modelCount);
        if (materials.Count > 0)
            definitions.Add("ObjectType").Property("Material").Add("Count").Property(materials.Count);
        if (skinned)
            definitions.Add("ObjectType").Property("Deformer").Add("Count").Property(deformerCount);
        yield return definitions;

        var objects = new FbxNode("Objects");
        BuildGeometry(objects, name, vertices, triangles, polygonMaterial, materials.Count);

        objects.Add("Model")
            .Property(ModelId)
            .Property(name + ClassSeparator + "Model")
            .Property("Mesh")
            .Add("Version").Property(232);

        for (int i = 0; i < materials.Count; i++)
            BuildMaterial(objects, materials[i], i);

        if (skinned)
        {
            objects.Add("Model")
                .Property(SkeletonRootId)
                .Property("Armature" + ClassSeparator + "Model")
                .Property("Null")
                .Add("Version").Property(232);

            for (int i = 0; i < bones.Count; i++)
                BuildBoneModel(objects, bones[i], i, parents[i] < 0 ? null : bones[parents[i]]);

            BuildSkin(objects, vertices, bones);
        }

        yield return objects;

        var connections = new FbxNode("Connections");
        connections.Add("C").Property("OO").Property(ModelId).Property(0L);
        connections.Add("C").Property("OO").Property(GeometryId).Property(ModelId);

        for (int i = 0; i < materials.Count; i++)
            connections.Add("C").Property("OO").Property(MaterialBase + i).Property(ModelId);

        if (skinned)
        {
            connections.Add("C").Property("OO").Property(SkeletonRootId).Property(0L);
            connections.Add("C").Property("OO").Property(SkinId).Property(GeometryId);

            for (int i = 0; i < bones.Count; i++)
            {
                long boneId = BoneModelBase + i;
                long clusterId = ClusterBase + i;
                long parentId = parents[i] < 0 ? SkeletonRootId : BoneModelBase + parents[i];

                connections.Add("C").Property("OO").Property(boneId).Property(parentId);
                connections.Add("C").Property("OO").Property(clusterId).Property(SkinId);
                connections.Add("C").Property("OO").Property(boneId).Property(clusterId);
            }
        }

        yield return connections;
    }

    static void BuildMaterial(FbxNode objects, string name, int index)
    {
        var material = objects.Add("Material")
            .Property(MaterialBase + index)
            .Property(name + ClassSeparator + "Material")
            .Property("");

        material.Add("Version").Property(102);
        material.Add("ShadingModel").Property("phong");
        material.Add("MultiLayer").Property(0);

        var properties = material.Add("Properties70");
        properties.Add("P").Property("DiffuseColor").Property("Color").Property("").Property("A")
            .Property(0.8).Property(0.8).Property(0.8);
    }

    static void BuildGeometry(FbxNode objects, string name, MeshVertex[] vertices, int[] triangles, int[] polygonMaterial, int materialCount)
    {
        var geometry = objects.Add("Geometry")
            .Property(GeometryId)
            .Property(name + ClassSeparator + "Geometry")
            .Property("Mesh");

        geometry.Add("Vertices").Array(vertices
            .SelectMany(v => new double[] { v.Position.X, v.Position.Y, v.Position.Z })
            .ToArray());

        geometry.Add("PolygonVertexIndex").Array(triangles
            .Select((index, i) => i % 3 == 2 ? ~index : index)
            .ToArray());

        geometry.Add("GeometryVersion").Property(124);

        Layer(geometry, "LayerElementNormal", 0, "Normals", vertices
            .SelectMany(v => new double[] { v.Normal.X, v.Normal.Y, v.Normal.Z })
            .ToArray());

        Layer(geometry, "LayerElementTangent", 0, "Tangents", vertices
            .SelectMany(v => new double[] { v.Tangent.X, v.Tangent.Y, v.Tangent.Z })
            .ToArray());

        Layer(geometry, "LayerElementBinormal", 0, "Binormals", vertices
            .SelectMany(v => new double[] { v.Binormal.X, v.Binormal.Y, v.Binormal.Z })
            .ToArray());

        int uvSets = vertices.Length > 0 ? vertices[0].Uv.Length : 0;
        for (int set = 0; set < uvSets; set++)
        {
            int which = set;
            var texture = geometry.Add("LayerElementUV").Property(set);
            texture.Add("Version").Property(101);
            texture.Add("Name").Property(set == 0 ? "UVMap" : "UVMap" + set);
            texture.Add("MappingInformationType").Property("ByVertice");
            texture.Add("ReferenceInformationType").Property("Direct");
            texture.Add("UV").Array(vertices
                .SelectMany(v => new double[] { v.Uv[which].X, 1 - v.Uv[which].Y })
                .ToArray());
        }

        bool hasColor = vertices.Length > 0 && vertices.Any(v => v.Color != 0xFFFFFFFF);
        if (hasColor)
        {
            var colors = geometry.Add("LayerElementColor").Property(0);
            colors.Add("Version").Property(101);
            colors.Add("Name").Property("Col");
            colors.Add("MappingInformationType").Property("ByVertice");
            colors.Add("ReferenceInformationType").Property("Direct");
            colors.Add("Colors").Array(vertices
                .SelectMany(v => new double[]
                {
                    (v.Color & 0xFF) / 255.0,
                    (v.Color >> 8 & 0xFF) / 255.0,
                    (v.Color >> 16 & 0xFF) / 255.0,
                    (v.Color >> 24 & 0xFF) / 255.0,
                })
                .ToArray());
        }

        if (materialCount > 0)
        {
            var element = geometry.Add("LayerElementMaterial").Property(0);
            element.Add("Version").Property(101);
            element.Add("Name").Property("");
            element.Add("MappingInformationType").Property("ByPolygon");
            element.Add("ReferenceInformationType").Property("IndexToDirect");
            element.Add("Materials").Array(polygonMaterial);
        }

        var names = new List<string> { "LayerElementNormal", "LayerElementTangent", "LayerElementBinormal" };
        if (uvSets > 0) names.Add("LayerElementUV");
        if (hasColor) names.Add("LayerElementColor");
        if (materialCount > 0) names.Add("LayerElementMaterial");

        var layer = geometry.Add("Layer").Property(0);
        layer.Add("Version").Property(100);
        foreach (string element in names)
        {
            var entry = layer.Add("LayerElement");
            entry.Add("Type").Property(element);
            entry.Add("TypedIndex").Property(0);
        }

        for (int set = 1; set < uvSets; set++)
        {
            var extra = geometry.Add("Layer").Property(set);
            extra.Add("Version").Property(100);
            var entry = extra.Add("LayerElement");
            entry.Add("Type").Property("LayerElementUV");
            entry.Add("TypedIndex").Property(set);
        }
    }

    static void Layer(FbxNode geometry, string element, int index, string arrayName, double[] values)
    {
        var node = geometry.Add(element).Property(index);
        node.Add("Version").Property(101);
        node.Add("Name").Property("");
        node.Add("MappingInformationType").Property("ByVertice");
        node.Add("ReferenceInformationType").Property("Direct");
        node.Add(arrayName).Array(values);
    }

    static Matrix4x4 BoneToWorld(MeshBone bone)
    {
        Matrix4x4.Invert(bone.Matrix, out var boneToMesh);
        return boneToMesh;
    }

    static void BuildBoneModel(FbxNode objects, MeshBone bone, int index, MeshBone? parent)
    {
        var position = BoneToWorld(bone).Translation;
        var translation = parent is null ? position : position - BoneToWorld(parent).Translation;

        var model = objects.Add("Model")
            .Property(BoneModelBase + index)
            .Property("Bone_" + bone.Name.ToString("X8") + ClassSeparator + "Model")
            .Property("LimbNode");
        model.Add("Version").Property(232);

        var properties = model.Add("Properties70");
        properties.Add("P").Property("Lcl Translation").Property("Lcl Translation").Property("").Property("A")
            .Property((double)translation.X).Property((double)translation.Y).Property((double)translation.Z);
    }

    static void BuildSkin(FbxNode objects, MeshVertex[] vertices, List<MeshBone> bones)
    {
        var skin = objects.Add("Deformer").Property(SkinId).Property("Skin" + ClassSeparator + "Deformer").Property("Skin");
        skin.Add("Version").Property(101);
        skin.Add("Link_DeformAcuracy").Property(50.0);
        skin.Add("SkinningType").Property("Linear");

        var indices = new List<int>[bones.Count];
        var weights = new List<double>[bones.Count];
        for (int b = 0; b < bones.Count; b++)
        {
            indices[b] = [];
            weights[b] = [];
        }

        for (int v = 0; v < vertices.Length; v++)
        {
            var joints = vertices[v].JointIndices;
            var vertexWeights = vertices[v].JointWeights;

            for (int j = 0; j < joints.Length; j++)
            {
                byte bone = joints[j];
                byte weight = vertexWeights[j];

                if (weight == 0 || bone >= bones.Count)
                    continue;

                indices[bone].Add(v);
                weights[bone].Add(weight / 255.0);
            }
        }

        for (int b = 0; b < bones.Count; b++)
        {
            var cluster = objects.Add("Deformer").Property(ClusterBase + b).Property("Cluster" + ClassSeparator + "SubDeformer").Property("Cluster");
            cluster.Add("Version").Property(100);
            cluster.Add("UserData").Property("").Property("");
            cluster.Add("Indexes").Array(indices[b].ToArray());
            cluster.Add("Weights").Array(weights[b].ToArray());
            cluster.Add("Transform").Array(Flatten(bones[b].Matrix));
            cluster.Add("TransformLink").Array(Flatten(BoneToWorld(bones[b])));
        }
    }

    static double[] Flatten(Matrix4x4 m) =>
    [
        m.M11, m.M12, m.M13, m.M14,
        m.M21, m.M22, m.M23, m.M24,
        m.M31, m.M32, m.M33, m.M34,
        m.M41, m.M42, m.M43, m.M44,
    ];

    static void Setting(FbxNode properties, string name, int value) => properties.Add("P").Property(name).Property("int").Property("Integer").Property("").Property(value);

    static void WriteFooter(BinaryWriter writer)
    {
        writer.Write(FooterId);
        writer.Write(new byte[4]);

        long padding = 16 - writer.BaseStream.Position % 16;
        writer.Write(new byte[padding]);

        writer.Write((uint)Version);
        writer.Write(new byte[120]);
        writer.Write(FooterMagic);
    }
}
