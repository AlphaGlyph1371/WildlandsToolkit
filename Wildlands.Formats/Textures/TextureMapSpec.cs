using System;
using System.IO;

namespace Wildlands.Formats.Textures;

public static class TextureMapSpec
{
    public const uint ClassHash = 0x989DC6B2;

    public static ulong TextureOf(byte[] resource)
    {
        if (resource.Length < 22)
            throw new InvalidDataException("A TextureMapSpec is too short to name its texture.");

        return BitConverter.ToUInt64(resource, resource.Length - 8);
    }
}
