using System.Globalization;
using System.Text;

namespace Cirq.Core.Units;

/// <summary>
/// Parses and formats engineering values written with SI prefixes, e.g. <c>10k</c>, <c>2.2M</c>,
/// <c>100R</c>, <c>4u7</c>, <c>1n</c>. Parsing is culture invariant and case sensitive for the
/// prefixes where case carries meaning (<c>m</c> = milli, <c>M</c> = mega).
/// </summary>
public static class SiPrefix
{
    private static readonly Dictionary<char, double> Multipliers = new()
    {
        ['f'] = 1e-15,
        ['p'] = 1e-12,
        ['n'] = 1e-9,
        ['u'] = 1e-6,
        ['µ'] = 1e-6,
        ['μ'] = 1e-6,
        ['m'] = 1e-3,
        ['k'] = 1e3,
        ['K'] = 1e3,
        ['M'] = 1e6,
        ['G'] = 1e9,
        ['T'] = 1e12,
    };

    /// <summary>Prefix characters that may also be used as a decimal point (R notation: 4k7 == 4700).</summary>
    private const string InfixCapable = "fpnuµμmkKMGTR";

    /// <summary>Unit suffixes that are stripped before parsing (Ω, F, H, V, A, Hz, s).</summary>
    private static readonly string[] UnitSuffixes = ["Ohm", "ohm", "Hz", "hz", "Ω", "F", "H", "V", "A", "s"];

    public static bool TryParse(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();

        // Strip a trailing unit token, but never strip a character that is itself a prefix
        // (e.g. the 'F' of "10F" is farads, the 'm' of "10m" is milli).
        foreach (var unit in UnitSuffixes)
        {
            if (s.Length > unit.Length && s.EndsWith(unit, StringComparison.Ordinal))
            {
                var trimmed = s[..^unit.Length].TrimEnd();
                if (trimmed.Length > 0 && (char.IsDigit(trimmed[^1]) || InfixCapable.Contains(trimmed[^1])))
                {
                    s = trimmed;
                    break;
                }
            }
        }

        if (s.Length == 0) return false;

        // Trailing prefix form: "10k", "2.2M", "100R"
        var last = s[^1];
        if (last == 'R' || last == 'r')
        {
            return double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        if (Multipliers.TryGetValue(last, out var mult))
        {
            if (!double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mantissa))
                return false;
            value = mantissa * mult;
            return true;
        }

        // Infix form: "4k7" == 4700, "4R7" == 4.7, "1u5" == 1.5e-6
        for (var i = 1; i < s.Length - 1; i++)
        {
            var c = s[i];
            if (!InfixCapable.Contains(c)) continue;
            var whole = s[..i];
            var frac = s[(i + 1)..];
            if (!frac.All(char.IsDigit)) continue;
            var combined = $"{whole}.{frac}";
            if (!double.TryParse(combined, NumberStyles.Float, CultureInfo.InvariantCulture, out var m)) return false;
            value = c is 'R' ? m : m * Multipliers[c];
            return true;
        }

        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static double Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"'{text}' is not a valid engineering value.");

    /// <summary>Formats a value using the nearest engineering prefix, e.g. 4700 -> "4.7k".</summary>
    public static string Format(double value, string unit = "", int significantDigits = 4)
    {
        if (value == 0 || double.IsNaN(value) || double.IsInfinity(value))
            return unit.Length > 0 ? $"{value:0.###}{unit}" : $"{value:0.###}";

        var abs = Math.Abs(value);
        var exponent = (int)Math.Floor(Math.Log10(abs) / 3.0) * 3;
        exponent = Math.Clamp(exponent, -15, 12);

        var scaled = value / Math.Pow(10, exponent);
        var prefix = exponent switch
        {
            -15 => "f", -12 => "p", -9 => "n", -6 => "µ", -3 => "m",
            0 => "", 3 => "k", 6 => "M", 9 => "G", 12 => "T",
            _ => "",
        };

        var rounded = Math.Round(scaled, Math.Max(0, significantDigits - 1 - (int)Math.Floor(Math.Log10(Math.Abs(scaled)))));
        var sb = new StringBuilder();
        sb.Append(rounded.ToString("0.###", CultureInfo.InvariantCulture));
        sb.Append(prefix);
        sb.Append(unit);
        return sb.ToString();
    }
}
