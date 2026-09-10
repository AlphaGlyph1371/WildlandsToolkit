using System.Windows;
using System.Windows.Media.Media3D;

namespace Wildlands.Toolkit;

public partial class BuildTableModelPreviewWindow : Window
{
    readonly Model3D _model;
    readonly OrbitCameraController _cameraController;

    public BuildTableModelPreviewWindow(Model3D model, string name, string details)
    {
        InitializeComponent();
        _model = model;
        Title = $"{name} - 3D preview";
        ModelNameText.Text = name;
        ModelDetailsText.Text = details;
        PreviewScene.Content = model;
        _cameraController = new OrbitCameraController(PreviewStage, PreviewCamera);
        Loaded += (_, _) => Fit();
    }

    void Fit_Click(object sender, RoutedEventArgs e) => Fit();

    void Fit() => _cameraController.Fit(_model.Bounds);
}
