using System.Collections.Generic;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using Wildlands.Formats.Textures;

namespace Wildlands.Toolkit;

public partial class ExportWindow : Window
{
    sealed record FormatOption(string Name, ExportFormat Format)
    {
        public override string ToString() => Name;
    }

    sealed record LevelOption(string Name, IReadOnlyList<TextureMipLevel> Levels)
    {
        public override string ToString() => Name;
    }

    readonly TextureView _view;
    readonly AppSettings _settings;

    public string Summary { get; private set; } = "";

    public ExportWindow(TextureView view, int level, AppSettings settings)
    {
        InitializeComponent();

        _view = view;
        _settings = settings;

        TitleText.Text = view.Name;
        NameBox.Text = CleanName(view.Name);
        FolderBox.Text = settings.ExportFolder.Length > 0
            ? settings.ExportFolder
            : System.Environment.GetFolderPath(System.Environment.SpecialFolder.Desktop);

        FormatBox.ItemsSource = new[]
        {
            new FormatOption("PNG", ExportFormat.Png),
            new FormatOption("DDS", ExportFormat.Dds),
            new FormatOption("JPEG", ExportFormat.Jpeg),
            new FormatOption("BMP", ExportFormat.Bmp),
            new FormatOption("TIFF", ExportFormat.Tiff),
        };
        FormatBox.SelectedIndex = 0;

        var chosen = view.Mips.Find(level) ?? view.Mips.Best;
        var options = new List<LevelOption>();

        if (chosen is not null)
            options.Add(new LevelOption($"Level {chosen.Level} only", [chosen]));

        if (view.Levels.Count > 1)
            options.Add(new LevelOption($"All {view.Levels.Count} levels", view.Levels));

        LevelBox.ItemsSource = options;
        LevelBox.SelectedIndex = 0;
        LevelBox.IsEnabled = options.Count > 1;

        UpdateHint();
    }

    void Options_Changed(object sender, RoutedEventArgs e) => UpdateHint();

    void UpdateHint()
    {
        if (!IsInitialized)
            return;

        string folder = FolderBox.Text.Trim();
        string name = NameBox.Text.Trim();

        if (Selected() is not (var format, var levels))
            return;

        if (name.Length == 0)
        {
            HintText.Text = "The file needs a name.";
            SaveButton.IsEnabled = false;
            return;
        }

        if (folder.Length == 0)
        {
            HintText.Text = "Pick a folder to write into.";
            SaveButton.IsEnabled = false;
            return;
        }

        SaveButton.IsEnabled = true;

        string extension = TextureExporter.ExtensionOf(format);
        int files = TextureExporter.FileCount(format, levels.Count);
        var top = levels[0];

        var lines = new List<string>();

        if (files == 1)
        {
            lines.Add(levels.Count > 1 ? $"Writes {name}{extension}   {top.Size}, {levels.Count} mip levels" : $"Writes {name}{extension}   {top.Size}");
        }
        else
        {
            lines.Add($"Writes {files} files: {name}_mip{top.Level}{extension} down to {name}_mip{levels[^1].Level}{extension}");
        }

        if (format == ExportFormat.Dds)
            lines.Add($"Keeps the pixels as {_view.Texture.Format}, nothing is re-encoded.");
        else if (format == ExportFormat.Jpeg)
            lines.Add("Jpeg has no alpha channel, so transparency is dropped.");

        if (!Directory.Exists(folder))
            lines.Add("The folder does not exist yet and will be created.");

        HintText.Text = string.Join(System.Environment.NewLine, lines);
    }

    (ExportFormat Format, IReadOnlyList<TextureMipLevel> Levels)? Selected()
    {
        if (FormatBox.SelectedItem is not FormatOption format || LevelBox.SelectedItem is not LevelOption level)
            return null;

        return (format.Format, level.Levels);
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder to save into" };

        if (Directory.Exists(FolderBox.Text))
            dialog.InitialDirectory = FolderBox.Text;

        if (dialog.ShowDialog() == true)
            FolderBox.Text = dialog.FolderName;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        if (Selected() is not (var format, var levels))
            return;

        string folder = FolderBox.Text.Trim();

        try
        {
            Summary = TextureExporter.Export(_view, levels, format, folder, NameBox.Text.Trim());
        }
        catch (System.Exception ex)
        {
            HintText.Text = $"Could not write: {ex.Message}";
            return;
        }

        _settings.ExportFolder = folder;
        _settings.Save();

        DialogResult = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    static string CleanName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');

        return name;
    }
}
