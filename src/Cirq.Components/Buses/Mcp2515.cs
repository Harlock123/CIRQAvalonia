using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// The MCP2515 stand-alone CAN controller, in its DIP-18: SPI to the host, TXCAN and RXCAN to a transceiver (the CAN
/// Transceiver part). It speaks CAN 2.0 with standard 11-bit identifiers: frames built with their bit stuffing and
/// CRC-15, sent with arbitration (a node that reads back dominant where it sent recessive in the identifier stops and
/// tries again after), acknowledged in the ACK slot by every receiver whose CRC matched, and received through the masks
/// and filters into RXB0 and RXB1. Extended identifiers and error frames aren't modelled.
/// <para>
/// The SPI commands are the datasheet's: RESET (0xC0), READ (0x03), WRITE (0x02), READ RX BUFFER (0x90-0x96), LOAD TX
/// BUFFER (0x40-0x45), RTS (0x81, 0x82, 0x84), READ STATUS (0xA0), RX STATUS (0xB0) and BIT MODIFY (0x05). It powers up
/// in configuration mode; CNF1-CNF3 set the bit time from <see cref="ClockFrequency"/>: TQ = 2·(BRP + 1) / Fosc, a bit
/// 1 + (PRSEG + 1) + (PHSEG1 + 1) + (PHSEG2 + 1) TQ, sampled at the end of PHSEG1. CANCTRL's REQOP picks normal,
/// loopback (its own frames back to itself, nothing on TXCAN), listen-only or configuration. INT goes low while any
/// interrupt enabled in CANINTE is flagged in CANINTF.
/// </para>
/// </summary>
public sealed partial class Mcp2515 : DigitalIc, IBreakpointSource
{
    private enum Phase { Idle, Stuffed, CrcDelimiter, AckSlot, AckDelimiter, EndOfFrame, Intermission }

    private const int TxOut = 0, MisoOut = 1, IntOut = 2;
    private readonly byte[] _r = new byte[128];
    private readonly SpiShifter _spi = new();
    private int _command = -1, _address, _bytes, _mask;

    // The bus engine.
    private Phase _phase;
    private double _bitStart = double.NaN, _nextBit = double.NaN, _nextSample = double.NaN;
    private readonly List<int> _bits = [];
    private int _same, _lastBit = 1, _count;
    private bool _rxLast = true;
    private bool _transmitting;
    private int _txBuffer = -1;
    private List<int> _txStream = [];
    private int _txPos;
    private int _driven = 1;
    private bool _acking, _acked, _crcOk;

    public Mcp2515() : base(18)
    {
        PropagationDelay = 20e-9;
        var pins = new Terminal[19];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 18));
        TxCan = Pin(1, "TXCAN", TerminalType.Output);
        RxCan = Pin(2, "RXCAN", TerminalType.Input);
        Pin(3, "CLKOUT", TerminalType.Passive);
        Pin(4, "TX0RTS", TerminalType.Passive);
        Pin(5, "TX1RTS", TerminalType.Passive);
        Pin(6, "TX2RTS", TerminalType.Passive);
        Pin(7, "OSC2", TerminalType.Passive);
        Pin(8, "OSC1", TerminalType.Passive);
        Gnd = Pin(9, "VSS", TerminalType.Ground);
        Pin(10, "RX1BF", TerminalType.Passive);
        Pin(11, "RX0BF", TerminalType.Passive);
        Interrupt = Pin(12, "INT", TerminalType.Output);
        Clock = Pin(13, "SCK", TerminalType.Input);
        Mosi = Pin(14, "SI", TerminalType.Input);
        Miso = Pin(15, "SO", TerminalType.Output);
        ChipSelect = Pin(16, "CS", TerminalType.Input);
        Reset = Pin(17, "RESET", TerminalType.Input);
        Vcc = Pin(18, "VDD", TerminalType.Power);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([RxCan, Clock, Mosi, ChipSelect, Reset], [TxCan, Miso, Interrupt]);
        ResetRegisters();
    }

    public Terminal TxCan { get; }
    public Terminal RxCan { get; }
    public Terminal Interrupt { get; }
    public Terminal Clock { get; }
    public Terminal Mosi { get; }
    public Terminal Miso { get; }
    public Terminal ChipSelect { get; }
    public Terminal Reset { get; }

    /// <summary>The crystal, in hertz: 8 MHz on most modules, 16 MHz on some.</summary>
    [ObservableProperty]
    public partial double ClockFrequency { get; set; } = 8e6;

    public IReadOnlyList<byte> Registers => _r;

    public override string PartNumber => "MCP2515";

    public override bool LabelsPins => true;

    public override string PinLabel(Terminal pin) => pin.Name is "INT" or "CS" or "RESET" ? "/" + pin.Name : pin.Name;

    public string Mode => (_r[0x0E] >> 5) switch { 0 => "normal", 1 => "sleep", 2 => "loopback", 3 => "listen-only", _ => "configuration" };

    public override string ValueLabel => $"MCP2515 {Mode}{(BitRate > 0 && Mode != "configuration" ? $" {BitRate / 1000:0.#} kbit/s" : "")}";

    /// <summary>The bit rate CNF1-CNF3 set, in bits a second.</summary>
    public double BitRate => 1 / BitTime;

    private double Tq => 2.0 * ((_r[0x2A] & 0x3F) + 1) / Math.Max(ClockFrequency, 1);

    private int PhaseSeg1 => ((_r[0x29] >> 3) & 7) + 1;

    private int PropSeg => (_r[0x29] & 7) + 1;

    private int PhaseSeg2 => (_r[0x29] & 0x80) != 0 ? (_r[0x28] & 7) + 1 : Math.Max(PhaseSeg1, 2);

    private double BitTime => Tq * (1 + PropSeg + PhaseSeg1 + PhaseSeg2);

    private double SamplePoint => Tq * (1 + PropSeg + PhaseSeg1);

    /// <summary>A received standard frame: its identifier, its data, and whether it was a remote request.</summary>
    public (int Id, byte[] Data, bool Remote)? ReceiveBuffer(int n)
    {
        if ((_r[0x2C] & (1 << n)) == 0) return null;
        var at = n == 0 ? 0x60 : 0x70;
        var id = (_r[at + 1] << 3) | (_r[at + 2] >> 5);
        var dlc = Math.Min(_r[at + 5] & 0x0F, 8);
        return (id, [.. _r.Skip(at + 6).Take(dlc)], (_r[at + 2] & 0x10) != 0);
    }

    private void ResetRegisters()
    {
        Array.Clear(_r);
        _r[0x0E] = 0x80;
        _r[0x0F] = 0x87;
        _phase = Phase.Idle;
        (_transmitting, _txBuffer, _acking) = (false, -1, false);
        (_bitStart, _nextBit, _nextSample) = (double.NaN, double.NaN, double.NaN);
    }

    // ───── SPI ─────

    private int NextOut()
    {
        switch (_command)
        {
            case 0x03 when _bytes >= 2:
            case >= 0x90 and <= 0x96 when (_command & 1) == 0:
                return _r[_address++ & 0x7F];
            case 0xA0:
                return StatusByte();
            case 0xB0:
                var rx = _r[0x2C] & 3;
                return (rx << 6) | (_r[0x60] & 1);
            default:
                return 0xFF;
        }
    }

    private int StatusByte()
    {
        var f = _r[0x2C];
        return (f & 1) | (f & 2) | ((_r[0x30] & 8) >> 1) | ((f & 4) << 1) | ((_r[0x40] & 8) << 1) | ((f & 8) << 2) | ((_r[0x50] & 8) << 3) | ((f & 16) << 3);
    }

    private void Byte(int b)
    {
        _bytes++;
        if (_bytes == 1)
        {
            _command = b;
            switch (b)
            {
                case 0xC0: ResetRegisters(); break;
                case >= 0x90 and <= 0x96 when (b & 1) == 0: _address = 0x61 + ((b & 6) == 0 ? 0 : (b & 6) == 2 ? 5 : (b & 6) == 4 ? 0x10 : 0x15); break;
                case >= 0x40 and <= 0x45: _address = new[] { 0x31, 0x36, 0x41, 0x46, 0x51, 0x56 }[b - 0x40]; break;
                case 0x81 or 0x82 or 0x84 or 0x83 or 0x85 or 0x86 or 0x87:
                    for (var i = 0; i < 3; i++) if ((b & (1 << i)) != 0) Write(0x30 + (i * 0x10), (byte)(_r[0x30 + (i * 0x10)] | 8));
                    break;
            }

            return;
        }

        switch (_command)
        {
            case 0x03 when _bytes == 2: _address = b & 0x7F; break;
            case 0x02 when _bytes == 2: _address = b & 0x7F; break;
            case 0x02: Write(_address, (byte)b); _address = (_address + 1) & 0x7F; break;
            case >= 0x40 and <= 0x45: Write(_address, (byte)b); _address = (_address + 1) & 0x7F; break;
            case 0x05 when _bytes == 2: _address = b & 0x7F; break;
            case 0x05 when _bytes == 3: _mask = b; break;
            case 0x05 when _bytes == 4: Write(_address, (byte)((_r[_address] & ~_mask) | (b & _mask))); break;
        }
    }

    private void Write(int address, byte value)
    {
        var config = Mode == "configuration";
        switch (address & 0x7F)
        {
            case 0x0E or 0x1E or 0x2E or 0x3E or 0x4E or 0x5E or 0x6E or 0x7E: break;      // CANSTAT
            case 0x0F or 0x1F or 0x2F or 0x3F or 0x4F or 0x5F or 0x6F or 0x7F:
                _r[0x0F] = value;
                _r[0x0E] = (byte)((_r[0x0E] & 0x1F) | (value & 0xE0));
                break;
            case 0x28 or 0x29 or 0x2A when !config: break;
            case >= 0x00 and < 0x0E or >= 0x10 and < 0x1E or >= 0x20 and < 0x28 when !config: break;    // filters and masks
            case 0x30 or 0x40 or 0x50:
                var keep = _r[address] & 0x70;
                _r[address] = (byte)(keep | (value & 0x0B));
                if ((value & 8) == 0 && _txBuffer == (address - 0x30) >> 4 && !_transmitting) _txBuffer = -1;
                break;
            case 0x60: _r[0x60] = (byte)((_r[0x60] & 0x0F & ~0x04) | (value & 0x64)); break;
            case 0x70: _r[0x70] = (byte)((_r[0x70] & 0x0F) | (value & 0x60)); break;
            default: _r[address & 0x7F] = value; break;
        }

        NotifyValueChanged();
    }

    private void TransferEnded()
    {
        if (_command is 0x90 or 0x92) _r[0x2C] &= 0xFE;
        if (_command is 0x94 or 0x96) _r[0x2C] &= 0xFD;
        (_command, _bytes) = (-1, 0);
    }

    // ───── the bus ─────

    private static int Crc15(IEnumerable<int> bits)
    {
        var crc = 0;
        foreach (var bit in bits)
        {
            var next = bit ^ ((crc >> 14) & 1);
            crc = (crc << 1) & 0x7FFF;
            if (next != 0) crc ^= 0x4599;
        }

        return crc;
    }

    /// <summary>A standard data or remote frame from a TX buffer, SOF to CRC, stuffed.</summary>
    private List<int> Frame(int buffer)
    {
        var at = 0x30 + (buffer * 0x10);
        var id = (_r[at + 1] << 3) | (_r[at + 2] >> 5);
        var remote = (_r[at + 5] & 0x40) != 0;
        var dlc = _r[at + 5] & 0x0F;
        var bits = new List<int> { 0 };
        for (var i = 10; i >= 0; i--) bits.Add((id >> i) & 1);
        bits.Add(remote ? 1 : 0);
        bits.Add(0);
        bits.Add(0);
        for (var i = 3; i >= 0; i--) bits.Add((dlc >> i) & 1);
        if (!remote)
            for (var b = 0; b < Math.Min(dlc, 8); b++)
                for (var i = 7; i >= 0; i--) bits.Add((_r[at + 6 + b] >> i) & 1);
        var crc = Crc15(bits);
        for (var i = 14; i >= 0; i--) bits.Add((crc >> i) & 1);

        var stuffed = new List<int>();
        int same = 0, last = -1;
        foreach (var bit in bits)
        {
            stuffed.Add(bit);
            same = bit == last ? same + 1 : 1;
            last = bit;
            if (same == 5)
            {
                stuffed.Add(1 - bit);
                (same, last) = (1, 1 - bit);
            }
        }

        return stuffed;
    }

    private int PendingBuffer()
    {
        var best = -1;
        var priority = -1;
        for (var b = 2; b >= 0; b--)
        {
            var ctrl = _r[0x30 + (b * 0x10)];
            if ((ctrl & 8) != 0 && (ctrl & 3) > priority) (best, priority) = (b, ctrl & 3);
        }

        return best;
    }

    private bool Loopback => Mode == "loopback";

    private bool OnBus => Mode is "normal" or "loopback" or "listen-only";

    public double? NextBreakpointAfter(double time)
    {
        if (_phase == Phase.Idle) return null;
        var next = Math.Min(_nextBit, _nextSample);
        return next > time + 1e-12 ? next : time + 1e-9;
    }

    private void StartFrame(double t)
    {
        _phase = Phase.Stuffed;
        _bits.Clear();
        (_same, _lastBit, _count, _acked, _crcOk) = (0, -1, 0, false, false);
        _bitStart = t;
        _nextSample = t + SamplePoint;
        _nextBit = t + BitTime;
    }

    private void BitBegins()
    {
        _driven = 1;
        if (_transmitting && _phase == Phase.Stuffed && _txPos < _txStream.Count) _driven = _txStream[_txPos];
        if (_acking) _driven = 0;
        _txPos++;
    }

    private void Sampled(int bit)
    {
        switch (_phase)
        {
            case Phase.Stuffed:
                if (_same == 5)
                {
                    // A stuff bit: dropped, or if it isn't one the frame's broken.
                    if (bit == _lastBit) { Abort(); return; }
                    (_same, _lastBit) = (1, bit);
                    return;
                }

                _same = bit == _lastBit ? _same + 1 : 1;
                _lastBit = bit;
                var position = _bits.Count;
                _bits.Add(bit);
                if (_transmitting && _driven == 1 && bit == 0 && position <= 12)
                {
                    _transmitting = false;          // lost arbitration: carry on as a receiver
                }

                if (_bits.Count >= 19)
                {
                    var remote = _bits[12] == 1;
                    var dlc = (_bits[15] << 3) | (_bits[16] << 2) | (_bits[17] << 1) | _bits[18];
                    var length = 19 + (remote ? 0 : 8 * Math.Min(dlc, 8)) + 15;
                    if (_bits.Count == length)
                    {
                        var crc = _bits.Skip(length - 15).Aggregate(0, (v, b) => (v << 1) | b);
                        _crcOk = Crc15(_bits.Take(length - 15)) == crc;
                        _phase = Phase.CrcDelimiter;
                    }
                }

                break;
            case Phase.CrcDelimiter:
                _phase = Phase.AckSlot;
                break;
            case Phase.AckSlot:
                _acked = bit == 0;
                _phase = Phase.AckDelimiter;
                break;
            case Phase.AckDelimiter:
                (_phase, _count) = (Phase.EndOfFrame, 0);
                break;
            case Phase.EndOfFrame:
                if (++_count == 7)
                {
                    Complete();
                    (_phase, _count) = (Phase.Intermission, 0);
                }

                break;
            case Phase.Intermission:
                if (++_count == 3) _phase = Phase.Idle;
                break;
        }
    }

    private void Abort()
    {
        _transmitting = false;
        _acking = false;
        _phase = Phase.Idle;
    }

    private void Complete()
    {
        var wasTransmitting = _transmitting;
        if (Loopback) _acked = true;                    // its own receiver acknowledges it
        if (wasTransmitting && _txBuffer >= 0)
        {
            var at = 0x30 + (_txBuffer * 0x10);
            if (_acked)
            {
                _r[at] &= 0xA7;                         // TXREQ clear, TXERR clear
                _r[0x2C] |= (byte)(4 << _txBuffer);     // TXnIF
            }
            else
            {
                _r[at] |= 0x10;                         // TXERR, and try again unless one-shot
                if ((_r[0x0F] & 8) != 0) _r[at] &= 0xF7;
            }
        }

        if ((!wasTransmitting || Loopback) && _crcOk) Store();
        (_transmitting, _txBuffer) = (false, -1);
        NotifyValueChanged();
    }

    private bool Accepts(int id, int mask, int filter) => ((id ^ filter) & mask) == 0;

    private int Std(int at) => (_r[at] << 3) | (_r[at + 1] >> 5);

    private void Store()
    {
        var id = (_bits[1] << 10) | _bits.Skip(2).Take(10).Aggregate(0, (v, b) => (v << 1) | b);
        var remote = _bits[12] == 1;
        var dlc = (_bits[15] << 3) | (_bits[16] << 2) | (_bits[17] << 1) | _bits[18];
        var data = new byte[remote ? 0 : Math.Min(dlc, 8)];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)_bits.Skip(19 + (i * 8)).Take(8).Aggregate(0, (v, b) => (v << 1) | b);

        int? Hit(int buffer)
        {
            var ctrl = _r[buffer == 0 ? 0x60 : 0x70];
            if (((ctrl >> 5) & 3) == 3) return 0;
            var mask = Std(buffer == 0 ? 0x20 : 0x24);
            int[] filters = buffer == 0 ? [0x00, 0x04] : [0x08, 0x10, 0x14, 0x18];
            for (var f = 0; f < filters.Length; f++)
                if (Accepts(id, mask, Std(filters[f]))) return buffer == 0 ? f : f + 2;
            return null;
        }

        var target = -1;
        int filterHit = 0;
        if ((_r[0x2C] & 1) == 0 && Hit(0) is { } h0) (target, filterHit) = (0, h0);
        else if ((_r[0x2C] & 2) == 0 && ((_r[0x2C] & 1) == 0 ? Hit(1) : (_r[0x60] & 4) != 0 ? Hit(0) ?? Hit(1) : Hit(1)) is { } h1) (target, filterHit) = (1, h1);
        if (target < 0)
        {
            _r[0x2D] |= (byte)(0x40 << ((_r[0x2C] & 1) != 0 && (_r[0x2C] & 2) != 0 ? 1 : 0));    // RXnOVR
            return;
        }

        var at = target == 0 ? 0x60 : 0x70;
        _r[at] = (byte)((_r[at] & 0x60) | (remote ? 8 : 0) | (target == 0 ? filterHit & 1 : filterHit & 7));
        _r[at + 1] = (byte)(id >> 3);
        _r[at + 2] = (byte)(((id & 7) << 5) | (remote ? 0x10 : 0));
        _r[at + 3] = _r[at + 4] = 0;
        _r[at + 5] = (byte)dlc;
        for (var i = 0; i < 8; i++) _r[at + 6 + i] = i < data.Length ? data[i] : (byte)0;
        _r[0x2C] |= (byte)(1 << target);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var t = context.Time;
        if (context.ReadInput(Reset, Levels).IsLow()) ResetRegisters();

        // SPI.
        var selected = context.ReadInput(ChipSelect, Levels).IsLow();
        if (!selected && _spi.Selected) TransferEnded();
        if (_spi.Step(selected, context.ReadInput(Clock, Levels).IsHigh(), context.ReadInput(Mosi, Levels).IsHigh(), NextOut) is { } b) Byte(b);

        // The bus: RXCAN, or in loopback what it would have sent.
        int Rx() => Loopback ? _driven : context.ReadInput(RxCan, Levels).IsHigh() ? 1 : 0;
        var rx = Rx();
        if (OnBus)
        {
            if (_phase == Phase.Idle)
            {
                var pending = PendingBuffer();
                if (rx == 0 && _rxLast && !Loopback)
                {
                    StartFrame(t);                      // somebody else's start of frame: hard sync
                    _txPos = 0;
                    BitBegins();
                }
                else if (pending >= 0 && Mode != "listen-only")
                {
                    (_txBuffer, _txStream, _txPos, _transmitting) = (pending, Frame(pending), 0, true);
                    StartFrame(t);
                    BitBegins();
                }
            }
            else if (rx == 0 && _rxLast && !Loopback && Math.Abs(t - _bitStart) > 2 * Tq && t < _nextSample)
            {
                // A recessive-to-dominant edge where we didn't expect a bit to start: resynchronise on it.
                _bitStart = t;
                (_nextSample, _nextBit) = (t + SamplePoint, t + BitTime);
            }

            while (_phase != Phase.Idle && Math.Min(_nextSample, _nextBit) <= t + 1e-12)
            {
                if (_nextSample <= _nextBit)
                {
                    var bit = Loopback ? _driven : Rx();
                    _nextSample = double.PositiveInfinity;
                    Sampled(bit);
                    if (_phase == Phase.AckSlot && !_transmitting && _crcOk && Mode == "normal") _acking = true;
                }
                else
                {
                    if (_phase != Phase.AckSlot) _acking = false;
                    _bitStart = _nextBit;
                    _nextBit += BitTime;
                    _nextSample = _bitStart + SamplePoint;
                    if (_phase == Phase.Idle) break;
                    BitBegins();
                }
            }

            if (_phase == Phase.Idle) (_acking, _driven) = (false, 1);
        }

        _rxLast = Loopback ? true : context.ReadInput(RxCan, Levels).IsHigh();
        var drive = Loopback || !OnBus ? 1 : _driven;
        Request(context, TxOut, drive == 1 ? LogicState.High : LogicState.Low, 0);
        Request(context, MisoOut, selected ? (_spi.Miso ? LogicState.High : LogicState.Low) : LogicState.HighImpedance, DelayFor(context));
        Request(context, IntOut, (_r[0x2B] & _r[0x2C]) != 0 ? LogicState.Low : LogicState.High, DelayFor(context));
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        ResetRegisters();
        (_command, _bytes, _rxLast, _driven) = (-1, 0, true, 1);
    }
}
