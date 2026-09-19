using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Components.Sources;

/// <summary>The explicit 0 V reference. Every net it touches becomes MNA node -1.</summary>
public class Ground : CircuitComponent, IGroundReference
{
    public Ground()
    {
        Pin = new Terminal("gnd", "GND", TerminalType.Ground, new Point(0, -20));
        Terminals = [Pin];
    }

    public Terminal Pin { get; }

    public Terminal GroundTerminal => Pin;

    public override string ComponentType => "Ground";

    public override string DesignatorPrefix => "GND";

    public override string ValueLabel => "0 V";

    /// <summary>Ground contributes nothing: it is the datum the whole system is referenced to.</summary>
    public override void StampMatrix(MnaSystem system, SimulationState state) { }
}
