using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>Datasheet figures for an insulated-gate bipolar transistor.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="ThresholdVoltage">Gate-emitter voltage at which the channel begins, in volts.</param>
/// <param name="TransconductanceParameter">Square-law gain of the MOS input, in A/V².</param>
/// <param name="OffsetVoltage">The junction drop the collector current must climb over, in volts.</param>
/// <param name="OnResistance">Slope of the on-state above that offset, in ohms.</param>
/// <param name="TailTime">Time constant of the current tail after turn-off, in seconds.</param>
/// <param name="TailFraction">How much of the on-state current the tail starts at, 0 to 1.</param>
public sealed record IgbtModel(
    string Name,
    double ThresholdVoltage,
    double TransconductanceParameter,
    double OffsetVoltage,
    double OnResistance,
    double TailTime,
    double TailFraction)
{
    /// <summary>A 600 V, 40 A general-purpose part of the sort used in motor drives.</summary>
    public static readonly IgbtModel Irg4Bc30 = new("IRG4BC30", 5.0, 8.0, 1.0, 0.035, 300e-9, 0.15);

    /// <summary>A fast one, traded against a higher on-state drop — the usual bargain.</summary>
    public static readonly IgbtModel Fgh40 = new("FGH40N60", 5.5, 12.0, 1.2, 0.025, 120e-9, 0.10);

    /// <summary>A big slow one for mains-frequency switching, where the tail costs nothing.</summary>
    public static readonly IgbtModel Stgw60 = new("STGW60H65", 5.0, 20.0, 0.9, 0.012, 900e-9, 0.22);

    public static readonly IReadOnlyList<IgbtModel> Library = [Irg4Bc30, Fgh40, Stgw60];

    public override string ToString() => Name;
}

/// <summary>
/// An insulated-gate bipolar transistor: a MOSFET gate driving a bipolar output stage.
/// <para>
/// It exists because the two devices either side of it in the palette each fail at what the other
/// does. A power <b>MOSFET</b> is a resistor when it is on, and that resistance rises roughly as
/// the square of the voltage it is built to block — a 600 V MOSFET has a dreadful on-resistance,
/// which is why they are rare above about 250 V. A <b>bipolar</b> transistor has a fixed
/// saturation drop instead of a resistance, so it does not care about the voltage rating, but it
/// is current-driven and needs amps of base drive to switch tens of amps.
/// </para>
/// <para>
/// An IGBT is the obvious combination: an insulated gate you drive like a MOSFET, in front of a
/// bipolar output that conducts like a bipolar. That gives it the defining characteristic to watch
/// for here — its on-state is a <b>voltage offset</b> of a volt or two plus a small resistance,
/// where a MOSFET's is a resistance alone. Put one against a MOSFET at ten amps and the IGBT looks
/// poor; at six hundred volts and a hundred amps there is no MOSFET to compare it against.
/// </para>
/// <para>
/// And the thing that decides where you may use it: the <b>current tail</b>. Turning the gate off
/// stops the MOS channel at once, but the bipolar section is full of stored charge that has
/// nowhere to go but recombine, so the collector current does not stop — it drops sharply and then
/// trails away over a fraction of a microsecond, with the full supply voltage across the device
/// the whole time. That tail is most of an IGBT's switching loss and the reason they run at a few
/// kilohertz where MOSFETs run at hundreds. It is modelled, so a switching circuit built round one
/// gets hot in the right way.
/// </para>
/// <para>
/// There is <b>no body diode</b>, unlike the MOSFETs here — an IGBT will not conduct backwards.
/// Anything inductive needs a diode across it, which is why the parts sold as half-bridge modules
/// have one built in beside each transistor.
/// </para>
/// </summary>
public partial class Igbt : CircuitComponent, ICurrentReporting
{
    private double _tailStart;
    private double _tailFrom = double.NegativeInfinity;
    private bool _wasConducting;

    public Igbt(IgbtModel? model = null)
    {
        Model = model ?? IgbtModel.Irg4Bc30;

        Collector = new Terminal("c", "C", TerminalType.Passive, new Point(0, -34));
        Gate = new Terminal("g", "G", TerminalType.Input, new Point(-40, 0));
        Emitter = new Terminal("e", "E", TerminalType.Passive, new Point(0, 34));

        Terminals = [Collector, Gate, Emitter];
    }

    public Terminal Collector { get; }

    public Terminal Gate { get; }

    public Terminal Emitter { get; }

    [ObservableProperty]
    public partial IgbtModel Model { get; set; }

    /// <summary>Gate input resistance, in ohms. Large; the gate is insulated.</summary>
    [ObservableProperty]
    public partial double GateResistance { get; set; } = 1e9;

    public override string ComponentType => "IGBT";

    public override string DesignatorPrefix => "Q";

    public override string ValueLabel => IsConducting
        ? $"{Model.Name} · {SiPrefix.Format(CollectorCurrent, "A")}"
        : Model.Name;

    public override bool IsNonlinear => true;

    /// <summary>Collector current at the last solved point, in amps.</summary>
    public double CollectorCurrent { get; private set; }

    /// <summary>Collector-emitter voltage at the last solved point.</summary>
    public double CollectorVoltage { get; private set; }

    /// <summary>Gate-emitter voltage at the last solved point.</summary>
    public double GateVoltage { get; private set; }

    /// <summary>True while the gate is above threshold.</summary>
    public bool IsOn => GateVoltage > Model.ThresholdVoltage;

    /// <summary>True while any current is flowing, tail included.</summary>
    public bool IsConducting => Math.Abs(CollectorCurrent) > 1e-6;

    /// <summary>
    /// True while the gate is off and the stored charge is still coming out. This is the interval
    /// that costs the energy: full voltage across the device and current still flowing through it.
    /// </summary>
    public bool IsTailing => !IsOn && Math.Abs(TailCurrent(CurrentTime)) > 1e-6;

    /// <summary>Instantaneous power in the device, in watts.</summary>
    public double Dissipation => Math.Abs(CollectorCurrent * CollectorVoltage);

    private double CurrentTime { get; set; }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var collector = system.Node(Collector);
        var gate = system.Node(Gate);
        var emitter = system.Node(Emitter);

        CurrentTime = state.Time;

        // The gate is insulated; it takes nothing but leakage, and the resistor is there to give
        // the node something to be solved against.
        system.StampResistor(gate, emitter, Math.Max(GateResistance, 1.0));

        var vge = system.IterationVoltageAcross(gate, emitter);

        // Never linearised below the offset, where this device has no conductance at all: a
        // linear step from there overshoots into the reverse-blocking region and the solve walks
        // away from the answer rather than towards it.
        var vceRaw = system.IterationVoltageAcross(collector, emitter);
        var vce = Math.Max(vceRaw, Model.OffsetVoltage);

        var (current, gm, gce) = Evaluate(vge, vce);

        // The tail is stored charge leaving, and it does not depend on the gate or on vce — it is
        // simply a current that is still flowing. An independent source is exactly what that is.
        var tail = TailCurrent(state.Time);

        var equivalent = current - (gm * vge) - (gce * vce) + tail;

        system.StampNorton(collector, emitter, gce, equivalent);
        system.StampVccs(collector, emitter, gate, emitter, gm);
    }

    /// <summary>
    /// Collector current and its two slopes.
    /// <para>
    /// The MOS input decides how much current the device <i>can</i> pass, by the same square law a
    /// MOSFET's channel follows. The bipolar output decides what it costs to pass it: nothing
    /// flows until the collector is above the junction's offset, and above that the current rises
    /// against the on-resistance until it meets what the channel will allow. Written as one smooth
    /// expression rather than two cases, because a corner in a device curve is a corner Newton has
    /// to be walked around.
    /// </para>
    /// </summary>
    private (double Current, double Gm, double Gce) Evaluate(double vge, double vce)
    {
        var overdrive = vge - Model.ThresholdVoltage;
        if (overdrive <= 0) return (0, 0, 0);

        var k = Math.Max(Model.TransconductanceParameter, 1e-12);
        var channel = k * overdrive * overdrive;

        var excess = Math.Max(vce - Model.OffsetVoltage, 0.0);
        var r = Math.Max(Model.OnResistance, 1e-6);

        // The knee comes out of the two figures rather than being another parameter: the current
        // the channel allows, against the resistance it has to climb to get there.
        var knee = Math.Max(channel * r, 1e-9);
        var u = excess / knee;
        var decay = Math.Exp(-u);

        var current = channel * (1.0 - decay);

        var gce = decay / r;
        var gm = ((1.0 - decay) - (u * decay)) * 2.0 * k * overdrive;

        return (current, gm, Math.Max(gce, 0));
    }

    /// <summary>What is left of the stored charge at a given moment, in amps.</summary>
    private double TailCurrent(double time)
    {
        if (_tailStart <= 0 || double.IsNegativeInfinity(_tailFrom)) return 0;

        var elapsed = time - _tailFrom;
        if (elapsed < 0) return _tailStart;

        var tau = Math.Max(Model.TailTime, 1e-12);
        if (elapsed > tau * 12) return 0;

        return _tailStart * Math.Exp(-elapsed / tau);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        CurrentTime = state.Time;

        CollectorVoltage = system.NodeVoltage(Collector) - system.NodeVoltage(Emitter);
        GateVoltage = system.NodeVoltage(Gate) - system.NodeVoltage(Emitter);

        var conducting = GateVoltage > Model.ThresholdVoltage;
        var vce = Math.Max(CollectorVoltage, Model.OffsetVoltage);
        var (current, _, _) = Evaluate(GateVoltage, vce);

        // The gate has just gone: whatever was flowing starts coming out as stored charge.
        if (_wasConducting && !conducting)
        {
            _tailStart = CollectorCurrent * Math.Clamp(Model.TailFraction, 0.0, 1.0);
            _tailFrom = state.Time;
        }
        else if (conducting)
        {
            _tailStart = 0;
            _tailFrom = double.NegativeInfinity;
        }

        _wasConducting = conducting;

        CollectorCurrent = current + TailCurrent(state.Time);
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        if (ReferenceEquals(terminal, Gate)) return 0;

        return ReferenceEquals(terminal, Collector) ? CollectorCurrent : -CollectorCurrent;
    }

    public override void ResetState()
    {
        CollectorCurrent = 0;
        CollectorVoltage = 0;
        GateVoltage = 0;

        _tailStart = 0;
        _tailFrom = double.NegativeInfinity;
        _wasConducting = false;
    }

    partial void OnModelChanged(IgbtModel value) => NotifyValueChanged();
}
