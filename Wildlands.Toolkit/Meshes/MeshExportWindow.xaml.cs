using System.Collections.Generic;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public partial class MeshExportWindow : Window
{
    sealed record FormatOption(string Name, string Extension, MeshExportFormat Format)
    {
        public override string ToString() => Name;
    }

    readonly Mesh _mesh;
    readonly string _name;
    readonly AppSettings _settings;
    readonly bool _isSkinned;
    List<SkeletonBone>? _skeleton;
    string _skeletonSource;
    bool _wantSkin;
    bool _updating;

    public string Summary { get; private set; } = "";

    public MeshExportWindow(Mesh mesh, string name, List<Resource> siblings,
        SkeletonIndex? skeletonIndex, AppSettings settings,
        List<SkeletonBone>? chosenSkeleton = null)
    {
        InitializeComponent();
        _mesh = mesh;
        _name = name;
        _settings = settings;

        var layout = VertexLayout.For(mesh.VertexFormat, mesh.VertexStride);
        _isSkinned = layout.IsSkinned && mesh.Bones.Count > 0;
        _wantSkin = _isSkinned;
        _skeleton = chosenSkeleton ?? (_isSkinned
            ? SkeletonFinder.Find(siblings, mesh.Bones, skeletonIndex)
            : null);
        _skeletonSource = chosenSkeleton is not null ? "Chosen skeleton"
            : _skeleton is not null ? "Matching skeleton found automatically"
            : "No matching skeleton hierarchy found";

        TitleText.Text = name;
        MeshInfoText.Text = $"{mesh.Data.Standard.Count} draw range(s)  ·  "
            + $"{layout.UvCount} UV set(s)"
            + (layout.HasColor ? "  ·  vertex colours" : "")
            + (_isSkinned ? $"  ·  {mesh.Bones.Count} weighted bones" : "  ·  static mesh");
        NameBox.Text = CleanName(Path.GetFileNameWithoutExtension(name));
        FolderBox.Text = settings.ExportFolder.Length > 0
            ? settings.ExportFolder
            : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        FormatBox.ItemsSource = new[]
        {
            new FormatOption("glTF binary (.glb) — recommended", ".glb", MeshExportFormat.Glb),
            new FormatOption("FBX binary (.fbx)", ".fbx", MeshExportFormat.Fbx),
            new FormatOption("Wavefront OBJ (.obj) — geometry only", ".obj", MeshExportFormat.Obj),
        };
        FormatBox.SelectedIndex = 0;
        UpdateOptions();
    }

    void Options_Changed(object sender, RoutedEventArgs e)
    {
        if (IsInitialized)
            UpdateOptions();
    }

    void Skeleton_Changed(object sender, RoutedEventArgs e)
    {
        if (_updating)
            return;
        _wantSkin = SkeletonBox.IsChecked == true;
        UpdateOptions();
    }

    void UpdateOptions()
    {
        if (FormatBox.SelectedItem is not FormatOption format)
            return;

        bool supportsSkin = format.Format is MeshExportFormat.Glb or MeshExportFormat.Fbx;
        bool includeSkin = _isSkinned && supportsSkin && _wantSkin;
        _updating = true;
        SkeletonBox.IsChecked = includeSkin;
        SkeletonBox.IsEnabled = _isSkinned && supportsSkin;
        ChooseSkeletonButton.IsEnabled = includeSkin;
        _updating = false;

        string folder = FolderBox.Text.Trim();
        string name = NameBox.Text.Trim();
        var lines = new List<string>();

        if (name.Length == 0)
            lines.Add("The file needs a name.");
        else if (folder.Length == 0)
            lines.Add("Pick a folder to write into.");
        else
            lines.Add($"Writes {Path.GetFileNameWithoutExtension(name)}{format.Extension}");

        if (format.Format == MeshExportFormat.Obj)
        {
            lines.Add("OBJ stores geometry, normals and one UV set. It cannot store an armature or skin weights.");
        }
        else if (!_isSkinned)
        {
            lines.Add("This is a static mesh and has no skeleton or skin weights.");
        }
        else if (!includeSkin)
        {
            lines.Add("Exports static geometry. The armature and all skin weights are omitted.");
        }
        else if (_skeleton is null)
        {
            lines.Add($"Includes all {_mesh.Bones.Count} weighted bones and skin weights, but no parent hierarchy was found.");
            lines.Add("Choose the matching Skeleton to export a connected armature.");
        }
        else
        {
            int matched = _skeleton.Count(bone => _mesh.Bones.Any(meshBone => meshBone.Name == bone.Name));
            lines.Add($"Includes all {_mesh.Bones.Count} weighted bones, skin weights and inverse-bind matrices.");
            lines.Add($"{_skeletonSource}: {matched} mesh bones matched in a {_skeleton.Count}-bone Skeleton.");
        }

        if (folder.Length > 0 && !Directory.Exists(folder))
            lines.Add("The folder does not exist yet and will be created.");

        HintText.Text = string.Join(Environment.NewLine, lines);
        SaveButton.IsEnabled = name.Length > 0 && folder.Length > 0;
    }

    void ChooseSkeleton_Click(object sender, RoutedEventArgs e)
    {
        List<SkeletonBone>? picked = SkeletonPicker.Choose(this, out string pickedName);
        if (picked is null || picked.Count == 0)
            return;

        _skeleton = picked;
        _skeletonSource = $"Chosen skeleton {pickedName}";
        UpdateOptions();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to save into" };
        if (Directory.Exists(FolderBox.Text))
            dialog.InitialDirectory = FolderBox.Text;
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (FormatBox.SelectedItem is not FormatOption format)
            return;

        string folder = FolderBox.Text.Trim();
        string fileName = Path.GetFileNameWithoutExtension(NameBox.Text.Trim()) + format.Extension;
        string path = Path.Combine(folder, fileName);
        bool includeSkin = SkeletonBox.IsChecked == true
            && format.Format is MeshExportFormat.Glb or MeshExportFormat.Fbx;

        if (File.Exists(path) && MessageBox.Show(this,
                $"{fileName} already exists. Replace it?", "Export mesh",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            Directory.CreateDirectory(folder);
            Summary = MeshExporter.Export(_mesh, _name, path, format.Format,
                includeSkin, includeSkin ? _skeleton : null);
        }
        catch (Exception ex)
        {
            HintText.Text = $"Could not write: {ex.Message}";
            return;
        }

        _settings.ExportFolder = folder;
        _settings.Save();
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    static string CleanName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
