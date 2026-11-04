using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// What is inside a Darlington package: the driver and output transistors, the two resistors across
/// their base-emitter junctions, and the diode across the output.
/// </summary>
/// <param name="Name">Part number.</param>
/// <param name="Driver">The small transistor whose emitter feeds the output transistor's base.</param>
/// <param name="Output">The power transistor that carries the load current.</param>
/// <param name="DriverBaseResistance">R1, base to the internal node, in ohms.</param>
/// <param name="OutputBaseResistance">R2, the internal node to the emitter, in ohms.</param>
/// <param name="DiodeSaturationCurrent">Saturation current of the emitter-to-collector diode, in amps.</param>
/// <param name="DiodeEmissionCoefficient">Its emission coefficient.</param>
public sealed record DarlingtonModel(
    string Name,
    BjtModel Driver,
    BjtModel Output,
    double DriverBaseResistance,
    double OutputBaseResistance,
    double DiodeSaturationCurrent = 1e-12,
    double DiodeEmissionCoefficient = 1.6)
{
    /// <summary>
    /// TIP120: 60 V, 5 A, a gain of at least a thousand. R1 is 8 kΩ and R2 120 Ω, as the datasheet's
    /// equivalent circuit gives them. The two transistors' parameters are not from a published
    /// model: they are chosen to land on the datasheet's figures — about 1.4 V from base to
    /// emitter to turn it on, a volt or so across it saturated at an amp, and a gain in the
    /// thousands at half an amp. The output stage's emission coefficient stands in for the high-
    /// injection effects that make a power transistor's V<sub>BE</sub> climb with current.
    /// </summary>
    public static readonly DarlingtonModel Tip120 = new(
        "TIP120",
        new BjtModel("TIP120 Q1", BjtPolarity.Npn, 1.0e-13, 80, 2, 100),
        new BjtModel("TIP120 Q2", BjtPolarity.Npn, 1.0e-11, 40, 2, 100, EmissionCoefficient: 1.3),
        DriverBaseResistance: 8e3,
        OutputBaseResistance: 120);

    /// <summary>Every Darlington the application knows.</summary>
    public static readonly IReadOnlyList<DarlingtonModel> Library = [Tip120];

    public override string ToString() => Name;
}

/// <summary>
/// An NPN Darlington power transistor — a TIP120 by default: one transistor's emitter driving
/// another's base, in one package, so the gains multiply and a logic pin's few milliamps switch
/// amps.
/// <para>
/// Modelled as what is inside it rather than as one transistor with a large β, because the
/// difference is the whole of how one behaves. Two base-emitter junctions in series means about
/// 1.4 V to turn it on, not 0.7. The output transistor's collector is fed through the driver, which
/// can never pull it below its own base, so a saturated Darlington still drops a volt or so —
/// a 12 V lamp sees 11 V, and the package dissipates a watt at an amp. And the real part carries
/// the two resistors that bleed off leakage so it turns off cleanly (R1 = 8 kΩ and R2 = 120 Ω
/// across the two junctions), and a diode from emitter to collector that catches the kick of an
/// inductive load.
/// </para>
/// <para>
/// The two transistors are Ebers-Moll, with the Early effect and no ohmic resistances or junction
/// capacitance, so this is a DC and low-frequency model, like the plain bipolar beside it.
/// </para>
/// </summary>
public partial class DarlingtonTransistor : CircuitComponent, ICurrentReporting
{
    /// <summary>One of the two transistors: its last junction voltages, for limiting the next.</summary>
    private sealed class Stage
    {
        public double Vbe;
        public double Vbc;
        public bool Limited;

        public double CollectorCurrent;
        public double BaseCurrent;

        public void Stamp(MnaSystem system, int c, int b, int e, BjtModel model, double kelvin)
        {
            var vt = PhysicalConstants.Boltzmann * kelvin / PhysicalConstants.ElementaryCharge * model.EmissionCoefficient;
            var saturation = model.SaturationCurrentAt(kelvin);
            var critical = Junction.CriticalVoltage(saturation, vt);

            var rawVbe = system.IterationVoltageAcross(b, e);
            var rawVbc = system.IterationVoltageAcross(b, c);

            var vbe = Junction.Limit(rawVbe, Vbe, vt, critical);
            var vbc = Junction.Limit(rawVbc, Vbc, vt, critical);

            Limited = Math.Abs(vbe - rawVbe) > 1e-12 || Math.Abs(vbc - rawVbc) > 1e-12;
            Vbe = vbe;
            Vbc = vbc;

            var (forward, gForward) = Junction.Evaluate(vbe, saturation, vt);
            var (reverse, gReverse) = Junction.Evaluate(vbc, saturation, vt);

            var bf = Math.Max(model.ForwardBetaAt(kelvin), 1e-3);
            var br = Math.Max(model.ReverseBeta, 1e-3);

            var ic = forward - reverse - (reverse / br);
            var ib = (forward / bf) + (reverse / br);

            // The same two-port linearisation the plain bipolar stamps; see BipolarTransistor.
            var gm = gForward;
            var gc = -gReverse * (1.0 + (1.0 / br));
            var gb = gForward / bf;
            var gu = gReverse / br;

            var icEq = ic - (gm * vbe) - (gc * vbc);
            var ibEq = ib - (gb * vbe) - (gu * vbc);

            system.Add(c, b, gm + gc);
            system.Add(c, e, -gm);
            system.Add(c, c, -gc);
            system.AddRhs(c, -icEq);

            system.Add(b, b, gb + gu);
            system.Add(b, e, -gb);
            system.Add(b, c, -gu);
            system.AddRhs(b, -ibEq);

            system.Add(e, b, -(gm + gc + gb + gu));
            system.Add(e, e, gm + gb);
            system.Add(e, c, gc + gu);
            system.AddRhs(e, icEq + ibEq);

            if (model.EarlyVoltage > 0) system.StampConductance(c, e, (Math.Abs(ic) / model.EarlyVoltage) + 1e-12);

            system.StampConductance(b, e, 1e-12);
            system.StampConductance(b, c, 1e-12);
        }

        public void Commit(MnaSystem system, int c, int b, int e, BjtModel model, double kelvin)
        {
            var vt = PhysicalConstants.Boltzmann * kelvin / PhysicalConstants.ElementaryCharge * model.EmissionCoefficient;
            var saturation = model.SaturationCurrentAt(kelvin);

            Vbe = system.NodeVoltage(b) - system.NodeVoltage(e);
            Vbc = system.NodeVoltage(b) - system.NodeVoltage(c);

            var (forward, _) = Junction.Evaluate(Vbe, saturation, vt);
            var (reverse, _) = Junction.Evaluate(Vbc, saturation, vt);

            var bf = Math.Max(model.ForwardBetaAt(kelvin), 1e-3);
            var br = Math.Max(model.ReverseBeta, 1e-3);

            var transport = forward - reverse - (reverse / br);
            BaseCurrent = (forward / bf) + (reverse / br);

            var early = model.EarlyVoltage > 0 ? (Math.Abs(transport) / model.EarlyVoltage) + 1e-12 : 0.0;
            CollectorCurrent = transport + (early * (Vbe - Vbc));
        }

        public void Reset()
        {
            Vbe = 0;
            Vbc = 0;
            Limited = false;
            CollectorCurrent = 0;
            BaseCurrent = 0;
        }
    }

    private readonly Stage _driver = new();
    private readonly Stage _output = new();

    private double _diodeVoltage;
    private bool _diodeLimited;

    public DarlingtonTransistor(DarlingtonModel? model = null)
    {
        Model = model ?? DarlingtonModel.Tip120;

        Collector = new Terminal("c", "C", TerminalType.Passive, new Point(20, -40));
        Base = new Terminal("b", "B", TerminalType.Input, new Point(-40, 0));
        Emitter = new Terminal("e", "E", TerminalType.Passive, new Point(20, 40));

        Terminals = [Collector, Base, Emitter];
    }

    public Terminal Collector { get; }

    public Terminal Base { get; }

    public Terminal Emitter { get; }

    [ObservableProperty]
    public partial DarlingtonModel Model { get; set; }

    public override string ComponentType => "NPN Darlington";

    public override string DesignatorPrefix => "Q";

    public override string ValueLabel => Model.Name;

    public override bool IsNonlinear => true;

    /// <summary>The node between the two transistors: the driver's emitter and the output's base.</summary>
    public override int InternalNodeCount => 1;

    /// <summary>Current into the collector pin at the last solved point, in amps.</summary>
    public double CollectorCurrent { get; private set; }

    /// <summary>Current into the base pin at the last solved point, in amps.</summary>
    public double BaseCurrent { get; private set; }

    /// <summary>Current the emitter-collector diode was carrying, in amps — the inductive kick, caught.</summary>
    public double DiodeCurrent { get; private set; }

    /// <summary>Base to emitter, in volts — both junctions, so about 1.4 V once it is on.</summary>
    public double Vbe { get; private set; }

    /// <summary>Collector to emitter, in volts.</summary>
    public double Vce { get; private set; }

    /// <summary>Effective current gain at the operating point, resistors and all.</summary>
    public double CurrentGain => Math.Abs(BaseCurrent) > 1e-12 ? CollectorCurrent / BaseCurrent : 0;

    /// <summary>What the package was dissipating at the last solved point, in watts.</summary>
    public double PowerDissipation => Math.Abs(Vce * CollectorCurrent) + Math.Abs(Vbe * BaseCurrent);

    /// <summary>True when it is passing meaningful collector current.</summary>
    public bool IsConducting => CollectorCurrent > 1e-4;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var c = system.Node(Collector);
        var b = system.Node(Base);
        var e = system.Node(Emitter);
        var mid = system.InternalNode(this);
        var kelvin = state.TemperatureKelvin;

        // Both collectors are the one pin; the driver's emitter is the output's base.
        _driver.Stamp(system, c, b, mid, Model.Driver, kelvin);
        _output.Stamp(system, c, mid, e, Model.Output, kelvin);

        system.StampResistor(b, mid, Model.DriverBaseResistance);
        system.StampResistor(mid, e, Model.OutputBaseResistance);

        // The diode, anode on the emitter and cathode on the collector: reverse-biased in normal
        // use, and conducting only when an inductive load drives the collector below the emitter.
        var vt = PhysicalConstants.Boltzmann * kelvin / PhysicalConstants.ElementaryCharge * Model.DiodeEmissionCoefficient;
        var critical = Junction.CriticalVoltage(Model.DiodeSaturationCurrent, vt);
        var raw = system.IterationVoltageAcross(e, c);
        var v = Junction.Limit(raw, _diodeVoltage, vt, critical);

        _diodeLimited = Math.Abs(v - raw) > 1e-12;
        _diodeVoltage = v;

        var (current, conductance) = Junction.Evaluate(v, Model.DiodeSaturationCurrent, vt);
        system.StampNorton(e, c, conductance + 1e-12, current - (conductance * v));
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) =>
        !_driver.Limited && !_output.Limited && !_diodeLimited;

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var c = system.Node(Collector);
        var b = system.Node(Base);
        var e = system.Node(Emitter);
        var mid = system.InternalNode(this);
        var kelvin = state.TemperatureKelvin;

        _driver.Commit(system, c, b, mid, Model.Driver, kelvin);
        _output.Commit(system, c, mid, e, Model.Output, kelvin);

        var vt = PhysicalConstants.Boltzmann * kelvin / PhysicalConstants.ElementaryCharge * Model.DiodeEmissionCoefficient;
        _diodeVoltage = system.NodeVoltage(e) - system.NodeVoltage(c);
        DiodeCurrent = Junction.Evaluate(_diodeVoltage, Model.DiodeSaturationCurrent, vt).Current;

        var vb = system.NodeVoltage(b);
        var vm = system.NodeVoltage(mid);

        BaseCurrent = _driver.BaseCurrent + ((vb - vm) / Model.DriverBaseResistance);
        CollectorCurrent = _driver.CollectorCurrent + _output.CollectorCurrent - DiodeCurrent;

        Vbe = vb - system.NodeVoltage(e);
        Vce = system.NodeVoltage(c) - system.NodeVoltage(e);
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        if (ReferenceEquals(terminal, Collector)) return CollectorCurrent;
        if (ReferenceEquals(terminal, Base)) return BaseCurrent;
        return -(CollectorCurrent + BaseCurrent);
    }

    public override void ResetState()
    {
        _driver.Reset();
        _output.Reset();
        _diodeVoltage = 0;
        _diodeLimited = false;
        CollectorCurrent = 0;
        BaseCurrent = 0;
        DiodeCurrent = 0;
        Vbe = 0;
        Vce = 0;
    }

    partial void OnModelChanged(DarlingtonModel value) => NotifyValueChanged();
}
