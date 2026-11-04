using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// 74139 dual 2-to-4 line decoder with active-low outputs — the address decoder in more small
/// computers than any other part, because two address lines and an enable split a memory map into
/// four, twice.
/// <para>
/// Each half is independent: with its enable /G low, the output numbered by B and A goes low and the
/// other three stay high; with /G high all four are high. Active-low outputs because the chip
/// selects they drive are active low.
/// </para>
/// <para>
/// Pinout: 1=/1G, 2=1A, 3=1B, 4=1Y0, 5=1Y1, 6=1Y2, 7=1Y3, 8=GND, 9=2Y3, 10=2Y2, 11=2Y1, 12=2Y0,
/// 13=2B, 14=2A, 15=/2G, 16=VCC.
/// </para>
/// </summary>
public sealed class Ic74139 : DigitalIc
{
    private readonly Terminal[] _enable = new Terminal[2];
    private readonly Terminal[] _a = new Terminal[2];
    private readonly Terminal[] _b = new Terminal[2];
    private readonly Terminal[][] _y = [new Terminal[4], new Terminal[4]];

    public Ic74139() : base(16)
    {
        PropagationDelay = 18e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        _enable[0] = Pin(1, "1G", TerminalType.Input);
        _a[0] = Pin(2, "1A", TerminalType.Input);
        _b[0] = Pin(3, "1B", TerminalType.Input);
        for (var i = 0; i < 4; i++) _y[0][i] = Pin(4 + i, $"1Y{i}", TerminalType.Output);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        for (var i = 0; i < 4; i++) _y[1][i] = Pin(12 - i, $"2Y{i}", TerminalType.Output);
        _b[1] = Pin(13, "2B", TerminalType.Input);
        _a[1] = Pin(14, "2A", TerminalType.Input);
        _enable[1] = Pin(15, "2G", TerminalType.Input);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _enable, .. _a, .. _b], [.. _y[0], .. _y[1]]);
    }

    public override string PartNumber => "74139";

    /// <summary>The two active-low enables, /1G and /2G.</summary>
    public IReadOnlyList<Terminal> Enable => _enable;

    /// <summary>The low select inputs, 1A and 2A.</summary>
    public IReadOnlyList<Terminal> SelectA => _a;

    /// <summary>The high select inputs, 1B and 2B.</summary>
    public IReadOnlyList<Terminal> SelectB => _b;

    /// <summary>One decoder's four outputs, Y0 to Y3: <c>Outputs(0)</c> is the first half.</summary>
    public IReadOnlyList<Terminal> Outputs(int half) => _y[half];

    /// <summary>The output each half is pulling low, or −1 while it is disabled.</summary>
    public (int First, int Second) Selected { get; private set; } = (-1, -1);

    public override string ValueLabel => $"{Show(Selected.First)} / {Show(Selected.Second)}";

    private static string Show(int selected) => selected < 0 ? "off" : $"Y{selected}";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        Span<int> chosen = stackalloc int[2];

        for (var half = 0; half < 2; half++)
        {
            var enabled = context.ReadInput(_enable[half], Levels).IsLow();
            var select = (context.ReadInput(_a[half], Levels).IsHigh() ? 1 : 0)
                         | (context.ReadInput(_b[half], Levels).IsHigh() ? 2 : 0);

            chosen[half] = enabled ? select : -1;

            for (var i = 0; i < 4; i++)
                Request(context, (half * 4) + i, enabled && i == select ? LogicState.Low : LogicState.High, delay);
        }

        var before = Selected;
        Selected = (chosen[0], chosen[1]);
        if (before != Selected) NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Selected = (-1, -1);
    }
}

/// <summary>
/// 74244 octal buffer with tri-state outputs, in two groups of four with an enable each — the part
/// that puts a set of switches, or an address, onto a shared bus only when asked.
/// <para>
/// It only goes one way, A to Y, which is the difference from the 74245. With /1G low, 1A1-1A4
/// appear on 1Y1-1Y4; with it high those four outputs let go of the bus. The second group is the
/// same with /2G. Enable a 244 onto a bus somebody else is driving and the two fight; the outputs
/// are marked as bus pins so the rule check does not object to the sharing itself.
/// </para>
/// <para>
/// Pinout: 1=/1G, 2=1A1, 3=2Y4, 4=1A2, 5=2Y3, 6=1A3, 7=2Y2, 8=1A4, 9=2Y1, 10=GND, 11=2A1,
/// 12=1Y4, 13=2A2, 14=1Y3, 15=2A3, 16=1Y2, 17=2A4, 18=1Y1, 19=/2G, 20=VCC.
/// </para>
/// </summary>
public sealed class Ic74244 : DigitalIc
{
    private readonly Terminal[] _a = new Terminal[8];
    private readonly Terminal[] _y = new Terminal[8];

    public Ic74244() : base(20)
    {
        PropagationDelay = 12e-9;

        var pins = new Terminal[21];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 20));

        // Index 0-3 is the first group (1A1-1A4), 4-7 the second (2A1-2A4).
        Enable1 = Pin(1, "1G", TerminalType.Input);
        _a[0] = Pin(2, "1A1", TerminalType.Input);
        _y[7] = Pin(3, "2Y4", TerminalType.Bidirectional);
        _a[1] = Pin(4, "1A2", TerminalType.Input);
        _y[6] = Pin(5, "2Y3", TerminalType.Bidirectional);
        _a[2] = Pin(6, "1A3", TerminalType.Input);
        _y[5] = Pin(7, "2Y2", TerminalType.Bidirectional);
        _a[3] = Pin(8, "1A4", TerminalType.Input);
        _y[4] = Pin(9, "2Y1", TerminalType.Bidirectional);
        Gnd = Pin(10, "GND", TerminalType.Ground);
        _a[4] = Pin(11, "2A1", TerminalType.Input);
        _y[3] = Pin(12, "1Y4", TerminalType.Bidirectional);
        _a[5] = Pin(13, "2A2", TerminalType.Input);
        _y[2] = Pin(14, "1Y3", TerminalType.Bidirectional);
        _a[6] = Pin(15, "2A3", TerminalType.Input);
        _y[1] = Pin(16, "1Y2", TerminalType.Bidirectional);
        _a[7] = Pin(17, "2A4", TerminalType.Input);
        _y[0] = Pin(18, "1Y1", TerminalType.Bidirectional);
        Enable2 = Pin(19, "2G", TerminalType.Input);
        Vcc = Pin(20, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Enable1, Enable2, .. _a], _y);
    }

    public override string PartNumber => "74244";

    /// <summary>/1G, active low: enables 1Y1-1Y4.</summary>
    public Terminal Enable1 { get; }

    /// <summary>/2G, active low: enables 2Y1-2Y4.</summary>
    public Terminal Enable2 { get; }

    /// <summary>The inputs: 1A1-1A4 then 2A1-2A4.</summary>
    public IReadOnlyList<Terminal> Inputs => _a;

    /// <summary>The outputs, in the same order as the inputs.</summary>
    public IReadOnlyList<Terminal> Outputs => _y;

    public override string ValueLabel => $"{(GroupEnabled.First ? "on" : "off")} / {(GroupEnabled.Second ? "on" : "off")}";

    /// <summary>Whether each group is driving its outputs.</summary>
    public (bool First, bool Second) GroupEnabled { get; private set; }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var first = context.ReadInput(Enable1, Levels).IsLow();
        var second = context.ReadInput(Enable2, Levels).IsLow();

        for (var i = 0; i < 8; i++)
        {
            var on = i < 4 ? first : second;
            var input = context.ReadInput(_a[i], Levels);
            Request(context, i, !on ? LogicState.HighImpedance : input.IsDriven() ? input : LogicState.Unknown, delay);
        }

        var before = GroupEnabled;
        GroupEnabled = (first, second);
        if (before != GroupEnabled) NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        GroupEnabled = (false, false);
    }
}

/// <summary>
/// Base for the octal registers with tri-state outputs: the 74374 and 74574 D flip-flops, which
/// take the inputs on the rising edge of CLK, and the 74573 transparent latch, which follows them
/// while LE is high. /OE releases the outputs without disturbing what is held, which is how a
/// register is put onto a bus and taken off it again.
/// </summary>
public abstract class OctalRegister : DigitalIc
{
    private const int Width = 8;

    private readonly Terminal[] _d = new Terminal[Width];
    private readonly Terminal[] _q = new Terminal[Width];

    private int _held;
    private LogicState _lastClock = LogicState.Unknown;

    /// <param name="flowThrough">The 574 pinout (inputs down one side, outputs up the other) rather than the 374's interleaved one.</param>
    /// <param name="edgeTriggered">A flip-flop clocked on CLK's rising edge rather than a latch open while LE is high.</param>
    protected OctalRegister(bool flowThrough, bool edgeTriggered) : base(20)
    {
        IsEdgeTriggered = edgeTriggered;
        PropagationDelay = 15e-9;

        var pins = new Terminal[21];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 20));

        OutputEnable = Pin(1, "OE", TerminalType.Input);

        if (flowThrough)
        {
            for (var i = 0; i < Width; i++)
            {
                _d[i] = Pin(2 + i, $"D{i}", TerminalType.Input);
                _q[i] = Pin(19 - i, $"Q{i}", TerminalType.Bidirectional);
            }
        }
        else
        {
            // The 374's pinout, which is the 373's: Q and D pairs interleaved down each side.
            int[] qPins = [2, 5, 6, 9, 12, 15, 16, 19];
            int[] dPins = [3, 4, 7, 8, 13, 14, 17, 18];

            for (var i = 0; i < Width; i++)
            {
                _q[i] = Pin(qPins[i], $"Q{i}", TerminalType.Bidirectional);
                _d[i] = Pin(dPins[i], $"D{i}", TerminalType.Input);
            }
        }

        Gnd = Pin(10, "GND", TerminalType.Ground);
        Strobe = Pin(11, edgeTriggered ? "CLK" : "LE", TerminalType.Input);
        Vcc = Pin(20, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([OutputEnable, Strobe, .. _d], _q);
    }

    /// <summary>Active low. High releases the outputs; what is held stays held.</summary>
    public Terminal OutputEnable { get; }

    /// <summary>CLK on the flip-flops (rising edge), LE on the latch (transparent while high).</summary>
    public Terminal Strobe { get; }

    /// <summary>True for the flip-flops, false for the latch.</summary>
    public bool IsEdgeTriggered { get; }

    /// <summary>The eight data inputs, D0 through D7.</summary>
    public IReadOnlyList<Terminal> Data => _d;

    /// <summary>The eight outputs, Q0 through Q7.</summary>
    public IReadOnlyList<Terminal> Outputs => _q;

    /// <summary>The byte it is holding.</summary>
    public int Value => _held;

    /// <summary>True while the outputs are released.</summary>
    public bool IsReleased { get; private set; }

    public override string ValueLabel => IsReleased ? $"0x{_held:X2} (off bus)" : $"0x{_held:X2}";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var strobe = context.ReadInput(Strobe, Levels);

        // A flip-flop takes the inputs on the rising edge and only then; nothing during the power-on
        // settling counts as an edge. A latch simply follows them while LE is high.
        var take = IsEdgeTriggered
            ? !context.IsInitializing && _lastClock.IsLow() && strobe.IsHigh()
            : strobe.IsHigh();
        _lastClock = strobe;

        var before = _held;

        if (take)
        {
            var value = 0;
            for (var i = 0; i < Width; i++)
                if (context.ReadInput(_d[i], Levels).IsHigh()) value |= 1 << i;

            _held = value;
        }

        var wasReleased = IsReleased;
        IsReleased = context.ReadInput(OutputEnable, Levels).IsHigh();

        for (var i = 0; i < Width; i++)
        {
            var bit = (_held & (1 << i)) != 0;
            Request(context, i, IsReleased ? LogicState.HighImpedance : bit ? LogicState.High : LogicState.Low, delay);
        }

        if (before != _held || wasReleased != IsReleased) NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _held = 0;
        _lastClock = LogicState.Unknown;
        IsReleased = false;
    }
}

/// <summary>
/// 74374 octal D flip-flop with tri-state outputs: eight bits taken on the rising edge of CLK, on the
/// 74373's interleaved pinout. Pinout: 1=/OE, 2=Q0, 3=D0, 4=D1, 5=Q1, 6=Q2, 7=D2, 8=D3, 9=Q3,
/// 10=GND, 11=CLK, 12=Q4, 13=D4, 14=D5, 15=Q5, 16=Q6, 17=D6, 18=D7, 19=Q7, 20=VCC.
/// </summary>
public sealed class Ic74374() : OctalRegister(flowThrough: false, edgeTriggered: true)
{
    public override string PartNumber => "74374";
}

/// <summary>
/// 74573 octal transparent latch with tri-state outputs: the 74373's logic on the flow-through
/// pinout, inputs down one side and outputs up the other, which is what makes it easy to lay out
/// between a bus and the thing it feeds. Pinout: 1=/OE, 2-9=D0-D7, 10=GND, 11=LE, 12-19=Q7-Q0, 20=VCC.
/// </summary>
public sealed class Ic74573() : OctalRegister(flowThrough: true, edgeTriggered: false)
{
    public override string PartNumber => "74573";
}

/// <summary>
/// 74574 octal D flip-flop with tri-state outputs, on the flow-through pinout: the 74374's logic
/// with inputs down one side and outputs up the other. Pinout: 1=/OE, 2-9=D0-D7, 10=GND, 11=CLK,
/// 12-19=Q7-Q0, 20=VCC.
/// </summary>
public sealed class Ic74574() : OctalRegister(flowThrough: true, edgeTriggered: true)
{
    public override string PartNumber => "74574";
}
