using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using Microsoft.Win32;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public sealed record AddAttachmentTemplate(int RowIndex, string DisplayName, string InternalName)
{
    public override string ToString() => DisplayName;
}

public sealed record AddAttachmentDraft(
    string DisplayName,
    string InternalName,
    AddAttachmentTemplate Template,
    bool ImportModel,
    string ModelOrAsset,
    bool AddToGunsmith,
    bool ReplaceTemplate,
    bool ReuseTemplateCategory,
    bool CreateUniqueBuildTag,
    AttachmentTextureDraft Textures);

public sealed record AttachmentTextureDraft(
    string Diffuse,
    string Normal,
    string Specular,
    string Mask1)
{
    public bool Any => Diffuse.Length > 0 || Normal.Length > 0 || Specular.Length > 0 || Mask1.Length > 0;

    public string PathFor(string slot) => slot switch
    {
        "Diffuse" => Diffuse,
        "Normal" => Normal,
        "Specular" => Specular,
        "Mask1" => Mask1,
        _ => "",
    };
}

public partial class AddAttachmentWindow : Window
{
    readonly bool _characterVest;
    ImportedGeometry? _geometry;
    List<MeshPart> _previewParts = [];
    readonly DirectionalLight _previewLight = new(Color.FromRgb(0xFF, 0xFC, 0xF5), new Vector3D(0, 0, -1));
    readonly OrbitCameraController _previewCameraController;

    public AddAttachmentWindow(string slotName, IReadOnlyList<AddAttachmentTemplate> templates, bool characterVest = false)
    {
        InitializeComponent();
        _characterVest = characterVest;
        _previewCameraController = new OrbitCameraController(PreviewStage, PreviewCamera, _previewLight);
        TitleText.Text = characterVest ? "Add vest" : "Add attachment";
        Title = TitleText.Text;
        AddButton.Content = TitleText.Text;
        SlotText.Text = slotName;
        TemplateBox.ItemsSource = templates;
        TemplateBox.SelectedIndex = templates.Count > 0 ? 0 : -1;
        if (characterVest)
        {
            ExistingAssetMode.Visibility = Visibility.Collapsed;
            ExistingAssetBox.Visibility = Visibility.Collapsed;
            ImportModelMode.IsChecked = true;
            TemplateHelpText.Text = "Copies the installed vest's proven configuration, male/female branches and gameplay registrations. The original vest is not replaced.";
            MaterialHelpText.Text = "Optional texture maps for the new vest. Empty slots keep the selected template's texture. The imported model is built into distinct male and female branches.";
            ModelImportInfo.Text = "Supported: glTF (.glb, .gltf) and OBJ (.obj). The model is cloned into both verified character gender branches.";
        }
    }

    public AddAttachmentDraft? Draft { get; private set; }

    void ModelMode_Changed(object sender, RoutedEventArgs e)
    {
        if (ImportModelMode is null || ExistingAssetBox is null || ModelPathBox is null || BrowseModelButton is null)
            return;

        bool importing = ImportModelMode.IsChecked == true;
        ExistingAssetBox.IsEnabled = !importing;
        ModelPathBox.IsEnabled = importing;
        BrowseModelButton.IsEnabled = importing;
    }

    void BrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = _characterVest ? "Choose vest model" : "Choose attachment model",
            Filter = "Supported models (*.glb;*.gltf;*.obj)|*.glb;*.gltf;*.obj|glTF (*.glb;*.gltf)|*.glb;*.gltf|Wavefront OBJ (*.obj)|*.obj",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            ImportedGeometry geometry = ReadGeometry(dialog.FileName);
            int vertices = geometry.Groups.Sum(group => group.Vertices.Count);
            int triangles = geometry.Groups.Sum(group => group.Indices.Count) / 3;
            if (vertices == 0 || triangles == 0)
                throw new InvalidDataException("The file contains no renderable triangles.");

            ModelPathBox.Text = dialog.FileName;
            _geometry = geometry;
            ModelImportInfo.Text = $"{vertices:N0} vertices, {triangles:N0} triangles, "
                + $"{geometry.Groups.Count} draw range(s). "
                + (geometry.HasSkinning
                    ? $"The file contains {geometry.JointNames.Count} joint(s)."
                    : "The model will be bound automatically to the attachment.")
                + " The imported shape is used for every LOD level cloned from the gameplay template.";
        }
        catch (Exception ex)
        {
            ModelPathBox.Clear();
            _geometry = null;
            ModelImportInfo.Text = $"Could not read this model: {ex.Message}";
            MessageBox.Show(this, ex.Message, "Could not read model", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    static ImportedGeometry ReadGeometry(string path) => Path.GetExtension(path)
        .Equals(".obj", StringComparison.OrdinalIgnoreCase)
            ? ObjFile.Read(path).ToGeometry()
            : GltfFile.Read(path);

    void Next_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateBasics(out bool importing, out string modelOrAsset))
            return;

        if (importing)
        {
            try
            {
                _geometry ??= ReadGeometry(modelOrAsset);
                _previewParts = BuildParts(_geometry);
                if (_previewParts.Count == 0)
                    throw new InvalidDataException("The model contains no renderable triangles.");
                TextureOptionsPanel.IsEnabled = true;
                BuildPreviewScene();
                PreviewFit();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Could not read model", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }
        else
        {
            _geometry = null;
            _previewParts = [];
            TextureOptionsPanel.IsEnabled = false;
            PreviewScene.Content = null;
            PreviewStatus.Text = "An existing game asset keeps its existing materials. Import a model file to assign new texture maps.";
        }

        BasicsPage.Visibility = Visibility.Collapsed;
        MaterialsPage.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Visible;
        NextButton.Visibility = Visibility.Collapsed;
        AddButton.Visibility = Visibility.Visible;
        NextButton.IsDefault = false;
        AddButton.IsDefault = true;
    }

    void Back_Click(object sender, RoutedEventArgs e)
    {
        MaterialsPage.Visibility = Visibility.Collapsed;
        BasicsPage.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        AddButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Visible;
        AddButton.IsDefault = false;
        NextButton.IsDefault = true;
    }

    void Review_Click(object sender, RoutedEventArgs e)
    {
        if (!ValidateBasics(out bool importModel, out string modelOrAsset))
            return;

        string displayName = DisplayNameBox.Text.Trim();
        string internalName = InternalNameBox.Text.Trim();
        var template = (AddAttachmentTemplate)TemplateBox.SelectedItem;
        var textures = new AttachmentTextureDraft(DiffusePathBox.Text.Trim(), NormalPathBox.Text.Trim(), SpecularPathBox.Text.Trim(), Mask1PathBox.Text.Trim());
        foreach (string path in new[] { textures.Diffuse, textures.Normal, textures.Specular, textures.Mask1 }
                     .Where(path => path.Length > 0))
            if (!File.Exists(path))
            {
                MessageBox.Show(this, $"Texture file not found:\n{path}", TitleText.Text, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

        Draft = new AddAttachmentDraft(displayName, internalName, template, importModel, modelOrAsset, AddToGunsmith: true, ReplaceTemplate: false, ReuseTemplateCategory: false, CreateUniqueBuildTag: true, textures);
        DialogResult = true;
    }

    bool ValidateBasics(out bool importing, out string modelOrAsset)
    {
        importing = ImportModelMode.IsChecked == true;
        modelOrAsset = (importing ? ModelPathBox.Text : ExistingAssetBox.Text).Trim();
        if (DisplayNameBox.Text.Trim().Length == 0 || InternalNameBox.Text.Trim().Length == 0 || TemplateBox.SelectedItem is not AddAttachmentTemplate || modelOrAsset.Length == 0)
        {
            MessageBox.Show(this, "Fill in all required fields.", TitleText.Text, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        if (importing && !File.Exists(modelOrAsset))
        {
            MessageBox.Show(this, "Choose an existing model file.", TitleText.Text, MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        return true;
    }

    void BrowseTexture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string slot })
            return;
        var dialog = new OpenFileDialog
        {
            Title = $"Choose {slot} texture",
            Filter = TextureImporter.Filter,
        };
        if (dialog.ShowDialog(this) != true)
            return;
        try
        {
            var (pixels, width, height) = TextureImporter.LoadPreview(dialog.FileName);
            BitmapSource image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            PathBoxFor(slot).Text = dialog.FileName;
            ThumbnailFor(slot).Source = image;
            if (slot == "Diffuse")
                BuildPreviewScene();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, $"Could not read {slot} texture", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void ClearTexture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string slot })
            return;
        PathBoxFor(slot).Clear();
        ThumbnailFor(slot).Source = null;
        if (slot == "Diffuse")
            BuildPreviewScene();
    }

    TextBox PathBoxFor(string slot) => slot switch
    {
        "Diffuse" => DiffusePathBox,
        "Normal" => NormalPathBox,
        "Specular" => SpecularPathBox,
        "Mask1" => Mask1PathBox,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    Image ThumbnailFor(string slot) => slot switch
    {
        "Diffuse" => DiffuseThumbnail,
        "Normal" => NormalThumbnail,
        "Specular" => SpecularThumbnail,
        "Mask1" => Mask1Thumbnail,
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    static List<MeshPart> BuildParts(ImportedGeometry geometry)
    {
        var result = new List<MeshPart>();
        foreach (ImportedGroup group in geometry.Groups)
        {
            var positions = new Point3DCollection(group.Vertices.Count);
            var normals = new Vector3DCollection(group.Vertices.Count);
            var uv = new PointCollection(group.Vertices.Count);
            foreach (MeshVertex vertex in group.Vertices)
            {
                positions.Add(new Point3D(vertex.Position.X, vertex.Position.Y, vertex.Position.Z));
                normals.Add(new Vector3D(vertex.Normal.X, vertex.Normal.Y, vertex.Normal.Z));
                uv.Add(vertex.Uv.Length > 0
                    ? new System.Windows.Point(vertex.Uv[0].X, vertex.Uv[0].Y)
                    : default);
            }
            var indices = new Int32Collection(group.Indices);
            var mesh = new MeshGeometry3D
            {
                Positions = positions,
                Normals = normals,
                TextureCoordinates = uv,
                TriangleIndices = indices,
            };
            result.Add(new MeshPart
            {
                Geometry = mesh,
                VertexCount = group.Vertices.Count,
                TriangleCount = group.Indices.Count / 3,
            });
        }
        return result;
    }

    void BuildPreviewScene()
    {
        if (_previewParts.Count == 0)
            return;
        Brush brush = new SolidColorBrush(Color.FromRgb(0xA9, 0xB8, 0xCA));
        string diffuse = DiffusePathBox.Text.Trim();
        if (diffuse.Length > 0 && File.Exists(diffuse))
        {
            var (pixels, width, height) = TextureImporter.LoadPreview(diffuse);
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            brush = new ImageBrush(image)
            {
                TileMode = TileMode.Tile,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, 1, 1),
            };
        }
        var material = new MaterialGroup();
        material.Children.Add(new DiffuseMaterial(brush));
        if (SpecularPathBox.Text.Trim().Length > 0)
            material.Children.Add(new SpecularMaterial(Brushes.White, 45));

        var scene = new Model3DGroup();
        scene.Children.Add(new AmbientLight(Color.FromRgb(0x4A, 0x4C, 0x52)));
        scene.Children.Add(_previewLight);
        foreach (MeshPart part in _previewParts)
            scene.Children.Add(new GeometryModel3D(part.Geometry, material)
            {
                BackMaterial = new DiffuseMaterial(new SolidColorBrush(Color.FromRgb(0x45, 0x4B, 0x55))),
            });
        PreviewScene.Content = scene;

        int triangles = _previewParts.Sum(part => part.TriangleCount);
        Rect3D bounds = MeshScene.Bounds(_previewParts);
        PreviewStatus.Text = $"{_previewParts.Sum(part => part.VertexCount):N0} vertices   {triangles:N0} triangles   {_previewParts.Count} material range(s)   {bounds.SizeX:0.000} × {bounds.SizeY:0.000} × {bounds.SizeZ:0.000} m";
    }

    void PreviewFit_Click(object sender, RoutedEventArgs e) => PreviewFit();

    void PreviewFit()
    {
        Rect3D bounds = MeshScene.Bounds(_previewParts);
        _previewCameraController.Fit(bounds);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
