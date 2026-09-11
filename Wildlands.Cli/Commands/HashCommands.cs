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
    internal static int HashNames(string[] names)
    {
        foreach (var name in names)
            Console.WriteLine("0x" + ResourceTypes.Crc32(name).ToString("X8") + "  " + name);
    
        return 0;
    }
    
    // CRC32 cannot be run backwards, so the only way from a hash to a name is the
    // other direction: hash a candidate and see whether the result is a class hash
    // that actually turns up somewhere in the game. This hashes every line of a
    // text file (blank lines and lines starting with # are skipped, so the file
    // can carry its own notes) and checks each one against every class hash seen
    // across the given archives. A hit that ResourceTypes does not already know
    // is a genuine new find, worth adding to KnownNames in ResourceTypes.cs.

    internal static int GuessTypes(string listPath, string[] forgePaths)
    {
        if (forgePaths.Length == 0)
        {
            Console.WriteLine("usage: wlcli guess <list.txt> <file.forge> [file2.forge...]");
            Console.WriteLine("  needs at least one archive to check the candidates against");
            return 1;
        }
    
        var candidates = File.ReadAllLines(listPath)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .Distinct()
            .ToList();
    
        var seen = new Dictionary<uint, int>();
    
        foreach (string forgePath in forgePaths)
        {
            using var archive = ForgeArchive.Open(forgePath);
    
            foreach (var entry in archive.Entries)
            {
                if (entry.FileExtension != ".data") continue;
    
                DataFile file;
                try
                {
                    using var stream = new MemoryStream(archive.ReadEntry(entry));
                    file = DataFile.Read(stream);
                }
                catch { continue; }
    
                foreach (var resource in file.Resources)
                    seen[resource.ClassHash] = seen.GetValueOrDefault(resource.ClassHash) + 1;
            }
        }
    
        Console.WriteLine(candidates.Count + " candidate(s), " + seen.Count + " distinct class hash(es) seen across "
            + forgePaths.Length + " archive(s)");
        Console.WriteLine();
    
        int newHits = 0, alreadyKnown = 0;
    
        foreach (string candidate in candidates)
        {
            uint hash = ResourceTypes.Crc32(candidate);
            if (!seen.TryGetValue(hash, out int count))
                continue;
    
            if (ResourceTypes.NameOf(hash) == candidate)
            {
                alreadyKnown++;
                continue;
            }
    
            newHits++;
            Console.WriteLine("HIT   " + candidate.PadRight(36) + "0x" + hash.ToString("X8")
                + "   seen " + count + " time(s)");
        }
    
        Console.WriteLine();
        Console.WriteLine(newHits + " new match(es), " + alreadyKnown + " already in ResourceTypes.KnownNames, "
            + (candidates.Count - newHits - alreadyKnown) + " no match");
    
        return 0;
    }

    internal static int ScanHashes(string binaryPath, string[] hashTexts)
    {
        var targets = hashTexts.Select(ParseHash).ToHashSet();
        var matches = new Dictionary<uint, SortedSet<string>>();
    
        VisitAsciiTokens(binaryPath, Record);
        VisitUtf16LeTokens(binaryPath, Record);
    
        foreach (uint hash in targets.Order())
        {
            if (matches.TryGetValue(hash, out var names))
                Console.WriteLine($"0x{hash:X8}  {string.Join(" | ", names)}");
            else
                Console.WriteLine($"0x{hash:X8}  <no literal match>");
        }
    
        return 0;
    
        void Record(string candidate, long _)
        {
            uint hash = ResourceTypes.Crc32(candidate);
            if (!targets.Contains(hash))
                return;
    
            if (!matches.TryGetValue(hash, out var names))
                matches[hash] = names = [];
            names.Add(candidate);
        }
    }

    internal static uint ParseHash(string text)
    {
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return Convert.ToUInt32(text[2..], 16);
        return Convert.ToUInt32(text, 16);
    }
}
