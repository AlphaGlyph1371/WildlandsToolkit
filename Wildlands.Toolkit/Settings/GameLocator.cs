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
            if (LooksLikeGameFolder(candidate))
                return candidate;
        }

        foreach (var candidate in UbisoftInstalls())
        {
            if (LooksLikeGameFolder(candidate))
                return candidate;
        }

        return null;
    }

    // Ubisoft Connect keeps one subkey per owned game, each with an InstallDir. Which of
    // them is Wildlands differs per account, so every folder is checked for the executable
    // instead of looking up an id.
    static IEnumerable<string> UbisoftInstalls()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var installs = root.OpenSubKey(@"SOFTWARE\Ubisoft\Launcher\Installs");
            if (installs is null)
                continue;

            foreach (string name in installs.GetSubKeyNames())
            {
                using var game = installs.OpenSubKey(name);
                if (game?.GetValue("InstallDir") is string folder && folder.Length > 0)
                    yield return folder;
            }
        }
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
