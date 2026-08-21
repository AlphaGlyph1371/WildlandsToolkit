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

public sealed class Mesh
{
    public const uint ClassHash = 0x415D9568;

    const uint CompiledMeshHash = 0xFC9E1595;
    const uint MeshDataHash = 0x0645ABB5;
    const uint DynamicReferenceHash = 0x9F1640AF;

    public ulong Id { get; private set; }
    public byte DescriptorMask { get; private set; }
    public bool Generated { get; private set; }
    public int SubMeshCount { get; private set; }
    public int BoneCount { get; private set; }
    public List<MeshBone> Bones { get; } = [];

    public List<ulong> MaterialIds { get; } = [];

    public float[] ExtentMin { get; private set; } = new float[4];
    public float[] ExtentMax { get; private set; } = new float[4];

    public GeometryKind Geometry { get; private set; }
    public MeshData? Data { get; private set; }
    public ClusteredMeshData? Clustered { get; private set; }

    public uint PlatformVersion { get; private set; }
    public uint SdkVersion { get; private set; }
    public float QuantizationFactor { get; private set; }
    public float UvQuantizationFactor { get; private set; }

    public string Note { get; private set; } = "";

    public byte[] VertexBuffer => Clustered is not null ? Clustered.VertexBuffer : Data?.VertexBuffer ?? [];
    public byte[] IndexBuffer => Clustered is not null ? Clustered.IndexBuffer : Data?.IndexBuffer ?? [];
    public int VertexStride => Clustered?.VertexStride ?? Data?.VertexStride ?? 0;
    public int VertexFormat => Clustered?.VertexFormat ?? Data?.VertexFormat ?? 0;

    public static Mesh Read(byte[] resource)
    {
        using var stream = new MemoryStream(resource);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        var mesh = new Mesh();

        mesh.Id = ScimitarHeader.Read(reader, ClassHash).Id;

        reader.ReadByte();
        mesh.DescriptorMask = reader.ReadByte();
        mesh.Generated = reader.ReadBoolean();

        // Wildlands carries one more field here than Breakpoint does, the same way its TextureMap header does
        reader.ReadUInt32();

        mesh.SubMeshCount = reader.ReadInt32();
        for (int i = 0; i < mesh.SubMeshCount; i++)
        {
            if (!SkipObjectPointer(reader))
            {
                mesh.Note = "unknown object pointer tag";
                return mesh;
            }
        }

        mesh.BoneCount = reader.ReadInt32();
        for (int i = 0; i < mesh.BoneCount; i++)
            mesh.Bones.Add(MeshBone.Read(reader));

        for (int i = 0; i < 4; i++) mesh.ExtentMin[i] = reader.ReadSingle();
        for (int i = 0; i < 4; i++) mesh.ExtentMax[i] = reader.ReadSingle();

        reader.ReadByte();
        mesh.ReadCompiledMesh(reader);

        // The material list sits right behind the CompiledMesh
        int materials = reader.ReadInt32();
        for (int i = 0; i < materials; i++)
            mesh.MaterialIds.Add(ReadDynamicReference(reader));

        return mesh;
    }

    // A dynamic reference is 31 bytes
    static ulong ReadDynamicReference(BinaryReader reader)
    {
        ScimitarHeader.Read(reader, DynamicReferenceHash);

        reader.ReadByte();
        reader.ReadByte();
        ulong id = reader.ReadUInt64();

        reader.ReadByte();
        reader.ReadUInt64();

        return id;
    }

    void ReadCompiledMesh(BinaryReader reader)
    {
        ScimitarHeader.Read(reader, CompiledMeshHash);
        reader.ReadBytes(reader.ReadInt32()); // platform blob

        // A tag of 3 means this mesh has no clustered form
        if (reader.ReadByte() != 3)
        {
            ScimitarHeader.Read(reader, ClusteredMeshData.ClassHash);
            Clustered = ClusteredMeshData.Read(reader);
        }

        ScimitarHeader.Read(reader, MeshDataHash);
        Data = MeshData.Read(reader);

        int instancing = reader.ReadInt32();
        for (int i = 0; i < instancing; i++)
            SkipInstancingData(reader);

        PlatformVersion = reader.ReadUInt32();
        SdkVersion = reader.ReadUInt32();
        QuantizationFactor = reader.ReadSingle();
        UvQuantizationFactor = reader.ReadSingle();

        Geometry = Clustered is not null ? GeometryKind.Clustered
            : Data.VertexBuffer.Length > 0 ? GeometryKind.Plain
            : GeometryKind.None;
    }

    static bool SkipObjectPointer(BinaryReader reader)
    {
        if (reader.ReadByte() != 1)
            return false;

        reader.ReadUInt64();
        return true;
    }

    static void SkipInstancingData(BinaryReader reader)
    {
        ScimitarHeader.Read(reader);

        reader.ReadBoolean();
        reader.ReadUInt16();
        reader.ReadInt16();
        reader.ReadUInt16();
        reader.ReadInt16();

        reader.ReadByte();
        reader.ReadUInt64();

        reader.ReadBytes(256);
    }
}
