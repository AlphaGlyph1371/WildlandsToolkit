using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Materials;
using Wildlands.Formats.Models;
using Wildlands.Formats.Textures;

namespace Wildlands.Toolkit;

enum BackFaces { FromMaterial, Always, Never }

sealed record MeshLevel(string Label, string Name, Resource Mesh, List<Resource> Siblings);

public partial class MeshWindow : Window
{
    static readonly Color[] Palette =
    [
        Color.FromRgb(0xB8, 0xC0, 0xCC),
        Color.FromRgb(0x8F, 0xC2, 0xE8),
        Color.FromRgb(0xC7, 0xB2, 0xE0),
        Color.FromRgb(0xA8, 0xD8, 0xB0),
        Color.FromRgb(0xE8, 0xC5, 0x94),
        Color.FromRgb(0xE0, 0xA5, 0xA5),
        Color.FromRgb(0x9E, 0xD8, 0xD2),
        Color.FromRgb(0xD4, 0xD2, 0x96),
    ];

    Mesh _mesh;
    ulong _meshId;
    byte[] _resourceData;
    string _name;
    readonly string _openedName;
    readonly List<Resource> _openedSiblings;
    List<Resource> _siblings;
    readonly AppSettings _settings;
    readonly Action<byte[], string>? _queueImport;
    readonly ChangeSet? _changes;
    readonly ArchiveSet? _archives;
    readonly SkeletonIndex? _skeletonIndex;

    MeshRenderer? _renderer;
    readonly PerspectiveCamera _camera = new() { FieldOfView = 45, UpDirection = new Vector3D(0, 0, 1) };
    readonly OrbitCameraController _cameraController;
    WriteableBitmap? _picture;
    bool _renderQueued;

    List<RenderPart> _parts = [];
    int _vertexCount;
    Rect3D _bounds = Rect3D.Empty;

    List<SkeletonBone>? _skeleton;
    List<SkeletonBone>? _automaticSkeleton;
    MeshGeometry3D? _armatureGeometry;
    bool[] _partVisible = [];
    bool _armatureVisible = true;
    bool _updatingHierarchy;
    GridLength _hierarchyWidth = new(300);
    MeshHierarchyItem? _selectedHierarchy;
    readonly ObservableCollection<MeshHierarchyItem> _hierarchy = [];

    List<MeshSurface> _surfaces = [];
    SurfaceSources _sources;
    List<ClothStyle> _styles = [];
    ClothStyle? _style;
    bool _fillingStyles;
    List<MeshLevel> _levels = [];
    bool _fillingLevels;
    bool _texturesLoaded;
    bool _textured = true;
    bool _colourRanges = true;
    bool _floor = true;
    bool _withChanges = true;
    bool _changesMatter;
    BackFaces _backFaces = BackFaces.FromMaterial;

    public MeshWindow(Mesh mesh, byte[] resourceData, string name, List<Resource> siblings,
        ArchiveSet? archives, SkeletonIndex? skeletonIndex, AppSettings settings,
        Action<byte[], string>? queueImport = null, bool hasPendingChange = false, ChangeSet? changes = null)
    {
        InitializeComponent();
        _cameraController = new OrbitCameraController(Stage, _camera);
        _camera.Changed += (_, _) => RequestRender();

        if (mesh.IsOpaque)
            throw new InvalidDataException("This Mesh uses an unknown object-pointer layout. It can be packaged unchanged, but cannot yet be previewed or edited.");

        _mesh = mesh;
        _resourceData = (byte[])resourceData.Clone();
        _meshId = BitConverter.ToUInt64(resourceData, 0);
        _name = name;
        _openedName = name;
        _siblings = siblings;
        _openedSiblings = siblings;
        _archives = archives;
        _skeletonIndex = skeletonIndex;
        _settings = settings;
        _queueImport = queueImport;
        _changes = changes;
        _changesMatter = hasPendingChange;
        _sources = NewSources();

        try
        {
            _renderer = new MeshRenderer();
        }
        catch (Exception ex)
        {
            _renderer = null;
            HintText.Text = $"3D rendering is not available: {ex.Message}";
        }

        ImportButton.IsEnabled = queueImport is not null;
        if (queueImport is null)
            ImportButton.ToolTip = "Import is available when the viewer was opened from a concrete Mesh resource inside a data container.";

        ShowMesh(mesh);
        _surfaces = MeshSurfaces.Load(mesh, _sources, withTextures: false, null);
        UpdateChangesButton();
        LoadStyles();

        Loaded += (_, _) =>
        {
            Fit();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_textured)
                    LoadTextures();
                LoadLevels();
            });
        };
        Closed += (_, _) =>
        {
            _renderer?.Dispose();
            _renderer = null;
        };
    }

    void ShowMesh(Mesh mesh)
    {
        _mesh = mesh;

        var vertices = MeshGeometry.ReadVertices(mesh);
        var gpu = new RenderVertex[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            var vertex = vertices[i];
            var uv = vertex.Uv.Length > 0 ? vertex.Uv[0] : Vector2.Zero;
            gpu[i] = new RenderVertex
            {
                Position = vertex.Position,
                Normal = vertex.Normal,
                Tangent = vertex.Tangent,
                Binormal = vertex.Binormal,
                UvA = uv,
                UvB = vertex.Uv.Length > 1 ? vertex.Uv[1] : uv,
                Paint = vertex.Paint,
            };
        }

        var indices = new List<uint>();
        _parts = [];
        _bounds = Rect3D.Empty;

        foreach (var range in mesh.Data.Standard)
        {
            var triangles = MeshGeometry.ReadTriangles(mesh, range);
            var low = new Vector3(float.MaxValue);
            var high = new Vector3(float.MinValue);
            foreach (int corner in triangles)
            {
                low = Vector3.Min(low, vertices[corner].Position);
                high = Vector3.Max(high, vertices[corner].Position);
            }

            var bounds = triangles.Length == 0 ? Rect3D.Empty : new Rect3D(low.X, low.Y, low.Z, high.X - low.X, high.Y - low.Y, high.Z - low.Z);
            _parts.Add(new RenderPart { IndexStart = indices.Count, IndexCount = triangles.Length, VertexCount = range.VertexCount, Bounds = bounds });
            _bounds.Union(bounds);

            foreach (int corner in triangles)
                indices.Add((uint)corner);
        }

        _vertexCount = vertices.Length;
        _renderer?.SetGeometry(gpu, indices.ToArray());
        _partVisible = Enumerable.Repeat(true, _parts.Count).ToArray();

        bool skinned = mesh.Bones.Count > 0;
        _automaticSkeleton = skinned ? SkeletonFinder.Find(_siblings, mesh.Bones, _skeletonIndex) : null;
        _armatureVisible = skinned;
        _armatureGeometry = skinned ? MeshScene.BuildArmature(mesh, ActiveSkeleton()) : null;

        Title = _name;
        MeshNameText.Text = _name;
        bool foundSkeleton = _automaticSkeleton is not null;
        MeshTypeText.Text = !skinned
            ? "Static mesh"
            : foundSkeleton
                ? $"Skinned mesh  ·  {mesh.Bones.Count} bones  ·  export skeleton resolved automatically"
                : $"Skinned mesh  ·  {mesh.Bones.Count} bones  ·  no export skeleton found";
        SkeletonButton.Visibility = skinned && !foundSkeleton ? Visibility.Visible : Visibility.Collapsed;
        PendingBadge.Visibility = _withChanges && Pending()?.ContainsKey(_meshId) == true ? Visibility.Visible : Visibility.Collapsed;

        UpdateRangeControl();
        BuildHierarchy();
        ShowFacts();
        UpdateOverlays();
    }

    Dictionary<ulong, byte[]>? Pending() => _changes?.PendingResources();

    SurfaceSources NewSources() => new()
    {
        Siblings = _siblings,
        Archives = _archives,
        Pending = _withChanges ? Pending() : null,
    };

    byte[] CurrentData()
    {
        byte[]? game = _siblings.FirstOrDefault(r => r.Id == _meshId && r.ClassHash == Mesh.ClassHash)?.Data;
        byte[]? pending = _withChanges ? Pending()?.GetValueOrDefault(_meshId) : null;
        return pending ?? game ?? _resourceData;
    }

    void Reload()
    {
        _renderer?.Forget(_sources.Decoded.Values.OfType<TexturePixels>());
        _sources = NewSources();
        ShowMesh(Mesh.Read(CurrentData()));
        _surfaces = MeshSurfaces.Load(_mesh, _sources, withTextures: false, _style);
        _texturesLoaded = false;
        if (_textured)
            LoadTextures();
        RequestRender();
    }

    void UpdateRangeControl()
    {
        ColourButton.IsEnabled = _parts.Count >= 2;
        ColourButton.ToolTip = ColourButton.IsEnabled
            ? "Tint separate material draw ranges when no texture is being shown"
            : "This mesh is drawn in one piece, so there are no ranges to distinguish";
    }

    void UpdateChangesButton()
    {
        ChangesButton.Visibility = _changes is not null && _changesMatter ? Visibility.Visible : Visibility.Collapsed;
    }

    void RequestRender()
    {
        if (_renderQueued || _renderer is null)
            return;

        _renderQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(Render));
    }

    void Render()
    {
        _renderQueued = false;
        if (_renderer is null)
            return;

        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Round(Stage.ActualWidth * dpi.DpiScaleX);
        int height = (int)Math.Round(Stage.ActualHeight * dpi.DpiScaleY);
        if (width < 1 || height < 1)
            return;

        if (_picture is null || _picture.PixelWidth != width || _picture.PixelHeight != height)
        {
            _picture = new WriteableBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Bgra32, null);
            Picture.Source = _picture;
        }

        bool? twoSided = _backFaces switch
        {
            BackFaces.Always => true,
            BackFaces.Never => false,
            _ => null,
        };

        try
        {
            _renderer.Render(_picture, _camera, Draws(), _textured, twoSided, _bounds, _floor);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"rendering failed: {ex.Message}";
        }
    }

    List<RenderDraw> Draws()
    {
        var draws = new List<RenderDraw>();

        for (int i = 0; i < _parts.Count; i++)
        {
            if (i >= _partVisible.Length || !_partVisible[i])
                continue;

            var surface = i < _surfaces.Count ? _surfaces[i] : null;
            draws.Add(new RenderDraw
            {
                Part = _parts[i],
                Material = surface?.Look,
                Colour = _colourRanges ? Palette[i % Palette.Length] : Palette[0],
                TwoSided = surface?.TwoSided ?? true,
                Translucent = surface?.Translucent ?? false,
            });
        }

        return draws;
    }

    void UpdateOverlays()
    {
        var overlays = new List<RenderOverlay>();

        if (_armatureVisible && _armatureGeometry is not null)
            overlays.Add(Overlay(_armatureGeometry, Color.FromRgb(0x73, 0xB7, 0xF2)));

        Rect3D selected = SelectedBounds();
        if (!selected.IsEmpty)
            overlays.Add(Overlay(MeshScene.BuildOutline(selected), Color.FromRgb(0x4D, 0xA7, 0xFF)));

        _renderer?.SetOverlays(overlays);
        RequestRender();
    }

    static RenderOverlay Overlay(MeshGeometry3D geometry, Color colour) => new()
    {
        Positions = geometry.Positions.Select(point => new Vector3((float)point.X, (float)point.Y, (float)point.Z)).ToArray(),
        Indices = geometry.TriangleIndices.ToArray(),
        Colour = colour,
    };

    List<SkeletonBone>? ActiveSkeleton() => _skeleton ?? _automaticSkeleton;

    void BuildHierarchy()
    {
        _selectedHierarchy = null;
        _hierarchy.Clear();

        var root = new MeshHierarchyItem
        {
            Name = _name,
            Detail = "Mesh",
            Kind = MeshHierarchyKind.Mesh,
        };
        var geometry = new MeshHierarchyItem
        {
            Name = "Geometry",
            Detail = $"{_parts.Count} draw range(s)",
            Kind = MeshHierarchyKind.Geometry,
            Parent = root,
        };
        root.Children.Add(geometry);

        for (int index = 0; index < _parts.Count; index++)
        {
            ulong materialId = index < _mesh.Materials.Count
                ? _mesh.Materials[index].MaterialId
                : 0;
            geometry.Children.Add(new MeshHierarchyItem
            {
                Name = $"Draw range {index + 1}",
                Detail = materialId == 0
                    ? $"{_parts[index].TriangleCount:N0} triangles"
                    : $"{_parts[index].TriangleCount:N0} triangles  ·  material 0x{materialId:X}",
                Kind = MeshHierarchyKind.DrawRange,
                Index = index,
                Parent = geometry,
                IsVisible = index < _partVisible.Length && _partVisible[index],
            });
        }

        if (_mesh.Bones.Count > 0)
        {
            List<SkeletonBone>? skeleton = ActiveSkeleton();
            root.Children.Add(new MeshHierarchyItem
            {
                Name = "Armature",
                Detail = skeleton is null
                    ? $"{_mesh.Bones.Count} weighted bones  ·  flat hierarchy"
                    : $"{_mesh.Bones.Count} weighted bones  ·  hierarchy from {skeleton.Count} bones",
                Kind = MeshHierarchyKind.Armature,
                Parent = root,
                IsVisible = _armatureVisible,
            });
        }

        RefreshGroupState(geometry);
        RefreshGroupState(root);
        _hierarchy.Add(root);
        HierarchyTree.ItemsSource = _hierarchy;
    }

    void HierarchyVisibility_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingHierarchy || sender is not System.Windows.Controls.CheckBox
            { Tag: MeshHierarchyItem item } check)
            return;

        bool visible = check.IsChecked == true;
        _updatingHierarchy = true;
        SetVisible(item, visible);
        for (MeshHierarchyItem? parent = item.Parent; parent is not null; parent = parent.Parent)
            RefreshGroupState(parent);
        _updatingHierarchy = false;
        UpdateOverlays();
        ShowSelectionFacts();
        e.Handled = true;
    }

    void SetVisible(MeshHierarchyItem item, bool visible)
    {
        item.IsVisible = visible;
        foreach (MeshHierarchyItem child in item.Children)
            SetVisible(child, visible);

        if (item.Kind == MeshHierarchyKind.DrawRange
            && (uint)item.Index < (uint)_partVisible.Length)
            _partVisible[item.Index] = visible;
        else if (item.Kind == MeshHierarchyKind.Armature)
            _armatureVisible = visible;
    }

    static void RefreshGroupState(MeshHierarchyItem item)
    {
        if (item.Children.Count == 0)
            return;
        item.IsVisible = item.Children.All(child => child.IsVisible == true) ? true
            : item.Children.All(child => child.IsVisible == false) ? false
            : null;
    }

    void Hierarchy_SelectedItemChanged(object sender,
        RoutedPropertyChangedEventArgs<object> e)
    {
        _selectedHierarchy = e.NewValue as MeshHierarchyItem;
        UpdateOverlays();
        ShowSelectionFacts();
    }

    void Hierarchy_Click(object sender, RoutedEventArgs e)
    {
        bool show = HierarchyButton.IsChecked == true;
        if (!show && HierarchyColumn.ActualWidth > 0)
            _hierarchyWidth = new GridLength(HierarchyColumn.ActualWidth);
        HierarchyPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        HierarchySplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        HierarchyColumn.Width = show ? _hierarchyWidth : new GridLength(0);
        HierarchySplitterColumn.Width = show ? new GridLength(4) : new GridLength(0);
    }

    Rect3D SelectedBounds()
    {
        if (_selectedHierarchy is null)
            return Rect3D.Empty;

        if (_selectedHierarchy.Kind == MeshHierarchyKind.DrawRange)
        {
            int index = _selectedHierarchy.Index;
            return (uint)index < (uint)_parts.Count && _partVisible[index]
                ? _parts[index].Bounds
                : Rect3D.Empty;
        }

        if (_selectedHierarchy.Kind == MeshHierarchyKind.Armature)
            return _armatureVisible && _armatureGeometry is not null
                ? _armatureGeometry.Bounds
                : Rect3D.Empty;

        var bounds = Rect3D.Empty;
        for (int index = 0; index < _parts.Count; index++)
            if (index < _partVisible.Length && _partVisible[index])
                bounds.Union(_parts[index].Bounds);
        if (_selectedHierarchy.Kind == MeshHierarchyKind.Mesh
            && _armatureVisible && _armatureGeometry is not null)
            bounds.Union(_armatureGeometry.Bounds);
        return bounds;
    }

    void ShowSelectionFacts()
    {
        if (_selectedHierarchy is null
            || _selectedHierarchy.Kind is MeshHierarchyKind.Mesh or MeshHierarchyKind.Geometry)
        {
            ShowFacts();
            return;
        }

        if (_selectedHierarchy.Kind == MeshHierarchyKind.Armature)
        {
            List<SkeletonBone>? skeleton = ActiveSkeleton();
            StatusText.Text = skeleton is null
                ? $"Armature   {_mesh.Bones.Count} weighted bones   flat hierarchy"
                : $"Armature   {_mesh.Bones.Count} weighted bones   hierarchy resolved from {skeleton.Count} skeleton bones";
            return;
        }

        int index = _selectedHierarchy.Index;
        if ((uint)index >= (uint)_parts.Count)
            return;
        ulong materialId = index < _mesh.Materials.Count
            ? _mesh.Materials[index].MaterialId
            : 0;
        StatusText.Text = $"Draw range {index + 1}   {_parts[index].VertexCount:N0} vertices   "
            + $"{_parts[index].TriangleCount:N0} triangles"
            + (materialId == 0 ? "" : $"   material 0x{materialId:X}");
    }

    void LoadTextures()
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _surfaces = MeshSurfaces.Load(_mesh, _sources, withTextures: true, _style);
            _texturesLoaded = true;
        }
        catch (Exception ex)
        {
            _textured = false;
            TextureButton.IsChecked = false;
            StatusText.Text = $"textures could not be loaded: {ex.Message}";
            MessageBox.Show(this, ex.Message, "Could not load mesh textures",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (_surfaces.TrueForAll(s => s.Look is null))
        {
            _textured = false;
            TextureButton.IsChecked = false;
            TextureButton.IsEnabled = false;
            TextureButton.Content = "No textures";
        }

        _changesMatter |= _sources.UsedPending;
        UpdateChangesButton();
        ShowFacts();
        RequestRender();
    }

    void Texture_Click(object sender, RoutedEventArgs e)
    {
        _textured = TextureButton.IsChecked == true;
        if (_textured && !_texturesLoaded)
            LoadTextures();

        ShowFacts();
        RequestRender();
    }

    async void LoadStyles()
    {
        var materials = _surfaces.Select(surface => surface.Cloth).OfType<Wildlands.Formats.Materials.Material>().ToList();
        StylePanel.Visibility = materials.Count > 0 && _archives is not null ? Visibility.Visible : Visibility.Collapsed;
        StyleSeparator.Visibility = StylePanel.Visibility;
        if (StylePanel.Visibility != Visibility.Visible)
            return;

        _fillingStyles = true;
        StyleBox.Items.Clear();
        StyleBox.Items.Add("Reading styles…");
        StyleBox.SelectedIndex = 0;
        _fillingStyles = false;

        try
        {
            _styles = await ClothStyles.For(_openedName, materials, _archives!);
        }
        catch (Exception ex)
        {
            _styles = [];
            StatusText.Text = $"styles could not be read: {ex.Message}";
        }

        _fillingStyles = true;
        StyleBox.Items.Clear();
        StyleBox.Items.Add("Material as stored");
        foreach (var style in _styles)
            StyleBox.Items.Add(style.Tag != 0 && style.Weight > 0 ? $"{style.Name}  (game default)" : style.Name);
        StyleBox.SelectedIndex = 0;
        StyleBox.IsEnabled = _styles.Count > 0;
        StyleBox.ToolTip = _styles.Count > 0
            ? $"{_styles.Count} styles from {string.Join(", ", _styles.Select(style => style.Group).Distinct())}"
            : "No customization style in the character templates uses this model";
        _fillingStyles = false;
    }

    void Style_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_fillingStyles)
            return;

        int index = StyleBox.SelectedIndex - 1;
        _style = index >= 0 && index < _styles.Count ? _styles[index] : null;
        _textured = true;
        TextureButton.IsChecked = true;
        LoadTextures();
    }

    void LoadLevels()
    {
        if (_archives is null)
            return;

        string model = Regex.Replace(_openedName, "_LOD\\d+$", "", RegexOptions.IgnoreCase);
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { model, model + "_LOD0" };
        var found = new Dictionary<string, (ForgeArchive Archive, ForgeEntry Entry)>(StringComparer.OrdinalIgnoreCase);

        foreach (var archive in _archives.All())
        {
            foreach (var entry in archive.Entries)
            {
                if (entry.FileExtension == ".data" && wanted.Contains(entry.Name))
                    found.TryAdd(entry.Name, (archive, entry));
            }
        }

        var levels = new List<MeshLevel>();
        foreach (var (archive, entry) in found.Values)
        {
            DataFile file;
            try
            {
                using var stream = new MemoryStream(archive.ReadEntry(entry));
                file = DataFile.Read(stream);
            }
            catch
            {
                continue;
            }

            foreach (var resource in file.Resources.Where(r => r.ClassHash == Mesh.ClassHash))
            {
                var match = Regex.Match(resource.Name, "_LOD(\\d+)$", RegexOptions.IgnoreCase);
                if (match.Success && resource.Name.StartsWith(model, StringComparison.OrdinalIgnoreCase))
                    levels.Add(new MeshLevel($"LOD {match.Groups[1].Value}", resource.Name, resource, file.Resources));
            }
        }

        levels = levels.DistinctBy(level => level.Name).OrderBy(level => level.Name, StringComparer.OrdinalIgnoreCase).ToList();
        int opened = levels.FindIndex(level => level.Name.Equals(_openedName, StringComparison.OrdinalIgnoreCase));
        if (levels.Count < 2 || opened < 0)
            return;

        _levels = levels;
        _fillingLevels = true;
        LodBox.ItemsSource = levels.Select(level => level.Label).ToList();
        LodBox.SelectedIndex = opened;
        LodBox.Visibility = Visibility.Visible;
        _fillingLevels = false;
    }

    void Lod_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_fillingLevels || LodBox.SelectedIndex < 0 || LodBox.SelectedIndex >= _levels.Count)
            return;

        var level = _levels[LodBox.SelectedIndex];
        bool opened = level.Name.Equals(_openedName, StringComparison.OrdinalIgnoreCase);

        _name = level.Name;
        _meshId = level.Mesh.Id;
        _siblings = opened ? _openedSiblings : level.Siblings;
        ImportButton.IsEnabled = opened && _queueImport is not null;
        ImportButton.ToolTip = opened
            ? "Replace this mesh from GLB, glTF or OBJ and queue it for Apply changes (Ctrl+R)"
            : $"Switch back to {_openedName} to replace it; other levels of detail are shown for comparison only";

        Reload();
        Fit();
    }

    void Changes_Click(object sender, RoutedEventArgs e)
    {
        _withChanges = ChangesButton.IsChecked == true;
        Reload();
        StatusText.Text = _withChanges
            ? "showing queued changes, nothing has been written to the game"
            : "showing the game files without the queued changes";
    }

    void ShowFacts()
    {
        if (_parts.Count == 0)
        {
            StatusText.Text = "nothing to draw: this mesh carries no geometry";
            return;
        }

        int triangles = 0;
        foreach (var part in _parts)
            triangles += part.TriangleCount;

        StatusText.Text =
            $"{_vertexCount:N0} vertices   {triangles:N0} triangles   " +
            $"{_parts.Count} draw range(s)   format {_mesh.VertexFormat}, stride {_mesh.VertexStride}" +
            (_mesh.Bones.Count > 0 ? $"   {_mesh.Bones.Count} bones, shown in bind pose" : "") +
            (_textured && _style is not null ? $"   style {_style.Name}" : "") +
            (_sources.Borrowed.Count == 0 ? "" : $"   completed from {string.Join(", ", _sources.Borrowed)}");

        SizeText.Text = $"{_bounds.SizeX:0.00} x {_bounds.SizeY:0.00} x {_bounds.SizeZ:0.00} m";
    }

    void Fit_Click(object sender, RoutedEventArgs e) => Fit();

    void Fit()
    {
        var bounds = _bounds;
        if (_armatureVisible && _armatureGeometry is not null)
            bounds.Union(_armatureGeometry.Bounds);
        _cameraController.Fit(bounds);
    }

    void Stage_SizeChanged(object sender, SizeChangedEventArgs e) => RequestRender();

    void Colour_Click(object sender, RoutedEventArgs e)
    {
        _colourRanges = ColourButton.IsChecked == true;
        RequestRender();
    }

    void Floor_Click(object sender, RoutedEventArgs e)
    {
        _floor = FloorButton.IsChecked == true;
        RequestRender();
    }

    void Backface_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _backFaces = BackfaceBox.SelectedIndex switch
        {
            1 => BackFaces.Always,
            2 => BackFaces.Never,
            _ => BackFaces.FromMaterial,
        };
        RequestRender();
    }

    void Skeleton_Click(object sender, RoutedEventArgs e)
    {
        if (_skeleton is not null)
        {
            _skeleton = null;
            SkeletonButton.Content = "Choose skeleton…";
            SkeletonButton.ToolTip = "Override the skeleton used when exporting GLB or FBX";
            _armatureGeometry = MeshScene.BuildArmature(_mesh, ActiveSkeleton());
            BuildHierarchy();
            UpdateOverlays();
            return;
        }

        _skeleton = SkeletonPicker.Choose(this, out string name);
        SkeletonButton.Content = _skeleton is null ? "Choose skeleton…" : name;
        SkeletonButton.ToolTip = _skeleton is null
            ? "Override the skeleton used when exporting GLB or FBX"
            : $"Export uses {name}. Click to return to automatic skeleton selection.";
        _armatureGeometry = MeshScene.BuildArmature(_mesh, ActiveSkeleton());
        BuildHierarchy();
        UpdateOverlays();
    }

    void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (_queueImport is null || !_name.Equals(_openedName, StringComparison.OrdinalIgnoreCase))
            return;

        byte[]? rebuilt = MeshImporter.Replace(this, _resourceData, _name, _settings,
            out string summary);
        if (rebuilt is null)
            return;

        try
        {
            Mesh.Read(rebuilt);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "The imported mesh cannot be previewed",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        try
        {
            _queueImport(rebuilt, summary);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not queue the mesh change",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _resourceData = rebuilt;
        _withChanges = true;
        _changesMatter = true;
        ChangesButton.IsChecked = true;
        UpdateChangesButton();
        Reload();
        Fit();

        PendingBadge.Visibility = Visibility.Visible;
        StatusText.Text = $"{summary}, waiting for Apply changes";
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        string done;
        try
        {
            done = MeshExporter.Save(this, _mesh, _name, _siblings, _skeletonIndex, _settings, _skeleton);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not export mesh",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (done.Length > 0)
            StatusText.Text = done;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.R && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            && ImportButton.IsEnabled)
            Replace_Click(ImportButton, new RoutedEventArgs());
        else if (e.Key == Key.E && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            Export_Click(this, new RoutedEventArgs());
        else if (e.Key == Key.F)
            Fit();
        else if (e.Key == Key.Escape)
            Close();
        else
            base.OnKeyDown(e);
    }
}
