namespace Cirq.Core.Units;

/// <summary>
/// A value as it is printed on a part rather than painted in bands.
/// </summary>
/// <param name="Code">What is written on it, e.g. <c>104K</c>.</param>
/// <param name="Digits">The significant figures part, for reading it out.</param>
/// <param name="Multiplier">What those figures are multiplied by, in words.</param>
/// <param name="ToleranceLetter">The letter, or null when the part carries none.</param>
/// <param name="ToleranceText">What that letter promises.</param>
/// <param name="Represented">The value the code actually says.</param>
/// <param name="Note">Why that is not the value asked for, or null when it is.</param>
public sealed record PrintedCode(
    string Code,
    string Digits,
    string Multiplier,
    string? ToleranceLetter,
    string? ToleranceText,
    double Represented,
    string? Note)
{
    public bool IsExact => Note is null;

    public override string ToString() => Code;
}

/// <summary>
/// The three-digit code printed on a ceramic capacitor.
/// <para>
/// It catches everybody once. A capacitor marked <c>104</c> is not 104 of anything — it is
/// <b>10 followed by four zeros, in picofarads</b>, which is 100 nF. The code is always in
/// picofarads however large the part is, which is why a 1 µF ceramic reads <c>105</c> and why
/// somebody reading it as microfarads is out by a million.
/// </para>
/// <para>
/// The tolerance letter is separate and in a different alphabet from the resistor colours: J is
/// five percent, K is ten, M is twenty. A part marked <c>104K</c> is a 100 nF at ten percent.
/// </para>
/// </summary>
public static class PrintedCodes
{
    /// <summary>
    /// The EIA tolerance letters, tightest first. A part is marked with the letter that is no
    /// looser than what it is sold as, for the same reason a resistor's band is.
    /// </summary>
    private static readonly (double Fraction, string Letter, string Text)[] Letters =
    [
        (0.001, "W", "± 0.1 %"),
        (0.0025, "C", "± 0.25 %"),
        (0.005, "D", "± 0.5 %"),
        (0.01, "F", "± 1 %"),
        (0.02, "G", "± 2 %"),
        (0.05, "J", "± 5 %"),
        (0.10, "K", "± 10 %"),
        (0.20, "M", "± 20 %"),
        (0.80, "Z", "+ 80 %, − 20 %"),
    ];

    /// <summary>
    /// The code for a capacitance in farads, or null when it has none — a part too small or too
    /// large for three digits in picofarads to reach.
    /// </summary>
    public static PrintedCode? ForCapacitance(double farads, double tolerance)
    {
        if (farads <= 0 || double.IsNaN(farads) || double.IsInfinity(farads)) return null;

        var picofarads = farads * 1e12;

        // Under ten picofarads the code is written out rather than coded — 4p7 is marked 4R7 or
        // just 4.7, because two digits and a multiplier cannot say it.
        if (picofarads < 10) return null;

        var exponent = (int)Math.Floor(Math.Log10(picofarads)) - 1;
        var mantissa = (int)Math.Round(picofarads / Math.Pow(10, exponent));

        if (mantissa >= 100)
        {
            mantissa /= 10;
            exponent++;
        }

        // The third character is the number of zeros, so it has to be a single digit.
        if (exponent is < 0 or > 9) return null;

        var digits = mantissa.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var code = digits + exponent.ToString(System.Globalization.CultureInfo.InvariantCulture);

        string? letter = null;
        string? text = null;

        if (tolerance > 0)
        {
            var match = Letters.FirstOrDefault(l => tolerance <= l.Fraction * 1.0000001, Letters[^1]);

            letter = match.Letter;
            text = match.Text;
        }

        var represented = mantissa * Math.Pow(10, exponent) * 1e-12;

        var note = Math.Abs(represented - farads) <= Math.Abs(farads) * 1e-9
            ? null
            : "the nearest three digits can say";

        return new PrintedCode(
            code + (letter ?? string.Empty),
            digits,
            exponent == 0 ? "and no zeros" : $"and {exponent} zero{(exponent == 1 ? string.Empty : "s")}",
            letter,
            text,
            represented,
            note);
    }
}
