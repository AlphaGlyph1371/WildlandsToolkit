using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wildlands.Formats.Textures;

namespace Wildlands.Toolkit;

public partial class TextureWindow : Window
{
    static readonly string[] Backgrounds = ["dark", "black", "white", "magenta", "checker"];

    TextureView _view;
    readonly AppSettings _settings;

    int _background;
    double _zoom = 1;
    Point _dragStart;
    bool _dragging;
    BitmapSource? _decoded;
    bool _invisible;

    readonly Action? _onReplace;

    public TextureWindow(TextureView view, AppSettings settings, Action? onReplace = null)
    {
        InitializeComponent();

        _view = view;
        _settings = settings;
        _onReplace = onReplace;
        UpdateHeader();

        if (onReplace is not null)
            ReplaceButton.Visibility = Visibility.Visible;

        LevelBox.ItemsSource = view.Levels;
        LevelBox.IsEnabled = view.Levels.Count > 1;

        var focus = view.Focus;
        LevelBox.SelectedItem = focus;

        if (focus is null)
            ShowNothing(TextureLoader.ExplainEmpty(view));

        Loaded += (_, _) => Fit();
    }

    void Replace_Click(object sender, RoutedEventArgs e) => _onReplace?.Invoke();

    public TextureView View => _view;

    public void ShowView(TextureView view)
    {
        _view = view;
        UpdateHeader();

        LevelBox.ItemsSource = view.Levels;
        LevelBox.IsEnabled = view.Levels.Count > 1;
        LevelBox.SelectedItem = view.Focus;

        if (view.Focus is null)
            ShowNothing(TextureLoader.ExplainEmpty(view));
    }

    void UpdateHeader()
    {
        var texture = _view.Texture;
        Title = _view.Name;
        TextureNameText.Text = _view.Name;
        TextureTypeText.Text =
            $"{texture.Width} × {texture.Height}  ·  {texture.Format}  ·  {texture.MipCount} mip level(s)  ·  " +
            $"{texture.StreamedMips.Length} streamed";
    }

    void Level_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LevelBox.SelectedItem is not TextureMipLevel level)
            return;

        _decoded = TextureLoader.Decode(_view.Texture, level, out string note);

        if (_decoded is null)
        {
            ShowNothing(note);
            return;
        }

        _invisible = TextureLoader.IsInvisibleWithAlpha(_decoded);

        SetImageCommandsEnabled(true);
        ApplyChannels();
        UpdateSource(level);
        UpdateAlphaHint();

        var texture = _view.Texture;
        StatusText.Text =
            $"{level.Width} x {level.Height}   {texture.Format}   " +
            $"level {level.Level} of {(int)texture.MipCount - 1}   " +
            $"{level.Pixels.Length:N0} B   from {level.Description}";

        SetZoom(_zoom);

        if (IsLoaded)
            Fit();
    }

    void UpdateSource(TextureMipLevel level)
    {
        SourceBanner.Visibility = Visibility.Visible;

        if (level.IsStreamed)
        {
            SourceTag.Background = (Brush)FindResource("Accent");
            SourceTagText.Text = "CompiledMip";
            SourceText.Text = $"level {level.Level} is streamed separately, loaded from {level.SourceName} (id 0x{level.SourceId:X})";
        }
        else
        {
            SourceTag.Background = (Brush)FindResource("Border");
            SourceTagText.Text = "TextureMap";
            SourceText.Text = $"level {level.Level} comes from the embedded chain of this TextureMap";
        }

        SourceTagText.Foreground = level.IsStreamed ? Brushes.Black : (Brush)FindResource("Text");
    }

    void ShowNothing(string note)
    {
        _decoded = null;
        _invisible = false;
        Picture.Source = null;
        StatusText.Text = note;
        ZoomText.Text = "";
        SourceBanner.Visibility = Visibility.Collapsed;
        AlphaHint.Visibility = Visibility.Collapsed;
        SetImageCommandsEnabled(false);
    }

    void SetImageCommandsEnabled(bool enabled)
    {
        FitButton.IsEnabled = enabled;
        ActualButton.IsEnabled = enabled;
        ZoomOutButton.IsEnabled = enabled;
        ZoomInButton.IsEnabled = enabled;
        RedToggle.IsEnabled = enabled;
        GreenToggle.IsEnabled = enabled;
        BlueToggle.IsEnabled = enabled;
        AlphaToggle.IsEnabled = enabled;
        ExportButton.IsEnabled = enabled && LevelBox.SelectedItem is TextureMipLevel;
    }

    void Channel_Click(object sender, RoutedEventArgs e)
    {
        ApplyChannels();
        UpdateAlphaHint();
    }

    void UpdateAlphaHint() => AlphaHint.Visibility = _invisible && AlphaToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    void ApplyChannels()
    {
        if (_decoded is null)
            return;

        var source = AlphaToggle.IsChecked == true ? _decoded : TextureLoader.WithoutAlpha(_decoded);

        bool red = RedToggle.IsChecked == true;
        bool green = GreenToggle.IsChecked == true;
        bool blue = BlueToggle.IsChecked == true;

        Picture.Source = red && green && blue ? source : TextureLoader.IsolateChannels(source, red, green, blue);
    }

    void Fit_Click(object sender, RoutedEventArgs e) => Fit();

    void Fit()
    {
        if (Picture.Source is not BitmapSource bitmap || Scroller.ActualWidth <= 0)
            return;

        double scale = Math.Min(
            Scroller.ActualWidth / bitmap.PixelWidth,
            Scroller.ActualHeight / bitmap.PixelHeight);

        SetZoom(Math.Min(1, scale));
    }

    void Actual_Click(object sender, RoutedEventArgs e) => SetZoom(1);

    void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);

    void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * 0.8);

    void SetZoom(double zoom)
    {
        if (Picture.Source is not BitmapSource bitmap)
            return;

        _zoom = Math.Clamp(zoom, 0.02, 32);
        Picture.Width = bitmap.PixelWidth * _zoom;
        Picture.Height = bitmap.PixelHeight * _zoom;

        RenderOptions.SetBitmapScalingMode(Picture, _zoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        ZoomText.Text = $"{_zoom * 100:0.#} %";
    }

    void Background_Click(object sender, RoutedEventArgs e)
    {
        _background = (_background + 1) % Backgrounds.Length;

        string name = Backgrounds[_background];
        Canvas.Background = (Brush)FindResource("Bg" + char.ToUpper(name[0]) + name[1..]);
        BackgroundButton.Content = $"Background: {name}";
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (LevelBox.SelectedItem is not TextureMipLevel level)
            return;

        var dialog = new ExportWindow(_view, level.Level, _settings) { Owner = this };

        if (dialog.ShowDialog() == true)
            StatusText.Text = dialog.Summary;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.R
            && ReplaceButton.Visibility == Visibility.Visible && ReplaceButton.IsEnabled)
            Replace_Click(ReplaceButton, new RoutedEventArgs());
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.E
            && ExportButton.IsEnabled)
            Save_Click(ExportButton, new RoutedEventArgs());
        else if (e.Key == Key.F)
            Fit();
        else if (e.Key == Key.Escape)
            Close();
        else
            base.OnKeyDown(e);
    }

    void Scroller_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;

        SetZoom(_zoom * (e.Delta > 0 ? 1.25 : 0.8));
        e.Handled = true;
    }

    void Scroller_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Scroller);
        _dragging = true;
        Scroller.CaptureMouse();
        Scroller.Cursor = Cursors.SizeAll;
    }

    void Scroller_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
            return;

        var now = e.GetPosition(Scroller);
        Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - (now.X - _dragStart.X));
        Scroller.ScrollToVerticalOffset(Scroller.VerticalOffset - (now.Y - _dragStart.Y));
        _dragStart = now;
    }

    void Scroller_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        Scroller.ReleaseMouseCapture();
        Scroller.Cursor = Cursors.Arrow;
    }
}
