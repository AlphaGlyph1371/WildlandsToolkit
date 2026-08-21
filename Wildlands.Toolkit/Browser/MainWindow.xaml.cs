using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    SkeletonIndex? _skeletonIndex;
    bool _buildingSkeletonIndex;
    List<BrowserItem> _shown = [];
    TextureView? _preview;
    readonly List<TextureWindow> _textureWindows = [];
    Mesh? _previewMesh;
    string _previewMeshName = "";

    List<Resource> _previewSiblings = [];

    TextureSet? _previewSet;
    string _previewSetName = "";

    TimeCycle? _previewCycle;
    BrowserItem? _previewCycleItem;
    Location? _previewCycleWhere;

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
            LoadSkeletonIndex();
        }

        ContentRendered += OnFirstRender;
    }

    // Anything that puts a dialog in front of the user waits until the window has drawn
    // itself once, so the dialog does not sit on a blank white window.
    void OnFirstRender(object? sender, EventArgs e)
    {
        ContentRendered -= OnFirstRender;

        ShowEarlyNotice();

        if (_settings.IsConfigured)
            return;

        if (!RunSetup())
        {
            SetStatus("No game folder set. Use \"Game folder\" or \"Open file\".");
            return;
        }

        LoadArchiveList();
        LoadSkeletonIndex();
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
        if (RunSetup())
            LoadArchiveList();
    }

    void LoadSkeletonIndex()
    {
        string path = AppSettings.SkeletonCachePath;

        if (File.Exists(SkeletonIndex.PartialPath(path)))
        {
            try { File.Delete(SkeletonIndex.PartialPath(path)); } catch { }

            Offer("The last scan was interrupted and did not finish.\n\nScan again now?");
            return;
        }

        if (File.Exists(path))
        {
            _skeletonIndex = SkeletonIndex.Load(path);
            UpdateSkeletonIndexButton();

            if (_skeletonIndex is null)
                Offer("The saved scan is damaged and cannot be used.\n\nScan again now?");

            return;
        }

        Offer("To export a mesh with a working skeleton, the toolkit has to scan the game once. "
            + "This can take up to 10 minutes or more.\n\nScan now? You can also do it later and pick skeletons by hand.");
    }

    void Offer(string question)
    {
        UpdateSkeletonIndexButton();

        if (!_settings.IsConfigured)
            return;

        if (MessageBox.Show(this, question, "Skeletons", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            BuildSkeletonIndex();
    }

    void SkeletonIndex_Click(object sender, RoutedEventArgs e) => BuildSkeletonIndex();

    async void BuildSkeletonIndex()
    {
        if (_buildingSkeletonIndex || !_settings.IsConfigured)
            return;

        _buildingSkeletonIndex = true;
        ScanText.Text = "Looking for skeletons";
        ScanText.Visibility = Visibility.Visible;
        UpdateSkeletonIndexButton();

        try
        {
            string folder = _settings.GamePath;
            var index = await Task.Run(() =>
            {
                var archives = Directory.GetFiles(folder, "*.forge").Select(ForgeArchive.Open).ToList();
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
        }
        catch (Exception ex)
        {
            ShowError("Could not scan for skeletons", ex);
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
        SkeletonIndexButton.Content = _buildingSkeletonIndex ? "Scanning…"
            : _skeletonIndex is null ? "Find skeletons"
            : "Scan again";
    }

    void LoadArchiveList()
    {
        if (!_settings.IsConfigured)
        {
            ArchiveList.ItemsSource = null;
            return;
        }

        var archives = Directory.GetFiles(_settings.GamePath, "*.forge")
            .Select(path => new ArchiveItem(path))
            .OrderBy(a => a.Name)
            .ToList();

        ArchiveList.ItemsSource = archives;
        SetStatus($"{archives.Count} archives in {_settings.GamePath}");
    }

    void ArchiveList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ArchiveList.SelectedItem is ArchiveItem archive)
            Navigate(new Location(archive.Path));
    }

    void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a forge archive",
            Filter = "Forge archives (*.forge)|*.forge|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() == true)
            Navigate(new Location(dialog.FileName));
    }

    void Navigate(Location location)
    {
        if (!Go(location))
            return;

        if (_historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

        _history.Add(location);
        _historyIndex = _history.Count - 1;
        UpdateNavigationButtons();
    }

    bool Go(Location location)
    {
        RememberPlace();

        try
        {
            if (_archive is null || _archive.FilePath != location.ArchivePath)
            {
                _archives?.Dispose();
                _archive?.Dispose();
                _archive = ForgeArchive.Open(location.ArchivePath);
                _archives = new ArchiveSet(_archive);
                _settings.AddRecent(location.ArchivePath);
                _settings.Save();
            }

            var watch = Stopwatch.StartNew();

            if (location.IsArchiveRoot)
            {
                _shown = _archive.Entries.Select(BrowserItem.FromEntry).ToList();
                watch.Stop();
                SetStatus($"{_shown.Count} entries in {watch.ElapsedMilliseconds} ms");
            }
            else
            {
                var entry = _archive.Entries.First(x => x.Index == location.EntryIndex);
                using var stream = new MemoryStream(_archive.ReadEntry(entry));
                var file = DataFile.Read(stream);
                _shown = file.Resources.Select(BrowserItem.FromResource).ToList();
                watch.Stop();
                SetStatus($"{_shown.Count} resources in {watch.ElapsedMilliseconds} ms");
            }

            PathText.Text = location.Display;
            _showing = location;
            ApplyFilter();
            RestorePlace(location);
            return true;
        }
        catch (Exception ex)
        {
            ShowError($"Could not open {location.Display}", ex);
            return false;
        }
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

    void ItemList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemList.SelectedItem is not BrowserItem item)
            return;

        if (item.Resource is not null)
        {
            OpenViewer();
            return;
        }

        if (item.Entry is not null && item.CanOpen)
            Navigate(new Location(_archive!.FilePath, item.Entry.Index, item.Name));
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex <= 0)
            return;

        _historyIndex--;
        Go(_history[_historyIndex]);
        UpdateNavigationButtons();
    }

    void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex >= _history.Count - 1)
            return;

        _historyIndex++;
        Go(_history[_historyIndex]);
        UpdateNavigationButtons();
    }

    void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_historyIndex < 0 || _history[_historyIndex].IsArchiveRoot)
            return;

        Navigate(_history[_historyIndex].Parent);
    }

    void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _historyIndex > 0;
        ForwardButton.IsEnabled = _historyIndex < _history.Count - 1;
        UpButton.IsEnabled = _historyIndex >= 0 && !_history[_historyIndex].IsArchiveRoot;
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
        var item = ItemList.SelectedItem as BrowserItem;
        int count = ItemList.SelectedItems.Count;

        bool stepsIn = item?.Entry is not null && item.CanOpen;
        MenuOpen.Header = stepsIn ? "Open"
            : _previewCycle is not null ? "Open in time cycle editor"
            : _previewMesh is not null ? "Open in mesh viewer"
            : "Open in texture viewer";
        MenuOpen.IsEnabled = stepsIn || (item is not null && (_preview is not null || _previewMesh is not null || _previewCycle is not null));

        MenuExtract.IsEnabled = count > 0;
        MenuExtract.Header = count > 1 ? $"Extract {count} items..." : "Extract...";

        MenuExport.IsEnabled = _preview is not null || _previewMesh is not null || _previewSet is not null;

        MenuReplace.IsEnabled = item?.Resource is not null || _preview is not null;

        MenuCopyName.IsEnabled = item is not null;
        MenuCopyId.IsEnabled = item is { Id: not 0 };
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

    void MenuReplace_Click(object sender, RoutedEventArgs e)
    {
        if (_preview is not null)
        {
            ReplaceTexture(_preview);
            return;
        }

        if (ItemList.SelectedItem is not BrowserItem item || item.Resource is null || _showing is null)
            return;

        var dialog = new OpenFileDialog { Title = $"Replace {item.Name}" };
        if (dialog.ShowDialog() != true)
            return;

        byte[] data;
        try
        {
            data = File.ReadAllBytes(dialog.FileName);
        }
        catch (Exception ex)
        {
            ShowError($"Could not read {Path.GetFileName(dialog.FileName)}", ex);
            return;
        }

        _changes.Set(new PendingChange(_showing.ArchivePath, _showing.EntryIndex, _showing.EntryName, item.Index, item.Name, data));

        SetStatus($"{item.Name}: {item.Size} -> {data.Length} bytes, waiting for Apply");
        UpdateChangeButtons();
    }

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (_changes.Count == 0 || _showing is null)
            return;

        SetStatus("Working out what has to be written...");

        List<ArchiveWork> plans;
        try
        {
            plans = await Task.Run(() => _changes.Plan(new Progress<string>(SetStatus)));
        }
        catch (System.Exception ex)
        {
            ShowError("Could not build the changed files", ex);
            return;
        }

        var fresh = plans.Where(x => !ArchiveBackup.Exists(x.Path)).ToList();
        var rebuilds = plans.Where(x => x.NeedsRebuild).ToList();

        long rebuildSize = rebuilds.Sum(x => x.Size) / (1L << 30);
        long backupSize = fresh.Sum(x => x.Size) / (1L << 30);

        var question = new StringBuilder();
        question.AppendLine(plans.Count == 1
            ? $"Write {Amount(_changes.Count, "change")} into {plans[0].Name}?"
            : $"Write {Amount(_changes.Count, "change")} into {Amount(plans.Count, "archive")}?");
        question.AppendLine();

        if (plans.Count == 1)
        {
            var only = plans[0];
            question.AppendLine(only.NeedsRebuild
                ? $"{Amount(only.EntryNames.Count, "entry", "entries")} change, and the whole archive is written again."
                : $"{Amount(only.EntryNames.Count, "entry", "entries")} change, written where they already are.");
        }
        else
        {
            foreach (var work in plans)
                question.AppendLine($"{work.Name}   {Amount(work.EntryNames.Count, "entry", "entries")}" + (work.NeedsRebuild ? ", written again completely" : ""));
        }

        if (rebuilds.Count > 0)
        {
            question.AppendLine();
            question.AppendLine($"That can take a few minutes and needs at least {rebuildSize + 1} GB of free disk space.");
        }

        if (fresh.Count > 0)
        {
            question.AppendLine();
            question.AppendLine($"A backup is made first and needs another {backupSize + 1} GB.");
        }

        if (MessageBox.Show(this, question.ToString(), "Apply changes", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            SetStatus("Nothing written");
            return;
        }

        var place = _showing;
        _archives?.Dispose();
        _archive?.Dispose();
        _archive = null;
        _archives = null;

        ApplyButton.IsEnabled = false;
        DiscardButton.IsEnabled = false;

        var progress = new Progress<string>(SetStatus);
        var watch = Stopwatch.StartNew();

        try
        {
            await Task.Run(() => _changes.Write(plans, progress));
            watch.Stop();
            Navigate(place);
            SetStatus($"Applied in {watch.Elapsed.TotalSeconds:0.0} s");
        }
        catch (System.Exception ex)
        {
            ShowError("Could not write the changes", ex);
            Navigate(place);
        }

        UpdateChangeButtons();
    }

    static string Amount(int count, string one, string? many = null) =>
        count == 1 ? $"1 {one}" : $"{count} {many ?? one + "s"}";

    void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (_changes.Count == 0)
            return;

        _changes.Clear();
        SetStatus("Changes discarded");
        UpdateChangeButtons();
        ShowArchiveAgain();
    }

    // Nothing was written, so both the preview and any open texture window have to go back
    // to what the archive holds.
    void ShowArchiveAgain()
    {
        ShowPreview(ItemList.SelectedItem as BrowserItem);

        foreach (var window in _textureWindows.ToList())
        {
            try
            {
                var (resource, _, _) = LocateTexture(window.View);
                var texture = TextureMap.Read(resource.Data);
                window.ShowView(TextureLoader.FromTexture(resource.Name, texture, _archives));
            }
            catch
            {
                // A window whose texture cannot be found again simply keeps what it shows
            }
        }
    }

    void UpdateChangeButtons()
    {
        ApplyButton.IsEnabled = _changes.Count > 0;
        DiscardButton.IsEnabled = _changes.Count > 0;
        ApplyButton.Content = _changes.Count > 0 ? $"Apply {_changes.Count} changes" : "Apply changes";
    }

    void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (ItemList.SelectedItem is not BrowserItem item)
            return;

        if (item.Entry is not null && item.CanOpen)
            Navigate(new Location(_archive!.FilePath, item.Entry.Index, item.Name));
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
            string done = MeshExporter.SaveFbx(this, _previewMesh, _previewMeshName, _previewSiblings,
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
        ExtractButton.IsEnabled = ItemList.SelectedItems.Count > 0;
        ShowPreview(ItemList.SelectedItem as BrowserItem);
    }

    void ShowPreview(BrowserItem? item)
    {
        _preview = null;
        _previewMesh = null;
        _previewSet = null;
        _previewCycle = null;
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
                AddHexDump(item.Resource.Data, lines);
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
                return FromResource(item.Name, item.Resource, out note);

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

    TextureView? FromResource(string name, Resource resource, out string note)
    {
        note = "";

        if (resource.ClassHash == TextureMap.ClassHash)
            return TextureLoader.FromTexture(name, TextureMap.Read(resource.Data), _archives);

        if (resource.ClassHash == CompiledMip.ClassHash)
            return TextureLoader.FromMip(name, CompiledMip.Read(resource.Data), _archives, out note);

        return null;
    }

    TextureView? FromEntry(ForgeEntry entry, out string note)
    {
        note = "";

        using var stream = new MemoryStream(_archive!.ReadEntry(entry));
        var file = DataFile.Read(stream);

        var resource = file.Resources.FirstOrDefault(r => r.ClassHash == TextureMap.ClassHash) ?? file.Resources.FirstOrDefault(r => r.ClassHash == CompiledMip.ClassHash);

        return resource is null ? null : FromResource(resource.Name, resource, out note);
    }

    Material? ResolveMaterial(BrowserItem item)
    {
        if (item.Resource is null || item.Resource.ClassHash != Material.ClassHash)
            return null;

        try
        {
            return Material.Read(item.Resource.Data);
        }
        catch
        {
            return null;
        }
    }

    void DescribeMaterial(Material material, List<string> lines)
    {
        lines.Add("");
        lines.Add($"blend  {material.BlendMode}{(material.IsOpaque ? "  (opaque)" : "")}"
            + (material.Flags.TwoSided ? "  two-sided" : ""));

        if (material.Note.Length > 0)
            lines.Add($"note   {material.Note}");

        lines.Add($"{material.Parameters.Count} parameters");

        var tiles = new List<SetTexture>();
        Mouse.OverrideCursor = Cursors.Wait;

        try
        {
            foreach (var parameter in material.Parameters)
            {
                string name = ParameterNames.NameOf(parameter.Name);
                lines.Add($"  {name,-20} {Describe(parameter, name, tiles)}");
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

    string Describe(MaterialParameter parameter, string name, List<SetTexture> tiles)
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
            return TextureSet.Read(item.Resource.Data);
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

        var view = TextureLoader.FromTexture(found.Resource.Name,
            TextureMap.Read(found.Resource.Data), _archives);

        TextureWindow? window = null;
        window = new TextureWindow(view, _settings, () =>
        {
            if (ReplaceTexture(view) is { } fresh)
                window!.ShowView(fresh);
        });
        window.Owner = this;
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
                return Mesh.Read(item.Resource.Data);
            }

            if (item.Entry is not null && item.Entry.FileExtension == ".data"
                && item.Entry.Length <= MaxPreviewBytes)
            {
                using var stream = new MemoryStream(_archive!.ReadEntry(item.Entry));
                var file = DataFile.Read(stream);
                var resource = file.Resources.FirstOrDefault(r => r.ClassHash == Mesh.ClassHash);

                if (resource is null)
                    return null;

                _previewSiblings = file.Resources;
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
            _previewCycle = TimeCycle.Read(item.Resource!.Data);
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

    void ShowMesh(Mesh mesh, string name, List<string> lines)
    {
        _previewMesh = mesh;
        _previewMeshName = name;

        lines.Add("");
        lines.Add($"geometry  {mesh.Geometry}");
        lines.Add($"submeshes {mesh.SubMeshCount}");

        if (mesh.BoneCount > 0)
            lines.Add($"bones     {mesh.BoneCount}");

        lines.Add($"size      {mesh.ExtentMax[0] - mesh.ExtentMin[0]:0.00} x " +
                  $"{mesh.ExtentMax[1] - mesh.ExtentMin[1]:0.00} x " +
                  $"{mesh.ExtentMax[2] - mesh.ExtentMin[2]:0.00} m");

        if (mesh.Data is null)
        {
            lines.Add("");
            lines.Add($"       not drawn: {mesh.Note}");
            return;
        }

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
        if (_previewCycle is not null)
        {
            OpenCycleEditor();
            return;
        }

        if (_previewMesh is not null)
        {
            new MeshWindow(_previewMesh, _previewMeshName, _previewSiblings, _archives, _skeletonIndex, _settings)
                { Owner = this }.Show();
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
        window.Owner = this;
        window.Closed += (_, _) => _textureWindows.Remove(window);
        _textureWindows.Add(window);
        window.Show();
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
            texture = TextureMap.Read(resource.Data);
        }
        catch (Exception ex)
        {
            ShowError("Could not work out which resource to write", ex);
            return null;
        }

        var dialog = new ImportWindow(resource.Name, texture,
            TextureImporter.Targets(texture, location, resource.Name, _archives))
            { Owner = this };

        if (dialog.ShowDialog() != true || dialog.Path is null)
            return null;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var changes = TextureImporter.Build(dialog.Path, texture, resource.Data, location, index,
                resource.Name, _archives, dialog.GenerateMips, out string summary);

            foreach (var change in changes)
                _changes.Set(change);

            SetStatus($"{resource.Name}: {summary}, waiting for Apply");
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

        foreach (var change in changes.Skip(1))
        {
            try { replaced[CompiledMip.Read(change.Data).Id] = change.Data; }
            catch { }
        }

        var view = TextureLoader.FromTexture(name, TextureMap.Read(changes[0].Data), _archives, replaced);

        _preview = view;
        PreviewLevelBox.ItemsSource = view.Levels;
        PreviewLevelBox.SelectedItem = view.Focus;

        return view;
    }

    (Resource Resource, Location Location, int Index) LocateTexture(TextureView view)
    {
        if (ItemList.SelectedItem is BrowserItem item && item.Resource is not null && _showing is not null
            && item.Resource.ClassHash == TextureMap.ClassHash && item.Resource.Id == view.Texture.Id)
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
            _changes.Set(new PendingChange(where.ArchivePath, where.EntryIndex, where.EntryName, item.Index, item.Name, data));

            SetStatus($"{item.Name}: {item.Size} -> {data.Length} bytes, waiting for Apply");
            UpdateChangeButtons();
        })
        { Owner = this }.Show();
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

    void ShowError(string what, Exception ex)
    {
        SetStatus($"{what}: {ex.Message}");
        StatusText.Foreground = (Brush)FindResource("Warning");

        MessageBox.Show(this, $"{what}.{Environment.NewLine}{Environment.NewLine}{ex.Message}", "Wildlands Toolkit", MessageBoxButton.OK, MessageBoxImage.Error);
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
