using Cirq.Core.Simulation;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;
using Cirq.Core.Probing;
using Cirq.Core.Topology;

namespace Cirq.Components.Passive;

/// <summary>
/// Capacitor discretised with a Norton companion model: <c>i = Geq·v + Ieq</c>, where the
/// coefficients come from Trapezoidal or Backward-Euler integration of <c>i = C·dv/dt</c>.
/// </summary>
public partial class Capacitor : TwoTerminalComponent, ICurrentReporting, IToleranced, IVoltageRated
{
    private double _previousVoltage;
    private double _previousCurrent;
    private double _conductance;
    private double _equivalentCurrent;

    public Capacitor(double capacitance = 1e-6)
    {
        Capacitance = capacitance;
    }

    /// <summary>Capacitance in farads.</summary>
    [ObservableProperty]
    public partial double Capacitance { get; set; }

    /// <summary>
    /// The most it may have across it, in volts. Fifty is the commonest small ceramic.
    /// <para>
    /// Until now a plain capacitor was the one passive with no rating of any kind, which made it
    /// the one part in the library that could be taken to four times what it is sold for and say
    /// nothing whatever. An electrolytic has always had this; a ceramic fails the same way, just
    /// more quietly and usually by losing most of its capacitance first.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double VoltageRating { get; set; } = 50.0;

    /// <summary>
    /// How far the real part may be from its marked capacitance, as a fraction — 0.05 for a
    /// five percent part. Twenty percent, which is ordinary for a ceramic and is much wider than people expect. A timing circuit built round one is not a precision timing circuit.
    /// <para>
    /// Nothing in an ordinary run uses this: the solver takes the value as given. It is what a
    /// Monte Carlo analysis varies, which is how you find out whether a circuit works with the
    /// parts you can actually buy rather than only with the ones in the drawing.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double Tolerance { get; set; } = 0.2;

    /// <summary>The value a tolerance applies to.</summary>
    public string TolerancedProperty => nameof(Capacitance);

    /// <summary>Optional initial voltage enforced during the bias-point solve.</summary>
    [ObservableProperty]
    public partial double? InitialVoltage { get; set; }

    /// <summary>Parallel leakage resistance in ohms; null models an ideal capacitor.</summary>
    [ObservableProperty]
    public partial double? LeakageResistance { get; set; }

    public override string ComponentType => "Capacitor";

    public override string DesignatorPrefix => "C";

    public override string ValueLabel => SiPrefix.Format(Capacitance, "F");

    /// <summary>Voltage across the capacitor at the last accepted time point.</summary>
    public double Voltage => _previousVoltage;

    /// <summary>Current through the capacitor at the last accepted time point.</summary>
    public double Current => _previousCurrent;

    /// <summary>
    /// The two nodes the capacitance itself sits between. Normally the terminals, but a part with
    /// series resistance puts the capacitance against an internal node and the resistance between
    /// that node and the terminal — see <c>ElectrolyticCapacitor</c>.
    /// </summary>
    protected virtual (int Positive, int Negative) CapacitanceNodes(MnaSystem system) =>
        (system.Node(A), system.Node(B));

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (na, nb) = CapacitanceNodes(system);

        if (LeakageResistance is { } rLeak and > 0) system.StampConductance(na, nb, 1.0 / rLeak);

        if (!state.IsTransient)
        {
            // Bias point: a capacitor is an open circuit, unless an initial condition pins it.
            // Not in a frequency sweep, though — pinning it there would short it out at every
            // frequency, which is the opposite of what a capacitor does.
            // Under "use initial conditions" every capacitor without an explicit value starts
            // discharged, which is what makes a source applied at t=0 a true step.
            var initial = state.IsBiasPoint
                ? InitialVoltage ?? (state.Settings.UseInitialConditions ? 0.0 : null)
                : null;

            if (initial is { } vic)
            {
                const double gStiff = 1e6;
                system.StampNorton(na, nb, gStiff, -gStiff * vic);
            }

            _conductance = 0;
            _equivalentCurrent = 0;
            return;
        }

        var h = state.TimeStep;
        var c = Math.Max(Capacitance, 1e-18);

        if (state.EffectiveIntegration == IntegrationMethod.Trapezoidal)
        {
            _conductance = 2.0 * c / h;
            _equivalentCurrent = -(_conductance * _previousVoltage + _previousCurrent);
        }
        else
        {
            _conductance = c / h;
            _equivalentCurrent = -_conductance * _previousVoltage;
        }

        system.StampNorton(na, nb, _conductance, _equivalentCurrent);
    }

    /// <summary>A capacitor at one frequency is an admittance of jωC, and nothing else.</summary>
    public override void StampAc(AcSystem system, SimulationState state)
    {
        var (na, nb) = AcCapacitanceNodes(system);

        system.StampCapacitance(na, nb, Math.Max(Capacitance, 0.0));
    }

    /// <summary>Which nodes the capacitance sits between, for the small-signal stamp.</summary>
    protected virtual (int Positive, int Negative) AcCapacitanceNodes(AcSystem system) =>
        (system.Node(A), system.Node(B));

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var (na, nb) = CapacitanceNodes(system);
        var v = system.NodeVoltage(na) - system.NodeVoltage(nb);

        if (!state.IsTransient)
        {
            _previousVoltage = InitialVoltage ?? v;
            _previousCurrent = 0;
            return;
        }

        _previousCurrent = _conductance * v + _equivalentCurrent;
        _previousVoltage = v;
    }

    public override void ResetState()
    {
        _previousVoltage = InitialVoltage ?? 0;
        _previousCurrent = 0;
        _conductance = 0;
        _equivalentCurrent = 0;
    }

    /// <summary>
    /// The companion model's current, which the device already works out each step to carry the
    /// charge forward. i = C dv/dt, sampled the way the integrator sampled it.
    /// </summary>
    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        CurrentIntoPin(terminal, Current);

    partial void OnCapacitanceChanged(double value) => NotifyValueChanged();
}
