using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public sealed class SoftBodyRow : INotifyPropertyChanged
{
    readonly SoftBodySettingsAsset _asset;
    readonly SoftBodyField _field;
    readonly Action _changed;
    string _text;

    public SoftBodyRow(SoftBodySettingsAsset asset, SoftBodyField field, Action changed)
    {
        _asset = asset;
        _field = field;
        _changed = changed;
        _text = Read();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name => _field.Name;

    public string Note
    {
        get
        {
            string common = _field.Common.ToString("0.#####", CultureInfo.InvariantCulture);
            return common == _text ? $"same as most ({common})" : $"most use {common}";
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value)
                return;
            _text = value;
            if (Write(value))
                _changed();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Note)));
        }
    }

    string Read() => _field.Kind switch
    {
        SoftBodyFieldKind.Float => _asset.GetFloat(_field).ToString("0.#####", CultureInfo.InvariantCulture),
        SoftBodyFieldKind.Byte => _asset.GetByte(_field).ToString(CultureInfo.InvariantCulture),
        _ => _asset.GetUInt32(_field).ToString(CultureInfo.InvariantCulture),
    };

    bool Write(string value)
    {
        switch (_field.Kind)
        {
            case SoftBodyFieldKind.Float:
                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float number))
                    return false;
                _asset.SetFloat(_field, number);
                return true;
            case SoftBodyFieldKind.Byte:
                if (!byte.TryParse(value, out byte small))
                    return false;
                _asset.SetByte(_field, small);
                return true;
            default:
                if (!uint.TryParse(value, out uint wide))
                    return false;
                _asset.SetUInt32(_field, wide);
                return true;
        }
    }
}

public partial class ClothWindow : Window
{
    readonly SoftBodySettingsAsset _settings;
    readonly List<SkeletonBone>? _bones;
    readonly Action<byte[]> _save;
    readonly OrbitCameraController? _camera;
    readonly DirectionalLight _light = new(Colors.White, new Vector3D(-0.4, -0.7, -0.6));

    public ClothWindow(SoftBodySettingsAsset settings, ClothAsset? cloth, string name,
        List<SkeletonBone>? bones, Action<byte[]> save)
    {
        InitializeComponent();
        _settings = settings;
        _bones = bones;
        _save = save;

        NameText.Text = name;
        DetailText.Text = cloth is null
            ? $"{settings.Bones.Count} bone(s), {SoftBodySettings.Fields.Count} setting(s)"
            : $"{cloth.Faces.Count()} face(s), {cloth.Links.Count()} link(s), "
                + $"{cloth.Objects.Count} object(s) — {settings.Bones.Count} bone(s), "
                + $"{SoftBodySettings.Fields.Count} setting(s)";

        RefreshFields();
        BoneList.ItemsSource = settings.Bones
            .Select((hash, index) => $"{index,2}  0x{hash:X8}{Suffix(hash)}").ToList();

        if (bones is null || bones.Count == 0)
        {
            NoSkeletonText.Visibility = Visibility.Visible;
            BoneHint.Text = $"{settings.Bones.Count} bone name hash(es).";
        }
        else
        {
            _camera = new OrbitCameraController(Stage, Camera, _light);
            _camera.SetOrbit(-0.9, 0.12);
            int known = settings.Bones.Count(hash => bones.Any(bone => bone.Name == hash));
            BoneHint.Text = $"{known} of {settings.Bones.Count} found on this skeleton, drawn in colour.";
            RenderSkeleton(-1);
        }

        SetStatus("Change a value, then Save to put it on the change list.");
    }

    string Suffix(uint hash) => _bones is not null && _bones.Any(bone => bone.Name == hash)
        ? "  (on this skeleton)"
        : "";

    void RefreshFields()
    {
        bool all = AllFieldsBox.IsChecked == true;
        FieldList.ItemsSource = SoftBodySettings.Fields
            .Where(field => all || field.Varies)
            .Select(field => new SoftBodyRow(_settings, field, MarkDirty))
            .ToList();
    }

    void AllFields_Click(object sender, RoutedEventArgs e) => RefreshFields();

    void Bone_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        RenderSkeleton(BoneList.SelectedIndex);

    void RenderSkeleton(int highlight)
    {
        if (_bones is null || _camera is null)
            return;

        var pose = new Matrix4x4[_bones.Count];
        for (int i = 0; i < _bones.Count; i++)
        {
            Matrix4x4 local = Matrix4x4.CreateFromQuaternion(_bones[i].LocalRotation);
            local.Translation = _bones[i].LocalPosition;
            pose[i] = _bones[i].ParentIndex >= 0 && _bones[i].ParentIndex < i
                ? local * pose[_bones[i].ParentIndex]
                : local;
        }

        var wanted = _settings.Bones.ToHashSet();
        uint single = highlight >= 0 && highlight < _settings.Bones.Count
            ? _settings.Bones[highlight]
            : 0;

        var group = new Model3DGroup();
        group.Children.Add(new AmbientLight(Color.FromRgb(0x3A, 0x3A, 0x42)));
        group.Children.Add(_light);
        Add(group, pose, index => !wanted.Contains(_bones[index].Name), 0x4A, 0x4A, 0x55);
        Add(group, pose, index => wanted.Contains(_bones[index].Name)
            && _bones[index].Name != single, 0x5A, 0x9A, 0xE8);
        if (single != 0)
            Add(group, pose, index => _bones[index].Name == single, 0xF0, 0xA0, 0x40);
        Scene.Content = group;
        _camera.Fit(AnimationScene.Bounds(pose));
    }

    void Add(Model3DGroup group, Matrix4x4[] pose, Func<int, bool> keep, byte red, byte green, byte blue)
    {
        MeshGeometry3D geometry = AnimationScene.Build(pose, _bones!, keep);
        if (geometry.TriangleIndices.Count == 0)
            return;
        group.Children.Add(new GeometryModel3D(geometry,
            new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(red, green, blue)))));
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        _save(SoftBodySettings.Write(_settings));
        SaveButton.IsEnabled = false;
        SetStatus("On the change list. Apply in the main window writes it into the game.");
    }

    void MarkDirty()
    {
        SaveButton.IsEnabled = true;
        SetStatus("Edited. Save puts it on the change list.");
    }

    void SetStatus(string text) => StatusText.Text = text;
}
