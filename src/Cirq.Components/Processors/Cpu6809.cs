using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>One bus cycle a 6809 ran: when it ended, where, what, and what kind of cycle it was.</summary>
/// <param name="Time">The E falling edge that ended it, in seconds.</param>
/// <param name="Address">The address on A0-A15: $FFFF on a dead cycle.</param>
/// <param name="Data">The byte read or written.</param>
/// <param name="Kind">An instruction's first byte, a read, a write, a dead cycle or a vector read.</param>
public readonly record struct M6809BusCycle(double Time, ushort Address, byte Data, M6809CycleKind Kind)
{
    public bool IsRead => Kind != M6809CycleKind.Write;
}

/// <summary>
/// A 6809 on its 40 pins, the bus cycles of <see cref="M6809Core"/> put on them one E cycle at a time:
/// the <see cref="Cpu6809"/> with its own clock generator, and the <see cref="Cpu6809E"/> clocked from
/// outside. The core is shared, never copied; only what is around it differs.
/// <para>
/// <b>The bus cycle.</b> A cycle ends as E falls: a read takes the data bus at that instant, so a
/// memory too slow for the clock hands the processor the wrong byte. The address, R/W, BA and BS
/// change <see cref="AddressSetupTime"/> later. A write's data goes out <see cref="WriteDataDelay"/>
/// after Q rises — a quarter cycle before E does — and is held <see cref="WriteDataHold"/> past E
/// falling. A peripheral of the 6800 family is selected by the address and latches on E, as on the
/// real board. The dead cycles are on the pins too: $FFFF with R/W high.
/// </para>
/// <para>
/// <b>The control inputs</b> are sampled as Q falls. /IRQ and /FIRQ are levels and /NMI an edge;
/// /HALT stops the processor at the end of the instruction with the buses let go and BA and BS
/// high; /RESET low holds it (at power-on it runs the reset sequence anyway, as if the line had been
/// pulsed). With BA high — SYNC, a halt, a DMA grant — the address bus, R/W and the data bus float.
/// BA and BS together say what the processor is doing: 00 running, 01 fetching a vector (interrupt
/// or reset acknowledge), 10 SYNC, 11 halted or granted. None of the inputs is pulled up inside, so
/// tie the unused ones high.
/// </para>
/// </summary>
public abstract class Processor6809 : DigitalIc, IRunState
{
    /// <summary>How many bus cycles the bus log keeps, newest last.</summary>
    public const int BusLogCapacity = 1 << 16;

    // Outputs: A0-A15 = 0-15, D0-D7 = 16-23, R/W = 24, BA = 25, BS = 26, then the package's own.
    private const int DataIndex = 16, RwIndex = 24, BaIndex = 25, BsIndex = 26;

    private readonly Terminal[] _address = new Terminal[16];
    private readonly Terminal[] _data = new Terminal[8];
    private readonly M6809BusCycle[] _log = new M6809BusCycle[BusLogCapacity];
    private int _logStart;
    private int _logCount;

    private bool _driving;
    private bool _floating;

    private protected Processor6809(bool external) : base(40)
    {
        External = external;
        Core = new M6809Core { TracksNextCycle = external };
        PropagationDelay = 50e-9;

        var pins = new Terminal[41];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 40));

        Gnd = Pin(1, "VSS", TerminalType.Ground);
        Nmi = Pin(2, "NMI", TerminalType.Input);
        Irq = Pin(3, "IRQ", TerminalType.Input);
        Firq = Pin(4, "FIRQ", TerminalType.Input);
        var bs = Pin(5, "BS", TerminalType.Output);
        var ba = Pin(6, "BA", TerminalType.Output);
        Vcc = Pin(7, "VCC", TerminalType.Power);
        for (var i = 0; i < 16; i++) _address[i] = Pin(8 + i, $"A{i}", TerminalType.Output);
        for (var i = 0; i < 8; i++) _data[i] = Pin(31 - i, $"D{i}", TerminalType.Bidirectional);
        var rw = Pin(32, "RW", TerminalType.Output);
        Reset = Pin(37, "RESET", TerminalType.Input);
        Halt = Pin(40, "HALT", TerminalType.Input);

        List<Terminal> inputs = [Nmi, Irq, Firq, Reset, Halt];
        List<Terminal> outputs = [.. _address, .. _data, rw, ba, bs];

        if (external)
        {
            // The 6809E: E and Q come in from outside; BUSY, AVMA and LIC go out; TSC floats the bus.
            BusyIndex = outputs.Count;
            outputs.Add(Pin(33, "BUSY", TerminalType.Output));
            E = Pin(34, "E", TerminalType.Input);
            Q = Pin(35, "Q", TerminalType.Input);
            AvmaIndex = outputs.Count;
            outputs.Add(Pin(36, "AVMA", TerminalType.Output));
            LicIndex = outputs.Count;
            outputs.Add(Pin(38, "LIC", TerminalType.Output));
            ThreeStateControl = Pin(39, "TSC", TerminalType.Input);
            inputs.AddRange([E, Q, ThreeStateControl]);
        }
        else
        {
            // The 6809: a crystal (or a clock into EXTAL) at four times E, and E and Q out.
            DmaRequest = Pin(33, "DMA", TerminalType.Input);
            EIndex = outputs.Count;
            E = Pin(34, "E", TerminalType.Output);
            outputs.Add(E);
            QIndex = outputs.Count;
            Q = Pin(35, "Q", TerminalType.Output);
            outputs.Add(Q);
            MemoryReady = Pin(36, "MRDY", TerminalType.Input);
            Extal = Pin(38, "EXTAL", TerminalType.Input);
            Pin(39, "XTAL", TerminalType.Passive);
            inputs.AddRange([DmaRequest, MemoryReady, Extal]);
        }

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. inputs, .. _data], outputs);
    }

    /// <summary>True for the 6809E, clocked from E and Q pins; false for the 6809 with its own clock.</summary>
    public bool External { get; }

    /// <summary>The processor inside: registers, flags and the cycle in progress.</summary>
    public M6809Core Core { get; private set; }

    public IReadOnlyList<Terminal> Address => _address;

    public IReadOnlyList<Terminal> Data => _data;

    public Terminal Nmi { get; }

    public Terminal Irq { get; }

    public Terminal Firq { get; }

    public Terminal Reset { get; }

    public Terminal Halt { get; }

    /// <summary>E: an output on the 6809, the clock input on the 6809E. A cycle ends as it falls.</summary>
    public Terminal E { get; }

    /// <summary>Q: E a quarter cycle early. An output on the 6809, an input on the 6809E.</summary>
    public Terminal Q { get; }

    /// <summary>The 6809's clock input: a crystal's worth of TTL clock at four times E. Null on the 6809E.</summary>
    public Terminal? Extal { get; }

    /// <summary>The 6809's MRDY: low stretches E high, a quarter cycle at a time. Null on the 6809E.</summary>
    public Terminal? MemoryReady { get; }

    /// <summary>The 6809's /DMA/BREQ: low asks for the bus. Null on the 6809E.</summary>
    public Terminal? DmaRequest { get; }

    /// <summary>The 6809E's TSC: high floats the address bus, R/W and the data bus. Null on the 6809.</summary>
    public Terminal? ThreeStateControl { get; }

    public Terminal ReadWrite => OutputTerminals[RwIndex];

    public Terminal BusAvailable => OutputTerminals[BaIndex];

    public Terminal BusStatus => OutputTerminals[BsIndex];

    private protected int EIndex { get; } = -1;

    private protected int QIndex { get; } = -1;

    private protected int BusyIndex { get; } = -1;

    private protected int AvmaIndex { get; } = -1;

    private protected int LicIndex { get; } = -1;

    /// <summary>After E falls, the address, R/W, BA and BS have changed.</summary>
    public double AddressSetupTime => PropagationDelay;

    /// <summary>After Q rises, a write's data is on the bus.</summary>
    public double WriteDataDelay => 30e-9;

    /// <summary>After E falls, a write's data is still held this long.</summary>
    public double WriteDataHold => 60e-9;

    /// <summary>E cycles stretched by MRDY so far.</summary>
    public long StretchedQuarters { get; private protected set; }

    public override bool LabelsPins => true;

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "NMI" or "IRQ" or "FIRQ" or "RESET" or "HALT" => "/" + pin.Name,
        "DMA" => "/DMA/BREQ",
        "RW" => "R/W",
        _ => pin.Name,
    };

    public override string ValueLabel => Core.IsStopped
        ? $"stopped at ${Core.PC:X4}"
        : Core.IsHalted
            ? "halted"
            : Core.IsSyncing
                ? $"SYNC at ${Core.PC:X4}"
                : Core.IsWaiting
                    ? $"CWAI at ${Core.PC:X4}"
                    : $"PC ${Core.InstructionAddress:X4} D ${Core.D:X4}";

    /// <summary>The bus cycles performed, oldest first, up to <see cref="BusLogCapacity"/> of the most recent.</summary>
    public IReadOnlyList<M6809BusCycle> BusLog
    {
        get
        {
            var list = new M6809BusCycle[_logCount];
            for (var i = 0; i < _logCount; i++) list[i] = _log[(_logStart + i) % BusLogCapacity];
            return list;
        }
    }

    /// <summary>Read cycles that found a data line at no logic level, dead cycles excepted.</summary>
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
                if (cycle.Kind != M6809CycleKind.Fetch) continue;

                var window = Enumerable.Range(i, Math.Min(12, _logCount - i)).Select(k => _log[(_logStart + k) % BusLogCapacity]).ToList();
                var text = Cpu6809BusDecoder.Instruction(window, 0);
                if (!text.EndsWith(" ?", StringComparison.Ordinal)) return text;
            }

            return string.Empty;
        }
    }

    // ---- the cycle on the pins -----------------------------------------------------

    /// <summary>The control inputs, as Q falls: the core acts on them as the cycle ends.</summary>
    private protected void SampleControls(IDigitalContext context)
    {
        Core.IrqAsserted = context.ReadInput(Irq, Levels).IsLow();
        Core.FirqAsserted = context.ReadInput(Firq, Levels).IsLow();
        Core.NmiAsserted = context.ReadInput(Nmi, Levels).IsLow();
        Core.HaltAsserted = context.ReadInput(Halt, Levels).IsLow();
        if (DmaRequest is not null) Core.DmaRequested = context.ReadInput(DmaRequest, Levels).IsLow();
    }

    /// <summary>Q has risen: a write cycle puts its data on the bus.</summary>
    private protected void QRose(IDigitalContext context)
    {
        if (Core.IsRead || Core.Kind == M6809CycleKind.Released || BusFloated(context)) return;

        var value = Core.DataOut;
        for (var i = 0; i < 8; i++)
            Request(context, DataIndex + i, (value & (1 << i)) != 0 ? LogicState.High : LogicState.Low, WriteDataDelay);
        _driving = true;
    }

    /// <summary>E has fallen: the end of the cycle. The read's byte is taken, the core moves on, the next cycle goes out.</summary>
    private protected void EFell(IDigitalContext context)
    {
        Core.ResetAsserted = context.ReadInput(Reset, Levels).IsLow();

        if (Core.Kind != M6809CycleKind.Released)
        {
            var value = Core.IsRead ? SampleData(context) : Core.DataOut;
            Record(new M6809BusCycle(context.Time, Core.Address, value, Core.Kind));
            var before = Core.Instructions;
            Core.Complete(value);
            if (Core.Instructions != before || Core.Kind == M6809CycleKind.Fetch) NotifyValueChanged();
        }
        else
        {
            Core.Complete();
            NotifyValueChanged();
        }

        if (_driving)
        {
            for (var i = 0; i < 8; i++) Request(context, DataIndex + i, LogicState.HighImpedance, WriteDataHold);
            _driving = false;
        }

        Present(context, AddressSetupTime);
    }

    /// <summary>The 6809E's TSC is high: the bus is someone else's.</summary>
    private protected bool BusFloated(IDigitalContext context) =>
        ThreeStateControl is not null && context.ReadInput(ThreeStateControl, Levels).IsHigh();

    private byte SampleData(IDigitalContext context)
    {
        var value = 0;
        var undefined = false;

        for (var i = 0; i < 8; i++)
        {
            var level = context.ReadInput(_data[i], Levels);
            if (level.IsHigh()) value |= 1 << i;
            else if (!level.IsLow()) undefined = true;
        }

        if (undefined && Core.Kind != M6809CycleKind.Dead)
        {
            if (UndefinedReads == 0) FirstUndefinedRead = Core.Address;
            UndefinedReads++;
        }

        return (byte)value;
    }

    /// <summary>Puts the cycle on offer onto the address pins, R/W, BA and BS (and the 6809E's BUSY, AVMA and LIC).</summary>
    private protected void Present(IDigitalContext context, double delay)
    {
        var released = Core.Kind == M6809CycleKind.Released;
        var floated = released || BusFloated(context);
        _floating = floated;
        var address = Core.Address;

        for (var i = 0; i < 16; i++)
            Request(context, i, floated ? LogicState.HighImpedance : (address & (1 << i)) != 0 ? LogicState.High : LogicState.Low, delay);
        Request(context, RwIndex, floated ? LogicState.HighImpedance : Core.IsRead ? LogicState.High : LogicState.Low, delay);
        Request(context, BaIndex, Core.BusAvailable ? LogicState.High : LogicState.Low, delay);
        Request(context, BsIndex, Core.BusStatus ? LogicState.High : LogicState.Low, delay);

        if (External)
        {
            var (valid, last) = Core.PeekNext();
            Request(context, BusyIndex, Core.Busy ? LogicState.High : LogicState.Low, delay);
            Request(context, AvmaIndex, valid ? LogicState.High : LogicState.Low, delay);
            Request(context, LicIndex, last ? LogicState.High : LogicState.Low, delay);
        }

        if (context.IsInitializing || !_driving)
        {
            for (var i = 0; i < 8; i++) Request(context, DataIndex + i, LogicState.HighImpedance, delay);
        }
    }

    /// <summary>Between edges only TSC can change what is on the pins.</summary>
    private protected void FollowThreeStateControl(IDigitalContext context)
    {
        if (ThreeStateControl is null) return;
        var floated = Core.Kind == M6809CycleKind.Released || BusFloated(context);
        if (floated == _floating) return;

        Present(context, AddressSetupTime);
        if (floated && _driving)
        {
            for (var i = 0; i < 8; i++) Request(context, DataIndex + i, LogicState.HighImpedance, AddressSetupTime);
            _driving = false;
        }
    }

    private void Record(M6809BusCycle cycle)
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

    private protected void ResetBus()
    {
        _driving = false;
        _floating = false;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        Core = new M6809Core { TracksNextCycle = External };
        _driving = false;
        _floating = false;
        _logStart = 0;
        _logCount = 0;
        UndefinedReads = 0;
        FirstUndefinedRead = -1;
        StretchedQuarters = 0;
        ResetClock();
    }

    /// <summary>The clock's own state, put back to power-on.</summary>
    private protected abstract void ResetClock();

    private sealed record RunState(M6809Core Core, M6809BusCycle[] Log, int Start, int Count, int UndefinedReads, int FirstUndefinedRead, long Stretched);

    public object CaptureRunState() =>
        new RunState(Core, (M6809BusCycle[])_log.Clone(), _logStart, _logCount, UndefinedReads, FirstUndefinedRead, StretchedQuarters);

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;

        Core = run.Core;
        Array.Copy(run.Log, _log, BusLogCapacity);
        _logStart = run.Start;
        _logCount = run.Count;
        UndefinedReads = run.UndefinedReads;
        FirstUndefinedRead = run.FirstUndefinedRead;
        StretchedQuarters = run.Stretched;
        NotifyValueChanged();
    }

    /// <summary>The registers as a line: <c>PC=E012 A=41 B=00 X=E100 Y=0000 U=0000 S=7F00 DP=00 CC=EFhInzvc</c>.</summary>
    public string Registers =>
        string.Create(CultureInfo.InvariantCulture,
            $"PC={Core.PC:X4} A={Core.A:X2} B={Core.B:X2} X={Core.X:X4} Y={Core.Y:X4} U={Core.U:X4} S={Core.S:X4} DP={Core.DP:X2} CC={Flags(Core.CC)}");

    /// <summary>The condition codes as letters, capital when set: <c>EFHINZVC</c>.</summary>
    public static string Flags(byte cc)
    {
        const string names = "EFHINZVC";
        var chars = new char[8];
        for (var i = 0; i < 8; i++)
            chars[i] = (cc & (0x80 >> i)) != 0 ? names[i] : char.ToLowerInvariant(names[i]);
        return new string(chars);
    }
}

/// <summary>
/// The Motorola MC6809, on its DIP-40 pinout with its own clock generator: a crystal on XTAL and
/// EXTAL (or a TTL clock into EXTAL) at four times the bus rate, E and Q out in quadrature at a
/// quarter of it.
/// <para>
/// <b>The clock.</b> Every rising edge on EXTAL is a quarter of an E cycle: Q rises, E rises, Q
/// falls, E falls — and with E the cycle ends. MRDY low as E is due to fall holds E high (and Q low)
/// for another quarter, and another, until MRDY comes back: a slow memory or peripheral stretches
/// the cycle in quarter-cycle steps, the stretch counted in <see cref="Processor6809.StretchedQuarters"/>.
/// /DMA/BREQ low asks for the bus between cycles: the processor finishes the cycle, takes a dead
/// cycle, lets go of the buses with BA and BS high, and takes one cycle back every fifteen to
/// refresh itself, with a dead cycle when the request ends. XTAL is drawn but not simulated: drive
/// EXTAL from an oscillator. The 1 MHz MC6809 wants a 4 MHz clock; the 68A09 and 68B09 run at 1.5
/// and 2 MHz on E.
/// </para>
/// <para>
/// Pinout: 1 = VSS, 2 = /NMI, 3 = /IRQ, 4 = /FIRQ, 5 = BS, 6 = BA, 7 = VCC, 8-23 = A0-A15,
/// 24-31 = D7-D0, 32 = R/W, 33 = /DMA/BREQ, 34 = E, 35 = Q, 36 = MRDY, 37 = /RESET, 38 = EXTAL,
/// 39 = XTAL, 40 = /HALT.
/// </para>
/// </summary>
public sealed class Cpu6809 : Processor6809
{
    // The quarter of the E cycle the clock is in: 0 E and Q low, 1 Q high, 2 both high, 3 E high.
    private int _phase;
    private LogicState _lastExtal = LogicState.Unknown;

    public Cpu6809() : base(external: false)
    {
    }

    public override string PartNumber => "MC6809";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var extal = context.ReadInput(Extal!, Levels);
        var previous = _lastExtal;
        _lastExtal = extal;

        if (context.IsInitializing)
        {
            ResetBus();
            Present(context, 0);
            DriveClocks(context, 0);
            return;
        }

        // Only a rising edge on EXTAL moves the clock on a quarter.
        if (!(extal.IsHigh() && previous.IsLow())) return;

        switch (_phase)
        {
            case 0:
                _phase = 1;
                DriveClocks(context, DelayFor(context) / 5);
                QRose(context);
                break;

            case 1:
                _phase = 2;
                DriveClocks(context, DelayFor(context) / 5);
                break;

            case 2:
                _phase = 3;
                SampleControls(context);
                DriveClocks(context, DelayFor(context) / 5);
                break;

            default:
                if (context.ReadInput(MemoryReady!, Levels).IsLow())
                {
                    // MRDY holds E high another quarter.
                    StretchedQuarters++;
                    break;
                }

                _phase = 0;
                DriveClocks(context, DelayFor(context) / 5);
                EFell(context);
                break;
        }
    }

    private void DriveClocks(IDigitalContext context, double delay)
    {
        Request(context, EIndex, _phase is 2 or 3 ? LogicState.High : LogicState.Low, delay);
        Request(context, QIndex, _phase is 1 or 2 ? LogicState.High : LogicState.Low, delay);
    }

    private protected override void ResetClock()
    {
        _phase = 0;
        _lastExtal = LogicState.Unknown;
    }
}

/// <summary>
/// The Motorola MC6809E: the 6809 with its clock generator taken out, for a system that makes E and
/// Q itself — as the Color Computer's SAM does — and with three pins for sharing the bus.
/// <para>
/// <b>The clock.</b> E and Q are inputs: a write's data goes out after Q rises, the control inputs
/// are sampled as Q falls, and the cycle ends as E falls. Stretching a cycle is the clock's business.
/// <b>TSC</b> high floats the address bus, R/W and the data bus, for another master; the processor
/// itself carries on, so the clock must be held while it lasts.
/// </para>
/// <para>
/// <b>The status outputs.</b> <b>LIC</b> is high during the last cycle of every instruction, so its
/// fall marks an opcode fetch. <b>AVMA</b> is high when the next cycle will use the bus, and low
/// before a dead cycle. Both are a cycle ahead, which the chip can be because neither depends on the
/// byte in flight; here the core works them out by replaying the instruction so far on a copy of
/// itself. Where the byte in flight would decide it — the opcode of TSTA and TSTB, whose next cycle
/// is dead, and the undocumented $38, and an undefined indexed postbyte — they are given as if a bus
/// cycle followed. <b>BUSY</b> is high during the read and modify cycles of a read-modify-write, the
/// first byte of a 16-bit operand, and the first byte of an indirect pointer or a vector. There is
/// no MRDY and no /DMA/BREQ.
/// </para>
/// <para>
/// Pinout: 1 = VSS, 2 = /NMI, 3 = /IRQ, 4 = /FIRQ, 5 = BS, 6 = BA, 7 = VCC, 8-23 = A0-A15,
/// 24-31 = D7-D0, 32 = R/W, 33 = BUSY, 34 = E, 35 = Q, 36 = AVMA, 37 = /RESET, 38 = LIC, 39 = TSC,
/// 40 = /HALT.
/// </para>
/// </summary>
public sealed class Cpu6809E : Processor6809
{
    private LogicState _lastE = LogicState.Unknown;
    private LogicState _lastQ = LogicState.Unknown;

    public Cpu6809E() : base(external: true)
    {
    }

    public override string PartNumber => "MC6809E";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var e = context.ReadInput(E, Levels);
        var q = context.ReadInput(Q, Levels);
        var eBefore = _lastE;
        var qBefore = _lastQ;
        _lastE = e;
        _lastQ = q;

        if (context.IsInitializing)
        {
            ResetBus();
            Present(context, 0);
            return;
        }

        if (q.IsHigh() && qBefore.IsLow()) QRose(context);
        if (q.IsLow() && qBefore.IsHigh()) SampleControls(context);
        if (e.IsLow() && eBefore.IsHigh()) EFell(context);
        else FollowThreeStateControl(context);
    }

    private protected override void ResetClock()
    {
        _lastE = LogicState.Unknown;
        _lastQ = LogicState.Unknown;
    }
}
