using System.Collections.Generic;
using System.IO;
using System.Linq;
using Wildlands.Formats.Data;
using Wildlands.Formats.Forge;

namespace Wildlands.Formats.Textures;

public sealed class TextureMipLevel
{
    public int Level { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public byte[] Pixels { get; init; } = [];

    // Streamed levels are in their own CompiledMip resource
    public bool IsStreamed { get; init; }
    public string SourceName { get; init; } = "";
    public string SourceArchive { get; init; } = "";
    public ulong SourceId { get; init; }

    public string Size => $"{Width} x {Height}";

    public override string ToString() => $"Level {Level}  {Size}  {(IsStreamed ? "CompiledMip" : "TextureMap")}";

    public string Description
    {
        get
        {
            if (!IsStreamed)
                return "TextureMap (embedded chain)";

            return SourceArchive.Length > 0
                ? $"CompiledMip {SourceName} in {SourceArchive}"
                : $"CompiledMip {SourceName}";
        }
    }
}

public sealed class TextureMipSet
{
    public List<TextureMipLevel> Levels { get; } = [];

    public List<ulong> MissingStreamed { get; } = [];

    public TextureMipLevel? Best => Levels.Count > 0 ? Levels[0] : null;

    public TextureMipLevel? Find(int level) => Levels.FirstOrDefault(l => l.Level == level);

    public static TextureMipSet Collect(TextureMap texture, ArchiveSet? archives) =>
        Collect(texture, archives, null);

    // Replacements are streamed mips that have been rebuilt but not written yet, so a
    // preview can show them before the archive is touched.
    public static TextureMipSet Collect(TextureMap texture, ArchiveSet? archives,
        IReadOnlyDictionary<ulong, byte[]>? replaced)
    {
        var set = new TextureMipSet();

        foreach (ulong id in texture.StreamedMips)
        {
            var level = replaced is not null && replaced.TryGetValue(id, out var pending)
                ? Describe(texture, CompiledMip.Read(pending), "replacement")
                : archives is null ? null : ReadStreamed(texture, archives, id);

            if (level is null)
                set.MissingStreamed.Add(id);
            else
                set.Levels.Add(level);
        }

        foreach (var level in ReadEmbedded(texture))
        {
            if (set.Find(level.Level) is null)
                set.Levels.Add(level);
        }

        set.Levels.Sort((a, b) => a.Level.CompareTo(b.Level));
        return set;
    }

    public static TextureMap? LoadParent(CompiledMip mip, ArchiveSet archives)
    {
        var found = archives.FindResource(mip.TextureMapId, TextureMap.ClassHash);
        if (found is null)
            return null;

        try
        {
            return TextureMap.Read(found.Resource.Data);
        }
        catch
        {
            return null;
        }
    }

    public static TextureMipLevel? Describe(TextureMap texture, CompiledMip mip, string sourceName,
        string sourceArchive = "")
    {
        int level = (int)mip.Level;
        if (level < 0 || level >= texture.MipCount)
            return null;

        var (width, height) = texture.SizeOfLevel(level);
        if (mip.Pixels.Length < texture.Format.LevelSize(width, height))
            return null;

        return new TextureMipLevel
        {
            Level = level,
            Width = width,
            Height = height,
            Pixels = mip.Pixels,
            IsStreamed = true,
            SourceName = sourceName,
            SourceArchive = sourceArchive,
            SourceId = mip.Id,
        };
    }

    static TextureMipLevel? ReadStreamed(TextureMap texture, ArchiveSet archives, ulong id)
    {
        var found = archives.FindResource(id, CompiledMip.ClassHash);
        if (found is null)
            return null;

        try
        {
            var mip = CompiledMip.Read(found.Resource.Data);
            return Describe(texture, mip, found.File.Name, found.File.ArchiveName);
        }
        catch
        {
            // A broken mip just means we fall back to a smaller one
            return null;
        }
    }

    static IEnumerable<TextureMipLevel> ReadEmbedded(TextureMap texture)
    {
        int start = texture.FirstStoredLevel();
        if (start < 0)
            yield break;

        int offset = 0;
        for (int level = start; level < texture.MipCount; level++)
        {
            var (width, height) = texture.SizeOfLevel(level);
            int size = texture.Format.LevelSize(width, height);

            if (size <= 0 || offset + size > texture.Pixels.Length)
                yield break;

            yield return new TextureMipLevel
            {
                Level = level,
                Width = width,
                Height = height,
                Pixels = texture.Pixels.AsSpan(offset, size).ToArray(),
                SourceName = "this resource",
                SourceId = texture.Id,
            };

            offset += size;
        }
    }
}
