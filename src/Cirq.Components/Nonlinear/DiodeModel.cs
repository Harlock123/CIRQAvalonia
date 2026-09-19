namespace Cirq.Components.Nonlinear;

/// <summary>
/// Shockley model parameters for a diode. The presets are fitted to the usual datasheet
/// forward-voltage figures at the stated test current.
/// </summary>
public sealed record DiodeModel(
    string Name,
    double SaturationCurrent,
    double EmissionCoefficient,
    double SeriesResistance,
    double BreakdownVoltage,
    double BreakdownCurrent = 1e-3)
{
    /// <summary>Small-signal switching diode, ~0.72 V at 10 mA.</summary>
    public static readonly DiodeModel D1N4148 = new("1N4148", 2.52e-9, 1.752, 0.568, 75.0);

    /// <summary>General purpose rectifier, ~0.9 V at 1 A.</summary>
    public static readonly DiodeModel D1N4001 = new("1N4001", 1.4e-8, 1.984, 0.0334, 50.0);

    /// <summary>Schottky rectifier, ~0.4 V forward drop.</summary>
    public static readonly DiodeModel D1N5817 = new("1N5817", 1.03e-5, 1.0, 0.0255, 20.0);

    /// <summary>Red LED, ~1.9 V forward drop.</summary>
    public static readonly DiodeModel LedRed = new("LED (red)", 1e-19, 1.8, 3.0, 5.0);

    /// <summary>Green LED, ~2.1 V forward drop.</summary>
    public static readonly DiodeModel LedGreen = new("LED (green)", 1e-21, 1.8, 3.0, 5.0);

    /// <summary>Yellow LED, ~2.0 V forward drop.</summary>
    public static readonly DiodeModel LedYellow = new("LED (yellow)", 1e-20, 1.8, 3.0, 5.0);

    /// <summary>Amber LED, between red and yellow at ~1.95 V.</summary>
    public static readonly DiodeModel LedAmber = new("LED (amber)", 5e-20, 1.8, 3.0, 5.0);

    /// <summary>Blue LED, ~3.0 V forward drop.</summary>
    public static readonly DiodeModel LedBlue = new("LED (blue)", 2e-29, 1.9, 4.0, 5.0);

    /// <summary>White LED, essentially a blue die with phosphor, ~3.1 V.</summary>
    public static readonly DiodeModel LedWhite = new("LED (white)", 1e-29, 1.9, 4.0, 5.0);

    /// <summary>Builds a Zener model with the requested breakdown voltage.</summary>
    public static DiodeModel Zener(double breakdownVoltage) =>
        new($"Zener {breakdownVoltage:0.#}V", 1e-14, 1.0, 1.0, breakdownVoltage);

    public static readonly IReadOnlyList<DiodeModel> Library =
    [
        D1N4148, D1N4001, D1N5817,
        LedRed, LedAmber, LedYellow, LedGreen, LedBlue, LedWhite,
        Zener(3.3), Zener(5.1), Zener(9.1), Zener(12.0),
    ];

    public override string ToString() => Name;
}
