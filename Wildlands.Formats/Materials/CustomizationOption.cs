using System;

namespace Wildlands.Formats.Materials;

public static class CustomizationOption
{
    public const uint ClassHash = 0x65594AE5;
    const uint BuildTagHash = 0xB332698E;

    public static uint? TagOf(byte[] resource)
    {
        if (resource.Length < 29 || BitConverter.ToUInt32(resource, 21) != BuildTagHash)
            return null;

        return BitConverter.ToUInt32(resource, 25);
    }
}
