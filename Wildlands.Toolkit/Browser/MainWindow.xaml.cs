using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Models;
using Wildlands.Formats.Materials;
using Wildlands.Formats.Textures;
using Wildlands.Formats.Weather;
using System.Text;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Wildlands.Toolkit;

enum ApplyResult
{
    Applied,
    Cancelled,
    FailedBeforeWrite,
    FailedDuringWrite,
}

public partial class MainWindow : Window
{
    readonly AppSettings _settings = AppSettings.Load();

    readonly List<Location> _history = [];
    int _historyIndex = -1;

    readonly Dictionary<Location, ListPlace> _places = [];
    Location? _showing;
    System.Windows.Controls.ScrollViewer? _itemScroller;

    ForgeArchive? _archive;
    ArchiveSet? _archives;
    readonly ChangeSet _changes = new();
    ChangeListWindow? _changeListWindow;
    ModProject? _project;
    SkeletonIndex? _skeletonIndex;
    ArmoryIndex? _armoryIndex;
    bool _buildingArmoryIndex;
    bool _buildingSkeletonIndex;
    bool _loading;
    bool _applying;
    List<BrowserItem> _shown = [];
    TextureView? _preview;
    readonly List<TextureWindow> _textureWindows = [];
    Mesh? _previewMesh;
    byte[]? _previewMeshData;
    string _previewMeshName = "";
    BrowserItem? _previewMeshItem;
    Location? _previewMeshWhere;

    List<Resource> _previewSiblings = [];

    TextureSet? _previewSet;
    string _previewSetName = "";

    TimeCycle? _previewCycle;
    BrowserItem? _previewCycleItem;
    Location? _previewCycleWhere;

    BuildTableAsset? _previewBuildTable;
    byte[]? _previewBuildTableData;
    BrowserItem? _previewBuildTableItem;
    Location? _previewBuildTableWhere;

    const int MaxPreviewBytes = 8 << 20;

    public MainWindow()
    {
        InitializeComponent();
        Title = VersionText();
        Loaded += OnLoaded;
    }

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_settings.IsConfigured)
        {
            LoadArchiveList();
            LoadIndexes();
        }

        ContentRendered += OnFirstRender;
    }

    void OnFirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstRender;

        try
        {
            ShowEarlyNotice();

            if (_settings.IsConfigured)
            {
                ShowIndexSetupIfNeeded();
                return;
            }

            if (!RunSetup())
            {
                SetStatus("No game folder set. Use \"Game folder\" or \"Open file\".");
                return;
            }

            LoadArchiveList();
            LoadIndexes();
            ShowIndexSetupIfNeeded();
        }
        finally
        {
            BeginUpdateCheck();
        }
    }

    void BeginUpdateCheck()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (_settings.LastUpdateCheckUtc is { } last && last <= now && now - last < TimeSpan.FromHours(1))
            return;

        _settings.LastUpdateCheckUtc = now;
        _settings.Save();
        _ = CheckForUpdateAsync();
    }

    async Task CheckForUpdateAsync()
    {
        try
        {
            ToolkitUpdate? update = await UpdateChecker.CheckAsync();
            if (update is null || !IsLoaded)
                return;

            if (update.IsAvailable)
                new UpdateWindow(update) { Owner = this }.ShowDialog();
        }
        catch
        {
            // Update checks are optional
        }
    }

    void ShowEarlyNotice()
    {
        if (_settings.SeenEarlyNotice)
            return;

        var text = new StringBuilder();
        text.AppendLine("This is early development. Expect bugs, and expect things that do not work yet.");
        text.AppendLine();
        text.AppendLine("Your archives are safe. Before the first change to an archive a copy is made as <archive>.original and never touched again, so you can always go back. Close the game before writing.");
        text.AppendLine();
        text.AppendLine("Please report anything that breaks on the Discord server, the link is in the bottom right corner of the window. A screenshot and what you did before it broke is usually enough.");
        text.AppendLine();
        text.Append("This message is only shown once.");

        MessageBox.Show(this, text.ToString(), $"Welcome to {VersionText()}", MessageBoxButton.OK, MessageBoxImage.Information);

        _settings.SeenEarlyNotice = true;
        _settings.Save();
    }

    bool RunSetup()
    {
        var setup = new SetupWindow(_settings.GamePath) { Owner = IsLoaded ? this : null };
        if (setup.ShowDialog() != true)
            return false;

        _settings.GamePath = setup.GamePath;
        _settings.Save();
        return true;
    }

    void ChangeGameFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_project is not null)
        {
            MessageBox.Show(this, "Close the active mod project before changing the game folder.", "Mod project is active", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_changes.Count > 0)
        {
            MessageBox.Show(this, "Apply or discard the queued changes before changing the game folder.", "Changes are still queued", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (RunSetup())
        {
            LoadArchiveList();
            LoadIndexes();
            ShowIndexSetupIfNeeded();
        }
    }


    void LoadIndexes()
    {
        LoadArmoryIndex();
        LoadSkeletonIndex();
    }

    void LoadArmoryIndex()
    {
        _armoryIndex = null;
        if (!_settings.IsConfigured)
            return;

        string path = AppSettings.ArmoryCachePath;
        if (File.Exists(ArmoryIndex.PartialPath(path)))
        {
            try { File.Delete(ArmoryIndex.PartialPath(path)); } catch { }
        }

        _armoryIndex = ArmoryIndex.Load(path, ArchiveLocator.Find(_settings.GamePath));
        UpdateArmoryIndexButton();
    }

    void LoadSkeletonIndex()
    {
        string path = AppSettings.SkeletonCachePath;

        if (File.Exists(SkeletonIndex.PartialPath(path)))
        {
            try { File.Delete(SkeletonIndex.PartialPath(path)); } catch { }
            _skeletonIndex = null;
            UpdateSkeletonIndexButton();
            return;
        }

        if (File.Exists(path))
        {
            _skeletonIndex = SkeletonIndex.Load(path);
            UpdateSkeletonIndexButton();
            return;
        }

        _skeletonIndex = null;
        UpdateSkeletonIndexButton();
    }

    void ShowIndexSetupIfNeeded()
    {
        if (!_settings.IsConfigured || _settings.SeenIndexSetup)
            return;

        ShowIndexSetup();
    }

    void Indexes_Click(object sender, RoutedEventArgs e) => ShowIndexSetup();

    void ShowIndexSetup()
    {
        if (!_settings.IsConfigured)
            return;

        var setup = new IndexSetupWindow(_armoryIndex is not null, _skeletonIndex is not null) { Owner = this };
        bool prepare = setup.ShowDialog() == true;
        _settings.SeenIndexSetup = true;
        _settings.Save();

        if (prepare)
            _ = PrepareIndexesAsync();
    }

    async Task PrepareIndexesAsync()
    {
        if (_armoryIndex is null && !await BuildArmoryIndexAsync())
            return;
        if (_skeletonIndex is null)
            await BuildSkeletonIndexAsync();
    }

    void ArmoryIndex_Click(object sender, RoutedEventArgs e) => _ = BuildArmoryIndexAsync();
    void SkeletonIndex_Click(object sender, RoutedEventArgs e) => _ = BuildSkeletonIndexAsync();

    async Task<bool> BuildArmoryIndexAsync(Action? completed = null)
    {
        if (_buildingArmoryIndex || !_settings.IsConfigured)
            return _armoryIndex is not null;

        _buildingArmoryIndex = true;
        ScanText.Text = "Preparing Armory index";
        ScanText.Visibility = Visibility.Visible;
        UpdateArmoryIndexButton();

        try
        {
            var progress = new Progress<string>(message => ScanText.Text = message);
            string folder = _settings.GamePath;
            var index = await Task.Run(() => ArmoryIndex.Build(ArchiveLocator.Find(folder), progress));
            index.Save(AppSettings.ArmoryCachePath);
            _armoryIndex = index;
            SetStatus($"Armory index ready: {index.DatabaseResourceCount:N0} game records.");
            completed?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Could not prepare the Armory index", ex);
            return false;
        }
        finally
        {
            _buildingArmoryIndex = false;
            ScanText.Visibility = Visibility.Collapsed;
            UpdateArmoryIndexButton();
        }
    }

    async Task<bool> BuildSkeletonIndexAsync()
    {
        if (_buildingSkeletonIndex || !_settings.IsConfigured)
            return _skeletonIndex is not null;

        _buildingSkeletonIndex = true;
        ScanText.Text = "Looking for skeletons";
        ScanText.Visibility = Visibility.Visible;
        UpdateSkeletonIndexButton();

        try
        {
            string folder = _settings.GamePath;
            var index = await Task.Run(() =>
            {
                var archives = ArchiveLocator.Find(folder).Select(ForgeArchive.Open).ToList();
                int done = 0;

                try
                {
                    return SkeletonIndex.Build(archives, _ => Dispatcher.Invoke(() => ScanText.Text = $"Looking for skeletons, {++done} of {archives.Count} archives"));
                }
                finally
                {
                    foreach (var archive in archives)
                        archive.Dispose();
                }
            });

            index.Save(AppSettings.SkeletonCachePath);
            _skeletonIndex = index;
            SetStatus($"Found {index.SkeletonCount} skeletons.");
            return true;
        }
        catch (Exception ex)
        {
            ShowError("Could not scan for skeletons", ex);
            return false;
        }
        finally
        {
            _buildingSkeletonIndex = false;
            ScanText.Visibility = Visibility.Collapsed;
            UpdateSkeletonIndexButton();
        }
    }

    void UpdateSkeletonIndexButton()
    {
        SkeletonIndexButton.IsEnabled = !_buildingSkeletonIndex;
        SkeletonIndexButton.Content = _buildingSkeletonIndex ? "Building…" : _skeletonIndex is null ? "Build SK index" : "Rebuild SK index";
    }

    void LoadArchiveList()
    {
        if (!_settings.IsConfigured)
        {
            ArchiveList.ItemsSource = null;
            return;
        }

        var found = ArchiveLocator.Find(_settings.GamePath);
        var archives = found
            .Select(path => new ArchiveItem(path))
            .OrderBy(a => a.Name)
            .ToList();

        ArchiveList.ItemsSource = archives;

        int inDlc = found.Count(path => Path.GetFileName(Path.GetDirectoryName(path) ?? "")
            .StartsWith("dlc_", StringComparison.OrdinalIgnoreCase));

        SetStatus($"{archives.Count} archives in {_settings.GamePath}" + (inDlc > 0 ? $", {inDlc} of them in dlc folders" : ""));
    }

    async void ArchiveList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ArchiveList.SelectedItem is ArchiveItem archive)
            await Navigate(new Location(archive.Path));
    }

    async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a forge archive",
            Filter = "Forge archives (*.forge)|*.forge|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
            await Navigate(new Location(dialog.FileName));
    }

    async Task Navigate(Location location)
    {
        if (!await Go(location))
            return;

        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

        _history.Add(location);
        _historyIndex = _history.Count - 1;
        UpdateNavigationButtons();
    }

    async Task<bool> Go(Location location)
    {
        if (_loading)
            return false;

        RememberPlace();
        var watch = Stopwatch.StartNew();
        SetLoading(true, location.IsArchiveRoot
            ? $"Opening {Path.GetFileName(location.ArchivePath)}…"
            : $"Loading {location.EntryName}…");

        try
        {
            if (_archive is null || _archive.FilePath != location.ArchivePath)
            {
                var oldArchives = _archives;
                var oldArchive = _archive;
                _archives = null;
                _archive = null;

                _archive = await Task.Run(() =>
                {
                    oldArchives?.Dispose();
                    oldArchive?.Dispose();
                    return ForgeArchive.Open(location.ArchivePath);
                });
                _archives = new ArchiveSet(_archive);
                _settings.AddRecent(location.ArchivePath);
                _settings.Save();
            }

            if (location.IsArchiveRoot)
            {
                _shown = _archive.Entries.Select(BrowserItem.FromEntry).ToList();
                watch.Stop();
                SetStatus($"{_shown.Count} entries loaded in {Elapsed(watch)}");
            }
            else
            {
                ForgeEntry? entry = _archive.Entries.FirstOrDefault(candidate =>
                    candidate.Index == location.EntryIndex
                    && (location.EntryName.Length == 0 || candidate.Name == location.EntryName));
                if (entry is null && location.EntryName.Length > 0)
                {
                    List<ForgeEntry> named = _archive.Entries
                        .Where(candidate => candidate.Name == location.EntryName)
                        .Take(2)
                        .ToList();
                    if (named.Count == 1)
                        entry = named[0];
                }
                if (entry is null)
                    throw new InvalidDataException($"{location.EntryName} is no longer present in the archive.");

                location = new Location(location.ArchivePath, entry.Index, entry.Name);
                _shown = await Task.Run(() =>
                {
                    using var stream = new MemoryStream(_archive.ReadEntry(entry));
                    var file = DataFile.Read(stream);
                    return file.Resources.Select(BrowserItem.FromResource).ToList();
                });
                watch.Stop();
                SetStatus($"{_shown.Count} resources loaded in {Elapsed(watch)}");
            }

            PathText.Text = location.Display;
            _showing = location;
            ApplyFilter();
            RestorePlace(location);
            return true;
        }
        catch (Exception ex)
        {
            if (_archive is null)
            {
                _shown = [];
                _showing = null;
                PathText.Text = "No archive open";
                ApplyFilter();
            }

            ShowError($"Could not open {location.Display}", ex);
            return false;
        }
        finally
        {
            SetLoading(false);
        }
    }

    void UpdateArmoryIndexButton()
    {
        ArmoryIndexButton.IsEnabled = !_buildingArmoryIndex;
        ArmoryIndexButton.Content = _buildingArmoryIndex ? "Building…" : _armoryIndex is null ? "Build BT index" : "Rebuild BT index";
    }

    static string Elapsed(Stopwatch watch) => watch.Elapsed.TotalSeconds >= 1
        ? $"{watch.Elapsed.TotalSeconds:0.0} s"
        : $"{watch.ElapsedMilliseconds} ms";

    void SetLoading(bool loading, string status = "")
    {
        _loading = loading;
        ArchiveLoadProgress.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ArchiveLoadingOverlay.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        ArchiveLoadingText.Text = status;
        FilterBox.IsEnabled = !loading;
        Mouse.OverrideCursor = loading || _applying ? Cursors.Wait : null;

        if (loading)
        {
            ArchiveLoadingOverlay.Focus();
            ChangeListButton.IsEnabled = false;
            _changeListWindow?.SetInteractionEnabled(false);
            SetStatus(status);
        }
        else
        {
            _changeListWindow?.SetInteractionEnabled(true);
            UpdateChangeButtons();
        }

        UpdateNavigationButtons();
        UpdateAssetActions();
    }

    void RememberPlace()
    {
        if (_showing is null)
            return;

        _places[_showing] = new ListPlace
        {
            SelectedIndex = (ItemList.SelectedItem as BrowserItem)?.Index ?? -1,
            ScrollOffset = ItemScroller?.VerticalOffset ?? 0,
        };
    }

    void RestorePlace(Location location)
    {
        if (!_places.TryGetValue(location, out var place))
            return;

        if (ItemList.Items.OfType<BrowserItem>().FirstOrDefault(i => i.Index == place.SelectedIndex) is { } item)
            ItemList.SelectedItem = item;

        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (ItemScroller is { } scroller)
                scroller.ScrollToVerticalOffset(place.ScrollOffset);
            else if (ItemList.SelectedItem is not null)
                ItemList.ScrollIntoView(ItemList.SelectedItem);
        }));
    }

    System.Windows.Controls.ScrollViewer? ItemScroller => _itemScroller ??= FindScroller(ItemList);

    static System.Windows.Controls.ScrollViewer? FindScroller(DependencyObject root)
    {
        if (root is System.Windows.Controls.ScrollViewer scroller)
            return scroller;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScroller(VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        }

        return null;
    }

    async void ItemList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemList.SelectedItem is not BrowserItem item)
            return;

        if (item.Resource is not null)
        {
            OpenViewer();
            return;
        }

        if (item.Entry is not null && item.CanOpen)
            await Navigate(new Location(_archive!.FilePath, item.Entry.Index, item.Name));
    }

    async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex <= 0)
            return;

        int next = _historyIndex - 1;
        if (await Go(_history[next]))
            _historyIndex = next;
        UpdateNavigationButtons();
    }

    async void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex >= _history.Count - 1)
            return;

        int next = _historyIndex + 1;
        if (await Go(_history[next]))
            _historyIndex = next;
        UpdateNavigationButtons();
    }

    async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex < 0 || _history[_historyIndex].IsArchiveRoot)
            return;

        await Navigate(_history[_historyIndex].Parent);
    }

    void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = !_loading && _historyIndex > 0;
        ForwardButton.IsEnabled = !_loading && _historyIndex < _history.Count - 1;
        UpButton.IsEnabled = !_loading && _historyIndex >= 0 && !_history[_historyIndex].IsArchiveRoot;
    }

    void Extract_Click(object sender, RoutedEventArgs e)
    {
        var selected = ItemList.SelectedItems.Cast<BrowserItem>().ToList();
        if (selected.Count == 0)
            return;

        if (selected.Count == 1)
        {
            SaveRaw(selected[0]);
            return;
        }

        var dialog = new OpenFolderDialog { Title = "Choose a folder to extract into" };
        if (dialog.ShowDialog() != true)
            return;

        int written = 0;
        try
        {
            foreach (var item in selected)
            {
                var bytes = item.Resource?.Data ?? (item.Entry is not null ? _archive?.ReadEntry(item.Entry) : null);
                if (bytes is null)
                    continue;

                string suffix = RawSuffix(item);
                string name = $"{item.Index}_-_{Path.GetFileNameWithoutExtension(item.Name)}{suffix}";
                File.WriteAllBytes(Path.Combine(dialog.FolderName, name), bytes);
                written++;
            }
        }
        catch (Exception ex)
        {
            ShowError($"Could not extract, {written} file(s) were written", ex);
            return;
        }

        SetStatus($"Extracted {written} file(s)");
    }

    void ItemList_RightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = RowUnder(e.OriginalSource as DependencyObject);

        if (row is not null && !row.IsSelected)
        {
            ItemList.SelectedItems.Clear();
            row.IsSelected = true;
        }
    }

    static System.Windows.Controls.ListViewItem? RowUnder(DependencyObject? source)
    {
        while (source is not null and not System.Windows.Controls.ListViewItem)
            source = VisualTreeHelper.GetParent(source);

        return source as System.Windows.Controls.ListViewItem;
    }

    void Menu_Opened(object sender, RoutedEventArgs e)
    {
        UpdateAssetActions();
        var item = ItemList.SelectedItem as BrowserItem;
        int count = ItemList.SelectedItems.Count;

        bool stepsIn = item?.Entry is not null && item.CanOpen;
        MenuOpen.Header = stepsIn ? "Open"
            : _previewCycle is not null ? "Open in time cycle editor"
            : _previewBuildTable is not null ? "Open in BuildTable editor"
            : _previewMesh is not null ? "Open in mesh viewer"
            : "Open in texture viewer";
        MenuOpen.IsEnabled = stepsIn || (item is not null && (_preview is not null
            || _previewMesh is not null || _previewCycle is not null || _previewBuildTable is not null));

        MenuExtract.IsEnabled = count > 0;
        MenuExtract.Header = count > 1 ? $"Extract {count} items..." : "Extract...";

        MenuExport.IsEnabled = _preview is not null || _previewMesh is not null || _previewSet is not null;

        MenuReplace.IsEnabled = item?.Resource is not null || _preview is not null;
        MenuReplace.Header = item?.Resource?.ClassHash == Mesh.ClassHash ? "Replace geometry..." : "Replace...";
        MenuReplaceRaw.IsEnabled = item?.Resource is not null;

        MenuFindCopies.IsEnabled = item is { Id: not 0 } && _showing is not null;
        MenuCopyName.IsEnabled = item is not null;
        MenuCopyId.IsEnabled = item is { Id: not 0 };
    }

    void FindCopies_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is not BrowserItem { Id: not 0 } item || _showing is null)
            return;

        new AssetUsageWindow(item.Name, item.Id, _showing.ArchivePath).Show();
    }

    static string VersionText()
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        return version is null ? "Wildlands Toolkit" : $"Wildlands Toolkit {version.Major}.{version.Minor}.{version.Build}";
    }

    void Discord_Navigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowError("Could not open the link", ex);
        }

        e.Handled = true;
    }


   async void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is not BrowserItem item)
            return;

        if (item.Entry is not null && item.CanOpen)
            await Navigate(new Location(_archive!.FilePath, item.Entry.Index, item.Name));
        else
            OpenViewer();
    }

    void MenuExport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Export();
        }
        catch (Exception ex)
        {
            ShowError("Could not export", ex);
        }
    }

    void Export()
    {
        if (_preview is not null)
        {
            int level = PreviewLevelBox.SelectedItem is TextureMipLevel selected ? selected.Level : _preview.FocusLevel;

            var dialog = new ExportWindow(_preview, level, _settings) { Owner = this };

            if (dialog.ShowDialog() == true)
                SetStatus(dialog.Summary);
        }
        else if (_previewMesh is not null)
        {
            string done = MeshExporter.Save(this, _previewMesh, _previewMeshName, _previewSiblings,
                _skeletonIndex, _settings);

            if (done.Length > 0)
                SetStatus(done);
        }
        else if (_previewSet is not null)
        {
            ExportSetTextures(_previewSet);
        }
    }

    void ExportSetTextures(TextureSet set)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder for the textures of this set" };

        if (_settings.ExportFolder.Length > 0 && Directory.Exists(_settings.ExportFolder))
            dialog.InitialDirectory = _settings.ExportFolder;

        if (dialog.ShowDialog() != true)
            return;

        Mouse.OverrideCursor = Cursors.Wait;
        int written;

        try
        {
            written = WriteSetTextures(set, _archives, dialog.FolderName);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _settings.ExportFolder = dialog.FolderName;
        _settings.Save();

        SetStatus($"Wrote {written} texture(s) of {_previewSetName}");
    }

    static int WriteSetTextures(TextureSet set, ArchiveSet? archives, string folder)
    {
        int written = 0;

        foreach (var slot in set.Textures)
        {
            var found = archives?.FindResource(slot.Id, TextureMap.ClassHash);
            if (found is null)
                continue;

            var view = TextureLoader.FromTexture(found.Resource.Name,
                TextureMap.Read(found.Resource.Data), archives);

            if (view.Focus is null)
                continue;

            TextureExporter.Export(view, [view.Focus], ExportFormat.Png, folder, found.Resource.Name);
            written++;
        }

        return written;
    }

    static string RawSuffix(BrowserItem item) => item.Entry?.FileExtension ?? "." + item.Type.ToLowerInvariant();

    void SaveRaw(BrowserItem item)
    {
        var bytes = item.Resource?.Data ?? (item.Entry is not null ? _archive?.ReadEntry(item.Entry) : null);
        if (bytes is null)
            return;

        string suffix = RawSuffix(item);

        var dialog = new SaveFileDialog
        {
            Title = "Extract as",
            FileName = item.Name + suffix,
            Filter = $"{item.Type} file (*{suffix})|*{suffix}|All files (*.*)|*.*",
        };

        if (_settings.ExportFolder.Length > 0 && Directory.Exists(_settings.ExportFolder))
            dialog.InitialDirectory = _settings.ExportFolder;

        if (dialog.ShowDialog() != true)
            return;

        File.WriteAllBytes(dialog.FileName, bytes);

        _settings.ExportFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
        _settings.Save();

        SetStatus($"Wrote {Path.GetFileName(dialog.FileName)}");
    }

    void MenuCopyName_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is BrowserItem item)
            Clipboard.SetText(item.Name);
    }

    void MenuCopyId_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is BrowserItem item)
            Clipboard.SetText($"0x{item.Id:X}");
    }

    void Filter_Changed(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        FilterHint.Visibility = FilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    void ItemList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        UpdateAssetActions();
        ShowPreview(ItemList.SelectedItem as BrowserItem);
    }

    void ShowPreview(BrowserItem? item)
    {
        _preview = null;
        _previewMesh = null;
        _previewMeshData = null;
        _previewMeshItem = null;
        _previewMeshWhere = null;
        _previewSet = null;
        _previewCycle = null;
        _previewBuildTable = null;
        _previewBuildTableData = null;
        _previewBuildTableItem = null;
        _previewBuildTableWhere = null;
        PreviewLevelBox.ItemsSource = null;
        PreviewImage.Source = null;
        SetStrip.ItemsSource = null;
        SetStrip.Visibility = Visibility.Collapsed;
        ImageFrame.Visibility = Visibility.Collapsed;
        LevelRow.Visibility = Visibility.Collapsed;
        SourceRow.Visibility = Visibility.Collapsed;
        OpenViewerButton.Visibility = Visibility.Collapsed;
        OpenMeshButton.Visibility = Visibility.Collapsed;
        OpenCycleButton.Visibility = Visibility.Collapsed;
        OpenBuildTableButton.Visibility = Visibility.Collapsed;

        if (item is null)
        {
            PreviewTitle.Text = "Nothing selected";
            PreviewInfo.Text = "";
            return;
        }

        PreviewTitle.Text = item.Name;

        var lines = new List<string>
        {
            $"type   {item.Type}",
            $"size   {item.Size:N0} bytes",
        };

        if (item.Id != 0)
            lines.Add($"id     0x{item.Id:X}");

        string note = "";
        TextureView? view = null;
        Mesh? mesh = null;

        if (item.Resource is not null && TimeCycle.IsTimeCycle(item.Resource.ClassHash))
        {
            ShowCycle(item, lines);
            PreviewInfo.Text = string.Join(Environment.NewLine, lines);
            return;
        }

        if (item.Resource?.ClassHash == BuildTable.ClassHash)
        {
            ShowBuildTable(item, lines);
            PreviewInfo.Text = string.Join(Environment.NewLine, lines);
            return;
        }

        var material = ResolveMaterial(item);

        if (material is not null)
            DescribeMaterial(material, lines);

        var set = ResolveTextureSet(item);

        if (set is not null)
        {
            DescribeSet(set, lines);
        }
        else
        {
            view = ResolveTexture(item, out note);

            if (view is null && note.Length == 0)
                mesh = ResolveMesh(item, out note);
        }

        if (view is not null)
        {
            ShowTexture(view, lines);
        }
        else if (mesh is not null)
        {
            ShowMesh(mesh, item.Name, lines);
        }
        else
        {
            if (note.Length > 0)
                lines.Add($"       {note}");

            if (item.Resource is not null && set is null && material is null)
                AddHexDump(EffectiveData(item), lines);
        }

        PreviewInfo.Text = string.Join(Environment.NewLine, lines);
    }

    TextureView? ResolveTexture(BrowserItem item, out string note)
    {
        note = "";
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            if (item.Resource is not null)
                return FromResource(item.Name, item.Resource, EffectiveData(item), out note);

            if (item.Entry is not null && item.Entry.FileExtension == ".data"
                && item.Entry.Length <= MaxPreviewBytes)
                return FromEntry(item.Entry, out note);
        }
        catch (Exception ex)
        {
            note = $"texture unreadable: {ex.Message}";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        return null;
    }

    TextureView? FromResource(string name, Resource resource, byte[] data, out string note)
    {
        note = "";

        if (resource.ClassHash == TextureMap.ClassHash)
            return TextureLoader.FromTexture(name, TextureMap.Read(data), _archives, PendingCompiledMips());

        if (resource.ClassHash == CompiledMip.ClassHash)
            return TextureLoader.FromMip(name, CompiledMip.Read(data), _archives, out note);

        return null;
    }

    TextureView? FromEntry(ForgeEntry entry, out string note)
    {
        note = "";

        using var stream = new MemoryStream(_archive!.ReadEntry(entry));
        var file = DataFile.Read(stream);

        var resource = file.Resources.FirstOrDefault(r => r.ClassHash == TextureMap.ClassHash) ?? file.Resources.FirstOrDefault(r => r.ClassHash == CompiledMip.ClassHash);

        return resource is null ? null : FromResource(resource.Name, resource, resource.Data, out note);
    }

    Material? ResolveMaterial(BrowserItem item)
    {
        if (item.Resource is null || item.Resource.ClassHash != Material.ClassHash)
            return null;

        try
        {
            return Material.Read(EffectiveData(item));
        }
        catch
        {
            return null;
        }
    }

    void DescribeMaterial(Material material, List<string> lines)
    {
        lines.Add("");
        lines.Add($"blend  {material.BlendMode}{(material.IsOpaque ? "  (opaque)" : "")}" + (material.Flags.TwoSided ? "  two-sided" : ""));

        if (material.Note.Length > 0)
            lines.Add($"note   {material.Note}");

        lines.Add($"{material.Parameters.Count} parameters");

        var tiles = new List<SetTexture>();
        var textureSets = new Dictionary<ulong, TextureSet?>();
        TextureSet? defaultTextureSet = ResolveTextureSet(material.TextureSetId, textureSets);
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            foreach (var parameter in material.Parameters)
            {
                string name = ParameterNames.NameOf(parameter.Name);
                if (name.StartsWith("0x") && parameter.TextureId != 0)
                {
                    TextureSet? selectedSet = parameter.TextureSetId != 0
                        ? ResolveTextureSet(parameter.TextureSetId, textureSets)
                        : defaultTextureSet;
                    name = selectedSet?.Textures.FirstOrDefault(slot => slot.Id == parameter.TextureId)?.Name
                        ?? defaultTextureSet?.Textures.FirstOrDefault(slot => slot.Id == parameter.TextureId)?.Name
                        ?? name;
                }
                string description = Describe(parameter, ref name, tiles);
                lines.Add($"  {name,-20} {description}");
            }
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (tiles.Count == 0)
            return;

        SetStrip.ItemsSource = tiles;
        SetStrip.Visibility = Visibility.Visible;
    }

    TextureSet? ResolveTextureSet(ulong id, Dictionary<ulong, TextureSet?> cache)
    {
        if (id == 0)
            return null;
        if (cache.TryGetValue(id, out TextureSet? cached))
            return cached;

        try
        {
            var found = _archives?.FindResource(id, TextureSet.ClassHash);
            TextureSet? set = found is null ? null : TextureSet.Read(found.Resource.Data);
            cache[id] = set;
            return set;
        }
        catch
        {
            cache[id] = null;
            return null;
        }
    }

    string Describe(MaterialParameter parameter, ref string name, List<SetTexture> tiles)
    {
        if (parameter.ObjectClass == UvTransform)
            return $"UVTransform  tiles {parameter.ScaleU:0.##} x {parameter.ScaleV:0.##}";

        if (parameter.TextureId == 0)
            return parameter.ObjectClass != 0
                ? ResourceTypes.NameOf(parameter.ObjectClass)
                : $"kind 0x{parameter.Kind:X2}";

        var found = _archives?.FindResource(parameter.TextureId, TextureMap.ClassHash);
        if (found is null)
            return $"0x{parameter.TextureId:X}  (not in these archives)";

        var texture = TextureMap.Read(found.Resource.Data);
        var thumbnail = Thumbnail(texture, out int level);
        if (name.StartsWith("0x"))
            name = found.Resource.Name;

        tiles.Add(new SetTexture
        {
            Slot = name,
            Name = found.Resource.Name,
            Size = $"{texture.Width} x {texture.Height}",
            From = $"from mip {level}",
            Id = parameter.TextureId,
            Thumbnail = thumbnail,
        });

        return found.Resource.Name;
    }

    const uint UvTransform = 0xC52E2125;

    TextureSet? ResolveTextureSet(BrowserItem item)
    {
        if (item.Resource is null || item.Resource.ClassHash != TextureSet.ClassHash)
            return null;

        try
        {
            return TextureSet.Read(EffectiveData(item));
        }
        catch
        {
            return null;
        }
    }

    void DescribeSet(TextureSet set, List<string> lines)
    {
        _previewSet = set;
        _previewSetName = PreviewTitle.Text;

        lines.Add("");
        Mouse.OverrideCursor = Cursors.Wait;

        var tiles = new List<SetTexture>();

        try
        {
            foreach (var slot in set.Textures)
            {
                var found = _archives?.FindResource(slot.Id, TextureMap.ClassHash);

                if (found is null)
                {
                    lines.Add($"{slot.Name,-10} 0x{slot.Id:X}  (not in these archives)");
                    continue;
                }

                lines.Add($"{slot.Name,-10} {found.Resource.Name}");

                var texture = TextureMap.Read(found.Resource.Data);
                var tile = Thumbnail(texture, out int level);

                tiles.Add(new SetTexture
                {
                    Slot = slot.Name,
                    Name = found.Resource.Name,
                    Size = $"{texture.Width} x {texture.Height}",
                    From = $"from mip {level}",
                    Id = slot.Id,
                    Thumbnail = tile,
                });
            }
        }
        catch (Exception ex)
        {
            lines.Add($"       texture set unreadable: {ex.Message}");
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (tiles.Count == 0)
        {
            lines.Add("       this set points at no textures at all");
            return;
        }

        SetStrip.ItemsSource = tiles;
        SetStrip.Visibility = Visibility.Visible;
    }

    static BitmapSource? Thumbnail(TextureMap texture, out int shown)
    {
        shown = 0;

        var mips = TextureMipSet.Collect(texture, null);
        if (mips.Levels.Count == 0)
            return null;

        var pick = mips.Levels[0];
        foreach (var level in mips.Levels)
        {
            if (level.Width >= 96)
                pick = level;
        }

        shown = pick.Level;

        var decoded = TextureLoader.Decode(texture, pick, out _);

        return decoded is null ? null : TextureLoader.WithoutAlpha(decoded);
    }

    void SetTexture_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not SetTexture tile)
            return;

        var found = _archives?.FindResource(tile.Id, TextureMap.ClassHash);
        if (found is null)
            return;

        var view = TextureLoader.FromTexture(found.Resource.Name, TextureMap.Read(found.Resource.Data), _archives);

        TextureWindow? window = null;
        window = new TextureWindow(view, _settings, () =>
        {
            if (ReplaceTexture(view) is { } fresh)
                window!.ShowView(fresh);
        });
        window.Closed += (_, _) => _textureWindows.Remove(window);
        _textureWindows.Add(window);
        window.Show();
    }

    Mesh? ResolveMesh(BrowserItem item, out string note)
    {
        note = "";
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            if (item.Resource is not null)
            {
                if (item.Resource.ClassHash != Mesh.ClassHash)
                    return null;

                _previewSiblings = _shown.Select(i => i.Resource).OfType<Resource>().ToList();
                byte[] data = _showing is null
                    ? item.Resource.Data
                    : _changes.Find(_showing.ArchivePath, _showing.EntryIndex, item.Index)?.Data
                        ?? item.Resource.Data;
                _previewMeshData = data;
                _previewMeshItem = item;
                _previewMeshWhere = _showing;
                return Mesh.Read(data);
            }

            if (item.Entry is not null && item.Entry.FileExtension == ".data" && item.Entry.Length <= MaxPreviewBytes)
            {
                using var stream = new MemoryStream(_archive!.ReadEntry(item.Entry));
                var file = DataFile.Read(stream);
                var resource = file.Resources.FirstOrDefault(r => r.ClassHash == Mesh.ClassHash);

                if (resource is null)
                    return null;

                _previewSiblings = file.Resources;
                _previewMeshData = resource.Data;
                return Mesh.Read(resource.Data);
            }
        }
        catch (Exception ex)
        {
            note = $"mesh unreadable: {ex.Message}";
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        return null;
    }

    void ShowCycle(BrowserItem item, List<string> lines)
    {
        try
        {
            _previewCycle = TimeCycle.Read(EffectiveData(item));
        }
        catch (Exception ex)
        {
            lines.Add("");
            lines.Add($"       time cycle unreadable: {ex.Message}");
            return;
        }

        _previewCycleItem = item;
        _previewCycleWhere = _showing;

        var varying = _previewCycle.Entries
            .Where(e => e.Values.Any(v => !v.SequenceEqual(e.Values[0])))
            .ToList();

        lines.Add("");
        lines.Add($"entries   {_previewCycle.Entries.Count}");

        if (varying.Count > 0)
        {
            lines.Add("");
            foreach (var entry in varying.Take(12))
                lines.Add($"  {entry.Values.Count,3} keys  {entry.PathText()}");

            if (varying.Count > 12)
                lines.Add($"  ... and {varying.Count - 12} more");
        }

        OpenCycleButton.Visibility = Visibility.Visible;
    }

    void ShowBuildTable(BrowserItem item, List<string> lines)
    {
        if (item.Resource is null || _showing is null)
            return;

        byte[] data = _changes.Find(_showing.ArchivePath, _showing.EntryIndex, item.Index)?.Data ?? item.Resource.Data;

        try
        {
            _previewBuildTable = BuildTable.Read(data);
            _previewBuildTableData = data;
            _previewBuildTableItem = item;
            _previewBuildTableWhere = _showing;
        }
        catch (Exception ex)
        {
            lines.Add("");
            lines.Add($"       BuildTable unreadable: {ex.Message}");
            return;
        }

        lines.Add("");
        lines.Add($"columns     {_previewBuildTable.ColumnCount}");
        lines.Add($"rows        {_previewBuildTable.RowCount}");
        lines.Add($"references  {_previewBuildTable.References.Count - 1}");

        foreach (var group in _previewBuildTable.References
            .Where(reference => reference.Kind != BuildTableReferenceKind.TableIdentity)
            .GroupBy(reference => reference.Kind))
            lines.Add($"  {group.Key,-13} {group.Count(),4}");

        if (_changes.Contains(_showing.ArchivePath, _showing.EntryIndex, item.Index))
            lines.Add("       pending edit");

        OpenBuildTableButton.Visibility = Visibility.Visible;
    }

    void ShowMesh(Mesh mesh, string name, List<string> lines)
    {
        _previewMesh = mesh;
        _previewMeshName = name;

        lines.Add("");
        lines.Add($"geometry  {mesh.Geometry}");
        lines.Add($"submeshes {mesh.SubMeshIds.Count}");

        if (mesh.Bones.Count > 0)
            lines.Add($"bones     {mesh.Bones.Count}");

        lines.Add($"size      {mesh.ExtentMax[0] - mesh.ExtentMin[0]:0.00} x " +
                  $"{mesh.ExtentMax[1] - mesh.ExtentMin[1]:0.00} x " +
                  $"{mesh.ExtentMax[2] - mesh.ExtentMin[2]:0.00} m");

        int triangles = mesh.Data.Standard.Sum(range => range.TriangleCount);
        int vertices = mesh.VertexStride > 0 ? mesh.VertexBuffer.Length / mesh.VertexStride : 0;

        lines.Add($"vertices  {vertices:N0}");
        lines.Add($"triangles {triangles:N0}");
        lines.Add($"format    {mesh.VertexFormat}, stride {mesh.VertexStride}");
        lines.Add($"ranges    {mesh.Data.Standard.Count} standard, {mesh.Data.Shadow.Count} shadow");

        if (triangles > 0)
            OpenMeshButton.Visibility = Visibility.Visible;
    }

    void ShowTexture(TextureView view, List<string> lines)
    {
        _preview = view;
        var texture = view.Texture;

        lines.Add("");
        lines.Add($"size   {texture.Width} x {texture.Height}");
        lines.Add($"format {texture.Format}");
        lines.Add($"mips   {texture.MipCount}");

        if (view.FromCompiledMip)
            lines.Add($"parent 0x{texture.Id:X}");

        int streamed = view.Levels.Count(l => l.IsStreamed);
        if (streamed > 0)
            lines.Add($"       {streamed} of them from separate CompiledMip files");

        if (view.Mips.MissingStreamed.Count > 0)
            lines.Add($"       {view.Mips.MissingStreamed.Count} streamed mip(s) not found in any archive");

        lines.Add("");
        
        foreach (var level in view.Levels)
            lines.Add($"mip {level.Level,-2} {level.Size,-13} {(level.IsStreamed ? "CompiledMip" : "TextureMap")}");

        if (!view.CanDraw || view.Levels.Count == 0)
        {
            lines.Add("");
            lines.Add($"       {TextureLoader.ExplainEmpty(view)}");
            return;
        }

        OpenViewerButton.Visibility = Visibility.Visible;
        LevelRow.Visibility = view.Levels.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        PreviewLevelBox.ItemsSource = view.Levels;
        PreviewLevelBox.SelectedItem = view.Focus;
    }

    void PreviewLevel_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_preview is null || PreviewLevelBox.SelectedItem is not TextureMipLevel level)
            return;

        var image = TextureLoader.Decode(_preview.Texture, level, out string note);

        PreviewImage.Source = image;
        ImageFrame.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        SourceRow.Visibility = Visibility.Visible;

        if (image is null)
        {
            SetSourceTag("no image", streamed: false);
            SourceText.Text = note;
            return;
        }

        if (level.IsStreamed)
        {
            SetSourceTag("CompiledMip", streamed: true);
            SourceText.Text = $"level {level.Level} ({level.Size}) is streamed separately" + Environment.NewLine + $"loaded from {level.SourceName}";
        }
        else
        {
            SetSourceTag("TextureMap", streamed: false);
            SourceText.Text = $"level {level.Level} ({level.Size}) from the embedded chain of this resource";
        }

        if (TextureLoader.IsInvisibleWithAlpha(image))
            SourceText.Text += Environment.NewLine + "every pixel is transparent - open the viewer and turn alpha off to see it";
    }

    void SetSourceTag(string text, bool streamed)
    {
        SourceTagText.Text = text;
        SourceTag.Background = (Brush)FindResource(streamed ? "Accent" : "Border");
        SourceTagText.Foreground = streamed ? Brushes.Black : (Brush)FindResource("Text");
    }

    void OpenViewer_Click(object sender, RoutedEventArgs e) => OpenViewer();

    void OpenViewer()
    {
        if (_previewBuildTable is not null)
        {
            OpenBuildTableEditor();
            return;
        }

        if (_previewCycle is not null)
        {
            OpenCycleEditor();
            return;
        }

        if (_previewMesh is not null)
        {
            byte[] meshData = _previewMeshData ?? _previewMesh.Write();
            Action<byte[], string>? queueImport = null;
            bool pending = false;
            if (_previewMeshItem?.Resource is not null && _previewMeshWhere is { } where)
            {
                BrowserItem item = _previewMeshItem;
                pending = _changes.Contains(where.ArchivePath, where.EntryIndex, item.Index);
                queueImport = (data, summary) => QueueMeshViewerImport(where, item, data, summary);
            }

            new MeshWindow(_previewMesh, meshData, _previewMeshName, _previewSiblings, _archives, _skeletonIndex, _settings, queueImport, pending).Show();
            return;
        }

        if (_preview is null)
            return;

        if (PreviewLevelBox.SelectedItem is TextureMipLevel level)
            _preview.FocusLevel = level.Level;

        var view = _preview;
        TextureWindow? window = null;
        window = new TextureWindow(view, _settings, () =>
        {
            if (ReplaceTexture(view) is { } fresh)
                window!.ShowView(fresh);
        });
        window.Closed += (_, _) => _textureWindows.Remove(window);
        _textureWindows.Add(window);
        window.Show();
    }

    void QueueMeshViewerImport(Location where, BrowserItem item, byte[] rebuilt, string _)
    {
        if (!QueueChanges([new PendingChange(where.ArchivePath, where.EntryIndex, where.EntryName, item.Index, item.Name, rebuilt, ResourceClassHash: Mesh.ClassHash)]))
            return;

        if (ReferenceEquals(_previewMeshItem, item) && _previewMeshWhere == where)
        {
            _previewMeshData = (byte[])rebuilt.Clone();
            _previewMesh = Mesh.Read(rebuilt);
        }

        SetStatus($"{item.Name}: replacement queued");
        UpdateChangeButtons();
    }

    TextureView? ReplaceTexture(TextureView view)
    {
        Resource resource;
        Location location;
        int index;
        TextureMap texture;

        try
        {
            (resource, location, index) = LocateTexture(view);
            texture = TextureMap.Read(EffectiveData(location, index, resource));
        }
        catch (Exception ex)
        {
            ShowError("Could not work out which resource to write", ex);
            return null;
        }

        var dialog = new ImportWindow(resource.Name, texture, TextureImporter.Targets(texture, location, resource.Name, _archives)) { Owner = this };

        if (dialog.ShowDialog() != true || dialog.Path is null)
            return null;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var changes = TextureImporter.Build(dialog.Path, texture, resource.Data, location, index, resource.Name, _archives, dialog.GenerateMips, out _);

            if (!QueueChanges(changes, $"Replace textures for {resource.Name}"))
                return null;

            SetStatus($"{resource.Name}: texture replacement queued");
            UpdateChangeButtons();

            return ShowPending(resource.Name, changes);
        }
        catch (Exception ex)
        {
            ShowError($"Could not import {Path.GetFileName(dialog.Path)}", ex);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        return null;
    }

    TextureView? ShowPending(string name, List<PendingChange> changes)
    {
        var replaced = new Dictionary<ulong, byte[]>();

        foreach (PendingChange change in changes.Where(change => change.ResourceClassHash == CompiledMip.ClassHash && change.Data.Length >= sizeof(ulong)))
            replaced[BitConverter.ToUInt64(change.Data, 0)] = change.Data;

        var view = TextureLoader.FromTexture(name, TextureMap.Read(changes[0].Data), _archives, replaced);

        _preview = view;
        PreviewLevelBox.ItemsSource = view.Levels;
        PreviewLevelBox.SelectedItem = view.Focus;

        return view;
    }

    (Resource Resource, Location Location, int Index) LocateTexture(TextureView view)
    {
        if (ItemList.SelectedItem is BrowserItem item && item.Resource is not null && _showing is not null && item.Resource.ClassHash == TextureMap.ClassHash && item.Resource.Id == view.Texture.Id)
            return (item.Resource, _showing, item.Index);

        var found = _archives?.FindResource(view.Texture.Id, TextureMap.ClassHash) ?? throw new InvalidOperationException($"The TextureMap behind this view (id 0x{view.Texture.Id:X}) was not found in any archive.");

        return (found.Resource, new Location(found.File.ArchivePath, found.File.EntryIndex, found.File.Name),
            found.ResourceIndex);
    }

    void OpenCycleEditor()
    {
        if (_previewCycle is null || _previewCycleItem is null || _previewCycleWhere is null)
            return;

        var cycle = _previewCycle;
        var item = _previewCycleItem;
        var where = _previewCycleWhere;

        new TimeCycleWindow(cycle, item.Name, data =>
        {
            if (!QueueChanges([new PendingChange(where.ArchivePath, where.EntryIndex, where.EntryName, item.Index, item.Name, data, ResourceClassHash: item.Resource!.ClassHash)], $"Edit {item.Name}"))
                return;

            SetStatus($"{item.Name}: changes queued");
            UpdateChangeButtons();
        }).Show();
    }

    void OpenBuildTableEditor()
    {
        if (_previewBuildTableData is null || _previewBuildTableItem is null || _previewBuildTableWhere is null)
            return;

        var currentArchivePaths = _settings.IsConfigured ? ArchiveLocator.Find(_settings.GamePath) : [];
        if (_armoryIndex is not null && !_armoryIndex.MatchesArchives(currentArchivePaths))
        {
            _armoryIndex = null;
            SetStatus("Game archives changed; rebuilding the Armory index before opening the editor…");
            _ = BuildArmoryIndexAsync(OpenBuildTableEditor);
            return;
        }

        if (_armoryIndex is null)
        {
            var answer = MessageBox.Show(this,
                "The Armory editor needs the Armory index first. It reads the confirmed game database and language data once, so opening a weapon does not trigger a slow full archive search.\n\nPrepare it now?",
                "Armory index required", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes)
                _ = BuildArmoryIndexAsync(OpenBuildTableEditor);
            return;
        }

        var data = _previewBuildTableData;
        var item = _previewBuildTableItem;
        var where = _previewBuildTableWhere;
        var targets = _shown
            .Where(shown => shown.Resource is not null && shown.Id != 0)
            .GroupBy(shown => shown.Id)
            .Select(group => group.First())
            .Select(shown => new BuildTableTarget(shown.Id, shown.Name, shown.Resource!.ClassHash, ResourceTypes.NameOf(shown.Resource.ClassHash), where.EntryName, Path.GetFileName(where.ArchivePath)))
            .ToList();

        var archivePaths = currentArchivePaths;
        archivePaths.RemoveAll(path => string.Equals(path, where.ArchivePath, StringComparison.OrdinalIgnoreCase));
        archivePaths.Insert(0, where.ArchivePath);

        var familyResources = _shown
            .Where(shown => shown.Resource?.ClassHash == BuildTable.ClassHash)
            .Select(shown => new BuildTableResourceSource(shown.Index, shown.Id, shown.Name, _changes.Find(where.ArchivePath, where.EntryIndex, shown.Index)?.Data ?? shown.Resource!.Data))
            .ToList();
        var previewResources = _shown
            .Where(shown => shown.Resource is not null && shown.Id != 0)
            .Select(shown => new Resource
            {
                Id = shown.Resource!.Id,
                Name = shown.Resource.Name,
                ClassHash = shown.Resource.ClassHash,
                Header = shown.Resource.Header,
                Data = _changes.Find(where.ArchivePath, where.EntryIndex, shown.Index)?.Data
                    ?? shown.Resource.Data,
            })
            .ToList();
        var previewResourceIndexes = _shown
            .Where(shown => shown.Resource is not null && shown.Id != 0)
            .GroupBy(shown => shown.Id)
            .ToDictionary(group => group.Key, group => group.First().Index);
        var workingArmoryIndex = _armoryIndex?.CreateWorkingCopy();
        if (workingArmoryIndex is not null)
        {
            var pendingArmoryChanges = _changes.Changes
                .Where(change => BuildTableGameMetadataResolver.IsGameDatabaseContainer(change.EntryName))
                .Select(change => new ArmoryDatabaseResourceChange(change.ArchivePath, change.EntryIndex, change.EntryName, change.ResourceIndex, change.ResourceName, change.Data))
                .ToList();
            workingArmoryIndex.ApplyChanges(pendingArmoryChanges);
        }

        new BuildTableWindow(data, item.Name, targets, archivePaths, familyResources, changed =>
        {
            var queued = changed.Select(resource => new PendingChange(where.ArchivePath, where.EntryIndex, where.EntryName, resource.ResourceIndex, resource.Name, resource.Data, ResourceClassHash: BuildTable.ClassHash)).ToList();
            if (!QueueChanges(queued, $"Edit {BuildTableNames.FamilyTitle(item.Name)}"))
                return;

            var current = changed.FirstOrDefault(change => change.ResourceIndex == item.Index);
            if (current is not null && _previewBuildTableItem == item && _previewBuildTableWhere == where)
            {
                _previewBuildTableData = current.Data;
                _previewBuildTable = BuildTable.Read(current.Data);
            }

            SetStatus($"{BuildTableNames.FamilyTitle(item.Name)}: changes queued");
            UpdateChangeButtons();
        }, workingArmoryIndex, databaseChanges =>
        {
            var queued = databaseChanges.Select(change => new PendingChange(change.ArchivePath, change.EntryIndex, change.EntryName, change.ResourceIndex, change.ResourceName, change.Data)).ToList();
            if (!QueueChanges(queued, "Update Gunsmith visibility"))
                return;

            SetStatus("Gunsmith visibility change queued");
            UpdateChangeButtons();
        }, previewResources, saveAttachment: attachment =>
            {
                var queued = new List<PendingChange>();
                queued.AddRange(attachment.LocalChanges.Select(resource => new PendingChange(
                    where.ArchivePath, where.EntryIndex, where.EntryName,
                    resource.ResourceIndex, resource.Name, resource.Data,
                    ResourceClassHash: BuildTable.ClassHash)));
                queued.AddRange(attachment.DatabaseChanges.Select(change => new PendingChange(
                    change.ArchivePath, change.EntryIndex, change.EntryName,
                    change.ResourceIndex, change.ResourceName, change.Data)));
                queued.AddRange(attachment.ResourceAdditions.Select(addition => new PendingChange(
                    addition.ArchivePath, addition.EntryIndex, addition.EntryName, -1,
                    addition.ResourceName, addition.Data,
                    new PendingResourceAddition(addition.ResourceId, addition.ClassHash,
                        addition.Header), ResourceClassHash: addition.ClassHash)));
                queued.AddRange(attachment.EntryAdditions.Select(addition => new PendingChange(
                    addition.ArchivePath, -1, addition.EntryName, -1, addition.EntryName,
                    addition.Data, null, new PendingForgeEntryAddition(addition.EntryId,
                        addition.EntryName, addition.Extension, addition.InfoTemplate, addition.PrefetchBlock))));

                if (!QueueChanges(queued, $"Add attachment {attachment.DisplayName}"))
                    throw new InvalidOperationException("The attachment could not be added to the change list.");

                BuildTableResourceChange? current = attachment.LocalChanges.FirstOrDefault(change => change.ResourceIndex == item.Index);
                if (current is not null && _previewBuildTableItem == item && _previewBuildTableWhere == where)
                {
                    _previewBuildTableData = current.Data;
                    _previewBuildTable = BuildTable.Read(current.Data);
                }
                SetStatus($"{attachment.DisplayName}: attachment queued");
            }, localArchivePath: where.ArchivePath, localEntryIndex: where.EntryIndex,
            localEntryName: where.EntryName,
            localResourceIndexes: previewResourceIndexes).Show();
    }

    const int HexColumns = 8;

    static void AddHexDump(byte[] data, List<string> lines)
    {
        lines.Add("");

        int rows = Math.Min(40, (data.Length + HexColumns - 1) / HexColumns);
        for (int row = 0; row < rows; row++)
        {
            int offset = row * HexColumns;
            int count = Math.Min(HexColumns, data.Length - offset);

            var hex = new StringBuilder(HexColumns * 3);
            var text = new StringBuilder(HexColumns);

            for (int i = 0; i < count; i++)
            {
                byte b = data[offset + i];
                hex.Append(b.ToString("x2")).Append(' ');
                text.Append(b >= 32 && b < 127 ? (char)b : '.');
            }

            lines.Add($"{offset:x4}  {hex,-24} {text}");
        }

        if (data.Length > rows * HexColumns)
            lines.Add($"...   {data.Length - rows * HexColumns:N0} more bytes");
    }

    void ApplyFilter()
    {
        string filter = FilterBox.Text.Trim();
        var items = filter.Length == 0
            ? _shown
            : _shown.Where(i => i.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        ItemList.ItemsSource = items;
        CountText.Text = items.Count == _shown.Count
            ? $"{items.Count} items"
            : $"{items.Count} of {_shown.Count} items";
        UpdateAssetActions();
    }

    void SetStatus(string text)
    {
        if (!StatusText.Dispatcher.CheckAccess())
        {
            StatusText.Dispatcher.Invoke(() => SetStatus(text));
            return;
        }

        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource("TextDim");
    }

    void ShowError(string what, Exception ex, Window? owner = null)
    {
        SetStatus($"{what}: {ex.Message}");
        StatusText.Foreground = (Brush)FindResource("Warning");

        MessageBox.Show(owner ?? this, $"{what}.{Environment.NewLine}{Environment.NewLine}{ex.Message}", "Wildlands Toolkit", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnClosed(EventArgs e)
    {
        _archives?.Dispose();
        _archive?.Dispose();
        base.OnClosed(e);
    }
}

public sealed class ArchiveItem(string path)
{
    public string Path { get; } = path;
    public string Name { get; } = System.IO.Path.GetFileName(path);
    public long Size { get; } = new FileInfo(path).Length;
    public string ShortName => System.IO.Path.GetFileNameWithoutExtension(Path);

    public string SizeText => Size >= 1L << 30
        ? $"{Size / (double)(1L << 30):0.0} GB"
        : $"{Size / (double)(1 << 20):0} MB";
}

public sealed class SetTexture
{
    public string Slot { get; init; } = "";
    public string Name { get; init; } = "";
    public string Size { get; init; } = "";

    public string From { get; init; } = "";

    public ulong Id { get; init; }
    public BitmapSource? Thumbnail { get; init; }
}

sealed class ListPlace
{
    public int SelectedIndex { get; init; } = -1;
    public double ScrollOffset { get; init; }
}
