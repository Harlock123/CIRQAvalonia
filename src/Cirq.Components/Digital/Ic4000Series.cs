using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// A 4017 decade counter with ten decoded outputs.
/// <para>
/// Unlike a 7490, which counts in BCD and needs a decoder to show anything, the 4017 decodes for
/// you: exactly one of its ten outputs is high at a time and it walks along them on each clock.
/// That is why it is the part behind every LED chaser — ten LEDs, ten pins, no decoder.
/// </para>
/// <para>
/// Carry-out is high for the first five counts and low for the last five, so it divides the clock
/// by ten and can be chained straight into the next stage's clock.
/// </para>
/// </summary>
public sealed class Ic4017 : DigitalIc
{
    private LogicState _lastClock = LogicState.Unknown;
    private int _count;

    public Ic4017() : base(16)
    {
        PropagationDelay = 200e-9;          // CMOS at 5 V is far slower than TTL
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        var q = new Terminal[10];
        q[5] = Pin(1, "Q5", TerminalType.Output);
        q[1] = Pin(2, "Q1", TerminalType.Output);
        q[0] = Pin(3, "Q0", TerminalType.Output);
        q[2] = Pin(4, "Q2", TerminalType.Output);
        q[6] = Pin(5, "Q6", TerminalType.Output);
        q[7] = Pin(6, "Q7", TerminalType.Output);
        q[3] = Pin(7, "Q3", TerminalType.Output);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        q[8] = Pin(9, "Q8", TerminalType.Output);
        q[4] = Pin(10, "Q4", TerminalType.Output);
        q[9] = Pin(11, "Q9", TerminalType.Output);
        CarryOut = Pin(12, "CO", TerminalType.Output);
        ClockInhibit = Pin(13, "INH", TerminalType.Input);
        Clock = Pin(14, "CLK", TerminalType.Input);
        Reset = Pin(15, "RST", TerminalType.Input);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Outputs = q;
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Clock, ClockInhibit, Reset], [.. q, CarryOut]);
    }

    /// <summary>The ten decoded outputs, Q0 through Q9.</summary>
    public IReadOnlyList<Terminal> Outputs { get; }

    public Terminal CarryOut { get; }

    public Terminal Clock { get; }

    public Terminal ClockInhibit { get; }

    public Terminal Reset { get; }

    public override string PartNumber => "4017";

    /// <summary>Which output is currently high, 0 through 9.</summary>
    public int Count => _count;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        if (context.ReadInput(Reset, Levels) == LogicState.High)
        {
            _count = 0;
        }
        else
        {
            var clock = context.ReadInput(Clock, Levels);
            var inhibited = context.ReadInput(ClockInhibit, Levels) == LogicState.High;

            // Rising edge, and only while the inhibit pin is low.
            if (!inhibited && clock == LogicState.High && _lastClock == LogicState.Low)
                _count = (_count + 1) % 10;

            _lastClock = clock;
        }

        for (var i = 0; i < 10; i++)
            context.Schedule(this, i, i == _count ? LogicState.High : LogicState.Low, delay);

        // Carry is high over the first half of the cycle, so it divides the clock by ten.
        context.Schedule(this, 10, _count < 5 ? LogicState.High : LogicState.Low, delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _lastClock = LogicState.Unknown;
        _count = 0;
    }
}

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

/// <summary>
/// A 4040 twelve-stage binary ripple counter.
/// <para>
/// Twelve flip-flops in a chain, so Q1 is the clock divided by two and Q12 is the clock divided by
/// 4096. That is what it is for: dividing something fast down to something slow. A 32.768 kHz
/// crystal into Q12 gives you eight ticks a second, and the next part along gets you to one.
/// </para>
/// <para>
/// It counts on the <b>falling</b> edge, unlike the 4017 beside it, and its reset is active high —
/// tie it low or the counter never gets going. Being a ripple counter rather than a synchronous
/// one, the stages do not change together: a real 4040 shows brief false codes as the carry walks
/// down the chain, which is why decoding its outputs directly is a way to get glitches.
/// </para>
/// <para>
/// Pinout: 1=Q12, 2=Q6, 3=Q5, 4=Q7, 5=Q4, 6=Q3, 7=Q2, 8=VSS,
/// 9=Q1, 10=CLK, 11=RST, 12=Q9, 13=Q8, 14=Q10, 15=Q11, 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4040 : DigitalIc
{
    private LogicState _lastClock = LogicState.Unknown;
    private int _count;

    public Ic4040() : base(16)
    {
        PropagationDelay = 200e-9;
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        var q = new Terminal[12];
        q[11] = Pin(1, "Q12", TerminalType.Output);
        q[5] = Pin(2, "Q6", TerminalType.Output);
        q[4] = Pin(3, "Q5", TerminalType.Output);
        q[6] = Pin(4, "Q7", TerminalType.Output);
        q[3] = Pin(5, "Q4", TerminalType.Output);
        q[2] = Pin(6, "Q3", TerminalType.Output);
        q[1] = Pin(7, "Q2", TerminalType.Output);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        q[0] = Pin(9, "Q1", TerminalType.Output);
        Clock = Pin(10, "CLK", TerminalType.Input);
        Reset = Pin(11, "RST", TerminalType.Input);
        q[8] = Pin(12, "Q9", TerminalType.Output);
        q[7] = Pin(13, "Q8", TerminalType.Output);
        q[9] = Pin(14, "Q10", TerminalType.Output);
        q[10] = Pin(15, "Q11", TerminalType.Output);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Outputs = q;
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Clock, Reset], q);
    }

    /// <summary>The twelve stage outputs, Q1 (divide by two) through Q12 (divide by 4096).</summary>
    public IReadOnlyList<Terminal> Outputs { get; }

    public Terminal Clock { get; }

    public Terminal Reset { get; }

    public override string PartNumber => "4040";

    /// <summary>The value on the outputs, 0 through 4095.</summary>
    public int Count => _count;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        if (context.ReadInput(Reset, Levels) == LogicState.High)
        {
            _count = 0;
        }
        else
        {
            var clock = context.ReadInput(Clock, Levels);

            // Falling edge, which is the other way round from the 4017.
            if (clock == LogicState.Low && _lastClock == LogicState.High)
                _count = (_count + 1) & 0xFFF;

            _lastClock = clock;
        }

        for (var stage = 0; stage < 12; stage++)
        {
            var bit = (_count & (1 << stage)) != 0;
            context.Schedule(this, stage, bit ? LogicState.High : LogicState.Low, delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _lastClock = LogicState.Unknown;
        _count = 0;
    }
}

/// <summary>
/// A 4051 single eight-channel analog multiplexer and demultiplexer.
/// <para>
/// The 4066 with an address decoder in front of it: three address pins pick one of eight channels
/// and connect it to the common pin, and the other seven are left open. Like the 4066 the path is
/// bilateral, so the same part reads eight sensors into one ADC pin or fans one signal out to
/// eight places, depending only on which end you drive.
/// </para>
/// <para>
/// Inhibit is active high and disconnects everything, which is what you use to park it or to gang
/// several of them onto one bus. Exactly one channel is connected at a time, so unlike four
/// separate 4066 switches there is no way to short two sources together by accident.
/// </para>
/// <para>
/// A real part has VEE for passing signals below ground; here it is a pin you can tie off, and the
/// switch conducts regardless of where the signal sits.
/// </para>
/// <para>
/// Pinout: 1=CH4, 2=CH6, 3=COM, 4=CH7, 5=CH5, 6=INH, 7=VEE, 8=VSS,
/// 9=C, 10=B, 11=A, 12=CH3, 13=CH0, 14=CH1, 15=CH2, 16=VDD.
/// </para>
/// </summary>
public sealed partial class Ic4051 : DigitalIc
{
    private readonly Terminal[] _channels = new Terminal[8];

    /// <summary>Which channel is connected to the common pin, or -1 while inhibited.</summary>
    private int _selected = -1;

    public Ic4051() : base(16)
    {
        PropagationDelay = 200e-9;
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        _channels[4] = Pin(1, "CH4", TerminalType.Bidirectional);
        _channels[6] = Pin(2, "CH6", TerminalType.Bidirectional);
        Common = Pin(3, "COM", TerminalType.Bidirectional);
        _channels[7] = Pin(4, "CH7", TerminalType.Bidirectional);
        _channels[5] = Pin(5, "CH5", TerminalType.Bidirectional);
        Inhibit = Pin(6, "INH", TerminalType.Input);
        NegativeSupply = Pin(7, "VEE", TerminalType.Passive);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        C = Pin(9, "C", TerminalType.Input);
        B = Pin(10, "B", TerminalType.Input);
        A = Pin(11, "A", TerminalType.Input);
        _channels[3] = Pin(12, "CH3", TerminalType.Bidirectional);
        _channels[0] = Pin(13, "CH0", TerminalType.Bidirectional);
        _channels[1] = Pin(14, "CH1", TerminalType.Bidirectional);
        _channels[2] = Pin(15, "CH2", TerminalType.Bidirectional);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        // The address and inhibit pins are the only logic inputs, and there are no logic outputs:
        // the channel paths are stamped as resistances rather than driven, as on the 4066.
        ConfigurePins([A, B, C, Inhibit], []);
    }

    /// <summary>The pin every selected channel connects to.</summary>
    public Terminal Common { get; }

    /// <summary>Active high: disconnects every channel.</summary>
    public Terminal Inhibit { get; }

    /// <summary>Address bit 0, the least significant.</summary>
    public Terminal A { get; }

    public Terminal B { get; }

    /// <summary>Address bit 2, the most significant.</summary>
    public Terminal C { get; }

    /// <summary>The negative supply of a real part. Tie it to VSS for single-supply use.</summary>
    public Terminal NegativeSupply { get; }

    /// <summary>Resistance of the selected channel, in ohms.</summary>
    [ObservableProperty]
    public partial double OnResistance { get; set; } = 125.0;

    /// <summary>Resistance of an unselected channel, in ohms.</summary>
    [ObservableProperty]
    public partial double OffResistance { get; set; } = 1e9;

    public override string PartNumber => "4051";

    /// <summary>One of the eight channel pins, indexed from 0.</summary>
    public Terminal Channel(int index) => _channels[index];

    /// <summary>Which channel is connected to <see cref="Common"/>, or -1 while inhibited.</summary>
    public int SelectedChannel => _selected;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        var common = system.Node(Common);

        for (var i = 0; i < 8; i++)
        {
            system.StampResistor(
                common, system.Node(_channels[i]),
                Math.Max(i == _selected ? OnResistance : OffResistance, 1e-3));
        }
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        if (context.ReadInput(Inhibit, Levels) == LogicState.High)
        {
            _selected = -1;
            return;
        }

        var address = 0;
        if (context.ReadInput(A, Levels) == LogicState.High) address |= 1;
        if (context.ReadInput(B, Levels) == LogicState.High) address |= 2;
        if (context.ReadInput(C, Levels) == LogicState.High) address |= 4;

        _selected = address;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _selected = -1;
    }
}
