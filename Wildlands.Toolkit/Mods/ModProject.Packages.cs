using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Wildlands.Toolkit;

public sealed partial class ModProject
{
    public void ExportPackage(string outputPath)
    {
        EnsureAttached();
        ValidateManifest();
        ValidatePayloadPaths();
        string temporary = Path.GetFullPath(outputPath) + ".tmp";
        if (File.Exists(temporary))
            File.Delete(temporary);

        try
        {
            ModProject packageProject = PackageCopy();
            using (ZipArchive archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                ZipArchiveEntry manifest = archive.CreateEntry("project.wlproj",
                    CompressionLevel.Optimal);
                using (StreamWriter writer = new(manifest.Open(), new UTF8Encoding(false)))
                    writer.Write(JsonSerializer.Serialize(packageProject, JsonOptions));

                foreach (string payload in PayloadNames(includeUndo: false))
                {
                    ZipArchiveEntry entry = archive.CreateEntry("payload/" + payload,
                        CompressionLevel.Optimal);
                    using Stream destination = entry.Open();
                    using FileStream source = File.OpenRead(PayloadPath(payload));
                    source.CopyTo(destination);
                }
            }

            File.Move(temporary, Path.GetFullPath(outputPath), overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); }
            catch { }
            throw;
        }
    }

    public static ModProject ImportPackage(string packagePath, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        using ZipArchive archive = ZipFile.OpenRead(packagePath);
        ModProject project = ReadPackageManifest(archive);

        string folder = AvailableDirectory(Path.GetFullPath(destinationDirectory), SafeName(project.Id));
        Directory.CreateDirectory(folder);
        string projectPath = Path.Combine(folder, ManifestFileName);
        string assetDirectory = Path.Combine(folder, "assets");
        Directory.CreateDirectory(assetDirectory);

        try
        {
            foreach (string payload in project.PayloadNames())
            {
                ZipArchiveEntry entry = RequiredEntry(archive, "payload/" + payload);
                string destination = Path.GetFullPath(Path.Combine(assetDirectory, payload));
                EnsureBelow(assetDirectory, destination);
                using Stream source = entry.Open();
                using FileStream target = File.Create(destination);
                source.CopyTo(target);
            }

            project.AssetFolder = "assets";
            project.FilePath = projectPath;
            project.LastDeployedHash = null;
            // A package may deliberately accept the payload deployed by its previous
            // version so an update can replace it without requiring a vanilla restore.
            // MarkDeployed replaces this with the newly installed payload hash.
            project.PendingUndo = project.Operations.Select(operation => new ModOperationUndo
            {
                Key = operation.Key,
            }).ToList();
            project.Save();
            return project;
        }
        catch
        {
            try { Directory.Delete(folder, recursive: true); }
            catch { }
            throw;
        }
    }

    public static ModPackageInfo InspectPackage(string packagePath)
    {
        string fullPath = Path.GetFullPath(packagePath);
        using ZipArchive archive = ZipFile.OpenRead(fullPath);
        ModProject project = ReadPackageManifest(archive);
        if (project.Operations.Count == 0)
            throw new InvalidDataException("The mod package does not contain any changes.");

        foreach (string payload in project.PayloadNames(includeUndo: false))
            _ = RequiredEntry(archive, "payload/" + payload);

        return new ModPackageInfo(fullPath, project.Name, project.Author,
            project.Version, project.FormatVersion, new FileInfo(fullPath).Length,
            project.Operations.Select(Clone).ToList());
    }

    static ModProject ReadPackageManifest(ZipArchive archive)
    {
        ZipArchiveEntry manifestEntry = RequiredEntry(archive, "project.wlproj");
        if (manifestEntry.Length > 16 << 20)
            throw new InvalidDataException("The package manifest is unreasonably large.");

        ModProject project;
        using (StreamReader reader = new(manifestEntry.Open(), Encoding.UTF8, true))
            project = JsonSerializer.Deserialize<ModProject>(reader.ReadToEnd(), JsonOptions)
                ?? throw new InvalidDataException("The package manifest is empty.");

        if (project.FormatVersion != CurrentFormatVersion)
            throw new NotSupportedException(
                $"Mod format {project.FormatVersion} is not supported by this Toolkit version.");
        project.ValidateManifest();
        return project;
    }

    static string AvailableDirectory(string root, string name)
    {
        string candidate = Path.Combine(root, name);
        for (int suffix = 2; Directory.Exists(candidate) || File.Exists(candidate); suffix++)
            candidate = Path.Combine(root, $"{name}-{suffix}");
        return candidate;
    }

    static ZipArchiveEntry RequiredEntry(ZipArchive archive, string name)
    {
        List<ZipArchiveEntry> matches = archive.Entries
            .Where(entry => entry.FullName.Equals(name, StringComparison.Ordinal))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new InvalidDataException($"The package is missing {name}."),
            _ => throw new InvalidDataException($"The package contains {name} more than once."),
        };
    }

    ModProject PackageCopy() => new()
    {
        FormatVersion = FormatVersion,
        Id = Id,
        Name = Name,
        Author = Author,
        Version = Version,
        CreatedUtc = CreatedUtc,
        ModifiedUtc = ModifiedUtc,
        LastDeployedHash = LastDeployedHash,
        AssetFolder = AssetFolder,
        Operations = Operations.Select(Clone).ToList(),
        PendingUndo = [],
    };
}
