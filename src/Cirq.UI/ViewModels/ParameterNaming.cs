using Cirq.Core.Units;

namespace Cirq.UI.ViewModels;

/// <summary>
/// How a component's property is named and given a unit for display.
/// <para>
/// Shared rather than duplicated, because the properties panel and the hover card describe the
/// same property and have to agree about it. Two copies would drift the first time a unit was
/// added to one of them, and the symptom would be a value that read differently depending on
/// where you happened to look at it.
/// </para>
/// </summary>
public static class ParameterNaming
{

    /// <summary>Units inferred from the property name, so values display as "4.7k&#937;" or "100nF".</summary>
    private static readonly (string Suffix, string Unit)[] UnitHints =
    [
        ("Resistance", "Ω"),
        ("Capacitance", "F"),
        ("Inductance", "H"),
        ("Frequency", "Hz"),
        ("Voltage", "V"),
        ("Current", "A"),
        ("Delay", "s"),
        ("Time", "s"),
        ("Temperature", "°C"),
        ("SlewRate", "V/s"),
        ("Vcc", "V"),
        ("Vih", "V"),
        ("Vil", "V"),
        ("Voh", "V"),
        ("Vol", "V"),
        ("Drop", "V"),
        ("Threshold", "V"),
        ("High", "V"),
        ("Low", "V"),
    ];

    /// <summary>The unit a property's name implies, or nothing when it implies none.</summary>
    public static string UnitFor(string propertyName)
    {
        foreach (var (suffix, unit) in UnitHints)
            if (propertyName.Contains(suffix, StringComparison.OrdinalIgnoreCase))
                return unit;
        return string.Empty;
    }

    /// <summary>
    /// A number as it should read for a given unit.
    /// <para>
    /// SI prefixes are right for ohms, farads, henries, hertz, volts, amps and seconds, and wrong
    /// for the two cases this handles: a temperature is not measured in kilodegrees, and a bare
    /// ratio like a duty cycle of 0.5 reads as "500m" under a prefix, which is correct and useless.
    /// Both go out plainly instead.
    /// </para>
    /// <para>
    /// For display only. The properties panel formats with <see cref="SiPrefix"/> directly,
    /// because its text is also the box you type into and has to come back through the parser.
    /// </para>
    /// </summary>
    public static string Format(double value, string unit)
    {
        if (unit.Length > 0 && unit != "°C") return SiPrefix.Format(value, unit);

        var text = Math.Abs(value) >= 1000 || (value != 0 && Math.Abs(value) < 0.001)
            ? value.ToString("0.###")
            : value.ToString("0.####");

        return unit.Length == 0 ? text : $"{text} {unit}";
    }

    /// <summary>Turns "AmplitudePeakToPeak" into "Amplitude Peak To Peak".</summary>
    public static string Humanise(string name)
    {
        var result = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) result.Append(' ');
            result.Append(name[i]);
        }
        return result.ToString();
    }
}
