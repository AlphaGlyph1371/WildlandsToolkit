using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public sealed record AssetUsage(string Archive, string Container, string Name, string Type, long Size)
{
    public string SizeText => Size switch
    {
        < 1024 => $"{Size} B",
        < 1024 * 1024 => $"{Size / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{Size / (1024.0 * 1024):0.0} MB",
        _ => $"{Size / (1024.0 * 1024 * 1024):0.00} GB",
    };
}

public partial class AssetUsageWindow : Window
{
    readonly string _archivePath;
    readonly ulong _id;
    readonly CancellationTokenSource _stop = new();
    bool _closed;

    public AssetUsageWindow(string name, ulong id, string archivePath)
    {
        InitializeComponent();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);

        _archivePath = archivePath;
        _id = id;
        Title = $"Asset copies — {name}";
        TitleText.Text = name;
        IdText.Text = $"Exact asset ID: 0x{id:X}";
        Loaded += Scan;
    }

    async void Scan(object sender, RoutedEventArgs e)
    {
        Loaded -= Scan;
        SetLoading(true, "Checking the installed archives…");
        var progress = new Progress<string>(text =>
        {
            if (!_closed)
            {
                LoadingText.Text = text;
                StatusText.Text = text;
            }
        });

        try
        {
            var results = await Task.Run(() => AssetUsageScanner.Find(
                _archivePath, _id, progress, _stop.Token));
            if (_closed)
                return;

            Results.ItemsSource = results;
            EmptyText.Text = results.Count == 0
                ? "This exact resource ID was not found in any readable archive container."
                : "";
            EmptyText.Visibility = results.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusText.Text = results.Count == 1
                ? "Found 1 copy."
                : $"Found {results.Count} copies. Every row is an exact resource-ID match.";
        }
        catch (OperationCanceledException)
        {
            // Closing the result window cancels its own read.
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                EmptyText.Text = $"Could not search the installed archives: {ex.Message}";
                EmptyText.Visibility = Visibility.Visible;
                StatusText.Text = "Search failed.";
            }
        }
        finally
        {
            if (!_closed)
                SetLoading(false);
        }
    }

    void SetLoading(bool loading, string text = "")
    {
        LoadingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (loading)
        {
            LoadingText.Text = text;
            LoadingOverlay.Focus();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _stop.Cancel();
        _stop.Dispose();
        base.OnClosed(e);
    }
}

public static class AssetUsageScanner
{
    sealed record ResourceLocation(string ArchivePath, string Archive, int EntryIndex, string Container);

    sealed record CachedSearch(string Fingerprint, IReadOnlyList<ResourceLocation> Locations,
        int ArchiveCount, int ContainerCount);

    static readonly object IndexLock = new();
    static readonly Dictionary<ulong, CachedSearch> CachedSearches = new();

    public static IReadOnlyList<AssetUsage> Find(string archivePath, ulong id,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var archivePaths = GetArchivePaths(archivePath);
        var fingerprint = GetFingerprint(archivePaths);
        var results = new List<AssetUsage>();
        var search = GetOrScan(archivePaths, fingerprint, id, progress, cancellationToken);
        if (search is null)
        {
            progress?.Report("Search cancelled.");
            return results;
        }

        if (search.Locations.Count == 0)
        {
            progress?.Report($"The exact ID was not present in {search.ContainerCount:N0} readable data containers.");
            return results;
        }

        foreach (var archiveGroup in search.Locations.GroupBy(location => location.ArchivePath,
                     StringComparer.OrdinalIgnoreCase))
        {
            if (cancellationToken.IsCancellationRequested)
                return [];
            using var archive = ForgeArchive.Open(archiveGroup.Key);
            foreach (var location in archiveGroup)
            {
                if (cancellationToken.IsCancellationRequested)
                    return [];
                progress?.Report($"Reading exact match in {location.Archive} · {location.Container}");
                try
                {
                    var entry = archive.Entries.FirstOrDefault(candidate => candidate.Index == location.EntryIndex);
                    if (entry is null)
                        continue;
                    using var stream = new MemoryStream(archive.ReadEntry(entry));
                    DataFile file = DataFile.Read(stream);
                    foreach (var resource in file.Resources.Where(resource => resource.Id == id))
                    {
                        results.Add(new AssetUsage(location.Archive, entry.Name,
                            resource.Name, ResourceTypes.NameOf(resource.ClassHash), resource.Data.Length));
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // A corrupted full payload is excluded rather than reported as a match.
                }
            }
        }

        progress?.Report($"Checked exact matches across {search.ArchiveCount} installed archives.");
        return results
            .OrderBy(result => result.Archive, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Container, StringComparer.OrdinalIgnoreCase)
            .ThenBy(result => result.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static CachedSearch? GetOrScan(IReadOnlyList<string> archivePaths, string fingerprint, ulong id,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        lock (IndexLock)
        {
            if (CachedSearches.TryGetValue(id, out var cached) && cached.Fingerprint == fingerprint)
            {
                progress?.Report($"Using the previous exact scan ({cached.ContainerCount:N0} data containers).");
                return cached;
            }

            // Keep only locations for the requested ID. A full index of every asset can consume
            // hundreds of megabytes on a complete installation, which is the opposite of a safe scan.
            var locations = new List<ResourceLocation>();
            int containers = 0;
            for (var archiveNumber = 0; archiveNumber < archivePaths.Count; archiveNumber++)
            {
                if (cancellationToken.IsCancellationRequested)
                    return null;
                var archivePath = archivePaths[archiveNumber];
                var archiveName = Path.GetFileName(archivePath);
                using var archive = ForgeArchive.Open(archivePath);
                int inArchive = 0;
                foreach (var entry in archive.Entries.Where(entry => entry.FileExtension == ".data"))
                {
                    if (cancellationToken.IsCancellationRequested)
                        return null;
                    inArchive++;
                    containers++;
                    if (inArchive % 1_000 == 1)
                        progress?.Report($"Scanning {archiveName} ({archiveNumber + 1}/{archivePaths.Count}) · {inArchive:N0} data containers…");

                    try
                    {
                        if (archive.ContainsResourceId(entry, id))
                            locations.Add(new ResourceLocation(archivePath, archiveName, entry.Index, entry.Name));
                    }
                    catch
                    {
                        // An unreadable container is not indexed: it can never be reported as a confirmed match.
                    }
                }
            }

            var built = new CachedSearch(fingerprint, locations, archivePaths.Count, containers);
            CachedSearches[id] = built;
            progress?.Report($"Exact scan complete: {containers:N0} data containers in {archivePaths.Count} archives.");
            return built;
        }
    }

    static List<string> GetArchivePaths(string archivePath)
    {
        var fullPath = Path.GetFullPath(archivePath);
        return ArchiveLocator.Siblings(fullPath)
            .Append(fullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static string GetFingerprint(IEnumerable<string> archivePaths) => string.Join("|", archivePaths
        .Select(path =>
        {
            var info = new FileInfo(path);
            return $"{path}:{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        }));
}
