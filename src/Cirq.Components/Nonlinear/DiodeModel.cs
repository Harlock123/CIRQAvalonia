using System.Threading;
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
    /// Roughly what this part drops carrying a given current at room temperature, from the
    /// junction equation and the bulk resistance.
    /// <para>
    /// The back-of-an-envelope figure, and deliberately: it is what somebody writes down before
    /// drawing anything, and the exact answer comes out of a solve. A red LED reads about 1.8 V
    /// and a 1N4001 at an amp about 0.9, which are the numbers people already carry.
    /// </para>
    /// </summary>
    public double ForwardVoltageAt(double amps = 0.01)
    {
        if (amps <= 0) return 0;

        const double thermal = 0.025852;

        var junction = EmissionCoefficient * thermal
                       * Math.Log((amps / Math.Max(SaturationCurrent, 1e-30)) + 1.0);

        return junction + (amps * SeriesResistance);
    }

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

    private static readonly IReadOnlyList<DiodeModel> BuiltIn =
    [
        D1N4148, D1N4001, D1N5817,
        LedRed, LedAmber, LedYellow, LedGreen, LedBlue, LedWhite,
        Zener(3.3), Zener(5.1), Zener(9.1), Zener(12.0),
    ];

    /// <summary>Models brought in from SPICE cards, which shadow a built-in of the same name.</summary>
    private static readonly List<DiodeModel> ImportedModels = [];

    /// <summary>
    /// Guards the imported list. It is static, and a save now reads it for every model a circuit
    /// names — so an import from one place while a circuit is being written somewhere else would
    /// otherwise read a list mid-edit, which a plain list answers by corrupting itself rather
    /// than by complaining.
    /// </summary>
    private static readonly Lock Gate = new();

    /// <summary>
    /// Every model the application knows, built in or imported. A saved circuit names its model
    /// and finds it again in here, so anything imported has to be registered before a circuit
    /// using it is opened.
    /// </summary>
    public static IReadOnlyList<DiodeModel> Library
    {
        get
        {
            lock (Gate)
            {
                if (ImportedModels.Count == 0) return BuiltIn;

                // An import shadows a built-in of the same name rather than replacing it, which is
                // what lets the import be removed again and the built-in come back. Somebody
                // importing a card called 1N4148 — much the likeliest name there is — should not be
                // able to delete the one that shipped.
                List<DiodeModel> library = [.. BuiltIn.Select(
                    m => ImportedModels.FirstOrDefault(
                        i => string.Equals(i.Name, m.Name, StringComparison.OrdinalIgnoreCase)) ?? m)];

                library.AddRange(ImportedModels.Where(
                    i => !BuiltIn.Any(m => string.Equals(m.Name, i.Name, StringComparison.OrdinalIgnoreCase))));

                return library;
            }
        }
    }

    /// <summary>
    /// Adds a model, replacing any with the same name. Used by the SPICE importer; a part brought
    /// in from a datasheet is then offered everywhere a built-in one is.
    /// </summary>
    public static void Register(DiodeModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        lock (Gate)
        {
            ImportedModels.RemoveAll(m => string.Equals(m.Name, model.Name, StringComparison.OrdinalIgnoreCase));
            ImportedModels.Add(model);
        }
    }

    /// <summary>
    /// Removes an imported model by name. Built-in models are not removable — an import of the
    /// same name was shadowing one, and taking the import away brings it back.
    /// </summary>
    public static bool Unregister(string name)
    {
        lock (Gate)
        {
            return ImportedModels.RemoveAll(
                m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;
        }
    }

    public override string ToString() => Name;
}
