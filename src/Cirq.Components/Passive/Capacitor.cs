using Cirq.Core.Simulation;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// Capacitor discretised with a Norton companion model: <c>i = Geq·v + Ieq</c>, where the
/// coefficients come from Trapezoidal or Backward-Euler integration of <c>i = C·dv/dt</c>.
/// </summary>
public partial class Capacitor : TwoTerminalComponent
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
            // Under "use initial conditions" every capacitor without an explicit value starts
            // discharged, which is what makes a source applied at t=0 a true step.
            var initial = InitialVoltage ?? (state.Settings.UseInitialConditions ? 0.0 : null);

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

    partial void OnCapacitanceChanged(double value) => NotifyValueChanged();
}
