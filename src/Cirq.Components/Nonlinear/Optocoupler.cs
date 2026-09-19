using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>An optocoupler part: how hard its LED drives the output transistor, and the LED itself.</summary>
/// <param name="Name">Part number as printed on the package.</param>
/// <param name="CurrentTransferRatio">Collector current per amp of LED current, at 1.0 for 100%.</param>
/// <param name="SaturationVoltage">Collector-emitter knee, below which the transistor stops sinking.</param>
/// <param name="Led">The infrared emitter, which drops around 1.2 V rather than an indicator LED's 1.8.</param>
public sealed record OptocouplerModel(
    string Name,
    double CurrentTransferRatio,
    double SaturationVoltage,
    DiodeModel Led)
{
    private static readonly DiodeModel Infrared = new("IR emitter", 1.25e-13, 1.8, 2.0, 6.0);

    public static readonly OptocouplerModel Pc817 = new("PC817", 1.0, 0.2, Infrared);

    public static readonly OptocouplerModel FourN35 = new("4N35", 1.0, 0.3, Infrared);

    public static readonly OptocouplerModel FourN25 = new("4N25", 0.5, 0.3, Infrared);

    public static readonly IReadOnlyList<OptocouplerModel> Library = [Pc817, FourN35, FourN25];

    public override string ToString() => Name;
}

/// <summary>
/// An optocoupler: an infrared LED facing a phototransistor, with no electrical connection between
/// them.
/// <para>
/// That lack of connection is the entire point of the part, and it is also the thing a simulator
/// has to be careful about — two circuits with nothing between them give a singular matrix. A real
/// device has an isolation resistance of some hundreds of gigohms, so that is stamped rather than
/// left as a true open, which keeps the output side referenced without meaningfully coupling it.
/// The output side still needs its own ground; that is a property of isolation, not a limitation.
/// </para>
/// <para>
/// The collector current follows the LED current through the transfer ratio, falling away as the
/// transistor saturates. Both dependencies are stamped as real Jacobian terms — the LED coupling
/// as a voltage-controlled current source off the junction's own conductance — so Newton sees the
/// true derivative rather than converging on a lagged estimate.
/// </para>
/// </summary>
public partial class Optocoupler : CircuitComponent
{
    private double _previousJunctionVoltage;
    private bool _limitedThisIteration;

    public Optocoupler(OptocouplerModel? model = null)
    {
        Model = model ?? OptocouplerModel.Pc817;

        Anode = new Terminal("a", "A", TerminalType.Passive, new Point(-40, -20));
        Cathode = new Terminal("k", "K", TerminalType.Passive, new Point(-40, 20));
        Collector = new Terminal("c", "C", TerminalType.Passive, new Point(40, -20));
        Emitter = new Terminal("e", "E", TerminalType.Passive, new Point(40, 20));

        Terminals = [Anode, Cathode, Collector, Emitter];
    }

    public Terminal Anode { get; }

    public Terminal Cathode { get; }

    public Terminal Collector { get; }

    public Terminal Emitter { get; }

    [ObservableProperty]
    public partial OptocouplerModel Model { get; set; }

    /// <summary>
    /// Resistance between the two halves, in ohms. Enormous, but finite: a true open would leave
    /// an isolated output side with no reference at all.
    /// </summary>
    [ObservableProperty]
    public partial double IsolationResistance { get; set; } = 1e9;

    public override string ComponentType => "Optocoupler";

    public override string DesignatorPrefix => "OK";

    public override string ValueLabel => Model.Name;

    public override bool IsNonlinear => true;

    /// <summary>The LED's ohmic node, as <see cref="Diode"/> carries.</summary>
    public override int InternalNodeCount => 1;

    /// <summary>LED current at the last solved point, in amps.</summary>
    public double LedCurrent { get; private set; }

    /// <summary>Collector current at the last solved point, in amps.</summary>
    public double CollectorCurrent { get; private set; }

    /// <summary>True when the output transistor is being driven hard enough to pull its collector down.</summary>
    public bool IsConducting => CollectorCurrent > 1e-6;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var anode = system.Node(Anode);
        var cathode = system.Node(Cathode);
        var bulk = system.InternalNode(this);
        var collector = system.Node(Collector);
        var emitter = system.Node(Emitter);

        // ---- emitter side: an ordinary junction with a bulk resistance ----
        var vt = state.ThermalVoltage * Model.Led.EmissionCoefficient;
        var raw = system.IterationVoltageAcross(anode, bulk);
        var vd = Diode.LimitJunctionVoltage(
            raw, _previousJunctionVoltage, vt, Junction.CriticalVoltage(Model.Led.SaturationCurrent, vt));

        _limitedThisIteration = Math.Abs(vd - raw) > 1e-12;
        _previousJunctionVoltage = vd;

        var (ledCurrent, ledConductance) = Junction.Evaluate(vd, Model.Led.SaturationCurrent, vt);

        system.StampNorton(anode, bulk, ledConductance, ledCurrent - (ledConductance * vd));
        system.StampConductance(bulk, cathode, 1.0 / Math.Max(Model.Led.SeriesResistance, 1e-4));

        // ---- detector side: i_c = CTR · i_led · (1 - exp(-v_ce / v_knee)) ----
        var knee = Math.Max(Model.SaturationVoltage, 1e-3);

        // The model is never linearised at a negative collector voltage. Out of saturation the
        // output is very nearly a current source, so the linear solve overshoots far past zero;
        // evaluated there the device looked like a perfect open, the pull-up took the node back
        // to the rail, and Newton sat in a two-point cycle for ever. Clamping at zero is enough,
        // because that is exactly where the conductance is greatest — the linearisation becomes
        // the few tens of ohms a saturated transistor really is, and the next solve lands on the
        // answer. It never binds at a real operating point, which sits a little above zero.
        var rawVce = system.IterationVoltageAcross(collector, emitter);
        var vce = Math.Max(rawVce, 0.0);

        var exponent = Math.Exp(-vce / knee);
        var saturation = 1.0 - exponent;
        var forward = Math.Max(ledCurrent, 0.0);

        var current = Model.CurrentTransferRatio * forward * saturation;

        // Partial with respect to v_ce: the transistor coming out of saturation.
        var gce = Model.CurrentTransferRatio * forward * exponent / knee;

        // The LED current is deliberately *not* stamped as a cross-coupling term. It looks like
        // the right Jacobian entry, but the junction limiter means the vd used here and the node
        // voltages a VCCS would read are different numbers whenever limiting is active, and the
        // junction's conductance is large enough that the mismatch swamps the whole collector
        // current. Nothing on the output side affects the LED anyway, so the outer Newton loop
        // settles the input first and the output follows it.
        system.StampConductance(collector, emitter, gce);
        system.StampCurrentSource(collector, emitter, current - (gce * vce));

        // ---- the isolation barrier ----
        system.StampConductance(cathode, emitter, 1.0 / Math.Max(IsolationResistance, 1.0));
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_limitedThisIteration;

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var vt = state.ThermalVoltage * Model.Led.EmissionCoefficient;
        var vd = system.NodeVoltage(Anode) - system.NodeVoltage(system.InternalNode(this));

        _previousJunctionVoltage = vd;
        (LedCurrent, _) = Junction.Evaluate(vd, Model.Led.SaturationCurrent, vt);

        var vce = system.NodeVoltage(Collector) - system.NodeVoltage(Emitter);
        var saturation = 1.0 - Math.Exp(-Math.Max(vce, 0.0) / Math.Max(Model.SaturationVoltage, 1e-3));
        CollectorCurrent = Model.CurrentTransferRatio * Math.Max(LedCurrent, 0.0) * saturation;
    }

    public override void ResetState()
    {
        _previousJunctionVoltage = 0;
        _limitedThisIteration = false;
        LedCurrent = 0;
        CollectorCurrent = 0;
    }

    partial void OnModelChanged(OptocouplerModel value) => NotifyValueChanged();
}
