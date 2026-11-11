using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>A 74-series part built from a pin table: its pins by number, with names, and its own evaluation.</summary>
public abstract class PinTableIc : DigitalIc
{
    private readonly Dictionary<string, Terminal> _byName = new(StringComparer.Ordinal);

    protected PinTableIc(int pinCount, (int Pin, string Name, TerminalType Type)[] table) : base(pinCount)
    {
        PropagationDelay = 20e-9;
        var pins = new Terminal[pinCount + 1];
        foreach (var (pin, name, type) in table)
        {
            var terminal = new Terminal($"p{pin}", name, type, DipPackage.PinOffset(pin, pinCount));
            pins[pin] = terminal;
            _byName[name] = terminal;
            if (type == TerminalType.Power) Vcc = terminal;
            if (type == TerminalType.Ground) Gnd = terminal;
        }

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. table.Where(t => t.Type == TerminalType.Input).Select(t => _byName[t.Name])],
            [.. table.Where(t => t.Type == TerminalType.Output).Select(t => _byName[t.Name])]);
        OutputNames = [.. table.Where(t => t.Type == TerminalType.Output).Select(t => t.Name)];
    }

    private string[] OutputNames { get; }

    public override bool LabelsPins => true;

    /// <summary>A pin by its datasheet name.</summary>
    public Terminal Pin(string name) => _byName[name];

    protected bool High(IDigitalContext context, string name) => context.ReadInput(_byName[name], Levels).IsHigh();

    protected void Drive(IDigitalContext context, string output, bool high) =>
        context.Schedule(this, Array.IndexOf(OutputNames, output), LogicStateExtensions.FromBool(high), DelayFor(context));
}

/// <summary>
/// The 74148 8-line to 3-line priority encoder, every input and output active low: the highest-numbered input held
/// low comes out on A2-A0, inverted; GS goes low while any input is, EO while none is (to enable the next encoder
/// down a chain), and EI high turns the whole thing off.
/// </summary>
public sealed class Ic74148() : PinTableIc(16,
[
    (1, "4", TerminalType.Input), (2, "5", TerminalType.Input), (3, "6", TerminalType.Input), (4, "7", TerminalType.Input),
    (5, "EI", TerminalType.Input), (6, "A2", TerminalType.Output), (7, "A1", TerminalType.Output), (8, "GND", TerminalType.Ground),
    (9, "A0", TerminalType.Output), (10, "0", TerminalType.Input), (11, "1", TerminalType.Input), (12, "2", TerminalType.Input),
    (13, "3", TerminalType.Input), (14, "GS", TerminalType.Output), (15, "EO", TerminalType.Output), (16, "VCC", TerminalType.Power),
])
{
    public override string PartNumber => "74148";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var enabled = !High(context, "EI");
        var highest = -1;
        for (var i = 7; i >= 0 && highest < 0; i--)
            if (!High(context, i.ToString(System.Globalization.CultureInfo.InvariantCulture))) highest = i;
        var code = enabled && highest >= 0 ? ~highest & 7 : 7;
        Drive(context, "A0", (code & 1) != 0);
        Drive(context, "A1", (code & 2) != 0);
        Drive(context, "A2", (code & 4) != 0);
        Drive(context, "GS", !(enabled && highest >= 0));
        Drive(context, "EO", !(enabled && highest < 0));
    }
}

/// <summary>
/// The 74688 8-bit identity comparator: P=Q goes low while /G is low and the P and Q bytes match, which is the address
/// decoder of a great many 8-bit boards — the Q side set with DIP switches, the P side on the address bus.
/// </summary>
public sealed class Ic74688() : PinTableIc(20,
[
    (1, "G", TerminalType.Input), (2, "P0", TerminalType.Input), (3, "Q0", TerminalType.Input), (4, "P1", TerminalType.Input),
    (5, "Q1", TerminalType.Input), (6, "P2", TerminalType.Input), (7, "Q2", TerminalType.Input), (8, "P3", TerminalType.Input),
    (9, "Q3", TerminalType.Input), (10, "GND", TerminalType.Ground), (11, "P4", TerminalType.Input), (12, "Q4", TerminalType.Input),
    (13, "P5", TerminalType.Input), (14, "Q5", TerminalType.Input), (15, "P6", TerminalType.Input), (16, "Q6", TerminalType.Input),
    (17, "P7", TerminalType.Input), (18, "Q7", TerminalType.Input), (19, "P=Q", TerminalType.Output), (20, "VCC", TerminalType.Power),
])
{
    public override string PartNumber => "74688";

    public override string PinLabel(Terminal terminal) =>
        terminal.Name is "G" or "P=Q" ? "/" + terminal.Name : base.PinLabel(terminal);

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var equal = !High(context, "G");
        for (var i = 0; i < 8 && equal; i++) equal = High(context, $"P{i}") == High(context, $"Q{i}");
        Drive(context, "P=Q", !equal);
    }
}

/// <summary>
/// The 74259 8-bit addressable latch: D is written to the output S2-S0 picks while /E is low, the others holding;
/// /E high holds them all. With /CLR low, /E low makes it a 1-of-8 demultiplexer (the picked output follows D, the
/// rest low) and /E high clears every output.
/// </summary>
public sealed class Ic74259() : PinTableIc(16,
[
    (1, "S0", TerminalType.Input), (2, "S1", TerminalType.Input), (3, "S2", TerminalType.Input), (4, "Q0", TerminalType.Output),
    (5, "Q1", TerminalType.Output), (6, "Q2", TerminalType.Output), (7, "Q3", TerminalType.Output), (8, "GND", TerminalType.Ground),
    (9, "Q4", TerminalType.Output), (10, "Q5", TerminalType.Output), (11, "Q6", TerminalType.Output), (12, "Q7", TerminalType.Output),
    (13, "D", TerminalType.Input), (14, "E", TerminalType.Input), (15, "CLR", TerminalType.Input), (16, "VCC", TerminalType.Power),
])
{
    private int _latched;

    public override string PartNumber => "74259";

    /// <summary>The eight outputs, Q0 the lowest bit.</summary>
    public int Latched => _latched;

    public override string ValueLabel => $"0x{_latched:X2}";

    public override string PinLabel(Terminal terminal) =>
        terminal.Name is "E" or "CLR" ? "/" + terminal.Name : base.PinLabel(terminal);

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var address = (High(context, "S0") ? 1 : 0) | (High(context, "S1") ? 2 : 0) | (High(context, "S2") ? 4 : 0);
        var d = High(context, "D");
        var enabled = !High(context, "E");
        if (High(context, "CLR"))
        {
            if (enabled) _latched = d ? _latched | (1 << address) : _latched & ~(1 << address);
        }
        else _latched = enabled && d ? 1 << address : 0;

        for (var i = 0; i < 8; i++) Drive(context, $"Q{i}", (_latched & (1 << i)) != 0);
        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _latched = 0;
    }
}

/// <summary>
/// The 74190 (decade) and 74191 (binary) synchronous up/down counters with one clock: a rising CLK counts while /CTEN
/// is low, up while D/U is low and down while it's high; /LOAD low copies A-D to the outputs at once. MAX/MIN goes high
/// at the end of the count in the direction it's going (9 or 15 up, 0 down), and /RCO goes low with it while CLK is
/// low, to clock the next stage.
/// </summary>
public abstract class SingleClockUpDownCounterIc() : PinTableIc(16,
[
    (1, "B", TerminalType.Input), (2, "QB", TerminalType.Output), (3, "QA", TerminalType.Output), (4, "CTEN", TerminalType.Input),
    (5, "D/U", TerminalType.Input), (6, "QC", TerminalType.Output), (7, "QD", TerminalType.Output), (8, "GND", TerminalType.Ground),
    (9, "D", TerminalType.Input), (10, "C", TerminalType.Input), (11, "LOAD", TerminalType.Input), (12, "MAX/MIN", TerminalType.Output),
    (13, "RCO", TerminalType.Output), (14, "CLK", TerminalType.Input), (15, "A", TerminalType.Input), (16, "VCC", TerminalType.Power),
])
{
    private int _count;
    private bool _lastClock = true;

    public int Count => _count;

    protected abstract int Maximum { get; }

    public override string ValueLabel => $"{_count}";

    public override string PinLabel(Terminal terminal) =>
        terminal.Name is "CTEN" or "LOAD" or "RCO" ? "/" + terminal.Name : base.PinLabel(terminal);

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var clock = High(context, "CLK");
        var down = High(context, "D/U");
        var counting = !High(context, "CTEN");
        if (!High(context, "LOAD"))
            _count = (High(context, "A") ? 1 : 0) | (High(context, "B") ? 2 : 0) | (High(context, "C") ? 4 : 0) | (High(context, "D") ? 8 : 0);
        else if (!context.IsInitializing && clock && !_lastClock && counting)
            _count = down ? (_count == 0 ? Maximum : Math.Min(_count, Maximum + 1) - 1) : (_count >= Maximum ? 0 : _count + 1);
        _lastClock = clock;

        var end = down ? _count == 0 : _count == Maximum;
        Drive(context, "QA", (_count & 1) != 0);
        Drive(context, "QB", (_count & 2) != 0);
        Drive(context, "QC", (_count & 4) != 0);
        Drive(context, "QD", (_count & 8) != 0);
        Drive(context, "MAX/MIN", end);
        Drive(context, "RCO", !(end && counting && !clock));
        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        (_count, _lastClock) = (0, true);
    }
}

public sealed class Ic74190 : SingleClockUpDownCounterIc
{
    public override string PartNumber => "74190";

    protected override int Maximum => 9;
}

public sealed class Ic74191 : SingleClockUpDownCounterIc
{
    public override string PartNumber => "74191";

    protected override int Maximum => 15;
}

/// <summary>
/// The 74279 quad /S-/R latch: each Q set by a low /S (latches 1 and 3 have two, either sets it) and reset by a low /R;
/// both low holds Q high, as the datasheet says, until one lets go. The switch debouncer of a thousand front panels.
/// </summary>
public sealed class Ic74279() : PinTableIc(16,
[
    (1, "1R", TerminalType.Input), (2, "1S1", TerminalType.Input), (3, "1S2", TerminalType.Input), (4, "1Q", TerminalType.Output),
    (5, "2R", TerminalType.Input), (6, "2S", TerminalType.Input), (7, "2Q", TerminalType.Output), (8, "GND", TerminalType.Ground),
    (9, "3Q", TerminalType.Output), (10, "3R", TerminalType.Input), (11, "3S1", TerminalType.Input), (12, "3S2", TerminalType.Input),
    (13, "4Q", TerminalType.Output), (14, "4R", TerminalType.Input), (15, "4S", TerminalType.Input), (16, "VCC", TerminalType.Power),
])
{
    private static readonly (string R, string[] S, string Q)[] Latches =
        [("1R", ["1S1", "1S2"], "1Q"), ("2R", ["2S"], "2Q"), ("3R", ["3S1", "3S2"], "3Q"), ("4R", ["4S"], "4Q")];

    private readonly bool[] _q = new bool[4];

    public override string PartNumber => "74279";

    public bool Q(int latch) => _q[latch];

    public override string PinLabel(Terminal terminal) =>
        terminal.Name.Contains('R', StringComparison.Ordinal) || terminal.Name.Contains('S', StringComparison.Ordinal) ? "/" + terminal.Name : base.PinLabel(terminal);

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        for (var i = 0; i < 4; i++)
        {
            var (r, s, q) = Latches[i];
            var set = s.Any(name => !High(context, name));
            if (set) _q[i] = true;
            else if (!High(context, r)) _q[i] = false;
            Drive(context, q, _q[i]);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_q);
    }
}
