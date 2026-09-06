using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Wildlands.Toolkit;

public sealed class AppSettings
{
    public DateTimeOffset? LastUpdateCheckUtc { get; set; }
    public string GamePath { get; set; } = "";
    public string ExportFolder { get; set; } = "";
    public List<string> RecentArchives { get; set; } = [];
    public bool SeenEarlyNotice { get; set; }

    static string FilePath => Path.Combine(Folder, "settings.json");

    // Building this takes some minutes, so it is kept next to the settings
    public static string SkeletonCachePath => Path.Combine(Folder, "skeletons.cache");

    // Kept separately so older Toolkit versions, which only know about the skeleton
    // cache, continue to start normally after an update.
    public static string ArmoryCachePath => Path.Combine(Folder, "armory.cache");
    public static string ModLibraryPath => Path.Combine(Folder, "Mods");
    public bool SeenIndexSetup { get; set; }

    static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WildlandsToolkit");

    public bool IsConfigured => Directory.Exists(GamePath);

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // A broken settings file should not stop the program from starting.
        }

        return new AppSettings();
    }

    public void Save()
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);

        var options = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, options));
    }

    public void AddRecent(string path)
    {
        RecentArchives.Remove(path);
        RecentArchives.Insert(0, path);

        while (RecentArchives.Count > 10)
            RecentArchives.RemoveAt(RecentArchives.Count - 1);
    }
}
