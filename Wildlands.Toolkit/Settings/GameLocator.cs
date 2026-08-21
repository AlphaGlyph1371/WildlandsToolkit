using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Wildlands.Toolkit;

public static class GameLocator
{
    const string Executable = "GRW.exe";

    public static string? FindGameFolder()
    {
        foreach (var library in SteamLibraries())
        {
            var candidate = Path.Combine(library, "steamapps", "common", "Wildlands");
            if (File.Exists(Path.Combine(candidate, Executable)))
                return candidate;
        }

        return null;
    }

    public static bool LooksLikeGameFolder(string path)
    {
        return File.Exists(Path.Combine(path, Executable));
    }

    static IEnumerable<string> SteamLibraries()
    {
        var steam = SteamInstallPath();
        if (steam is null)
            yield break;

        yield return steam;

        var manifest = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(manifest))
            yield break;

        foreach (Match match in Regex.Matches(File.ReadAllText(manifest), "\"path\"\\s+\"([^\"]+)\""))
            yield return match.Groups[1].Value.Replace(@"\\", @"\");
    }

    static string? SteamInstallPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("SteamPath") as string;
    }
}
