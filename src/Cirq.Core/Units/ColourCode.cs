namespace Cirq.Core.Units;

/// <summary>The twelve colours a band can be, plus the absence of one.</summary>
public enum BandColour
{
    None,
    Black,
    Brown,
    Red,
    Orange,
    Yellow,
    Green,
    Blue,
    Violet,
    Grey,
    White,
    Gold,
    Silver,
}

/// <summary>What one band is and what it says.</summary>
/// <param name="Colour">The colour painted on.</param>
/// <param name="Meaning">What it means in this position — "2", "× 100", "± 5 %".</param>
public sealed record ColourBand(BandColour Colour, string Meaning);

/// <summary>
/// A value as it is painted on the part.
/// </summary>
/// <param name="Bands">The bands, in reading order.</param>
/// <param name="Represented">
/// The value the bands actually say, which is not always the value asked for — a four-band code
/// carries two significant figures and cannot spell 1234.
/// </param>
/// <param name="Note">What to say about the difference, or null when there is none.</param>
public sealed record ColourCode(IReadOnlyList<ColourBand> Bands, double Represented, string? Note)
{
    public bool IsExact => Note is null;

    public override string ToString() =>
        string.Join(" ", Bands.Select(b => b.Colour.ToString().ToLowerInvariant()));
}

/// <summary>
/// Turns a value into the bands painted on a resistor or an inductor.
/// <para>
/// Reading the colour code is a real skill and an oddly durable one — it is the first thing
/// anybody learns about components and the last thing they forget. Showing the bands beside the
/// number is how it gets learned: not by being told the mnemonic, but by seeing brown-black-red
/// next to 1 kΩ enough times that the two stop being separate facts.
/// </para>
/// <para>
/// The band count follows the real convention rather than a setting. A part at five percent or
/// looser carries <b>four</b> bands — two digits, a multiplier and a tolerance — and one at two
/// percent or tighter carries <b>five</b>, because you cannot promise one percent on a value you
/// only spelled to two figures. That is why a 1 % part is marked brown-black-black-brown-brown
/// and not brown-black-red-brown.
/// </para>
/// </summary>
public static class ColourCodes
{
    /// <summary>Digit colours, black for zero through white for nine.</summary>
    private static readonly BandColour[] Digits =
    [
        BandColour.Black, BandColour.Brown, BandColour.Red, BandColour.Orange, BandColour.Yellow,
        BandColour.Green, BandColour.Blue, BandColour.Violet, BandColour.Grey, BandColour.White,
    ];

    /// <summary>
    /// Tolerance colours. Gold and silver are the loose ones and everything else is a precision
    /// part; twenty percent has no band at all, which is why an old resistor can have only three.
    /// </summary>
    private static readonly (double Fraction, BandColour Colour)[] Tolerances =
    [
        (0.0005, BandColour.Grey),
        (0.001, BandColour.Violet),
        (0.0025, BandColour.Blue),
        (0.005, BandColour.Green),
        (0.01, BandColour.Brown),
        (0.02, BandColour.Red),
        (0.05, BandColour.Gold),
        (0.10, BandColour.Silver),
    ];

    /// <summary>
    /// The bands for a value and a tolerance, or null when there are none to draw — a part with
    /// no value, or one too large or small for the multiplier band to reach.
    /// </summary>
    /// <param name="value">
    /// In the units the code itself is written in: ohms for a resistor, microhenries for an
    /// inductor. The caller scales — the table has no idea what it is counting.
    /// </param>
    /// <param name="tolerance">As a fraction. Zero means the part carries no tolerance band.</param>
    public static ColourCode? For(double value, double tolerance)
    {
        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return null;

        // Three digits for a precision part, two for an ordinary one — the convention, and the
        // reason a five-band resistor is a five-band resistor.
        var precise = tolerance > 0 && tolerance <= 0.02;
        var figures = precise ? 3 : 2;

        // The mantissa as an integer of that many figures, and the power of ten to put it back.
        var exponent = (int)Math.Floor(Math.Log10(value)) - (figures - 1);
        var mantissa = (int)Math.Round(value / Math.Pow(10, exponent));

        // Rounding 99.6 to two figures gives 100, which is three — carry it.
        var ceiling = (int)Math.Pow(10, figures);

        if (mantissa >= ceiling)
        {
            mantissa /= 10;
            exponent++;
        }

        if (mantissa < (int)Math.Pow(10, figures - 1)) return null;

        // Gold and silver multipliers divide, which is how a 4R7 is painted. Below that, and above
        // white, the code simply has no way to say it.
        if (exponent < -2 || exponent > 9) return null;

        List<ColourBand> bands = [];

        foreach (var digit in mantissa.ToString(System.Globalization.CultureInfo.InvariantCulture))
            bands.Add(new ColourBand(Digits[digit - '0'], digit.ToString()));

        bands.Add(new ColourBand(Multiplier(exponent), MultiplierText(exponent)));

        if (tolerance > 0)
        {
            var band = NearestTolerance(tolerance);

            bands.Add(new ColourBand(band.Colour, $"± {Percent(band.Fraction)}"));
        }

        var represented = mantissa * Math.Pow(10, exponent);

        // A hair's difference is rounding in the arithmetic rather than a real one; a part is not
        // made to a millionth anyway.
        var note = Math.Abs(represented - value) <= Math.Abs(value) * 1e-9
            ? null
            : $"the nearest {bands.Count} bands can say";

        return new ColourCode(bands, represented, note);
    }

    private static BandColour Multiplier(int exponent) => exponent switch
    {
        -2 => BandColour.Silver,
        -1 => BandColour.Gold,
        _ => Digits[exponent],
    };

    private static string MultiplierText(int exponent) => exponent switch
    {
        -2 => "÷ 100",
        -1 => "÷ 10",
        0 => "× 1",
        _ => $"× {Math.Pow(10, exponent):#,##0}",
    };

    /// <summary>
    /// The band a tolerance is painted as. The nearest one that is <b>no looser</b> than asked
    /// for, because a part sold as five percent may not be marked ten — the marking is a promise,
    /// and rounding it the wrong way turns it into a lie.
    /// </summary>
    private static (double Fraction, BandColour Colour) NearestTolerance(double tolerance)
    {
        foreach (var candidate in Tolerances)
            if (tolerance <= candidate.Fraction * 1.0000001) return candidate;

        return Tolerances[^1];
    }

    private static string Percent(double fraction)
    {
        var percent = fraction * 100;

        return percent < 0.1
            ? $"{percent:0.##} %"
            : percent < 1 ? $"{percent:0.#} %" : $"{percent:0.#} %";
    }
}
