namespace Cirq.Core.Digital;

/// <summary>
/// Voltage thresholds and drive characteristics that define a logic family, and with it the
/// analog/digital boundary behaviour of every gate and IC.
/// </summary>
public sealed record LogicLevels
{
    /// <summary>Minimum input voltage recognised as a logic high.</summary>
    public double Vih { get; init; } = 2.0;

    /// <summary>Maximum input voltage recognised as a logic low.</summary>
    public double Vil { get; init; } = 0.8;

    /// <summary>Output voltage driven when high.</summary>
    public double Voh { get; init; } = 3.4;

    /// <summary>Output voltage driven when low.</summary>
    public double Vol { get; init; } = 0.2;

    /// <summary>Thevenin output resistance of a driven pin, in ohms.</summary>
    public double OutputResistance { get; init; } = 25.0;

    /// <summary>Leakage resistance presented by an input pin to ground.</summary>
    public double InputResistance { get; init; } = 1e7;

    /// <summary>Nominal supply for the family.</summary>
    public double Vcc { get; init; } = 5.0;

    /// <summary>Default low-to-high / high-to-low propagation delay, in seconds.</summary>
    public double PropagationDelay { get; init; } = 10e-9;

    /// <summary>Classifies an analog voltage into a logic state using the family thresholds.</summary>
    public LogicState Classify(double voltage, LogicState previous = LogicState.Unknown)
    {
        if (voltage >= Vih) return LogicState.High;
        if (voltage <= Vil) return LogicState.Low;
        // Inside the forbidden band the pin holds its previous interpretation (hysteresis),
        // which keeps slow analog edges from producing spurious mid-rail glitches.
        return previous.IsDriven() ? previous : LogicState.Unknown;
    }

    public double VoltageFor(LogicState state) => state switch
    {
        LogicState.High => Voh,
        LogicState.Low => Vol,
        _ => (Voh + Vol) * 0.5,
    };

    /// <summary>Standard 74xx bipolar TTL levels.</summary>
    public static readonly LogicLevels Ttl = new();

    /// <summary>74HC-style CMOS levels on a 5 V rail.</summary>
    public static readonly LogicLevels Cmos5V = new()
    {
        Name = "CMOS 5V",
        Vih = 3.5, Vil = 1.5, Voh = 4.95, Vol = 0.05,
        OutputResistance = 50.0, Vcc = 5.0, PropagationDelay = 8e-9,
    };

    /// <summary>Name shown in the inspector's family picker.</summary>
    public string Name { get; init; } = "TTL";

    /// <summary>CMOS levels on a 3.3 V rail.</summary>
    public static readonly LogicLevels Cmos33V = new()
    {
        Name = "CMOS 3.3V",
        Vih = 2.0, Vil = 0.8, Voh = 3.25, Vol = 0.05,
        OutputResistance = 50.0, Vcc = 3.3, PropagationDelay = 6e-9,
    };

    /// <summary>The families offered in the component inspector.</summary>
    public static readonly IReadOnlyList<LogicLevels> Library = [Ttl, Cmos5V, Cmos33V];

    public override string ToString() => Name;
}
