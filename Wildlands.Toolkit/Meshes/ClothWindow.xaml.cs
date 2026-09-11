using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
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
            return common == _text ? $"the same, {common}" : $"almost all use {common}";
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
    readonly string _name;
    readonly OrbitCameraController? _camera;
    readonly DirectionalLight _light = new(Colors.White, new Vector3D(-0.4, -0.7, -0.6));

    public ClothWindow(SoftBodySettingsAsset settings, ClothAsset? cloth, string name,
        List<SkeletonBone>? bones, Action<byte[]> save)
    {
        InitializeComponent();
        _settings = settings;
        _bones = bones;
        _save = save;
        _name = name;

        NameText.Text = cloth is null
            ? name
            : $"{name}  —  {cloth.Faces.Count()} faces, {cloth.Links.Count()} links";

        RefreshFields();
        RefreshBones();
        AddBoneButton.IsEnabled = bones is not null && bones.Count > 0;

        if (bones is null || bones.Count == 0)
        {
            NoSkeletonText.Visibility = Visibility.Visible;
        }
        else
        {
            _camera = new OrbitCameraController(Stage, Camera, _light);
            _camera.SetOrbit(-0.9, 0.12);
            RenderSkeleton(-1);
        }

        SetStatus("Nothing changed yet.");
    }

    void RefreshBones()
    {
        int keep = BoneList.SelectedIndex;
        BoneList.ItemsSource = _settings.Bones
            .Select((hash, index) => $"{index,2}   0x{hash:X8}{Suffix(hash)}").ToList();
        if (keep >= 0 && keep < _settings.Bones.Count)
            BoneList.SelectedIndex = keep;
        RemoveBoneButton.IsEnabled = BoneList.SelectedIndex >= 0;

        if (_bones is not null && _bones.Count > 0)
        {
            int known = _settings.Bones.Count(hash => _bones.Any(bone => bone.Name == hash));
            BoneHint.Text = $"{_settings.Bones.Count} bone(s), {known} of them on this skeleton. "
                + "Blue is the whole list, orange is the one you picked.";
        }

        int differ = SoftBodySettings.Fields.Count(field => !IsCommon(field));
        DiffText.Text = differ == 0
            ? "Every value here is already the one almost all garments use."
            : $"This garment goes its own way in {differ} of {SoftBodySettings.Fields.Count} values.";
    }

    bool IsCommon(SoftBodyField field)
    {
        double value = field.Kind switch
        {
            SoftBodyFieldKind.Float => _settings.GetFloat(field),
            SoftBodyFieldKind.Byte => _settings.GetByte(field),
            _ => _settings.GetUInt32(field),
        };
        return Math.Abs(value - field.Common) < 1e-6;
    }

    string Suffix(uint hash) => _bones is not null && _bones.Any(bone => bone.Name == hash)
        ? ""
        : "   (not on this skeleton)";

    void RefreshFields()
    {
        bool all = AllFieldsBox.IsChecked == true;
        FieldList.ItemsSource = SoftBodySettings.Fields
            .Where(field => all || field.Varies)
            .Select(field => new SoftBodyRow(_settings, field, Changed))
            .ToList();
    }

    void Raw_Toggled(object sender, RoutedEventArgs e) => RefreshFields();

    void AllFields_Click(object sender, RoutedEventArgs e) => RefreshFields();

    void Bone_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        RemoveBoneButton.IsEnabled = BoneList.SelectedIndex >= 0;
        RenderSkeleton(BoneList.SelectedIndex);
    }

    void RemoveBone_Click(object sender, RoutedEventArgs e)
    {
        int index = BoneList.SelectedIndex;
        if (index < 0 || index >= _settings.Bones.Count)
            return;

        uint hash = _settings.Bones[index];
        _settings.Bones.RemoveAt(index);
        Changed();
        RenderSkeleton(-1);
        SetStatus($"Took bone 0x{hash:X8} out.");
    }

    void AddBone_Click(object sender, RoutedEventArgs e)
    {
        if (_bones is null)
            return;

        var pick = new ClothBonePicker(_bones, _settings.Bones) { Owner = this };
        if (pick.ShowDialog() != true || pick.Chosen == 0)
            return;

        _settings.Bones.Add(pick.Chosen);
        _settings.Bones.Sort();
        Changed();
        RenderSkeleton(-1);
        SetStatus($"Added bone 0x{pick.Chosen:X8}.");
    }

    void ExportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save these cloth settings",
            Filter = "Cloth settings (*.softbody)|*.softbody",
            FileName = _name + ".softbody",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        File.WriteAllBytes(dialog.FileName, SoftBodySettings.Write(_settings));
        SetStatus($"Wrote {Path.GetFileName(dialog.FileName)}.");
    }

    void ImportSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Load cloth settings",
            Filter = "Cloth settings (*.softbody)|*.softbody|All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        SoftBodySettingsAsset source;
        try
        {
            source = SoftBodySettings.Read(File.ReadAllBytes(dialog.FileName));
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Cloth", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        source.Fixed.CopyTo(_settings.Fixed, 0);
        bool bones = WithBonesBox.IsChecked == true;
        if (bones)
        {
            _settings.Bones.Clear();
            _settings.Bones.AddRange(source.Bones);
        }

        RefreshFields();
        Changed();
        RenderSkeleton(-1);
        SetStatus($"Took the behaviour from {Path.GetFileName(dialog.FileName)}"
            + (bones ? ", bones and all." : "."));
    }

    void Defaults_Click(object sender, RoutedEventArgs e)
    {
        foreach (SoftBodyField field in SoftBodySettings.Fields)
        {
            switch (field.Kind)
            {
                case SoftBodyFieldKind.Float:
                    _settings.SetFloat(field, (float)field.Common);
                    break;
                case SoftBodyFieldKind.Byte:
                    _settings.SetByte(field, (byte)field.Common);
                    break;
                default:
                    _settings.SetUInt32(field, (uint)field.Common);
                    break;
            }
        }

        RefreshFields();
        Changed();
        SetStatus("Every value is now the one almost all garments use.");
    }

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
        Add(group, pose, index => !wanted.Contains(_bones[index].Name), 0x40, 0x40, 0x4A);
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

    void Changed()
    {
        SaveButton.IsEnabled = true;
        RefreshBones();
    }

    void SetStatus(string text) => StatusText.Text = text;
}
