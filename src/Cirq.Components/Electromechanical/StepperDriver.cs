using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// An A4988 microstepping driver: step and direction in, two regulated coil currents out.
/// <para>
/// The ULN2003 in this library switches a stepper's phases on and off, and that is all a simple
/// driver does. This one does something else entirely, and the difference is why every printer,
/// plotter and 3D printer built since the eighties has one of these rather than seven Darlingtons.
/// </para>
/// <para>
/// <b>It regulates current rather than applying voltage.</b> A stepper winding is about two ohms
/// and a few millihenries, so at its rated amp it only needs two volts — but at two volts the
/// current takes milliseconds to rise through the inductance, and a motor stepping a thousand
/// times a second never reaches its rated current at all. Its torque falls off a cliff with speed.
/// The answer is to feed it from a supply far higher than it needs, twelve or twenty-four volts,
/// so the current rises quickly, and then <b>chop</b> — switch the supply off the moment the
/// current reaches target, let it decay, and switch it back on. The winding sees its rated current
/// almost immediately and never more than that, whatever the supply is.
/// </para>
/// <para>
/// The chopping is modelled rather than assumed, so it is on the scope: probe a coil and watch the
/// current sawtooth between the target and a little below it at tens of kilohertz. Raise
/// <see cref="SupplyVoltage"/> and the ripple gets faster and the rise sharper; drop it towards the
/// winding's own requirement and the regulation stops working, because there is no headroom left
/// to chop against.
/// </para>
/// <para>
/// <b>And it microsteps.</b> Full stepping energises one winding at a time; the rotor jumps between
/// four positions per electrical cycle and rings when it arrives. Driving the two windings with a
/// sine and a cosine instead puts the field anywhere in between, so the rotor is pulled smoothly
/// rather than snapped. MS1, MS2 and MS3 pick full, half, quarter, eighth or sixteenth steps —
/// which is worth understanding as a <i>smoothness</i> control rather than a resolution one, since
/// the position accuracy is set by the motor's own detents, not by the driver.
/// </para>
/// <para>
/// It drives a <b>bipolar</b> motor: 1A/1B is one winding and 2A/2B the other, with current pushed
/// both ways through each. Wire the stepper's four coils as two pairs and leave its common wire
/// unconnected.
/// </para>
/// </summary>
public sealed partial class StepperDriver : DigitalComponent, IBreakpointSource
{
    /// <summary>The two windings, each driven by an H-bridge.</summary>
    private const int Windings = 2;

    /// <summary>Electrical steps in a full cycle at full-step resolution.</summary>
    private const int FullCycle = 4;

    /// <summary>
    /// Below this a commanded current counts as none at all, in amps.
    /// <para>
    /// It exists because a cosine of a right angle is not zero, it is six times ten to the minus
    /// seventeen. At every full step one winding is commanded to exactly that, and asking
    /// <c>Math.Sign</c> about it gets back a confident "positive" — so the bridge turned hard on
    /// for a target the regulator had already written off as zero, and drove the winding to the
    /// supply's limit with nothing to chop against. One threshold, consulted by both halves.
    /// </para>
    /// </summary>
    private const double CurrentDeadband = 1e-9;

    private readonly double[] _current = new double[Windings];
    private readonly bool[] _chopping = new bool[Windings];
    private readonly double[] _offUntil = [double.NegativeInfinity, double.NegativeInfinity];

    private LogicState _lastStep = LogicState.Low;
    private int _microstep;

    public StepperDriver()
    {
        // TTL thresholds rather than CMOS: the real part's step and direction inputs are
        // specified low enough to be driven from a 3.3 V microcontroller, and a CMOS threshold
        // here would refuse a 3.4 V logic high from the 74-series parts in this palette.
        Levels = LogicLevels.Ttl;

        Step = new Terminal("step", "STEP", TerminalType.Input, new Point(-50, -42));
        Direction = new Terminal("dir", "DIR", TerminalType.Input, new Point(-50, -20));
        Enable = new Terminal("en", "EN", TerminalType.Input, new Point(-50, 2));
        Ms1 = new Terminal("ms1", "MS1", TerminalType.Input, new Point(-50, 24));
        Ms2 = new Terminal("ms2", "MS2", TerminalType.Input, new Point(-50, 46));
        Ms3 = new Terminal("ms3", "MS3", TerminalType.Input, new Point(-50, 68));

        Vcc = new Terminal("vdd", "VDD", TerminalType.Power, new Point(0, -60));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(0, 80));
        Motor = new Terminal("vmot", "VMOT", TerminalType.Power, new Point(50, -60));

        OutputA1 = new Terminal("1a", "1A", TerminalType.Passive, new Point(50, -20));
        OutputB1 = new Terminal("1b", "1B", TerminalType.Passive, new Point(50, 2));
        OutputA2 = new Terminal("2a", "2A", TerminalType.Passive, new Point(50, 24));
        OutputB2 = new Terminal("2b", "2B", TerminalType.Passive, new Point(50, 46));

        Terminals =
        [
            Step, Direction, Enable, Ms1, Ms2, Ms3,
            OutputA1, OutputB1, OutputA2, OutputB2,
            Vcc, Motor, Gnd,
        ];

        ConfigurePins([Step, Direction, Enable, Ms1, Ms2, Ms3], []);
    }

    public Terminal Step { get; }

    /// <summary>High counts forwards, low counts back.</summary>
    public Terminal Direction { get; }

    /// <summary>Active low, so an unwired enable leaves the outputs on.</summary>
    public Terminal Enable { get; }

    public Terminal Ms1 { get; }
    public Terminal Ms2 { get; }
    public Terminal Ms3 { get; }

    /// <summary>First winding.</summary>
    public Terminal OutputA1 { get; }

    public Terminal OutputB1 { get; }

    /// <summary>Second winding.</summary>
    public Terminal OutputA2 { get; }

    public Terminal OutputB2 { get; }

    public Terminal Vcc { get; }

    public Terminal Gnd { get; }

    /// <summary>The motor supply, which is deliberately far higher than the winding needs.</summary>
    public Terminal Motor { get; }

    /// <summary>
    /// The current each winding is regulated to, in amps — set on a real board by a trimmer and a
    /// pair of sense resistors, and the single most important adjustment on one.
    /// </summary>
    [ObservableProperty]
    public partial double CurrentLimit { get; set; } = 1.0;

    /// <summary>
    /// Supply the windings are driven from, in volts, when <c>VMOT</c> is not wired to anything.
    /// Connect the pin and the circuit's own supply is used instead.
    /// </summary>
    [ObservableProperty]
    public partial double SupplyVoltage { get; set; } = 12.0;

    /// <summary>
    /// How long the bridge stays off once the current has reached target, in seconds. It is what
    /// sets the chopping frequency, and on a real part it is an external capacitor.
    /// </summary>
    [ObservableProperty]
    public partial double OffTime { get; set; } = 30e-6;

    /// <summary>Resistance of the bridge while it is driving, in ohms.</summary>
    [ObservableProperty]
    public partial double OnResistance { get; set; } = 0.4;

    public override string ComponentType => "Stepper Driver";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => IsEnabled
        ? $"A4988 · 1/{Microsteps}"
        : "disabled";

    /// <summary>Each output is driven through its own branch, two ends per winding.</summary>
    public override int VoltageSourceCount => base.VoltageSourceCount + (Windings * 2);

    /// <summary>Microsteps per full step: 1, 2, 4, 8 or 16.</summary>
    public int Microsteps { get; private set; } = 1;

    /// <summary>Where it is in the electrical cycle, in microsteps.</summary>
    public int Position => _microstep;

    /// <summary>True while the outputs are driving at all.</summary>
    public bool IsEnabled { get; private set; } = true;

    /// <summary>Current in one winding at the last solved point, in amps.</summary>
    public double WindingCurrent(int index) => _current[index];

    /// <summary>What one winding is being regulated towards, in amps. Signed: it reverses.</summary>
    public double TargetCurrent(int index)
    {
        if (!IsEnabled) return 0;

        // Sine and cosine of the electrical angle: full stepping is this sampled four times a
        // cycle, and microstepping is the same thing sampled more finely.
        var angle = 2.0 * Math.PI * _microstep / (FullCycle * Microsteps);

        return CurrentLimit * (index == 0 ? Math.Cos(angle) : Math.Sin(angle));
    }

    /// <summary>True while a winding is at its limit and the bridge is chopping to hold it there.</summary>
    public bool IsChopping(int index) => _chopping[index];

    /// <summary>True while the bridge is actually pushing, rather than letting the winding decay.</summary>
    public bool IsDriving(int index) => IsEnabled && _lastStampTime >= _offUntil[index];

    private double _lastStampTime;

    /// <summary>Which way the bridge should push for a commanded current, or nought for neither.</summary>
    private static int PushDirection(double target) =>
        Math.Abs(target) < CurrentDeadband ? 0 : Math.Sign(target);

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        _lastStampTime = state.Time;

        for (var i = 0; i < Windings; i++)
        {
            var (plus, minus) = i == 0 ? (OutputA1, OutputB1) : (OutputA2, OutputB2);

            var target = TargetCurrent(i);
            // Not at the bias point. A chopper regulates by watching the current and switching,
            // and at a DC operating point there is nothing to watch: the winding is a short, the
            // solver hands it the supply divided by a couple of ohms, and the run starts several
            // amps over the limit. A real board comes up with its windings at zero and the
            // regulator walks them up over the first few hundred microseconds, so that is where
            // this starts too.
            var driving = IsEnabled && state.IsTransient && state.Time >= _offUntil[i];

            // The bridge pushes the winding whichever way the target says, and stops pushing
            // while it is in its off time — which is the chop.
            var toward = driving ? PushDirection(target) : 0;

            // Branches numbered from zero: the base class reserved none, because none of this
            // part's pins is an ordinary logic output.
            StampBridgeEnd(system, plus, i * 2, toward);
            StampBridgeEnd(system, minus, (i * 2) + 1, -toward);
        }
    }

    /// <summary>
    /// One end of one H-bridge: tied to the motor supply, to ground, or left as a freewheeling
    /// path while the bridge is off. The off state is a low resistance to ground rather than an
    /// open, because the winding's current has to keep flowing somewhere — which is what the
    /// bridge's own diodes do, and what makes the current decay instead of producing a spike.
    /// </summary>
    private void StampBridgeEnd(MnaSystem system, Terminal pin, int local, int toward)
    {
        var branch = system.Branch(this, local);
        var node = system.Node(pin);
        var reference = system.Node(Gnd);

        var supply = MotorSupply(system);

        var voltage = toward switch
        {
            > 0 => supply,
            < 0 => 0.0,
            _ => 0.0,        // both ends to ground: the winding freewheels through the bridge
        };

        system.StampTheveninSource(branch, node, reference, voltage, Math.Max(OnResistance, 1e-3));
    }

    /// <summary>The motor supply: the pin if it is wired to anything, otherwise the property.</summary>
    private double MotorSupply(MnaSystem system)
    {
        var pin = system.IterationVoltage(system.Node(Motor)) - system.IterationVoltage(system.Node(Gnd));

        return pin > 0.5 ? pin : Math.Max(SupplyVoltage, 0.0);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        base.CommitTimeStep(system, state);

        for (var i = 0; i < Windings; i++)
        {
            // Current out of the first end of the winding, which is the winding's own current.
            _current[i] = -system.BranchCurrent(this, i * 2);

            var target = TargetCurrent(i);

            if (!IsEnabled || PushDirection(target) == 0)
            {
                _chopping[i] = false;
                continue;
            }

            if (state.Time < _offUntil[i])
            {
                _chopping[i] = true;
                continue;
            }

            // Reached the limit: stop pushing for the off time and let it decay. That is the whole
            // mechanism, and the frequency of it falls out of the winding and the supply rather
            // than being a number anybody set.
            if (Math.Abs(_current[i]) >= Math.Abs(target))
            {
                _offUntil[i] = state.Time + Math.Max(OffTime, 1e-9);
                _chopping[i] = true;
            }
            else
            {
                _chopping[i] = false;
            }
        }
    }

    /// <summary>
    /// Bounds the time step while the driver is doing anything, in both directions.
    /// <para>
    /// The end of an off time is an obvious breakpoint — step past it and the bridge never comes
    /// back on. The less obvious one is the <i>on</i> phase. A real chopper has a comparator
    /// watching the sense resistor continuously and switches off within nanoseconds of reaching
    /// the limit; a model only finds out at the end of a time step, so the step size decides the
    /// overshoot. At twelve volts into a couple of millihenries the current climbs five thousand
    /// amps a second, and an unbounded step sails several amps past a limit of one.
    /// </para>
    /// <para>
    /// So while it is driving, the step is held to a quarter of the off time. That is not a
    /// physical parameter, it is a sampling rate — the model's answer to a comparator it does not
    /// have — and it keeps the overshoot to a few percent.
    /// </para>
    /// </summary>
    public double? NextBreakpointAfter(double time)
    {
        if (!IsEnabled) return null;

        double? next = null;

        for (var i = 0; i < Windings; i++)
        {
            var until = _offUntil[i];

            // Waiting out an off time: the moment it ends is the next thing that matters.
            // Otherwise it is driving, and wants looking at again shortly.
            var candidate = !double.IsNegativeInfinity(until) && until > time
                ? until
                : time + (Math.Max(OffTime, 1e-9) / 4.0);

            if (next is null || candidate < next) next = candidate;
        }

        return next;
    }

    public override void EvaluateLogic(IDigitalContext context)
    {
        IsEnabled = !context.ReadInput(Enable, Levels).IsHigh();

        // MS1, MS2, MS3 as a three-bit number, which is exactly how the datasheet's table reads.
        var mode = 0;
        if (context.ReadInput(Ms1, Levels).IsHigh()) mode |= 1;
        if (context.ReadInput(Ms2, Levels).IsHigh()) mode |= 2;
        if (context.ReadInput(Ms3, Levels).IsHigh()) mode |= 4;

        Microsteps = mode switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            3 => 8,
            _ => 16,
        };

        var step = context.ReadInput(Step, Levels);

        if (_lastStep.IsLow() && step.IsHigh() && IsEnabled)
        {
            var forwards = context.ReadInput(Direction, Levels).IsHigh();
            var cycle = FullCycle * Microsteps;

            _microstep = ((_microstep + (forwards ? 1 : -1)) % cycle + cycle) % cycle;
            NotifyValueChanged();
        }

        _lastStep = step;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        Array.Clear(_current);
        Array.Clear(_chopping);
        Array.Fill(_offUntil, double.NegativeInfinity);

        _lastStep = LogicState.Low;
        _microstep = 0;
        Microsteps = 1;
        IsEnabled = true;
    }
}
