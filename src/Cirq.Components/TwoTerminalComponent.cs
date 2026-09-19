using Cirq.Core.Primitives;
using Cirq.Core.Topology;

namespace Cirq.Components;

/// <summary>
/// Convenience base for devices with a single pair of pins, laid out horizontally on the grid.
/// </summary>
public abstract class TwoTerminalComponent : CircuitComponent
{
    /// <summary>Half the default body length, in canvas units.</summary>
    public const double LeadLength = 30.0;

    protected TwoTerminalComponent(string aName = "A", string bName = "B")
    {
        A = new Terminal("a", aName, TerminalType.Passive, new Point(-LeadLength, 0));
        B = new Terminal("b", bName, TerminalType.Passive, new Point(LeadLength, 0));
        Terminals = [A, B];
    }

    public Terminal A { get; }

    public Terminal B { get; }

    /// <summary>Voltage across the device from <see cref="A"/> to <see cref="B"/> in the present solution.</summary>
    protected static double VoltageAcross(Cirq.Core.Simulation.MnaSystem system, Terminal plus, Terminal minus) =>
        system.NodeVoltage(plus) - system.NodeVoltage(minus);

    /// <summary>
    /// Turns a current measured from <see cref="A"/> to <see cref="B"/> into one flowing *into*
    /// the probed pin, so clamping either end of a part reads equal and opposite, as on a bench.
    /// </summary>
    protected double CurrentIntoPin(Terminal terminal, double currentAToB) =>
        ReferenceEquals(terminal, B) ? -currentAToB : currentAToB;
}
