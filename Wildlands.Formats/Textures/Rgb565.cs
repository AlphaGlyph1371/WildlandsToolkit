namespace Wildlands.Formats.Textures;

internal static class Rgb565
{
    public static void WriteBgra(Span<byte> target, int slot, ushort value)
    {
        int red = (value >> 11) & 0x1F;
        int green = (value >> 5) & 0x3F;
        int blue = value & 0x1F;

        target[slot * 4] = (byte)((blue << 3) | (blue >> 2));
        target[slot * 4 + 1] = (byte)((green << 2) | (green >> 4));
        target[slot * 4 + 2] = (byte)((red << 3) | (red >> 2));
        target[slot * 4 + 3] = 255;
    }
}
