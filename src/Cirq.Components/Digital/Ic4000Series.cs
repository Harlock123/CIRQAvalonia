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
