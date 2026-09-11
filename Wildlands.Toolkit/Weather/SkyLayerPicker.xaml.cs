using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public partial class SkyLayerPicker : Window
{
    readonly List<int> _indices = [];

    public SkyLayerPicker(LayeredSkyAsset sky, int exclude)
    {
        InitializeComponent();
        var tiles = new List<SkyTile>();
        for (int i = 0; i < sky.Layers.Count; i++)
        {
            if (i == exclude)
                continue;
            LayeredSkyLayer layer = sky.Layers[i];
            _indices.Add(i);
            tiles.Add(new SkyTile
            {
                Thumbnail = SkyImage.Render(layer,
                    System.Math.Min(12, SkyImage.SliceCount(layer) - 1), SkyAdjust.None, 1),
                Title = $"{layer.Hour:0.#} h",
            });
        }

        List.ItemsSource = tiles;
        List.SelectedIndex = 0;
    }

    public int Chosen { get; private set; } = -1;

    void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedIndex >= 0)
            Chosen = _indices[List.SelectedIndex];
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
