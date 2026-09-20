using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>A stocked crystal or resonator, named by the frequency on the can.</summary>
/// <param name="Name">What it is called.</param>
/// <param name="Frequency">Series resonant frequency, in hertz.</param>
/// <param name="MotionalCapacitance">The motional arm's capacitance, in farads.</param>
/// <param name="SeriesResistance">Equivalent series resistance, in ohms — this sets the Q.</param>
/// <param name="ShuntCapacitance">Capacitance of the holder and electrodes, in farads.</param>
public sealed record CrystalModel(
    string Name,
    double Frequency,
    double MotionalCapacitance,
    double SeriesResistance,
    double ShuntCapacitance)
{
    /// <summary>The watch crystal, and what a 4060 is usually asked to divide down.</summary>
    public static readonly CrystalModel Watch32k = new("32.768 kHz", 32768.0, 1e-12, 600.0, 1.7e-12);

    public static readonly CrystalModel Khz100 = new("100 kHz", 100e3, 1e-12, 300.0, 3e-12);

    public static readonly CrystalModel Khz455 = new("455 kHz ceramic", 455e3, 20e-12, 12.0, 30e-12);

    public static readonly CrystalModel Mhz1 = new("1 MHz", 1e6, 2e-12, 120.0, 4e-12);

    public static readonly IReadOnlyList<CrystalModel> Library = [Watch32k, Khz100, Khz455, Mhz1];

    public override string ToString() => Name;
}

/// <summary>
/// A quartz crystal, modelled as what it electrically is: a very high-Q series resonance.
/// <para>
/// Every other oscillator in the library takes its frequency from the circuit around it — the
/// 74HC14, the 4093 and the 4060 all charge a capacitor through a resistor and switch at a
/// threshold, so the components set the rate. A crystal is the opposite. The mechanical resonance
/// of a slice of quartz is so sharp that the surrounding circuit can barely move it, which is why
/// a watch keeps time and an RC oscillator does not.
/// </para>
/// <para>
/// Electrically that is a series R-L-C — the <b>motional arm</b>, standing for the mechanical
/// resonance — in parallel with the plain capacitance of the holder and its electrodes. The
/// inductance is enormous and the capacitance tiny, which is exactly what a high Q looks like
/// written as components.
/// </para>
/// <para>
/// <b>A necessary compromise.</b> A real 32.768 kHz crystal has a motional capacitance of a few
/// femtofarads and a Q of around 100,000, and a Q that high takes some hundred thousand cycles to
/// start — seconds of simulated time before anything happens. The defaults here use a larger
/// motional capacitance, which leaves the frequency exactly where it belongs (it depends only on
/// L and C) and trades Q for an oscillator that starts while you are watching. Lower the series
/// resistance if you want it sharper and are willing to wait.
/// </para>
/// <para>
/// The other limit is the time step. A resonance needs a good many steps per cycle to come out at
/// the right frequency, so the stocked parts stop at 1 MHz; a 16 MHz crystal would need the
/// simulation step shortened by two orders of magnitude before it meant anything.
/// </para>
/// </summary>
public partial class Crystal : TwoTerminalComponent, ICurrentReporting
{
    /// <summary>Motional branch current at the last accepted step, in amps.</summary>
    private double _previousCurrent;

    /// <summary>Voltage across the motional inductance at the last accepted step.</summary>
    private double _previousInductorVoltage;

    /// <summary>Voltage across the motional capacitance at the last accepted step.</summary>
    private double _previousCapacitorVoltage;

    /// <summary>Holder capacitance companion state.</summary>
    private double _previousShuntVoltage;
    private double _previousShuntCurrent;
    private double _shuntConductance;
    private double _shuntCurrentSource;

    public Crystal(CrystalModel? model = null)
    {
        Model = model ?? CrystalModel.Watch32k;
    }

    [ObservableProperty]
    public partial CrystalModel Model { get; set; }

    public override string ComponentType => "Crystal";

    public override string DesignatorPrefix => "Y";

    public override string ValueLabel => Model.Name;

    /// <summary>One branch carries the motional current.</summary>
    public override int VoltageSourceCount => 1;

    /// <summary>Current through the motional arm at the last solved point, in amps.</summary>
    public double MotionalCurrent { get; private set; }

    /// <summary>
    /// The motional inductance implied by the frequency and the motional capacitance, in henries.
    /// Large is the point: it is a mechanical resonance written as an electrical one.
    /// </summary>
    public double MotionalInductance
    {
        get
        {
            var omega = 2.0 * Math.PI * Math.Max(Model.Frequency, 1e-6);
            return 1.0 / (omega * omega * Math.Max(Model.MotionalCapacitance, 1e-18));
        }
    }

    /// <summary>
    /// Quality factor. A real crystal is in the tens of thousands; the stocked parts are lower so
    /// that they start oscillating in a watchable amount of simulated time.
    /// </summary>
    public double QualityFactor =>
        Math.Sqrt(MotionalInductance / Math.Max(Model.MotionalCapacitance, 1e-18))
        / Math.Max(Model.SeriesResistance, 1e-9);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var na = system.Node(A);
        var nb = system.Node(B);
        var branch = system.Branch(this);

        system.Add(na, branch, 1.0);
        system.Add(nb, branch, -1.0);

        StampShuntCapacitance(system, state, na, nb);

        if (!state.IsTransient)
        {
            // At DC the motional arm is a capacitor, so nothing flows. Constraining the branch to
            // zero keeps its row occupied rather than leaving the matrix singular. In a frequency
            // sweep the arm is an impedance like any other and StampAc writes the row itself, so
            // the constraint would be fighting it.
            if (state.IsBiasPoint) system.Add(branch, branch, 1.0);

            return;
        }

        // Trapezoidal, deliberately. Backward Euler adds numerical damping, and damping is the one
        // thing a resonator must not have applied to it for free — it would quietly lower the Q
        // that the whole part exists to provide.
        var h = state.TimeStep;
        var l = MotionalInductance;
        var c = Math.Max(Model.MotionalCapacitance, 1e-18);

        var inductive = 2.0 * l / h;
        var capacitive = h / (2.0 * c);

        // v_a - v_b - Z·i = history, the row shape the inductor uses.
        var impedance = Math.Max(Model.SeriesResistance, 0.0) + inductive + capacitive;

        var history = (-inductive * _previousCurrent) - _previousInductorVoltage
                      + _previousCapacitorVoltage + (capacitive * _previousCurrent);

        system.Add(branch, na, 1.0);
        system.Add(branch, nb, -1.0);
        system.Add(branch, branch, -impedance);
        system.AddRhs(branch, history);
    }

    /// <summary>
    /// The motional arm as one impedance, which is what it is and what makes the two resonances
    /// fall out on their own: series resonance where jωL and 1/jωC cancel and the arm is just its
    /// resistance, parallel resonance a little above it where the arm and the holder capacitance
    /// cancel each other instead.
    /// </summary>
    public override void StampAc(AcSystem system, SimulationState state)
    {
        var na = system.Node(A);
        var nb = system.Node(B);
        var branch = system.Branch(this);

        system.StampCapacitance(na, nb, Math.Max(Model.ShuntCapacitance, 1e-18));

        var w = Math.Max(state.AngularFrequency, 1e-9);
        var l = MotionalInductance;
        var c = Math.Max(Model.MotionalCapacitance, 1e-18);

        var impedance = new System.Numerics.Complex(
            Math.Max(Model.SeriesResistance, 0.0), (w * l) - (1.0 / (w * c)));

        // v_a - v_b - Z·i = 0
        system.Add(branch, na, 1.0);
        system.Add(branch, nb, -1.0);
        system.Add(branch, branch, -impedance);
    }

    /// <summary>The holder capacitance, straight across the part and in parallel with everything.</summary>
    private void StampShuntCapacitance(MnaSystem system, SimulationState state, int na, int nb)
    {
        if (!state.IsTransient)
        {
            _shuntConductance = 0;
            _shuntCurrentSource = 0;
            return;
        }

        var c = Math.Max(Model.ShuntCapacitance, 1e-18);

        _shuntConductance = 2.0 * c / state.TimeStep;
        _shuntCurrentSource = -((_shuntConductance * _previousShuntVoltage) + _previousShuntCurrent);

        system.StampNorton(na, nb, _shuntConductance, _shuntCurrentSource);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var across = system.NodeVoltage(A) - system.NodeVoltage(B);
        var current = system.BranchCurrent(this);

        MotionalCurrent = current;

        if (!state.IsTransient)
        {
            _previousCurrent = 0;
            _previousInductorVoltage = 0;
            _previousCapacitorVoltage = 0;
            _previousShuntVoltage = across;
            _previousShuntCurrent = 0;
            return;
        }

        var h = state.TimeStep;
        var c = Math.Max(Model.MotionalCapacitance, 1e-18);

        // Split the branch voltage back into its three parts, so the next step's history is right.
        var capacitorVoltage = _previousCapacitorVoltage + (h / (2.0 * c) * (current + _previousCurrent));
        var resistive = Math.Max(Model.SeriesResistance, 0.0) * current;

        _previousInductorVoltage = across - resistive - capacitorVoltage;
        _previousCapacitorVoltage = capacitorVoltage;
        _previousCurrent = current;

        _previousShuntCurrent = (_shuntConductance * across) + _shuntCurrentSource;
        _previousShuntVoltage = across;
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        var total = MotionalCurrent + _previousShuntCurrent;
        return CurrentIntoPin(terminal, total);
    }

    public override void ResetState()
    {
        _previousCurrent = 0;
        _previousInductorVoltage = 0;
        _previousCapacitorVoltage = 0;
        _previousShuntVoltage = 0;
        _previousShuntCurrent = 0;
        _shuntConductance = 0;
        _shuntCurrentSource = 0;
        MotionalCurrent = 0;
    }

    partial void OnModelChanged(CrystalModel value) => NotifyValueChanged();
}
