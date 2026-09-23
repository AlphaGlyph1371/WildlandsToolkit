using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using Wildlands.Formats.Materials;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

enum BackFaces { FromMaterial, Always, Never }

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
    byte[] _resourceData;
    readonly string _name;
    readonly AppSettings _settings;
    readonly Action<byte[], string>? _queueImport;
    List<MeshPart> _parts;
    readonly List<Resource> _siblings;
    readonly ArchiveSet? _archives;
    readonly SkeletonIndex? _skeletonIndex;

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
    bool _texturesLoaded;
    string? _borrowed;
    bool _textured;
    readonly DirectionalLight _light = new(Color.FromRgb(0xFF, 0xFC, 0xF5), new Vector3D(0, 0, -1));

    bool _colourRanges = true;
    BackFaces _backFaces = BackFaces.FromMaterial;
    readonly OrbitCameraController _cameraController;

    public MeshWindow(Mesh mesh, byte[] resourceData, string name, List<Resource> siblings,
        ArchiveSet? archives, SkeletonIndex? skeletonIndex, AppSettings settings,
        Action<byte[], string>? queueImport = null, bool hasPendingChange = false)
    {
        InitializeComponent();
        _cameraController = new OrbitCameraController(Stage, Camera, _light);

        _mesh = mesh;
        _resourceData = (byte[])resourceData.Clone();
        _name = name;
        _siblings = siblings;
        _archives = archives;
        _skeletonIndex = skeletonIndex;
        _settings = settings;
        _queueImport = queueImport;
        if (mesh.IsOpaque)
            throw new InvalidDataException("This Mesh uses an unknown object-pointer layout. It can be packaged unchanged, but cannot yet be previewed or edited.");
        Title = name;
        MeshNameText.Text = name;
        bool isSkinned = mesh.Bones.Count > 0;
        _automaticSkeleton = isSkinned
            ? SkeletonFinder.Find(siblings, mesh.Bones, skeletonIndex)
            : null;
        bool foundSkeleton = _automaticSkeleton is not null;
        MeshTypeText.Text = !isSkinned
            ? "Static mesh"
            : foundSkeleton
                ? $"Skinned mesh  ·  {mesh.Bones.Count} bones  ·  export skeleton resolved automatically"
                : $"Skinned mesh  ·  {mesh.Bones.Count} bones  ·  no export skeleton found";
        SkeletonButton.Visibility = isSkinned && !foundSkeleton
            ? Visibility.Visible
            : Visibility.Collapsed;
        PendingBadge.Visibility = hasPendingChange ? Visibility.Visible : Visibility.Collapsed;

        ImportButton.IsEnabled = queueImport is not null;
        if (queueImport is null)
            ImportButton.ToolTip = "Import is available when the viewer was opened from a concrete Mesh resource inside a data container.";

        _parts = MeshScene.Build(mesh);
        _partVisible = Enumerable.Repeat(true, _parts.Count).ToArray();
        _armatureGeometry = isSkinned
            ? MeshScene.BuildArmature(mesh, ActiveSkeleton())
            : null;
        _surfaces = MeshSurfaces.Load(mesh, siblings, archives, withTextures: false, out _borrowed);
        BuildHierarchy();
        BuildScene();
        ShowFacts();
        UpdateRangeControl();

        Loaded += (_, _) => Fit();
    }

    void UpdateRangeControl()
    {
        ColourButton.IsEnabled = _parts.Count >= 2;
        ColourButton.ToolTip = ColourButton.IsEnabled
            ? "Tint separate material draw ranges when no texture is being shown"
            : "This mesh is drawn in one piece, so there are no ranges to distinguish";
    }

    void BuildScene()
    {
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(0x4A, 0x4C, 0x52)));
        group.Children.Add(_light);

        for (int i = 0; i < _parts.Count; i++)
        {
            if (i >= _partVisible.Length || !_partVisible[i])
                continue;

            var colour = _colourRanges ? Palette[i % Palette.Length] : Palette[0];

            var surface = i < _surfaces.Count ? _surfaces[i] : null;
            var brush = _textured ? surface?.Texture : null;
            var front = brush ?? Paint(colour);

            var model = new GeometryModel3D(_parts[i].Geometry, new DiffuseMaterial(front));

            bool twoSided = _backFaces switch
            {
                BackFaces.Always => true,
                BackFaces.Never => false,
                _ => surface?.TwoSided ?? true,
            };

            if (twoSided)
                model.BackMaterial = new DiffuseMaterial(brush ?? Paint(Shade(colour, 0.45)));

            group.Children.Add(model);
        }

        if (_armatureVisible && _armatureGeometry is not null)
        {
            var armatureBrush = Paint(Color.FromRgb(0x73, 0xB7, 0xF2));
            var armature = new GeometryModel3D(_armatureGeometry,
                new EmissiveMaterial(armatureBrush))
            {
                BackMaterial = new EmissiveMaterial(armatureBrush),
            };
            group.Children.Add(armature);
        }

        Rect3D selected = SelectedBounds();
        if (!selected.IsEmpty)
        {
            var accent = Paint(Color.FromRgb(0x4D, 0xA7, 0xFF));
            var outline = new GeometryModel3D(MeshScene.BuildOutline(selected),
                new EmissiveMaterial(accent))
            {
                BackMaterial = new EmissiveMaterial(accent),
            };
            group.Children.Add(outline);
        }

        Scene.Content = group;
    }

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
        BuildScene();
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
        BuildScene();
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
                ? _parts[index].Geometry.Bounds
                : Rect3D.Empty;
        }

        if (_selectedHierarchy.Kind == MeshHierarchyKind.Armature)
            return _armatureVisible && _armatureGeometry is not null
                ? _armatureGeometry.Bounds
                : Rect3D.Empty;

        var bounds = Rect3D.Empty;
        for (int index = 0; index < _parts.Count; index++)
            if (index < _partVisible.Length && _partVisible[index])
                bounds.Union(_parts[index].Geometry.Bounds);
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

    void Texture_Click(object sender, RoutedEventArgs e)
    {
        if (!_texturesLoaded)
        {
            Mouse.OverrideCursor = Cursors.Wait;
            try
            {
                _surfaces = MeshSurfaces.Load(_mesh, _siblings, _archives, withTextures: true, out _borrowed);
                _texturesLoaded = true;
            }
            catch (Exception ex)
            {
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

            if (_surfaces.TrueForAll(s => s.Texture is null))
            {
                StatusText.Text = "no textures found for this mesh";
                TextureButton.IsChecked = false;
                TextureButton.IsEnabled = false;
                TextureButton.Content = "No textures";
                return;
            }

            ShowFacts();
        }

        _textured = TextureButton.IsChecked == true;
        BuildScene();
    }

    static Brush Paint(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    static Color Shade(Color colour, double factor) => Color.FromRgb((byte)(colour.R * factor), (byte)(colour.G * factor), (byte)(colour.B * factor));

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
            $"{_parts[0].Geometry.Positions.Count:N0} vertices   {triangles:N0} triangles   " +
            $"{_parts.Count} draw range(s)   format {_mesh.VertexFormat}, stride {_mesh.VertexStride}" +
            (_mesh.Bones.Count > 0 ? $"   {_mesh.Bones.Count} bones, shown in bind pose" : "") +
            (_borrowed is null ? "" : $"   completed from {_borrowed}");

        var bounds = MeshScene.Bounds(_parts);
        SizeText.Text = $"{bounds.SizeX:0.00} x {bounds.SizeY:0.00} x {bounds.SizeZ:0.00} m";
    }

    void Fit_Click(object sender, RoutedEventArgs e) => Fit();

    void Fit()
    {
        var bounds = MeshScene.Bounds(_parts);
        if (_armatureVisible && _armatureGeometry is not null)
            bounds.Union(_armatureGeometry.Bounds);
        _cameraController.Fit(bounds);
    }

    void Colour_Click(object sender, RoutedEventArgs e)
    {
        _colourRanges = ColourButton.IsChecked == true;
        BuildScene();
    }

    void Backface_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        _backFaces = BackfaceBox.SelectedIndex switch
        {
            1 => BackFaces.Always,
            2 => BackFaces.Never,
            _ => BackFaces.FromMaterial,
        };
        if (IsLoaded)
            BuildScene();
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
            BuildScene();
            return;
        }

        _skeleton = SkeletonPicker.Choose(this, out string name);
        SkeletonButton.Content = _skeleton is null ? "Choose skeleton…" : name;
        SkeletonButton.ToolTip = _skeleton is null
            ? "Override the skeleton used when exporting GLB or FBX"
            : $"Export uses {name}. Click to return to automatic skeleton selection.";
        _armatureGeometry = MeshScene.BuildArmature(_mesh, ActiveSkeleton());
        BuildHierarchy();
        BuildScene();
    }

    void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (_queueImport is null)
            return;

        byte[]? rebuilt = MeshImporter.Replace(this, _resourceData, _name, _settings,
            out string summary);
        if (rebuilt is null)
            return;

        Mesh imported;
        List<MeshPart> parts;
        try
        {
            imported = Mesh.Read(rebuilt);
            parts = MeshScene.Build(imported);
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

        _mesh = imported;
        _resourceData = rebuilt;
        _parts = parts;
        _partVisible = Enumerable.Repeat(true, _parts.Count).ToArray();
        _automaticSkeleton = _mesh.Bones.Count > 0
            ? SkeletonFinder.Find(_siblings, _mesh.Bones, _skeletonIndex)
            : null;
        _armatureVisible = _mesh.Bones.Count > 0;
        _armatureGeometry = _mesh.Bones.Count > 0
            ? MeshScene.BuildArmature(_mesh, ActiveSkeleton())
            : null;
        _texturesLoaded = false;
        _textured = false;
        _borrowed = null;
        TextureButton.IsChecked = false;
        TextureButton.IsEnabled = true;
        TextureButton.Content = "Textures";
        _surfaces = MeshSurfaces.Load(_mesh, _siblings, _archives,
            withTextures: false, out _borrowed);
        UpdateRangeControl();
        BuildHierarchy();
        BuildScene();
        ShowFacts();
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
