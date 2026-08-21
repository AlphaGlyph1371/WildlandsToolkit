using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Wildlands.Formats.Textures;

namespace Wildlands.Toolkit;

public partial class ImportWindow : Window
{
    readonly TextureMap _texture;
    readonly IReadOnlyList<string> _targets;

    public string? Path { get; private set; }
    public bool GenerateMips { get; private set; } = true;

    public ImportWindow(string name, TextureMap texture, IReadOnlyList<string> targets)
    {
        InitializeComponent();

        _texture = texture;
        _targets = targets;

        TitleText.Text = name;
        TargetText.Text = $"{texture.Width} x {texture.Height}  {texture.Format}  {texture.MipCount} mip level(s), {texture.StreamedMips.Length} of them streamed";

        TargetList.ItemsSource = targets;
        SourceText.Text = "Pick a png, jpeg, bmp, tif or dds.";
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = TitleText.Text,
            Filter = TextureImporter.Filter,
        };

        if (dialog.ShowDialog() == true)
            PathBox.Text = dialog.FileName;
    }

    void Path_Changed(object sender, RoutedEventArgs e) => Describe();

    void Options_Changed(object sender, RoutedEventArgs e) => Describe();

    void Describe()
    {
        if (SourceText is null)
            return;

        ImportButton.IsEnabled = false;
        Preview.Source = null;

        string path = PathBox.Text.Trim();
        if (path.Length == 0)
            return;

        if (!File.Exists(path))
        {
            SourceText.Text = "No file at that path.";
            return;
        }

        try
        {
            var (pixels, width, height) = TextureImporter.LoadPreview(path);

            Preview.Source = BitmapSource.Create(width, height, 96, 96,
                System.Windows.Media.PixelFormats.Bgra32, null, pixels, width * 4);

            var text = new System.Text.StringBuilder();
            text.Append($"{width} x {height}");

            //if (width == (int)_texture.Width && height == (int)_texture.Height)
            //{
            //    text.Append(", the same size as the texture");
            //}
            //else
            //{
                text.Append($" - the texture becomes {width} x {height}");
            //}

            int levels = ChainLength(width, height);
            if (levels < _texture.MipCount)
            {
                text.Append(Environment.NewLine);
                text.Append($"Too small: a chain from here has {levels} levels, the texture needs {_texture.MipCount}.");
                SourceText.Text = text.ToString();
                return;
            }

            SourceText.Text = text.ToString();
            ImportButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SourceText.Text = ex.Message;
        }
    }

    static int ChainLength(int width, int height)
    {
        int levels = 1;

        while (width > 1 || height > 1)
        {
            width = Math.Max(1, width / 2);
            height = Math.Max(1, height / 2);
            levels++;
        }

        return levels;
    }

    void Import_Click(object sender, RoutedEventArgs e)
    {
        Path = PathBox.Text.Trim();
        GenerateMips = GenerateMipsBox.IsChecked == true;
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
