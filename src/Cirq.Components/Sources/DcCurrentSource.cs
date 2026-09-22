using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Sources;

/// <summary>Independent DC current source. Current flows through the device from + to -.</summary>
public partial class DcCurrentSource : TwoTerminalComponent, IAcExcitation
{
    public DcCurrentSource(double current = 1e-3) : base("+", "-")
    {
        Current = current;
    }

    /// <summary>Source current in amps.</summary>
    [ObservableProperty]
    [Operable("Current", Minimum = 0, Maximum = 2, Unit = "A")]
    public partial double Current { get; set; }

    /// <summary>Shunt resistance across the source in ohms; null models an ideal source.</summary>
    [ObservableProperty]
    public partial double? ShuntResistance { get; set; }

    public Terminal Positive => A;

    public Terminal Negative => B;

    public override string ComponentType => "DC Current Source";

    public override string DesignatorPrefix => "I";

    public override string ValueLabel => SiPrefix.Format(Current, "A");

    /// <summary>
    /// How hard it drives a frequency sweep, in amps — nothing to do with its DC current, which a
    /// small signal does not see.
    /// </summary>
    [ObservableProperty]
    public partial double AcMagnitude { get; set; }

    /// <summary>Phase of that excitation, in degrees.</summary>
    [ObservableProperty]
    public partial double AcPhaseDegrees { get; set; }

    public override void StampAc(AcSystem system, SimulationState state)
    {
        var phasor = AcSystem.Phasor(AcMagnitude, AcPhaseDegrees);

        system.AddRhs(system.Node(A), phasor);
        system.AddRhs(system.Node(B), -phasor);
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var na = system.Node(A);
        var nb = system.Node(B);
        if (ShuntResistance is { } r and > 0) system.StampConductance(na, nb, 1.0 / r);
        system.StampCurrentSource(na, nb, Current);
    }

    partial void OnCurrentChanged(double value) => NotifyValueChanged();
}
