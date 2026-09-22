using System.Globalization;

namespace Cirq.Components.Spice;

/// <summary>
/// Reading the numbers out of a SPICE model card.
/// <para>
/// They are not quite ordinary numbers. SPICE has its own suffix set, and one of them is a trap
/// that has been catching people since the seventies: <b>M means milli and MEG means mega</b>, and
/// the whole thing is case-insensitive. <c>1M</c> is a thousandth. A resistor written <c>1M</c>
/// expecting a megohm is a thousand million times wrong, and nothing in the file will say so.
/// </para>
/// <para>
/// Anything after the suffix is ignored, which is also SPICE's rule: <c>1kOhm</c>, <c>2.2uF</c>
/// and <c>10MegHz</c> all parse, because the unit is decoration and the letter that matters is the
/// first one after the digits.
/// </para>
/// </summary>
public static class SpiceValue
{
    /// <summary>Parses a SPICE number, or returns null when it is not one.</summary>
    public static double? Parse(string? text)
    {
        var token = (text ?? string.Empty).Trim();
        if (token.Length == 0) return null;

        // The mantissa: digits, sign, decimal point, and an exponent if it is written that way.
        var end = 0;
        var seenDigit = false;

        while (end < token.Length)
        {
            var c = token[end];

            if (char.IsAsciiDigit(c)) { seenDigit = true; end++; continue; }
            if (c is '+' or '-' && end == 0) { end++; continue; }
            if (c == '.') { end++; continue; }

            // An exponent, but only when it is one: the E in "1E-9" is followed by a number, and
            // the E of a suffix is not a suffix SPICE has.
            if ((c is 'e' or 'E') && seenDigit && end + 1 < token.Length &&
                (char.IsAsciiDigit(token[end + 1]) || token[end + 1] is '+' or '-'))
            {
                end++;
                if (end < token.Length && token[end] is '+' or '-') end++;

                while (end < token.Length && char.IsAsciiDigit(token[end])) end++;
                break;
            }

            break;
        }

        if (!seenDigit) return null;

        if (!double.TryParse(
                token[..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var mantissa))
        {
            return null;
        }

        return mantissa * SuffixOf(token[end..]);
    }

    /// <summary>
    /// The multiplier a suffix stands for. MEG is checked before M, which is the whole of the
    /// difference between a megohm and a milliohm.
    /// </summary>
    private static double SuffixOf(string rest)
    {
        var tail = rest.TrimStart();
        if (tail.Length == 0) return 1.0;

        if (tail.StartsWith("MEG", StringComparison.OrdinalIgnoreCase)) return 1e6;
        if (tail.StartsWith("MIL", StringComparison.OrdinalIgnoreCase)) return 25.4e-6;

        return char.ToUpperInvariant(tail[0]) switch
        {
            'T' => 1e12,
            'G' => 1e9,
            'K' => 1e3,
            'M' => 1e-3,
            'U' => 1e-6,
            'N' => 1e-9,
            'P' => 1e-12,
            'F' => 1e-15,
            _ => 1.0,
        };
    }
}
