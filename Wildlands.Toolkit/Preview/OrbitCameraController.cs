using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Media3D;

namespace Wildlands.Toolkit;

sealed class OrbitCameraController
{
    readonly FrameworkElement _stage;
    readonly PerspectiveCamera _camera;
    readonly DirectionalLight? _light;
    Point3D _target;
    double _distance = 10;
    double _yaw = -2.2;
    double _pitch = 0.45;
    Point _dragStart;
    bool _orbiting;
    bool _panning;

    public OrbitCameraController(FrameworkElement stage, PerspectiveCamera camera,
        DirectionalLight? light = null)
    {
        _stage = stage;
        _camera = camera;
        _light = light;
        stage.MouseLeftButtonDown += MouseDown;
        stage.MouseRightButtonDown += MouseDown;
        stage.MouseMove += MouseMove;
        stage.MouseLeftButtonUp += MouseUp;
        stage.MouseRightButtonUp += MouseUp;
        stage.MouseWheel += MouseWheel;
        stage.LostMouseCapture += (_, _) =>
        {
            StopMoving();
            _stage.Cursor = Cursors.Arrow;
        };
    }

    public void Fit(Rect3D bounds)
    {
        if (bounds.IsEmpty)
            return;

        _target = new Point3D(bounds.X + bounds.SizeX / 2,
            bounds.Y + bounds.SizeY / 2, bounds.Z + bounds.SizeZ / 2);
        double radius = new Vector3D(bounds.SizeX, bounds.SizeY, bounds.SizeZ).Length / 2;
        double halfFieldOfView = _camera.FieldOfView / 2 * Math.PI / 180;
        _distance = Math.Max(radius / Math.Sin(halfFieldOfView) * 1.1, 0.1);
        UpdateCamera();
    }

    void UpdateCamera()
    {
        var direction = new Vector3D(Math.Cos(_pitch) * Math.Cos(_yaw),
            Math.Cos(_pitch) * Math.Sin(_yaw), Math.Sin(_pitch));
        _camera.Position = _target + direction * _distance;
        _camera.LookDirection = -direction;
        _camera.NearPlaneDistance = Math.Max(_distance / 100, 0.0001);
        if (_light is not null)
        {
            var (right, up) = Axes(direction);
            _light.Direction = -direction - up * 0.4 + right * 0.3;
        }
    }

    static (Vector3D Right, Vector3D Up) Axes(Vector3D direction)
    {
        var right = Vector3D.CrossProduct(direction, new Vector3D(0, 0, 1));
        right.Normalize();
        var up = Vector3D.CrossProduct(right, direction);
        up.Normalize();
        return (right, up);
    }

    void MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(_stage);
        _orbiting = e.ChangedButton == MouseButton.Left;
        _panning = e.ChangedButton == MouseButton.Right;
        _stage.CaptureMouse();
        _stage.Cursor = _panning ? Cursors.SizeAll : Cursors.ScrollAll;
        e.Handled = true;
    }

    void MouseMove(object sender, MouseEventArgs e)
    {
        if (!_orbiting && !_panning)
            return;

        Point now = e.GetPosition(_stage);
        Vector moved = now - _dragStart;
        _dragStart = now;
        if (_orbiting)
        {
            _yaw -= moved.X * 0.008;
            _pitch = Math.Clamp(_pitch + moved.Y * 0.008, -1.5, 1.5);
        }
        else
        {
            var (right, up) = Axes(_camera.LookDirection);
            double scale = _distance * 0.0015;
            _target += right * (moved.X * scale) + up * (moved.Y * scale);
        }
        UpdateCamera();
    }

    void MouseUp(object sender, MouseButtonEventArgs e)
    {
        StopMoving();
        _stage.ReleaseMouseCapture();
        _stage.Cursor = Cursors.Arrow;
        e.Handled = true;
    }

    void MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance * (e.Delta > 0 ? 0.85 : 1 / 0.85), 0.001, 100000);
        UpdateCamera();
        e.Handled = true;
    }

    void StopMoving()
    {
        _orbiting = false;
        _panning = false;
    }
}
