using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Wildlands.Formats.Models;

public enum GeometryKind
{
    None,
    Plain,
    Clustered,
}

public sealed class MeshInstancing
{
    public const uint ClassHash = 0x0B1D34C1;

    public ulong Id { get; set; }
    public bool ShadowCaster { get; set; }
    public ushort SubMeshIndex { get; set; }
    public short Padding { get; set; }
    public ushort MaterialType { get; set; }
    // This UInt16 sits in the instancing draw record. Comparing shipped meshes
    // shows that it is the vertex count for SubMeshIndex (not a rendering mask).
    // Leaving the template's old value after replacing geometry makes Anvil read
    // beyond the new vertex buffer and eventually destabilises the renderer.
    public int VertexCount { get; set; }
    public byte MaterialPointerTag { get; set; }
    public ulong MaterialId { get; set; }
    public byte[] BoneTable { get; set; } = new byte[BoneTableSize];

    public const int BoneTableSize = 256;
}

public sealed class MeshMaterial
{
    public const uint ClassHash = 0x9F1640AF;

    public ulong Id { get; set; }
    public byte ReferenceTag { get; set; }
    public byte Global { get; set; }
    public ulong MaterialId { get; set; }
    public byte HandleTag { get; set; }
    public ulong HandleId { get; set; }
}

public sealed class Mesh
{
    public const uint ClassHash = 0x415D9568;
    public const uint CompiledMeshHash = 0xFC9E1595;

    const int MaximumListItems = 1_000_000;

    public ulong Id { get; set; }
    public byte Category { get; set; }
    public byte DescriptorMask { get; set; }
    public bool Generated { get; set; }
    public uint HeaderFlags { get; set; }

    public List<byte> SubMeshTags { get; } = [];
    public List<ulong> SubMeshIds { get; } = [];
    public List<MeshBone> Bones { get; } = [];

    public float[] ExtentMin { get; } = new float[4];
    public float[] ExtentMax { get; } = new float[4];

    public byte CompiledMeshTag { get; set; }
    public ulong CompiledMeshId { get; set; }
    public byte[] PlatformData { get; set; } = [];
    public ulong ClusteredId { get; set; }
    public ClusteredMeshData? Clustered { get; set; }
    public ulong DataId { get; set; }
    public MeshData Data { get; set; } = new();
    public List<MeshInstancing> Instancing { get; } = [];

    public uint PlatformVersion { get; set; }
    public uint SdkVersion { get; set; }
    public float QuantizationFactor { get; set; }
    public float UvQuantizationFactor { get; set; }

    public List<MeshMaterial> Materials { get; } = [];

    public bool Dynamic { get; set; }
    public bool DynamicPrecomputedSkinning { get; set; }
    public bool UseFakeMeshDrawPrimMasking { get; set; }
    public bool UseFakeMeshVertexFormat { get; set; }
    public bool KeepSubMeshesFastLoad { get; set; }
    public bool KeepNonClusteredMeshData { get; set; }
    public bool UseEntityBoundsForCulling { get; set; }
    public bool IsHideable { get; set; }
    public bool ReservedA { get; set; }
    public bool ReservedB { get; set; }
    public int DecalType { get; set; }
    public bool IgnoreAlphaTestForDepthOnlyPass { get; set; }
    public bool IgnorePositionModificationDepthOnlyPass { get; set; }
    public bool MaterialAlwaysOverriddenInEngine { get; set; }
    public int DynamicMeshVertexCount { get; set; }
    public int DynamicMeshIndexCount { get; set; }
    public int UserCategory { get; set; }

    public GeometryKind Geometry => Clustered is not null ? GeometryKind.Clustered
        : Data.VertexBuffer.Length > 0 ? GeometryKind.Plain
        : GeometryKind.None;

    public byte[] VertexBuffer => Clustered is not null ? Clustered.VertexBuffer : Data.VertexBuffer;
    public byte[] IndexBuffer => Clustered is not null ? Clustered.IndexBuffer : Data.IndexBuffer;
    public int VertexStride => Clustered?.VertexStride ?? Data.VertexStride;
    public int VertexFormat => Clustered?.VertexFormat ?? Data.VertexFormat;

    public static Mesh Read(byte[] resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        using var stream = new MemoryStream(resource, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        var mesh = new Mesh();

        mesh.Id = ReadHeader(reader, ClassHash, "Mesh").Id;
        mesh.Category = ReadByte(reader, "category");
        mesh.DescriptorMask = ReadByte(reader, "descriptor mask");
        mesh.Generated = ReadBoolean(reader, "generated flag");
        mesh.HeaderFlags = ReadUInt32(reader, "header flags");

        int subMeshes = ReadCount(reader, "sub-mesh");
        for (int i = 0; i < subMeshes; i++)
        {
            byte tag = ReadByte(reader, $"sub-mesh {i} pointer tag");
            if (tag is not (1 or 2 or 3))
                throw new InvalidDataException($"Sub-mesh {i} uses unsupported object-pointer tag {tag}.");

            mesh.SubMeshTags.Add(tag);
            mesh.SubMeshIds.Add(tag == 3 ? 0 : ReadUInt64(reader, $"sub-mesh {i} id"));
        }

        int bones = ReadCount(reader, "bone");
        for (int i = 0; i < bones; i++)
            mesh.Bones.Add(MeshBone.Read(reader));

        for (int i = 0; i < 4; i++) mesh.ExtentMin[i] = ReadSingle(reader, "extent minimum");
        for (int i = 0; i < 4; i++) mesh.ExtentMax[i] = ReadSingle(reader, "extent maximum");

        mesh.CompiledMeshTag = ReadByte(reader, "compiled-mesh pointer tag");
        mesh.ReadCompiledMesh(reader);

        int materials = ReadCount(reader, "material");
        for (int i = 0; i < materials; i++)
            mesh.Materials.Add(ReadMaterial(reader, i));

        mesh.Dynamic = ReadBoolean(reader, "dynamic flag");
        mesh.DynamicPrecomputedSkinning = ReadBoolean(reader, "precomputed-skinning flag");
        mesh.UseFakeMeshDrawPrimMasking = ReadBoolean(reader, "draw-prim masking flag");
        mesh.UseFakeMeshVertexFormat = ReadBoolean(reader, "fake vertex-format flag");
        mesh.KeepSubMeshesFastLoad = ReadBoolean(reader, "fast-load flag");
        mesh.KeepNonClusteredMeshData = ReadBoolean(reader, "keep non-clustered flag");
        mesh.UseEntityBoundsForCulling = ReadBoolean(reader, "entity-bounds culling flag");
        mesh.IsHideable = ReadBoolean(reader, "hideable flag");
        mesh.ReservedA = ReadBoolean(reader, "reserved flag A");
        mesh.ReservedB = ReadBoolean(reader, "reserved flag B");
        mesh.DecalType = ReadInt32(reader, "decal type");
        mesh.IgnoreAlphaTestForDepthOnlyPass = ReadBoolean(reader, "depth-only alpha-test flag");
        mesh.IgnorePositionModificationDepthOnlyPass = ReadBoolean(reader, "depth-only position flag");
        mesh.MaterialAlwaysOverriddenInEngine = ReadBoolean(reader, "material-override flag");
        mesh.DynamicMeshVertexCount = ReadInt32(reader, "dynamic vertex count");
        mesh.DynamicMeshIndexCount = ReadInt32(reader, "dynamic index count");
        mesh.UserCategory = ReadInt32(reader, "user category");

        if (stream.Position != stream.Length)
            throw new InvalidDataException(
                $"The Mesh has {stream.Length - stream.Position} unexplained byte(s) at 0x{stream.Position:X}.");

        return mesh;
    }

    public byte[] Write()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        writer.Write(Id);
        writer.Write(ClassHash);
        writer.Write(Category);
        writer.Write(DescriptorMask);
        writer.Write(Generated);
        writer.Write(HeaderFlags);

        if (SubMeshTags.Count != SubMeshIds.Count)
            throw new InvalidDataException("The Mesh has a different number of sub-mesh tags and ids.");

        writer.Write(SubMeshTags.Count);
        for (int i = 0; i < SubMeshTags.Count; i++)
        {
            writer.Write(SubMeshTags[i]);
            if (SubMeshTags[i] != 3)
                writer.Write(SubMeshIds[i]);
        }

        writer.Write(Bones.Count);
        foreach (var bone in Bones)
            bone.Write(writer);

        for (int i = 0; i < 4; i++) writer.Write(ExtentMin[i]);
        for (int i = 0; i < 4; i++) writer.Write(ExtentMax[i]);

        writer.Write(CompiledMeshTag);
        WriteCompiledMesh(writer);

        writer.Write(Materials.Count);
        foreach (var material in Materials)
        {
            writer.Write(material.Id);
            writer.Write(MeshMaterial.ClassHash);
            writer.Write(material.ReferenceTag);
            writer.Write(material.Global);
            writer.Write(material.MaterialId);
            writer.Write(material.HandleTag);
            writer.Write(material.HandleId);
        }

        writer.Write(Dynamic);
        writer.Write(DynamicPrecomputedSkinning);
        writer.Write(UseFakeMeshDrawPrimMasking);
        writer.Write(UseFakeMeshVertexFormat);
        writer.Write(KeepSubMeshesFastLoad);
        writer.Write(KeepNonClusteredMeshData);
        writer.Write(UseEntityBoundsForCulling);
        writer.Write(IsHideable);
        writer.Write(ReservedA);
        writer.Write(ReservedB);
        writer.Write(DecalType);
        writer.Write(IgnoreAlphaTestForDepthOnlyPass);
        writer.Write(IgnorePositionModificationDepthOnlyPass);
        writer.Write(MaterialAlwaysOverriddenInEngine);
        writer.Write(DynamicMeshVertexCount);
        writer.Write(DynamicMeshIndexCount);
        writer.Write(UserCategory);

        return stream.ToArray();
    }

    void ReadCompiledMesh(BinaryReader reader)
    {
        CompiledMeshId = ReadHeader(reader, CompiledMeshHash, "CompiledMesh").Id;
        PlatformData = ReadBytes(reader, ReadCount(reader, "platform-data byte"), "platform data");

        byte clusteredTag = ReadByte(reader, "clustered-mesh pointer tag");
        if (clusteredTag is not (0 or 3))
            throw new InvalidDataException($"The clustered mesh uses unsupported object-pointer tag {clusteredTag}.");

        if (clusteredTag == 0)
        {
            ClusteredId = ReadHeader(reader, ClusteredMeshData.ClassHash, "ClusteredMeshData").Id;
            Clustered = ClusteredMeshData.Read(reader);
        }

        DataId = ReadHeader(reader, MeshData.ClassHash, "MeshData").Id;
        Data = MeshData.Read(reader);

        int instancing = ReadCount(reader, "instancing");
        for (int i = 0; i < instancing; i++)
            Instancing.Add(ReadInstancing(reader, i));

        PlatformVersion = ReadUInt32(reader, "platform version");
        SdkVersion = ReadUInt32(reader, "SDK version");
        QuantizationFactor = ReadSingle(reader, "quantization factor");
        UvQuantizationFactor = ReadSingle(reader, "uv quantization factor");
    }

    void WriteCompiledMesh(BinaryWriter writer)
    {
        writer.Write(CompiledMeshId);
        writer.Write(CompiledMeshHash);
        writer.Write(PlatformData.Length);
        writer.Write(PlatformData);

        if (Clustered is null)
        {
            writer.Write((byte)3);
        }
        else
        {
            writer.Write((byte)0);
            writer.Write(ClusteredId);
            writer.Write(ClusteredMeshData.ClassHash);
            Clustered.Write(writer);
        }

        writer.Write(DataId);
        writer.Write(MeshData.ClassHash);
        Data.Write(writer);

        writer.Write(Instancing.Count);
        foreach (var entry in Instancing)
        {
            if (entry.BoneTable.Length != MeshInstancing.BoneTableSize)
                throw new InvalidDataException(
                    $"An instancing entry has a {entry.BoneTable.Length}-byte bone table; {MeshInstancing.BoneTableSize} are required.");
            if (entry.VertexCount < 0 || entry.VertexCount > ushort.MaxValue)
                throw new InvalidDataException(
                    $"An instancing entry has vertex count {entry.VertexCount}; only 0..{ushort.MaxValue} fit this mesh format.");

            writer.Write(entry.Id);
            writer.Write(MeshInstancing.ClassHash);
            writer.Write(entry.ShadowCaster);
            writer.Write(entry.SubMeshIndex);
            writer.Write(entry.Padding);
            writer.Write(entry.MaterialType);
            writer.Write((ushort)entry.VertexCount);
            writer.Write(entry.MaterialPointerTag);
            writer.Write(entry.MaterialId);
            writer.Write(entry.BoneTable);
        }

        writer.Write(PlatformVersion);
        writer.Write(SdkVersion);
        writer.Write(QuantizationFactor);
        writer.Write(UvQuantizationFactor);
    }

    static MeshInstancing ReadInstancing(BinaryReader reader, int index)
    {
        var header = ReadHeader(reader, MeshInstancing.ClassHash, $"instancing {index}");

        return new MeshInstancing
        {
            Id = header.Id,
            ShadowCaster = ReadBoolean(reader, $"instancing {index} shadow-caster flag"),
            SubMeshIndex = ReadUInt16(reader, $"instancing {index} sub-mesh index"),
            Padding = ReadInt16(reader, $"instancing {index} padding"),
            MaterialType = ReadUInt16(reader, $"instancing {index} material type"),
            VertexCount = ReadUInt16(reader, $"instancing {index} vertex count"),
            MaterialPointerTag = ReadByte(reader, $"instancing {index} material pointer tag"),
            MaterialId = ReadUInt64(reader, $"instancing {index} material id"),
            BoneTable = ReadBytes(reader, MeshInstancing.BoneTableSize, $"instancing {index} bone table"),
        };
    }

    static MeshMaterial ReadMaterial(BinaryReader reader, int index)
    {
        var header = ReadHeader(reader, MeshMaterial.ClassHash, $"material {index}");

        return new MeshMaterial
        {
            Id = header.Id,
            ReferenceTag = ReadByte(reader, $"material {index} reference tag"),
            Global = ReadByte(reader, $"material {index} global flag"),
            MaterialId = ReadUInt64(reader, $"material {index} id"),
            HandleTag = ReadByte(reader, $"material {index} handle tag"),
            HandleId = ReadUInt64(reader, $"material {index} handle id"),
        };
    }

    static ScimitarHeader ReadHeader(BinaryReader reader, uint expected, string field)
    {
        Ensure(reader, 12, field);
        return ScimitarHeader.Read(reader, expected);
    }

    static int ReadCount(BinaryReader reader, string field)
    {
        int value = ReadInt32(reader, field + " count");

        if (value < 0 || value > MaximumListItems)
            throw new InvalidDataException($"Invalid {field} count {value}.");

        return value;
    }

    static byte ReadByte(BinaryReader reader, string field)
    {
        Ensure(reader, 1, field);
        return reader.ReadByte();
    }

    static bool ReadBoolean(BinaryReader reader, string field)
    {
        byte value = ReadByte(reader, field);

        if (value > 1)
            throw new InvalidDataException($"The {field} holds {value}, which is not a boolean.");

        return value == 1;
    }

    static short ReadInt16(BinaryReader reader, string field)
    {
        Ensure(reader, 2, field);
        return reader.ReadInt16();
    }

    static ushort ReadUInt16(BinaryReader reader, string field)
    {
        Ensure(reader, 2, field);
        return reader.ReadUInt16();
    }

    static int ReadInt32(BinaryReader reader, string field)
    {
        Ensure(reader, 4, field);
        return reader.ReadInt32();
    }

    static uint ReadUInt32(BinaryReader reader, string field)
    {
        Ensure(reader, 4, field);
        return reader.ReadUInt32();
    }

    static ulong ReadUInt64(BinaryReader reader, string field)
    {
        Ensure(reader, 8, field);
        return reader.ReadUInt64();
    }

    static float ReadSingle(BinaryReader reader, string field)
    {
        Ensure(reader, 4, field);
        return reader.ReadSingle();
    }

    static byte[] ReadBytes(BinaryReader reader, int count, string field)
    {
        Ensure(reader, count, field);
        return reader.ReadBytes(count);
    }

    static void Ensure(BinaryReader reader, int count, string field)
    {
        if (count < 0 || reader.BaseStream.Position > reader.BaseStream.Length - count)
            throw new EndOfStreamException(
                $"Unexpected end of Mesh while reading {field} at 0x{reader.BaseStream.Position:X}.");
    }
}
