using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// Two magnetically coupled windings. Both windings are solved in branch-current form, and the
/// mutual term <c>M = k·sqrt(L1·L2)</c> appears as an off-diagonal coefficient linking the two
/// branch unknowns.
/// </summary>
public partial class Transformer : CircuitComponent
{
    private double _i1Prev, _i2Prev, _v1Prev, _v2Prev;

    public Transformer(double primaryInductance = 1e-3, double secondaryInductance = 1e-3, double coupling = 0.99)
    {
        PrimaryInductance = primaryInductance;
        SecondaryInductance = secondaryInductance;
        Coupling = coupling;

        P1 = new Terminal("p1", "P1", TerminalType.Passive, new Point(-30, -20));
        P2 = new Terminal("p2", "P2", TerminalType.Passive, new Point(-30, 20));
        S1 = new Terminal("s1", "S1", TerminalType.Passive, new Point(30, -20));
        S2 = new Terminal("s2", "S2", TerminalType.Passive, new Point(30, 20));
        Terminals = [P1, P2, S1, S2];
    }

    public Terminal P1 { get; }
    public Terminal P2 { get; }
    public Terminal S1 { get; }
    public Terminal S2 { get; }

    [ObservableProperty]
    public partial double PrimaryInductance { get; set; }

    [ObservableProperty]
    public partial double SecondaryInductance { get; set; }

    /// <summary>Coupling factor k, constrained to (0, 1].</summary>
    [ObservableProperty]
    public partial double Coupling { get; set; }

    public override string ComponentType => "Transformer";

    public override string DesignatorPrefix => "T";

    public override int VoltageSourceCount => 2;

    public override string ValueLabel =>
        $"{SiPrefix.Format(PrimaryInductance, "H")}:{SiPrefix.Format(SecondaryInductance, "H")} k={Coupling:0.##}";

    /// <summary>Mutual inductance in henries.</summary>
    public double MutualInductance =>
        Math.Clamp(Coupling, 0, 1) * Math.Sqrt(Math.Max(PrimaryInductance, 0) * Math.Max(SecondaryInductance, 0));

    /// <summary>Ideal turns ratio implied by the two inductances.</summary>
    public double TurnsRatio => Math.Sqrt(Math.Max(SecondaryInductance, 1e-18) / Math.Max(PrimaryInductance, 1e-18));

    public double PrimaryCurrent => _i1Prev;

    public double SecondaryCurrent => _i2Prev;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var p1 = system.Node(P1);
        var p2 = system.Node(P2);
        var s1 = system.Node(S1);
        var s2 = system.Node(S2);
        var b1 = system.Branch(this, 0);
        var b2 = system.Branch(this, 1);

        // Branch-current incidence for both windings.
        system.Add(p1, b1, 1.0);
        system.Add(p2, b1, -1.0);
        system.Add(s1, b2, 1.0);
        system.Add(s2, b2, -1.0);

        if (!state.IsTransient && state.Settings.UseInitialConditions)
        {
            // Both windings start unenergised, matching the inductor's initial-condition handling.
            system.Add(b1, b1, 1.0);
            system.Add(b2, b2, 1.0);
            return;
        }

        system.Add(b1, p1, 1.0);
        system.Add(b1, p2, -1.0);
        system.Add(b2, s1, 1.0);
        system.Add(b2, s2, -1.0);

        if (!state.IsTransient) return; // Bias point: both windings are shorts.

        var h = state.TimeStep;
        var trapezoidal = state.EffectiveIntegration == IntegrationMethod.Trapezoidal;
        var scale = (trapezoidal ? 2.0 : 1.0) / h;

        var l1 = Math.Max(PrimaryInductance, 1e-18) * scale;
        var l2 = Math.Max(SecondaryInductance, 1e-18) * scale;
        var m = MutualInductance * scale;

        system.Add(b1, b1, -l1);
        system.StampBranchCoupling(b1, b2, -m);
        system.Add(b2, b2, -l2);
        system.StampBranchCoupling(b2, b1, -m);

        var h1 = -l1 * _i1Prev - m * _i2Prev - (trapezoidal ? _v1Prev : 0);
        var h2 = -m * _i1Prev - l2 * _i2Prev - (trapezoidal ? _v2Prev : 0);
        system.AddRhs(b1, h1);
        system.AddRhs(b2, h2);
    }

    /// <summary>
    /// Both windings and the coupling between them. The DC stamp has already shorted each branch
    /// and put its winding resistance on the diagonal, so this adds the reactance to each and the
    /// mutual term that makes it a transformer rather than two unrelated inductors.
    /// </summary>
    public override void StampAc(AcSystem system, SimulationState state)
    {
        var b1 = system.Branch(this, 0);
        var b2 = system.Branch(this, 1);

        system.AddInductance(b1, Math.Max(PrimaryInductance, 1e-18));
        system.AddInductance(b2, Math.Max(SecondaryInductance, 1e-18));
        system.AddMutualInductance(b1, b2, MutualInductance);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        _i1Prev = system.BranchCurrent(this, 0);
        _i2Prev = system.BranchCurrent(this, 1);
        _v1Prev = system.NodeVoltage(P1) - system.NodeVoltage(P2);
        _v2Prev = system.NodeVoltage(S1) - system.NodeVoltage(S2);
    }

    public override void ResetState()
    {
        _i1Prev = _i2Prev = _v1Prev = _v2Prev = 0;
    }

    partial void OnCouplingChanged(double value)
    {
        if (value is <= 0 or > 1) Coupling = Math.Clamp(value, 1e-6, 1.0);
        NotifyValueChanged();
    }

    partial void OnPrimaryInductanceChanged(double value) => NotifyValueChanged();

    partial void OnSecondaryInductanceChanged(double value) => NotifyValueChanged();
}
