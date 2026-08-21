using System.IO;
using System.Numerics;

namespace Wildlands.Formats.Models;

public sealed class MeshBone
{
    public const uint ClassHash = 0x9EF0E7A1;

    public uint Name { get; private set; }
    public Matrix4x4 Matrix { get; private set; }
    public bool UsedBySubMeshes { get; private set; }
    public bool UsedBySkeletonLodMapping { get; private set; }

    public static MeshBone Read(BinaryReader reader)
    {
        ScimitarHeader.Read(reader, ClassHash);

        return new MeshBone
        {
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
}
