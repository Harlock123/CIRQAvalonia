using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// A four-phase unipolar stepper — the 28BYJ-48 sort, and the reason the ULN2003 is in the palette.
/// <para>
/// It has no idea where it is. Energise the coils in order and the rotor follows the magnetic
/// field one step at a time; energise them in the wrong order and it goes the other way; energise
/// them faster than the rotor can follow and it stops dead while the field carries on without it.
/// Counting steps is the only position feedback there is, which is why losing them matters.
/// </para>
/// <para>
/// The drive pattern is not built in here, because the pattern is the interesting part. The rotor
/// follows the vector sum of whatever coils are actually carrying current, so wave drive, full
/// step and half step all fall out of what you send rather than being selected from a list.
/// </para>
/// <para>
/// The common wire goes to the supply and the four coil ends are pulled down, which is what a
/// unipolar winding means and why it pairs with a sink driver.
/// </para>
/// </summary>
public partial class StepperMotor : CircuitComponent
{
    private const int Phases = 4;

    private readonly Terminal[] _coils = new Terminal[Phases];
    private readonly double[] _currents = new double[Phases];

    /// <summary>Field angle in radians, unwrapped so revolutions can be counted.</summary>
    private double _fieldAngle;

    private bool _fieldKnown;

    public StepperMotor()
    {
        Common = new Terminal("com", "COM", TerminalType.Power, new Point(-50, 0));

        for (var i = 0; i < Phases; i++)
            _coils[i] = new Terminal($"c{i + 1}", $"C{i + 1}", TerminalType.Passive,
                new Point(50, -30 + (i * 20)));

        Terminals = [Common, .. _coils];
    }

    /// <summary>The wire that goes to the supply; the coil ends are pulled down against it.</summary>
    public Terminal Common { get; }

    /// <summary>The four coil ends, in the order they should be energised to turn forwards.</summary>
    public IReadOnlyList<Terminal> Coils => _coils;

    /// <summary>Resistance of one winding, in ohms.</summary>
    [ObservableProperty]
    public partial double CoilResistance { get; set; } = 50.0;

    /// <summary>Inductance of one winding, in henries.</summary>
    [ObservableProperty]
    public partial double CoilInductance { get; set; } = 25e-3;

    /// <summary>Full steps for one revolution of the output shaft.</summary>
    [ObservableProperty]
    public partial double StepsPerRevolution { get; set; } = 2048.0;

    /// <summary>Fastest the rotor can follow the field, in full steps per second.</summary>
    [ObservableProperty]
    public partial double MaximumStepRate { get; set; } = 600.0;

    /// <summary>Current a coil must carry before it counts as energised, in amps.</summary>
    [ObservableProperty]
    public partial double HoldingCurrent { get; set; } = 10e-3;

    /// <summary>Each coil needs a branch, because a winding is an inductor.</summary>
    public override int VoltageSourceCount => Phases;

    public override string ComponentType => "Stepper Motor";

    public override string DesignatorPrefix => "M";

    public override string ValueLabel => IsEnergised ? $"{Angle:0.#}°" : "unpowered";

    /// <summary>Shaft angle in degrees, counted from where it started and not wrapped.</summary>
    public double Angle { get; private set; }

    /// <summary>Net full steps taken since the start, negative for the other direction.</summary>
    public double Steps => Angle * StepsPerRevolution / 360.0;

    /// <summary>True while at least one coil is carrying enough current to hold the rotor.</summary>
    public bool IsEnergised { get; private set; }

    /// <summary>
    /// True when the field is moving faster than the rotor can follow. A real motor does not fall
    /// behind gracefully — it buzzes and stays where it is, and every step after that is lost.
    /// </summary>
    public bool IsSlipping { get; private set; }

    public IReadOnlyList<string> Violations => IsSlipping
        ? [$"the drive is stepping faster than {MaximumStepRate:0} steps/s and the rotor has " +
           "stopped following it — a stepper has no feedback, so these steps are simply lost"]
        : [];

    /// <summary>Current through one winding at the last solved point, in amps.</summary>
    public double CoilCurrent(int index) => _currents[index];

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var common = system.Node(Common);

        for (var i = 0; i < Phases; i++)
        {
            var node = system.Node(_coils[i]);
            var branch = system.Branch(this, i);

            // Current leaves the common wire and enters the coil end.
            system.Add(common, branch, 1.0);
            system.Add(node, branch, -1.0);

            system.Add(branch, common, 1.0);
            system.Add(branch, node, -1.0);
            system.Add(branch, branch, -Math.Max(CoilResistance, 1e-6));

            if (!state.IsTransient) continue;

            // Trapezoidal companion for the winding, the same shape the inductor uses.
            var leq = 2.0 * Math.Max(CoilInductance, 1e-12) / state.TimeStep;
            var previous = _currents[i];

            system.Add(branch, branch, -leq);
            system.AddRhs(branch, -leq * previous);
        }
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        double x = 0, y = 0;
        var energised = false;

        for (var i = 0; i < Phases; i++)
        {
            _currents[i] = system.BranchCurrent(this, i);

            var magnitude = Math.Max(_currents[i], 0.0);
            if (magnitude < HoldingCurrent) continue;

            energised = true;

            // Coil i pulls the rotor towards its own quarter of the circle. Summing them as
            // vectors is what makes half-stepping work without being told about it: two adjacent
            // coils together point between the two.
            var direction = i * Math.PI / 2.0;
            x += magnitude * Math.Cos(direction);
            y += magnitude * Math.Sin(direction);
        }

        IsEnergised = energised;

        if (!energised || !state.IsTransient)
        {
            IsSlipping = false;
            return;
        }

        var target = Math.Atan2(y, x);

        if (!_fieldKnown)
        {
            _fieldAngle = target;
            _fieldKnown = true;
            return;
        }

        // Unwrap: the field never jumps more than half a turn between steps, so the shortest way
        // round is the way it went.
        var delta = target - _fieldAngle;
        while (delta > Math.PI) delta -= 2.0 * Math.PI;
        while (delta < -Math.PI) delta += 2.0 * Math.PI;

        _fieldAngle += delta;

        // The field is electrical and moves the instant the drive changes; the rotor has mass and
        // does not. Limiting the field itself was wrong — every step looks infinitely fast when
        // the pattern changes between one time point and the next, so nothing could ever turn.
        // What is limited is how fast the rotor closes on where the field has gone.
        var degreesPerStep = 360.0 / Math.Max(StepsPerRevolution, 1.0);
        var commanded = _fieldAngle / (Math.PI / 2.0) * degreesPerStep;
        var allowed = MaximumStepRate * degreesPerStep * state.TimeStep;

        var error = commanded - Angle;
        Angle += Math.Abs(error) <= allowed ? error : Math.Sign(error) * allowed;

        // Lagging by more than a couple of steps is pull-out: past that a real rotor is no longer
        // being pulled forwards at all, and the steps it has fallen behind are gone for good.
        IsSlipping = Math.Abs(commanded - Angle) > 2.0 * degreesPerStep;

        NotifyValueChanged();
    }

    public override void ResetState()
    {
        Array.Clear(_currents);
        Angle = 0;
        _fieldAngle = 0;
        _fieldKnown = false;
        IsEnergised = false;
        IsSlipping = false;
    }
}
