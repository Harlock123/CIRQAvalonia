using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;
using Cirq.Core.Probing;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// A single-pole changeover relay: an inductive coil that throws a mechanical contact.
/// <para>
/// The coil is the interesting part. It is an inductor, so interrupting its current produces
/// <c>v = L·di/dt</c> — hundreds of volts from a 5 V coil, backwards, in microseconds. That spike
/// is what destroys the transistor driving it, and why every relay driver in the world has a diode
/// across the coil. The relay reports the spike rather than quietly simulating it.
/// </para>
/// <para>
/// Pull-in and drop-out are deliberately different currents. A real relay latches: it needs more
/// current to close than to hold, so a coil drifting around its threshold does not chatter.
/// </para>
/// </summary>
public partial class Relay : CircuitComponent, ICurrentReporting
{
    private double _previousCurrent;
    private double _previousVoltage;

    public Relay()
    {
        CoilA = new Terminal("a1", "A1", TerminalType.Passive, new Point(-40, -20));
        CoilB = new Terminal("a2", "A2", TerminalType.Passive, new Point(-40, 20));
        Common = new Terminal("com", "COM", TerminalType.Passive, new Point(40, 0));
        NormallyOpen = new Terminal("no", "NO", TerminalType.Passive, new Point(40, -30));
        NormallyClosed = new Terminal("nc", "NC", TerminalType.Passive, new Point(40, 30));

        Terminals = [CoilA, CoilB, Common, NormallyOpen, NormallyClosed];
    }

    public Terminal CoilA { get; }

    public Terminal CoilB { get; }

    /// <summary>The moving contact.</summary>
    public Terminal Common { get; }

    /// <summary>Connected to <see cref="Common"/> only while the coil is energised.</summary>
    public Terminal NormallyOpen { get; }

    /// <summary>Connected to <see cref="Common"/> while the coil is at rest.</summary>
    public Terminal NormallyClosed { get; }

    /// <summary>Coil resistance in ohms. 70 R is a typical 5 V relay.</summary>
    [ObservableProperty]
    public partial double CoilResistance { get; set; } = 70.0;

    /// <summary>Coil inductance in henries — what makes the turn-off spike.</summary>
    [ObservableProperty]
    public partial double CoilInductance { get; set; } = 0.15;

    /// <summary>Coil current at which the contact throws, in amps.</summary>
    [ObservableProperty]
    public partial double PullInCurrent { get; set; } = 0.035;

    /// <summary>Coil current at which it falls back, in amps. Below pull-in, so it does not chatter.</summary>
    [ObservableProperty]
    public partial double DropOutCurrent { get; set; } = 0.015;

    /// <summary>
    /// Coil voltage above which the turn-off spike is reported, in volts. Comfortably above any
    /// sensible coil supply, and far below what an unclamped coil actually reaches.
    /// </summary>
    [ObservableProperty]
    public partial double KickbackLimit { get; set; } = 60.0;

    [ObservableProperty]
    public partial double ClosedResistance { get; set; } = 0.05;

    [ObservableProperty]
    public partial double OpenResistance { get; set; } = 1e9;

    public override string ComponentType => "Relay";

    public override string DesignatorPrefix => "K";

    public override string ValueLabel => IsEnergised ? "energised" : "at rest";

    /// <summary>The coil needs its own branch, the same as any inductor.</summary>
    public override int VoltageSourceCount => 1;

    /// <summary>True while the contact is thrown.</summary>
    public bool IsEnergised { get; private set; }

    /// <summary>Coil current at the last solved point, in amps.</summary>
    public double CoilCurrent { get; private set; }

    /// <summary>Largest magnitude of coil voltage seen, in volts.</summary>
    public double PeakCoilVoltage { get; private set; }

    /// <summary>Number of times the contact has thrown, which is what wears a relay out.</summary>
    public int Operations { get; private set; }

    /// <summary>What is wrong with how this relay is being driven, if anything.</summary>
    public IReadOnlyList<string> Violations =>
        PeakCoilVoltage > KickbackLimit
            ? [$"the coil kicked back to {PeakCoilVoltage:0} V on turn-off — fit a flyback diode " +
               "across it, or whatever is switching the coil will not survive"]
            : [];

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        StampCoil(system, state);

        // The contact is a changeover: one side is always made.
        system.StampConductance(
            system.Node(Common), system.Node(NormallyOpen),
            1.0 / Math.Max(IsEnergised ? ClosedResistance : OpenResistance, 1e-9));

        system.StampConductance(
            system.Node(Common), system.Node(NormallyClosed),
            1.0 / Math.Max(IsEnergised ? OpenResistance : ClosedResistance, 1e-9));
    }

    /// <summary>
    /// The coil, stamped as <see cref="Passive.Inductor"/> does: a branch carrying the current,
    /// with the trapezoidal companion and the winding resistance in the same row.
    /// </summary>
    private void StampCoil(MnaSystem system, SimulationState state)
    {
        var na = system.Node(CoilA);
        var nb = system.Node(CoilB);
        var branch = system.Branch(this);

        system.Add(na, branch, 1.0);
        system.Add(nb, branch, -1.0);

        if (!state.IsTransient && state.Settings.UseInitialConditions)
        {
            // Switch-on: the coil starts with no current, so energising it is a genuine ramp.
            system.Add(branch, branch, 1.0);
            return;
        }

        system.Add(branch, na, 1.0);
        system.Add(branch, nb, -1.0);
        system.Add(branch, branch, -CoilResistance);

        if (!state.IsTransient) return;

        var h = state.TimeStep;
        var l = Math.Max(CoilInductance, 1e-18);

        if (state.EffectiveIntegration == IntegrationMethod.Trapezoidal)
        {
            var leq = 2.0 * l / h;
            system.Add(branch, branch, -leq);
            system.AddRhs(branch, -leq * _previousCurrent - _previousVoltage);
        }
        else
        {
            var be = l / h;
            system.Add(branch, branch, -be);
            system.AddRhs(branch, -be * _previousCurrent);
        }
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        CoilCurrent = system.BranchCurrent(this);
        var across = system.NodeVoltage(CoilA) - system.NodeVoltage(CoilB);

        _previousCurrent = CoilCurrent;
        _previousVoltage = across - (CoilResistance * CoilCurrent);

        PeakCoilVoltage = Math.Max(PeakCoilVoltage, Math.Abs(across));

        // Hysteresis: more current to throw than to hold.
        var magnitude = Math.Abs(CoilCurrent);
        var energised = IsEnergised
            ? magnitude > DropOutCurrent
            : magnitude > PullInCurrent;

        if (energised != IsEnergised)
        {
            IsEnergised = energised;
            Operations++;
            NotifyValueChanged();
        }
    }

    /// <summary>
    /// The coil and the contacts are separate circuits, so which pin was probed decides the
    /// answer entirely. Falling back to the coil's branch current for a contact pin would report
    /// a milliamp of drive as though it were the amps the contact is switching.
    /// </summary>
    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        if (ReferenceEquals(terminal, CoilA)) return CoilCurrent;
        if (ReferenceEquals(terminal, CoilB)) return -CoilCurrent;

        var closed = IsEnergised ? NormallyOpen : NormallyClosed;
        var through = (system.NodeVoltage(Common) - system.NodeVoltage(closed))
                      / Math.Max(ClosedResistance, 1e-9);

        if (ReferenceEquals(terminal, Common)) return through;
        return ReferenceEquals(terminal, closed) ? -through : 0;
    }

    public override void ResetState()
    {
        _previousCurrent = 0;
        _previousVoltage = 0;
        CoilCurrent = 0;
        PeakCoilVoltage = 0;
        Operations = 0;
        IsEnergised = false;
    }
}
