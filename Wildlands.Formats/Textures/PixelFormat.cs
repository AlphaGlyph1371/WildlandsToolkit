namespace Wildlands.Formats.Textures;

// The values are DXGI formats

public enum PixelFormat
{
    Unknown = 0,
    R16G16B16A16Float = 10,
    R8G8B8A8Signed = 31,
    R8 = 61,
    Bc1 = 71,
    Bc2 = 74,
    Bc3 = 77,
    B8G8R8A8 = 87,
    Bc7 = 98,
}

public static class PixelFormats
{
    static readonly PixelFormat[] Table =
    [
        PixelFormat.B8G8R8A8,
        PixelFormat.R8G8B8A8Signed,
        PixelFormat.Bc1,
        PixelFormat.Bc1,
        PixelFormat.Bc2,
        PixelFormat.Bc3,
        PixelFormat.Unknown,
        PixelFormat.R8,
        PixelFormat.Unknown,
        PixelFormat.Bc7,

        PixelFormat.R8,
        PixelFormat.R8,

        PixelFormat.Unknown,
        PixelFormat.Unknown,
        PixelFormat.R16G16B16A16Float,
        PixelFormat.Unknown,
        PixelFormat.Unknown,
        PixelFormat.Unknown,
        PixelFormat.Unknown,

        PixelFormat.R16G16B16A16Float,
    ];

    public static PixelFormat FromIndex(int index)
    {
        return index >= 0 && index < Table.Length ? Table[index] : PixelFormat.Unknown;
    }

    public static bool IsBlockCompressed(this PixelFormat format) => format switch
    {
        PixelFormat.Bc1 or PixelFormat.Bc2 or PixelFormat.Bc3 or PixelFormat.Bc7 => true,
        _ => false,
    };

    public static int BytesPerBlock(this PixelFormat format) => format switch
    {
        PixelFormat.Bc1 => 8,
        PixelFormat.Bc2 or PixelFormat.Bc3 or PixelFormat.Bc7 => 16,
        _ => 0,
    };

    public static int BitsPerPixel(this PixelFormat format) => format switch
    {
        PixelFormat.R16G16B16A16Float => 64,
        PixelFormat.B8G8R8A8 or PixelFormat.R8G8B8A8Signed => 32,
        PixelFormat.R8 => 8,
        _ => 0,
    };

    public static int LevelSize(this PixelFormat format, int width, int height)
    {
        if (format.IsBlockCompressed())
        {
            int blocksX = Math.Max(1, (width + 3) / 4);
            int blocksY = Math.Max(1, (height + 3) / 4);
            return blocksX * blocksY * format.BytesPerBlock();
        }

        return Math.Max(1, width) * Math.Max(1, height) * format.BitsPerPixel() / 8;
    }
}
