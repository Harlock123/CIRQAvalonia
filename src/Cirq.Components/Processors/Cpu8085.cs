using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>One machine cycle an 8085 ran on its bus: when it ended, where, what, and what kind it was.</summary>
/// <param name="Time">When the byte was taken (or the cycle ended), in seconds.</param>
/// <param name="Address">The address: the port (on both halves) for I/O, PC for an acknowledge.</param>
/// <param name="Data">The byte read or written; the byte the device gave in an INTR acknowledge.</param>
/// <param name="Kind">The machine cycle, as IO/M, S1, S0 and the strobes say; <see cref="I8085CycleKind.Halt"/> and <see cref="I8085CycleKind.Hold"/> mark where a halt or a hold began.</param>
/// <param name="WaitStates">Wait states READY added to it.</param>
/// <param name="First">An opcode fetch that starts an instruction.</param>
public readonly record struct I8085BusCycle(double Time, ushort Address, byte Data, I8085CycleKind Kind, int WaitStates, bool First);

/// <summary>
/// The Intel 8085A (8085AH), on its DIP-40 pinout: the T-states of <see cref="I8085Core"/> put on
/// the pins, a T-state per CLK OUT cycle, CLK OUT being X1 divided by two.
/// <para>
/// <b>The clock.</b> X1 takes a clock at twice the processor's (an oscillator module in place of the
/// crystal across X1 and X2; X2 is left open), and every rising edge on X1 turns CLK OUT over. Each
/// T-state starts as CLK OUT falls. In T1 the status lines IO/M, S1 and S0 change for the machine
/// cycle, A8-A15 and AD0-AD7 carry the address, and ALE is high for CLK OUT's low half — a 74373 (or
/// an 8155's own latch) keeps the low byte as ALE falls. From T2 AD0-AD7 is the data bus: /RD (or
/// /INTA) low with the bus let go for the device, or the processor's byte with /WR low. The byte is
/// taken, and /RD or /WR raised, as CLK OUT rises in T3; a write's byte stays on until the next T1.
/// The changes come <see cref="DigitalComponent.PropagationDelay"/> after X1's edge.
/// </para>
/// <para>
/// <b>READY</b> low as CLK OUT rises in T2 (or a wait state) adds a wait state. <b>HOLD</b> is
/// latched as CLK OUT rises in T2 (or T4 of a six-state fetch, or any HALT state): HLDA goes high
/// for the next T-state, then the address and data buses, /RD, /WR and IO/M float until HOLD is let
/// go. <b>TRAP</b>, <b>RST 7.5</b>, <b>RST 6.5</b>, <b>RST 5.5</b> and <b>INTR</b> are sampled as CLK OUT
/// rises in the next-to-last T-state of an instruction (RST 7.5 and TRAP latch their rising edges
/// whenever they come); INTR is answered with /INTA in place of /RD. <b>SID</b> is read by RIM,
/// <b>SOD</b> set by SIM. <b>RESET IN</b> low floats the bus and drives <b>RESET OUT</b> high; let go,
/// the processor starts at $0000. None of the inputs is pulled up inside: tie READY high and the
/// unused interrupts and HOLD low.
/// </para>
/// <para>
/// Pinout: 1 = X1, 2 = X2, 3 = RESET OUT, 4 = SOD, 5 = SID, 6 = TRAP, 7 = RST 7.5, 8 = RST 6.5,
/// 9 = RST 5.5, 10 = INTR, 11 = /INTA, 12-19 = AD0-AD7, 20 = VSS, 21-28 = A8-A15, 29 = S0, 30 = ALE,
/// 31 = /WR, 32 = /RD, 33 = S1, 34 = IO/M, 35 = READY, 36 = /RESET IN, 37 = CLK OUT, 38 = HLDA,
/// 39 = HOLD, 40 = VCC.
/// </para>
/// </summary>
public sealed class Cpu8085 : DigitalIc, IRunState
{
    /// <summary>How many machine cycles the bus log keeps, newest last.</summary>
    public const int BusLogCapacity = 1 << 16;

    // Outputs: AD0-AD7 = 0-7, A8-A15 = 8-15, then the controls.
    private const int AleIndex = 16, RdIndex = 17, WrIndex = 18, IoMIndex = 19, S0Index = 20, S1Index = 21, IntaIndex = 22,
        HldaIndex = 23, ResetOutIndex = 24, SodIndex = 25, ClockOutIndex = 26;

    private readonly Terminal[] _ad = new Terminal[8];
    private readonly Terminal[] _high = new Terminal[8];
    private readonly I8085BusCycle[] _log = new I8085BusCycle[BusLogCapacity];
    private int _logStart;
    private int _logCount;

    private LogicState _lastX1 = LogicState.Unknown;
    private bool _clockHigh = true;
    private byte _captured;
    private int _cycleWaits;
    private bool _wasHalted;
    private bool _wasHolding;

    public Cpu8085() : base(40)
    {
        PropagationDelay = 40e-9;

        var pins = new Terminal[41];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 40));

        X1 = Pin(1, "X1", TerminalType.Input);
        X2 = Pin(2, "X2", TerminalType.Input);
        var resetOut = Pin(3, "RESETOUT", TerminalType.Output);
        var sod = Pin(4, "SOD", TerminalType.Output);
        Sid = Pin(5, "SID", TerminalType.Input);
        Trap = Pin(6, "TRAP", TerminalType.Input);
        Rst75 = Pin(7, "RST7.5", TerminalType.Input);
        Rst65 = Pin(8, "RST6.5", TerminalType.Input);
        Rst55 = Pin(9, "RST5.5", TerminalType.Input);
        Intr = Pin(10, "INTR", TerminalType.Input);
        var inta = Pin(11, "INTA", TerminalType.Output);
        for (var i = 0; i < 8; i++) _ad[i] = Pin(12 + i, $"AD{i}", TerminalType.Bidirectional);
        Gnd = Pin(20, "VSS", TerminalType.Ground);
        for (var i = 0; i < 8; i++) _high[i] = Pin(21 + i, $"A{8 + i}", TerminalType.Output);
        var s0 = Pin(29, "S0", TerminalType.Output);
        var ale = Pin(30, "ALE", TerminalType.Output);
        var wr = Pin(31, "WR", TerminalType.Output);
        var rd = Pin(32, "RD", TerminalType.Output);
        var s1 = Pin(33, "S1", TerminalType.Output);
        var iom = Pin(34, "IO/M", TerminalType.Output);
        Ready = Pin(35, "READY", TerminalType.Input);
        ResetIn = Pin(36, "RESETIN", TerminalType.Input);
        var clockOut = Pin(37, "CLKOUT", TerminalType.Output);
        var hlda = Pin(38, "HLDA", TerminalType.Output);
        Hold = Pin(39, "HOLD", TerminalType.Input);
        Vcc = Pin(40, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([X1, X2, Sid, Trap, Rst75, Rst65, Rst55, Intr, Ready, ResetIn, Hold, .. _ad],
            [.. _ad, .. _high, ale, rd, wr, iom, s0, s1, inta, hlda, resetOut, sod, clockOut]);
    }

    public override string PartNumber => "8085";

    public override string ComponentType => "8085 CPU";

    public override bool LabelsPins => true;

    /// <summary>The processor inside: registers, flags and the T-state in progress.</summary>
    public I8085Core Core { get; private set; } = new();

    /// <summary>AD0-AD7: the address's low byte in T1, then the data bus.</summary>
    public IReadOnlyList<Terminal> AddressData => _ad;

    /// <summary>A8-A15.</summary>
    public IReadOnlyList<Terminal> AddressHigh => _high;

    /// <summary>X1: a clock at twice the processor's, two edges to a CLK OUT cycle.</summary>
    public Terminal X1 { get; }

    /// <summary>X2: the crystal's other end. Leave it open when X1 is driven.</summary>
    public Terminal X2 { get; }

    public Terminal Sid { get; }

    public Terminal Trap { get; }

    public Terminal Rst75 { get; }

    public Terminal Rst65 { get; }

    public Terminal Rst55 { get; }

    public Terminal Intr { get; }

    public Terminal Ready { get; }

    /// <summary>/RESET IN: low holds the processor in reset.</summary>
    public Terminal ResetIn { get; }

    public Terminal Hold { get; }

    public Terminal AddressLatchEnable => OutputTerminals[AleIndex];

    public Terminal ReadStrobe => OutputTerminals[RdIndex];

    public Terminal WriteStrobe => OutputTerminals[WrIndex];

    /// <summary>IO/M: high for an I/O cycle (or an acknowledge), low for memory.</summary>
    public Terminal IoMemory => OutputTerminals[IoMIndex];

    public Terminal S0 => OutputTerminals[S0Index];

    public Terminal S1 => OutputTerminals[S1Index];

    /// <summary>/INTA: low in place of /RD in an INTR acknowledge.</summary>
    public Terminal InterruptAcknowledge => OutputTerminals[IntaIndex];

    public Terminal HoldAcknowledge => OutputTerminals[HldaIndex];

    public Terminal ResetOut => OutputTerminals[ResetOutIndex];

    public Terminal Sod => OutputTerminals[SodIndex];

    /// <summary>CLK OUT: X1 ÷ 2, a cycle per T-state, for the rest of the system.</summary>
    public Terminal ClockOut => OutputTerminals[ClockOutIndex];

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "INTA" or "WR" or "RD" => "/" + pin.Name,
        "RESETIN" => "/RESET IN",
        "RESETOUT" => "RESET OUT",
        "CLKOUT" => "CLK OUT",
        "RST7.5" => "RST 7.5",
        "RST6.5" => "RST 6.5",
        "RST5.5" => "RST 5.5",
        "VCC" => "+5V",
        _ => pin.Name,
    };

    public override string ValueLabel => Core.ResetAsserted
        ? "reset"
        : Core.Halted
            ? $"HLT at ${Core.PC - 1:X4}"
            : Core.Kind == I8085CycleKind.Hold
                ? "hold"
                : $"PC ${Core.InstructionAddress:X4} A ${Core.A:X2}";

    /// <summary>The machine cycles performed, oldest first, up to <see cref="BusLogCapacity"/> of the most recent.</summary>
    public IReadOnlyList<I8085BusCycle> BusLog
    {
        get
        {
            var list = new I8085BusCycle[_logCount];
            for (var i = 0; i < _logCount; i++) list[i] = _log[(_logStart + i) % BusLogCapacity];
            return list;
        }
    }

    /// <summary>Read cycles (memory, I/O or acknowledge) that found a data line at no logic level.</summary>
    public int UndefinedReads { get; private set; }

    /// <summary>The address of the first read that found the data bus undefined, or −1.</summary>
    public int FirstUndefinedRead { get; private set; } = -1;

    /// <summary>What is wrong with how this part is being run, if anything.</summary>
    public IReadOnlyList<string> Violations => UndefinedReads > 0
        ?
        [
            $"{UndefinedReads} read(s) found a data line at no logic level, first at ${FirstUndefinedRead:X4} — " +
            "two outputs fighting on AD0-AD7, nothing selected there at all, or a device still driving when the address went out",
        ]
        : [];

    /// <summary>The latest instruction fetched, disassembled from the bus.</summary>
    public string CurrentInstruction
    {
        get
        {
            for (var i = _logCount - 1; i >= 0; i--)
            {
                var cycle = _log[(_logStart + i) % BusLogCapacity];
                if (cycle.Kind != I8085CycleKind.OpcodeFetch || !cycle.First) continue;

                var window = Enumerable.Range(i, Math.Min(8, _logCount - i)).Select(k => _log[(_logStart + k) % BusLogCapacity]).ToList();
                var text = Cpu8085BusDecoder.Instruction(window, 0);
                if (!text.EndsWith(" ?", StringComparison.Ordinal)) return text;
            }

            return string.Empty;
        }
    }

    /// <summary>The registers as a line: <c>PC=0012 A=41 BC=… DE=… HL=… SP=… EI M=111 F=SzkApvc</c>.</summary>
    public string Registers =>
        string.Create(CultureInfo.InvariantCulture,
            $"PC={Core.PC:X4} A={Core.A:X2} BC={Core.BC:X4} DE={Core.DE:X4} HL={Core.HL:X4} SP={Core.SP:X4} {(Core.InterruptsEnabled ? "EI" : "DI")} M={Convert.ToString(Core.Masks, 2).PadLeft(3, '0')} F={Flags(Core.F)}");

    /// <summary>The flags as letters, capital when set: <c>SZKAPVC</c> — sign, zero, K, auxiliary carry, parity, V, carry (bit 3, always 0, left out).</summary>
    public static string Flags(byte f)
    {
        const string names = "SZKAPVC";
        int[] bits = [7, 6, 5, 4, 2, 1, 0];
        var chars = new char[7];
        for (var i = 0; i < 7; i++)
            chars[i] = (f & (1 << bits[i])) != 0 ? names[i] : char.ToLowerInvariant(names[i]);
        return new string(chars);
    }

    // ---- the T-states on the pins ------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var x1 = context.ReadInput(X1, Levels);
        var previous = _lastX1;
        _lastX1 = x1;

        // RST 7.5 and TRAP latch their rising edges whenever they come, between clocks too.
        var core = Core;
        core.Rst75 = context.ReadInput(Rst75, Levels).IsHigh();
        core.Trap = context.ReadInput(Trap, Levels).IsHigh();
        core.SenseEdges();

        if (context.IsInitializing)
        {
            _clockHigh = true;
            Present(context, 0);
            return;
        }

        if (!(x1.IsHigh() && previous.IsLow())) return;

        _clockHigh = !_clockHigh;
        if (_clockHigh) ClockRose(context);
        else ClockFell(context);
    }

    /// <summary>CLK OUT has risen: the middle of the T-state, where the inputs are sampled and a read's byte is taken.</summary>
    private void ClockRose(IDigitalContext context)
    {
        var core = Core;
        core.ResetAsserted = context.ReadInput(ResetIn, Levels).IsLow();
        core.Intr = context.ReadInput(Intr, Levels).IsHigh();
        core.Rst55 = context.ReadInput(Rst55, Levels).IsHigh();
        core.Rst65 = context.ReadInput(Rst65, Levels).IsHigh();
        core.Hold = context.ReadInput(Hold, Levels).IsHigh();
        core.Sid = context.ReadInput(Sid, Levels).IsHigh();
        if (core.SamplesReady) core.ReadyLow = context.ReadInput(Ready, Levels).IsLow();
        if (core.SamplesData) _captured = SampleData(context, core.Address);
        Present(context, DelayFor(context));
    }

    /// <summary>CLK OUT has fallen: the T-state on offer ends and the next begins.</summary>
    private void ClockFell(IDigitalContext context)
    {
        var core = Core;
        var kind = core.Kind;
        var state = core.State;
        var address = core.Address;
        var first = core.FirstCycle;
        if (state == I8085TState.T1) _cycleWaits = 0;
        if (state == I8085TState.Tw) _cycleWaits++;

        var written = core.DataOut;
        var before = core.Instructions;
        core.Complete(_captured);

        var time = context.Time;
        if (state == I8085TState.T3)
        {
            switch (kind)
            {
                case I8085CycleKind.OpcodeFetch or I8085CycleKind.MemoryRead or I8085CycleKind.IoRead or I8085CycleKind.InterruptAcknowledge:
                    Record(new I8085BusCycle(time, address, _captured, kind, _cycleWaits, _fetchFirst && kind == I8085CycleKind.OpcodeFetch));
                    break;
                case I8085CycleKind.MemoryWrite or I8085CycleKind.IoWrite:
                    Record(new I8085BusCycle(time, address, written, kind, _cycleWaits, false));
                    break;
                case I8085CycleKind.BusIdle or I8085CycleKind.RestartAcknowledge:
                    Record(new I8085BusCycle(time, address, 0, kind, 0, false));
                    break;
            }
        }

        if (state == I8085TState.T1) _fetchFirst = first;

        if (core.Halted && !_wasHalted) Record(new I8085BusCycle(time, core.PC, 0, I8085CycleKind.Halt, 0, false));
        _wasHalted = core.Halted;
        var holding = core.Kind == I8085CycleKind.Hold;
        if (holding && !_wasHolding) Record(new I8085BusCycle(time, core.Address, 0, I8085CycleKind.Hold, 0, false));
        _wasHolding = holding;

        if (core.Instructions != before || core.FirstCycle || kind is I8085CycleKind.Hold or I8085CycleKind.Reset) NotifyValueChanged();
        Present(context, DelayFor(context));
    }

    private bool _fetchFirst;

    private byte SampleData(IDigitalContext context, ushort address)
    {
        var value = 0;
        var undefined = false;
        for (var i = 0; i < 8; i++)
        {
            var level = context.ReadInput(_ad[i], Levels);
            if (level.IsHigh()) value |= 1 << i;
            else if (!level.IsLow()) undefined = true;
        }

        if (undefined)
        {
            if (UndefinedReads == 0) FirstUndefinedRead = address;
            UndefinedReads++;
        }

        return (byte)value;
    }

    /// <summary>The pins for the T-state on offer, in its first half (CLK OUT low) or its second (CLK OUT high).</summary>
    private void Present(IDigitalContext context, double delay)
    {
        var core = Core;
        var kind = core.Kind;
        var state = core.State;
        var secondHalf = _clockHigh;
        var floated = core.BusFloated;

        LogicState Level(bool high) => high ? LogicState.High : LogicState.Low;

        // Status: IO/M, S1, S0 for the machine cycle.
        var (ioM, s1, s0) = kind switch
        {
            I8085CycleKind.OpcodeFetch => (false, true, true),
            I8085CycleKind.MemoryRead or I8085CycleKind.BusIdle => (false, true, false),
            I8085CycleKind.MemoryWrite => (false, false, true),
            I8085CycleKind.IoRead => (true, true, false),
            I8085CycleKind.IoWrite => (true, false, true),
            _ => (true, true, true),
        };
        if (kind is I8085CycleKind.Halt or I8085CycleKind.Reset) s1 = s0 = false;
        if (kind == I8085CycleKind.Hold)
        {
            s1 = GetOutputState(S1Index).IsHigh();
            s0 = GetOutputState(S0Index).IsHigh();
        }

        // The strobe: low from T2 to the middle of T3.
        var strobe = state is I8085TState.T2 or I8085TState.Tw || (state == I8085TState.T3 && !secondHalf);
        var rd = strobe && kind is I8085CycleKind.OpcodeFetch or I8085CycleKind.MemoryRead or I8085CycleKind.IoRead;
        var wr = strobe && kind is I8085CycleKind.MemoryWrite or I8085CycleKind.IoWrite;
        var inta = strobe && kind == I8085CycleKind.InterruptAcknowledge;
        var ale = core.AddressLatchEnable && state == I8085TState.T1 && !secondHalf;

        var address = core.Address;
        for (var i = 0; i < 8; i++)
            Request(context, 8 + i, floated ? LogicState.HighImpedance : Level((address & (0x100 << i)) != 0), delay);

        // AD0-AD7: the address in T1, a write's byte from T2 to the end of T3, otherwise let go.
        var writes = kind is I8085CycleKind.MemoryWrite or I8085CycleKind.IoWrite && state is I8085TState.T2 or I8085TState.Tw or I8085TState.T3;
        var addressOut = state == I8085TState.T1 && kind != I8085CycleKind.BusIdle && !floated;
        for (var i = 0; i < 8; i++)
        {
            var level = floated ? LogicState.HighImpedance
                : addressOut ? Level((address & (1 << i)) != 0)
                : writes ? Level((core.DataOut & (1 << i)) != 0)
                : LogicState.HighImpedance;
            Request(context, i, level, delay);
        }

        LogicState Strobe(bool active) => floated ? LogicState.HighImpedance : Level(!active);
        Request(context, AleIndex, Level(ale), delay);
        Request(context, RdIndex, Strobe(rd), delay);
        Request(context, WrIndex, Strobe(wr), delay);
        Request(context, IntaIndex, Level(!inta), delay);
        Request(context, IoMIndex, floated ? LogicState.HighImpedance : Level(ioM), delay);
        Request(context, S0Index, Level(s0), delay);
        Request(context, S1Index, Level(s1), delay);
        Request(context, HldaIndex, Level(core.HoldAcknowledged), delay);
        Request(context, ResetOutIndex, Level(kind == I8085CycleKind.Reset), delay);
        Request(context, SodIndex, Level(core.Sod), delay);
        Request(context, ClockOutIndex, Level(secondHalf), delay);
    }

    private void Record(I8085BusCycle cycle)
    {
        if (_logCount < BusLogCapacity)
        {
            _log[(_logStart + _logCount) % BusLogCapacity] = cycle;
            _logCount++;
        }
        else
        {
            _log[_logStart] = cycle;
            _logStart = (_logStart + 1) % BusLogCapacity;
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Core = new I8085Core();
        _lastX1 = LogicState.Unknown;
        _clockHigh = true;
        _captured = 0;
        _cycleWaits = 0;
        _wasHalted = _wasHolding = _fetchFirst = false;
        _logStart = 0;
        _logCount = 0;
        UndefinedReads = 0;
        FirstUndefinedRead = -1;
    }

    private sealed record RunState(I8085Core Core, I8085BusCycle[] Log, int Start, int Count, int UndefinedReads, int FirstUndefinedRead);

    public object CaptureRunState() =>
        new RunState(Core, (I8085BusCycle[])_log.Clone(), _logStart, _logCount, UndefinedReads, FirstUndefinedRead);

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;

        Core = run.Core;
        Array.Copy(run.Log, _log, BusLogCapacity);
        _logStart = run.Start;
        _logCount = run.Count;
        UndefinedReads = run.UndefinedReads;
        FirstUndefinedRead = run.FirstUndefinedRead;
        NotifyValueChanged();
    }
}
