using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>One machine cycle a Z80 ran on its bus: when it ended, where, what, and what kind it was.</summary>
/// <param name="Time">When the byte was taken or the cycle ended, in seconds.</param>
/// <param name="Address">The address on A0-A15: the port (with A8-A15) for I/O, the refresh address for a refresh.</param>
/// <param name="Data">The byte read or written; the vector for an acknowledge; nothing for a refresh.</param>
/// <param name="Kind">An M1 fetch, a memory or I/O read or write, an interrupt acknowledge, or a refresh (<see cref="Z80CycleKind.Internal"/>).</param>
/// <param name="WaitStates">Wait states /WAIT added to it (not the automatic ones).</param>
/// <param name="First">An M1 that starts an instruction, rather than one fetching the opcode after a prefix.</param>
/// <param name="Halted">An M1 of the NOPs HALT runs.</param>
public readonly record struct Z80BusCycle(double Time, ushort Address, byte Data, Z80CycleKind Kind, int WaitStates, bool First, bool Halted)
{
    /// <summary>A refresh: the low seven bits of R, and I, on the address bus in T3 and T4 of an M1.</summary>
    public bool IsRefresh => Kind == Z80CycleKind.Internal;
}

/// <summary>
/// The Zilog Z80, on its DIP-40 pinout: the T-states of <see cref="Z80Core"/> put on the pins one CLK
/// cycle at a time.
/// <para>
/// <b>The clock.</b> A T-state is one CLK cycle, from rising edge to rising edge. The processor
/// changes the address bus and starts each machine cycle as CLK rises; /MREQ and /RD go low as CLK
/// falls in T1 of a memory cycle, /WR as it falls in T2, and an I/O cycle's /IORQ with /RD or /WR as
/// CLK rises in T2. An M1's opcode is taken from the data bus as T3 begins, a read's byte as CLK
/// falls in T3; then /M1 goes high, the refresh address (R on A0-A6, I on A8-A15) goes out with
/// /RFSH low, and /MREQ strobes once more for the refresh. The changes come
/// <see cref="DigitalComponent.PropagationDelay"/> after the edge. A write's data goes out as CLK
/// falls in T1 and is held until the next cycle begins.
/// </para>
/// <para>
/// <b>/WAIT</b> is sampled as CLK falls in T2 (and in an I/O cycle's automatic wait state, and the
/// second of an acknowledge's two): low, and a wait state follows, and another, until it is seen
/// high — the slow memory's or peripheral's way of stretching the cycle; the processor counts them.
/// <b>/INT</b> is sampled as the last T-state of an instruction begins and acknowledged with an M1 in
/// which /IORQ goes low in place of /MREQ, after two automatic wait states: the interrupting device
/// puts its byte on the bus then. <b>/NMI</b> is an edge. <b>/HALT</b> goes low while the processor
/// runs HALT's NOPs. <b>/BUSREQ</b> low, seen as a machine cycle's last T-state begins, floats the
/// address and data buses, /MREQ, /IORQ, /RD and /WR before the next cycle and takes /BUSACK low;
/// there is no refresh while it lasts. <b>/RESET</b> low floats the buses and holds the controls
/// inactive; let go, the processor starts at $0000. None of the inputs is pulled up inside: tie
/// the unused ones high.
/// </para>
/// <para>
/// Pinout: 1-5 = A11-A15, 6 = CLK, 7 = D4, 8 = D3, 9 = D5, 10 = D6, 11 = +5V, 12 = D2, 13 = D7,
/// 14 = D0, 15 = D1, 16 = /INT, 17 = /NMI, 18 = /HALT, 19 = /MREQ, 20 = /IORQ, 21 = /RD, 22 = /WR,
/// 23 = /BUSACK, 24 = /WAIT, 25 = /BUSREQ, 26 = /RESET, 27 = /M1, 28 = /RFSH, 29 = GND, 30-40 = A0-A10.
/// </para>
/// </summary>
public sealed class CpuZ80 : DigitalIc, IRunState
{
    /// <summary>How many machine cycles the bus log keeps, newest last.</summary>
    public const int BusLogCapacity = 1 << 16;

    // Outputs: A0-A15 = 0-15, D0-D7 = 16-23, then the controls.
    private const int DataIndex = 16, M1Index = 24, MreqIndex = 25, IorqIndex = 26, RdIndex = 27, WrIndex = 28, RfshIndex = 29,
        HaltIndex = 30, BusackIndex = 31;

    private static readonly int[] DataPins = [14, 15, 12, 8, 7, 9, 10, 13];

    private readonly Terminal[] _address = new Terminal[16];
    private readonly Terminal[] _data = new Terminal[8];
    private readonly Z80BusCycle[] _log = new Z80BusCycle[BusLogCapacity];
    private int _logStart;
    private int _logCount;

    private LogicState _lastClock = LogicState.Unknown;
    private byte _captured;
    private bool _driving;
    private int _cycleWaits;
    private bool _cycleFirst;
    private ushort _cycleAddress;

    public CpuZ80() : base(40)
    {
        PropagationDelay = 40e-9;

        var pins = new Terminal[41];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 40));

        for (var i = 0; i < 5; i++) _address[11 + i] = Pin(1 + i, $"A{11 + i}", TerminalType.Output);
        Clock = Pin(6, "CLK", TerminalType.Input);
        for (var i = 0; i < 8; i++) _data[i] = Pin(DataPins[i], $"D{i}", TerminalType.Bidirectional);
        Vcc = Pin(11, "VCC", TerminalType.Power);
        Int = Pin(16, "INT", TerminalType.Input);
        Nmi = Pin(17, "NMI", TerminalType.Input);
        var halt = Pin(18, "HALT", TerminalType.Output);
        var mreq = Pin(19, "MREQ", TerminalType.Output);
        var iorq = Pin(20, "IORQ", TerminalType.Output);
        var rd = Pin(21, "RD", TerminalType.Output);
        var wr = Pin(22, "WR", TerminalType.Output);
        var busack = Pin(23, "BUSACK", TerminalType.Output);
        Wait = Pin(24, "WAIT", TerminalType.Input);
        BusRequest = Pin(25, "BUSREQ", TerminalType.Input);
        Reset = Pin(26, "RESET", TerminalType.Input);
        var m1 = Pin(27, "M1", TerminalType.Output);
        var rfsh = Pin(28, "RFSH", TerminalType.Output);
        Gnd = Pin(29, "GND", TerminalType.Ground);
        for (var i = 0; i < 11; i++) _address[i] = Pin(30 + i, $"A{i}", TerminalType.Output);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Clock, Int, Nmi, Wait, BusRequest, Reset, .. _data], [.. _address, .. _data, m1, mreq, iorq, rd, wr, rfsh, halt, busack]);
    }

    public override string PartNumber => "Z80";

    public override string ComponentType => "Z80 CPU";

    public override bool LabelsPins => true;

    /// <summary>The processor inside: registers, flags and the T-state in progress.</summary>
    public Z80Core Core { get; private set; } = new();

    public IReadOnlyList<Terminal> Address => _address;

    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>CLK: one cycle a T-state.</summary>
    public Terminal Clock { get; }

    public Terminal Int { get; }

    public Terminal Nmi { get; }

    public Terminal Wait { get; }

    public Terminal BusRequest { get; }

    public Terminal Reset { get; }

    public Terminal M1 => OutputTerminals[M1Index];

    public Terminal MemoryRequest => OutputTerminals[MreqIndex];

    public Terminal IoRequest => OutputTerminals[IorqIndex];

    public Terminal ReadStrobe => OutputTerminals[RdIndex];

    public Terminal WriteStrobe => OutputTerminals[WrIndex];

    public Terminal RefreshStrobe => OutputTerminals[RfshIndex];

    public Terminal HaltOut => OutputTerminals[HaltIndex];

    public Terminal BusAcknowledge => OutputTerminals[BusackIndex];

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "INT" or "NMI" or "HALT" or "MREQ" or "IORQ" or "RD" or "WR" or "BUSACK" or "WAIT" or "BUSREQ" or "RESET" or "M1" or "RFSH" => "/" + pin.Name,
        "VCC" => "+5V",
        _ => pin.Name,
    };

    public override string ValueLabel => Core.ResetAsserted
        ? "reset"
        : Core.Halted
            ? $"HALT at ${Core.PC - 1:X4}"
            : Core.BusAcknowledged
                ? "bus released"
                : $"PC ${Core.InstructionAddress:X4} A ${Core.A:X2}";

    /// <summary>The machine cycles performed, oldest first, up to <see cref="BusLogCapacity"/> of the most recent, refreshes included.</summary>
    public IReadOnlyList<Z80BusCycle> BusLog
    {
        get
        {
            var list = new Z80BusCycle[_logCount];
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
            "two outputs fighting on the data bus, or nothing selected there at all",
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
                if (cycle.Kind != Z80CycleKind.Fetch || !cycle.First || cycle.Halted) continue;

                var window = Enumerable.Range(i, Math.Min(16, _logCount - i)).Select(k => _log[(_logStart + k) % BusLogCapacity]).ToList();
                var text = CpuZ80BusDecoder.Instruction(window, 0);
                if (!text.EndsWith(" ?", StringComparison.Ordinal)) return text;
            }

            return string.Empty;
        }
    }

    /// <summary>The registers as a line: <c>PC=0012 AF=41C4 BC=… DE=… HL=… IX=… IY=… SP=… I=00 R=1F IM1 EI F=SZyHxPnc</c>.</summary>
    public string Registers =>
        string.Create(CultureInfo.InvariantCulture,
            $"PC={Core.PC:X4} AF={Core.AF:X4} BC={Core.BC:X4} DE={Core.DE:X4} HL={Core.HL:X4} IX={Core.IX:X4} IY={Core.IY:X4} SP={Core.SP:X4} I={Core.I:X2} R={Core.R:X2} IM{Core.InterruptMode} {(Core.Iff1 ? "EI" : "DI")} F={Flags(Core.F)}");

    /// <summary>The flags as letters, capital when set: <c>SZYHXPNC</c>.</summary>
    public static string Flags(byte f)
    {
        const string names = "SZYHXPNC";
        var chars = new char[8];
        for (var i = 0; i < 8; i++)
            chars[i] = (f & (0x80 >> i)) != 0 ? names[i] : char.ToLowerInvariant(names[i]);
        return new string(chars);
    }

    // ---- the T-states on the pins ------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var clock = context.ReadInput(Clock, Levels);
        var previous = _lastClock;
        _lastClock = clock;

        if (context.IsInitializing)
        {
            _driving = false;
            Present(context, secondHalf: false, 0);
            return;
        }

        if (clock.IsHigh() && previous.IsLow()) Rose(context);
        else if (clock.IsLow() && previous.IsHigh()) Fell(context);
    }

    /// <summary>CLK has risen: the T-state on offer ends and the next begins.</summary>
    private void Rose(IDigitalContext context)
    {
        var core = Core;
        core.ResetAsserted = context.ReadInput(Reset, Levels).IsLow();
        core.IntAsserted = context.ReadInput(Int, Levels).IsLow();
        core.NmiAsserted = context.ReadInput(Nmi, Levels).IsLow();
        core.BusRequested = context.ReadInput(BusRequest, Levels).IsLow();

        var kind = core.Kind;
        var state = core.State;
        var address = core.Address;
        if (state == Z80TState.T1)
        {
            _cycleWaits = 0;
            _cycleFirst = core.FirstCycle;
            _cycleAddress = address;
        }

        if (state == Z80TState.Tw && !core.AutomaticWait) _cycleWaits++;

        // An M1's or an acknowledge's byte is taken as T3 begins; a read's was taken as CLK fell.
        // A wait state still to come means the byte is not taken yet.
        byte value = 0;
        var waiting = core.SamplesWait && core.WaitAsserted;
        if (core.SamplesData && !waiting) value = kind is Z80CycleKind.Fetch or Z80CycleKind.Acknowledge ? SampleData(context, address) : _captured;

        var halted = core.Halted;
        var written = core.DataOut;
        var before = core.Instructions;
        core.Complete(value);

        var time = context.Time;
        switch (kind)
        {
            case Z80CycleKind.Fetch or Z80CycleKind.Acknowledge when core.State == Z80TState.T3 && state != Z80TState.T3:
                Record(new Z80BusCycle(time, _cycleAddress, value, kind, _cycleWaits, _cycleFirst && kind == Z80CycleKind.Fetch, halted));
                break;
            case Z80CycleKind.Fetch or Z80CycleKind.Acknowledge when state == Z80TState.T4:
                Record(new Z80BusCycle(time, address, 0, Z80CycleKind.Internal, 0, false, false));
                break;
            case Z80CycleKind.Read or Z80CycleKind.IoRead when state == Z80TState.T3:
                Record(new Z80BusCycle(time, address, value, kind, _cycleWaits, false, false));
                break;
            case Z80CycleKind.Write or Z80CycleKind.IoWrite when state == Z80TState.T3:
                Record(new Z80BusCycle(time, address, written, kind, _cycleWaits, false, false));
                break;
        }

        if (core.Instructions != before || core.FirstCycle || kind is Z80CycleKind.Released or Z80CycleKind.Reset) NotifyValueChanged();

        // A write's data is held into the next cycle's start, then let go.
        if (_driving && core.State == Z80TState.T1 || _driving && core.Kind is not (Z80CycleKind.Write or Z80CycleKind.IoWrite))
        {
            for (var i = 0; i < 8; i++) Request(context, DataIndex + i, LogicState.HighImpedance, PropagationDelay);
            _driving = false;
        }

        Present(context, secondHalf: false, DelayFor(context));
    }

    /// <summary>CLK has fallen: the middle of the T-state, where /WAIT and a read's byte are sampled.</summary>
    private void Fell(IDigitalContext context)
    {
        var core = Core;
        if (core.SamplesWait) core.WaitAsserted = context.ReadInput(Wait, Levels).IsLow();
        if (core.SamplesData && core.Kind is Z80CycleKind.Read or Z80CycleKind.IoRead) _captured = SampleData(context, core.Address);
        Present(context, secondHalf: true, DelayFor(context));
    }

    private byte SampleData(IDigitalContext context, ushort address)
    {
        var value = 0;
        var undefined = false;
        for (var i = 0; i < 8; i++)
        {
            var level = context.ReadInput(_data[i], Levels);
            if (level.IsHigh()) value |= 1 << i;
            else if (!level.IsLow()) undefined = true;
        }

        // Mode 1 ignores the acknowledge's byte, so nothing driving the bus then is no fault.
        if (undefined && !(Core.Kind == Z80CycleKind.Acknowledge && Core.InterruptMode == 1))
        {
            if (UndefinedReads == 0) FirstUndefinedRead = address;
            UndefinedReads++;
        }

        return (byte)value;
    }

    /// <summary>The pins for the T-state on offer, in its first half (CLK high) or its second (CLK low).</summary>
    private void Present(IDigitalContext context, bool secondHalf, double delay)
    {
        var core = Core;
        var kind = core.Kind;
        var state = core.State;
        var floated = kind is Z80CycleKind.Released or Z80CycleKind.Reset;

        bool m1 = false, mreq = false, iorq = false, rd = false, wr = false, rfsh = false;
        switch (kind)
        {
            case Z80CycleKind.Fetch or Z80CycleKind.Acknowledge:
            {
                var fetch = kind == Z80CycleKind.Fetch;
                switch (state)
                {
                    case Z80TState.T1:
                        m1 = true;
                        mreq = rd = fetch && secondHalf;
                        break;
                    case Z80TState.T2:
                        m1 = true;
                        mreq = rd = fetch;
                        break;
                    case Z80TState.Tw:
                        m1 = true;
                        mreq = rd = fetch;
                        // The acknowledge's /IORQ from the middle of the first automatic wait.
                        iorq = !fetch && (secondHalf || core.SamplesWait);
                        break;
                    case Z80TState.T3:
                        rfsh = true;
                        mreq = secondHalf;
                        break;
                    default:
                        rfsh = true;
                        mreq = !secondHalf;
                        break;
                }

                break;
            }

            case Z80CycleKind.Read:
                mreq = rd = state switch { Z80TState.T1 => secondHalf, Z80TState.T3 => !secondHalf, _ => true };
                break;

            case Z80CycleKind.Write:
                mreq = state switch { Z80TState.T1 => secondHalf, Z80TState.T3 => !secondHalf, _ => true };
                wr = state switch { Z80TState.T2 => secondHalf, Z80TState.Tw => true, Z80TState.T3 => !secondHalf, _ => false };
                break;

            case Z80CycleKind.IoRead or Z80CycleKind.IoWrite:
            {
                var active = state switch { Z80TState.T1 => false, Z80TState.T3 => !secondHalf, _ => true };
                iorq = active;
                rd = active && kind == Z80CycleKind.IoRead;
                wr = active && kind == Z80CycleKind.IoWrite;
                break;
            }
        }

        var address = core.Address;
        for (var i = 0; i < 16; i++)
            Request(context, i, floated ? LogicState.HighImpedance : (address & (1 << i)) != 0 ? LogicState.High : LogicState.Low, delay);

        LogicState Strobe(bool active) => floated ? LogicState.HighImpedance : active ? LogicState.Low : LogicState.High;
        Request(context, MreqIndex, Strobe(mreq), delay);
        Request(context, IorqIndex, Strobe(iorq), delay);
        Request(context, RdIndex, Strobe(rd), delay);
        Request(context, WrIndex, Strobe(wr), delay);
        Request(context, M1Index, m1 ? LogicState.Low : LogicState.High, delay);
        Request(context, RfshIndex, rfsh ? LogicState.Low : LogicState.High, delay);
        Request(context, HaltIndex, core.Halted ? LogicState.Low : LogicState.High, delay);
        Request(context, BusackIndex, kind == Z80CycleKind.Released ? LogicState.Low : LogicState.High, delay);

        // A write's data from the middle of T1 until the next cycle begins.
        if (kind is Z80CycleKind.Write or Z80CycleKind.IoWrite && (secondHalf || state != Z80TState.T1))
        {
            var value = core.DataOut;
            for (var i = 0; i < 8; i++) Request(context, DataIndex + i, (value & (1 << i)) != 0 ? LogicState.High : LogicState.Low, delay);
            _driving = true;
        }
        else if (!_driving || floated)
        {
            for (var i = 0; i < 8; i++) Request(context, DataIndex + i, LogicState.HighImpedance, delay);
            _driving = false;
        }
    }

    private void Record(Z80BusCycle cycle)
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
        Core = new Z80Core();
        _lastClock = LogicState.Unknown;
        _captured = 0;
        _driving = false;
        _cycleWaits = 0;
        _logStart = 0;
        _logCount = 0;
        UndefinedReads = 0;
        FirstUndefinedRead = -1;
    }

    private sealed record RunState(Z80Core Core, Z80BusCycle[] Log, int Start, int Count, int UndefinedReads, int FirstUndefinedRead);

    public object CaptureRunState() =>
        new RunState(Core, (Z80BusCycle[])_log.Clone(), _logStart, _logCount, UndefinedReads, FirstUndefinedRead);

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
