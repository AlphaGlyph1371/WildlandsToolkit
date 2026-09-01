using System.IO;
using System.Numerics;

namespace Wildlands.Formats.Models;

public sealed class MeshBone
{
    public const uint ClassHash = 0x9EF0E7A1;

    public ulong Id { get; set; }
    public uint Name { get; set; }
    public Matrix4x4 Matrix { get; set; }
    public bool UsedBySubMeshes { get; set; }
    public bool UsedBySkeletonLodMapping { get; set; }

    public static MeshBone Read(BinaryReader reader)
    {
        var header = ScimitarHeader.Read(reader, ClassHash);

        return new MeshBone
        {
            Id = header.Id,
            Name = reader.ReadUInt32(),
            Matrix = new Matrix4x4(
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(),
                reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()),
            UsedBySubMeshes = reader.ReadBoolean(),
            UsedBySkeletonLodMapping = reader.ReadBoolean(),
        };
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(Id);
        writer.Write(ClassHash);
        writer.Write(Name);

        writer.Write(Matrix.M11); writer.Write(Matrix.M12); writer.Write(Matrix.M13); writer.Write(Matrix.M14);
        writer.Write(Matrix.M21); writer.Write(Matrix.M22); writer.Write(Matrix.M23); writer.Write(Matrix.M24);
        writer.Write(Matrix.M31); writer.Write(Matrix.M32); writer.Write(Matrix.M33); writer.Write(Matrix.M34);
        writer.Write(Matrix.M41); writer.Write(Matrix.M42); writer.Write(Matrix.M43); writer.Write(Matrix.M44);

        writer.Write(UsedBySubMeshes);
        writer.Write(UsedBySkeletonLodMapping);
    }
}
