using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// The Intel 8155 and 8156: 256 bytes of static RAM, two 8-bit ports and a 6-bit one, and a 14-bit
/// timer, on the 8085's multiplexed bus — the <see cref="Ram8155"/> selected by /CE low, the
/// <see cref="Ram8156"/> by CE high. With an 8085 and an 8755A it was Intel's three-chip computer.
/// <para>
/// <b>The bus.</b> The chip has its own address latch: as ALE falls it keeps AD0-AD7, IO/M and the
/// chip enable. Then, if it was enabled, /RD low drives AD0-AD7 — the RAM byte at that address for
/// IO/M low, an I/O register for IO/M high — and a write's byte is taken off AD0-AD7 as /WR goes
/// high. The I/O registers are chosen by AD2-AD0: 000 the command register (written) and the status
/// register (read), 001 port A, 010 port B, 011 port C, 100 the timer's low byte, 101 its high six
/// bits with the timer mode in bits 7-6.
/// </para>
/// <list type="bullet">
/// <item><b>Command</b>: bit 0 port A an output (1) or an input, bit 1 port B the same, bits 3-2 port
/// C — 00 ALT1, all inputs; 11 ALT2, all outputs; 01 ALT3, port A strobed (PC0 A INTR, PC1 A BF,
/// PC2 /A STB) with PC3-PC5 outputs; 10 ALT4, both strobed (PC3 B INTR, PC4 B BF, PC5 /B STB) — bit 4
/// port A's interrupt enable, bit 5 port B's, and bits 7-6 the timer command: 00 nothing, 01 stop
/// (if running), 10 stop after the next terminal count, 11 start — load the count length and mode
/// and start at once, or, if the timer is running, at its next terminal count.</item>
/// <item><b>Status</b>: bit 0 A INTR, 1 A BF, 2 A INTE, 3 B INTR, 4 B BF, 5 B INTE, 6 TIMER — set at
/// each terminal count, cleared when the status is read and by RESET.</item>
/// </list>
/// <para>
/// <b>The ports.</b> An output port drives its pins from its latch and reads back the latch; an input
/// port lets its pins float and reads them. The output latch is cleared when a port becomes an input
/// and cannot be written while it is one, so a port turned to an output starts low. Strobed input:
/// /STB low loads the pins into the port's latch and raises BF; /STB rising raises INTR; reading the
/// port drops INTR as the read begins and BF as it ends. Strobed output: a write drops INTR and, as it
/// ends, raises BF; the peripheral's /STB low drops BF and its rising edge raises INTR. INTR reaches
/// its pin (and the status) only while the port's interrupt is enabled. Entering a strobed mode, BF
/// starts low and INTR low for an input, high for an output, as the datasheet sets them.
/// </para>
/// <para>
/// <b>The timer</b> counts rising edges on TIMER IN — usually the 8085's CLK OUT — down from a
/// length of 2 to $3FFF. /TIMER OUT is, by the mode bits M2 M1: 00 a single square wave, 01 a
/// continuous one (high for the first half of each count, one TIMER IN cycle longer when the count
/// is odd, low for the second); 10 a single low pulse one TIMER IN cycle long at the terminal count,
/// 11 the same pulse at every terminal count. RESET stops it and leaves the count length and mode.
/// </para>
/// <para>
/// <b>RESET</b> (active high) clears the command register — every port an input, port C ALT1 — the
/// output latches and the status, and stops the timer.
/// </para>
/// <para>
/// Simplifications: reading the timer registers gives the count still to go and the mode, where the
/// chip's down-counter counts by twos twice per cycle and needs the datasheet's arithmetic to
/// interpret; the ports' handshakes act on the strobe edges at once; and the timer's edges are taken
/// as they come, with no minimum high or low time checked.
/// </para>
/// </summary>
public abstract class RamIoTimer8155 : DigitalIc, IRunState
{
    // Outputs: AD0-AD7 = 0-7, PA0-PA7 = 8-15, PB0-PB7 = 16-23, PC0-PC5 = 24-29, /TIMER OUT = 30.
    private const int PortAIndex = 8, PortBIndex = 16, PortCIndex = 24, TimerOutIndex = 30;

    private readonly Terminal[] _ad = new Terminal[8];
    private readonly Terminal[] _portA = new Terminal[8];
    private readonly Terminal[] _portB = new Terminal[8];
    private readonly Terminal[] _portC = new Terminal[6];
    private readonly byte[] _ram = new byte[256];

    // The latch ALE fills.
    private byte _address;
    private bool _io;
    private bool _selected;
    private bool _aleWas;

    // A read or write in progress.
    private bool _accessWas;
    private bool _accessRead;
    private byte _accessData;

    private byte _command;
    private byte _outA, _outB, _outC;
    private byte _inA, _inB;
    private bool _intrA, _bfA, _intrB, _bfB;
    private bool _timerFlag;
    private bool _stbAWas = true, _stbBWas = true;

    // The timer.
    private byte _countLow, _countHigh;
    private bool _running;
    private int _length;
    private int _mode;
    private int _position;
    private bool _pendingStart;
    private bool _stopAfterCount;
    private bool _timerInWas;
    private bool _resetWas;

    private protected RamIoTimer8155(bool activeHighEnable) : base(40)
    {
        ActiveHighEnable = activeHighEnable;
        PropagationDelay = 50e-9;

        var pins = new Terminal[41];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 40));

        _portC[3] = Pin(1, "PC3", TerminalType.Bidirectional);
        _portC[4] = Pin(2, "PC4", TerminalType.Bidirectional);
        TimerIn = Pin(3, "TIMERIN", TerminalType.Input);
        Reset = Pin(4, "RESET", TerminalType.Input);
        _portC[5] = Pin(5, "PC5", TerminalType.Bidirectional);
        var timerOut = Pin(6, "TIMEROUT", TerminalType.Output);
        IoMemory = Pin(7, "IO/M", TerminalType.Input);
        ChipEnable = Pin(8, "CE", TerminalType.Input);
        ReadStrobe = Pin(9, "RD", TerminalType.Input);
        WriteStrobe = Pin(10, "WR", TerminalType.Input);
        AddressLatchEnable = Pin(11, "ALE", TerminalType.Input);
        for (var i = 0; i < 8; i++) _ad[i] = Pin(12 + i, $"AD{i}", TerminalType.Bidirectional);
        Gnd = Pin(20, "VSS", TerminalType.Ground);
        for (var i = 0; i < 8; i++) _portA[i] = Pin(21 + i, $"PA{i}", TerminalType.Bidirectional);
        for (var i = 0; i < 8; i++) _portB[i] = Pin(29 + i, $"PB{i}", TerminalType.Bidirectional);
        _portC[0] = Pin(37, "PC0", TerminalType.Bidirectional);
        _portC[1] = Pin(38, "PC1", TerminalType.Bidirectional);
        _portC[2] = Pin(39, "PC2", TerminalType.Bidirectional);
        Vcc = Pin(40, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([AddressLatchEnable, IoMemory, ChipEnable, ReadStrobe, WriteStrobe, Reset, TimerIn, .. _ad, .. _portA, .. _portB, .. _portC],
            [.. _ad, .. _portA, .. _portB, .. _portC, timerOut]);
    }

    /// <summary>True for the 8156: CE selects it high rather than low.</summary>
    public bool ActiveHighEnable { get; }

    public override bool LabelsPins => true;

    /// <summary>AD0-AD7: the address as ALE falls, then the data.</summary>
    public IReadOnlyList<Terminal> AddressData => _ad;

    public IReadOnlyList<Terminal> PortA => _portA;

    public IReadOnlyList<Terminal> PortB => _portB;

    /// <summary>PC0-PC5: a port, or the handshake lines of ports A and B.</summary>
    public IReadOnlyList<Terminal> PortC => _portC;

    public Terminal AddressLatchEnable { get; }

    /// <summary>IO/M: high for the I/O registers, low for the RAM.</summary>
    public Terminal IoMemory { get; }

    /// <summary>/CE on the 8155, CE on the 8156.</summary>
    public Terminal ChipEnable { get; }

    public Terminal ReadStrobe { get; }

    public Terminal WriteStrobe { get; }

    /// <summary>RESET: high resets the chip — from the 8085's RESET OUT.</summary>
    public Terminal Reset { get; }

    /// <summary>TIMER IN: the timer counts its rising edges.</summary>
    public Terminal TimerIn { get; }

    /// <summary>/TIMER OUT: the square wave or the low pulses.</summary>
    public Terminal TimerOut => OutputTerminals[TimerOutIndex];

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "CE" => ActiveHighEnable ? "CE" : "/CE",
        "RD" or "WR" => "/" + pin.Name,
        "TIMERIN" => "TIMER IN",
        "TIMEROUT" => "/TIMER OUT",
        "VCC" => "+5V",
        _ => pin.Name,
    };

    public override string ValueLabel => _running
        ? string.Create(CultureInfo.InvariantCulture, $"{PartNumber} timer {_length}")
        : PartNumber;

    // ---- what a test or an agent wants to see -----------------------------------

    /// <summary>The command register as last written (it cannot be read back on the chip).</summary>
    public byte Command => _command;

    /// <summary>The status register as a read would give it now, without clearing the TIMER bit.</summary>
    public byte Status => (byte)((AIntr ? 0x01 : 0) | (_bfA ? 0x02 : 0) | ((_command & 0x10) != 0 ? 0x04 : 0) |
                                 (BIntr ? 0x08 : 0) | (_bfB ? 0x10 : 0) | ((_command & 0x20) != 0 ? 0x20 : 0) | (_timerFlag ? 0x40 : 0));

    /// <summary>A RAM byte.</summary>
    public byte ReadByte(int address) => _ram[address & 0xFF];

    /// <summary>Writes a RAM byte, as a test or an agent would.</summary>
    public void WriteByte(int address, byte value) => _ram[address & 0xFF] = value;

    /// <summary>The output latches of ports A, B and C.</summary>
    public (byte A, byte B, byte C) OutputLatches => (_outA, _outB, _outC);

    /// <summary>The port C mode: 1 ALT1 (inputs), 2 ALT2 (outputs), 3 ALT3 (port A strobed), 4 ALT4 (both strobed).</summary>
    public int PortCAlternative => ((_command >> 2) & 3) switch { 0 => 1, 3 => 2, 1 => 3, _ => 4 };

    /// <summary>The count length and mode registers as written: 14 bits of length, M2 M1 in bits 15-14.</summary>
    public int CountRegister => (_countHigh << 8) | _countLow;

    /// <summary>The length the timer is counting, 2 to $3FFF.</summary>
    public int TimerLength => _length;

    /// <summary>The timer mode it is counting in: 0 single square wave, 1 continuous square wave, 2 single pulse, 3 continuous pulses.</summary>
    public int TimerMode => _mode;

    public bool TimerRunning => _running;

    /// <summary>TIMER IN cycles still to go to the next terminal count.</summary>
    public int TimerRemaining => _running ? _length - _position : 0;

    /// <summary>Terminal counts reached since the last reset of the simulation.</summary>
    public long TerminalCounts { get; private set; }

    private bool AIntr => _intrA && (_command & 0x10) != 0;

    private bool BIntr => _intrB && (_command & 0x20) != 0;

    private bool AOutput => (_command & 1) != 0;

    private bool BOutput => (_command & 2) != 0;

    private bool AStrobed => PortCAlternative >= 3;

    private bool BStrobed => PortCAlternative == 4;

    // ---- the logic --------------------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var reset = context.ReadInput(Reset, Levels).IsHigh();
        var ale = context.ReadInput(AddressLatchEnable, Levels).IsHigh();
        var rd = context.ReadInput(ReadStrobe, Levels).IsLow();
        var wr = context.ReadInput(WriteStrobe, Levels).IsLow();

        if (!context.IsInitializing)
        {
            if (reset && !_resetWas) ResetChip();
            _resetWas = reset;

            // The latch follows while ALE is high and keeps what it had as ALE falls.
            if (ale)
            {
                if (ReadAd(context) is { } address) _address = address;
                _io = context.ReadInput(IoMemory, Levels).IsHigh();
                var ce = context.ReadInput(ChipEnable, Levels);
                _selected = ActiveHighEnable ? ce.IsHigh() : ce.IsLow();
            }

            _aleWas = ale;

            if (!reset)
            {
                Access(context, rd, wr);
                Strobes(context);
                Timer(context);
            }
        }

        Drive(context, rd);
    }

    private void Access(IDigitalContext context, bool rd, bool wr)
    {
        var access = _selected && !_aleWas && (rd || wr);
        if (access)
        {
            if (!_accessWas)
            {
                _accessRead = rd;
                if (rd) ReadStarted();
                else WriteStarted();
            }

            if (wr && ReadAd(context) is { } value) _accessData = value;
        }
        else if (_accessWas)
        {
            if (_accessRead) ReadEnded();
            else Write(_accessData);
            NotifyValueChanged();
        }

        _accessWas = access;
    }

    private int Register => _address & 7;

    private void ReadStarted()
    {
        if (!_io) return;
        if (Register == 1 && AStrobed && !AOutput) _intrA = false;
        if (Register == 2 && BStrobed && !BOutput) _intrB = false;
    }

    private void ReadEnded()
    {
        if (!_io) return;
        switch (Register)
        {
            case 0:
                _timerFlag = false;
                break;
            case 1 when AStrobed && !AOutput:
                _bfA = false;
                break;
            case 2 when BStrobed && !BOutput:
                _bfB = false;
                break;
        }
    }

    private void WriteStarted()
    {
        if (!_io) return;
        if (Register == 1 && AStrobed && AOutput) _intrA = false;
        if (Register == 2 && BStrobed && BOutput) _intrB = false;
    }

    /// <summary>What a read gives: the RAM byte, or the I/O register.</summary>
    private byte ReadValue(IDigitalContext context)
    {
        if (!_io) return _ram[_address];
        return Register switch
        {
            0 => Status,
            1 => AOutput ? _outA : AStrobed ? _inA : PortPins(context, _portA),
            2 => BOutput ? _outB : BStrobed ? _inB : PortPins(context, _portB),
            3 => (byte)(PortPins(context, _portC) & 0x3F),
            4 => (byte)TimerReadback(),
            5 => (byte)(TimerReadback() >> 8),
            _ => 0xFF,
        };
    }

    private int TimerReadback() => _running ? (_mode << 14) | (TimerRemaining & 0x3FFF) : CountRegister;

    private void Write(byte value)
    {
        if (!_io)
        {
            _ram[_address] = value;
            return;
        }

        switch (Register)
        {
            case 0:
                WriteCommand(value);
                break;
            case 1:
                if (AOutput)
                {
                    _outA = value;
                    if (AStrobed) _bfA = true;
                }

                break;
            case 2:
                if (BOutput)
                {
                    _outB = value;
                    if (BStrobed) _bfB = true;
                }

                break;
            case 3:
                _outC = (byte)(value & 0x3F & PortCOutputMask);
                break;
            case 4:
                _countLow = value;
                break;
            case 5:
                _countHigh = value;
                break;
        }
    }

    /// <summary>The port C bits its latch drives: all in ALT2, PC3-PC5 in ALT3, none otherwise.</summary>
    private int PortCOutputMask => PortCAlternative switch { 2 => 0x3F, 3 => 0x38, _ => 0 };

    private void WriteCommand(byte value)
    {
        var old = _command;
        _command = value;

        // A port that becomes (or stays) an input has its output latch cleared.
        if (!AOutput) _outA = 0;
        if (!BOutput) _outB = 0;
        _outC = (byte)(_outC & PortCOutputMask);

        // Entering a strobed mode, or changing direction in one: BF low, INTR low for an input and high for an output.
        var oldAlt = ((old >> 2) & 3) switch { 0 => 1, 3 => 2, 1 => 3, _ => 4 };
        if (AStrobed && (oldAlt < 3 || ((old ^ value) & 1) != 0))
        {
            _bfA = false;
            _intrA = AOutput;
        }

        if (BStrobed && (oldAlt != 4 || ((old ^ value) & 2) != 0))
        {
            _bfB = false;
            _intrB = BOutput;
        }

        switch (value >> 6)
        {
            case 1:
                if (_running) StopTimer();
                break;
            case 2:
                if (_running) _stopAfterCount = true;
                break;
            case 3:
                if (_running) _pendingStart = true;
                else StartTimer();
                break;
        }
    }

    private void StartTimer()
    {
        _length = Math.Max(2, CountRegister & 0x3FFF);
        _mode = _countHigh >> 6;
        _position = 0;
        _running = true;
        _pendingStart = false;
        _stopAfterCount = false;
    }

    private void StopTimer()
    {
        _running = false;
        _pendingStart = false;
        _stopAfterCount = false;
        _position = 0;
    }

    /// <summary>The strobe inputs' edges, in ALT3 and ALT4.</summary>
    private void Strobes(IDigitalContext context)
    {
        if (AStrobed)
        {
            var stb = context.ReadInput(_portC[2], Levels).IsHigh();
            if (!stb && _stbAWas)
            {
                if (AOutput) _bfA = false;
                else
                {
                    _inA = PortPins(context, _portA);
                    _bfA = true;
                }

                NotifyValueChanged();
            }

            if (stb && !_stbAWas)
            {
                _intrA = true;
                NotifyValueChanged();
            }

            _stbAWas = stb;
        }
        else
        {
            _stbAWas = true;
        }

        if (BStrobed)
        {
            var stb = context.ReadInput(_portC[5], Levels).IsHigh();
            if (!stb && _stbBWas)
            {
                if (BOutput) _bfB = false;
                else
                {
                    _inB = PortPins(context, _portB);
                    _bfB = true;
                }

                NotifyValueChanged();
            }

            if (stb && !_stbBWas)
            {
                _intrB = true;
                NotifyValueChanged();
            }

            _stbBWas = stb;
        }
        else
        {
            _stbBWas = true;
        }
    }

    /// <summary>The timer counts each rising edge on TIMER IN.</summary>
    private void Timer(IDigitalContext context)
    {
        var timerIn = context.ReadInput(TimerIn, Levels).IsHigh();
        var rose = timerIn && !_timerInWas;
        _timerInWas = timerIn;
        if (!rose || !_running) return;

        _position++;
        if (_position < _length) return;

        // The terminal count.
        _timerFlag = true;
        TerminalCounts++;
        if (_pendingStart) StartTimer();
        else if (_stopAfterCount || (_mode & 1) == 0) StopTimer();
        else _position = 0;
        NotifyValueChanged();
    }

    /// <summary>/TIMER OUT's level: high while stopped; the square wave's high half first; the pulse low for the last cycle.</summary>
    private bool TimerOutHigh => !_running || ((_mode & 2) == 0
        ? _position < (_length + 1) / 2
        : _position != _length - 1);

    private void Drive(IDigitalContext context, bool rd)
    {
        var delay = DelayFor(context);
        LogicState Level(bool high) => high ? LogicState.High : LogicState.Low;

        byte? drive = _accessWas && _accessRead && rd ? ReadValue(context) : null;
        for (var i = 0; i < 8; i++)
            Request(context, i, drive is { } d ? Level(((d >> i) & 1) != 0) : LogicState.HighImpedance, delay);

        for (var i = 0; i < 8; i++)
        {
            Request(context, PortAIndex + i, AOutput ? Level(((_outA >> i) & 1) != 0) : LogicState.HighImpedance, delay);
            Request(context, PortBIndex + i, BOutput ? Level(((_outB >> i) & 1) != 0) : LogicState.HighImpedance, delay);
        }

        var alt = PortCAlternative;
        for (var i = 0; i < 6; i++)
        {
            LogicState level;
            if (alt >= 3 && i < 3 || alt == 4 && i >= 3)
            {
                var a = i < 3;
                level = (i % 3) switch
                {
                    0 => Level(a ? AIntr : BIntr),
                    1 => Level(a ? _bfA : _bfB),
                    _ => LogicState.HighImpedance,          // /STB, an input
                };
            }
            else
            {
                level = (PortCOutputMask & (1 << i)) != 0 ? Level(((_outC >> i) & 1) != 0) : LogicState.HighImpedance;
            }

            Request(context, PortCIndex + i, level, delay);
        }

        Request(context, TimerOutIndex, Level(TimerOutHigh), delay);
    }

    private byte? ReadAd(IDigitalContext context)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
        {
            var level = context.ReadInput(_ad[i], Levels);
            if (level.IsHigh()) value |= 1 << i;
            else if (!level.IsLow()) return null;
        }

        return (byte)value;
    }

    /// <summary>A port's pins as they stand, a floating one read as 1.</summary>
    private byte PortPins(IDigitalContext context, IReadOnlyList<Terminal> pins)
    {
        var value = 0;
        for (var i = 0; i < pins.Count; i++)
            if (!context.ReadInput(pins[i], Levels).IsLow()) value |= 1 << i;
        return (byte)value;
    }

    private void ResetChip()
    {
        _command = 0;
        _outA = _outB = _outC = 0;
        _intrA = _bfA = _intrB = _bfB = false;
        _timerFlag = false;
        StopTimer();
        _accessWas = false;
        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        ResetChip();
        _countLow = _countHigh = 0;
        _length = 2;
        _mode = 0;
        _address = 0;
        _io = _selected = _aleWas = false;
        _stbAWas = _stbBWas = true;
        _inA = _inB = 0;
        _timerInWas = _resetWas = false;
        TerminalCounts = 0;
        Array.Clear(_ram);
    }

    private sealed record RunState(byte[] Ram, byte Command, byte OutA, byte OutB, byte OutC, byte InA, byte InB, bool[] Flags,
        byte CountLow, byte CountHigh, bool Running, int Length, int Mode, int Position, bool Pending, bool StopAfter, long TerminalCounts);

    public object CaptureRunState() =>
        new RunState([.. _ram], _command, _outA, _outB, _outC, _inA, _inB, [_intrA, _bfA, _intrB, _bfB, _timerFlag],
            _countLow, _countHigh, _running, _length, _mode, _position, _pendingStart, _stopAfterCount, TerminalCounts);

    public void AdoptRunState(object state)
    {
        if (state is not RunState s) return;
        Array.Copy(s.Ram, _ram, 256);
        _command = s.Command;
        (_outA, _outB, _outC, _inA, _inB) = (s.OutA, s.OutB, s.OutC, s.InA, s.InB);
        (_intrA, _bfA, _intrB, _bfB, _timerFlag) = (s.Flags[0], s.Flags[1], s.Flags[2], s.Flags[3], s.Flags[4]);
        (_countLow, _countHigh, _running, _length, _mode, _position) = (s.CountLow, s.CountHigh, s.Running, s.Length, s.Mode, s.Position);
        (_pendingStart, _stopAfterCount, TerminalCounts) = (s.Pending, s.StopAfter, s.TerminalCounts);
        NotifyValueChanged();
    }
}

/// <summary>
/// The Intel 8155 (8155H): 256 bytes of RAM, ports A, B and C and a 14-bit timer for the 8085,
/// selected by /CE low — see <see cref="RamIoTimer8155"/>.
/// <para>
/// Pinout (DIP-40): 1 = PC3, 2 = PC4, 3 = TIMER IN, 4 = RESET, 5 = PC5, 6 = /TIMER OUT, 7 = IO/M,
/// 8 = /CE, 9 = /RD, 10 = /WR, 11 = ALE, 12-19 = AD0-AD7, 20 = VSS, 21-28 = PA0-PA7, 29-36 = PB0-PB7,
/// 37 = PC0, 38 = PC1, 39 = PC2, 40 = VCC.
/// </para>
/// </summary>
public sealed class Ram8155() : RamIoTimer8155(activeHighEnable: false)
{
    public override string PartNumber => "8155";
}

/// <summary>
/// The Intel 8156 (8156H): the 8155 with its chip enable active high, CE on pin 8 — see
/// <see cref="RamIoTimer8155"/>.
/// <para>
/// Pinout (DIP-40): as the 8155's, with 8 = CE.
/// </para>
/// </summary>
public sealed class Ram8156() : RamIoTimer8155(activeHighEnable: true)
{
    public override string PartNumber => "8156";
}
