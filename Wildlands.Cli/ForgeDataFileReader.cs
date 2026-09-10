using System.IO;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;

internal static class ForgeDataFileReader
{
    public static bool TryRead(ForgeArchive archive, ForgeEntry entry, out DataFile file, out string error)
    {
        try
        {
            using var stream = new MemoryStream(archive.ReadEntry(entry), writable: false);
            file = DataFile.Read(stream);
            error = "";
            return true;
        }
        catch (Exception exception)
        {
            file = null!;
            error = exception.Message;
            return false;
        }
    }
}
