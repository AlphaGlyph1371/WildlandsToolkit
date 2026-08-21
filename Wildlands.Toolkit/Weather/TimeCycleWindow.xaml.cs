using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Wildlands.Formats.Weather;

namespace Wildlands.Toolkit;

public partial class TimeCycleWindow : Window
{
    readonly TimeCycle _cycle;
    readonly byte[] _original;
    readonly string _name;
    readonly Action<byte[]> _save;

    readonly List<CycleRow> _rows = [];
    bool _dirty;

    public TimeCycleWindow(TimeCycle cycle, string name, Action<byte[]> save)
    {
        InitializeComponent();

        _cycle = cycle;
        _name = name;
        _save = save;
        _original = cycle.Write();

        Title = $"Time cycle - {name}";

        for (int i = 0; i < cycle.Entries.Count; i++)
            _rows.Add(new CycleRow(i, cycle.Entries[i]));

        ShowEntries();
        SetStatus($"{cycle.Entries.Count} entries. Edit a value, then Save to put it on the change list.");
    }

    void ShowEntries()
    {
        string filter = FilterBox.Text.Trim();
        bool onlyCurves = VaryingToggle.IsChecked == true;

        EntryList.ItemsSource = _rows.Where(row =>
            (filter.Length == 0 || row.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            && (!onlyCurves || row.Varies)).ToList();
    }

    void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded)
            ShowEntries();
    }

    void Entry_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (EntryList.SelectedItem is not CycleRow row)
        {
            KeyList.ItemsSource = null;
            EntryTitle.Text = "Nothing selected";
            EntryNote.Text = "";
            AddKeyButton.IsEnabled = false;
            RemoveKeyButton.IsEnabled = false;
            return;
        }

        EntryTitle.Text = row.Path;
        EntryNote.Text = $"{row.Entry.Kind}, {row.Entry.Components} number(s) per key. " +
                         (_cycle.IsWeather
                            ? "A weather curve runs 0% to 100%, it is not a time of day."
                            : "The day runs 00:00:00 to 24:00:00, and the last key repeats the first.");

        ShowKeys(row);
        AddKeyButton.IsEnabled = true;
        RemoveKeyButton.IsEnabled = true;
    }

    void ShowKeys(CycleRow row)
    {
        KeyList.ItemsSource = row.Entry.Values
            .Select((_, k) => new KeyRow(row.Entry, k, _cycle.IsWeather))
            .ToList();
    }

    void Key_Committed(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not KeyRow key
            || EntryList.SelectedItem is not CycleRow row)
            return;

        if (!key.Commit(out string complaint))
        {
            SetStatus(complaint);
            ShowKeys(row);
            return;
        }

        row.Refresh();
        MarkDirty();
    }

    void AddKey_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not CycleRow row)
            return;

        var entry = row.Entry;
        int at = Math.Max(1, KeyList.SelectedIndex + 1);

        ushort before = entry.Times[at - 1];
        ushort after = at < entry.Times.Count ? entry.Times[at] : (ushort)_cycle.Length;

        entry.Times.Insert(at, (ushort)((before + after) / 2));
        entry.Values.Insert(at, (float[])entry.Values[at - 1].Clone());

        ShowKeys(row);
        KeyList.SelectedIndex = at;
        row.Refresh();
        MarkDirty();
    }

    void RemoveKey_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not CycleRow row || KeyList.SelectedIndex < 0)
            return;

        var entry = row.Entry;

        if (entry.Values.Count <= 2)
        {
            SetStatus("An entry needs at least two keys, one at each end of the day.");
            return;
        }

        int at = KeyList.SelectedIndex;
        if (at == 0 || at == entry.Values.Count - 1)
        {
            SetStatus("The first and last key hold the day together and cannot be removed.");
            return;
        }

        entry.Times.RemoveAt(at);
        entry.Values.RemoveAt(at);

        ShowKeys(row);
        row.Refresh();
        MarkDirty();
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        byte[] data;
        try
        {
            data = _cycle.Write();
            _save(data);
        }
        catch (Exception ex)
        {
            ShowError("Could not save the cycle", ex);
            return;
        }

        _dirty = false;
        SaveButton.IsEnabled = false;

        int changed = _rows.Count(r => r.Changed);
        SetStatus($"{changed} entries changed, {data.Length:N0} bytes on the change list. " +
                  "Apply in the main window writes it into the archive.");
    }

    void Revert_Click(object sender, RoutedEventArgs e)
    {
        var fresh = TimeCycle.Read(_original);

        _cycle.Entries.Clear();
        _cycle.Entries.AddRange(fresh.Entries);

        _rows.Clear();
        for (int i = 0; i < _cycle.Entries.Count; i++)
            _rows.Add(new CycleRow(i, _cycle.Entries[i]));

        ShowEntries();
        KeyList.ItemsSource = null;

        _dirty = false;
        SaveButton.IsEnabled = false;
        RevertButton.IsEnabled = false;
        SetStatus("Back to what the game shipped.");
    }

    void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Export time cycle",
            FileName = _name + ".txt",
            Filter = "Text (*.txt)|*.txt|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(dialog.FileName, TimeCycleText.Write(_cycle, _name));
        }
        catch (Exception ex)
        {
            ShowError($"Could not write {Path.GetFileName(dialog.FileName)}", ex);
            return;
        }

        SetStatus($"written to {dialog.FileName}");
    }

    void MarkDirty()
    {
        _dirty = true;
        SaveButton.IsEnabled = true;
        RevertButton.IsEnabled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_dirty)
            return;

        var answer = MessageBox.Show(this,
            "There are edits that were never saved. Close anyway?",
            "Time cycle", MessageBoxButton.YesNo, MessageBoxImage.Question);

        e.Cancel = answer != MessageBoxResult.Yes;
    }

    void ShowError(string what, Exception ex)
    {
        SetStatus($"{what}: {ex.Message}");
        StatusText.Foreground = (Brush)FindResource("Warning");

        MessageBox.Show(this, $"{what}.{Environment.NewLine}{Environment.NewLine}{ex.Message}", "Time cycle", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    void SetStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Foreground = (Brush)FindResource("TextDim");
    }
}

public sealed class CycleRow : INotifyPropertyChanged
{
    readonly float[][] _shipped;

    public CycleRow(int index, TimeCycleEntry entry)
    {
        Index = index;
        Entry = entry;
        Path = entry.PathText();
        _shipped = entry.Values.Select(v => (float[])v.Clone()).ToArray();
    }

    public int Index { get; }
    public TimeCycleEntry Entry { get; }
    public string Path { get; }

    public bool Varies => Entry.Values.Any(v => !v.SequenceEqual(Entry.Values[0]));

    public bool Changed => Entry.Values.Count != _shipped.Length || Entry.Values.Where((v, i) => !v.SequenceEqual(_shipped[i])).Any();

    public string Mark => Changed ? "●" : "";

    public string Summary => Varies ? $"{Entry.Values.Count} keys" : "constant";

    public void Refresh()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Mark)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class KeyRow : INotifyPropertyChanged
{
    readonly TimeCycleEntry _entry;
    readonly int _key;

    readonly bool _weather;

    public KeyRow(TimeCycleEntry entry, int key, bool weather)
    {
        _entry = entry;
        _key = key;
        _weather = weather;
        Reload();
    }

    public string Time { get; set; } = "";
    public string Value { get; set; } = "";

    public bool TimeFixed => _key == 0;

    public Brush Swatch { get; private set; } = Brushes.Transparent;

    public Visibility SwatchShown =>
        _entry.Kind == CurveKind.Color ? Visibility.Visible : Visibility.Collapsed;

    public void Reload()
    {
        Time = TimeCycleText.Axis(_entry.Times[_key], _weather);
        Value = string.Join(" ", _entry.Values[_key].Select(v => v.ToString(CultureInfo.InvariantCulture)));

        UpdateSwatch();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Time)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
    }

    public bool Commit(out string complaint)
    {
        complaint = "";

        var parts = Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != _entry.Components)
        {
            complaint = $"{_entry.Kind} needs {_entry.Components} number(s), not {parts.Length}.";
            return false;
        }

        var value = new float[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!TryNumber(parts[i], out value[i]))
            {
                complaint = $"\"{parts[i]}\" is not a number.";
                return false;
            }

        if (!TimeFixed)
        {
            if (!TimeCycleText.TryAxis(Time, _weather, out ushort time))
            {
                complaint = _weather
                    ? $"\"{Time}\" is not a position on the curve. Write it as a percentage from 0 to 100."
                    : $"\"{Time}\" is not a time of day. Write it as hh:mm:ss.";
                return false;
            }
            _entry.Times[_key] = time;
        }

        _entry.Values[_key] = value;
        UpdateSwatch();
        return true;
    }

    static bool TryNumber(string text, out float value) => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) || float.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value);

    void UpdateSwatch()
    {
        if (_entry.Kind != CurveKind.Color)
            return;

        var value = _entry.Values[_key];
        Swatch = new SolidColorBrush(Color.FromRgb(Channel(value[0]), Channel(value[1]), Channel(value[2])));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Swatch)));

        static byte Channel(float v) => (byte)Math.Clamp(v * 255f, 0f, 255f);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
