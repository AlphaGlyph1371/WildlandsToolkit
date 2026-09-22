using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Wildlands.Toolkit;

public enum MeshHierarchyKind
{
    Mesh,
    Geometry,
    DrawRange,
    Armature,
}

public sealed class MeshHierarchyItem : INotifyPropertyChanged
{
    bool? _isVisible = true;

    public required string Name { get; init; }
    public string Detail { get; init; } = "";
    public required MeshHierarchyKind Kind { get; init; }
    public int Index { get; init; } = -1;
    public MeshHierarchyItem? Parent { get; set; }
    public ObservableCollection<MeshHierarchyItem> Children { get; } = [];

    public bool? IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value)
                return;
            _isVisible = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
