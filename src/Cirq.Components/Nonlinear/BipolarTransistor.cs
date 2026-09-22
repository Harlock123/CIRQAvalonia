using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;
using Cirq.Core.Probing;

namespace Cirq.Components.Nonlinear;

public enum BjtPolarity
{
    Npn,
    Pnp,
}

/// <summary>Ebers-Moll parameters for a bipolar transistor, in the usual SPICE naming.</summary>
public sealed record BjtModel(
    string Name,
    BjtPolarity Polarity,
    double SaturationCurrent,
    double ForwardBeta,
    double ReverseBeta,
    double EarlyVoltage,
    double EmissionCoefficient = 1.0,
    double EnergyGap = 1.11,
    double TemperatureExponent = 3.0,
    double BetaTemperatureExponent = 1.5)
{
    /// <summary>
    /// Saturation current at a temperature. This is what makes V<sub>BE</sub> fall about two
    /// millivolts a degree at a fixed collector current — the fact every bias network, every
    /// current mirror and every bandgap reference is built around.
    /// </summary>
    public double SaturationCurrentAt(double kelvin) =>
        JunctionTemperature.SaturationCurrentAt(
            SaturationCurrent, kelvin, EmissionCoefficient, EnergyGap, TemperatureExponent);

    /// <summary>
    /// Forward gain at a temperature. Beta climbs as the part warms, which is one of the reasons
    /// a bias network that depends on it is a bias network that drifts — and part of why the
    /// emitter-resistor arrangement that does not depend on it is the one everybody uses.
    /// </summary>
    public double ForwardBetaAt(double kelvin) =>
        JunctionTemperature.BetaAt(ForwardBeta, kelvin, BetaTemperatureExponent);

    public static readonly BjtModel N2N3904 = new("2N3904", BjtPolarity.Npn, 6.734e-15, 200, 4, 74);
    public static readonly BjtModel N2N2222 = new("2N2222", BjtPolarity.Npn, 3.0e-14, 200, 3, 75);
    public static readonly BjtModel Bc547 = new("BC547", BjtPolarity.Npn, 7.05e-15, 250, 5, 62);
    public static readonly BjtModel Tip31C = new("TIP31C", BjtPolarity.Npn, 1.0e-13, 100, 2, 100);

    public static readonly BjtModel P2N3906 = new("2N3906", BjtPolarity.Pnp, 1.41e-14, 180, 5, 19);
    public static readonly BjtModel Bc557 = new("BC557", BjtPolarity.Pnp, 1.0e-14, 200, 5, 60);
    public static readonly BjtModel Tip32C = new("TIP32C", BjtPolarity.Pnp, 1.0e-13, 100, 2, 100);

    private static readonly List<BjtModel> Models =
        [N2N3904, N2N2222, Bc547, Tip31C, P2N3906, Bc557, Tip32C];

    /// <summary>Every model the application knows, built in or imported.</summary>
    public static IReadOnlyList<BjtModel> Library => Models;

    /// <summary>Adds a model, replacing any with the same name.</summary>
    public static void Register(BjtModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        Models.RemoveAll(m => string.Equals(m.Name, model.Name, StringComparison.OrdinalIgnoreCase));
        Models.Add(model);
    }

    /// <summary>Removes an imported model by name.</summary>
    public static bool Unregister(string name) =>
        Models.RemoveAll(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase)) > 0;

    public override string ToString() => Name;
}

/// <summary>
/// Bipolar junction transistor, Ebers-Moll transport form:
/// <code>
///   Ic = (Ife − Ire) − Ire/Br
///   Ib = Ife/Bf + Ire/Br
/// </code>
/// where <c>Ife</c> and <c>Ire</c> are the forward and reverse junction currents. Each Newton
/// iteration linearises both junctions into a four-conductance two-port and stamps the residual as
/// current sources.
/// <para>
/// A PNP is solved as an NPN with every voltage mirrored. The conductances come out
/// polarity-independent — only the equivalent current terms change sign — which is why the stamp
/// below has a single <c>polarity</c> factor rather than two code paths.
/// </para>
/// <para>
/// Known simplifications: no ohmic base/collector/emitter resistance, and no junction
/// capacitance, so this is a DC and low-frequency model. The Early effect is included as an
/// output conductance, which is what sets a common-emitter stage's gain.
/// </para>
/// </summary>
public partial class BipolarTransistor : CircuitComponent, ICurrentReporting
{
    private double _vbe;
    private double _vbc;
    private bool _limitedThisIteration;

    public BipolarTransistor(BjtModel? model = null)
    {
        Model = model ?? BjtModel.N2N3904;

        Collector = new Terminal("c", "C", TerminalType.Passive, new Point(20, -40));
        Base = new Terminal("b", "B", TerminalType.Input, new Point(-40, 0));
        Emitter = new Terminal("e", "E", TerminalType.Passive, new Point(20, 40));

        Terminals = [Collector, Base, Emitter];
    }

    public Terminal Collector { get; }
    public Terminal Base { get; }
    public Terminal Emitter { get; }

    [ObservableProperty]
    public partial BjtModel Model { get; set; }

    public override string ComponentType => Model.Polarity == BjtPolarity.Npn ? "NPN Transistor" : "PNP Transistor";

    public override string DesignatorPrefix => "Q";

    public override string ValueLabel => Model.Name;

    public override bool IsNonlinear => true;

    /// <summary>True for an NPN, which the symbol renderer uses to point the emitter arrow.</summary>
    public bool IsNpn => Model.Polarity == BjtPolarity.Npn;

    /// <summary>Collector current at the converged solution, in amps.</summary>
    public double CollectorCurrent { get; private set; }

    /// <summary>Base current at the converged solution, in amps.</summary>
    public double BaseCurrent { get; private set; }

    public double EmitterCurrent => CollectorCurrent + BaseCurrent;

    /// <summary>Base-emitter voltage in the device's own polarity convention.</summary>
    public double Vbe => _vbe;

    /// <summary>Collector-emitter voltage in the device's own polarity convention.</summary>
    public double Vce => _vbe - _vbc;

    /// <summary>Effective current gain at the operating point.</summary>
    public double CurrentGain => Math.Abs(BaseCurrent) > 1e-15 ? CollectorCurrent / BaseCurrent : 0;

    /// <summary>True when both junctions are forward biased, i.e. the device is bottomed out.</summary>
    public bool IsSaturated => _vbe > 0.4 && _vbc > 0.3;

    /// <summary>True when the transistor is passing meaningful collector current.</summary>
    public bool IsConducting => Math.Abs(CollectorCurrent) > 1e-6;

    private double Polarity => Model.Polarity == BjtPolarity.Npn ? 1.0 : -1.0;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var c = system.Node(Collector);
        var b = system.Node(Base);
        var e = system.Node(Emitter);

        var polarity = Polarity;
        var vt = state.ThermalVoltage * Model.EmissionCoefficient;
        var saturation = Model.SaturationCurrentAt(state.TemperatureKelvin);
        var vCritical = Junction.CriticalVoltage(saturation, vt);

        // Mirror into NPN convention, then limit each junction against its previous iterate.
        var rawVbe = polarity * system.IterationVoltageAcross(b, e);
        var rawVbc = polarity * system.IterationVoltageAcross(b, c);

        var vbe = Junction.Limit(rawVbe, _vbe, vt, vCritical);
        var vbc = Junction.Limit(rawVbc, _vbc, vt, vCritical);

        _limitedThisIteration =
            Math.Abs(vbe - rawVbe) > 1e-12 || Math.Abs(vbc - rawVbc) > 1e-12;

        _vbe = vbe;
        _vbc = vbc;

        var (forward, gForward) = Junction.Evaluate(vbe, saturation, vt);
        var (reverse, gReverse) = Junction.Evaluate(vbc, saturation, vt);

        var bf = Math.Max(Model.ForwardBetaAt(state.TemperatureKelvin), 1e-3);
        var br = Math.Max(Model.ReverseBeta, 1e-3);

        var ic = forward - reverse - reverse / br;
        var ib = forward / bf + reverse / br;

        // Partial derivatives of (Ic, Ib) with respect to (Vbe, Vbc).
        var gm = gForward;                       // dIc/dVbe
        var gc = -gReverse * (1.0 + 1.0 / br);   // dIc/dVbc
        var gb = gForward / bf;                  // dIb/dVbe
        var gu = gReverse / br;                  // dIb/dVbc

        // Residual current sources, mirrored back into the device's real polarity.
        var icEq = polarity * (ic - gm * vbe - gc * vbc);
        var ibEq = polarity * (ib - gb * vbe - gu * vbc);

        // Collector node: the current leaving it into the device is Ic.
        system.Add(c, b, gm + gc);
        system.Add(c, e, -gm);
        system.Add(c, c, -gc);
        system.AddRhs(c, -icEq);

        // Base node: the current leaving it into the device is Ib.
        system.Add(b, b, gb + gu);
        system.Add(b, e, -gb);
        system.Add(b, c, -gu);
        system.AddRhs(b, -ibEq);

        // Emitter node carries the sum back out.
        system.Add(e, b, -(gm + gc + gb + gu));
        system.Add(e, e, gm + gb);
        system.Add(e, c, gc + gu);
        system.AddRhs(e, icEq + ibEq);

        // Early effect as an output conductance, which is what gives a common-emitter stage a
        // finite voltage gain instead of an infinite one.
        if (Model.EarlyVoltage > 0)
            system.StampConductance(c, e, Math.Abs(ic) / Model.EarlyVoltage + 1e-12);

        // Keep both junctions weakly connected so a floating base cannot empty the matrix row.
        system.StampConductance(b, e, 1e-12);
        system.StampConductance(b, c, 1e-12);
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_limitedThisIteration;

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var polarity = Polarity;
        var vt = state.ThermalVoltage * Model.EmissionCoefficient;

        _vbe = polarity * (system.NodeVoltage(Base) - system.NodeVoltage(Emitter));
        _vbc = polarity * (system.NodeVoltage(Base) - system.NodeVoltage(Collector));

        var (forward, _) = Junction.Evaluate(_vbe, Model.SaturationCurrentAt(state.TemperatureKelvin), vt);
        var (reverse, _) = Junction.Evaluate(_vbc, Model.SaturationCurrentAt(state.TemperatureKelvin), vt);

        var bf = Math.Max(Model.ForwardBetaAt(state.TemperatureKelvin), 1e-3);
        var br = Math.Max(Model.ReverseBeta, 1e-3);

        var transport = forward - reverse - reverse / br;
        BaseCurrent = polarity * (forward / bf + reverse / br);

        // The Early effect is stamped as a collector-emitter conductance, so its current is part
        // of what the collector pin actually carries. Leaving it out would report a collector
        // current that does not match the current in the collector load.
        var earlyConductance = Model.EarlyVoltage > 0
            ? Math.Abs(transport) / Model.EarlyVoltage + 1e-12
            : 0.0;

        var vce = system.NodeVoltage(Collector) - system.NodeVoltage(Emitter);
        CollectorCurrent = polarity * transport + earlyConductance * vce;
    }

    /// <summary>
    /// Collector and base currents are tracked by the model; the emitter carries their sum, which
    /// is Kirchhoff's law and also the one number a transistor stage is usually sized by.
    /// </summary>
    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        if (ReferenceEquals(terminal, Collector)) return CollectorCurrent;
        if (ReferenceEquals(terminal, Base)) return BaseCurrent;
        return -(CollectorCurrent + BaseCurrent);
    }

    public override void ResetState()
    {
        _vbe = 0;
        _vbc = 0;
        _limitedThisIteration = false;
        CollectorCurrent = 0;
        BaseCurrent = 0;
    }

    partial void OnModelChanged(BjtModel value) => NotifyValueChanged();
}
