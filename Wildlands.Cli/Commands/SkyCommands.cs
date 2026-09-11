using System.Globalization;
using System.IO;
using Wildlands.Formats.Data;
using Wildlands.Formats.Models;
using Wildlands.Formats.Textures;

static partial class Commands
{
    internal static int CycleLayeredSkies(string folder)
    {
        int read = 0, same = 0, differ = 0, broken = 0;
        foreach (string path in Directory.EnumerateFiles(folder, "*.data"))
        {
            DataFile file;
            try
            {
                using var stream = File.OpenRead(path);
                file = DataFile.Read(stream);
            }
            catch
            {
                continue;
            }

            foreach (Resource resource in file.Resources.Where(item => item.ClassHash == LayeredSky.ClassHash))
            {
                read++;
                try
                {
                    LayeredSkyAsset asset = LayeredSky.Read(resource.Data);
                    Console.WriteLine($"0x{resource.Id:X12} {resource.Name}: {asset.Layers.Count} layer(s) at "
                        + string.Join(", ", asset.Layers.Select(layer => $"{layer.Hour:0.#}h"))
                        + $" — {asset.Layers[0].Width}x{asset.Layers[0].Height}x{asset.Layers[0].Depth} "
                        + $"at {asset.Layers[0].BytesPerTexel} byte(s) per texel");
                    if (LayeredSky.Write(asset).AsSpan().SequenceEqual(resource.Data))
                        same++;
                    else
                        differ++;
                }
                catch (Exception error)
                {
                    broken++;
                    Console.WriteLine($"0x{resource.Id:X12} {resource.Name}: {error.Message}");
                }
            }
        }

        Console.WriteLine($"{read} LayeredSky: {same} byte for byte, {differ} different, {broken} unreadable");
        return same == read ? 0 : 1;
    }

    internal static int ExportLayeredSky(string dataFile, string outDir)
    {
        Directory.CreateDirectory(outDir);
        using var stream = File.OpenRead(dataFile);
        int written = 0;
        foreach (Resource resource in DataFile.Read(stream).Resources
                     .Where(item => item.ClassHash == LayeredSky.ClassHash))
        {
            LayeredSkyAsset asset = LayeredSky.Read(resource.Data);
            foreach (LayeredSkyLayer layer in asset.Layers)
            {
                string hour = layer.Hour.ToString("00.0", CultureInfo.InvariantCulture).Replace('.', '_');
                string name = Path.Combine(outDir, $"{resource.Name}_{hour}h.dds");
                using var file = File.Create(name);
                DdsWriter.Write(file, PixelFormat.R16G16B16A16Float,
                [
                    new TextureMipLevel
                    {
                        Level = 0,
                        Width = layer.Width,
                        Height = layer.Height * Math.Max(1, layer.Depth),
                        Pixels = layer.Texels,
                    },
                ]);
                written++;
            }
        }

        Console.WriteLine($"{written} layer(s) written to {outDir}");
        return 0;
    }
}
