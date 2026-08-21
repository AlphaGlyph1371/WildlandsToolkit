using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using Wildlands.Formats.Data;
using Wildlands.Formats.Models;

namespace Wildlands.Toolkit;

public static class SkeletonPicker
{
    public static List<SkeletonBone>? Choose(Window owner, out string name)
    {
        name = "";

        var dialog = new OpenFileDialog
        {
            Title = "Pick the data file holding the skeleton",
            Filter = "Data files (*.data)|*.data|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(owner) != true)
            return null;

        Resource? resource;
        try
        {
            resource = DataFile.Read(dialog.FileName).Resources.FirstOrDefault(r => r.ClassHash == Skeleton.ClassHash);
        }
        catch
        {
            MessageBox.Show(owner, "That file could not be read.", "Skeleton",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        if (resource is null)
        {
            MessageBox.Show(owner, "There is no skeleton in that file.", "Skeleton",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        name = resource.Name;
        return Skeleton.Read(resource.Data);
    }
}
