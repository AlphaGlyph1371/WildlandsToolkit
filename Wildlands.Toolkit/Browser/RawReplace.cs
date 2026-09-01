using System;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Wildlands.Formats;
using Wildlands.Formats.Data;

namespace Wildlands.Toolkit;

public static class RawReplace
{
    public static byte[]? Choose(Window owner, Resource resource, AppSettings settings, out string summary)
    {
        summary = "";
        string kind = ResourceTypes.NameOf(resource.ClassHash);

        var dialog = new OpenFileDialog
        {
            Title = $"Replace {resource.Name} with a raw {kind}",
            Filter = $"{kind} resource (*.{kind};*.bin;*.dat)|*.{kind};*.bin;*.dat|All files (*.*)|*.*",
        };

        if (settings.ExportFolder.Length > 0 && Directory.Exists(settings.ExportFolder))
            dialog.InitialDirectory = settings.ExportFolder;

        if (dialog.ShowDialog(owner) != true)
            return null;

        byte[] data;

        try
        {
            data = File.ReadAllBytes(dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, ex.Message, $"Could not read {Path.GetFileName(dialog.FileName)}",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        string? complaint = ResourceCheck.Against(data, resource.Id, resource.ClassHash, resource.Name);

        if (complaint is not null && MessageBox.Show(owner,
            complaint + "\n\nUse it anyway?", "Replace resource",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return null;
        }

        settings.ExportFolder = Path.GetDirectoryName(dialog.FileName) ?? "";
        settings.Save();

        summary = $"raw {kind}, {resource.Data.Length} -> {data.Length} bytes"
            + (complaint is null ? ", checked" : ", forced past a warning");

        return data;
    }
}
