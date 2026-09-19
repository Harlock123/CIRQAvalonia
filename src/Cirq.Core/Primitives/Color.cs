namespace Cirq.Core.Primitives;

/// <summary>Framework-independent RGBA colour (used for probe trace colours).</summary>
public readonly record struct Color(byte R, byte G, byte B, byte A = 255)
{
    public static Color FromRgb(byte r, byte g, byte b) => new(r, g, b);

    public static Color FromHex(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 6)
            return new Color(Convert.ToByte(s[..2], 16), Convert.ToByte(s.Substring(2, 2), 16), Convert.ToByte(s.Substring(4, 2), 16));
        if (s.Length == 8)
            return new Color(Convert.ToByte(s.Substring(2, 2), 16), Convert.ToByte(s.Substring(4, 2), 16), Convert.ToByte(s.Substring(6, 2), 16), Convert.ToByte(s[..2], 16));
        throw new FormatException($"Unsupported colour literal '{hex}'.");
    }

    public string ToHex() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";

    public static readonly Color Yellow = FromRgb(0xFF, 0xD7, 0x33);
    public static readonly Color Cyan = FromRgb(0x33, 0xD7, 0xFF);
    public static readonly Color Magenta = FromRgb(0xFF, 0x5C, 0xC8);
    public static readonly Color Green = FromRgb(0x5C, 0xE0, 0x7A);
    public static readonly Color Orange = FromRgb(0xFF, 0x9A, 0x3C);
    public static readonly Color White = FromRgb(0xF0, 0xF0, 0xF0);

    /// <summary>Default probe palette, cycled as probes are created.</summary>
    public static readonly IReadOnlyList<Color> ProbePalette = [Yellow, Cyan, Magenta, Green, Orange, White];
}
