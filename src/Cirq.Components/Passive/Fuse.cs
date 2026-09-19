using Cirq.Core.Simulation;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;
using Cirq.Core.Probing;
using Cirq.Core.Topology;

namespace Cirq.Components.Passive;

/// <summary>
/// A fuse: a low resistance that opens permanently once it has carried too much current for too
/// long.
/// <para>
/// Blowing is governed by the melting integral rather than by instantaneous current, which is what
/// a real fuse does and why one survives an inrush that is many times its rating. Heat has to
/// accumulate. A fuse carries its rated current indefinitely, so only the excess counts — the
/// integral used here is of <c>i² − rated²</c>, which is zero at the rating and grows with the
/// square of the overload beyond it.
/// </para>
/// </summary>
public partial class Fuse : TwoTerminalComponent, ICurrentReporting
{
    public Fuse(double ratedCurrent = 1.0)
    {
        RatedCurrent = ratedCurrent;
    }

    /// <summary>Current the fuse carries indefinitely, in amps.</summary>
    [ObservableProperty]
    public partial double RatedCurrent { get; set; }

    /// <summary>
    /// Melting integral in A²s: how much excess heat the element absorbs before it opens. A 1 A
    /// quick-blow fuse is around 0.5 A²s, which is about half a second at twice its rating.
    /// </summary>
    [ObservableProperty]
    public partial double MeltingIntegral { get; set; } = 0.5;

    /// <summary>Resistance of the intact element, in ohms.</summary>
    [ObservableProperty]
    public partial double ColdResistance { get; set; } = 0.05;

    /// <summary>Resistance once it has blown, in ohms.</summary>
    [ObservableProperty]
    public partial double BlownResistance { get; set; } = 1e9;

    public override string ComponentType => "Fuse";

    public override string DesignatorPrefix => "F";

    public override string ValueLabel =>
        HasBlown ? "BLOWN" : SiPrefix.Format(RatedCurrent, "A");

    /// <summary>True once the element has opened. It does not recover.</summary>
    public bool HasBlown { get; private set; }

    /// <summary>Heat absorbed so far, in A²s, as a fraction of <see cref="MeltingIntegral"/>.</summary>
    public double MeltFraction { get; private set; }

    /// <summary>Current through the element at the last solved point, in amps.</summary>
    public double Current { get; private set; }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        CurrentIntoPin(terminal, Current);

    /// <summary>Largest current the element has carried, in amps.</summary>
    public double PeakCurrent { get; private set; }

    /// <summary>What happened to this fuse, if anything.</summary>
    public IReadOnlyList<string> Violations =>
        HasBlown
            ? [$"blown — it carried up to {PeakCurrent:0.00} A against a {RatedCurrent:0.##} A rating"]
            : [];

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        system.StampConductance(
            system.Node(A), system.Node(B),
            1.0 / Math.Max(HasBlown ? BlownResistance : ColdResistance, 1e-9));

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        Current = (system.NodeVoltage(A) - system.NodeVoltage(B))
                  / Math.Max(HasBlown ? BlownResistance : ColdResistance, 1e-9);

        if (HasBlown) return;

        PeakCurrent = Math.Max(PeakCurrent, Math.Abs(Current));

        var excess = (Current * Current) - (RatedCurrent * RatedCurrent);
        if (excess <= 0 || state.TimeStep <= 0) return;

        MeltFraction += excess * state.TimeStep / Math.Max(MeltingIntegral, 1e-12);

        if (MeltFraction >= 1.0)
        {
            MeltFraction = 1.0;
            HasBlown = true;
            NotifyValueChanged();
        }
    }

    public override void ResetState()
    {
        HasBlown = false;
        MeltFraction = 0;
        Current = 0;
        PeakCurrent = 0;
    }

    partial void OnRatedCurrentChanged(double value) => NotifyValueChanged();
}
