namespace Wildlands.Formats.Textures;

public sealed record TexturePixels(int Width, int Height, byte[] Bgra, bool Srgb);
