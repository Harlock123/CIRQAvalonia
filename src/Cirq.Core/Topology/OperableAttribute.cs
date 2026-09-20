namespace Cirq.Core.Topology;

/// <summary>
/// Marks a property as something a person operates rather than configures.
/// <para>
/// The distinction is between a part's <i>settings</i> and its <i>controls</i>. A resistor's
/// resistance is a setting: you choose it when you design the circuit and it stays chosen. A
/// switch's position is a control: it is what somebody does to the circuit while it is running,
/// and so is a potentiometer's wiper, the light falling on an LDR, or a magnet held near a Hall
/// sensor. Settings live in the properties panel; controls are gathered into one place so they can
/// be reached without hunting round the schematic for the part they belong to.
/// </para>
/// <para>
/// Declared here rather than as a list in the user interface, so that a new component gets a
/// control by saying so on the property itself and nothing else has to be edited to notice.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class OperableAttribute(string label) : Attribute
{
    /// <summary>What to call the control. Short: it sits beside the part's designator.</summary>
    public string Label { get; } = label;

    /// <summary>Low end of the range, for the things that have one.</summary>
    public double Minimum { get; init; }

    /// <summary>High end of the range.</summary>
    public double Maximum { get; init; }

    /// <summary>Unit shown beside the value, e.g. "lx" or "°C". Empty for a bare number.</summary>
    public string Unit { get; init; } = string.Empty;

    /// <summary>
    /// Whether the range spans too many decades for a linear slider to be usable. Light is the
    /// case that forces it: a slider from darkness to direct sun puts everything a circuit
    /// actually does in the first half-millimetre of travel.
    /// </summary>
    public bool IsLogarithmic { get; init; }

    /// <summary>
    /// Whether changing it changes the shape of the circuit rather than a value in it, so the
    /// engine has to rebuild rather than carry on.
    /// </summary>
    public bool IsStructural { get; init; }

    /// <summary>True when the attribute carries a usable range.</summary>
    public bool HasRange => Maximum > Minimum;
}
