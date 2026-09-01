using System;
using Wildlands.Formats.Models;
using Wildlands.Formats.Textures;
using Wildlands.Formats.Weather;

namespace Wildlands.Formats.Data;

public static class ResourceCheck
{
    public static string? Against(byte[] data, ulong id, uint classHash, string name)
    {
        ArgumentNullException.ThrowIfNull(data);
        string kind = ResourceTypes.NameOf(classHash);

        if (data.Length < 12)
            return $"This file is only {data.Length} bytes. A resource starts with an 8 byte id and a 4 byte type.";

        ulong fileId = BitConverter.ToUInt64(data);
        uint fileClass = BitConverter.ToUInt32(data, 8);

        if (fileClass != classHash)
            return $"This file says it is a {ResourceTypes.NameOf(fileClass)}, but {name} is a {kind}.\n\n"
                + "Putting it here will almost certainly break the archive.";

        if (fileId != id)
            return $"This file carries the id 0x{fileId:X} and {name} has 0x{id:X}.\n\n"
                + "Other resources point at the old id, so those references would go nowhere. "
                + "The file was most likely taken from a different place.";

        try
        {
            Parse(data, classHash);
        }
        catch (Exception ex)
        {
            return $"The file is a {kind} with the right id, but it does not read back cleanly:\n\n{ex.Message}";
        }

        return null;
    }

    static void Parse(byte[] data, uint classHash)
    {
        if (classHash == Mesh.ClassHash) Mesh.Read(data);
        else if (classHash == Skeleton.ClassHash) Skeleton.ReadAsset(data);
        else if (classHash == BuildTable.ClassHash) BuildTable.Read(data);
        else if (classHash == TextureMap.ClassHash) TextureMap.Read(data);
        else if (TimeCycle.IsTimeCycle(classHash)) TimeCycle.Read(data);
    }
}
