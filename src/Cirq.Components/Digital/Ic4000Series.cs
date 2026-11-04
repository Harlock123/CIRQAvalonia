using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// A 4511 BCD to seven-segment latch, decoder and driver.
/// <para>
/// The counterpart to the 7447: where that part sinks current from a common-anode display, this
/// one sources it into a common-cathode one, so its outputs are active high. It also has a latch,
/// which the 7447 does not — hold LE high and the display keeps showing whatever was on the inputs
/// when it went high, while the counter behind it carries on.
/// </para>
/// <para>
/// Inputs above nine blank the display rather than showing the odd glyphs a 7447 produces. Lamp
/// test and blanking behave as the datasheet has them, with lamp test taking priority.
/// </para>
/// </summary>
public sealed class Ic4511 : DigitalIc
{
    /// <summary>Segment patterns for 0-9, in the order a..g. Values above nine are blank.</summary>
    private static readonly byte[] Glyphs =
    [
        0b0111111, 0b0000110, 0b1011011, 0b1001111, 0b1100110,
        0b1101101, 0b1111101, 0b0000111, 0b1111111, 0b1101111,
    ];

    private readonly Terminal[] _segments = new Terminal[7];
    private int _latched;

    public Ic4511() : base(16)
    {
        PropagationDelay = 200e-9;
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        B = Pin(1, "B", TerminalType.Input);
        C = Pin(2, "C", TerminalType.Input);
        LampTest = Pin(3, "LT", TerminalType.Input);
        Blanking = Pin(4, "BL", TerminalType.Input);
        LatchEnable = Pin(5, "LE", TerminalType.Input);
        D = Pin(6, "D", TerminalType.Input);
        A = Pin(7, "A", TerminalType.Input);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        _segments[4] = Pin(9, "e", TerminalType.Output);
        _segments[3] = Pin(10, "d", TerminalType.Output);
        _segments[2] = Pin(11, "c", TerminalType.Output);
        _segments[1] = Pin(12, "b", TerminalType.Output);
        _segments[0] = Pin(13, "a", TerminalType.Output);
        _segments[6] = Pin(14, "g", TerminalType.Output);
        _segments[5] = Pin(15, "f", TerminalType.Output);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([A, B, C, D, LampTest, Blanking, LatchEnable], _segments);
    }

    public Terminal A { get; }
    public Terminal B { get; }
    public Terminal C { get; }
    public Terminal D { get; }

    /// <summary>Active low: lights every segment, whatever the inputs say.</summary>
    public Terminal LampTest { get; }

    /// <summary>Active low: blanks the display.</summary>
    public Terminal Blanking { get; }

    /// <summary>Active high: freezes the display on whatever was decoded when it went high.</summary>
    public Terminal LatchEnable { get; }

    /// <summary>Segments a through g.</summary>
    public IReadOnlyList<Terminal> Segments => _segments;

    public override string PartNumber => "4511";

    /// <summary>The digit currently displayed, or the latched one.</summary>
    public int DisplayedValue { get; private set; }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        bool High(Terminal pin) => context.ReadInput(pin, Levels) == LogicState.High;

        // The latch holds the previous value while LE is high.
        if (!High(LatchEnable))
        {
            _latched = (High(A) ? 1 : 0) | (High(B) ? 2 : 0) | (High(C) ? 4 : 0) | (High(D) ? 8 : 0);
        }

        DisplayedValue = _latched;

        // Lamp test beats blanking, which beats the decoded value — the datasheet's order.
        var pattern = !High(LampTest) ? 0b1111111
            : !High(Blanking) ? 0
            : _latched < Glyphs.Length ? Glyphs[_latched]
            : 0;

        for (var segment = 0; segment < 7; segment++)
        {
            // Active high: this drives a common-cathode display, unlike the 7447 which sinks.
            var lit = (pattern & (1 << segment)) != 0;
            context.Schedule(this, segment, lit ? LogicState.High : LogicState.Low, delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _latched = 0;
        DisplayedValue = 0;
    }
}

/// <summary>
/// A 4066 quad bilateral switch: four independent analog switches, each closed by a logic level.
/// <para>
/// This is the one part in the 4000 series here that is not a logic gate. Its switched pins carry
/// whatever analog signal you put on them — audio, a sensor divider, a reference — and the control
/// pin only decides whether the path is there. That is what "bilateral" means: current goes either
/// way, and neither switched pin is an input or an output.
/// </para>
/// <para>
/// A closed switch is a real resistance of some tens of ohms, not a short, and it varies with the
/// supply. Feeding a low-impedance load through one is the usual way to be surprised by how much
/// signal it loses.
/// </para>
/// </summary>
public sealed partial class Ic4066 : DigitalIc
{
    private readonly Terminal[] _a = new Terminal[4];
    private readonly Terminal[] _b = new Terminal[4];
    private readonly Terminal[] _control = new Terminal[4];
    private readonly bool[] _closed = new bool[4];

    public Ic4066() : base(14)
    {
        PropagationDelay = 200e-9;
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[15];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 14));

        _a[0] = Pin(1, "1A", TerminalType.Bidirectional);
        _b[0] = Pin(2, "1B", TerminalType.Bidirectional);
        _b[1] = Pin(3, "2B", TerminalType.Bidirectional);
        _a[1] = Pin(4, "2A", TerminalType.Bidirectional);
        _control[1] = Pin(5, "2C", TerminalType.Input);
        _control[2] = Pin(6, "3C", TerminalType.Input);
        Gnd = Pin(7, "VSS", TerminalType.Ground);
        _a[2] = Pin(8, "3A", TerminalType.Bidirectional);
        _b[2] = Pin(9, "3B", TerminalType.Bidirectional);
        _a[3] = Pin(10, "4A", TerminalType.Bidirectional);
        _b[3] = Pin(11, "4B", TerminalType.Bidirectional);
        _control[3] = Pin(12, "4C", TerminalType.Input);
        _control[0] = Pin(13, "1C", TerminalType.Input);
        Vcc = Pin(14, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        // The control pins are the only logic inputs, and there are no logic outputs at all: the
        // switched paths are stamped as resistances rather than driven.
        ConfigurePins(_control, []);
    }

    /// <summary>Resistance of a closed switch, in ohms.</summary>
    [ObservableProperty]
    public partial double OnResistance { get; set; } = 80.0;

    /// <summary>Resistance of an open switch, in ohms.</summary>
    [ObservableProperty]
    public partial double OffResistance { get; set; } = 1e9;

    public override string PartNumber => "4066";

    /// <summary>The switched pins of one of the four switches, indexed from 0.</summary>
    public (Terminal A, Terminal B) Switch(int index) => (_a[index], _b[index]);

    /// <summary>The control pin of one of the four switches.</summary>
    public Terminal Control(int index) => _control[index];

    /// <summary>Whether a given switch is presently closed.</summary>
    public bool IsClosed(int index) => _closed[index];

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        for (var i = 0; i < 4; i++)
        {
            system.StampResistor(
                system.Node(_a[i]), system.Node(_b[i]),
                Math.Max(_closed[i] ? OnResistance : OffResistance, 1e-3));
        }
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        for (var i = 0; i < 4; i++)
            _closed[i] = context.ReadInput(_control[i], Levels) == LogicState.High;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_closed);
    }
}

/// <summary>
/// A 4013 dual D-type flip-flop with set and reset.
/// <para>
/// The same job as a 7474, with one difference that catches people every time: set and reset here
/// are <b>active high</b>, where the 7474's preset and clear are active low. A 7474 with its
/// preset and clear pins left floating sits there and clocks; a 4013 wired the same way is held in
/// whatever state the noise on those pins decides. Tie them low and it behaves.
/// </para>
/// <para>
/// Pinout: 1=1Q, 2=1Q', 3=1CLK, 4=1RST, 5=1D, 6=1SET, 7=VSS,
/// 8=2SET, 9=2D, 10=2RST, 11=2CLK, 12=2Q', 13=2Q, 14=VDD.
/// </para>
/// </summary>
public sealed class Ic4013 : DigitalIc
{
    private sealed class FlipFlop
    {
        public required Terminal Set;
        public required Terminal Reset;
        public required Terminal Data;
        public required Terminal Clock;
        public required Terminal Q;
        public required Terminal QNot;

        /// <summary>Clock level seen on the previous evaluation, for rising-edge detection.</summary>
        public LogicState LastClock = LogicState.Unknown;

        public LogicState State = LogicState.Low;
    }

    private readonly FlipFlop[] _flipFlops;

    public Ic4013() : base(14)
    {
        PropagationDelay = 150e-9;
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[15];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 14));

        _flipFlops =
        [
            new FlipFlop
            {
                Q = Pin(1, "1Q", TerminalType.Output),
                QNot = Pin(2, "1Q'", TerminalType.Output),
                Clock = Pin(3, "1CLK", TerminalType.Input),
                Reset = Pin(4, "1RST", TerminalType.Input),
                Data = Pin(5, "1D", TerminalType.Input),
                Set = Pin(6, "1SET", TerminalType.Input),
            },
            new FlipFlop
            {
                Set = Pin(8, "2SET", TerminalType.Input),
                Data = Pin(9, "2D", TerminalType.Input),
                Reset = Pin(10, "2RST", TerminalType.Input),
                Clock = Pin(11, "2CLK", TerminalType.Input),
                QNot = Pin(12, "2Q'", TerminalType.Output),
                Q = Pin(13, "2Q", TerminalType.Output),
            },
        ];

        Gnd = Pin(7, "VSS", TerminalType.Ground);
        Vcc = Pin(14, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        ConfigurePins(
            [.. _flipFlops.SelectMany(f => new[] { f.Set, f.Reset, f.Data, f.Clock })],
            [_flipFlops[0].Q, _flipFlops[0].QNot, _flipFlops[1].Q, _flipFlops[1].QNot]);
    }

    public override string PartNumber => "4013";

    /// <summary>Current Q state of one of the two flip-flops, indexed from 0.</summary>
    public LogicState QState(int index) => _flipFlops[index].State;

    /// <summary>The pins of one of the two flip-flops, indexed from 0.</summary>
    public (Terminal Set, Terminal Reset, Terminal Data, Terminal Clock, Terminal Q, Terminal QNot)
        Section(int index)
    {
        var f = _flipFlops[index];
        return (f.Set, f.Reset, f.Data, f.Clock, f.Q, f.QNot);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        for (var i = 0; i < _flipFlops.Length; i++)
        {
            var f = _flipFlops[i];
            var set = context.ReadInput(f.Set, Levels);
            var reset = context.ReadInput(f.Reset, Levels);
            var clock = context.ReadInput(f.Clock, Levels);

            // Set and reset are asynchronous and beat the clock. Both asserted is the illegal
            // state, and the datasheet leaves both outputs high rather than pretending otherwise.
            if (set.IsHigh() && reset.IsHigh())
            {
                f.State = LogicState.High;
                f.LastClock = clock;
                context.Schedule(this, (i * 2) + 0, LogicState.High, delay);
                context.Schedule(this, (i * 2) + 1, LogicState.High, delay);
                continue;
            }

            if (set.IsHigh())
            {
                f.State = LogicState.High;
            }
            else if (reset.IsHigh())
            {
                f.State = LogicState.Low;
            }
            else if (f.LastClock.IsLow() && clock.IsHigh())
            {
                f.State = context.ReadInput(f.Data, Levels);
            }

            f.LastClock = clock;
            context.Schedule(this, (i * 2) + 0, f.State, delay);
            context.Schedule(this, (i * 2) + 1, f.State.Invert(), delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        foreach (var f in _flipFlops)
        {
            f.State = LogicState.Low;
            f.LastClock = LogicState.Unknown;
        }
    }
}
