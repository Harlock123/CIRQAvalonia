using System.Globalization;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// The Intel 8259A programmable interrupt controller: eight interrupt requests, IR0-IR7, ranked,
/// masked and put in service, answered on the processor's /INTA with a CALL to the request's own
/// handler (8080/8085 mode) or with a vector number (8086/8088 mode).
/// <para>
/// <b>The bus.</b> A0 picks one of two ports; see <see cref="IntelPeripheral"/> for the cycle.
/// Written with A0 low, a byte with D4 set is <b>ICW1</b> and starts the initialisation: D0 IC4 (an
/// ICW4 will follow), D1 SNGL (no other 8259A, so no ICW3), D2 ADI (the handlers 4 bytes apart when
/// set, 8 when clear), D3 LTIM (level-triggered requests), D7-D5 A7-A5 of the handlers' address.
/// ICW1 clears the mask, resets the edge sense of every input, gives IR7 the lowest priority, ends
/// special mask mode and points reads at the IRR. Then, with A0 high: <b>ICW2</b> (A15-A8 of the
/// handlers' address, or T7-T3 of the vector in 8086 mode), <b>ICW3</b> unless SNGL (on a master
/// which IRs have a slave, on a slave its ID), and <b>ICW4</b> if IC4 (D0 µPM — 1 for the 8086 —,
/// D1 AEOI, D2 M/S, D3 BUF, D4 SFNM; without it all are 0). Initialised, a write with A0 high is
/// <b>OCW1</b>, the interrupt mask (IMR), a 1 masking that IR; with A0 low and D4 D3 = 00 it is
/// <b>OCW2</b> — R SL EOI in D7-D5 and a level in D2-D0: 001 non-specific EOI, 011 specific EOI, 101
/// rotate on non-specific EOI, 111 rotate on specific EOI, 100 and 000 set and clear rotation in
/// automatic EOI mode, 110 set priority, 010 nothing — and with D4 D3 = 01 <b>OCW3</b>: D6 ESMM with
/// D5 SMM sets or ends special mask mode, D2 P is a poll, and D1 RR with D0 RIS points reads with A0
/// low at the ISR (RIS set) or the IRR. A read with A0 high gives the IMR.
/// </para>
/// <para>
/// <b>Requests.</b> Edge-triggered (LTIM clear), an IR's rising edge sets its IRR bit, which then
/// needs the line held high until the acknowledge; level-triggered, the IRR bit is the line. Each
/// request is ranked — IR0 highest, IR7 lowest, until a rotation moves the lowest — and INT goes
/// high when an unmasked request outranks everything in service (the ISR): fully nested, so a
/// handler can be interrupted by a higher level once it has done EI, never by its own or a lower.
/// In special mask mode only a level's own ISR bit and the mask hold it off. With SFNM on a master,
/// a level with a slave can interrupt again while in service, for the slave's higher requests.
/// </para>
/// <para>
/// <b>The acknowledge, 8085 mode.</b> The processor answers INT with three /INTA pulses. On the
/// first the 8259A chooses the highest request, sets its ISR bit, clears its IRR bit (and its edge),
/// and puts $CD — CALL — on the bus; on the second the handler's address's low byte (A7-A5 with the
/// IR number in A4-A2 at interval 4, A7-A6 with it in A5-A3 at interval 8); on the third the high
/// byte, ICW2. <b>8086 mode</b> is two pulses, the first driving nothing and the second the vector,
/// T7-T3 with the IR number in D2-D0. With AEOI the ISR bit is cleared as the last pulse ends;
/// otherwise the handler must send an EOI. A request gone by the first pulse is answered as IR7,
/// with no ISR bit set — the spurious interrupt the datasheet describes. A <b>poll</b> (OCW3 with
/// P) makes the next read give 1 0 0 0 0 W2 W1 W0 — bit 7 set and the level, if anything is
/// requesting — and acknowledges it as /INTA would.
/// </para>
/// <para>
/// <b>Cascading.</b> In non-buffered mode /SP/EN is an input: high a master, low a slave. A master
/// puts the number of an IR with a slave on CAS0-CAS2 from the first /INTA to the end of the last,
/// and leaves the address bytes (or the 8086 vector) to the slave, still sending the CALL itself; a
/// slave whose ID (ICW3) is on CAS0-CAS2 at the end of the first pulse puts its request in service
/// and answers the rest. In buffered mode (ICW4 BUF) /SP/EN is an output, low while the chip drives
/// the data bus, and ICW4's M/S says master or slave.
/// </para>
/// <para>
/// Simplifications: the edge sense is a latch set by the rising edge and cleared by the
/// acknowledge, with the line still needing to be high when the first /INTA comes; the choice is
/// frozen as the first /INTA falls (the chip freezes it during the pulse), and a slave decides it is
/// addressed as that pulse ends; special mask mode lets every unmasked level not itself in service
/// interrupt, and non-specific EOI in it clears the highest level in service as usual.
/// </para>
/// <para>
/// Pinout (DIP-28): 1 = /CS, 2 = /WR, 3 = /RD, 4 = D7, 5 = D6, 6 = D5, 7 = D4, 8 = D3, 9 = D2,
/// 10 = D1, 11 = D0, 12 = CAS0, 13 = CAS1, 14 = GND, 15 = CAS2, 16 = /SP/EN, 17 = INT, 18-25 =
/// IR0-IR7, 26 = /INTA, 27 = A0, 28 = +5V.
/// </para>
/// </summary>
public sealed class Pic8259 : IntelPeripheral, IRunState
{
    // Outputs: D0-D7 = 0-7, INT = 8, CAS0-CAS2 = 9-11, /SP/EN = 12.
    private const int IntIndex = 8, CasIndex = 9, SpEnIndex = 12;

    private readonly Terminal[] _requests = new Terminal[8];
    private readonly Terminal[] _cascade = new Terminal[3];
    private readonly bool[] _levels = new bool[8];

    private byte _icw1, _icw2, _icw3, _icw4;
    private int _expecting;         // the ICW the next A0-high write is: 2, 3, 4, or 0 when initialised
    private bool _initialised;
    private byte _edges;            // edge-sense latches
    private byte _isr, _imr;
    private int _lowest = 7;
    private bool _rotateInAeoi;
    private bool _specialMask;
    private bool _readIsr;
    private bool _pollNext;
    private byte? _pollWord;

    // The acknowledge in progress.
    private bool _intaWas;
    private int _pulses;
    private int _level = -1;        // the level the acknowledge chose (7 for a spurious one), or −1
    private bool _spurious;
    private bool _slaveCascade;     // a master: the level has a slave, which sends the address
    private bool _slaveAddressed;   // a slave: the master put this chip's ID on CAS

    public Pic8259() : base(28)
    {
        PropagationDelay = 50e-9;

        var pins = new Terminal[29];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, Digital.DipPackage.PinOffset(number, 28));

        ChipSelect = Pin(1, "CS", TerminalType.Input);
        WriteStrobe = Pin(2, "WR", TerminalType.Input);
        ReadStrobe = Pin(3, "RD", TerminalType.Input);
        for (var i = 0; i < 8; i++) SetDataPin(7 - i, Pin(4 + i, $"D{7 - i}", TerminalType.Bidirectional));
        _cascade[0] = Pin(12, "CAS0", TerminalType.Bidirectional);
        _cascade[1] = Pin(13, "CAS1", TerminalType.Bidirectional);
        Gnd = Pin(14, "GND", TerminalType.Ground);
        _cascade[2] = Pin(15, "CAS2", TerminalType.Bidirectional);
        SlaveProgram = Pin(16, "SPEN", TerminalType.Bidirectional);
        var interrupt = Pin(17, "INT", TerminalType.Output);
        for (var i = 0; i < 8; i++) _requests[i] = Pin(18 + i, $"IR{i}", TerminalType.Input);
        InterruptAcknowledge = Pin(26, "INTA", TerminalType.Input);
        Address0 = Pin(27, "A0", TerminalType.Input);
        Vcc = Pin(28, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([ChipSelect, WriteStrobe, ReadStrobe, Address0, InterruptAcknowledge, SlaveProgram, .. _requests, .. _cascade, .. Data],
            [.. Data, interrupt, .. _cascade, SlaveProgram]);
    }

    public override string PartNumber => "8259A";

    public override string ComponentType => "8259A PIC";

    /// <summary>A0: low for ICW1, OCW2, OCW3 and the IRR or ISR; high for the other ICWs, OCW1 and the IMR.</summary>
    public Terminal Address0 { get; }

    /// <summary>/INTA: the processor's interrupt acknowledge.</summary>
    public Terminal InterruptAcknowledge { get; }

    /// <summary>/SP/EN: in non-buffered mode an input, high for a master and low for a slave; in buffered mode an output, low while the chip drives the data bus.</summary>
    public Terminal SlaveProgram { get; }

    /// <summary>IR0-IR7, IR0 the highest priority until a rotation.</summary>
    public IReadOnlyList<Terminal> Requests => _requests;

    /// <summary>CAS0-CAS2: the cascade lines, out from a master, in to a slave.</summary>
    public IReadOnlyList<Terminal> Cascade => _cascade;

    /// <summary>INT: to the processor's INTR (or a master's IR).</summary>
    public Terminal Interrupt => OutputTerminals[IntIndex];

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "CS" or "WR" or "RD" or "INTA" => "/" + pin.Name,
        "SPEN" => "/SP/EN",
        "VCC" => "+5V",
        _ => pin.Name,
    };

    // ---- what a test or an agent wants to see -----------------------------------

    /// <summary>The interrupt request register: which IRs are requesting now.</summary>
    public byte Irr => ComputeIrr();

    /// <summary>The in-service register: which levels have been acknowledged and not yet ended.</summary>
    public byte Isr => _isr;

    /// <summary>The interrupt mask register (OCW1): a 1 masks that IR.</summary>
    public byte Imr => _imr;

    public byte Icw1 => _icw1;

    public byte Icw2 => _icw2;

    public byte Icw3 => _icw3;

    public byte Icw4 => _icw4;

    /// <summary>The initialisation sequence is complete.</summary>
    public bool Initialised => _initialised;

    /// <summary>The ICW the next write with A0 high will be (2, 3 or 4), or 0 once initialised.</summary>
    public int ExpectingIcw => _expecting;

    /// <summary>8086/8088 mode (ICW4 µPM) rather than 8080/8085.</summary>
    public bool Mode8086 => (_icw4 & 0x01) != 0;

    /// <summary>Automatic end of interrupt (ICW4 AEOI).</summary>
    public bool AutomaticEoi => (_icw4 & 0x02) != 0;

    /// <summary>Level-triggered requests (ICW1 LTIM) rather than edge-triggered.</summary>
    public bool LevelTriggered => (_icw1 & 0x08) != 0;

    /// <summary>The handlers 4 bytes apart (ICW1 ADI) rather than 8, in 8085 mode.</summary>
    public int CallInterval => (_icw1 & 0x04) != 0 ? 4 : 8;

    /// <summary>Single (ICW1 SNGL): no ICW3, no cascade.</summary>
    public bool Single => (_icw1 & 0x02) != 0;

    /// <summary>The chip is a cascade slave: /SP/EN low, or in buffered mode ICW4's M/S clear.</summary>
    public bool IsSlave { get; private set; }

    /// <summary>Special mask mode (OCW3).</summary>
    public bool SpecialMaskMode => _specialMask;

    /// <summary>Special fully nested mode (ICW4 SFNM).</summary>
    public bool SpecialFullyNested => (_icw4 & 0x10) != 0;

    /// <summary>Rotation on automatic EOI (OCW2 100).</summary>
    public bool RotateInAutomaticEoi => _rotateInAeoi;

    /// <summary>The lowest-priority level: 7 after ICW1, moved by rotation and set priority.</summary>
    public int LowestPriority => _lowest;

    /// <summary>What a read with A0 low gives: "IRR", "ISR", or "poll" when a poll command is waiting.</summary>
    public string ReadSelect => _pollNext ? "poll" : _readIsr ? "ISR" : "IRR";

    /// <summary>INT is high.</summary>
    public bool Interrupting { get; private set; }

    /// <summary>Acknowledges (and polls) served, spurious ones included.</summary>
    public int Acknowledges { get; private set; }

    /// <summary>Spurious acknowledges: no request left by the first /INTA, answered as IR7.</summary>
    public int Spurious { get; private set; }

    /// <summary>The address 8085 mode calls for <paramref name="level"/>: ICW2 over A7-A5 (or A7-A6) and the level.</summary>
    public ushort CallAddress(int level) => (ushort)((_icw2 << 8) | LowByte(level));

    /// <summary>The vector 8086 mode gives for <paramref name="level"/>: T7-T3 and the level.</summary>
    public byte Vector(int level) => (byte)((_icw2 & 0xF8) | (level & 7));

    public override string ValueLabel => _initialised
        ? string.Create(CultureInfo.InvariantCulture, $"IRR ${Irr:X2} ISR ${_isr:X2} IMR ${_imr:X2}")
        : PartNumber;

    // ---- the logic --------------------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        for (var i = 0; i < 8; i++)
        {
            var high = context.ReadInput(_requests[i], Levels).IsHigh();
            if (high && !_levels[i] && !context.IsInitializing) _edges |= (byte)(1 << i);
            _levels[i] = high;
        }

        var buffered = (_icw4 & 0x08) != 0;
        IsSlave = buffered ? (_icw4 & 0x04) == 0 : context.ReadInput(SlaveProgram, Levels).IsLow();

        var inta = context.ReadInput(InterruptAcknowledge, Levels).IsLow();
        if (!context.IsInitializing)
        {
            if (inta && !_intaWas) IntaFell(context);
            else if (!inta && _intaWas) IntaRose(context);
        }

        _intaWas = inta;

        EvaluateBus(context);
        Outputs(context, inta);
    }

    private byte ComputeIrr()
    {
        byte irr = 0;
        for (var i = 0; i < 8; i++)
        {
            if (!_levels[i]) continue;
            if (LevelTriggered || (_edges & (1 << i)) != 0) irr |= (byte)(1 << i);
        }

        return irr;
    }

    /// <summary>A level's rank, 0 the highest: the level after the lowest-priority one ranks first.</summary>
    private int Rank(int level) => (level - _lowest - 1 + 16) % 8;

    private bool HasSlave(int level) => !Single && !IsSlave && (_icw3 & (1 << level)) != 0;

    /// <summary>The request the chip would put in service now, or −1.</summary>
    private int Requesting()
    {
        if (!_initialised) return -1;
        var candidates = ComputeIrr() & ~_imr;
        var best = -1;
        for (var i = 0; i < 8; i++)
            if ((candidates & (1 << i)) != 0 && (best < 0 || Rank(i) < Rank(best))) best = i;
        if (best < 0) return -1;

        if (_specialMask) return (_isr & (1 << best)) != 0 ? NextUnserved(candidates) : best;

        // Fully nested: only something ranked above everything in service.
        var served = HighestInService();
        if (served < 0) return best;
        var allowed = Rank(best) < Rank(served) || (Rank(best) == Rank(served) && SpecialFullyNested && HasSlave(best));
        return allowed ? best : -1;
    }

    /// <summary>In special mask mode, the highest request whose own level is not in service.</summary>
    private int NextUnserved(int candidates)
    {
        var best = -1;
        for (var i = 0; i < 8; i++)
            if ((candidates & (1 << i)) != 0 && (_isr & (1 << i)) == 0 && (best < 0 || Rank(i) < Rank(best))) best = i;
        return best;
    }

    private int HighestInService()
    {
        var best = -1;
        for (var i = 0; i < 8; i++)
            if ((_isr & (1 << i)) != 0 && (best < 0 || Rank(i) < Rank(best))) best = i;
        return best;
    }

    /// <summary>Puts the level the chip would choose in service, as an acknowledge or a poll does; returns it, or −1 for none.</summary>
    private int Accept()
    {
        var level = Requesting();
        if (level < 0) return -1;
        _isr |= (byte)(1 << level);
        _edges &= (byte)~(1 << level);
        Acknowledges++;
        NotifyValueChanged();
        return level;
    }

    private void IntaFell(IDigitalContext context)
    {
        _pulses++;
        if (_pulses != 1) return;

        _slaveCascade = false;
        _slaveAddressed = false;
        _spurious = false;
        if (IsSlave)
        {
            _level = -1;        // a slave waits for its ID on CAS, at the end of this pulse
            return;
        }

        _level = Accept();
        if (_level < 0)
        {
            _level = 7;
            _spurious = true;
            Spurious++;
            Acknowledges++;
        }

        _slaveCascade = !_spurious && HasSlave(_level);
    }

    private void IntaRose(IDigitalContext context)
    {
        if (_pulses == 1 && IsSlave && !Single)
        {
            var id = 0;
            for (var i = 0; i < 3; i++)
                if (context.ReadInput(_cascade[i], Levels).IsHigh()) id |= 1 << i;
            if (id == (_icw3 & 7))
            {
                _slaveAddressed = true;
                _level = Accept();
                if (_level < 0)
                {
                    _level = 7;
                    _spurious = true;
                    Spurious++;
                }
            }
        }

        var last = Mode8086 ? 2 : 3;
        if (_pulses < last) return;

        // The sequence is over: automatic EOI ends the service now.
        var served = IsSlave ? _slaveAddressed : true;
        if (served && AutomaticEoi && !_spurious && _level >= 0)
        {
            _isr &= (byte)~(1 << _level);
            if (_rotateInAeoi) _lowest = _level;
        }

        _pulses = 0;
        _level = -1;
        _slaveCascade = false;
        _slaveAddressed = false;
        NotifyValueChanged();
    }

    /// <summary>The handler's address's low byte in 8085 mode.</summary>
    private byte LowByte(int level) => CallInterval == 4
        ? (byte)((_icw1 & 0xE0) | (level << 2))
        : (byte)((_icw1 & 0xC0) | (level << 3));

    /// <summary>What goes on D0-D7 during an /INTA pulse.</summary>
    private protected override byte? Drive(IDigitalContext context)
    {
        if (!context.ReadInput(InterruptAcknowledge, Levels).IsLow() || _pulses == 0) return null;

        if (IsSlave)
        {
            if (!_slaveAddressed || _level < 0) return null;
            return Mode8086
                ? _pulses == 2 ? Vector(_level) : null
                : _pulses switch { 2 => LowByte(_level), 3 => _icw2, _ => null };
        }

        if (_level < 0) return null;
        if (Mode8086) return _pulses == 2 && !_slaveCascade ? Vector(_level) : null;
        return _pulses switch
        {
            1 => 0xCD,
            2 when !_slaveCascade => LowByte(_level),
            3 when !_slaveCascade => _icw2,
            _ => null,
        };
    }

    private void Outputs(IDigitalContext context, bool inta)
    {
        var delay = DelayFor(context);
        var requesting = Requesting() >= 0;
        Interrupting = requesting;
        Request(context, IntIndex, requesting ? LogicState.High : LogicState.Low, delay);

        // CAS0-CAS2: a master drives them, the slave's level during an acknowledge and low otherwise.
        for (var i = 0; i < 3; i++)
        {
            var level = IsSlave ? LogicState.HighImpedance
                : _slaveCascade && _level >= 0 ? ((_level >> i) & 1) != 0 ? LogicState.High : LogicState.Low
                : LogicState.Low;
            Request(context, CasIndex + i, level, delay);
        }

        // Buffered, /SP/EN enables the bus's transceiver while the chip drives the data lines.
        var buffered = (_icw4 & 0x08) != 0;
        var driving = CurrentAccessReads || (inta && Drive(context) is not null);
        Request(context, SpEnIndex, buffered ? driving ? LogicState.Low : LogicState.High : LogicState.HighImpedance, delay);
    }

    // ---- the registers --------------------------------------------------------------

    private protected override int SelectedRegister(IDigitalContext context) =>
        context.ReadInput(Address0, Levels).IsHigh() ? 1 : 0;

    private protected override void ReadStarted(int register)
    {
        if (register != 0 || !_pollNext) return;

        // A poll: the read is the acknowledge.
        var level = Accept();
        _pollWord = level < 0 ? (byte)0 : (byte)(0x80 | level);
    }

    private protected override byte ReadRegister(int register)
    {
        if (register == 1) return _imr;
        if (_pollWord is { } poll) return poll;
        return _readIsr ? _isr : ComputeIrr();
    }

    private protected override void AfterRead(int register)
    {
        if (register != 0 || _pollWord is null) return;
        _pollWord = null;
        _pollNext = false;
    }

    private protected override void WriteRegister(int register, byte value)
    {
        if (register == 0)
        {
            if ((value & 0x10) != 0) InitialisationStart(value);
            else if ((value & 0x08) == 0) Command(value);
            else
            {
                // OCW3.
                if ((value & 0x40) != 0) _specialMask = (value & 0x20) != 0;
                if ((value & 0x04) != 0) _pollNext = true;
                if ((value & 0x02) != 0) _readIsr = (value & 0x01) != 0;
            }

            return;
        }

        switch (_expecting)
        {
            case 2:
                _icw2 = value;
                _expecting = Single ? (_icw1 & 0x01) != 0 ? 4 : 0 : 3;
                break;
            case 3:
                _icw3 = value;
                _expecting = (_icw1 & 0x01) != 0 ? 4 : 0;
                break;
            case 4:
                _icw4 = value;
                _expecting = 0;
                break;
            default:
                _imr = value;
                break;
        }

        if (_expecting == 0) _initialised = true;
    }

    /// <summary>ICW1: the mask cleared, the edges forgotten, IR7 lowest, special mask off, reads at the IRR.</summary>
    private void InitialisationStart(byte value)
    {
        _icw1 = value;
        _icw3 = 0;
        _icw4 = 0;
        _imr = 0;
        _edges = 0;
        _lowest = 7;
        _specialMask = false;
        _rotateInAeoi = false;
        _readIsr = false;
        _pollNext = false;
        _initialised = false;
        _expecting = 2;
    }

    /// <summary>OCW2: the EOIs, rotations and priority setting.</summary>
    private void Command(byte value)
    {
        var level = value & 7;
        switch (value >> 5)
        {
            case 1:     // non-specific EOI
                EndHighest(rotate: false);
                break;
            case 3:     // specific EOI
                _isr &= (byte)~(1 << level);
                break;
            case 5:     // rotate on non-specific EOI
                EndHighest(rotate: true);
                break;
            case 4:
                _rotateInAeoi = true;
                break;
            case 0:
                _rotateInAeoi = false;
                break;
            case 7:     // rotate on specific EOI
                _isr &= (byte)~(1 << level);
                _lowest = level;
                break;
            case 6:     // set priority
                _lowest = level;
                break;
        }
    }

    private void EndHighest(bool rotate)
    {
        var level = HighestInService();
        if (level < 0) return;
        _isr &= (byte)~(1 << level);
        if (rotate) _lowest = level;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_levels);
        _icw1 = _icw2 = _icw3 = _icw4 = 0;
        _expecting = 0;
        _initialised = false;
        _edges = _isr = _imr = 0;
        _lowest = 7;
        _rotateInAeoi = _specialMask = _readIsr = _pollNext = false;
        _pollWord = null;
        _intaWas = false;
        _pulses = 0;
        _level = -1;
        _spurious = _slaveCascade = _slaveAddressed = false;
        Acknowledges = Spurious = 0;
        Interrupting = false;
        IsSlave = false;
    }

    private sealed record RunState(byte Icw1, byte Icw2, byte Icw3, byte Icw4, int Expecting, bool Initialised, byte Edges, byte Isr, byte Imr,
        int Lowest, bool RotateInAeoi, bool SpecialMask, bool ReadIsr, bool PollNext, int Acknowledges, int Spurious);

    public object CaptureRunState() => new RunState(_icw1, _icw2, _icw3, _icw4, _expecting, _initialised, _edges, _isr, _imr,
        _lowest, _rotateInAeoi, _specialMask, _readIsr, _pollNext, Acknowledges, Spurious);

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;
        (_icw1, _icw2, _icw3, _icw4) = (run.Icw1, run.Icw2, run.Icw3, run.Icw4);
        _expecting = run.Expecting;
        _initialised = run.Initialised;
        _edges = run.Edges;
        _isr = run.Isr;
        _imr = run.Imr;
        _lowest = run.Lowest;
        _rotateInAeoi = run.RotateInAeoi;
        _specialMask = run.SpecialMask;
        _readIsr = run.ReadIsr;
        _pollNext = run.PollNext;
        Acknowledges = run.Acknowledges;
        Spurious = run.Spurious;
        NotifyValueChanged();
    }
}
