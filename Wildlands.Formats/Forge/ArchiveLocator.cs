using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Wildlands.Formats.Forge;

public static class ArchiveLocator
{
    public static List<string> Find(string gameFolder)
    {
        var found = new List<string>();

        if (gameFolder.Length == 0 || !Directory.Exists(gameFolder))
            return found;

        found.AddRange(Directory.GetFiles(gameFolder, "*.forge"));

        foreach (string folder in Directory.GetDirectories(gameFolder, "dlc_*").OrderBy(f => f))
            found.AddRange(Directory.GetFiles(folder, "*.forge"));

        return found;
    }

    public static List<string> Siblings(string archivePath)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(archivePath));
        if (folder is null)
            return [];

        bool inDlc = Path.GetFileName(folder).StartsWith("dlc_", System.StringComparison.OrdinalIgnoreCase);
        string root = inDlc ? Path.GetDirectoryName(folder) ?? folder : folder;

        return [.. Find(root).Where(p => !string.Equals(p, archivePath, System.StringComparison.OrdinalIgnoreCase))];
    }
}
