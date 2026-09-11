using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using Wildlands.Formats;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;
using System.Linq;
using System.Collections.Generic;
using System.Collections.Concurrent;
using Wildlands.Formats.Models;
using Wildlands.Formats.Materials;
using Wildlands.Formats.Textures;
using Wildlands.Formats.Weather;
using Wildlands.Toolkit;

static partial class Commands
{
    internal static int ShowTimeCycle(string path, string name, string? output)
    {
        var file = DataFile.Read(path);
        Resource resource = RequireNamedResource(file, name);
    
        var cycle = TimeCycle.Read(resource.Data);
        string text = TimeCycleText.Write(cycle, name);
    
        if (output is null)
            Console.Write(text);
        else
        {
            File.WriteAllText(output, text);
            Console.WriteLine($"{cycle.Entries.Count} entries -> {output}");
        }
        return 0;
    }
    
    // The way back in. The text only carries values, everything structural comes from
    // the resource that is already there, so a patch cannot break the layout - the
    // worst it can do is set a number the game does not like.

    internal static int ApplyTimeCycle(string path, string name, string input, string output)
    {
        var file = DataFile.Read(path);
        Resource resource = RequireNamedResource(file, name);
    
        var cycle = TimeCycle.Read(resource.Data);
        int changed = TimeCycleText.Apply(cycle, File.ReadAllLines(input));
    
        resource.Data = cycle.Write();
        file.Write(output);
    
        Console.WriteLine($"{changed} of {cycle.Entries.Count} entries changed -> {output}");
        return 0;
    }

    internal static int ApplyGraphicsProfile(string path, string name, string profilePath, string? output)
    {
        var file = DataFile.Read(path);
        Resource resource = RequireNamedResource(file, name);
    
        var cycle = TimeCycle.Read(resource.Data);
        var profile = GraphicsProfile.Load(profilePath);
        var result = profile.Apply(cycle, resource.Name);
    
        Console.WriteLine($"{result.Name}{(result.Experimental ? " [EXPERIMENTAL]" : "")}: "
            + $"{result.Changes.Count} curve(s), {result.ChangedValues} value(s)");
        foreach (var change in result.Changes)
            Console.WriteLine($"  {change.Label}: {change.BeforeMinimum:0.#####}..{change.BeforeMaximum:0.#####} "
                + $"-> {change.AfterMinimum:0.#####}..{change.AfterMaximum:0.#####} ({change.ChangedValues} values)");
    
        if (output is null)
        {
            Console.WriteLine("preview only; pass out.data to write the modified data file");
            return 0;
        }
    
        resource.Data = cycle.Write();
        file.Write(output);
        var written = DataFile.Read(output);
        if (written.Resources.Count != file.Resources.Count)
            throw new InvalidDataException("The written data file has a different resource count.");
    
        for (int i = 0; i < file.Resources.Count; i++)
        {
            var expected = file.Resources[i];
            var actual = written.Resources[i];
            if (expected.Id != actual.Id || expected.ClassHash != actual.ClassHash
                || expected.Name != actual.Name || !expected.Data.AsSpan().SequenceEqual(actual.Data))
                throw new InvalidDataException($"The written data file failed verification at resource {i} ({expected.Name}).");
        }
    
        Console.WriteLine("written -> " + output);
        Console.WriteLine($"verified {written.Resources.Count} resource(s) after reopening the output");
        return 0;
    }
    
    // Reads every time cycle in an archive and writes it straight back. The resource
    // carries eight bytes per entry that nothing derives, and object ids that are
    // handed out again on writing, so "identical" is the only answer that proves both
    // the reader and the writer. Anything less and an edited cycle would come back
    // subtly different from the one the game shipped.

    internal static int CheckTimeCycles(string forgePath, int limit)
    {
        using var archive = ForgeArchive.Open(forgePath);
    
        int read = 0, identical = 0, failed = 0, entries = 0;
        var kinds = new Dictionary<string, int>();
        var unreadable = new List<string>();
    
        foreach (var entry in archive.Entries)
        {
            if (read >= limit) break;
            if (entry.FileExtension != ".data") continue;
    
            DataFile file;
            try { using var s = new MemoryStream(archive.ReadEntry(entry)); file = DataFile.Read(s); }
            catch (Exception ex) { unreadable.Add($"{entry.Name}: {ex.Message}"); continue; }
    
            foreach (var resource in file.Resources)
            {
                if (!TimeCycle.IsTimeCycle(resource.ClassHash)) continue;
                read++;
    
                try
                {
                    var cycle = TimeCycle.Read(resource.Data);
                    bool same = cycle.Write().SequenceEqual(resource.Data);
    
                    if (same) identical++; else Console.WriteLine($"  differs: {resource.Name}");
    
                    Console.WriteLine($"  {ResourceTypes.NameOf(resource.ClassHash),-32} " +
                                      $"{cycle.Entries.Count,4} entries  {entry.Name} :: {resource.Name}");
    
                    entries += cycle.Entries.Count;
                    foreach (var e in cycle.Entries)
                        Bump(kinds, e.Kind.ToString());
                }
                catch (Exception ex)
                {
                    failed++;
                    Console.WriteLine($"  {resource.Name}: {ex.Message}");
                }
            }
        }
    
        Console.WriteLine($"{identical} of {read} time cycles rewrite identically, {failed} failed to read");
        Console.WriteLine($"{entries} entries in total");
        foreach (var kind in kinds.OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kind.Key,-8} {kind.Value}");
    
        // A data file that will not open is worth saying out loud. Skipping it quietly
        // is how a broken archive comes back as "0 of 0" and reads like success.
        if (unreadable.Count > 0)
        {
            Console.WriteLine($"{unreadable.Count} data files could not be opened:");
            foreach (string note in unreadable.Take(10))
                Console.WriteLine($"  {note}");
        }
    
        return failed == 0 && unreadable.Count == 0 && identical == read ? 0 : 1;
    }
    
    // A property hash alone says very little. This census keeps the curve kind and
    // every full property path in which it occurs, which turns a future name match
    // into something we can check semantically as well as by CRC32.

    internal static int CensusWeatherProperties(string forgePath, int limit)
    {
        var properties = CollectWeatherProperties(forgePath, limit, out int controllers, out int unreadable);
        int confirmed = properties.Count(p => PropertyNames.IsConfirmed(p.Key));
        int unverified = properties.Count - confirmed;
    
        Console.WriteLine($"{controllers} controller(s), {properties.Count} distinct property hash(es): "
            + $"{confirmed} confirmed, {unverified} still unverified");
    
        if (unreadable > 0)
            Console.WriteLine($"{unreadable} data file(s) could not be read and were skipped.");
    
        foreach (var (hash, observation) in properties
            .Where(p => !PropertyNames.IsConfirmed(p.Key))
            .OrderByDescending(p => p.Value.Uses)
            .ThenBy(p => p.Key))
        {
            string display = PropertyNames.TryName(hash, out string? name)
                ? name!
                : "unknown";
            string kinds = string.Join(", ", observation.Kinds.OrderBy(p => p.Key)
                .Select(p => $"{p.Key} {p.Value}"));
    
            Console.WriteLine();
            Console.WriteLine($"0x{hash:X8}  {display}  used {observation.Uses} time(s), {kinds}");
            foreach (string path in observation.Paths.Take(4))
                Console.WriteLine("  " + path);
            if (observation.Paths.Count > 4)
                Console.WriteLine($"  … {observation.Paths.Count - 4} more path(s)");
        }
    
        return 0;
    }
    
    // Values are the second half of the evidence: a field that is a constant RGBA
    // colour across all climates should not be called a density, and a 32-element
    // animated array should not be called an exposure threshold. This command keeps
    // the original curves visible while trying names.

    internal static int ProfileWeatherProperties(string forgePath, string[] hashTexts)
    {
        var wanted = new HashSet<uint>();
        foreach (string text in hashTexts)
        {
            string number = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
            if (!uint.TryParse(number, System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out uint hash))
            {
                Console.WriteLine($"not a hexadecimal property hash: {text}");
                return 1;
            }
            wanted.Add(hash);
        }
    
        int controllers = 0;
        int matches = 0;
        using var archive = ForgeArchive.Open(forgePath);
        foreach (var entry in archive.Entries)
        {
            if (entry.FileExtension != ".data")
                continue;
    
            DataFile file;
            try
            {
                using var stream = new MemoryStream(archive.ReadEntry(entry));
                file = DataFile.Read(stream);
            }
            catch
            {
                continue;
            }
    
            foreach (var resource in file.Resources)
            {
                if (!TimeCycle.IsTimeCycle(resource.ClassHash))
                    continue;
    
                TimeCycle cycle;
                try { cycle = TimeCycle.Read(resource.Data); }
                catch { continue; }
    
                controllers++;
                foreach (var curve in cycle.Entries)
                {
                    uint? hash = curve.Path.Where(step => wanted.Contains(step.NameHash))
                        .Select(step => (uint?)step.NameHash)
                        .FirstOrDefault();
                    if (hash is null)
                        continue;
    
                    matches++;
                    var values = curve.Values.SelectMany(value => value).ToArray();
                    float low = values.Min();
                    float high = values.Max();
                    bool constant = curve.Values.All(value => value.AsSpan().SequenceEqual(curve.Values[0]));
                    string lowText = low.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
                    string highText = high.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
    
                    Console.WriteLine($"{resource.Name}  {curve.PathText()}");
                    Console.WriteLine($"  0x{hash:X8}  {curve.Kind}, {curve.Values.Count} key(s), "
                        + $"range {lowText}..{highText}, {(constant ? "constant" : "animated")}");
                    Console.WriteLine("  " + string.Join(" | ", curve.Values.Select((value, index) =>
                        $"{TimeCycleText.Axis(curve.Times[index], cycle.IsWeather)}={string.Join(",", value.Select(v => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)))}")));
                }
            }
        }
    
        Console.WriteLine();
        Console.WriteLine($"{matches} matching curve(s) in {controllers} controller(s)");
        return matches > 0 ? 0 : 1;
    }
    
    // Property strings are sometimes retained in the executable, diagnostics, or
    // companion DLLs. Only byte strings actually present in the files supplied by
    // the user are tried, and only against hashes observed in a controller. This is
    // deliberately much narrower than a dictionary attack.

    internal static int CrackPropertyNames(string forgePath, string[] binaryPaths)
    {
        var properties = CollectWeatherProperties(forgePath, int.MaxValue, out int controllers, out int unreadable);
        var targets = properties
            .Where(p => !PropertyNames.IsConfirmed(p.Key))
            .Select(p => p.Key)
            .ToHashSet();
    
        Console.WriteLine($"{controllers} controller(s), {targets.Count} unverified property hash(es)");
        if (unreadable > 0)
            Console.WriteLine($"{unreadable} data file(s) could not be read and were skipped.");
    
        var matches = new Dictionary<uint, Dictionary<string, List<string>>>();
    
        foreach (string binaryPath in binaryPaths)
        {
            if (Directory.Exists(binaryPath))
            {
                int names = 0;
                VisitFileNameTokens(binaryPath, (candidate, source) =>
                {
                    names++;
                    Record(candidate, source);
                });
                Console.WriteLine($"{Path.GetFileName(Path.TrimEndingDirectorySeparator(binaryPath))}: examined {names:N0} extracted file-name identifier(s)");
                continue;
            }
    
            if (!File.Exists(binaryPath))
            {
                Console.WriteLine($"missing: {binaryPath}");
                continue;
            }
    
            int asciiStrings = 0;
            int wideStrings = 0;
            VisitAsciiTokens(binaryPath, (candidate, offset) =>
            {
                asciiStrings++;
                Record(candidate, $"{Path.GetFileName(binaryPath)}+0x{offset:X}");
            });
            VisitUtf16LeTokens(binaryPath, (candidate, offset) =>
            {
                wideStrings++;
                Record(candidate, $"{Path.GetFileName(binaryPath)}+0x{offset:X}");
            });
    
            Console.WriteLine($"{Path.GetFileName(binaryPath)}: examined {asciiStrings:N0} ASCII and {wideStrings:N0} UTF-16 identifier(s)");
    
            void Record(string candidate, string source)
            {
                uint hash = ResourceTypes.Crc32(candidate);
                if (!targets.Contains(hash))
                    return;
    
                if (!matches.TryGetValue(hash, out var candidates))
                    matches[hash] = candidates = [];
                if (!candidates.TryGetValue(candidate, out var places))
                    candidates[candidate] = places = [];
                if (places.Count < 3)
                    places.Add(source);
            }
        }
    
        Console.WriteLine();
        if (matches.Count == 0)
        {
            Console.WriteLine("No literal property-name match. Try GRW.exe plus engine DLLs, or a symbol/config dump from the same game build.");
            return 0;
        }
    
        foreach (var (hash, candidates) in matches.OrderBy(p => p.Key))
        {
            Console.WriteLine($"0x{hash:X8}  {string.Join(" | ", candidates.Keys.Order())}");
            foreach (string place in candidates.Values.SelectMany(p => p).Take(3))
                Console.WriteLine("  " + place);
        }
    
        Console.WriteLine();
        Console.WriteLine($"{matches.Count} hash match(es). Validate each candidate against the printed controller paths before adding it to PropertyNames.ByHash.");
        return 0;
    }

    internal static Dictionary<uint, (int Uses, Dictionary<CurveKind, int> Kinds, SortedSet<string> Paths)> CollectWeatherProperties(string forgePath, int limit,
        out int controllers, out int unreadable)
    {
        var properties = new Dictionary<uint, (int Uses, Dictionary<CurveKind, int> Kinds, SortedSet<string> Paths)>();
        controllers = 0;
        unreadable = 0;
    
        using var archive = ForgeArchive.Open(forgePath);
        foreach (var entry in archive.Entries)
        {
            if (controllers >= limit)
                break;
            if (entry.FileExtension != ".data")
                continue;
    
            if (!TryReadDataFile(archive, entry, out DataFile file))
            {
                unreadable++;
                continue;
            }
    
            foreach (var resource in file.Resources)
            {
                if (controllers >= limit)
                    break;
                if (!TimeCycle.IsTimeCycle(resource.ClassHash))
                    continue;
    
                TimeCycle cycle;
                try
                {
                    cycle = TimeCycle.Read(resource.Data);
                }
                catch
                {
                    continue;
                }
    
                controllers++;
                foreach (var curve in cycle.Entries)
                {
                    string path = curve.PathText();
                    foreach (var step in curve.Path)
                    {
                        if (!properties.TryGetValue(step.NameHash, out var observation))
                            observation = (0, [], []);
    
                        observation.Uses++;
                        observation.Kinds[curve.Kind] = observation.Kinds.GetValueOrDefault(curve.Kind) + 1;
                        observation.Paths.Add(path);
                        properties[step.NameHash] = observation;
                    }
                }
            }
        }
    
        return properties;
    }

    internal static void VisitAsciiTokens(string path, Action<string, long> visit)
    {
        const int MaximumLength = 128;
        var buffer = new byte[1 << 20];
        var word = new System.Text.StringBuilder();
        bool tooLong = false;
        long position = 0;
        long start = 0;
    
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++, position++)
            {
                byte value = buffer[i];
                bool identifier = value is >= (byte)'A' and <= (byte)'Z'
                    or >= (byte)'a' and <= (byte)'z'
                    or >= (byte)'0' and <= (byte)'9'
                    or (byte)'_';
    
                if (identifier)
                {
                    if (word.Length == 0 && !tooLong)
                        start = position;
    
                    if (word.Length < MaximumLength && !tooLong)
                        word.Append((char)value);
                    else
                        tooLong = true;
                    continue;
                }
    
                Emit();
            }
        }
    
        Emit();
    
        void Emit()
        {
            if (!tooLong && word.Length >= 3 && IsAsciiLetter(word[0]))
                visit(word.ToString(), start);
    
            word.Clear();
            tooLong = false;
        }
    }

    internal static void VisitUtf16LeTokens(string path, Action<string, long> visit)
    {
        const int MaximumLength = 128;
        var buffer = new byte[1 << 20];
        var word = new System.Text.StringBuilder();
        bool tooLong = false;
        bool waitingForZero = false;
        byte pending = 0;
        long position = 0;
        long pendingPosition = 0;
        long start = 0;
    
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++, position++)
            {
                byte value = buffer[i];
                if (!waitingForZero)
                {
                    if (IsIdentifierByte(value))
                    {
                        pending = value;
                        pendingPosition = position;
                        waitingForZero = true;
                    }
                    else
                    {
                        Emit();
                    }
                    continue;
                }
    
                if (value == 0)
                {
                    if (word.Length == 0 && !tooLong)
                        start = pendingPosition;
    
                    if (word.Length < MaximumLength && !tooLong)
                        word.Append((char)pending);
                    else
                        tooLong = true;
                    waitingForZero = false;
                    continue;
                }
    
                Emit();
                waitingForZero = IsIdentifierByte(value);
                if (waitingForZero)
                {
                    pending = value;
                    pendingPosition = position;
                }
            }
        }
    
        Emit();
    
        void Emit()
        {
            if (!tooLong && word.Length >= 3 && IsAsciiLetter(word[0]))
                visit(word.ToString(), start);
    
            word.Clear();
            tooLong = false;
        }
    }

    internal static void VisitFileNameTokens(string folder, Action<string, string> visit)
    {
        foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            int start = 0;
    
            for (int i = 0; i <= stem.Length; i++)
            {
                bool identifier = i < stem.Length && (char.IsLetterOrDigit(stem[i]) || stem[i] == '_');
                if (identifier)
                    continue;
    
                if (i - start >= 3 && IsAsciiLetter(stem[start]))
                    visit(stem[start..i], Path.GetRelativePath(folder, path));
                start = i + 1;
            }
        }
    }

    internal static bool IsIdentifierByte(byte value) => value is >= (byte)'A' and <= (byte)'Z'
        or >= (byte)'a' and <= (byte)'z'
        or >= (byte)'0' and <= (byte)'9'
        or (byte)'_';

    internal static bool IsAsciiLetter(char value) => value is >= 'A' and <= 'Z' or >= 'a' and <= 'z';
    
    
    // Who points at a resource. Ids are eight bytes and a resource that refers to
    // another simply carries its id, so a scan for that pattern finds the holders
    // without knowing any of the formats involved. Good enough to answer whether a
    // thing is used at all, which is otherwise hard to tell.
    // Before changing a mesh for a real test it matters which archive the game
    // actually loads. The same resource often sits in the base archive, in the
    // world map and in a patch, and only the last one wins.
}
