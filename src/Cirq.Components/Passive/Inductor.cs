using Cirq.Core.Simulation;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// Inductor solved in branch-current form, so its current is a first-class unknown. That makes the
/// bias point a plain short circuit and lets <see cref="Transformer"/> couple two of them.
/// </summary>
public partial class Inductor : TwoTerminalComponent
{
    private double _previousCurrent;
    private double _previousVoltage;

    public Inductor(double inductance = 1e-3)
    {
        Inductance = inductance;
    }

    /// <summary>Inductance in henries.</summary>
    [ObservableProperty]
    public partial double Inductance { get; set; }

    /// <summary>Winding resistance in ohms, stamped in series with the ideal inductance.</summary>
    [ObservableProperty]
    public partial double SeriesResistance { get; set; }

    /// <summary>Optional initial current enforced during the bias-point solve.</summary>
    [ObservableProperty]
    public partial double? InitialCurrent { get; set; }

    public override string ComponentType => "Inductor";

    public override string DesignatorPrefix => "L";

    public override string ValueLabel => SiPrefix.Format(Inductance, "H");

    public override int VoltageSourceCount => 1;

    /// <summary>Current through the inductor at the last accepted time point.</summary>
    public double Current => _previousCurrent;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var na = system.Node(A);
        var nb = system.Node(B);
        var branch = system.Branch(this);

        // The branch current leaves node A and enters node B, whichever equation governs it.
        system.Add(na, branch, 1.0);
        system.Add(nb, branch, -1.0);

        if (!state.IsTransient)
        {
            // Under "use initial conditions" the inductor starts at its initial current (zero
            // unless stated), so a supply applied at t=0 is a genuine step. Without it the bias
            // point treats the inductor as the short circuit it is at DC.
            var initial = InitialCurrent ?? (state.Settings.UseInitialConditions ? 0.0 : null);

            if (initial is { } i0)
            {
                system.Add(branch, branch, 1.0);
                system.AddRhs(branch, i0);
                return;
            }
        }

        // v_a - v_b - (Leq + Rs)·i = history
        system.Add(branch, na, 1.0);
        system.Add(branch, nb, -1.0);
        system.Add(branch, branch, -SeriesResistance);

        if (!state.IsTransient) return;

        var (leq, history) = CompanionTerms(state);
        system.Add(branch, branch, -leq);
        system.AddRhs(branch, history);
    }

    /// <summary>
    /// Returns the companion inductance coefficient and the history term for the current step.
    /// Shared with <see cref="Transformer"/>, which stamps the same row shape.
    /// </summary>
    internal (double Leq, double History) CompanionTerms(SimulationState state)
    {
        var h = state.TimeStep;
        var l = Math.Max(Inductance, 1e-18);

        if (state.EffectiveIntegration == IntegrationMethod.Trapezoidal)
        {
            var leq = 2.0 * l / h;
            return (leq, -leq * _previousCurrent - _previousVoltage);
        }

        var be = l / h;
        return (be, -be * _previousCurrent);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        _previousCurrent = system.BranchCurrent(this);
        // The stored voltage is the inductive part only: the resistive drop is not integrated.
        _previousVoltage = VoltageAcross(system, A, B) - SeriesResistance * _previousCurrent;
    }

    public override void ResetState()
    {
        _previousCurrent = 0;
        _previousVoltage = 0;
    }

    partial void OnInductanceChanged(double value) => NotifyValueChanged();
}
