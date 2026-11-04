using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for the eight-stage parallel-in, serial-out shift registers: the 74165 and 74166, and the
/// CMOS 4014 and 4021. Load the eight inputs, then each rising clock moves every bit one stage on
/// towards the last, the serial input filling the first, and the last stage's bit appears on the
/// output — the eighth bit loaded first, the first bit loaded last.
/// <para>
/// What separates them is the load. The 74165 and 4021 load <b>asynchronously</b>, the moment the load
/// pin is asserted; the 74166 and 4014 load on the next clock edge, so a load and a shift can never
/// overlap. The 74165 and 74166 load on a low and have a clock inhibit; the CMOS parts load on a high,
/// and bring out stages 6, 7 and 8 rather than only the last.
/// </para>
/// </summary>
public abstract class ParallelInSerialOutIc : DigitalIc
{
    private const int Width = 8;

    private readonly Terminal[] _parallel = new Terminal[Width];
    private readonly (int Stage, bool Inverted)[] _outputs;
    private readonly bool _asyncLoad;
    private readonly bool _loadActiveHigh;
    private LogicState _lastClock = LogicState.Unknown;
    private int _register;

    /// <summary>Where each of the part's pins is: zero for one it does not have.</summary>
    protected internal sealed record Pinout(
        int[] Parallel, int Serial, int Clock, int Load, int Inhibit, int Clear,
        (int Pin, string Name, int Stage, bool Inverted)[] Outputs, int Vcc, int Gnd, int[] Unconnected);

    protected ParallelInSerialOutIc(Pinout pinout, bool asyncLoad, bool loadActiveHigh, bool cmos) : base(16)
    {
        _asyncLoad = asyncLoad;
        _loadActiveHigh = loadActiveHigh;
        Levels = cmos ? LogicLevels.Cmos5V : LogicLevels.Ttl;
        PropagationDelay = cmos ? 200e-9 : 20e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        for (var i = 0; i < Width; i++)
            _parallel[i] = Pin(pinout.Parallel[i], cmos ? $"P{i + 1}" : ((char)('A' + i)).ToString(), TerminalType.Input);

        SerialInput = Pin(pinout.Serial, "SER", TerminalType.Input);
        Clock = Pin(pinout.Clock, "CLK", TerminalType.Input);
        ShiftLoad = Pin(pinout.Load, cmos ? "P/S" : "SH/LD", TerminalType.Input);
        ClockInhibit = pinout.Inhibit > 0 ? Pin(pinout.Inhibit, "CLK INH", TerminalType.Input) : null;
        Clear = pinout.Clear > 0 ? Pin(pinout.Clear, "CLR", TerminalType.Input) : null;

        _outputs = [.. pinout.Outputs.Select(o => (o.Stage, o.Inverted))];
        SerialOutputs = [.. pinout.Outputs.Select(o => Pin(o.Pin, o.Name, TerminalType.Output))];

        Gnd = Pin(pinout.Gnd, cmos ? "VSS" : "GND", TerminalType.Ground);
        Vcc = Pin(pinout.Vcc, cmos ? "VDD" : "VCC", TerminalType.Power);
        foreach (var nc in pinout.Unconnected) Pin(nc, "NC", TerminalType.Passive);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [.. _parallel, ShiftLoad, Clock, SerialInput, .. new[] { ClockInhibit, Clear }.OfType<Terminal>()],
            SerialOutputs);
    }

    /// <summary>The eight parallel inputs, first stage first. The last is the first bit out.</summary>
    public IReadOnlyList<Terminal> ParallelInputs => _parallel;

    /// <summary>The load pin: SH/LD (low loads) on the TTL parts, P/S (high loads) on the CMOS ones.</summary>
    public Terminal ShiftLoad { get; }

    public Terminal Clock { get; }

    /// <summary>Active high: blocks the clock. Null on the CMOS parts.</summary>
    public Terminal? ClockInhibit { get; }

    /// <summary>The 74166's asynchronous clear, active low; null elsewhere.</summary>
    public Terminal? Clear { get; }

    /// <summary>Enters the first stage on each shift.</summary>
    public Terminal SerialInput { get; }

    /// <summary>The outputs the part brings out, in pin-table order.</summary>
    public IReadOnlyList<Terminal> SerialOutputs { get; }

    /// <summary>True for the 74165 and 4021, which load without waiting for the clock.</summary>
    public bool LoadsAsynchronously => _asyncLoad;

    /// <summary>Register contents, the first stage as bit 0.</summary>
    public int Value => _register;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var clock = context.ReadInput(Clock, Levels);
        var load = context.ReadInput(ShiftLoad, Levels).IsHigh() == _loadActiveHigh;
        var inhibited = ClockInhibit is not null && context.ReadInput(ClockInhibit, Levels).IsHigh();
        var edge = !inhibited && !context.IsInitializing && _lastClock.IsLow() && clock.IsHigh();

        int Parallel()
        {
            var value = 0;
            for (var i = 0; i < Width; i++)
                if (context.ReadInput(_parallel[i], Levels).IsHigh()) value |= 1 << i;
            return value;
        }

        if (Clear is not null && context.ReadInput(Clear, Levels).IsLow())
        {
            _register = 0;
        }
        else if (load && (_asyncLoad || edge))
        {
            _register = Parallel();
        }
        else if (!load && edge)
        {
            // One stage on towards the last, the serial input into the first.
            var incoming = context.ReadInput(SerialInput, Levels).IsHigh() ? 1 : 0;
            _register = ((_register << 1) | incoming) & 0xFF;
        }

        _lastClock = clock;

        var delay = DelayFor(context);
        for (var i = 0; i < _outputs.Length; i++)
        {
            var bit = LogicStateExtensions.FromBool((_register & (1 << _outputs[i].Stage)) != 0);
            context.Schedule(this, i, _outputs[i].Inverted ? bit.Invert() : bit, delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _register = 0;
        _lastClock = LogicState.Unknown;
    }
}

/// <summary>
/// 8-bit parallel-in, serial-out shift register.
/// <para>
/// Pinout: 1=SH/LD, 2=CLK, 3=E, 4=F, 5=G, 6=H, 7=QH', 8=GND,
/// 9=QH, 10=SER, 11=A, 12=B, 13=C, 14=D, 15=CLK INH, 16=VCC.
/// </para>
/// <para>
/// Taking SH/LD low loads the eight parallel inputs asynchronously; releasing it shifts towards QH
/// on each rising clock edge, so H comes out first and A last, with SER following. The clock-inhibit
/// pin gates the clock, which is what lets one of these be read out under the control of a slower
/// device.
/// </para>
/// </summary>
public sealed class Ic74165() : ParallelInSerialOutIc(
    new Pinout([11, 12, 13, 14, 3, 4, 5, 6], Serial: 10, Clock: 2, Load: 1, Inhibit: 15, Clear: 0,
        [(9, "QH", 7, false), (7, "QH'", 7, true)], Vcc: 16, Gnd: 8, []),
    asyncLoad: true, loadActiveHigh: false, cmos: false)
{
    public override string PartNumber => "74165";

    /// <summary>QH, the last stage.</summary>
    public Terminal Output => SerialOutputs[0];

    /// <summary>QH', its complement.</summary>
    public Terminal InvertedOutput => SerialOutputs[1];
}

/// <summary>
/// 74166 8-bit parallel-in, serial-out shift register with a synchronous load and a clear: SH/LD low
/// loads on the next rising clock rather than at once, CLR low empties it at once, and CLK INH high
/// blocks the clock for both loading and shifting.
/// <para>
/// Pinout: 1=SER, 2=A, 3=B, 4=C, 5=D, 6=CLK INH, 7=CLK, 8=GND, 9=CLR, 10=E, 11=F, 12=G, 13=QH, 14=H,
/// 15=SH/LD, 16=VCC.
/// </para>
/// </summary>
public sealed class Ic74166() : ParallelInSerialOutIc(
    new Pinout([2, 3, 4, 5, 10, 11, 12, 14], Serial: 1, Clock: 7, Load: 15, Inhibit: 6, Clear: 9,
        [(13, "QH", 7, false)], Vcc: 16, Gnd: 8, []),
    asyncLoad: false, loadActiveHigh: false, cmos: false)
{
    public override string PartNumber => "74166";

    /// <summary>QH, the last stage.</summary>
    public Terminal Output => SerialOutputs[0];
}

/// <summary>
/// 4014 8-stage static shift register, parallel or serial in, with stages 6, 7 and 8 brought out. P/S
/// high loads P1-P8 on the next rising clock (synchronously); low shifts, SER into stage 1.
/// <para>
/// Pinout: 1=P8, 2=Q6, 3=Q8, 4=P4, 5=P3, 6=P2, 7=P1, 8=VSS, 9=P/S, 10=CLK, 11=SER, 12=Q7, 13=P5,
/// 14=P6, 15=P7, 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4014() : ParallelInSerialOutIc(CmosPinout, asyncLoad: false, loadActiveHigh: true, cmos: true)
{
    internal static readonly Pinout CmosPinout = new(
        [7, 6, 5, 4, 13, 14, 15, 1], Serial: 11, Clock: 10, Load: 9, Inhibit: 0, Clear: 0,
        [(2, "Q6", 5, false), (12, "Q7", 6, false), (3, "Q8", 7, false)], Vcc: 16, Gnd: 8, []);

    public override string PartNumber => "4014";
}

/// <summary>
/// 4021 8-stage static shift register: the 4014 on the same pinout with an <b>asynchronous</b> load —
/// P/S high copies P1-P8 in at once. It is the shift register inside a NES controller.
/// </summary>
public sealed class Ic4021() : ParallelInSerialOutIc(Ic4014.CmosPinout, asyncLoad: true, loadActiveHigh: true, cmos: true)
{
    public override string PartNumber => "4021";
}

/// <summary>
/// Base for the universal shift registers, the 74194 (four bits) and the 74299 (eight, with its data
/// and outputs sharing tri-state pins). Two mode pins decide what each rising clock does: S1 S0 = 00
/// holds, 01 shifts right (towards the last stage, the right serial input into the first), 10 shifts
/// left (the left serial input into the last stage), and 11 loads the parallel inputs. CLR, active
/// low, empties it at once.
/// <para>
/// On the 74299 the eight I/O pins are the outputs while /G1 and /G2 are low and the part is not
/// loading, and the parallel inputs while S1 and S0 are both high, when they let go of the bus so
/// something else can drive the byte in. QA' and QH', the end stages, are always driven for chaining.
/// </para>
/// </summary>
public abstract class UniversalShiftRegisterIc : DigitalIc
{
    private readonly int _width;
    private readonly Terminal[] _data;
    private readonly Terminal[] _q;
    private readonly Terminal[] _enables;
    private readonly bool _sharedPins;
    private LogicState _lastClock = LogicState.Unknown;
    private int _register;

    protected UniversalShiftRegisterIc(int pinCount, int width, Terminal[] pinsByNumber,
        Terminal[] data, Terminal[] q, Terminal[] extraOutputs, Terminal[] enables,
        Terminal s0, Terminal s1, Terminal clock, Terminal clear, Terminal serialRight, Terminal serialLeft)
        : base(pinCount)
    {
        _width = width;
        _data = data;
        _q = q;
        _enables = enables;
        _sharedPins = ReferenceEquals(data[0], q[0]);
        S0 = s0;
        S1 = s1;
        Clock = clock;
        Clear = clear;
        SerialRight = serialRight;
        SerialLeft = serialLeft;
        ExtraOutputs = extraOutputs;

        PropagationDelay = 20e-9;
        Terminals = [.. pinsByNumber.Skip(1)];

        // On the 74299 every I/O pin is in both lists, as on the 74245: driven or released, and read.
        ConfigurePins(
            [S0, S1, Clock, Clear, SerialRight, SerialLeft, .. enables, .. (_sharedPins ? [] : data)],
            [.. q, .. extraOutputs]);

        if (_sharedPins) InputTerminals = [.. InputTerminals, .. data];
    }

    public Terminal S0 { get; }
    public Terminal S1 { get; }
    public Terminal Clock { get; }

    /// <summary>Asynchronous clear, active low.</summary>
    public Terminal Clear { get; }

    /// <summary>Into the first stage on a shift right.</summary>
    public Terminal SerialRight { get; }

    /// <summary>Into the last stage on a shift left.</summary>
    public Terminal SerialLeft { get; }

    /// <summary>The parallel inputs, first stage first (the I/O pins on the 74299).</summary>
    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>The stage outputs, first stage first (the I/O pins on the 74299).</summary>
    public IReadOnlyList<Terminal> Outputs => _q;

    /// <summary>The 74299's QA' and QH'; empty on the 74194.</summary>
    public IReadOnlyList<Terminal> ExtraOutputs { get; }

    /// <summary>Register contents, the first stage as bit 0.</summary>
    public int Value => _register;

    public override string ValueLabel => _width == 4 ? Convert.ToString(_register, 2).PadLeft(4, '0') : $"0x{_register:X2}";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        bool High(Terminal pin) => context.ReadInput(pin, Levels).IsHigh();

        var clock = context.ReadInput(Clock, Levels);
        var mode = (High(S1) ? 2 : 0) | (High(S0) ? 1 : 0);
        var edge = !context.IsInitializing && _lastClock.IsLow() && clock.IsHigh();
        var top = 1 << (_width - 1);
        var mask = (1 << _width) - 1;

        if (!High(Clear))
        {
            _register = 0;
        }
        else if (edge)
        {
            _register = mode switch
            {
                1 => ((_register << 1) | (High(SerialRight) ? 1 : 0)) & mask,
                2 => (_register >> 1) | (High(SerialLeft) ? top : 0),
                3 => Enumerable.Range(0, _width).Sum(i => High(_data[i]) ? 1 << i : 0),
                _ => _register,
            };
        }

        _lastClock = clock;

        // The 74299 lets go of its I/O pins to load through them, and while either enable is high.
        var released = _sharedPins && (mode == 3 || _enables.Any(High));

        for (var i = 0; i < _width; i++)
        {
            var bit = LogicStateExtensions.FromBool((_register & (1 << i)) != 0);
            Request(context, i, released ? LogicState.HighImpedance : bit, delay);
        }

        if (ExtraOutputs.Count == 2)
        {
            Request(context, _width, LogicStateExtensions.FromBool((_register & 1) != 0), delay);
            Request(context, _width + 1, LogicStateExtensions.FromBool((_register & top) != 0), delay);
        }

        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _register = 0;
        _lastClock = LogicState.Unknown;
    }
}

/// <summary>
/// 74194 4-bit bidirectional universal shift register: hold, shift right, shift left or load, by S1 S0.
/// <para>
/// Pinout: 1=CLR, 2=SR, 3=A, 4=B, 5=C, 6=D, 7=SL, 8=GND, 9=S0, 10=S1, 11=CLK, 12=QD, 13=QC, 14=QB,
/// 15=QA, 16=VCC.
/// </para>
/// </summary>
public sealed class Ic74194 : UniversalShiftRegisterIc
{
    public Ic74194() : this(Build())
    {
    }

    private Ic74194((Terminal[] Pins, Terminal[] Data, Terminal[] Q) p)
        : base(16, 4, p.Pins, p.Data, p.Q, [], [], s0: p.Pins[9], s1: p.Pins[10], clock: p.Pins[11],
            clear: p.Pins[1], serialRight: p.Pins[2], serialLeft: p.Pins[7])
    {
        Gnd = p.Pins[8];
        Vcc = p.Pins[16];
    }

    private static (Terminal[], Terminal[], Terminal[]) Build()
    {
        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        Pin(1, "CLR", TerminalType.Input);
        Pin(2, "SR", TerminalType.Input);
        Terminal[] data = [Pin(3, "A", TerminalType.Input), Pin(4, "B", TerminalType.Input),
            Pin(5, "C", TerminalType.Input), Pin(6, "D", TerminalType.Input)];
        Pin(7, "SL", TerminalType.Input);
        Pin(8, "GND", TerminalType.Ground);
        Pin(9, "S0", TerminalType.Input);
        Pin(10, "S1", TerminalType.Input);
        Pin(11, "CLK", TerminalType.Input);
        Terminal[] q = [Pin(15, "QA", TerminalType.Output), Pin(14, "QB", TerminalType.Output),
            Pin(13, "QC", TerminalType.Output), Pin(12, "QD", TerminalType.Output)];
        Pin(16, "VCC", TerminalType.Power);
        return (pins, data, q);
    }

    public override string PartNumber => "74194";
}

/// <summary>
/// 74299 8-bit universal shift/storage register with tri-state I/O: the 74194's four modes on eight
/// bits, the parallel data and the outputs sharing eight bus pins.
/// <para>
/// Pinout: 1=S0, 2=G1, 3=G2, 4=G/QG, 5=E/QE, 6=C/QC, 7=A/QA, 8=QA', 9=CLR, 10=GND, 11=SR, 12=CLK,
/// 13=B/QB, 14=D/QD, 15=F/QF, 16=H/QH, 17=QH', 18=SL, 19=S1, 20=VCC.
/// </para>
/// </summary>
public sealed class Ic74299 : UniversalShiftRegisterIc
{
    public Ic74299() : this(Build())
    {
    }

    private Ic74299((Terminal[] Pins, Terminal[] Io, Terminal[] Extra) p)
        : base(20, 8, p.Pins, p.Io, p.Io, p.Extra, [p.Pins[2], p.Pins[3]], s0: p.Pins[1], s1: p.Pins[19],
            clock: p.Pins[12], clear: p.Pins[9], serialRight: p.Pins[11], serialLeft: p.Pins[18])
    {
        Gnd = p.Pins[10];
        Vcc = p.Pins[20];
    }

    private static (Terminal[], Terminal[], Terminal[]) Build()
    {
        var pins = new Terminal[21];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 20));

        Pin(1, "S0", TerminalType.Input);
        Pin(2, "G1", TerminalType.Input);
        Pin(3, "G2", TerminalType.Input);
        var io = new Terminal[8];
        io[6] = Pin(4, "G/QG", TerminalType.Bidirectional);
        io[4] = Pin(5, "E/QE", TerminalType.Bidirectional);
        io[2] = Pin(6, "C/QC", TerminalType.Bidirectional);
        io[0] = Pin(7, "A/QA", TerminalType.Bidirectional);
        var qa = Pin(8, "QA'", TerminalType.Output);
        Pin(9, "CLR", TerminalType.Input);
        Pin(10, "GND", TerminalType.Ground);
        Pin(11, "SR", TerminalType.Input);
        Pin(12, "CLK", TerminalType.Input);
        io[1] = Pin(13, "B/QB", TerminalType.Bidirectional);
        io[3] = Pin(14, "D/QD", TerminalType.Bidirectional);
        io[5] = Pin(15, "F/QF", TerminalType.Bidirectional);
        io[7] = Pin(16, "H/QH", TerminalType.Bidirectional);
        var qh = Pin(17, "QH'", TerminalType.Output);
        Pin(18, "SL", TerminalType.Input);
        Pin(19, "S1", TerminalType.Input);
        Pin(20, "VCC", TerminalType.Power);
        return (pins, io, [qa, qh]);
    }

    public override string PartNumber => "74299";

    /// <summary>/G1 and /G2, both low to drive the I/O pins.</summary>
    public (Terminal G1, Terminal G2) OutputEnables => (Terminals[1], Terminals[2]);
}

/// <summary>
/// Base for the four-bit parallel-access shift registers with a J-K̄ serial input, the 74195 and the
/// CMOS 4035. Shifting moves each bit one stage on and sets the first from J and K̄: both low clears it,
/// both high sets it, J high with K̄ low toggles it, and J low with K̄ high keeps it. Tie J to K̄ for an
/// ordinary D input. Loading takes the four parallel inputs on the clock edge.
/// <para>
/// The 74195 loads while SH/LD is low and clears on a low; the 4035 loads while P/S is high, resets on
/// a high, and has a TRUE/COMPLEMENT pin that inverts all four outputs while it is low.
/// </para>
/// </summary>
public abstract class JkShiftRegisterIc : DigitalIc
{
    private readonly Terminal[] _p = new Terminal[4];
    private readonly Terminal[] _q = new Terminal[4];
    private readonly bool _cmos;
    private LogicState _lastClock = LogicState.Unknown;
    private int _register;

    /// <param name="pins">J, K', CLK, load, clear, T/C (0 on the 74195), Q̄4 (0 on the 4035), P1-P4, Q1-Q4, VCC, GND.</param>
    protected JkShiftRegisterIc(int[] pins, bool cmos) : base(16)
    {
        _cmos = cmos;
        Levels = cmos ? LogicLevels.Cmos5V : LogicLevels.Ttl;
        PropagationDelay = cmos ? 250e-9 : 20e-9;

        var all = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            all[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        J = Pin(pins[0], "J", TerminalType.Input);
        KBar = Pin(pins[1], "K'", TerminalType.Input);
        Clock = Pin(pins[2], "CLK", TerminalType.Input);
        Load = Pin(pins[3], cmos ? "P/S" : "SH/LD", TerminalType.Input);
        Clear = Pin(pins[4], cmos ? "RST" : "CLR", TerminalType.Input);
        TrueComplement = pins[5] > 0 ? Pin(pins[5], "T/C", TerminalType.Input) : null;
        for (var i = 0; i < 4; i++)
        {
            _p[i] = Pin(pins[7 + i], cmos ? $"P{i + 1}" : ((char)('A' + i)).ToString(), TerminalType.Input);
            _q[i] = Pin(pins[11 + i], cmos ? $"Q{i + 1}" : $"Q{(char)('A' + i)}", TerminalType.Output);
        }

        LastInverted = pins[6] > 0 ? Pin(pins[6], "QD'", TerminalType.Output) : null;
        Vcc = Pin(pins[15], cmos ? "VDD" : "VCC", TerminalType.Power);
        Gnd = Pin(pins[16], cmos ? "VSS" : "GND", TerminalType.Ground);

        Terminals = [.. all.Skip(1)];
        ConfigurePins([J, KBar, Clock, Load, Clear, .. new[] { TrueComplement }.OfType<Terminal>(), .. _p],
            [.. _q, .. new[] { LastInverted }.OfType<Terminal>()]);
    }

    public Terminal J { get; }

    /// <summary>K̄: low with J low clears the first stage.</summary>
    public Terminal KBar { get; }

    public Terminal Clock { get; }

    /// <summary>SH/LD (low loads) on the 74195, P/S (high loads) on the 4035.</summary>
    public Terminal Load { get; }

    /// <summary>CLR (active low) on the 74195, RESET (active high) on the 4035.</summary>
    public Terminal Clear { get; }

    /// <summary>The 4035's TRUE/COMPLEMENT: low inverts the outputs. Null on the 74195.</summary>
    public Terminal? TrueComplement { get; }

    /// <summary>The 74195's QD'. Null on the 4035.</summary>
    public Terminal? LastInverted { get; }

    public IReadOnlyList<Terminal> Parallel => _p;

    public IReadOnlyList<Terminal> Outputs => _q;

    /// <summary>Register contents, the first stage as bit 0.</summary>
    public int Value => _register;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        bool High(Terminal pin) => context.ReadInput(pin, Levels).IsHigh();

        var clock = context.ReadInput(Clock, Levels);
        var edge = !context.IsInitializing && _lastClock.IsLow() && clock.IsHigh();
        var cleared = High(Clear) == _cmos;
        var loading = High(Load) == _cmos;

        if (cleared)
        {
            _register = 0;
        }
        else if (edge && loading)
        {
            _register = Enumerable.Range(0, 4).Sum(i => High(_p[i]) ? 1 << i : 0);
        }
        else if (edge)
        {
            var first = (_register & 1) != 0;
            first = (High(J), High(KBar)) switch
            {
                (false, false) => false,
                (true, true) => true,
                (true, false) => !first,
                _ => first,
            };

            _register = ((_register << 1) & 0xE) | (first ? 1 : 0);
        }

        _lastClock = clock;

        var invert = TrueComplement is not null && !High(TrueComplement);
        for (var i = 0; i < 4; i++)
            Request(context, i, LogicStateExtensions.FromBool(((_register & (1 << i)) != 0) ^ invert), delay);

        if (LastInverted is not null)
            Request(context, 4, LogicStateExtensions.FromBool((_register & 8) == 0), delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _register = 0;
        _lastClock = LogicState.Unknown;
    }
}

/// <summary>
/// 74195 4-bit parallel-access shift register. Pinout: 1=CLR, 2=J, 3=K', 4=A, 5=B, 6=C, 7=D, 8=GND,
/// 9=SH/LD, 10=CLK, 11=QD', 12=QD, 13=QC, 14=QB, 15=QA, 16=VCC.
/// </summary>
public sealed class Ic74195() : JkShiftRegisterIc([2, 3, 10, 9, 1, 0, 11, 4, 5, 6, 7, 15, 14, 13, 12, 16, 8], cmos: false)
{
    public override string PartNumber => "74195";
}

/// <summary>
/// 4035 4-bit parallel-in/parallel-out shift register with J-K̄ serial input and true/complement
/// outputs. Pinout: 1=Q4, 2=Q3, 3=Q2, 4=Q1, 5=T/C, 6=RST, 7=P/S, 8=VSS, 9=CLK, 10=K', 11=J, 12=P1,
/// 13=P2, 14=P3, 15=P4, 16=VDD.
/// </summary>
public sealed class Ic4035() : JkShiftRegisterIc([11, 10, 9, 7, 6, 5, 0, 12, 13, 14, 15, 4, 3, 2, 1, 16, 8], cmos: true)
{
    public override string PartNumber => "4035";
}

/// <summary>
/// 4015 dual 4-stage serial-in, parallel-out shift register: two independent four-bit shift registers,
/// each with its own clock, data input and reset (active high). Rising edges move the data along.
/// <para>
/// Pinout: 1=2CLK, 2=2Q4, 3=1Q3, 4=1Q2, 5=1Q1, 6=1RST, 7=1D, 8=VSS, 9=1CLK, 10=1Q4, 11=2Q3, 12=2Q2,
/// 13=2Q1, 14=2RST, 15=2D, 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4015 : DigitalIc
{
    private readonly Terminal[] _clock = new Terminal[2];
    private readonly Terminal[] _data = new Terminal[2];
    private readonly Terminal[] _reset = new Terminal[2];
    private readonly Terminal[][] _q = [new Terminal[4], new Terminal[4]];
    private readonly int[] _register = new int[2];
    private readonly LogicState[] _lastClock = [LogicState.Unknown, LogicState.Unknown];

    public Ic4015() : base(16)
    {
        PropagationDelay = 200e-9;
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        _clock[1] = Pin(1, "2CLK", TerminalType.Input);
        _q[1][3] = Pin(2, "2Q4", TerminalType.Output);
        _q[0][2] = Pin(3, "1Q3", TerminalType.Output);
        _q[0][1] = Pin(4, "1Q2", TerminalType.Output);
        _q[0][0] = Pin(5, "1Q1", TerminalType.Output);
        _reset[0] = Pin(6, "1RST", TerminalType.Input);
        _data[0] = Pin(7, "1D", TerminalType.Input);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        _clock[0] = Pin(9, "1CLK", TerminalType.Input);
        _q[0][3] = Pin(10, "1Q4", TerminalType.Output);
        _q[1][2] = Pin(11, "2Q3", TerminalType.Output);
        _q[1][1] = Pin(12, "2Q2", TerminalType.Output);
        _q[1][0] = Pin(13, "2Q1", TerminalType.Output);
        _reset[1] = Pin(14, "2RST", TerminalType.Input);
        _data[1] = Pin(15, "2D", TerminalType.Input);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _clock, .. _data, .. _reset], [.. _q[0], .. _q[1]]);
    }

    public override string PartNumber => "4015";

    public Terminal Clock(int half) => _clock[half];

    public Terminal Data(int half) => _data[half];

    /// <summary>Active high.</summary>
    public Terminal Reset(int half) => _reset[half];

    /// <summary>Q1 to Q4 of one half.</summary>
    public IReadOnlyList<Terminal> Outputs(int half) => _q[half];

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        for (var half = 0; half < 2; half++)
        {
            var clock = context.ReadInput(_clock[half], Levels);

            if (context.ReadInput(_reset[half], Levels).IsHigh())
                _register[half] = 0;
            else if (!context.IsInitializing && _lastClock[half].IsLow() && clock.IsHigh())
                _register[half] = ((_register[half] << 1) | (context.ReadInput(_data[half], Levels).IsHigh() ? 1 : 0)) & 0xF;

            _lastClock[half] = clock;

            for (var i = 0; i < 4; i++)
                Request(context, (half * 4) + i, LogicStateExtensions.FromBool((_register[half] & (1 << i)) != 0), delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_register);
        Array.Fill(_lastClock, LogicState.Unknown);
    }
}

/// <summary>
/// 4094 8-stage shift-and-store bus register: the CMOS forerunner of the 74595. Rising clocks shift
/// DATA into stage 1; while STROBE is high the storage latches follow the shift register, and when it
/// falls they hold, so the outputs change only when asked. OUTPUT ENABLE high drives Q1-Q8; low
/// releases them (tri-state) without touching what is stored. QS is stage 8 for chaining, changing on
/// the rising clock; Q'S is the same bit a half clock later, on the falling edge, for chaining across a
/// slow clock.
/// <para>
/// Pinout: 1=STROBE, 2=DATA, 3=CLK, 4=Q1, 5=Q2, 6=Q3, 7=Q4, 8=VSS, 9=QS, 10=Q'S, 11=Q8, 12=Q7, 13=Q6,
/// 14=Q5, 15=OE, 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4094 : DigitalIc
{
    private readonly Terminal[] _q = new Terminal[8];
    private LogicState _lastClock = LogicState.Unknown;
    private int _register;
    private int _latched;
    private bool _delayedBit;

    public Ic4094() : base(16)
    {
        PropagationDelay = 200e-9;
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        Strobe = Pin(1, "STR", TerminalType.Input);
        Data = Pin(2, "DATA", TerminalType.Input);
        Clock = Pin(3, "CLK", TerminalType.Input);
        for (var i = 0; i < 4; i++) _q[i] = Pin(4 + i, $"Q{i + 1}", TerminalType.Bidirectional);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        SerialOut = Pin(9, "QS", TerminalType.Output);
        SerialOutDelayed = Pin(10, "Q'S", TerminalType.Output);
        for (var i = 4; i < 8; i++) _q[i] = Pin(18 - i, $"Q{i + 1}", TerminalType.Bidirectional);
        OutputEnable = Pin(15, "OE", TerminalType.Input);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Strobe, Data, Clock, OutputEnable], [.. _q, SerialOut, SerialOutDelayed]);
    }

    public override string PartNumber => "4094";

    /// <summary>High: the outputs' latches follow the shift register.</summary>
    public Terminal Strobe { get; }

    public Terminal Data { get; }

    public Terminal Clock { get; }

    /// <summary>Active high: drives Q1-Q8. Low releases them.</summary>
    public Terminal OutputEnable { get; }

    /// <summary>Q1 to Q8.</summary>
    public IReadOnlyList<Terminal> Outputs => _q;

    /// <summary>Stage 8, for the next one in a chain.</summary>
    public Terminal SerialOut { get; }

    /// <summary>Stage 8 a half clock later.</summary>
    public Terminal SerialOutDelayed { get; }

    /// <summary>What is in the shift register, stage 1 as bit 0.</summary>
    public int ShiftRegister => _register;

    /// <summary>What the storage latches hold.</summary>
    public int Latched => _latched;

    public override string ValueLabel => $"0x{_latched:X2}";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var clock = context.ReadInput(Clock, Levels);

        if (!context.IsInitializing && _lastClock.IsLow() && clock.IsHigh())
            _register = ((_register << 1) | (context.ReadInput(Data, Levels).IsHigh() ? 1 : 0)) & 0xFF;

        if (!context.IsInitializing && _lastClock.IsHigh() && clock.IsLow())
            _delayedBit = (_register & 0x80) != 0;

        _lastClock = clock;

        var before = _latched;
        if (context.ReadInput(Strobe, Levels).IsHigh()) _latched = _register;

        var enabled = context.ReadInput(OutputEnable, Levels).IsHigh();
        for (var i = 0; i < 8; i++)
        {
            var bit = LogicStateExtensions.FromBool((_latched & (1 << i)) != 0);
            Request(context, i, enabled ? bit : LogicState.HighImpedance, delay);
        }

        Request(context, 8, LogicStateExtensions.FromBool((_register & 0x80) != 0), delay);
        Request(context, 9, LogicStateExtensions.FromBool(_delayedBit), delay);

        if (before != _latched) NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _register = 0;
        _latched = 0;
        _delayedBit = false;
        _lastClock = LogicState.Unknown;
    }
}
