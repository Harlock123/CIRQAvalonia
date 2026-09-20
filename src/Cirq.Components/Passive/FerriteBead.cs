using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// A ferrite bead — the part on every supply rail that almost nobody can describe.
/// <para>
/// It looks like an inductor on a schematic and it is drawn like one, which is where the trouble
/// starts, because it is not really being used as one. An inductor stores energy and gives it
/// back; that is what makes an LC filter ring, and it is why putting an ordinary inductor in a
/// supply rail to quieten it can make things worse — the inductance and the decoupling capacitor
/// form a resonant circuit, and noise at that frequency comes out <b>larger</b> than it went in.
/// </para>
/// <para>
/// A bead is lossy on purpose. At low frequencies it is a piece of wire, a few tens of milliohms,
/// and the supply current goes through it without noticing. As the frequency climbs the ferrite
/// starts to absorb rather than store, and somewhere around a hundred megahertz the thing is
/// essentially a resistor of a hundred ohms or so — it turns the noise into heat instead of
/// handing it back. Climb further and the winding's own stray capacitance shorts it out again,
/// so the impedance falls away: a bead has a band it works in, and above that band it is not
/// there at all.
/// </para>
/// <para>
/// So the datasheet number — "600 Ω" — is the impedance at <b>one frequency</b>, conventionally
/// 100 MHz, and not a property of the part at any other. Choosing a bead by that number alone,
/// for noise nowhere near 100 MHz, is the usual way of fitting one that does nothing.
/// </para>
/// <para>
/// Modelled as the datasheets model it: a resistance, an inductance and a capacitance all in
/// parallel, with the DC resistance of the wire in series. The resistance is the height of the
/// peak, the inductance decides where the impedance starts rising, and the capacitance decides
/// where it gives up.
/// </para>
/// </summary>
public partial class FerriteBead : TwoTerminalComponent, ICurrentReporting
{
    private double _previousCurrent;
    private double _previousVoltage;
    private double _capacitorVoltage;
    private double _capacitorCurrent;
    private double _conductance;
    private double _equivalentCurrent;

    public FerriteBead(double peakImpedance = 600.0)
    {
        PeakImpedance = peakImpedance;
    }

    /// <summary>
    /// Impedance at the top of its band, in ohms — the number on the datasheet, and the most it
    /// will ever present at any frequency.
    /// </summary>
    [ObservableProperty]
    public partial double PeakImpedance { get; set; }

    /// <summary>
    /// Frequency at which it peaks, in hertz. The inductance and capacitance are worked out from
    /// this and the peak impedance, because those are the two numbers a datasheet actually gives.
    /// </summary>
    [ObservableProperty]
    public partial double PeakFrequency { get; set; } = 100e6;

    /// <summary>
    /// How sharp the peak is. One is a broad, well-damped bead, which is what most of them are;
    /// higher is a bead that works over a narrower band but harder within it.
    /// </summary>
    [ObservableProperty]
    public partial double Sharpness { get; set; } = 1.0;

    /// <summary>
    /// Resistance of the wire through it, in ohms. Small, and the reason a bead in a supply rail
    /// does not drop any useful voltage — but not nothing, and worth watching at an amp or two.
    /// </summary>
    [ObservableProperty]
    public partial double DcResistance { get; set; } = 0.05;

    public override string ComponentType => "Ferrite Bead";

    public override string DesignatorPrefix => "FB";

    public override string ValueLabel => $"{SiPrefix.Format(PeakImpedance, "Ω")} @ {SiPrefix.Format(PeakFrequency, "Hz")}";

    /// <summary>One internal node, between the wire's resistance and the ferrite itself.</summary>
    public override int InternalNodeCount => 1;

    /// <summary>One branch for the inductance, which is solved in current form as inductors are.</summary>
    public override int VoltageSourceCount => 1;

    /// <summary>
    /// The parallel inductance, in henries. At the peak the inductive and capacitive parts cancel,
    /// so the frequency fixes the product of the two and the sharpness fixes the ratio.
    /// </summary>
    public double Inductance => Math.Max(PeakImpedance, 1e-6) / (2.0 * Math.PI * Math.Max(PeakFrequency, 1.0) * Math.Max(Sharpness, 0.05));

    /// <summary>The parallel capacitance, in farads — what eventually shorts the bead out.</summary>
    public double Capacitance => Math.Max(Sharpness, 0.05) / (2.0 * Math.PI * Math.Max(PeakFrequency, 1.0) * Math.Max(PeakImpedance, 1e-6));

    /// <summary>Current through the bead at the last accepted point, in amps.</summary>
    public double Current { get; private set; }

    /// <summary>
    /// Magnitude of the impedance it presents at a given frequency, in ohms. Not used by the
    /// solver — the circuit works that out for itself — but it is the number people look up, and
    /// having it lets a test check the model against the datasheet curve it came from.
    /// </summary>
    public double ImpedanceAt(double hertz)
    {
        var (resistance, reactance) = PartsAt(hertz);

        return Math.Sqrt((resistance * resistance) + (reactance * reactance));
    }

    /// <summary>
    /// The resistive part of that impedance, in ohms — the half that turns noise into heat.
    /// <para>
    /// Datasheets plot this separately from the reactance for a reason. Where the reactance
    /// dominates the bead is storing energy and giving it back, which is an inductor and can ring;
    /// where the resistance dominates it is absorbing, which is what a bead is fitted for. The
    /// crossover is what "the band it works in" means.
    /// </para>
    /// </summary>
    public double ResistanceAt(double hertz) => PartsAt(hertz).Resistance;

    /// <summary>The reactive part — positive inductive, negative capacitive.</summary>
    public double ReactanceAt(double hertz) => PartsAt(hertz).Reactance;

    /// <summary>The parallel RLC worked out as a series resistance and reactance.</summary>
    private (double Resistance, double Reactance) PartsAt(double hertz)
    {
        var w = 2.0 * Math.PI * Math.Max(hertz, 1e-9);

        // Admittances of the three parallel parts: 1/R, 1/jwL, jwC.
        var conductance = 1.0 / Math.Max(PeakImpedance, 1e-6);
        var susceptance = (w * Capacitance) - (1.0 / (w * Inductance));

        var magnitude = (conductance * conductance) + (susceptance * susceptance);

        return (Math.Max(DcResistance, 0.0) + (conductance / magnitude), -susceptance / magnitude);
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var na = system.Node(A);
        var nb = system.Node(B);
        var mid = system.InternalNode(this);
        var branch = system.Branch(this);

        // The wire.
        system.StampConductance(na, mid, 1.0 / Math.Max(DcResistance, 1e-9));

        // The loss, which is the part that makes a bead a bead.
        system.StampConductance(mid, nb, 1.0 / Math.Max(PeakImpedance, 1e-6));

        StampCapacitance(system, state, mid, nb);
        StampInductance(system, state, mid, nb, branch);
    }

    /// <summary>The stray capacitance across it, by the usual companion model.</summary>
    private void StampCapacitance(MnaSystem system, SimulationState state, int mid, int nb)
    {
        if (!state.IsTransient)
        {
            // A capacitor is an open circuit at DC, and this one has a resistor across it anyway.
            _conductance = 0;
            _equivalentCurrent = 0;
            return;
        }

        var h = state.TimeStep;
        var c = Math.Max(Capacitance, 1e-18);

        if (state.EffectiveIntegration == IntegrationMethod.Trapezoidal)
        {
            _conductance = 2.0 * c / h;
            _equivalentCurrent = -((_conductance * _capacitorVoltage) + _capacitorCurrent);
        }
        else
        {
            _conductance = c / h;
            _equivalentCurrent = -_conductance * _capacitorVoltage;
        }

        system.StampNorton(mid, nb, _conductance, _equivalentCurrent);
    }

    /// <summary>
    /// The inductance, in branch-current form like an ordinary inductor — so that at DC it is the
    /// short circuit it ought to be, shorting out the loss resistance and leaving only the wire.
    /// </summary>
    private void StampInductance(MnaSystem system, SimulationState state, int mid, int nb, int branch)
    {
        system.Add(mid, branch, 1.0);
        system.Add(nb, branch, -1.0);

        system.Add(branch, mid, 1.0);
        system.Add(branch, nb, -1.0);

        if (!state.IsTransient) return;

        var h = state.TimeStep;
        var l = Math.Max(Inductance, 1e-18);

        if (state.EffectiveIntegration == IntegrationMethod.Trapezoidal)
        {
            var leq = 2.0 * l / h;
            system.Add(branch, branch, -leq);
            system.AddRhs(branch, (-leq * _previousCurrent) - _previousVoltage);
        }
        else
        {
            var leq = l / h;
            system.Add(branch, branch, -leq);
            system.AddRhs(branch, -leq * _previousCurrent);
        }
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var mid = system.InternalNode(this);
        var nb = system.Node(B);

        var across = system.NodeVoltage(mid) - system.NodeVoltage(nb);

        _capacitorCurrent = state.IsTransient
            ? (_conductance * across) + _equivalentCurrent
            : 0.0;
        _capacitorVoltage = across;

        _previousCurrent = system.BranchCurrent(this);
        _previousVoltage = across;

        Current = (system.NodeVoltage(A) - system.NodeVoltage(mid)) / Math.Max(DcResistance, 1e-9);
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        ReferenceEquals(terminal, A) ? Current : -Current;

    public override void ResetState()
    {
        _previousCurrent = 0;
        _previousVoltage = 0;
        _capacitorVoltage = 0;
        _capacitorCurrent = 0;
        _conductance = 0;
        _equivalentCurrent = 0;
        Current = 0;
    }

    partial void OnPeakImpedanceChanged(double value) => NotifyValueChanged();

    partial void OnPeakFrequencyChanged(double value) => NotifyValueChanged();
}
