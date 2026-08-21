using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Wildlands.Formats.Weather;

public static class TimeCycleText
{
    public static string Write(TimeCycle cycle, string name)
    {
        var text = new StringBuilder();

        text.AppendLine($"# {name}  {ResourceTypes.NameOf(cycle.Type)}  {cycle.Entries.Count} entries");
        text.AppendLine("#");
        text.AppendLine("# One block per entry: its number and the property it drives, then a line per");
        text.AppendLine("# key. A key is a position on the curve and the value at it - one number for");
        text.AppendLine("# Float and Byte, three for Vector3, four for Color as R G B A.");

        if (cycle.IsWeather)
            text.AppendLine("# A weather curve runs 0 % to 100 %, not over the day.");
        else
            text.AppendLine("# The day runs 00:00:00 to 24:00:00 and the last key repeats the first.");
        text.AppendLine("#");
        text.AppendLine("# Blocks left out stay as they are. Times and the number of keys are read back");
        text.AppendLine("# as well, so keys can be added and removed.");

        for (int i = 0; i < cycle.Entries.Count; i++)
        {
            var entry = cycle.Entries[i];

            text.AppendLine();
            text.AppendLine($"{i}  {entry.PathText()}  {entry.Kind}");

            for (int k = 0; k < entry.Values.Count; k++)
            {
                text.Append("    ").Append(Axis(entry.Times[k], cycle.IsWeather)).Append("  ");

                foreach (float component in entry.Values[k])
                    text.Append(component.ToString(CultureInfo.InvariantCulture)).Append(' ');

                text.Length--;
                text.AppendLine();
            }
        }

        return text.ToString();
    }

    public static int Apply(TimeCycle cycle, IEnumerable<string> lines)
    {
        TimeCycleEntry? entry = null;
        var times = new List<ushort>();
        var values = new List<float[]>();
        int changed = 0;

        foreach (string line in lines)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed[0] == '#')
                continue;

            if (!char.IsWhiteSpace(line[0]))
            {
                changed += Commit(entry, times, values);

                int index = int.Parse(trimmed.Split(' ', '\t')[0]);
                entry = cycle.Entries[index];
                continue;
            }

            if (entry is null)
                throw new FormatException($"A key line before any entry: {trimmed}");

            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != entry.Components + 1)
                throw new FormatException($"Entry {entry.PathText()} is {entry.Kind}, so a key needs {entry.Components} value(s), not {parts.Length - 1}: {trimmed}");

            if (!TryAxis(parts[0], cycle.IsWeather, out ushort time))
                throw new FormatException($"\"{parts[0]}\" is not a position on this curve: {trimmed}");

            times.Add(time);

            var value = new float[entry.Components];
            for (int c = 0; c < value.Length; c++)
                value[c] = float.Parse(parts[c + 1], CultureInfo.InvariantCulture);
            values.Add(value);
        }

        return changed + Commit(entry, times, values);
    }

    static int Commit(TimeCycleEntry? entry, List<ushort> times, List<float[]> values)
    {
        if (entry is null || values.Count == 0)
            return 0;

        bool same = times.Count == entry.Times.Count && values.Count == entry.Values.Count;
        for (int i = 0; same && i < values.Count; i++)
            same = times[i] == entry.Times[i] && values[i].AsSpan().SequenceEqual(entry.Values[i]);

        entry.Times.Clear();
        entry.Times.AddRange(times);
        entry.Values.Clear();
        entry.Values.AddRange(values);

        entry.Times[0] = 0;

        times.Clear();
        values.Clear();
        return same ? 0 : 1;
    }

    public static string Axis(int time, bool weather)
    {
        if (weather)
            return (time / 120.0).ToString("0.####", CultureInfo.InvariantCulture) + "%";

        int seconds = time * 30;
        return $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}";
    }

    public static bool TryAxis(string text, bool weather, out ushort time)
    {
        time = 0;
        string trimmed = text.Trim().TrimEnd('%').Trim();

        if (weather)
        {
            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double percent)
                && !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out percent))
                return false;

            if (percent < 0 || percent * 120 > TimeCycle.WeatherLength)
                return false;

            time = (ushort)Math.Round(percent * 120);
            return true;
        }

        var parts = trimmed.Split(':');
        if (parts.Length is < 2 or > 3)
            return false;

        int seconds = 0;
        foreach (string part in parts)
        {
            if (!int.TryParse(part, out int number) || number < 0)
                return false;
            seconds = seconds * 60 + number;
        }

        if (parts.Length == 2)
            seconds *= 60;

        if (seconds > TimeCycle.DayLength * 30)
            return false;

        time = (ushort)(seconds / 30);
        return true;
    }
}
