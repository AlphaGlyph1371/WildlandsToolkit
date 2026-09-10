using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;

namespace Wildlands.Toolkit;

public sealed class BrowserItem : INotifyPropertyChanged
{
    string _changeMark = "";

    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Type { get; init; } = "";
    public long Size { get; init; }
    public ulong Id { get; init; }

    public ForgeEntry? Entry { get; init; }
    public Resource? Resource { get; init; }

    public Geometry Icon => TypeIcon.For(Type);

    public string SizeText => FormatSize(Size);
    public string IdText => Id == 0 ? "" : $"0x{Id:X}";

    public bool CanOpen => Entry?.FileExtension == ".data";

    public string ChangeMark
    {
        get => _changeMark;
        private set
        {
            if (_changeMark == value)
                return;
            _changeMark = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChangeMarkVisibility));
        }
    }

    public Visibility ChangeMarkVisibility => ChangeMark.Length == 0
        ? Visibility.Collapsed
        : Visibility.Visible;

    public void SetChangeMark(string? mark) => ChangeMark = mark ?? "";

    public static BrowserItem FromEntry(ForgeEntry entry) => new()
    {
        Index = entry.Index,
        Name = entry.Name,
        Type = entry.FileExtension.TrimStart('.'),
        Size = entry.Length,
        Id = entry.Id,
        Entry = entry,
    };

    public static BrowserItem FromResource(Resource resource, int index) => new()
    {
        Index = index,
        Name = resource.Name,
        Type = ResourceTypes.NameOf(resource.ClassHash),
        Size = resource.Data.Length,
        Id = resource.Id,
        Resource = resource,
    };

    static string FormatSize(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        if (bytes < 1024 * 1024)
            return $"{bytes / 1024.0:0.0} KB";
        if (bytes < 1024L * 1024 * 1024)
            return $"{bytes / (1024.0 * 1024):0.0} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):0.00} GB";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
