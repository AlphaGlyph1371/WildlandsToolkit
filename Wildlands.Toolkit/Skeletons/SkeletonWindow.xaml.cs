using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public partial class SkeletonWindow : Window
{
    SkeletonAsset _skeleton;
    byte[] _resourceData;
    readonly string _name;
    readonly AppSettings _settings;
    readonly Action<byte[], string>? _queueImport;
    readonly OrbitCameraController _camera;
    readonly DirectionalLight _light = new(Color.FromRgb(0xFF, 0xFC, 0xF5), new Vector3D(0, 0, -1));
    IReadOnlyList<Matrix4x4> _pose;

    public SkeletonWindow(byte[] resourceData, string name, AppSettings settings,
        Action<byte[], string>? queueImport = null, bool hasPendingChange = false)
    {
        InitializeComponent();
        _resourceData = (byte[])resourceData.Clone();
        _skeleton = Skeleton.ReadAsset(_resourceData);
        _pose = SkeletonScene.Pose(_skeleton);
        _name = name;
        _settings = settings;
        _queueImport = queueImport;
        _camera = new OrbitCameraController(Stage, Camera, _light);
        _camera.SetOrbit(-0.9, 0.12);

        Title = name;
        SkeletonNameText.Text = name;
        SkeletonTypeText.Text = $"Skeleton  ·  {_skeleton.Bones.Count} bones";
        PendingBadge.Visibility = hasPendingChange ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.IsEnabled = queueImport is not null;
        if (queueImport is null)
            ImportButton.ToolTip = "Import is available when the viewer was opened from a concrete Skeleton resource inside a data container.";

        BuildScene();
        ShowFacts();
        Loaded += (_, _) => Fit();
        KeyDown += OnKey;
    }

    void BuildScene()
    {
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(0x48, 0x4A, 0x50)));
        group.Children.Add(_light);

        var bones = SkeletonScene.BuildSkeleton(_pose, _skeleton.Bones);
        group.Children.Add(new GeometryModel3D(bones,
            Paint(Color.FromRgb(0xC8, 0xCC, 0xD4))));

        if (AxesButton.IsChecked == true)
        {
            var axes = SkeletonScene.BuildAxes(_pose);
            group.Children.Add(new GeometryModel3D(axes.X, Paint(Color.FromRgb(0xED, 0x55, 0x55))));
            group.Children.Add(new GeometryModel3D(axes.Y, Paint(Color.FromRgb(0x62, 0xC9, 0x73))));
            group.Children.Add(new GeometryModel3D(axes.Z, Paint(Color.FromRgb(0x58, 0x91, 0xE8))));
        }

        Scene.Content = group;
    }

    static DiffuseMaterial Paint(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var material = new DiffuseMaterial(brush);
        material.Freeze();
        return material;
    }

    void ShowFacts()
    {
        int roots = _skeleton.Bones.Count(bone => bone.ParentIndex < 0);
        int modifiers = _skeleton.Bones.Sum(bone => bone.Modifiers.Count);
        StatusText.Text = $"{_skeleton.Bones.Count:N0} bones   {roots:N0} root(s)   {modifiers:N0} modifier(s)";

        Rect3D bounds = AnimationScene.Bounds(_pose);
        SizeText.Text = bounds.IsEmpty
            ? "no bounds"
            : $"{bounds.SizeX:0.00} x {bounds.SizeY:0.00} x {bounds.SizeZ:0.00} m";
    }

    void Fit_Click(object sender, RoutedEventArgs e) => Fit();

    void Fit()
    {
        Rect3D bounds = AnimationScene.Bounds(_pose);
        if (bounds.IsEmpty)
            return;

        double padding = Math.Max(new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length * 0.06, 0.02);
        _camera.Fit(new Rect3D(bounds.X - padding, bounds.Y - padding, bounds.Z - padding,
            bounds.SizeX + padding * 2, bounds.SizeY + padding * 2, bounds.SizeZ + padding * 2));
    }

    void Axes_Click(object sender, RoutedEventArgs e) => BuildScene();

    void Export_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string done = SkeletonInterchange.Export(this, _resourceData, _name, _settings);
            if (done.Length > 0)
                StatusText.Text = done;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not export skeleton",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_queueImport is null)
            return;

        byte[]? rebuilt = SkeletonInterchange.Import(this, _resourceData, _name,
            _settings, out string summary);
        if (rebuilt is null)
            return;

        SkeletonAsset imported;
        try
        {
            imported = Skeleton.ReadAsset(rebuilt);
            _queueImport(rebuilt, summary);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not queue the skeleton change",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _resourceData = rebuilt;
        _skeleton = imported;
        _pose = SkeletonScene.Pose(imported);
        SkeletonTypeText.Text = $"Skeleton  ·  {_skeleton.Bones.Count} bones";
        BuildScene();
        ShowFacts();
        Fit();
        PendingBadge.Visibility = Visibility.Visible;
        StatusText.Text = $"{summary}, waiting for Apply changes";
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F)
            return;

        Fit();
        e.Handled = true;
    }
}
