using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>What a 68000 bus cycle in the log was.</summary>
public enum M68000CycleKind
{
    Read,
    Write,
    InterruptAcknowledge,

    /// <summary>Not a bus cycle: an instruction that started at <see cref="M68000BusCycle.Start"/>, with its words.</summary>
    Instruction,
}

/// <summary>One bus cycle a 68000 ran, or an instruction it started, as its bus log records them.</summary>
/// <param name="Start">When the cycle began: the CLK rising edge starting S0, in seconds.</param>
/// <param name="End">When it ended: the rising edge ending S7.</param>
/// <param name="Address">The byte address: A1-A23 with the strobe that was used as A0. A word is at the even address.</param>
/// <param name="Data">The word moved, or the byte on a byte cycle.</param>
/// <param name="Kind">Read, write, interrupt acknowledge, or an instruction marker.</param>
/// <param name="FunctionCode">FC2-FC0.</param>
/// <param name="UpperByte">/UDS was asserted: D8-D15.</param>
/// <param name="LowerByte">/LDS was asserted: D0-D7.</param>
/// <param name="WaitStates">Clocks spent waiting for /DTACK (or /VPA's E clock) beyond the four of a cycle.</param>
/// <param name="Words">For an instruction marker: its opcode and extension words.</param>
public readonly record struct M68000BusCycle(
    double Start, double End, uint Address, ushort Data, M68000CycleKind Kind, int FunctionCode,
    bool UpperByte, bool LowerByte, int WaitStates, ushort[]? Words = null)
{
    public bool IsWord => UpperByte && LowerByte;
}

/// <summary>
/// The Motorola MC68000 on its 64-pin DIP, run by its CLK pin one state at a time.
/// <para>
/// <b>The asynchronous bus.</b> A 68000 does not assume its memory keeps up. Every bus cycle is
/// eight states, two per clock — S0 on CLK rising puts out the function code and R/W, S1 the
/// address, S2 asserts /AS (and on a read /UDS and /LDS), S3 puts out a write's data, S4 asserts a
/// write's strobes — and at the end of S4 the processor looks at /DTACK. If nothing has answered,
/// it waits, a whole clock at a time, until /DTACK (or /BERR) comes; then S5 and S6, and as CLK falls
/// into S7 it takes the data and lets go of the strobes. So a slow memory needs a circuit that
/// pulls /DTACK low when the memory is ready — a DTACK generator — and the wait states it asks for
/// are visible here on the pins, in <see cref="WaitStates"/> and in the bus log.
/// </para>
/// <para>
/// <b>Everything else on the pins.</b> /BERR ends a cycle with a bus error (with /HALT too, the cycle
/// is run again once /HALT goes high). /VPA instead of /DTACK makes a 6800-style cycle: the processor
/// asserts /VMA when the E clock (CLK ÷ 10, six clocks low and four high) is low and takes the data
/// as E falls. /VPA during an interrupt acknowledge (FC = 111, the level on A1-A3) asks for the
/// autovector. /IPL0-/IPL2 are the interrupt level, active low, sampled as CLK falls. /BR asks for
/// the bus: /BG answers at the end of the cycle, and the processor stays off the bus — address,
/// data, FC, /AS, /UDS, /LDS and R/W let go — until /BR and /BGACK are both high. /RESET and /HALT
/// low together reset it; the RESET instruction drives /RESET low for 124 clocks; a double bus fault
/// drives /HALT low. /HALT alone stops it before its next cycle. /RESET and /HALT are open drain.
/// </para>
/// <para>
/// Simplifications: the address bus and FC hold their last value between cycles rather than floating
/// (they float on a bus grant and in reset); /IPL is taken as soon as it is sampled, not after two
/// matching samples; the processor's internal work also stops while the bus is granted or /HALT is
/// low; and timing inside a state is a single propagation delay. The instruction set is
/// <see cref="M68000Core"/>'s.
/// </para>
/// <para>
/// Pinout: 1-5 = D4-D0, 6 = /AS, 7 = /UDS, 8 = /LDS, 9 = R/W, 10 = /DTACK, 11 = /BG, 12 = /BGACK,
/// 13 = /BR, 14 = VCC, 15 = CLK, 16 = GND, 17 = /HALT, 18 = /RESET, 19 = /VMA, 20 = E, 21 = /VPA,
/// 22 = /BERR, 23-25 = /IPL2-/IPL0, 26-28 = FC2-FC0, 29-48 = A1-A20, 49 = VCC, 50-52 = A21-A23,
/// 53 = GND, 54-64 = D15-D5.
/// </para>
/// </summary>
public sealed class Cpu68000 : DigitalIc, IRunState
{
    /// <summary>How many entries the bus log keeps, newest last.</summary>
    public const int BusLogCapacity = 1 << 16;

    // Output indices: A1-A23, D0-D15, FC0-FC2, then the control outputs.
    private const int DataIndex = 23, FcIndex = 39;
    private const int AsIndex = 42, UdsIndex = 43, LdsIndex = 44, RwIndex = 45, BgIndex = 46, EIndex = 47, VmaIndex = 48;
    private const int HaltIndex = 49, ResetIndex = 50;

    private readonly Terminal[] _address = new Terminal[23];
    private readonly Terminal[] _data = new Terminal[16];
    private readonly Terminal[] _fc = new Terminal[3];
    private readonly Terminal[] _ipl = new Terminal[3];
    private readonly M68000BusCycle[] _log = new M68000BusCycle[BusLogCapacity];
    private int _logStart;
    private int _logCount;

    private LogicState _lastClock = LogicState.Unknown;

    // The bus cycle in progress: its state, S0-S7, or 9 for a wait state's low half; −1 between cycles.
    private int _state = -1;
    private int _idle;
    private bool _idleRunning;
    private int _waits;
    private ushort _latched;
    private bool _berr;
    private bool _rerun;
    private bool _vpa;
    private bool _vma;
    private bool _driving;
    private bool _resetDriven;
    private double _cycleStart;
    private double _instructionStart;
    private long _instructionsSeen;

    // E is CLK / 10: low for six clocks, high for four, stepped as CLK falls.
    private int _eCount;

    private bool _granted;
    private bool _stalled;
    private bool _held;

    public Cpu68000() : base(64)
    {
        PropagationDelay = 30e-9;

        var pins = new Terminal[65];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 64));

        for (var i = 0; i < 5; i++) _data[i] = Pin(5 - i, $"D{i}", TerminalType.Bidirectional);
        AddressStrobe = Pin(6, "AS", TerminalType.Output);
        UpperDataStrobe = Pin(7, "UDS", TerminalType.Output);
        LowerDataStrobe = Pin(8, "LDS", TerminalType.Output);
        ReadWrite = Pin(9, "RW", TerminalType.Output);
        DataAcknowledge = Pin(10, "DTACK", TerminalType.Input);
        BusGrant = Pin(11, "BG", TerminalType.Output);
        BusGrantAcknowledge = Pin(12, "BGACK", TerminalType.Input);
        BusRequest = Pin(13, "BR", TerminalType.Input);
        Vcc = Pin(14, "VCC", TerminalType.Power);
        Clock = Pin(15, "CLK", TerminalType.Input);
        Gnd = Pin(16, "GND", TerminalType.Ground);
        Halt = Pin(17, "HALT", TerminalType.Bidirectional);
        Reset = Pin(18, "RESET", TerminalType.Bidirectional);
        ValidMemoryAddress = Pin(19, "VMA", TerminalType.Output);
        Enable = Pin(20, "E", TerminalType.Output);
        ValidPeripheralAddress = Pin(21, "VPA", TerminalType.Input);
        BusError = Pin(22, "BERR", TerminalType.Input);
        for (var i = 0; i < 3; i++) _ipl[i] = Pin(25 - i, $"IPL{i}", TerminalType.Input);
        for (var i = 0; i < 3; i++) _fc[i] = Pin(28 - i, $"FC{i}", TerminalType.Output);
        for (var i = 0; i < 20; i++) _address[i] = Pin(29 + i, $"A{i + 1}", TerminalType.Output);
        Pin(49, "VCC2", TerminalType.Power);
        for (var i = 20; i < 23; i++) _address[i] = Pin(30 + i, $"A{i + 1}", TerminalType.Output);
        Pin(53, "GND2", TerminalType.Ground);
        for (var i = 5; i < 16; i++) _data[i] = Pin(69 - i, $"D{i}", TerminalType.Bidirectional);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [Clock, DataAcknowledge, BusError, ValidPeripheralAddress, BusRequest, BusGrantAcknowledge, .. _ipl, Halt, Reset, .. _data],
            [.. _address, .. _data, .. _fc, AddressStrobe, UpperDataStrobe, LowerDataStrobe, ReadWrite, BusGrant, Enable, ValidMemoryAddress, Halt, Reset]);

        Core = NewCore();
    }

    public override string PartNumber => "MC68000";

    public override string ComponentType => "68000";

    public override bool LabelsPins => true;

    /// <summary>The processor inside: registers, flags, the prefetch queue and the cycle on offer.</summary>
    public M68000Core Core { get; private set; }

    /// <summary>A1-A23.</summary>
    public IReadOnlyList<Terminal> Address => _address;

    /// <summary>D0-D15. D8-D15 are the even byte (/UDS), D0-D7 the odd byte (/LDS).</summary>
    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>FC0-FC2.</summary>
    public IReadOnlyList<Terminal> FunctionCode => _fc;

    /// <summary>/IPL0-/IPL2: the interrupt level, active low.</summary>
    public IReadOnlyList<Terminal> InterruptLevel => _ipl;

    public Terminal Clock { get; }

    public Terminal AddressStrobe { get; }

    public Terminal UpperDataStrobe { get; }

    public Terminal LowerDataStrobe { get; }

    /// <summary>R/W: high to read, low to write.</summary>
    public Terminal ReadWrite { get; }

    /// <summary>/DTACK: the device's answer, ending the cycle. Until it comes, the processor waits.</summary>
    public Terminal DataAcknowledge { get; }

    public Terminal BusError { get; }

    public Terminal ValidPeripheralAddress { get; }

    public Terminal ValidMemoryAddress { get; }

    /// <summary>E: CLK ÷ 10, the 6800 peripherals' clock.</summary>
    public Terminal Enable { get; }

    public Terminal BusRequest { get; }

    public Terminal BusGrant { get; }

    public Terminal BusGrantAcknowledge { get; }

    /// <summary>/HALT: an input that stops the processor, and an open-drain output after a double bus fault.</summary>
    public Terminal Halt { get; }

    /// <summary>/RESET: with /HALT, resets the processor; the RESET instruction drives it low. Open drain.</summary>
    public Terminal Reset { get; }

    /// <summary>Clocks spent waiting for /DTACK since power-on, beyond the four each cycle takes.</summary>
    public long WaitStates { get; private set; }

    /// <summary>CLK cycles since power-on.</summary>
    public long ClockCycles { get; private set; }

    /// <summary>Bus cycles run since power-on.</summary>
    public long BusCycles { get; private set; }

    /// <summary>Read cycles that found an active data line at no logic level.</summary>
    public int UndefinedReads { get; private set; }

    /// <summary>The address of the first such read, or −1.</summary>
    public long FirstUndefinedRead { get; private set; } = -1;

    /// <summary>What is wrong with how this part is being run, if anything.</summary>
    public IReadOnlyList<string> Violations => UndefinedReads > 0
        ?
        [
            $"{UndefinedReads} read(s) found a data line at no logic level, first at ${FirstUndefinedRead:X6} — " +
            "two outputs fighting on the data bus, or nothing selected there at all",
        ]
        : [];

    public override string ValueLabel => Core.IsHalted
        ? "halted (double bus fault)"
        : Core.IsStopped
            ? $"stopped at ${Core.PC & 0xFFFFFF:X6}"
            : $"PC ${Core.InstructionAddress & 0xFFFFFF:X6} D0 ${Core.GetD(0):X8}";

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "AS" or "UDS" or "LDS" or "DTACK" or "BG" or "BGACK" or "BR" or "HALT" or "RESET" or "VMA" or "VPA" or "BERR"
            or "IPL0" or "IPL1" or "IPL2" => "/" + pin.Name,
        "RW" => "R/W",
        "VCC2" => "VCC",
        "GND2" => "GND",
        _ => pin.Name,
    };

    /// <summary>The bus log, oldest first: every cycle, and each instruction where it began.</summary>
    public IReadOnlyList<M68000BusCycle> BusLog
    {
        get
        {
            var list = new M68000BusCycle[_logCount];
            for (var i = 0; i < _logCount; i++) list[i] = _log[(_logStart + i) % BusLogCapacity];
            return list;
        }
    }

    /// <summary>The latest instruction finished, disassembled: <c>$000408 MOVE.B D0,(A0)</c>.</summary>
    public string CurrentInstruction
    {
        get
        {
            var words = Core.LastInstructionWords;
            if (words.Count == 0) return string.Empty;
            var (text, _) = M68000Disassembler.Disassemble(Core.LastInstructionAddress, a =>
            {
                var i = (int)((a - Core.LastInstructionAddress) / 2);
                return i >= 0 && i < words.Count ? words[i] : (ushort)0;
            });
            return string.Create(CultureInfo.InvariantCulture, $"${Core.LastInstructionAddress & 0xFFFFFF:X6} {text}");
        }
    }

    /// <summary>The registers on a line: <c>PC=000400 SR=2700 D0=00000000 ... A7=00010000</c>.</summary>
    public string Registers
    {
        get
        {
            var c = Core;
            var d = string.Join(" ", Enumerable.Range(0, 8).Select(i => string.Create(CultureInfo.InvariantCulture, $"D{i}={c.GetD(i):X8}")));
            var a = string.Join(" ", Enumerable.Range(0, 8).Select(i => string.Create(CultureInfo.InvariantCulture, $"A{i}={c.GetA(i):X8}")));
            return string.Create(CultureInfo.InvariantCulture, $"PC={c.PC & 0xFFFFFF:X6} SR={c.SR:X4} {d} {a}");
        }
    }

    /// <summary>SR as letters: T and S, the mask, then XNZVC — capitals set: <c>tS7xNzvc</c>.</summary>
    public static string Flags(ushort sr)
    {
        static char F(bool on, char c) => on ? c : char.ToLowerInvariant(c);
        return string.Create(CultureInfo.InvariantCulture,
            $"{F((sr & 0x8000) != 0, 'T')}{F((sr & 0x2000) != 0, 'S')}{(sr >> 8) & 7}{F((sr & 0x10) != 0, 'X')}{F((sr & 8) != 0, 'N')}{F((sr & 4) != 0, 'Z')}{F((sr & 2) != 0, 'V')}{F((sr & 1) != 0, 'C')}");
    }

    private M68000Core NewCore()
    {
        var core = new M68000Core();
        core.InstructionFinished += OnInstructionFinished;
        return core;
    }

    private double _now;

    private void OnInstructionFinished(uint address, ushort[] words) =>
        Record(new M68000BusCycle(_instructionStart, _now, address & 0xFFFFFF, words[0], M68000CycleKind.Instruction, 0, false, false, 0, words));

    // ---- the logic -------------------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var clock = context.ReadInput(Clock, Levels);
        var previous = _lastClock;
        _lastClock = clock;
        _now = context.Time;

        if (context.IsInitializing)
        {
            Idle(context, 0);
            Request(context, EIndex, LogicState.Low, 0);
            Request(context, BgIndex, LogicState.High, 0);
            Request(context, HaltIndex, LogicState.HighImpedance, 0);
            Request(context, ResetIndex, LogicState.HighImpedance, 0);
            for (var i = 0; i < 23; i++) Request(context, i, LogicState.Low, 0);
            for (var i = 0; i < 3; i++) Request(context, FcIndex + i, LogicState.Low, 0);
            return;
        }

        if (clock == previous || !clock.IsDriven() || !previous.IsDriven()) return;

        if (clock.IsHigh()) Rising(context);
        else Falling(context);
    }

    private bool Low(IDigitalContext context, Terminal pin) => context.ReadInput(pin, Levels).IsLow();

    /// <summary>CLK has risen: an even state begins, or an idle clock ends.</summary>
    private void Rising(IDigitalContext context)
    {
        ClockCycles++;
        var delay = PropagationDelay;

        if (_held) return;

        if (_state >= 0)
        {
            switch (_state)
            {
                case 1:
                    _state = 2;
                    Request(context, AsIndex, LogicState.Low, delay);
                    if (IsReadCycle) Strobes(context, delay);
                    else Request(context, RwIndex, LogicState.Low, delay);
                    return;

                case 3:
                    _state = 4;
                    if (!IsReadCycle) Strobes(context, delay);
                    return;

                case 5:
                    _state = 6;
                    return;

                case 9:
                    // The high half of a wait state; with /VPA, /VMA goes low while E is low.
                    if (_vpa && !_vma && _eCount < 6)
                    {
                        _vma = true;
                        Request(context, VmaIndex, LogicState.Low, delay);
                    }

                    _state = _vpa ? 9 : 4;
                    return;

                case 7:
                    EndCycle(context);
                    break;

                default:
                    return;
            }
        }
        else if (_idleRunning)
        {
            if (--_idle > 0) return;
            _idleRunning = false;
            if (_resetDriven)
            {
                _resetDriven = false;
                Request(context, ResetIndex, LogicState.HighImpedance, delay);
            }

            Core.Complete();
        }

        StartNext(context);
    }

    /// <summary>CLK has fallen: an odd state begins; DTACK and the interrupt lines are sampled; E steps.</summary>
    private void Falling(IDigitalContext context)
    {
        var delay = PropagationDelay;

        var eWasHigh = _eCount >= 6;
        _eCount = (_eCount + 1) % 10;
        var eHigh = _eCount >= 6;
        if (eHigh != eWasHigh) Request(context, EIndex, eHigh ? LogicState.High : LogicState.Low, delay);

        var level = (Low(context, _ipl[0]) ? 1 : 0) | (Low(context, _ipl[1]) ? 2 : 0) | (Low(context, _ipl[2]) ? 4 : 0);
        Core.InterruptLevel = level;

        // /RESET and /HALT low together: reset, abandoning whatever was going on.
        var reset = Low(context, Reset) && Low(context, Halt) && !_resetDriven;
        if (reset != _held)
        {
            _held = reset;
            Core.ResetAsserted = reset;
            if (reset)
            {
                _state = -1;
                _idleRunning = false;
                _vpa = _vma = _berr = _rerun = false;
                ReleaseBus(context, delay);
            }

            return;
        }

        if (_held || _state < 0) return;

        switch (_state)
        {
            case 0:
                _state = 1;
                DriveAddress(context, delay);
                break;

            case 2:
                _state = 3;
                if (!IsReadCycle)
                {
                    var value = Core.DataOut;
                    for (var i = 0; i < 16; i++)
                        Request(context, DataIndex + i, (value & (1 << i)) != 0 ? LogicState.High : LogicState.Low, delay);
                    _driving = true;
                }

                break;

            case 4:
                if (Low(context, DataAcknowledge))
                {
                    _state = 5;
                }
                else if (Low(context, BusError))
                {
                    _berr = true;
                    _rerun = Low(context, Halt);
                    _state = 5;
                }
                else
                {
                    if (Low(context, ValidPeripheralAddress)) _vpa = true;
                    _state = 9;
                    _waits++;
                    WaitStates++;
                }

                break;

            case 9 when _vpa:
                _waits++;
                WaitStates++;

                // A 6800 cycle ends as E falls with /VMA low: the data is taken then.
                if (_vma && eWasHigh && !eHigh) Latch(context, delay);
                break;

            case 6:
                Latch(context, delay);
                break;
        }
    }

    private bool IsReadCycle => Core.Action != M68000BusAction.Write;

    private void Strobes(IDigitalContext context, double delay)
    {
        if (Core.UpperStrobe) Request(context, UdsIndex, LogicState.Low, delay);
        if (Core.LowerStrobe) Request(context, LdsIndex, LogicState.Low, delay);
    }

    /// <summary>The falling edge into S7: a read's data is taken and the strobes let go.</summary>
    private void Latch(IDigitalContext context, double delay)
    {
        _state = 7;
        if (IsReadCycle) _latched = SampleData(context);
        Request(context, AsIndex, LogicState.High, delay);
        Request(context, UdsIndex, LogicState.High, delay);
        Request(context, LdsIndex, LogicState.High, delay);
    }

    private ushort SampleData(IDigitalContext context)
    {
        var value = 0;
        var undefined = false;
        for (var i = 0; i < 16; i++)
        {
            var active = i < 8 ? Core.LowerStrobe : Core.UpperStrobe;
            var level = context.ReadInput(_data[i], Levels);
            if (level.IsHigh()) value |= 1 << i;
            else if (active && !level.IsLow()) undefined = true;
        }

        // A /VPA autovector reads nothing that matters.
        if (undefined && !(_vpa && Core.Action == M68000BusAction.InterruptAcknowledge))
        {
            if (UndefinedReads == 0) FirstUndefinedRead = Core.Address & 0xFFFFFF;
            UndefinedReads++;
        }

        return (ushort)value;
    }

    /// <summary>The rising edge that ends S7: the cycle is logged and the processor told how it went.</summary>
    private void EndCycle(IDigitalContext context)
    {
        var delay = PropagationDelay;
        _state = -1;
        BusCycles++;

        if (_driving)
        {
            for (var i = 0; i < 16; i++) Request(context, DataIndex + i, LogicState.HighImpedance, delay);
            _driving = false;
        }

        Request(context, RwIndex, LogicState.High, delay);
        if (_vma)
        {
            Request(context, VmaIndex, LogicState.High, delay);
            _vma = false;
        }

        var action = Core.Action;
        var kind = action switch
        {
            M68000BusAction.Write => M68000CycleKind.Write,
            M68000BusAction.InterruptAcknowledge => M68000CycleKind.InterruptAcknowledge,
            _ => M68000CycleKind.Read,
        };
        var data = action == M68000BusAction.Write ? Core.DataOut : _latched;
        if (!Core.IsWord) data = (Core.Address & 1) == 0 ? (ushort)(data >> 8) : (ushort)(data & 0xFF);
        Record(new M68000BusCycle(_cycleStart, context.Time, Core.Address & 0xFFFFFF, data, kind, Core.FunctionCode,
            Core.UpperStrobe, Core.LowerStrobe, _waits));

        var vpa = _vpa;
        _vpa = false;

        if (_berr)
        {
            _berr = false;
            if (_rerun)
            {
                // /BERR with /HALT: the same cycle again once /HALT goes high.
                _rerun = false;
                return;
            }

            Core.BusError();
        }
        else if (action == M68000BusAction.InterruptAcknowledge && vpa)
        {
            Core.CompleteAutovector();
        }
        else
        {
            Core.Complete(_latched);
        }

        NoteInstruction();
    }

    private void NoteInstruction()
    {
        if (Core.Instructions == _instructionsSeen) return;
        _instructionsSeen = Core.Instructions;
        _instructionStart = _now;
        NotifyValueChanged();
    }

    /// <summary>At a rising edge between cycles: begins whatever the processor offers next.</summary>
    private void StartNext(IDigitalContext context)
    {
        var delay = PropagationDelay;

        for (var guard = 0; guard < 64; guard++)
        {
            // Another master wants the bus: grant it between cycles, and stay off until it is done.
            var request = Low(context, BusRequest);
            var acknowledge = Low(context, BusGrantAcknowledge);
            if (_granted || request)
            {
                if (!_granted)
                {
                    _granted = true;
                    Request(context, BgIndex, LogicState.Low, delay);
                    ReleaseBus(context, delay);
                    return;
                }

                if (acknowledge) Request(context, BgIndex, LogicState.High, delay);
                if (request || acknowledge) return;

                _granted = false;
                Request(context, BgIndex, LogicState.High, delay);
                Idle(context, delay);
            }

            // /HALT from outside stops it between cycles.
            if (Low(context, Halt) && !Core.IsHalted)
            {
                if (!_stalled)
                {
                    _stalled = true;
                    ReleaseBus(context, delay);
                }

                return;
            }

            if (_stalled)
            {
                _stalled = false;
                Idle(context, delay);
            }

            switch (Core.Action)
            {
                case M68000BusAction.Read or M68000BusAction.Write or M68000BusAction.InterruptAcknowledge:
                    _state = 0;
                    _waits = 0;
                    _cycleStart = context.Time;
                    var fc = Core.FunctionCode;
                    for (var i = 0; i < 3; i++)
                        Request(context, FcIndex + i, (fc & (1 << i)) != 0 ? LogicState.High : LogicState.Low, delay);
                    Idle(context, delay);
                    return;

                case M68000BusAction.ResetOut:
                    _resetDriven = true;
                    Request(context, ResetIndex, LogicState.Low, delay);
                    _idle = Core.IdleClocks;
                    _idleRunning = true;
                    return;

                case M68000BusAction.Halted:
                    Request(context, HaltIndex, LogicState.Low, delay);
                    _idle = 2;
                    _idleRunning = true;
                    return;

                case M68000BusAction.Boundary:
                    Core.Complete();
                    NoteInstruction();
                    continue;

                default:
                    if (Core.IdleClocks <= 0)
                    {
                        Core.Complete();
                        NoteInstruction();
                        continue;
                    }

                    _idle = Core.IdleClocks;
                    _idleRunning = true;
                    NoteInstruction();
                    return;
            }
        }
    }

    private void DriveAddress(IDigitalContext context, double delay)
    {
        var address = Core.Address;
        for (var i = 0; i < 23; i++)
            Request(context, i, (address & (2u << i)) != 0 ? LogicState.High : LogicState.Low, delay);
    }

    /// <summary>The bus idle: strobes high, R/W high, /VMA high, the data lines released.</summary>
    private void Idle(IDigitalContext context, double delay)
    {
        Request(context, AsIndex, LogicState.High, delay);
        Request(context, UdsIndex, LogicState.High, delay);
        Request(context, LdsIndex, LogicState.High, delay);
        Request(context, RwIndex, LogicState.High, delay);
        Request(context, VmaIndex, LogicState.High, delay);
        if (!_driving)
            for (var i = 0; i < 16; i++) Request(context, DataIndex + i, LogicState.HighImpedance, delay);
    }

    /// <summary>Off the bus: every line another master might drive, let go.</summary>
    private void ReleaseBus(IDigitalContext context, double delay)
    {
        for (var i = 0; i < 23; i++) Request(context, i, LogicState.HighImpedance, delay);
        for (var i = 0; i < 16; i++) Request(context, DataIndex + i, LogicState.HighImpedance, delay);
        for (var i = 0; i < 3; i++) Request(context, FcIndex + i, LogicState.HighImpedance, delay);
        foreach (var index in new[] { AsIndex, UdsIndex, LdsIndex, RwIndex, VmaIndex })
            Request(context, index, LogicState.HighImpedance, delay);
        _driving = false;
    }

    private void Record(M68000BusCycle cycle)
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

        Core = NewCore();
        _lastClock = LogicState.Unknown;
        _state = -1;
        _idle = 0;
        _idleRunning = false;
        _waits = 0;
        _berr = _rerun = _vpa = _vma = _driving = _resetDriven = false;
        _granted = _stalled = _held = false;
        _eCount = 0;
        _logStart = 0;
        _logCount = 0;
        _instructionsSeen = 0;
        _instructionStart = 0;
        UndefinedReads = 0;
        FirstUndefinedRead = -1;
        WaitStates = 0;
        ClockCycles = 0;
        BusCycles = 0;
    }

    private sealed record RunState(
        M68000Core Core, M68000BusCycle[] Log, int Start, int Count, int UndefinedReads, long FirstUndefinedRead,
        long WaitStates, long ClockCycles, long BusCycles);

    public object CaptureRunState() =>
        new RunState(Core, (M68000BusCycle[])_log.Clone(), _logStart, _logCount, UndefinedReads, FirstUndefinedRead,
            WaitStates, ClockCycles, BusCycles);

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;

        Core = run.Core;
        Array.Copy(run.Log, _log, BusLogCapacity);
        _logStart = run.Start;
        _logCount = run.Count;
        UndefinedReads = run.UndefinedReads;
        FirstUndefinedRead = run.FirstUndefinedRead;
        WaitStates = run.WaitStates;
        ClockCycles = run.ClockCycles;
        BusCycles = run.BusCycles;
        NotifyValueChanged();
    }
}
