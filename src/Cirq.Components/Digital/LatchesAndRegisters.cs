using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for the quad transparent latches with true and complement outputs, the 7475 and the 4042.
/// Each bit follows its D input while it is open and holds what D was when it closed; the parts
/// differ only in what opens them, which is left to <see cref="IsOpen"/>.
/// </summary>
public abstract class QuadLatchIc : DigitalIc
{
    private readonly Terminal[] _d = new Terminal[4];
    private readonly Terminal[] _q = new Terminal[4];
    private readonly Terminal[] _qNot = new Terminal[4];
    private int _held;

    /// <summary>Builds the pins; <paramref name="pins"/> gives D, Q and Q' per bit, then the control pins are the subclass's.</summary>
    protected QuadLatchIc(int[][] pins, int vccPin, int gndPin, bool cmos, params (int Pin, string Name)[] controls)
        : base(16)
    {
        Levels = cmos ? LogicLevels.Cmos5V : LogicLevels.Ttl;
        PropagationDelay = cmos ? 150e-9 : 15e-9;

        var all = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            all[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        for (var i = 0; i < 4; i++)
        {
            _d[i] = Pin(pins[i][0], $"{i + 1}D", TerminalType.Input);
            _q[i] = Pin(pins[i][1], $"{i + 1}Q", TerminalType.Output);
            _qNot[i] = Pin(pins[i][2], $"{i + 1}Q'", TerminalType.Output);
        }

        Controls = [.. controls.Select(c => Pin(c.Pin, c.Name, TerminalType.Input))];
        Gnd = Pin(gndPin, cmos ? "VSS" : "GND", TerminalType.Ground);
        Vcc = Pin(vccPin, cmos ? "VDD" : "VCC", TerminalType.Power);

        Terminals = [.. all.Skip(1)];

        // Outputs in the order 1Q, 1Q', 2Q, 2Q' and so on.
        ConfigurePins([.. _d, .. Controls], [.. Enumerable.Range(0, 4).SelectMany(i => new[] { _q[i], _qNot[i] })]);
    }

    /// <summary>The enable or clock and polarity pins, in the order the part declares them.</summary>
    protected IReadOnlyList<Terminal> Controls { get; }

    /// <summary>D1 to D4.</summary>
    public IReadOnlyList<Terminal> Data => _d;

    /// <summary>Q1 to Q4.</summary>
    public IReadOnlyList<Terminal> Outputs => _q;

    /// <summary>Q1' to Q4'.</summary>
    public IReadOnlyList<Terminal> InvertedOutputs => _qNot;

    /// <summary>The four bits held, bit 0 the first latch.</summary>
    public int Value => _held;

    public override string ValueLabel => $"{Convert.ToString(_held, 2).PadLeft(4, '0')}";

    /// <summary>Whether a given bit is following its input at the moment.</summary>
    protected abstract bool IsOpen(IDigitalContext context, int bit);

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var before = _held;

        for (var i = 0; i < 4; i++)
        {
            if (IsOpen(context, i))
            {
                if (context.ReadInput(_d[i], Levels).IsHigh()) _held |= 1 << i;
                else _held &= ~(1 << i);
            }

            var bit = LogicStateExtensions.FromBool((_held & (1 << i)) != 0);
            Request(context, 2 * i, bit, delay);
            Request(context, (2 * i) + 1, bit.Invert(), delay);
        }

        if (before != _held) NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _held = 0;
    }
}

/// <summary>
/// 7475 quad bistable latch: four transparent latches in two pairs, each pair opened by its own
/// enable (high follows, low holds), with true and complement outputs. Its supply is not on the
/// corners — VCC on 5 and GND on 12, like the 7476.
/// <para>
/// Pinout: 1=1Q', 2=1D, 3=2D, 4=3-4C, 5=VCC, 6=3D, 7=4D, 8=4Q', 9=4Q, 10=3Q, 11=3Q', 12=GND,
/// 13=1-2C, 14=2Q', 15=2Q, 16=1Q.
/// </para>
/// </summary>
public sealed class Ic7475() : QuadLatchIc(
    [[2, 16, 1], [3, 15, 14], [6, 10, 11], [7, 9, 8]], vccPin: 5, gndPin: 12, cmos: false,
    (13, "1-2C"), (4, "3-4C"))
{
    public override string PartNumber => "7475";

    /// <summary>Enable for latches 1 and 2, active high.</summary>
    public Terminal Enable12 => Controls[0];

    /// <summary>Enable for latches 3 and 4, active high.</summary>
    public Terminal Enable34 => Controls[1];

    protected override bool IsOpen(IDigitalContext context, int bit) =>
        context.ReadInput(bit < 2 ? Enable12 : Enable34, Levels).IsHigh();
}

/// <summary>
/// 4042 quad clocked D latch with a polarity pin: the latches follow D while CLOCK equals POLARITY
/// and hold from the moment it differs. Polarity low makes it a latch open while the clock is low;
/// high, one open while it is high.
/// <para>
/// Pinout: 1=4Q, 2=1Q, 3=1Q', 4=1D, 5=CLOCK, 6=POLARITY, 7=2D, 8=VSS, 9=2Q', 10=2Q, 11=3Q, 12=3Q',
/// 13=3D, 14=4D, 15=4Q', 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4042() : QuadLatchIc(
    [[4, 2, 3], [7, 10, 9], [13, 11, 12], [14, 1, 15]], vccPin: 16, gndPin: 8, cmos: true,
    (5, "CLOCK"), (6, "POLARITY"))
{
    public override string PartNumber => "4042";

    public Terminal Clock => Controls[0];

    public Terminal Polarity => Controls[1];

    protected override bool IsOpen(IDigitalContext context, int bit) =>
        context.ReadInput(Clock, Levels).IsHigh() == context.ReadInput(Polarity, Levels).IsHigh();
}

/// <summary>
/// Base for the D flip-flop registers with a common clock and a common asynchronous clear — the
/// 74174 (six bits, Q only), the 74175 (four, Q and Q') and the 74273 (eight). Every bit takes its D
/// on the rising edge of CLK, all together, and /CLR low empties them at once without waiting for a
/// clock. Unlike the 74374 these outputs are always driven: a register for holding a value, not for
/// putting one on a bus.
/// </summary>
public abstract class ClockedRegisterIc : DigitalIc
{
    private readonly Terminal[] _d;
    private readonly Terminal[] _q;
    private readonly Terminal[] _qNot;

    private int _held;
    private LogicState _lastClock = LogicState.Unknown;

    /// <param name="bits">Pin numbers per bit: D, Q, and Q' where the part has it.</param>
    protected ClockedRegisterIc(int pinCount, int[][] bits, int clearPin, int clockPin, int vccPin, int gndPin)
        : base(pinCount)
    {
        PropagationDelay = 20e-9;

        var pins = new Terminal[pinCount + 1];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, pinCount));

        _d = new Terminal[bits.Length];
        _q = new Terminal[bits.Length];
        _qNot = bits[0].Length > 2 ? new Terminal[bits.Length] : [];

        for (var i = 0; i < bits.Length; i++)
        {
            _d[i] = Pin(bits[i][0], $"{i + 1}D", TerminalType.Input);
            _q[i] = Pin(bits[i][1], $"{i + 1}Q", TerminalType.Output);
            if (bits[i].Length > 2) _qNot[i] = Pin(bits[i][2], $"{i + 1}Q'", TerminalType.Output);
        }

        Clear = Pin(clearPin, "CLR", TerminalType.Input);
        Clock = Pin(clockPin, "CLK", TerminalType.Input);
        Gnd = Pin(gndPin, "GND", TerminalType.Ground);
        Vcc = Pin(vccPin, "VCC", TerminalType.Power);

        HasComplements = bits[0].Length > 2;
        Terminals = [.. pins.Skip(1)];

        ConfigurePins([Clear, Clock, .. _d],
            HasComplements ? [.. Enumerable.Range(0, bits.Length).SelectMany(i => new[] { _q[i], _qNot[i] })] : _q);
    }

    /// <summary>Asynchronous clear, active low.</summary>
    public Terminal Clear { get; }

    /// <summary>Rising edges take every D at once.</summary>
    public Terminal Clock { get; }

    /// <summary>The D inputs, bit 0 first.</summary>
    public IReadOnlyList<Terminal> Data => _d;

    /// <summary>The Q outputs, bit 0 first.</summary>
    public IReadOnlyList<Terminal> Outputs => _q;

    /// <summary>The Q' outputs on the 74175; empty on the parts without them.</summary>
    public IReadOnlyList<Terminal> InvertedOutputs => _qNot;

    /// <summary>True for the 74175, which brings out Q' as well as Q.</summary>
    public bool HasComplements { get; }

    /// <summary>What it is holding, bit 0 the first flip-flop.</summary>
    public int Value => _held;

    public override string ValueLabel => $"0x{_held:X2}";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var clock = context.ReadInput(Clock, Levels);
        var before = _held;

        if (context.ReadInput(Clear, Levels).IsLow())
        {
            _held = 0;
        }
        else if (!context.IsInitializing && _lastClock.IsLow() && clock.IsHigh())
        {
            var value = 0;
            for (var i = 0; i < _d.Length; i++)
                if (context.ReadInput(_d[i], Levels).IsHigh()) value |= 1 << i;

            _held = value;
        }

        _lastClock = clock;

        for (var i = 0; i < _d.Length; i++)
        {
            var bit = LogicStateExtensions.FromBool((_held & (1 << i)) != 0);

            if (HasComplements)
            {
                Request(context, 2 * i, bit, delay);
                Request(context, (2 * i) + 1, bit.Invert(), delay);
            }
            else
            {
                Request(context, i, bit, delay);
            }
        }

        if (before != _held) NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _held = 0;
        _lastClock = LogicState.Unknown;
    }
}

/// <summary>
/// 74174 hex D flip-flop with common clock and clear. Pinout: 1=CLR, 2=1Q, 3=1D, 4=2D, 5=2Q, 6=3D,
/// 7=3Q, 8=GND, 9=CLK, 10=4Q, 11=4D, 12=5Q, 13=5D, 14=6D, 15=6Q, 16=VCC.
/// </summary>
public sealed class Ic74174() : ClockedRegisterIc(16,
    [[3, 2], [4, 5], [6, 7], [11, 10], [13, 12], [14, 15]], clearPin: 1, clockPin: 9, vccPin: 16, gndPin: 8)
{
    public override string PartNumber => "74174";
}

/// <summary>
/// 74175 quad D flip-flop with common clock and clear, and Q' as well as Q. Pinout: 1=CLR, 2=1Q,
/// 3=1Q', 4=1D, 5=2D, 6=2Q', 7=2Q, 8=GND, 9=CLK, 10=3Q, 11=3Q', 12=3D, 13=4D, 14=4Q', 15=4Q, 16=VCC.
/// </summary>
public sealed class Ic74175() : ClockedRegisterIc(16,
    [[4, 2, 3], [5, 7, 6], [12, 10, 11], [13, 15, 14]], clearPin: 1, clockPin: 9, vccPin: 16, gndPin: 8)
{
    public override string PartNumber => "74175";
}

/// <summary>
/// 74273 octal D flip-flop with clear: the 74374's pinout with /CLR on pin 1 where the 374 has /OE,
/// so the outputs are always driven. Pinout: 1=CLR, 2=1Q, 3=1D, 4=2D, 5=2Q, 6=3Q, 7=3D, 8=4D, 9=4Q,
/// 10=GND, 11=CLK, 12=5Q, 13=5D, 14=6D, 15=6Q, 16=7Q, 17=7D, 18=8D, 19=8Q, 20=VCC.
/// </summary>
public sealed class Ic74273() : ClockedRegisterIc(20,
    [[3, 2], [4, 5], [7, 6], [8, 9], [13, 12], [14, 15], [17, 16], [18, 19]],
    clearPin: 1, clockPin: 11, vccPin: 20, gndPin: 10)
{
    public override string PartNumber => "74273";
}
