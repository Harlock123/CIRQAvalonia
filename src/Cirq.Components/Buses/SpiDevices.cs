using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// The shifting half of an SPI slave in mode 0, MSB first: a bit in on each rising edge of SCK while selected, the next
/// bit out after each falling edge, a byte handed over every eight.
/// </summary>
internal sealed class SpiShifter
{
    private bool _lastSck;
    private int _in, _bits, _out;
    private bool _wantNext;

    public bool Selected { get; private set; }

    /// <summary>The level MISO should have now.</summary>
    public bool Miso { get; private set; }

    /// <summary>Steps on the pins as they now are; a whole byte in, when one has arrived. <paramref name="next"/> gives the next byte to send.</summary>
    public int? Step(bool selected, bool sck, bool mosi, Func<int> next)
    {
        int? received = null;
        if (selected && !Selected)
        {
            (_in, _bits, _wantNext) = (0, 0, false);
            _out = next() & 0xFF;
            Miso = (_out & 0x80) != 0;
        }

        Selected = selected;
        if (!selected)
        {
            _lastSck = sck;
            return null;
        }

        if (!_lastSck && sck)
        {
            _in = ((_in << 1) | (mosi ? 1 : 0)) & 0xFF;
            if (++_bits == 8)
            {
                received = _in;
                _bits = 0;
                _wantNext = true;   // asked for as SCK falls, once the caller has taken this byte in
            }
        }
        else if (_lastSck && !sck)
        {
            if (_wantNext) _out = next() & 0xFF;
            _wantNext = false;
            Miso = ((_out << _bits) & 0x80) != 0;
        }

        _lastSck = sck;
        return received;
    }
}

/// <summary>
/// A W25Q32 SPI NOR flash, 4 MB: the commands a sketch uses. 0x9F reads the JEDEC ID (EF 40 16), 0x05 the status
/// (BUSY in bit 0, write enable in bit 1), 0x06 and 0x04 set and clear write enable; 0x03 reads from a 24-bit address on
/// and on, 0x0B the same after a dummy byte; 0x02 programs up to a page (256 bytes, wrapping within it), only clearing
/// bits as flash does; 0x20, 0x52 and 0xD8 erase 4, 32 and 64 KB to 0xFF, and 0xC7 or 0x60 the whole chip. A program or
/// an erase takes effect as CS rises and keeps BUSY set for as long as the datasheet's typical time.
/// </summary>
public sealed partial class W25qFlash : DigitalIc
{
    public const int Size = 4 << 20;

    private readonly Dictionary<int, byte[]> _sectors = [];
    private readonly SpiShifter _spi = new();
    private readonly List<int> _frame = [];
    private bool _writeEnabled;
    private double _busyUntil;
    private double _now;
    private (int Address, List<byte> Bytes)? _program;
    private (int Address, int Length)? _erase;

    public W25qFlash() : base(8)
    {
        PropagationDelay = 7e-9;
        var pins = new Terminal[9];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 8));
        ChipSelect = Pin(1, "/CS", TerminalType.Input);
        Miso = Pin(2, "DO", TerminalType.Output);
        Pin(3, "/WP", TerminalType.Input);
        Gnd = Pin(4, "GND", TerminalType.Ground);
        Mosi = Pin(5, "DI", TerminalType.Input);
        Clock = Pin(6, "CLK", TerminalType.Input);
        Pin(7, "/HOLD", TerminalType.Input);
        Vcc = Pin(8, "VCC", TerminalType.Power);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([ChipSelect, Mosi, Clock], [Miso]);
    }

    public Terminal ChipSelect { get; }
    public Terminal Miso { get; }
    public Terminal Mosi { get; }
    public Terminal Clock { get; }

    public override string PartNumber => "W25Q32";

    public override string ComponentType => "SPI Flash";

    public override bool LabelsPins => true;

    public bool Busy => _now < _busyUntil;

    /// <summary>A byte as the flash holds it now.</summary>
    public byte Read(int address)
    {
        address &= Size - 1;
        return _sectors.TryGetValue(address >> 12, out var s) ? s[address & 0xFFF] : (byte)0xFF;
    }

    private byte[] Sector(int address)
    {
        if (!_sectors.TryGetValue(address >> 12, out var s)) _sectors[address >> 12] = s = Enumerable.Repeat((byte)0xFF, 4096).ToArray();
        return s;
    }

    private int Address() => (_frame[1] << 16) | (_frame[2] << 8) | _frame[3];

    private int NextOut()
    {
        if (_frame.Count == 0) return 0xFF;
        switch (_frame[0])
        {
            case 0x9F:
                return _frame.Count switch { 1 => 0xEF, 2 => 0x40, 3 => 0x16, _ => 0xFF };
            case 0x90 when _frame.Count >= 4:
                return (_frame.Count - 4) % 2 == 0 ? 0xEF : 0x15;
            case 0x05:
                return (Busy ? 1 : 0) | (_writeEnabled ? 2 : 0);
            case 0x03 when _frame.Count >= 4 && !Busy:
                return Read(Address() + _frame.Count - 4);
            case 0x0B when _frame.Count >= 5 && !Busy:
                return Read(Address() + _frame.Count - 5);
            default:
                return 0xFF;
        }
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        _now = context.Time;
        var selected = context.ReadInput(ChipSelect, Levels).IsLow();
        if (!selected && _spi.Selected) Finish();
        if (selected && !_spi.Selected) _frame.Clear();
        var received = _spi.Step(selected, context.ReadInput(Clock, Levels).IsHigh(), context.ReadInput(Mosi, Levels).IsHigh(), NextOut);
        if (received is { } b)
        {
            _frame.Add(b);
            if (_frame[0] == 0x02 && _frame.Count > 4)
            {
                _program ??= (Address(), []);
                _program.Value.Bytes.Add((byte)b);
            }
        }

        Request(context, 0, !selected ? LogicState.HighImpedance : _spi.Miso ? LogicState.High : LogicState.Low, DelayFor(context));
    }

    /// <summary>CS has gone high: the command, if it wants to, does its work now.</summary>
    private void Finish()
    {
        if (_frame.Count == 0) return;
        var command = _frame[0];
        switch (command)
        {
            case 0x06: _writeEnabled = true; break;
            case 0x04: _writeEnabled = false; break;
            case 0x02 when _writeEnabled && !Busy && _program is { } p:
                for (var i = 0; i < p.Bytes.Count; i++)
                {
                    var at = (p.Address & ~0xFF) | ((p.Address + i) & 0xFF);
                    Sector(at)[at & 0xFFF] &= p.Bytes[i];
                }

                _busyUntil = _now + 0.7e-3;
                _writeEnabled = false;
                break;
            case 0x20 or 0x52 or 0xD8 when _writeEnabled && !Busy && _frame.Count >= 4:
                var length = command switch { 0x20 => 4096, 0x52 => 32768, _ => 65536 };
                var start = Address() & ~(length - 1);
                for (var a = start; a < start + length; a += 4096) _sectors.Remove(a >> 12);
                _busyUntil = _now + (command == 0x20 ? 45e-3 : 150e-3);
                _writeEnabled = false;
                break;
            case 0xC7 or 0x60 when _writeEnabled && !Busy:
                _sectors.Clear();
                _busyUntil = _now + 50e-3;
                _writeEnabled = false;
                break;
        }

        _program = null;
        _erase = null;
        _frame.Clear();
        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _frame.Clear();
        _writeEnabled = false;
        _busyUntil = 0;
        _program = null;
    }
}

/// <summary>
/// An SD card on its SPI pins (a microSD breakout): the SPI-mode commands a library like Arduino's SD uses. CMD0 to go
/// idle, CMD8 (an SDHC card answers its voltage check), CMD55 and ACMD41 until it's ready (the second time asked), CMD58
/// for the OCR (block addressing), CMD16, CMD13, CMD17 to read a 512-byte block (0xFE then the data and a CRC) and
/// CMD24 to write one (0xFE from the host, then the data; the card answers 0x05 and is busy a moment). Blocks never
/// written read as zeros. The card's contents are held as it runs, not saved with the circuit.
/// </summary>
public sealed partial class SdCard : DigitalIc
{
    private readonly Dictionary<uint, byte[]> _blocks = [];
    private readonly SpiShifter _spi = new();
    private readonly Queue<byte> _reply = new();
    private readonly List<byte> _command = [];
    private bool _idle = true, _appCommand;
    private int _initTries;
    private uint? _writeBlock;
    private List<byte>? _writing;

    public SdCard() : base(6)
    {
        PropagationDelay = 10e-9;
        ChipSelect = new Terminal("cs", "CS", TerminalType.Input, new Point(-40, -25));
        Clock = new Terminal("sck", "SCK", TerminalType.Input, new Point(-40, -15));
        Mosi = new Terminal("mosi", "MOSI", TerminalType.Input, new Point(-40, -5));
        Miso = new Terminal("miso", "MISO", TerminalType.Output, new Point(-40, 5));
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-40, 15));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-40, 25));
        Terminals = [ChipSelect, Clock, Mosi, Miso, Vcc, Gnd];
        ConfigurePins([ChipSelect, Mosi, Clock], [Miso]);
    }

    public Terminal ChipSelect { get; }
    public Terminal Miso { get; }
    public Terminal Mosi { get; }
    public Terminal Clock { get; }

    public override string PartNumber => "microSD";

    public override string ComponentType => "SD Card";

    public override string DesignatorPrefix => "J";

    /// <summary>The card's size, in megabytes, as CSD would say.</summary>
    [ObservableProperty]
    public partial int CapacityMegabytes { get; set; } = 1024;

    public override string ValueLabel => $"microSD {CapacityMegabytes} MB{(_idle ? "" : ", ready")}";

    /// <summary>A block as the card holds it.</summary>
    public byte[] Block(uint number) => _blocks.TryGetValue(number, out var b) ? (byte[])b.Clone() : new byte[512];

    /// <summary>Puts a block on the card, as if it had been written on a computer.</summary>
    public void SetBlock(uint number, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var block = new byte[512];
        Array.Copy(data, block, Math.Min(512, data.Length));
        _blocks[number] = block;
    }

    private int NextOut() => _reply.Count > 0 ? _reply.Dequeue() : 0xFF;

    private void Received(byte b)
    {
        if (_writing is not null)
        {
            if (_writing.Count == 0 && b != 0xFE) return;   // waiting for the data token
            _writing.Add(b);
            if (_writing.Count < 1 + 512 + 2) return;
            _blocks[_writeBlock!.Value] = [.. _writing.Skip(1).Take(512)];
            _writing = null;
            _reply.Enqueue(0x05);
            for (var i = 0; i < 4; i++) _reply.Enqueue(0x00);   // busy while it writes
            NotifyValueChanged();
            return;
        }

        if (_command.Count == 0 && (b & 0xC0) != 0x40) return;
        _command.Add(b);
        if (_command.Count < 6) return;
        var index = _command[0] & 0x3F;
        var argument = (uint)((_command[1] << 24) | (_command[2] << 16) | (_command[3] << 8) | _command[4]);
        _command.Clear();
        _reply.Clear();
        _reply.Enqueue(0xFF);   // NCR: a byte before the answer
        var r1 = (byte)(_idle ? 0x01 : 0x00);
        var app = _appCommand;
        _appCommand = false;
        switch (index)
        {
            case 0:
                _idle = true;
                _initTries = 0;
                _reply.Enqueue(0x01);
                break;
            case 8:
                foreach (var x in new byte[] { r1, 0x00, 0x00, (byte)((argument >> 8) & 0x0F), (byte)argument }) _reply.Enqueue(x);
                break;
            case 55:
                _appCommand = true;
                _reply.Enqueue(r1);
                break;
            case 41 when app:
                if (++_initTries >= 2) _idle = false;
                _reply.Enqueue((byte)(_idle ? 0x01 : 0x00));
                break;
            case 58:
                foreach (var x in new byte[] { r1, (byte)(_idle ? 0x80 : 0xC0), 0xFF, 0x80, 0x00 }) _reply.Enqueue(x);
                break;
            case 16 or 59:
                _reply.Enqueue(r1);
                break;
            case 13:
                _reply.Enqueue(r1);
                _reply.Enqueue(0x00);
                break;
            case 17 when !_idle:
                _reply.Enqueue(0x00);
                _reply.Enqueue(0xFF);
                _reply.Enqueue(0xFE);
                foreach (var x in Block(argument)) _reply.Enqueue(x);
                _reply.Enqueue(0xFF);
                _reply.Enqueue(0xFF);
                break;
            case 24 when !_idle:
                _reply.Enqueue(0x00);
                _writeBlock = argument;
                _writing = [];
                break;
            default:
                _reply.Enqueue((byte)(r1 | 0x04));   // illegal command
                break;
        }
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var selected = context.ReadInput(ChipSelect, Levels).IsLow();
        if (_spi.Step(selected, context.ReadInput(Clock, Levels).IsHigh(), context.ReadInput(Mosi, Levels).IsHigh(), NextOut) is { } b)
            Received((byte)b);
        Request(context, 0, !selected ? LogicState.HighImpedance : _spi.Miso ? LogicState.High : LogicState.Low, DelayFor(context));
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _reply.Clear();
        _command.Clear();
        _idle = true;
        _appCommand = false;
        _initTries = 0;
        _writing = null;
    }
}

/// <summary>
/// An MCP4131 digital potentiometer, 10 kΩ in 129 steps, set over SPI: a 16-bit write (0x00 then the wiper, 0 to 128)
/// sets it, and the 8-bit increment (0x04) and decrement (0x08) commands step it. Between P0A and P0B is the whole
/// 10 kΩ; the wiper P0W sits along it at the setting, with its own 75 Ω.
/// </summary>
public sealed partial class Mcp4131 : DigitalIc
{
    private readonly SpiShifter _spi = new();
    private int? _pending;

    public Mcp4131() : base(8)
    {
        PropagationDelay = 0;
        var pins = new Terminal[9];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 8));
        ChipSelect = Pin(1, "/CS", TerminalType.Input);
        Clock = Pin(2, "SCK", TerminalType.Input);
        Mosi = Pin(3, "SDI", TerminalType.Input);
        Gnd = Pin(4, "VSS", TerminalType.Ground);
        TerminalB = Pin(5, "P0B", TerminalType.Passive);
        Wiper = Pin(6, "P0W", TerminalType.Passive);
        TerminalA = Pin(7, "P0A", TerminalType.Passive);
        Vcc = Pin(8, "VDD", TerminalType.Power);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([ChipSelect, Clock, Mosi], []);
    }

    public Terminal ChipSelect { get; }
    public Terminal Clock { get; }
    public Terminal Mosi { get; }
    public Terminal TerminalA { get; }
    public Terminal Wiper { get; }
    public Terminal TerminalB { get; }

    /// <summary>End to end, in ohms.</summary>
    [ObservableProperty]
    public partial double Resistance { get; set; } = 10e3;

    /// <summary>The wiper's step, 0 (at B) to 128 (at A). It powers up at mid-scale, 64.</summary>
    [ObservableProperty]
    public partial int Step { get; set; } = 64;

    public override string PartNumber => "MCP4131";

    public override string ComponentType => "Digital Potentiometer";

    public override string DesignatorPrefix => "U";

    public override bool LabelsPins => true;

    public override string ValueLabel => $"MCP4131 {Step}/128";

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);
        const double wiper = 75;
        var fraction = Math.Clamp(Step, 0, 128) / 128.0;
        system.StampResistor(system.Node(TerminalA), system.Node(Wiper), (Resistance * (1 - fraction)) + wiper);
        system.StampResistor(system.Node(Wiper), system.Node(TerminalB), (Resistance * fraction) + wiper);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var selected = context.ReadInput(ChipSelect, Levels).IsLow();
        if (!selected) _pending = null;
        if (_spi.Step(selected, context.ReadInput(Clock, Levels).IsHigh(), context.ReadInput(Mosi, Levels).IsHigh(), () => 0xFF) is not { } b) return;
        if (_pending is { } first)
        {
            _pending = null;
            Step = Math.Clamp(((first & 0x01) << 8) | b, 0, 128);
            return;
        }

        switch ((b >> 2) & 3)
        {
            case 0: _pending = b; break;             // a write: the data byte follows
            case 1: Step = Math.Min(128, Step + 1); break;
            case 2: Step = Math.Max(0, Step - 1); break;
        }
    }

    partial void OnStepChanged(int value) => NotifyValueChanged();
}

/// <summary>
/// An AD9833 waveform generator module (a DDS chip on a 25 MHz crystal): 16-bit words over SPI while FSYNC is low,
/// clocked in on SCLK's falling edges. A control word (top bits 00) sets RESET, B28 (two frequency words make a 28-bit
/// frequency), the frequency and phase register selects, and the output: sine, triangle (MODE) or square (OPBITEN, at
/// half the frequency without DIV2). Frequency words (01 for FREQ0, 10 for FREQ1) and phase words (11) follow. The
/// output is f = FREQ × 25 MHz / 2^28, from 0.038 V to 0.65 V for the sine and triangle, the rail for the square,
/// behind 200 Ω.
/// </summary>
public sealed partial class Ad9833 : DigitalIc
{
    public const double MasterClock = 25e6;

    private bool _lastClock = true;
    private bool _dataWhileHigh;       // SDATA as last seen with SCLK high: the bit a falling edge takes
    private bool _framing;
    private int _word, _bits;
    private readonly uint[] _frequency = new uint[2];
    private readonly int[] _phase = new int[2];
    private int _control = 0x0100;      // powered up in reset
    private bool _lsbNext = true;
    private double _phaseAt;           // the accumulator's phase, in turns, at _phaseTime
    private double _phaseTime;

    public Ad9833() : base(7)
    {
        PropagationDelay = 0;
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-40, -30));
        Gnd = new Terminal("dgnd", "DGND", TerminalType.Ground, new Point(-40, -20));
        Data = new Terminal("sdata", "SDATA", TerminalType.Input, new Point(-40, -10));
        Clock = new Terminal("sclk", "SCLK", TerminalType.Input, new Point(-40, 0));
        Sync = new Terminal("fsync", "FSYNC", TerminalType.Input, new Point(-40, 10));
        AnalogGround = new Terminal("agnd", "AGND", TerminalType.Ground, new Point(-40, 20));
        Output = new Terminal("vout", "VOUT", TerminalType.Output, new Point(40, 0));
        Terminals = [Vcc, Gnd, Data, Clock, Sync, AnalogGround, Output];
        ConfigurePins([Data, Clock, Sync], []);
    }

    public Terminal Data { get; }
    public Terminal Clock { get; }
    public Terminal Sync { get; }
    public Terminal AnalogGround { get; }
    public Terminal Output { get; }

    public override string PartNumber => "AD9833";

    public override string ComponentType => "Waveform Generator";

    public override string DesignatorPrefix => "U";

    public override int VoltageSourceCount => base.VoltageSourceCount + 1;

    /// <summary>The frequency being made, in hertz.</summary>
    public double Frequency => _frequency[(_control >> 11) & 1] * MasterClock / (1 << 28) / ((_control & 0x28) == 0x20 && (_control & 0x08) == 0 ? 2 : 1);

    public bool InReset => (_control & 0x0100) != 0;

    public string Shape => (_control & 0x20) != 0 ? "square" : (_control & 0x02) != 0 ? "triangle" : "sine";

    public override string ValueLabel => InReset ? "AD9833 (reset)" : $"AD9833 {Cirq.Core.Units.SiPrefix.Format(Frequency, "Hz")} {Shape}";

    private double Turns(double time) =>
        InReset ? 0 : _phaseAt + (_frequency[(_control >> 11) & 1] * MasterClock / (1 << 28) * (time - _phaseTime)) + (_phase[(_control >> 10) & 1] / 4096.0);

    /// <summary>The output at a time, in volts.</summary>
    public double VoltageAt(double time, double rail)
    {
        if ((_control & 0x00C0) == 0x00C0) return 0;                  // asleep
        var turns = Turns(time);
        var x = turns - Math.Floor(turns);
        if ((_control & 0x20) != 0)
        {
            var divided = (_control & 0x08) != 0 ? x : (turns / 2) - Math.Floor(turns / 2);
            return divided < 0.5 ? rail : 0;
        }

        var unit = (_control & 0x02) != 0 ? (x < 0.5 ? (4 * x) - 1 : 3 - (4 * x)) : Math.Sin(2 * Math.PI * x);
        return 0.344 + (0.306 * unit);
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);
        var rail = system.IterationVoltage(system.Node(Vcc)) - system.IterationVoltage(system.Node(Gnd));
        system.StampTheveninSource(system.Branch(this, base.VoltageSourceCount), system.Node(Output), system.Node(AnalogGround),
            VoltageAt(state.Time, Math.Max(0, rail)), 200);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var framing = context.ReadInput(Sync, Levels).IsLow();
        var clock = context.ReadInput(Clock, Levels).IsHigh();
        if (framing && !_framing) (_word, _bits) = (0, 0);
        _framing = framing;
        if (framing && _lastClock && !clock)
        {
            _word = ((_word << 1) | (_dataWhileHigh ? 1 : 0)) & 0xFFFF;
            if (++_bits == 16)
            {
                Word(_word, context.Time);
                _bits = 0;
            }
        }

        if (clock) _dataWhileHigh = context.ReadInput(Data, Levels).IsHigh();
        _lastClock = clock;
    }

    private void Word(int word, double time)
    {
        // Keep the phase continuous across a change: where it had got to, carried on at the new setting.
        _phaseAt = Turns(time) - (_phase[(_control >> 10) & 1] / 4096.0);
        _phaseTime = time;
        switch (word >> 14)
        {
            case 0:
                _control = word;
                _lsbNext = true;
                if ((word & 0x0100) != 0) _phaseAt = 0;
                break;
            case 1 or 2:
                var register = (word >> 15) & 1;
                var value = (uint)(word & 0x3FFF);
                if ((_control & 0x2000) != 0)
                {
                    _frequency[register] = _lsbNext ? (_frequency[register] & ~0x3FFFu) | value : (_frequency[register] & 0x3FFF) | (value << 14);
                    _lsbNext = !_lsbNext;
                }
                else if ((_control & 0x1000) != 0) _frequency[register] = (_frequency[register] & 0x3FFF) | (value << 14);
                else _frequency[register] = (_frequency[register] & ~0x3FFFu) | value;
                break;
            case 3:
                _phase[(word >> 13) & 1] = word & 0x0FFF;
                break;
        }

        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        (_control, _lsbNext, _phaseAt, _phaseTime, _bits, _framing) = (0x0100, true, 0, 0, 0, false);
        Array.Clear(_frequency);
        Array.Clear(_phase);
    }
}

/// <summary>
/// A MAX7219 driving an 8 × 8 LED matrix, the module everyone buys: 16-bit words clocked in on CLK's rising edges, MSB
/// first, latched as CS (LOAD) rises. The top four of the low twelve bits are the register: 1-8 the rows, 9 the decode
/// mode (0 for a matrix), 10 the intensity, 11 the scan limit, 12 shutdown (0 off, 1 on) and 15 display test; the low
/// eight the data, bit 7 the leftmost LED. DOUT gives out what falls off the end sixteen clocks later, to chain another.
/// </summary>
public sealed partial class Max7219Matrix : DigitalIc
{
    private readonly byte[] _rows = new byte[8];
    private int _shift;
    private bool _lastClock, _lastLoad = true;

    public Max7219Matrix() : base(5)
    {
        PropagationDelay = 0;
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-80, -40));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-80, -20));
        DataIn = new Terminal("din", "DIN", TerminalType.Input, new Point(-80, 0));
        Load = new Terminal("cs", "CS", TerminalType.Input, new Point(-80, 20));
        Clock = new Terminal("clk", "CLK", TerminalType.Input, new Point(-80, 40));
        DataOut = new Terminal("dout", "DOUT", TerminalType.Output, new Point(80, 0));
        Terminals = [Vcc, Gnd, DataIn, Load, Clock, DataOut];
        ConfigurePins([DataIn, Load, Clock], [DataOut]);
    }

    public Terminal DataIn { get; }
    public Terminal Load { get; }
    public Terminal Clock { get; }
    public Terminal DataOut { get; }

    public override string PartNumber => "MAX7219";

    public override string ComponentType => "LED Matrix";

    public override string DesignatorPrefix => "DS";

    public override string ValueLabel => Shutdown ? "MAX7219 8×8 (shut down)" : "MAX7219 8×8";

    public bool Shutdown { get; private set; } = true;

    public bool TestMode { get; private set; }

    public int Intensity { get; private set; }

    public int ScanLimit { get; private set; } = 7;

    /// <summary>Whether an LED is lit: row 0 at the top, column 0 at the left.</summary>
    public bool Lit(int row, int column) => TestMode || (!Shutdown && row <= ScanLimit && (_rows[row] & (0x80 >> column)) != 0);

    public IReadOnlyList<byte> Rows => _rows;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var clock = context.ReadInput(Clock, Levels).IsHigh();
        var load = context.ReadInput(Load, Levels).IsHigh();
        if (!_lastClock && clock)
            _shift = ((_shift << 1) | (context.ReadInput(DataIn, Levels).IsHigh() ? 1 : 0)) & 0xFFFF;
        if (!_lastLoad && load) Latch(_shift);
        (_lastClock, _lastLoad) = (clock, load);
        Request(context, 0, (_shift & 0x8000) != 0 ? LogicState.High : LogicState.Low, 10e-9);
    }

    private void Latch(int word)
    {
        var data = (byte)word;
        switch ((word >> 8) & 0x0F)
        {
            case >= 1 and <= 8 and var digit: _rows[digit - 1] = data; break;
            case 10: Intensity = data & 0x0F; break;
            case 11: ScanLimit = data & 0x07; break;
            case 12: Shutdown = (data & 1) == 0; break;
            case 15: TestMode = (data & 1) != 0; break;
        }

        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_rows);
        (_shift, _lastClock, _lastLoad, Shutdown, TestMode, Intensity, ScanLimit) = (0, false, true, true, false, 0, 7);
    }
}

/// <summary>
/// A TM1637 four-digit seven-segment display module: two wires, CLK and DIO, a START (DIO falling while CLK is high),
/// bytes least significant bit first, each acknowledged by the chip holding DIO low through a ninth clock, and a STOP.
/// 0x40 sets writing with the address stepping on (0x44 fixed); 0xC0 plus an address then the digits' segments (bit 0
/// segment a to bit 6 g, bit 7 of the second digit the colon); 0x88 plus a brightness of 0-7 turns the display on, 0x80
/// off.
/// </summary>
public sealed partial class Tm1637Display : DigitalIc
{
    private readonly byte[] _digits = new byte[6];
    private bool _lastClock = true, _lastData = true;
    private bool _receiving;
    private int _byte, _bits;
    private bool _ack;
    private bool _first;
    private int _address;
    private bool _fixed;

    public Tm1637Display() : base(4)
    {
        PropagationDelay = 0;
        Clock = new Terminal("clk", "CLK", TerminalType.Input, new Point(-60, -15));
        Dio = new Terminal("dio", "DIO", TerminalType.Bidirectional, new Point(-60, -5));
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-60, 5));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-60, 15));
        Terminals = [Clock, Dio, Vcc, Gnd];
        ConfigurePins([Clock, Dio], [Dio]);
    }

    public Terminal Clock { get; }
    public Terminal Dio { get; }

    public override string PartNumber => "TM1637";

    public override string ComponentType => "4-Digit Display";

    public override string DesignatorPrefix => "DS";

    public bool On { get; private set; }

    public int Brightness { get; private set; }

    /// <summary>A digit's segments, bit 0 segment a.</summary>
    public byte Segments(int digit) => _digits[digit];

    /// <summary>
    /// CLK and DIO as two lines of a <see cref="Digital.PatternGenerator"/>'s pattern, a step to a half bit: a start, each
    /// byte LSB first with a released ninth clock for the chip's ACK (so DIO wants a pull-up), and a stop, for each frame.
    /// </summary>
    public static string PatternFor(params byte[][] frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        var clk = new System.Text.StringBuilder("11");
        var dio = new System.Text.StringBuilder("11");
        void Step(char c, char d)
        {
            clk.Append(c);
            dio.Append(d);
        }

        foreach (var frame in frames)
        {
            Step('1', '0');                                  // start: DIO falls with CLK high
            foreach (var b in frame)
            {
                for (var i = 0; i < 8; i++)
                {
                    var bit = ((b >> i) & 1) != 0 ? '1' : '0';
                    Step('0', bit);
                    Step('1', bit);
                }

                Step('0', 'z');                              // the chip pulls DIO low for its ACK
                Step('1', 'z');
            }

            Step('0', '0');
            Step('1', '0');
            Step('1', '1');                                  // stop: DIO rises with CLK high
        }

        return clk + "\n" + dio;
    }

    public override string ValueLabel => On ? "TM1637 4 digits" : "TM1637 (off)";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var clock = context.ReadInput(Clock, Levels).IsHigh();
        var data = context.ReadInput(Dio, Levels).IsHigh() || _ack;   // our own acknowledge isn't the master's data

        if (clock && _lastClock && _lastData && !data && !_ack)
        {
            // START: a new burst; its first byte is a command.
            (_receiving, _byte, _bits, _first) = (true, 0, 0, true);
        }
        else if (clock && _lastClock && !_lastData && data && !_ack)
        {
            _receiving = false;   // STOP
        }
        else if (_receiving && !_lastClock && clock && !_ack)
        {
            if (context.ReadInput(Dio, Levels).IsHigh()) _byte |= 1 << _bits;
            _bits++;
        }
        else if (_receiving && _lastClock && !clock)
        {
            if (_ack)
            {
                _ack = false;
            }
            else if (_bits == 8)
            {
                Byte(_byte);
                (_byte, _bits, _ack) = (0, 0, true);
            }
        }

        (_lastClock, _lastData) = (clock, context.ReadInput(Dio, Levels).IsHigh());
        Request(context, 0, _ack ? LogicState.Low : LogicState.HighImpedance, 50e-9);
    }

    private void Byte(int value)
    {
        if (_first)
        {
            _first = false;
            switch (value & 0xC0)
            {
                case 0x40: _fixed = (value & 0x04) != 0; return;
                case 0xC0: _address = value & 0x07; return;
                case 0x80:
                    On = (value & 0x08) != 0;
                    Brightness = value & 0x07;
                    NotifyValueChanged();
                    return;
            }
        }

        if (_address < _digits.Length) _digits[_address] = (byte)value;
        if (!_fixed) _address++;
        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_digits);
        (_receiving, _ack, _address, _fixed, On, _lastClock, _lastData) = (false, false, 0, false, false, true, true);
    }
}
