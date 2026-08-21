using System.IO;

namespace Wildlands.Formats.Forge;

public static class ArchiveBackup
{
    public const string Suffix = ".original";

    public static string PathFor(string archivePath) => archivePath + Suffix;

    public static bool Exists(string archivePath) => File.Exists(PathFor(archivePath));

    public static void Ensure(string archivePath)
    {
        string backup = PathFor(archivePath);
        if (File.Exists(backup))
            return;

        string partial = backup + ".part";
        File.Copy(archivePath, partial, overwrite: true);
        File.Move(partial, backup);
    }
}
