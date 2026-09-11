using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public partial class ClothBonePicker : Window
{
    readonly List<uint> _hashes;

    public ClothBonePicker(List<SkeletonBone> skeleton, List<uint> already)
    {
        InitializeComponent();
        _hashes = skeleton.Select(bone => bone.Name).Distinct()
            .Where(hash => !already.Contains(hash)).Order().ToList();
        List.ItemsSource = _hashes.Select(hash => $"0x{hash:X8}").ToList();
        List.SelectedIndex = 0;
    }

    public uint Chosen { get; private set; }

    void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedIndex >= 0)
            Chosen = _hashes[List.SelectedIndex];
        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
