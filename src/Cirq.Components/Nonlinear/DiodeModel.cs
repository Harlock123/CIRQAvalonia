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
    double BreakdownCurrent = 1e-3,
    double EnergyGap = 1.11,
    double TemperatureExponent = 3.0)
{
    /// <summary>
    /// The saturation current at a given temperature. Everything else in the model is quoted at
    /// 27 °C; this is the one parameter that has to move, and it is the one that decides which way
    /// the forward drop goes as the part warms up.
    /// </summary>
    public double SaturationCurrentAt(double kelvin) =>
        JunctionTemperature.SaturationCurrentAt(
            SaturationCurrent, kelvin, EmissionCoefficient, EnergyGap, TemperatureExponent);

    /// <summary>Small-signal switching diode, ~0.72 V at 10 mA.</summary>
    public static readonly DiodeModel D1N4148 = new("1N4148", 2.52e-9, 1.752, 0.568, 75.0);

    /// <summary>General purpose rectifier, ~0.9 V at 1 A.</summary>
    public static readonly DiodeModel D1N4001 = new("1N4001", 1.4e-8, 1.984, 0.0334, 50.0);

    /// <summary>Schottky rectifier, ~0.4 V forward drop.</summary>
    public static readonly DiodeModel D1N5817 =
        new("1N5817", 1.03e-5, 1.0, 0.0255, 20.0, EnergyGap: 0.69, TemperatureExponent: 2.0);

    /// <summary>Red LED, ~1.9 V forward drop.</summary>
    public static readonly DiodeModel LedRed = new("LED (red)", 1e-19, 1.8, 3.0, 5.0, EnergyGap: 2.50);

    /// <summary>Green LED, ~2.1 V forward drop.</summary>
    public static readonly DiodeModel LedGreen = new("LED (green)", 1e-21, 1.8, 3.0, 5.0, EnergyGap: 2.69);

    /// <summary>Yellow LED, ~2.0 V forward drop.</summary>
    public static readonly DiodeModel LedYellow = new("LED (yellow)", 1e-20, 1.8, 3.0, 5.0, EnergyGap: 2.59);

    /// <summary>Amber LED, between red and yellow at ~1.95 V.</summary>
    public static readonly DiodeModel LedAmber = new("LED (amber)", 5e-20, 1.8, 3.0, 5.0, EnergyGap: 2.54);

    /// <summary>
    /// Blue LED, ~3.1 V forward drop.
    /// <para>
    /// The high emission coefficient is not a fudge: wide-gap LEDs really do have ideality factors
    /// of two and upwards, because recombination in the active region competes with diffusion in a
    /// way it does not in a silicon rectifier. Fitting one with an ideality near unity forces a
    /// saturation current down around 1e-29 A, and a junction that extreme sits so far out on the
    /// exponential that the solver's own overflow guard starts shaping the answer — which showed
    /// up here as a temperature coefficient a fifth of the size it should be.
    /// </para>
    /// </summary>
    public static readonly DiodeModel LedBlue = new("LED (blue)", 2.5e-19, 3.0, 4.0, 5.0, EnergyGap: 4.07);

    /// <summary>White LED, essentially a blue die with phosphor, ~3.1 V.</summary>
    public static readonly DiodeModel LedWhite = new("LED (white)", 1.8e-19, 3.0, 4.0, 5.0, EnergyGap: 4.10);

    /// <summary>
    /// The band gap figures on the LEDs above are <b>fitted to their temperature coefficient</b>
    /// rather than being the literal gap of the die, and that is deliberate. An LED's forward drop
    /// falls with temperature like any other diode's — two to four millivolts a degree — but the
    /// simple expression that gets a silicon junction right from its real 1.11 eV does not get a
    /// wide-gap one right from its real gap. Since the parameter's whole job in this model is to
    /// set which way and how fast the drop moves, it is fitted to the measurement. That is what a
    /// model parameter is; the alternative is a number that is honest about the crystal and wrong
    /// about the part.
    /// </summary>

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
