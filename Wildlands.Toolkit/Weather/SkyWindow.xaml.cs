using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Wildlands.Formats.Models;
using Wildlands.Formats.Textures;

namespace Wildlands.Toolkit;

public sealed class SkyTile
{
    public required BitmapSource Thumbnail { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public Brush Outline { get; init; } = Brushes.Transparent;
}

public partial class SkyWindow : Window
{
    const int SkyColourSlice = 12;

    readonly LayeredSkyAsset _sky;
    readonly string _name;
    readonly Action<byte[]> _save;
    int _layer;
    int _slice = SkyColourSlice;
    bool _loading;

    public SkyWindow(LayeredSkyAsset sky, string name, Action<byte[]> save)
    {
        InitializeComponent();
        _sky = sky;
        _name = name;
        _save = save;

        NameText.Text = name;
        LayeredSkyLayer first = sky.Layers[0];
        DetailText.Text = $"{sky.Layers.Count} moments across the day, each holding "
            + $"{first.Width * first.Height * first.Depth} colours.";
        SetStatus("Pick a time of day, move the sliders, press Apply, then Save.");
        RefreshLayers();
        LayerList.SelectedIndex = 0;
    }

    SkyAdjust Adjust => new((float)ExposureSlider.Value, (float)RedSlider.Value,
        (float)GreenSlider.Value, (float)BlueSlider.Value, (float)SaturationSlider.Value);

    LayeredSkyLayer Current => _sky.Layers[_layer];

    void RefreshLayers()
    {
        int keep = LayerList.SelectedIndex;
        LayerList.ItemsSource = _sky.Layers.Select(layer => new SkyTile
        {
            Thumbnail = SkyImage.Render(layer, Math.Min(SkyColourSlice, SkyImage.SliceCount(layer) - 1),
                SkyAdjust.None, 1),
            Title = $"{layer.Hour:0.#} h",
            Detail = $"brightness {SkyImage.Brightness(layer, Math.Min(SkyColourSlice, SkyImage.SliceCount(layer) - 1)):0.000}",
        }).ToList();
        if (keep >= 0 && keep < _sky.Layers.Count)
            LayerList.SelectedIndex = keep;
    }

    void RefreshSlices()
    {
        LayeredSkyLayer layer = Current;
        int slices = SkyImage.SliceCount(layer);
        if (RawBox.IsChecked == true)
            SliceStrip.ItemsSource = Enumerable.Range(0, slices).Select(slice => new SkyTile
            {
                Thumbnail = SkyImage.Render(layer, slice, Adjust, 1),
                Title = slice.ToString(),
                Outline = slice == _slice ? (Brush)FindResource("Accent") : Brushes.Transparent,
            }).ToList();
        BigImage.Source = SkyImage.Render(layer, Math.Min(_slice, slices - 1), Adjust, 6);
        PreviewCaption.Text = $"2. SEE WHAT IT LOOKS LIKE — {Current.Hour:0.#} h";
    }

    void Raw_Click(object sender, RoutedEventArgs e)
    {
        SliceStrip.Visibility = RawBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshSlices();
    }

    void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Adjust.IsNone)
            return;

        if (ScopeAll.IsChecked == true)
        {
            SkyAdjust adjust = Adjust;
            foreach (LayeredSkyLayer layer in _sky.Layers)
                SkyImage.Bake(layer, adjust);
            SetStatus($"Written into all {_sky.Layers.Count} times of day.");
        }
        else
        {
            SkyImage.Bake(Current, Adjust);
            SetStatus($"Written into the {Current.Hour:0.#} h sky.");
        }

        Reset_Click(this, e);
        RefreshLayers();
        MarkDirty();
    }

    public void SelectLayer(int index)
    {
        if (index >= 0 && index < _sky.Layers.Count)
            LayerList.SelectedIndex = index;
    }

    void Layer_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (LayerList.SelectedIndex < 0)
            return;
        _layer = LayerList.SelectedIndex;
        _loading = true;
        HourBox.Text = Current.Hour.ToString("0.###", CultureInfo.InvariantCulture);
        _loading = false;
        RefreshSlices();
    }

    void Adjust_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded || _loading)
            return;
        ExposureLabel.Text = $"Brightness  {ExposureSlider.Value:0.00}";
        RedLabel.Text = $"Red  {RedSlider.Value:0.00}";
        GreenLabel.Text = $"Green  {GreenSlider.Value:0.00}";
        BlueLabel.Text = $"Blue  {BlueSlider.Value:0.00}";
        SaturationLabel.Text = $"Colour strength  {SaturationSlider.Value:0.00}";
        ApplyButton.IsEnabled = !Adjust.IsNone;
        RefreshSlices();
    }

    void Reset_Click(object sender, RoutedEventArgs e)
    {
        _loading = true;
        ExposureSlider.Value = 1;
        RedSlider.Value = 1;
        GreenSlider.Value = 1;
        BlueSlider.Value = 1;
        SaturationSlider.Value = 1;
        _loading = false;
        Adjust_Changed(this, new RoutedPropertyChangedEventArgs<double>(1, 1));
    }

    void Hour_Key(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            Hour_Click(this, new RoutedEventArgs());
    }

    void Hour_Click(object sender, RoutedEventArgs e)
    {
        if (!float.TryParse(HourBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture,
                out float hour) || hour < 0 || hour >= 24)
        {
            SetStatus("The hour has to be a number from 0 up to but not including 24.");
            return;
        }

        _sky.Layers[_layer] = WithHour(Current, hour);
        SetStatus($"Layer set to {hour:0.###} h.");
        RefreshLayers();
        MarkDirty();
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export this layer",
            Filter = "DirectDraw surface (*.dds)|*.dds",
            FileName = $"{_name}_{Current.Hour.ToString("00.0", CultureInfo.InvariantCulture)
                .Replace('.', '_')}h.dds",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        using var file = File.Create(dialog.FileName);
        DdsWriter.Write(file, Wildlands.Formats.Textures.PixelFormat.R16G16B16A16Float,
        [
            new TextureMipLevel
            {
                Level = 0,
                Width = Current.Width,
                Height = Current.Height * SkyImage.SliceCount(Current),
                Pixels = Current.Texels,
            },
        ]);
        SetStatus($"Wrote {Path.GetFileName(dialog.FileName)}.");
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Replace this layer",
            Filter = "DirectDraw surface (*.dds)|*.dds",
        };
        if (dialog.ShowDialog(this) != true)
            return;

        DdsImage image;
        try
        {
            using var file = File.OpenRead(dialog.FileName);
            image = DdsReader.Read(file);
        }
        catch (Exception error)
        {
            MessageBox.Show(this, error.Message, "Layered sky",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int wanted = Current.Height * SkyImage.SliceCount(Current);
        if (image.Format != Wildlands.Formats.Textures.PixelFormat.R16G16B16A16Float
            || image.Width != Current.Width || image.Height != wanted)
        {
            MessageBox.Show(this, $"That file is {image.Width} x {image.Height} in {image.Format}, "
                + $"this layer needs {Current.Width} x {wanted} in R16G16B16A16Float.",
                "Layered sky", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        byte[] texels = image.Level(0);
        if (texels.Length != Current.Texels.Length)
        {
            MessageBox.Show(this, $"That file holds {texels.Length} byte(s) of texels, "
                + $"this layer needs {Current.Texels.Length}.", "Layered sky",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        texels.CopyTo(Current.Texels, 0);
        SetStatus($"Read {Path.GetFileName(dialog.FileName)} into the {Current.Hour:0.#} h layer.");
        RefreshLayers();
        RefreshSlices();
        MarkDirty();
    }

    void Copy_Click(object sender, RoutedEventArgs e)
    {
        var pick = new SkyLayerPicker(_sky, _layer) { Owner = this };
        if (pick.ShowDialog() != true || pick.Chosen < 0)
            return;

        LayeredSkyLayer source = _sky.Layers[pick.Chosen];
        if (source.Texels.Length != Current.Texels.Length)
        {
            SetStatus("Those two layers do not hold the same number of texels.");
            return;
        }

        source.Texels.CopyTo(Current.Texels, 0);
        SetStatus($"Copied the {source.Hour:0.#} h texels into the {Current.Hour:0.#} h layer.");
        RefreshLayers();
        RefreshSlices();
        MarkDirty();
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        _save(LayeredSky.Write(_sky));
        SaveButton.IsEnabled = false;
        SetStatus("On the change list. Apply in the main window writes it into the game.");
    }

    void MarkDirty() => SaveButton.IsEnabled = true;

    void SetStatus(string text) => StatusText.Text = text;

    static LayeredSkyLayer WithHour(LayeredSkyLayer layer, float hour) => new()
    {
        Hour = hour,
        Hash = layer.Hash,
        RowPitch = layer.RowPitch,
        Height = layer.Height,
        Width = layer.Width,
        Unknown = layer.Unknown,
        BytesPerTexel = layer.BytesPerTexel,
        HeaderTail = layer.HeaderTail,
        Texels = layer.Texels,
        Padding = layer.Padding,
    };
}
