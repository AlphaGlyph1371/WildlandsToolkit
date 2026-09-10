using System.Collections.Generic;
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
        Title = name;
        MeshNameText.Text = name;
        bool isSkinned = mesh.Bones.Count > 0;
        bool foundSkeleton = isSkinned
            && SkeletonFinder.Find(siblings, mesh.Bones, skeletonIndex) is not null;
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
        _surfaces = MeshSurfaces.Load(mesh, siblings, archives, withTextures: false, out _borrowed);
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

        Scene.Content = group;
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
            return;
        }

        _skeleton = SkeletonPicker.Choose(this, out string name);
        SkeletonButton.Content = _skeleton is null ? "Choose skeleton…" : name;
        SkeletonButton.ToolTip = _skeleton is null
            ? "Override the skeleton used when exporting GLB or FBX"
            : $"Export uses {name}. Click to return to automatic skeleton selection.";
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
        _texturesLoaded = false;
        _textured = false;
        _borrowed = null;
        TextureButton.IsChecked = false;
        TextureButton.IsEnabled = true;
        TextureButton.Content = "Textures";
        _surfaces = MeshSurfaces.Load(_mesh, _siblings, _archives,
            withTextures: false, out _borrowed);
        UpdateRangeControl();
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
