using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Wildlands.Toolkit;

public partial class ChangeListWindow : Window
{
    readonly Func<ChangeListState> _getState;
    readonly Func<IReadOnlyList<PendingChange>, Task> _remove;
    readonly Func<ChangeOperationRow, Task> _open;
    readonly Func<Task> _apply;
    bool _busy;
    bool _interactionEnabled = true;

    public ChangeListWindow(Func<ChangeListState> getState,
        Func<IReadOnlyList<PendingChange>, Task> remove,
        Func<ChangeOperationRow, Task> open,
        Func<Task> apply)
    {
        InitializeComponent();
        _getState = getState;
        _remove = remove;
        _open = open;
        _apply = apply;
        Refresh();
    }

    public void Refresh()
    {
        if (_busy)
            return;

        string[] selectedKeys = OperationList.SelectedItems.OfType<ChangeOperationRow>()
            .Select(row => row.Key).ToArray();
        ChangeListState state = _getState();
        var rows = BuildRows(state);
        OperationList.ItemsSource = rows;
        foreach (ChangeOperationRow row in rows.Where(row => selectedKeys.Contains(row.Key)))
            OperationList.SelectedItems.Add(row);

        int pending = rows.Count(row => row.IsPending);
        CountText.Text = state.ProjectName is null
            ? rows.Count == 1 ? "1 pending change" : $"{rows.Count} pending changes"
            : $"{Amount(rows.Count, "project change")} · {Amount(pending, "pending")}";
        DescriptionText.Text = state.ProjectName is null
            ? "Nothing is written to the game until you click Apply changes."
            : $"Changes in {state.ProjectName} stay in its project folder. Only pending changes need to be applied.";
        EmptyText.Text = state.ProjectName is null
            ? "No changes are queued."
            : "This project does not contain any changes.";
        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtons();
    }

    public void SetInteractionEnabled(bool enabled)
    {
        _interactionEnabled = enabled;
        UpdateButtons();
    }

    static List<ChangeOperationRow> BuildRows(ChangeListState state)
    {
        var rows = new List<ChangeOperationRow>();
        var representedProjectKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (state.ProjectOperations.Count > 0)
        {
            foreach (IGrouping<string, ModOperation> group in state.ProjectOperations.GroupBy(
                         operation => string.IsNullOrWhiteSpace(operation.OperationGroupId)
                             ? operation.Id : operation.OperationGroupId,
                         StringComparer.OrdinalIgnoreCase))
            {
                List<ModOperation> operations = group.ToList();
                var keys = operations.Select(operation => operation.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                representedProjectKeys.UnionWith(keys);
                List<PendingChange> pending = state.PendingChanges.Where(change =>
                        change.ProjectOperationKey is { } key && keys.Contains(key))
                    .ToList();
                rows.Add(new ChangeOperationRow(group.Key, pending, operations));
            }
        }

        rows.AddRange(state.PendingChanges.Where(change =>
                change.ProjectOperationKey is null
                || !representedProjectKeys.Contains(change.ProjectOperationKey))
            .Select((change, index) => new
            {
                Change = change,
                Key = change.OperationGroupId ?? change.ProjectOperationKey ?? $"legacy-{index}",
            })
            .GroupBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ChangeOperationRow(group.Key,
                group.Select(item => item.Change).ToList(), [])));

        return rows;
    }

    void OperationList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    void UpdateButtons()
    {
        var selected = OperationList.SelectedItems.OfType<ChangeOperationRow>().ToList();
        bool hasPending = OperationList.Items.OfType<ChangeOperationRow>().Any(row => row.IsPending);
        RemoveButton.IsEnabled = _interactionEnabled && !_busy
            && selected.Any(row => row.IsPending);
        RemoveAllButton.IsEnabled = _interactionEnabled && !_busy && hasPending;
        ApplyButton.IsEnabled = _interactionEnabled && !_busy && hasPending;
    }

    async void OperationList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_interactionEnabled && !_busy)
            await OpenSelected();
    }

    async void OperationList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || !_interactionEnabled || _busy)
            return;

        e.Handled = true;
        await OpenSelected();
    }

    async Task OpenSelected()
    {
        if (OperationList.SelectedItem is not ChangeOperationRow row || !row.CanOpen)
            return;
        await _open(row);
    }

    async void Remove_Click(object sender, RoutedEventArgs e)
    {
        var changes = OperationList.SelectedItems.OfType<ChangeOperationRow>()
            .SelectMany(row => row.Changes).ToList();
        await Remove(changes);
    }

    async void RemoveAll_Click(object sender, RoutedEventArgs e)
    {
        var changes = OperationList.Items.OfType<ChangeOperationRow>()
            .SelectMany(row => row.Changes).ToList();
        int count = OperationList.Items.OfType<ChangeOperationRow>().Count(row => row.IsPending);
        if (count == 0 || MessageBox.Show(this,
                count == 1 ? "Remove the pending change?" : $"Remove all {count} pending changes?",
                "Remove changes", MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        await Remove(changes);
    }

    static string Amount(int count, string noun) => count == 1
        ? $"1 {noun}"
        : $"{count} {noun}s";

    async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyButton.IsEnabled)
            return;

        _busy = true;
        UpdateButtons();
        try
        {
            await _apply();
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    async Task Remove(IReadOnlyList<PendingChange> changes)
    {
        if (changes.Count == 0)
            return;
        _busy = true;
        UpdateButtons();
        try
        {
            await _remove(changes);
        }
        finally
        {
            _busy = false;
            Refresh();
        }
    }

    void Close_Click(object sender, RoutedEventArgs e) => Close();
}

public sealed class ChangeOperationRow
{
    public ChangeOperationRow(string key, IReadOnlyList<PendingChange> changes,
        IReadOnlyList<ModOperation> projectOperations)
    {
        Key = key;
        Changes = changes;
        ProjectOperations = projectOperations;
        Label = projectOperations.Select(operation => operation.OperationLabel)
            .Concat(changes.Select(change => change.OperationLabel))
            .FirstOrDefault(label => !string.IsNullOrWhiteSpace(label))
            ?? "Queued change";

        Target = Summarize(projectOperations.Select(operation =>
                operation.ResourceName.Default(operation.EntryName))
            .Concat(changes.Select(change => change.ResourceName)), "resources");
        Archive = Summarize(projectOperations.Select(operation =>
                Path.GetFileName(operation.Archive))
            .Concat(changes.Select(change => Path.GetFileName(change.ArchivePath))), "archives");
        Status = changes.Count > 0 ? "Pending" : "Saved in project";
        RevealChange = changes.FirstOrDefault(change => change.EntryAddition is null
            && change.EntryIndex >= 0 && change.ResourceIndex >= 0);
        RevealOperation = projectOperations.FirstOrDefault();
    }

    public string Key { get; }
    public IReadOnlyList<PendingChange> Changes { get; }
    public IReadOnlyList<ModOperation> ProjectOperations { get; }
    public string Label { get; }
    public string Status { get; }
    public string Target { get; }
    public string Archive { get; }
    public PendingChange? RevealChange { get; }
    public ModOperation? RevealOperation { get; }
    public bool IsPending => Changes.Count > 0;
    public bool CanOpen => RevealChange is not null || RevealOperation is not null;

    static string Summarize(IEnumerable<string> values, string plural)
    {
        string[] distinct = values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return distinct.Length switch
        {
            0 => "—",
            1 => distinct[0],
            _ => $"{distinct.Length} {plural}",
        };
    }
}

public sealed record ChangeListState(
    IReadOnlyList<PendingChange> PendingChanges,
    IReadOnlyList<ModOperation> ProjectOperations,
    string? ProjectName);
