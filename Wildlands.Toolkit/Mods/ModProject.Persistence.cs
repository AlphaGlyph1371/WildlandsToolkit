using System.IO;
using System.Text;
using System.Text.Json;

namespace Wildlands.Toolkit;

public sealed partial class ModProject
{
    public static ModProject Create(string path, string name, string author, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var project = new ModProject
        {
            Name = name.Trim(),
            Author = author.Trim(),
            Version = version.Trim(),
            FilePath = Path.GetFullPath(path),
        };
        project.Save();
        return project;
    }

    public static ModProject CreateInFolder(string folder, string name, string author, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string fullFolder = Path.GetFullPath(folder);
        Directory.CreateDirectory(fullFolder);
        string manifest = Path.Combine(fullFolder, ManifestFileName);
        if (File.Exists(manifest))
            throw new InvalidOperationException(
                "This folder already contains a mod project. Open that project instead.");
        if (Directory.EnumerateFileSystemEntries(fullFolder).Any())
            throw new InvalidOperationException(
                "A new mod project needs an empty project folder. Create or choose an empty folder.");

        var project = new ModProject
        {
            Name = name.Trim(),
            Author = author.Trim(),
            Version = version.Trim(),
            AssetFolder = "assets",
            FilePath = manifest,
        };
        project.Save();
        return project;
    }

    public static ModProject Load(string path)
    {
        string fullPath = Path.GetFullPath(path);
        ModProject project = JsonSerializer.Deserialize<ModProject>(
                File.ReadAllText(fullPath), JsonOptions)
            ?? throw new InvalidDataException("The project file is empty.");
        if (project.FormatVersion != CurrentFormatVersion)
            throw new NotSupportedException(
                $"Project format {project.FormatVersion} is not supported by this Toolkit version.");
        project.ValidateManifest();

        project.FilePath = fullPath;
        project.ValidatePayloadPaths();
        return project;
    }

    public void Save()
    {
        EnsureAttached();
        ModifiedUtc = DateTimeOffset.UtcNow;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        Directory.CreateDirectory(AssetDirectory);

        string temporary = FilePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions),
            new UTF8Encoding(false));
        File.Move(temporary, FilePath, overwrite: true);
        RemoveUnusedPayloads();
    }
}
