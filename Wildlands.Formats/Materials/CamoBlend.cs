using System;

namespace Wildlands.Formats.Materials;

public static class CamoBlend
{
    public static bool IsMarked(byte blue, byte green, byte red)
    {
        int other = Math.Max(green, blue);
        return red > 28 && red > other * 2 && Math.Abs(green - blue) <= 26;
    }

    public static int Count(byte[] diffuse, int width, int height)
    {
        int marked = 0;

        for (int i = 0; i < width * height; i++)
        {
            if (IsMarked(diffuse[i * 4], diffuse[i * 4 + 1], diffuse[i * 4 + 2]))
                marked++;
        }

        return marked;
    }

    public static int Apply(byte[] diffuse, int width, int height, byte[] pattern, int patternWidth, int patternHeight)
    {
        double sum = 0;
        int marked = 0;

        for (int i = 0; i < width * height; i++)
        {
            int at = i * 4;
            if (!IsMarked(diffuse[at], diffuse[at + 1], diffuse[at + 2]))
                continue;

            marked++;
            sum += Brightness(diffuse, at);
        }

        if (marked == 0)
            return 0;

        double mean = sum / marked;
        if (mean < 1)
            mean = 1;

        for (int y = 0; y < height; y++)
        {
            int sourceY = Math.Min(patternHeight - 1, y * patternHeight / height);

            for (int x = 0; x < width; x++)
            {
                int at = (y * width + x) * 4;
                if (!IsMarked(diffuse[at], diffuse[at + 1], diffuse[at + 2]))
                    continue;

                int sourceX = Math.Min(patternWidth - 1, x * patternWidth / width);
                int from = (sourceY * patternWidth + sourceX) * 4;

                double factor = Brightness(diffuse, at) / mean;

                diffuse[at + 0] = Scale(pattern[from + 0], factor);
                diffuse[at + 1] = Scale(pattern[from + 1], factor);
                diffuse[at + 2] = Scale(pattern[from + 2], factor);
            }
        }

        return marked;
    }

    static double Brightness(byte[] pixels, int at)
    {
        return 0.299 * pixels[at + 2] + 0.587 * pixels[at + 1] + 0.114 * pixels[at];
    }

    static byte Scale(byte value, double factor)
    {
        return (byte)Math.Clamp(value * factor, 0, 255);
    }
}
