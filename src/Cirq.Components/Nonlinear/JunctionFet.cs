using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;
using Cirq.Core.Probing;

namespace Cirq.Components.Nonlinear;

/// <summary>A JFET's datasheet numbers.</summary>
/// <param name="Name">Part number.</param>
/// <param name="IsNChannel">True for an N-channel device.</param>
/// <param name="SaturationCurrent">Drain current with the gate shorted to the source, in amps.</param>
/// <param name="PinchOffVoltage">
/// Gate-source voltage at which the channel closes, as a magnitude in volts. Negative on an
/// N-channel part in practice; the sign is handled by the polarity rather than stored here.
/// </param>
/// <param name="ChannelModulation">Output conductance per volt of drain-source, the JFET's Early effect.</param>
public sealed record JfetModel(
    string Name,
    bool IsNChannel,
    double SaturationCurrent,
    double PinchOffVoltage,
    double ChannelModulation = 0.01)
{
    public static readonly JfetModel J2N3819 = new("2N3819 (N)", true, 10e-3, 3.0);

    public static readonly JfetModel J201 = new("J201 (N)", true, 1e-3, 1.0);

    public static readonly JfetModel J2N5457 = new("2N5457 (N)", true, 3e-3, 2.0);

    public static readonly JfetModel J2N5460 = new("2N5460 (P)", false, 5e-3, 3.0);

    public static readonly IReadOnlyList<JfetModel> Library = [J2N3819, J201, J2N5457, J2N5460];

    public override string ToString() => Name;
}

/// <summary>
/// A junction field-effect transistor.
/// <para>
/// A JFET is a <b>depletion</b> device, which is what separates it from every MOSFET in the
/// library: it conducts with no gate drive at all, and you pinch the channel off by reversing the
/// gate. Zero volts on the gate gives you the full <c>I_DSS</c>; it takes a negative gate on an
/// N-channel part to turn it off. Wiring one expecting it to start off is the usual first
/// surprise.
/// </para>
/// <para>
/// The gate is a reverse-biased junction, so it draws essentially nothing — which is the reason to
/// reach for one, and also the reason forward-biasing the gate is a mistake worth catching. The
/// junctions are modelled rather than assumed away, so a forward-driven gate conducts like the
/// diode it is.
/// </para>
/// </summary>
public partial class JunctionFet : CircuitComponent, ICurrentReporting
{
    private double _previousGateSource;
    private double _previousGateDrain;
    private bool _limitedThisIteration;

    public JunctionFet(JfetModel? model = null)
    {
        Model = model ?? JfetModel.J2N3819;

        // The same pin geometry as the bipolar and MOSFET symbols, so the three sit on the grid
        // interchangeably — a JFET usually goes where one of the others was tried first.
        Drain = new Terminal("d", "D", TerminalType.Passive, new Point(20, -40));
        Gate = new Terminal("g", "G", TerminalType.Input, new Point(-40, 0));
        Source = new Terminal("s", "S", TerminalType.Passive, new Point(20, 40));

        Terminals = [Drain, Gate, Source];
    }

    public Terminal Drain { get; }

    public Terminal Gate { get; }

    public Terminal Source { get; }

    [ObservableProperty]
    public partial JfetModel Model { get; set; }

    public override string ComponentType => "JFET";

    public override string DesignatorPrefix => "Q";

    public override string ValueLabel => Model.Name;

    public override bool IsNonlinear => true;

    /// <summary>+1 for an N-channel part, −1 for a P-channel one.</summary>
    private double Polarity => Model.IsNChannel ? 1.0 : -1.0;

    /// <summary>Drain current at the last solved point, in amps.</summary>
    public double DrainCurrent { get; private set; }

    /// <summary>Gate current at the last solved point — vanishing unless the gate is forward-biased.</summary>
    public double GateCurrent { get; private set; }

    /// <summary>True while the channel is carrying meaningful current.</summary>
    public bool IsConducting => Math.Abs(DrainCurrent) > 1e-6;

    /// <summary>
    /// True when the gate junction has been driven forward, which a JFET circuit is not supposed
    /// to do: the whole point of the device is a gate that draws nothing.
    /// </summary>
    public bool IsGateForwardBiased { get; private set; }

    public IReadOnlyList<string> Violations =>
        IsGateForwardBiased
            ? [$"the gate junction is forward-biased and drawing {Math.Abs(GateCurrent) * 1e3:0.0} mA — " +
               "a JFET gate is a reverse-biased diode, and driving it positive turns the device " +
               "into an ordinary diode"]
            : [];

    /// <summary>
    /// The square law, in the channel's own convention where the drain is the more positive end
    /// and the pinch-off voltage is a magnitude.
    /// </summary>
    private (double Current, double Gm, double Gds) Evaluate(double vgs, double vds)
    {
        var pinch = Math.Max(Model.PinchOffVoltage, 1e-6);
        var beta = Model.SaturationCurrent / (pinch * pinch);

        // vgs runs from 0 (full conduction) down to -pinch (cut off).
        var overdrive = vgs + pinch;

        if (overdrive <= 0) return (0, 0, 0);

        var lambda = Math.Max(Model.ChannelModulation, 0);
        var modulation = 1.0 + (lambda * vds);

        if (vds >= overdrive)
        {
            // Saturation: current set by the gate, with a gentle slope from channel modulation.
            return (beta * overdrive * overdrive * modulation,
                2.0 * beta * overdrive * modulation,
                beta * overdrive * overdrive * lambda);
        }

        // Triode: the channel behaves as a voltage-controlled resistance.
        //
        // Channel modulation applies here too, as it does in the MOSFET model. Leaving it to the
        // saturation branch alone makes the current jump by a few percent as the device crosses
        // between them, and a discontinuity is exactly what Newton cannot walk down: an amplifier
        // whose drain dipped into triode on startup hunted either side of the boundary until the
        // solver gave up. With it, current, gm and gds all meet at vds = overdrive.
        var core = vds * ((2.0 * overdrive) - vds);
        return (beta * core * modulation,
            2.0 * beta * vds * modulation,
            beta * ((2.0 * (overdrive - vds) * modulation) + (core * lambda)));
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var d = system.Node(Drain);
        var g = system.Node(Gate);
        var s = system.Node(Source);

        var polarity = Polarity;
        _limitedThisIteration = false;

        var vd = system.IterationVoltage(d);
        var vg = system.IterationVoltage(g);
        var vs = system.IterationVoltage(s);

        // A JFET is symmetric, so whichever end is more positive acts as the drain.
        var nominal = polarity * (vd - vs);
        var reversed = nominal < 0;

        var drainNode = reversed ? s : d;
        var sourceNode = reversed ? d : s;

        var vgs = reversed ? polarity * (vg - vd) : polarity * (vg - vs);
        var vds = Math.Abs(nominal);

        var (id, gm, gds) = Evaluate(vgs, vds);
        var idEq = polarity * (id - (gm * vgs) - (gds * vds));

        system.Add(drainNode, g, gm);
        system.Add(drainNode, sourceNode, -(gm + gds));
        system.Add(drainNode, drainNode, gds);
        system.AddRhs(drainNode, -idEq);

        system.Add(sourceNode, g, -gm);
        system.Add(sourceNode, sourceNode, gm + gds);
        system.Add(sourceNode, drainNode, -gds);
        system.AddRhs(sourceNode, idEq);

        // The two gate junctions. Normally reverse-biased and drawing picoamps, which is the
        // reason to use a JFET at all — but they are real diodes and behave like it if driven.
        StampGateJunction(system, state, g, s, ref _previousGateSource);
        StampGateJunction(system, state, g, d, ref _previousGateDrain);
    }

    private void StampGateJunction(
        MnaSystem system, SimulationState state, int gate, int channel, ref double previous)
    {
        const double saturation = 1e-14;

        var vt = state.ThermalVoltage;
        var polarity = Polarity;

        // The junction conducts when the gate is driven towards the channel's own polarity.
        var raw = polarity * (system.IterationVoltage(gate) - system.IterationVoltage(channel));
        var limited = Diode.LimitJunctionVoltage(raw, previous, vt, Junction.CriticalVoltage(saturation, vt));

        if (Math.Abs(limited - raw) > 1e-12) _limitedThisIteration = true;
        previous = limited;

        var (current, conductance) = Junction.Evaluate(limited, saturation, vt);

        var plus = polarity > 0 ? gate : channel;
        var minus = polarity > 0 ? channel : gate;

        system.StampNorton(plus, minus, conductance, current - (conductance * limited));
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_limitedThisIteration;

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var polarity = Polarity;
        var vd = system.NodeVoltage(Drain);
        var vg = system.NodeVoltage(Gate);
        var vs = system.NodeVoltage(Source);

        var nominal = polarity * (vd - vs);
        var reversed = nominal < 0;
        var vgs = reversed ? polarity * (vg - vd) : polarity * (vg - vs);

        var (id, _, _) = Evaluate(vgs, Math.Abs(nominal));
        DrainCurrent = polarity * (reversed ? -id : id);

        // Gate current, and whether the junction has been driven where it should never go.
        var vt = state.ThermalVoltage;
        var gateToSource = polarity * (vg - vs);
        var (gateCurrent, _) = Junction.Evaluate(gateToSource, 1e-14, vt);

        GateCurrent = polarity * gateCurrent;
        IsGateForwardBiased = Math.Abs(GateCurrent) > 1e-6;
    }

    /// <summary>
    /// Unlike a MOSFET the gate is a junction, so it has a current worth reporting — vanishing
    /// while it is reverse-biased, and the whole story when it is not.
    /// </summary>
    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        if (ReferenceEquals(terminal, Gate)) return GateCurrent;
        return ReferenceEquals(terminal, Drain) ? DrainCurrent : -DrainCurrent;
    }

    public override void ResetState()
    {
        _previousGateSource = 0;
        _previousGateDrain = 0;
        _limitedThisIteration = false;
        DrainCurrent = 0;
        GateCurrent = 0;
        IsGateForwardBiased = false;
    }

    partial void OnModelChanged(JfetModel value) => NotifyValueChanged();
}
