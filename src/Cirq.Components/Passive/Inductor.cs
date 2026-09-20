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

    /// <summary>
    /// Current at which the core has given up half the inductance, in amps. Zero — the default —
    /// means an ideal inductor that never saturates, which is what every circuit built before this
    /// existed assumes.
    /// <para>
    /// Saturation is the failure that destroys switching converters. The core stores flux until it
    /// cannot store any more, and past that point the winding is just a piece of wire: the
    /// inductance collapses, nothing is left to limit di/dt, and the current goes wherever the
    /// supply will let it in the time the switch is still on. It does not announce itself on a
    /// voltage trace — you have to be looking at the current.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double SaturationCurrent { get; set; }

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
            // Only at the bias point: a frequency sweep has no starting current, and pinning one
            // would open-circuit the inductor at every frequency.
            var initial = state.IsBiasPoint
                ? InitialCurrent ?? (state.Settings.UseInitialConditions ? 0.0 : null)
                : null;

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
    /// The DC stamp already put <c>v_a − v_b − Rs·i = 0</c> on the branch, so all the frequency
    /// does is add the reactance to the resistance already sitting there.
    /// </summary>
    public override void StampAc(AcSystem system, SimulationState state) =>
        system.AddInductance(system.Branch(this), EffectiveInductance);

    /// <summary>
    /// Returns the companion inductance coefficient and the history term for the current step.
    /// Shared with <see cref="Transformer"/>, which stamps the same row shape.
    /// </summary>
    /// <summary>
    /// Inductance at the current the winding is presently carrying.
    /// <para>
    /// A fourth power rather than a square: a real core holds its inductance almost to the knee
    /// and then loses it quickly, where a gentler law would sag from the start and make every
    /// inductor behave slightly wrongly rather than one behave very wrongly at the right moment.
    /// </para>
    /// </summary>
    public double EffectiveInductance
    {
        get
        {
            var nominal = Math.Max(Inductance, 1e-18);
            if (SaturationCurrent <= 0) return nominal;

            var ratio = Math.Abs(_previousCurrent) / SaturationCurrent;
            return nominal / (1.0 + (ratio * ratio * ratio * ratio));
        }
    }

    /// <summary>True when the core has lost a noticeable part of its inductance.</summary>
    public bool IsSaturating =>
        SaturationCurrent > 0 && Math.Abs(_previousCurrent) > SaturationCurrent * 0.75;

    public virtual IReadOnlyList<string> Violations => IsSaturating
        ? [$"carrying {SiPrefix.Format(Math.Abs(_previousCurrent), "A")} against a " +
           $"{SiPrefix.Format(SaturationCurrent, "A")} saturation current — the core is giving up " +
           "and the inductance with it, so there is less and less holding the current back"]
        : [];

    internal (double Leq, double History) CompanionTerms(SimulationState state)
    {
        var h = state.TimeStep;
        var l = EffectiveInductance;

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
