using Cirq.Core.Simulation;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>Ideal linear resistor.</summary>
public partial class Resistor : TwoTerminalComponent
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

    partial void OnResistanceChanged(double value) => NotifyValueChanged();
}
