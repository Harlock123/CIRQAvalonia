using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// A zero-crossing solid-state relay: an opto-isolated input driving a triac output.
/// <para>
/// The palette already has all three pieces — an optocoupler, a triac and a relay — and this is
/// not simply those in a box. What makes an SSR its own part is a detail that sounds like a
/// limitation and is the reason to buy one: <b>it waits</b>. Turn the input on halfway through a
/// mains cycle and nothing happens until the supply next passes through zero volts, which may be
/// up to ten milliseconds later.
/// </para>
/// <para>
/// That wait is what keeps it quiet. Switching a load on at the peak of the mains means taking the
/// current from nothing to amps in a microsecond, and a step that steep is a radio transmitter:
/// it is why a mechanical relay clicks on the AM band across the room and why a light dimmer needs
/// a choke in it. Starting at the zero crossing means starting from no current at all, and the
/// interference goes with it.
/// </para>
/// <para>
/// The same property is why an SSR <b>cannot dim anything</b>. Phase control works by firing
/// <i>late</i> in each half cycle, deliberately, and a part that will only ever fire at the
/// beginning of one has no way to do it. The lamp dimmer example in this library uses a bare triac
/// and a diac for exactly that reason.
/// </para>
/// <para>
/// Two things it shares with the triac inside it. It <b>turns itself off</b> at the next zero
/// crossing once the input goes away, because that is when the current falls below the holding
/// current — so switching off also waits. And it has a real <b>forward drop</b> of a volt or so,
/// which at ten amps is ten watts of heat and the reason these come bolted to a slab of aluminium.
/// </para>
/// </summary>
public partial class SolidStateRelay : CircuitComponent, ICurrentReporting
{
    /// <summary>Which way the load current was last flowing, for the forward drop's sign.</summary>
    private int _conductingSign = 1;

    private bool _armed;
    private double _previousLoadVoltage;

    public SolidStateRelay()
    {
        ControlPositive = new Terminal("in+", "+", TerminalType.Input, new Point(-50, -20));
        ControlNegative = new Terminal("in-", "-", TerminalType.Input, new Point(-50, 20));

        LoadA = new Terminal("l1", "L1", TerminalType.Passive, new Point(50, -20));
        LoadB = new Terminal("l2", "L2", TerminalType.Passive, new Point(50, 20));

        Terminals = [ControlPositive, ControlNegative, LoadA, LoadB];
    }

    /// <summary>Control input, positive. An LED inside, so it wants a current not a voltage.</summary>
    public Terminal ControlPositive { get; }

    public Terminal ControlNegative { get; }

    /// <summary>One side of the switched load.</summary>
    public Terminal LoadA { get; }

    /// <summary>The other.</summary>
    public Terminal LoadB { get; }

    /// <summary>Forward drop of the input LED, in volts.</summary>
    [ObservableProperty]
    public partial double InputForwardVoltage { get; set; } = 1.2;

    /// <summary>Current the input needs before it will turn on, in amps.</summary>
    [ObservableProperty]
    public partial double InputTriggerCurrent { get; set; } = 5e-3;

    /// <summary>Resistance of the input LED once conducting, in ohms.</summary>
    [ObservableProperty]
    public partial double InputResistance { get; set; } = 30.0;

    /// <summary>
    /// How close to zero the load voltage must come before it will fire, in volts. Real parts
    /// quote a few volts rather than none, which is why an SSR on a DC supply that never crosses
    /// zero may never turn on at all.
    /// </summary>
    [ObservableProperty]
    public partial double ZeroCrossWindow { get; set; } = 5.0;

    /// <summary>The output's drop while conducting, in volts.</summary>
    [ObservableProperty]
    public partial double ForwardDrop { get; set; } = 1.1;

    /// <summary>Resistance of the output in series with that drop, in ohms.</summary>
    [ObservableProperty]
    public partial double OnResistance { get; set; } = 0.05;

    /// <summary>Leakage resistance of the output while off, in ohms.</summary>
    [ObservableProperty]
    public partial double OffResistance { get; set; } = 1e7;

    /// <summary>Current below which the output drops out, in amps — the triac's holding current.</summary>
    [ObservableProperty]
    public partial double HoldingCurrent { get; set; } = 20e-3;

    public override string ComponentType => "Solid-State Relay";

    public override string DesignatorPrefix => "K";

    public override string ValueLabel => IsConducting
        ? SiPrefix.Format(Math.Abs(LoadCurrent), "A")
        : IsCommanded ? "waiting for zero" : "off";

    public override bool IsNonlinear => true;

    /// <summary>True while the input LED has enough current to be on.</summary>
    public bool IsCommanded { get; private set; }

    /// <summary>True while the output is actually conducting.</summary>
    public bool IsConducting { get; private set; }

    /// <summary>
    /// True while the input is on and the output is not — the wait for a zero crossing, which is
    /// the whole character of the part and lasts up to half a mains cycle.
    /// </summary>
    public bool IsWaitingForZero => IsCommanded && !IsConducting;

    /// <summary>Current through the load at the last solved point, in amps.</summary>
    public double LoadCurrent { get; private set; }

    /// <summary>Current through the input LED at the last solved point, in amps.</summary>
    public double InputCurrent { get; private set; }

    /// <summary>What the output is dissipating, in watts — the reason these need a heatsink.</summary>
    public double Dissipation => IsConducting ? Math.Abs(LoadCurrent) * ForwardDrop : 0;

    public IReadOnlyList<string> Violations
    {
        get
        {
            List<string> found = [];

            if (IsWaitingForZero && _armed)
            {
                found.Add(
                    "commanded on, but the load voltage has not been near zero since — a " +
                    "zero-crossing relay cannot switch a DC supply, because there is no crossing " +
                    "for it to wait for");
            }

            if (Dissipation > 10)
            {
                found.Add(
                    $"dissipating {SiPrefix.Format(Dissipation, "W")} in the output — a volt or so " +
                    "of drop at this current needs a heatsink, not a PCB pad");
            }

            return found;
        }
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var inPlus = system.Node(ControlPositive);
        var inMinus = system.Node(ControlNegative);

        // The input LED: a drop in series with a resistance, which is what makes the control
        // current rather than the control voltage the thing that matters.
        var conductance = 1.0 / Math.Max(InputResistance, 1e-3);
        system.StampNorton(inPlus, inMinus, conductance, -InputForwardVoltage * conductance);

        var loadA = system.Node(LoadA);
        var loadB = system.Node(LoadB);

        if (!IsConducting)
        {
            system.StampConductance(loadA, loadB, 1.0 / Math.Max(OffResistance, 1.0));
            return;
        }

        var on = 1.0 / Math.Max(OnResistance, 1e-6);
        system.StampNorton(loadA, loadB, on, -_conductingSign * ForwardDrop * on);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var across = system.NodeVoltage(LoadA) - system.NodeVoltage(LoadB);

        InputCurrent = Math.Max(
            (system.NodeVoltage(ControlPositive) - system.NodeVoltage(ControlNegative) - InputForwardVoltage)
            / Math.Max(InputResistance, 1e-3), 0.0);

        var commanded = InputCurrent >= InputTriggerCurrent;

        LoadCurrent = IsConducting
            ? (across - (_conductingSign * ForwardDrop)) / Math.Max(OnResistance, 1e-6)
            : across / Math.Max(OffResistance, 1.0);

        if (commanded && !IsCommanded) _armed = true;
        IsCommanded = commanded;

        if (IsConducting)
        {
            // Like the triac it is built from, it lets go when the current through it falls below
            // the holding current — which on AC happens at every zero crossing. So switching off
            // waits for one too.
            if (Math.Abs(LoadCurrent) < HoldingCurrent && !ShouldFire(across, commanded))
                IsConducting = false;
            else
                _conductingSign = Math.Sign(LoadCurrent) == 0 ? _conductingSign : Math.Sign(LoadCurrent);
        }
        else if (ShouldFire(across, commanded))
        {
            IsConducting = true;
            _armed = false;
            _conductingSign = across >= 0 ? 1 : -1;
            NotifyValueChanged();
        }

        _previousLoadVoltage = across;
    }

    /// <summary>
    /// Whether this is a moment it may start conducting: commanded on, and the load voltage near
    /// enough to zero. Either side of zero counts, and so does having crossed it since the last
    /// point — at a coarse time step the solver can step straight over the crossing, and a part
    /// that only looked at the instant would miss it and wait another half cycle.
    /// </summary>
    private bool ShouldFire(double across, bool commanded)
    {
        if (!commanded) return false;

        var window = Math.Max(ZeroCrossWindow, 1e-6);

        return Math.Abs(across) <= window
               || (Math.Sign(across) != Math.Sign(_previousLoadVoltage) && _previousLoadVoltage != 0);
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        if (ReferenceEquals(terminal, LoadA)) return LoadCurrent;
        if (ReferenceEquals(terminal, LoadB)) return -LoadCurrent;

        return ReferenceEquals(terminal, ControlPositive) ? InputCurrent : -InputCurrent;
    }

    public override void ResetState()
    {
        IsCommanded = false;
        IsConducting = false;
        LoadCurrent = 0;
        InputCurrent = 0;
        _conductingSign = 1;
        _armed = false;
        _previousLoadVoltage = 0;
    }
}
