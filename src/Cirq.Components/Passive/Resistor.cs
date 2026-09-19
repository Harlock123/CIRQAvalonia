using Cirq.Core.Simulation;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;
using Cirq.Core.Probing;
using Cirq.Core.Topology;

namespace Cirq.Components.Passive;

/// <summary>Ideal linear resistor.</summary>
public partial class Resistor : TwoTerminalComponent, ICurrentReporting
{
    public Resistor(double resistance = 1e3)
    {
        Resistance = resistance;
    }

    /// <summary>Resistance in ohms. Must be strictly positive.</summary>
    [ObservableProperty]
    public partial double Resistance { get; set; }

    public override string ComponentType => "Resistor";

    public override string DesignatorPrefix => "R";

    public override string ValueLabel => SiPrefix.Format(Resistance, "Ω");

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        // A zero-ohm resistor would be an infinite conductance; clamp to a realistic wire resistance.
        var r = Math.Max(Resistance, 1e-9);
        system.StampConductance(system.Node(A), system.Node(B), 1.0 / r);
    }

    /// <summary>Ohm's law, which is all a resistor's current has ever been.</summary>
    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        CurrentIntoPin(terminal, VoltageAcross(system, A, B) / Math.Max(Resistance, 1e-12));

    partial void OnResistanceChanged(double value) => NotifyValueChanged();
}
