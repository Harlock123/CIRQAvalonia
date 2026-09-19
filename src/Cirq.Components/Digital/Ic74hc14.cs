using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// A 74HC14 hex Schmitt-trigger inverter.
/// <para>
/// The package and pinout are the 7404's; the difference is entirely in how the inputs are read —
/// see <see cref="SchmittGateIc"/>. This is the part you reach for to clean up a slow edge,
/// debounce a contact, or — with a resistor from output back to input and a capacitor to ground —
/// build an oscillator out of a single gate.
/// </para>
/// </summary>
public sealed class Ic74hc14 : SchmittGateIc
{
    private static readonly GateDefinition[] Pinout =
    [
        new([1], 2),
        new([3], 4),
        new([5], 6),
        new([13], 12),
        new([11], 10),
        new([9], 8),
    ];

    public Ic74hc14()
        : base(14, GateFunction.Not, vccPin: 14, gndPin: 7, Pinout) =>
        PropagationDelay = 15e-9;

    public override string PartNumber => "74HC14";

    /// <summary>The input and output pins of one of the six inverters, indexed from 0.</summary>
    public (Terminal A, Terminal Y) Inverter(int index) => (GateInputs(index)[0], GateOutput(index));
}
