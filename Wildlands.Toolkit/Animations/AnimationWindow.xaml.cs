using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public partial class AnimationWindow : Window
{
    readonly AnimationAsset _animation;
    readonly OrbitCameraController _camera;
    readonly DirectionalLight _light = new(Color.FromRgb(0xFF, 0xFF, 0xFF), new Vector3D(-0.4, -0.7, -0.6));
    readonly Stopwatch _clock = new();
    readonly DiffuseMaterial _material = new(new SolidColorBrush(Color.FromRgb(0xC8, 0xCC, 0xD4)));

    List<SkeletonBone> _bones;
    string _skeletonName;
    AnimationPlayer _player;
    double _speed = 1;
    bool _playing = true;
    bool _following;
    float _time;

    public AnimationWindow(AnimationAsset animation, string name,
        List<SkeletonBone> bones, string skeletonName)
    {
        InitializeComponent();
        _animation = animation;
        _bones = bones;
        _skeletonName = skeletonName;
        _player = new AnimationPlayer(animation, bones);
        _camera = new OrbitCameraController(Stage, Camera, _light);
        _camera.SetOrbit(-0.9, 0.12);

        NameText.Text = name;
        TimeSlider.Maximum = Math.Max(animation.Duration, 0.0001);
        Describe();
        Render(0);
        Fit();
        _clock.Start();
        CompositionTarget.Rendering += Tick;
        Closed += (_, _) => CompositionTarget.Rendering -= Tick;
        KeyDown += OnKey;
    }

    void Describe()
    {
        DetailText.Text = $"{_animation.Duration:0.###} s, {_animation.Tracks.Count} track(s) on "
            + $"{_skeletonName} with {_bones.Count} bone(s) — {_player.Matched} track(s) drive a bone, "
            + $"{_player.Unmatched} name no bone here, {_player.Unreadable} use a format that is not read yet";
    }

    void Tick(object? sender, EventArgs e)
    {
        if (!_playing)
        {
            _clock.Restart();
            return;
        }

        double elapsed = _clock.Elapsed.TotalSeconds * _speed;
        _clock.Restart();
        if (_animation.Duration <= 0)
            return;

        _time = (float)((_time + elapsed) % _animation.Duration);
        _following = true;
        TimeSlider.Value = _time;
        _following = false;
        Render(_time);
    }

    void Render(float seconds)
    {
        IReadOnlyList<Matrix4x4> pose = _player.Evaluate(seconds);
        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(0x3A, 0x3A, 0x42)));
        group.Children.Add(_light);
        group.Children.Add(new GeometryModel3D(AnimationScene.Build(pose, _bones), _material));
        Scene.Content = group;
        TimeText.Text = $"{seconds:0.00} / {_animation.Duration:0.00} s";
    }

    void Fit()
    {
        var bounds = AnimationScene.Bounds(_player.Evaluate(_time));
        if (bounds.IsEmpty)
            return;
        _camera.Fit(new Rect3D(bounds.X - 0.1, bounds.Y - 0.1, bounds.Z - 0.1,
            bounds.SizeX + 0.2, bounds.SizeY + 0.2, bounds.SizeZ + 0.2));
    }

    public void Scrub(float seconds)
    {
        _playing = false;
        PlayButton.Content = "Play";
        _time = seconds;
        TimeSlider.Value = seconds;
        Render(seconds);
    }

    void Fit_Click(object sender, RoutedEventArgs e) => Fit();

    void Play_Click(object sender, RoutedEventArgs e)
    {
        _playing = !_playing;
        PlayButton.Content = _playing ? "Pause" : "Play";
        _clock.Restart();
    }

    void Speed_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        _speed = SpeedBox.SelectedIndex switch { 0 => 0.1, 1 => 0.5, 3 => 2.0, _ => 1.0 };

    void Time_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_following && Math.Abs(e.NewValue - _time) >= 1e-4)
            Scrub((float)e.NewValue);
    }

    void Skeleton_Click(object sender, RoutedEventArgs e)
    {
        List<SkeletonBone>? picked = SkeletonPicker.Choose(this, out string name);
        if (picked is null || picked.Count == 0)
            return;

        _bones = picked;
        _skeletonName = name;
        _player = new AnimationPlayer(_animation, picked);
        Describe();
        Render(_time);
        Fit();
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            Play_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.F)
        {
            Fit();
            e.Handled = true;
        }
    }
}
