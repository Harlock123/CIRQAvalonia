using Cirq.Core.Simulation;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// A permanent-magnet DC motor, modelled electrically and mechanically at once.
/// <para>
/// Electrically it is the armature resistance and inductance in series with a back-EMF that is
/// proportional to speed. Mechanically the torque is proportional to current, and the rotor's
/// inertia integrates the difference between that torque and the load.
/// </para>
/// <para>
/// The coupling is the whole point, and it is why a motor cannot be modelled as a resistor. At
/// rest there is no back-EMF, so the armature draws the stall current — often ten or twenty times
/// the running current — and only as the rotor spins up does the back-EMF rise and choke the
/// current back. That startup surge is what trips supplies and welds relay contacts, and it falls
/// out of the model rather than being asserted.
/// </para>
/// </summary>
public partial class DcMotor : TwoTerminalComponent
{
    private double _previousCurrent;
    private double _previousInductiveVoltage;
    private double _stalledSeconds;

    /// <summary>Speed below which the rotor counts as stalled, in rad/s — a few rpm.</summary>
    private const double StallSpeed = 0.5;

    /// <summary>
    /// How long the rotor must be stalled while drawing over its rating before it is reported, in
    /// seconds. Every motor is momentarily stalled at switch-on; only sustained stall cooks it.
    /// </summary>
    private const double StallTolerance = 0.5;

    public DcMotor()
        : base("+", "-")
    {
    }

    /// <summary>Armature resistance in ohms. Small, which is why a stalled motor draws so much.</summary>
    [ObservableProperty]
    public partial double ArmatureResistance { get; set; } = 3.0;

    /// <summary>Armature inductance in henries.</summary>
    [ObservableProperty]
    public partial double ArmatureInductance { get; set; } = 2e-3;

    /// <summary>
    /// Torque per amp, in newton-metres per amp. For a permanent-magnet motor in SI units this is
    /// numerically the same as the back-EMF constant in volt-seconds per radian, so one number
    /// serves both.
    /// </summary>
    [ObservableProperty]
    public partial double TorqueConstant { get; set; } = 0.02;

    /// <summary>Rotor moment of inertia in kg·m².</summary>
    [ObservableProperty]
    public partial double Inertia { get; set; } = 1.5e-5;

    /// <summary>Viscous friction in N·m per rad/s — windage and bearing drag.</summary>
    [ObservableProperty]
    public partial double Damping { get; set; } = 2e-6;

    /// <summary>Constant opposing load on the shaft, in newton-metres.</summary>
    [ObservableProperty]
    public partial double LoadTorque { get; set; }

    /// <summary>Current the motor may draw continuously, in amps.</summary>
    [ObservableProperty]
    public partial double ContinuousCurrent { get; set; } = 1.0;

    public override string ComponentType => "DC Motor";

    public override string DesignatorPrefix => "M";

    public override string ValueLabel => $"{SiPrefix.Format(TorqueConstant, "Nm/A")}";

    /// <summary>The armature needs its own branch, the same as any inductor.</summary>
    public override int VoltageSourceCount => 1;

    /// <summary>Shaft speed in radians per second.</summary>
    public double AngularVelocity { get; private set; }

    /// <summary>Shaft speed in revolutions per minute, which is how motors are actually specified.</summary>
    public double Rpm => AngularVelocity * 60.0 / (2.0 * Math.PI);

    /// <summary>Armature current at the last solved point, in amps.</summary>
    public double Current { get; private set; }

    /// <summary>Torque the motor is producing, in newton-metres.</summary>
    public double Torque => TorqueConstant * Current;

    /// <summary>Voltage the spinning rotor generates against the supply, in volts.</summary>
    public double BackEmf => TorqueConstant * AngularVelocity;

    /// <summary>Largest current drawn, in amps — usually the startup surge.</summary>
    public double PeakCurrent { get; private set; }

    /// <summary>What is wrong with how this motor is being run, if anything.</summary>
    public IReadOnlyList<string> Violations =>
        _stalledSeconds > StallTolerance
            ? [$"stalled while drawing {Math.Abs(Current):0.0} A against a " +
               $"{ContinuousCurrent:0.##} A continuous rating — a stalled motor has no back-EMF " +
               "to limit its current and will cook"]
            : [];

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var na = system.Node(A);
        var nb = system.Node(B);
        var branch = system.Branch(this);

        system.Add(na, branch, 1.0);
        system.Add(nb, branch, -1.0);

        if (!state.IsTransient && state.Settings.UseInitialConditions)
        {
            // Switch-on: the armature starts with no current and the rotor at rest.
            system.Add(branch, branch, 1.0);
            return;
        }

        // v_a - v_b - Ra·i - Leq·i = history + back-EMF
        system.Add(branch, na, 1.0);
        system.Add(branch, nb, -1.0);
        system.Add(branch, branch, -ArmatureResistance);

        // The back-EMF is known from the speed carried in from the last step, so it is a constant
        // on the right-hand side rather than another unknown.
        var emf = BackEmf;

        if (!state.IsTransient)
        {
            system.AddRhs(branch, emf);
            return;
        }

        var h = state.TimeStep;
        var l = Math.Max(ArmatureInductance, 1e-18);

        if (state.EffectiveIntegration == IntegrationMethod.Trapezoidal)
        {
            var leq = 2.0 * l / h;
            system.Add(branch, branch, -leq);
            system.AddRhs(branch, (-leq * _previousCurrent) - _previousInductiveVoltage + emf);
        }
        else
        {
            var be = l / h;
            system.Add(branch, branch, -be);
            system.AddRhs(branch, (-be * _previousCurrent) + emf);
        }
    }

    /// <summary>
    /// The armature inductance. The back-EMF is a mechanical quantity carried between time steps,
    /// so a small-signal solve sees the winding and nothing else — which is the right answer for
    /// an impedance sweep and no answer at all about how the motor responds to a torque.
    /// </summary>
    public override void StampAc(AcSystem system, SimulationState state) =>
        system.AddInductance(system.Branch(this), Math.Max(ArmatureInductance, 1e-18));

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        Current = system.BranchCurrent(this);
        var across = VoltageAcross(system, A, B);

        _previousCurrent = Current;
        _previousInductiveVoltage = across - (ArmatureResistance * Current) - BackEmf;

        PeakCurrent = Math.Max(PeakCurrent, Math.Abs(Current));

        var step = state.TimeStep;
        if (step <= 0) return;

        // J·dω/dt = torque - viscous drag - load. The load is Coulomb friction, so it opposes
        // whichever way the shaft is turning; tanh rounds off the reversal rather than leaving a
        // step discontinuity for the integrator to trip over at standstill.
        var net = Torque
                  - (Damping * AngularVelocity)
                  - (LoadTorque * Math.Tanh(AngularVelocity / StallSpeed));

        AngularVelocity += net / Math.Max(Inertia, 1e-12) * step;

        var stalled = Math.Abs(AngularVelocity) < StallSpeed
                      && Math.Abs(Current) > ContinuousCurrent;

        _stalledSeconds = stalled ? _stalledSeconds + step : 0.0;
    }

    public override void ResetState()
    {
        _previousCurrent = 0;
        _previousInductiveVoltage = 0;
        _stalledSeconds = 0;
        AngularVelocity = 0;
        Current = 0;
        PeakCurrent = 0;
    }

    partial void OnTorqueConstantChanged(double value) => NotifyValueChanged();
}
