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
public enum StepperWiring
{
    /// <summary>
    /// Four coils hanging off one common wire, which goes to the supply while the coil ends are
    /// pulled down. Current only ever flows one way through a coil, so the driver can be four
    /// switches — a ULN2003, say — and nothing more.
    /// </summary>
    Unipolar,

    /// <summary>
    /// Two isolated windings, C1-C3 and C2-C4, with no common wire. Current is pushed both ways
    /// through each, which needs an H-bridge per winding, and in exchange the whole winding is
    /// working at once rather than half of it. This is what an A4988 or a DRV8825 drives.
    /// </summary>
    Bipolar,
}

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

    /// <summary>
    /// How the windings are brought out, which decides what can drive it.
    /// <para>
    /// The same iron and the same copper can be wound either way, and the difference is entirely
    /// in how many wires leave the case. A <b>unipolar</b> motor taps the middle of each winding
    /// and brings the tap out, so a switch pulling one end down energises half the winding in one
    /// direction — cheap to drive, and half the copper is idle at any moment. A <b>bipolar</b>
    /// motor leaves the taps inside, so reversing a winding means reversing the current through
    /// it, which takes an H-bridge — and all the copper works all the time, which is most of the
    /// reason a bipolar motor of the same size is the stronger one.
    /// </para>
    /// <para>
    /// Bipolar leaves <c>COM</c> unused and pairs the coils: C1-C3 is one winding and C2-C4 the
    /// other, matching an A4988's 1A/1B and 2A/2B. <see cref="CoilResistance"/> and
    /// <see cref="CoilInductance"/> then describe a whole winding end to end, which is the figure
    /// a bipolar motor's datasheet gives.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial StepperWiring Wiring { get; set; } = StepperWiring.Unipolar;

    /// <summary>True when the coils are paired into two isolated windings.</summary>
    public bool IsBipolar => Wiring == StepperWiring.Bipolar;

    /// <summary>
    /// Each winding needs a branch, because a winding is an inductor — four of them when the
    /// coils are driven separately, two when they are paired.
    /// </summary>
    public override int VoltageSourceCount => IsBipolar ? Phases / 2 : Phases;

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
        if (IsBipolar)
        {
            // Two windings, each strung between a pair of coil ends. COM is left out of the
            // matrix entirely — on a bipolar motor that wire does not exist.
            for (var w = 0; w < Phases / 2; w++)
                StampWinding(system, state, system.Node(_coils[w]), system.Node(_coils[w + 2]), w);

            return;
        }

        var common = system.Node(Common);

        for (var i = 0; i < Phases; i++)
            StampWinding(system, state, common, system.Node(_coils[i]), i);
    }

    /// <summary>
    /// One winding as a branch from <paramref name="from"/> to <paramref name="to"/>: its
    /// resistance, and a trapezoidal companion for its inductance. Both wirings are this same
    /// stamp; all that changes is what sits at the two ends.
    /// </summary>
    private void StampWinding(MnaSystem system, SimulationState state, int from, int to, int branchIndex)
    {
        var branch = system.Branch(this, branchIndex);

        // Current leaves the first node and enters the second.
        system.Add(from, branch, 1.0);
        system.Add(to, branch, -1.0);

        system.Add(branch, from, 1.0);
        system.Add(branch, to, -1.0);
        system.Add(branch, branch, -Math.Max(CoilResistance, 1e-6));

        if (!state.IsTransient) return;

        // Trapezoidal companion for the winding, the same shape the inductor uses.
        var leq = 2.0 * Math.Max(CoilInductance, 1e-12) / state.TimeStep;
        // The branch's own current last time. Bipolar keeps a winding's current in the entry for
        // its first coil, so the index is the branch index either way.
        var previous = _currents[branchIndex];

        system.Add(branch, branch, -leq);
        system.AddRhs(branch, -leq * previous);
    }

    /// <summary>
    /// Fills <see cref="_currents"/> from the solved branches, as a current per coil.
    /// <para>
    /// Unipolar is one branch per coil and needs no thought. Bipolar has one branch per pair, and
    /// what it means is that the two coils of a winding carry the <i>same</i> current in
    /// <i>opposite</i> senses — the field from C3 points backwards along C1's axis. Writing it out
    /// that way is what lets the field sum below stay exactly as it was: reverse a winding and its
    /// pair of entries swaps sign, which swings the vector round by half a turn, which is the
    /// whole difference between a bipolar drive and a unipolar one.
    /// </para>
    /// </summary>
    private void ReadCurrents(MnaSystem system)
    {
        if (!IsBipolar)
        {
            for (var i = 0; i < Phases; i++) _currents[i] = system.BranchCurrent(this, i);
            return;
        }

        for (var w = 0; w < Phases / 2; w++)
        {
            var current = system.BranchCurrent(this, w);

            _currents[w] = current;
            _currents[w + 2] = -current;
        }
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        double x = 0, y = 0;
        var energised = false;

        ReadCurrents(system);

        for (var i = 0; i < Phases; i++)
        {
            // Signed, not rectified. A unipolar drive only ever pushes current one way through a
            // winding, so this changes nothing for one — but a bipolar driver reverses it, and a
            // reversed winding pulls the rotor the opposite way rather than not at all. Taking the
            // magnitude alone would make an H-bridge look like an open circuit half the time.
            var magnitude = _currents[i];
            if (Math.Abs(magnitude) < HoldingCurrent) continue;

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
