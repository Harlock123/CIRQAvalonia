using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// An RS-485 transceiver — the SN75176 / MAX485 sort: a differential driver and a differential
/// receiver sharing one pair of wires, which is how a signal is got down a hundred metres of
/// cable in a factory and arrives meaning what it meant.
/// <para>
/// A UART's own pins measure against <b>ground</b>, and over any distance that is the problem:
/// the ground at the far end is not the ground at this end, and whatever the difference happens to
/// be gets added to every bit. RS-485 measures one wire against <i>the other</i>. Noise picked up
/// along the way lands on both equally and subtracts out, and the two grounds can differ by volts
/// without anybody noticing. That is the whole idea, and everything else about the standard
/// follows from it.
/// </para>
/// <para>
/// Three things bite people, and all three are modelled.
/// </para>
/// <para>
/// <b>An idle bus is not a low bus — it is floating.</b> Nothing drives the pair between
/// transmissions, so the difference across it is whatever noise happens to be there, and a
/// receiver faithfully reports the sign of it. That is a stream of invented characters arriving
/// from a bus nobody is talking on, and it is the classic first fault. The cure is failsafe
/// biasing — a pair of resistors that hold the line a couple of hundred millivolts apart when
/// nothing else is driving it — or a modern receiver that has it built in, which
/// <see cref="HasFailSafe"/> switches on.
/// </para>
/// <para>
/// <b>It is half duplex, and the enable is yours to drive.</b> Two devices driving at once is a
/// short between two stiff sources, not merely a collision, and releasing the driver too early
/// truncates the last character.
/// </para>
/// <para>
/// <b>A long line is a transmission line.</b> Put one at each end of the
/// <see cref="Passive.TransmissionLine"/> in the palette and the reason for the 120 Ω terminator
/// is visible rather than a rule: unterminated, every edge rings for as long as the cable takes to
/// settle.
/// </para>
/// </summary>
public sealed partial class Rs485Transceiver : DigitalComponent
{
    private const int ReceiverOutput = 0;

    private double _difference;

    /// <summary>What the driver is holding the pair at, or null while it is letting go.</summary>
    private (double A, double B)? _drive;

    public Rs485Transceiver()
    {
        PropagationDelay = 30e-9;
        Levels = LogicLevels.Cmos5V;

        // Everything but the pair down the left, so no lead has to be drawn across the package.
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-50, -48));
        ReceiverOut = new Terminal("ro", "RO", TerminalType.Output, new Point(-50, -28));
        ReceiverEnable = new Terminal("re", "RE", TerminalType.Input, new Point(-50, -10));
        DriverEnable = new Terminal("de", "DE", TerminalType.Input, new Point(-50, 10));
        DriverIn = new Terminal("di", "DI", TerminalType.Input, new Point(-50, 28));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-50, 48));

        A = new Terminal("a", "A", TerminalType.Bidirectional, new Point(50, -18));
        B = new Terminal("b", "B", TerminalType.Bidirectional, new Point(50, 18));

        Terminals = [ReceiverOut, ReceiverEnable, DriverEnable, DriverIn, A, B, Vcc, Gnd];

        // Only RO is an ordinary logic output. The pair is driven by this component's own
        // branches, because an RS-485 driver swings about mid-supply rather than between the
        // logic family's rails, and the base class has one answer for every output pin.
        ConfigurePins([ReceiverEnable, DriverEnable, DriverIn], [ReceiverOut]);
    }

    /// <summary>What the receiver heard, to the UART's RX pin.</summary>
    public Terminal ReceiverOut { get; }

    /// <summary>Receiver enable, active low — so tying it to ground leaves the receiver on.</summary>
    public Terminal ReceiverEnable { get; }

    /// <summary>Driver enable, active high. Half duplex means this is yours to get right.</summary>
    public Terminal DriverEnable { get; }

    /// <summary>From the UART's TX pin.</summary>
    public Terminal DriverIn { get; }

    /// <summary>The non-inverting side of the pair.</summary>
    public Terminal A { get; }

    /// <summary>The inverting side.</summary>
    public Terminal B { get; }

    public Terminal Vcc { get; }

    public Terminal Gnd { get; }

    /// <summary>
    /// How far apart the driver pulls the two wires, in volts. The standard asks for at least 1.5 V
    /// into a fully loaded bus; a real part managing 2 V or so is typical.
    /// </summary>
    [ObservableProperty]
    public partial double DifferentialDrive { get; set; } = 2.0;

    /// <summary>
    /// The difference the receiver needs before it will call a bit, in volts. Two hundred
    /// millivolts is what the standard requires, and it is why the pair can be so long.
    /// </summary>
    [ObservableProperty]
    public partial double ReceiverThreshold { get; set; } = 0.2;

    /// <summary>
    /// Whether the receiver holds its output high when the bus is idle rather than reporting
    /// whatever noise it can hear. Modern parts have this; the original ones do not, and turning
    /// it off is how to watch a quiet bus invent characters.
    /// </summary>
    [ObservableProperty]
    public partial bool HasFailSafe { get; set; } = true;

    /// <summary>Resistance behind each of the two driver pins, in ohms.</summary>
    [ObservableProperty]
    public partial double DriverResistance { get; set; } = 12.0;

    /// <summary>The receiver's input resistance across the pair, in ohms — one unit load.</summary>
    [ObservableProperty]
    public partial double InputResistance { get; set; } = 12e3;

    /// <summary>
    /// How far either wire may sit from this end's ground and still be read, in volts. The
    /// standard asks for −7 V to +12 V, and that window is the budget the whole idea is spent
    /// from: it is what the two ends' grounds are allowed to differ by, plus whatever common-mode
    /// noise the cable has picked up along the way. Past it the receiver stops being a
    /// differential amplifier, and the difference between the wires no longer decides anything.
    /// </summary>
    [ObservableProperty]
    public partial double CommonModeLow { get; set; } = -7.0;

    /// <summary>The top of that window.</summary>
    [ObservableProperty]
    public partial double CommonModeHigh { get; set; } = 12.0;

    public override string ComponentType => "RS-485 Transceiver";

    public override string ValueLabel => IsDriving ? "driving" : "listening";

    /// <summary>A − B at the last solved point, which is the only thing the receiver looks at.</summary>
    public double Difference => _difference;

    /// <summary>True while this transceiver is driving the pair.</summary>
    public bool IsDriving { get; private set; }

    /// <summary>
    /// True when the receiver is enabled and the pair is not far enough apart to call — the state
    /// a quiet bus is in, and the one that invents characters without failsafe biasing.
    /// </summary>
    public bool IsBusIdle { get; private set; }

    /// <summary>
    /// True when the pair has drifted outside the window this end can read it in. The difference
    /// between the wires may be perfect and it will still not be decoded, which is what makes a
    /// long run with a big ground offset fail in a way that looks like nothing at all.
    /// </summary>
    public bool IsOutOfCommonModeRange { get; private set; }

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    /// <summary>RO from the base class, plus one branch each for the two bus pins.</summary>
    public override int VoltageSourceCount => base.VoltageSourceCount + 2;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        // The receiver is a resistance across the pair whether or not it is enabled — a unit load,
        // and the reason a standard bus is limited to thirty-two of them.
        system.StampConductance(system.Node(A), system.Node(B), 1.0 / Math.Max(InputResistance, 1.0));

        StampBusPin(system, A, 1, _drive?.A);
        StampBusPin(system, B, 2, _drive?.B);
    }

    /// <summary>
    /// One of the two bus pins: a stiff source at whatever the driver is holding it to, or a
    /// branch carrying no current at all while the driver has let go — which is what leaves an
    /// idle bus floating rather than sitting anywhere in particular.
    /// </summary>
    private void StampBusPin(MnaSystem system, Terminal pin, int local, double? driveTo)
    {
        var branch = system.Branch(this, local);

        if (driveTo is not { } voltage)
        {
            system.Add(branch, branch, 1.0);
            return;
        }

        system.StampTheveninSource(
            branch, system.Node(pin), system.Node(Gnd), voltage, Math.Max(DriverResistance, 1e-3));
    }

    public override void EvaluateLogic(IDigitalContext context)
    {
        var supply = context.NodeVoltage(Vcc) - context.NodeVoltage(Gnd);
        var delay = DelayFor(context);

        var driving = context.ReadInput(DriverEnable, Levels) == LogicState.High;
        var listening = context.ReadInput(ReceiverEnable, Levels) != LogicState.High;

        IsDriving = driving;

        if (driving)
        {
            var bit = context.ReadInput(DriverIn, Levels) == LogicState.High;

            // The pair swings symmetrically about mid-supply, which is what keeps both wires
            // inside the rails while putting the whole differential voltage between them. A goes
            // high and B low for a one, and the other way round for a zero — swapping the pair
            // inverts every bit on the wire without breaking anything else, which is why it is
            // the first thing to check on a link that will not talk.
            var middle = supply / 2.0;
            var half = Math.Max(DifferentialDrive, 0.0) / 2.0;

            _drive = bit ? (middle + half, middle - half) : (middle - half, middle + half);
        }
        else
        {
            // Not driving means letting go entirely, which is what leaves an idle bus floating.
            _drive = null;
        }

        var reference = context.NodeVoltage(Gnd);
        var onA = context.NodeVoltage(A) - reference;
        var onB = context.NodeVoltage(B) - reference;

        _difference = onA - onB;

        IsOutOfCommonModeRange =
            Math.Min(onA, onB) < CommonModeLow || Math.Max(onA, onB) > CommonModeHigh;

        IsBusIdle = listening && Math.Abs(_difference) < ReceiverThreshold;

        if (!listening)
        {
            context.Schedule(this, ReceiverOutput, LogicState.HighImpedance, delay);
            return;
        }

        // Outside the common-mode window the receiver is not a differential amplifier any more,
        // so what the wires are doing relative to each other stops mattering.
        if (IsOutOfCommonModeRange)
        {
            context.Schedule(this, ReceiverOutput, LogicState.Unknown, delay);
            return;
        }

        // Past the threshold the answer is the sign of the difference. Inside it there is nothing
        // to go on: a failsafe receiver says one, and a receiver without it reports the sign of
        // whatever noise is on the pair, which is exactly the fault worth being able to see.
        var received = IsBusIdle
            ? HasFailSafe || _difference >= 0
            : _difference > 0;

        context.Schedule(this, ReceiverOutput, received ? LogicState.High : LogicState.Low, delay);
    }

    public IReadOnlyList<string> Violations
    {
        get
        {
            List<string> found = [];

            if (IsOutOfCommonModeRange)
            {
                found.Add($"the pair has drifted outside the {CommonModeLow:0.#} V to " +
                          $"{CommonModeHigh:0.#} V window this end can read it in, so the difference " +
                          "between the wires no longer decides anything");
            }

            if (IsBusIdle && !HasFailSafe)
            {
                found.Add("the bus is idle and nothing is biasing it, so the receiver is reporting " +
                          "the sign of whatever noise is on the pair — which arrives at the UART as " +
                          "characters nobody sent");
            }

            return found;
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _difference = 0;
        _drive = null;
        IsDriving = false;
        IsBusIdle = false;
        IsOutOfCommonModeRange = false;
    }

    partial void OnHasFailSafeChanged(bool value) => NotifyValueChanged();
}
