using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// The Motorola MC6850 Asynchronous Communications Interface Adapter: a UART for a 6800-family bus,
/// with its transmitter and receiver clocked from pins of their own — the serial port of most 6809
/// boards.
/// <para>
/// <b>The bus.</b> Selected while CS0 and CS1 are high and /CS2 low. RS low is the control register
/// to write and the status register to read; RS high the transmit data register to write and the
/// receive data register to read. A read drives the data lines while E is high; reads and writes
/// take effect as E falls.
/// </para>
/// <list type="bullet">
/// <item><b>Control</b>: bits 1-0 the counter divide — ÷1, ÷16, ÷64, or 11 a <b>master reset</b>;
/// bits 4-2 the word: 7 bits even, 7 odd, each with 2 stop bits; 7 even, 7 odd with 1; 8 bits and
/// no parity with 2 or 1; 8 even and 8 odd with 1. Bits 6-5 the transmitter: 00 /RTS low,
/// transmit interrupt off; 01 /RTS low, on; 10 /RTS high, off; 11 /RTS low, off, and a break —
/// TxData held at space. Bit 7 enables the receive interrupt (a byte, an overrun, or the carrier
/// lost).</item>
/// <item><b>Status</b>: bit 0 RDRF (a byte is waiting), 1 TDRE (room to send — held low while /CTS is
/// high), 2 DCD (the carrier was lost), 3 CTS (the /CTS pin), 4 FE (the byte's stop bit was low),
/// 5 OVRN (bytes were lost), 6 PE (the byte's parity was wrong), 7 IRQ (/IRQ is low).</item>
/// </list>
/// <para>
/// <b>The documented clearing sequences.</b> Reading the receive data register clears RDRF, FE and
/// PE. An overrun — a byte finished while RDRF was still set — loses the new byte and does not show
/// at once: the next read of the data register hands over the byte that was waiting and sets OVRN,
/// RDRF staying set; the read after that clears both. /DCD going high (the carrier lost) sets DCD,
/// interrupts if the receive interrupt is on, and holds the receiver in reset; reading the status
/// register and then the data register clears it once /DCD is low again (while /DCD stays high the
/// bit follows it). Writing the transmit data register clears TDRE. A master reset clears the
/// status, empties both halves and stops the transmitter; at power-on the chip is held in that reset
/// until a program writes one, as the datasheet requires before it is used.
/// </para>
/// <para>
/// <b>The clocks.</b> TxCLK and RxCLK clock the transmitter and receiver at 1, 16 or 64 times the
/// bit rate; on a 6809 board they are often both E. The part measures each clock's period from its
/// rising edges and times the bits from it, so the rate follows whatever drives the pin; the
/// receiver samples the middle of each bit, as at ÷16 and ÷64. Both serial pins are ordinary logic,
/// so TxData goes to a Serial Terminal's RX and the terminal's TX to RxData, and the UART bus decode
/// reads them.
/// </para>
/// <para>
/// Simplifications: at ÷1 the receiver also samples mid-bit, where the chip needs RxCLK
/// synchronised to the data and samples on its rising edge; the start bit begins at the next time
/// point after the data register is loaded rather than on a TxCLK edge; and timing finer than a
/// bit is not modelled.
/// </para>
/// <para>
/// Pinout (DIP-24): 1 = VSS, 2 = RxData, 3 = RxCLK, 4 = TxCLK, 5 = /RTS, 6 = TxData, 7 = /IRQ,
/// 8 = CS0, 9 = /CS2, 10 = CS1, 11 = RS, 12 = VCC, 13 = R/W, 14 = E, 15-22 = D7-D0, 23 = /DCD,
/// 24 = /CTS.
/// </para>
/// </summary>
public sealed class Acia6850 : DigitalIc, IBreakpointSource, IRunState
{
    // Outputs: D0-D7 = 0-7, TxData = 8, /RTS = 9, /IRQ = 10.
    private const int TxIndex = 8, RtsIndex = 9, IrqIndex = 10;

    private readonly Terminal[] _data = new Terminal[8];
    private readonly SerialLine _line = new();

    private byte _control;
    private bool _held = true;
    private byte _receive;
    private int? _transmit;
    private bool _rdrf, _framing, _parity, _overrun, _overrunPending, _dcd, _statusReadWithDcd, _dcdFollows;
    private bool _lastDcdPin;

    private LogicState _lastE = LogicState.Unknown;
    private bool _lastTxClock, _lastRxClock;
    private double _lastTxEdge = double.NaN, _lastRxEdge = double.NaN;
    private double _txPeriod, _rxPeriod;

    public Acia6850() : base(24)
    {
        PropagationDelay = 50e-9;

        var pins = new Terminal[25];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 24));

        Gnd = Pin(1, "VSS", TerminalType.Ground);
        Receive = Pin(2, "RxData", TerminalType.Input);
        ReceiveClock = Pin(3, "RxCLK", TerminalType.Input);
        TransmitClock = Pin(4, "TxCLK", TerminalType.Input);
        var rts = Pin(5, "RTS", TerminalType.Output);
        Transmit = Pin(6, "TxData", TerminalType.Output);
        Irq = Pin(7, "IRQ", TerminalType.Bidirectional);
        ChipSelect0 = Pin(8, "CS0", TerminalType.Input);
        ChipSelect2 = Pin(9, "CS2", TerminalType.Input);
        ChipSelect1 = Pin(10, "CS1", TerminalType.Input);
        RegisterSelect = Pin(11, "RS", TerminalType.Input);
        Vcc = Pin(12, "VCC", TerminalType.Power);
        ReadWrite = Pin(13, "RW", TerminalType.Input);
        Enable = Pin(14, "E", TerminalType.Input);
        for (var i = 0; i < 8; i++) _data[i] = Pin(22 - i, $"D{i}", TerminalType.Bidirectional);
        CarrierDetect = Pin(23, "DCD", TerminalType.Input);
        ClearToSend = Pin(24, "CTS", TerminalType.Input);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [Enable, ReadWrite, ChipSelect0, ChipSelect1, ChipSelect2, RegisterSelect, Receive, ReceiveClock, TransmitClock, CarrierDetect, ClearToSend, .. _data],
            [.. _data, Transmit, rts, Irq]);
    }

    public override string PartNumber => "MC6850";

    public override string ComponentType => "6850 ACIA";

    public override bool LabelsPins => true;

    public IReadOnlyList<Terminal> Data => _data;

    public Terminal Enable { get; }

    public Terminal ReadWrite { get; }

    /// <summary>CS0, active high.</summary>
    public Terminal ChipSelect0 { get; }

    /// <summary>CS1, active high.</summary>
    public Terminal ChipSelect1 { get; }

    /// <summary>/CS2, active low.</summary>
    public Terminal ChipSelect2 { get; }

    /// <summary>RS: low for control and status, high for the data registers.</summary>
    public Terminal RegisterSelect { get; }

    public Terminal Transmit { get; }

    public Terminal Receive { get; }

    public Terminal TransmitClock { get; }

    public Terminal ReceiveClock { get; }

    /// <summary>/RTS, as the control register's bits 6-5 set it.</summary>
    public Terminal RequestToSend => OutputTerminals[RtsIndex];

    /// <summary>/CTS: high holds TDRE low in the status register. Tie it low if nothing drives it.</summary>
    public Terminal ClearToSend { get; }

    /// <summary>/DCD: going high says the carrier was lost. Tie it low if nothing drives it.</summary>
    public Terminal CarrierDetect { get; }

    /// <summary>/IRQ: open drain, low while an enabled condition is set.</summary>
    public Terminal Irq { get; }

    // ---- what a test or an agent wants to see -----------------------------------

    /// <summary>Every byte it has sent, in order.</summary>
    public List<byte> TransmittedBytes { get; } = [];

    /// <summary>Every byte it has received, in order — the ones an overrun lost included.</summary>
    public List<byte> ReceivedBytes { get; } = [];

    /// <summary>What it has sent, as text, anything unprintable a dot.</summary>
    public string TransmittedText => string.Concat(TransmittedBytes.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.'));

    public byte Control => _control;

    /// <summary>The status register as a read would give it, the /CTS pin as last seen.</summary>
    public byte Status => StatusValue(_ctsHigh);

    /// <summary>True from power-on until a master reset has been written, and while one is in force.</summary>
    public bool InReset => _held || (_control & 0x03) == 0x03;

    /// <summary>The counter divide the control register picks: 1, 16 or 64.</summary>
    public int Divide => (_control & 0x03) switch { 0 => 1, 1 => 16, _ => 64 };

    /// <summary>The transmit rate the TxCLK pin and the divide give, in baud; 0 until the clock has been seen.</summary>
    public double TransmitBaud => _txPeriod > 0 ? 1.0 / (_txPeriod * Divide) : 0;

    /// <summary>The receive rate RxCLK and the divide give, in baud.</summary>
    public double ReceiveBaud => _rxPeriod > 0 ? 1.0 / (_rxPeriod * Divide) : 0;

    /// <summary>The word format as text: <c>8N1</c>, <c>7E2</c>.</summary>
    public string WordFormat => ((_control >> 2) & 7) switch
    {
        0 => "7E2",
        1 => "7O2",
        2 => "7E1",
        3 => "7O1",
        4 => "8N2",
        5 => "8N1",
        6 => "8E1",
        _ => "8O1",
    };

    public override string ValueLabel => InReset
        ? "in reset"
        : string.Create(CultureInfo.InvariantCulture, $"{TransmitBaud:0} baud {WordFormat}{(TransmittedBytes.Count > 0 ? $", sent {TransmittedBytes.Count}" : string.Empty)}");

    private bool _ctsHigh;

    private bool TransmitInterruptOn => ((_control >> 5) & 3) == 1;

    private bool Breaking => ((_control >> 5) & 3) == 3;

    private bool ReceiveInterruptOn => (_control & 0x80) != 0;

    private byte StatusValue(bool ctsHigh)
    {
        var tdre = !InReset && _transmit is null && !ctsHigh;
        var irq = Interrupting(ctsHigh);
        return (byte)((_rdrf ? 0x01 : 0) | (tdre ? 0x02 : 0) | (_dcd ? 0x04 : 0) | (ctsHigh ? 0x08 : 0) |
                      (_framing ? 0x10 : 0) | (_overrun ? 0x20 : 0) | (_parity ? 0x40 : 0) | (irq ? 0x80 : 0));
    }

    private bool Interrupting(bool ctsHigh)
    {
        if (InReset) return false;
        var tdre = _transmit is null && !ctsHigh;
        return (ReceiveInterruptOn && (_rdrf || _overrun || _dcd)) || (TransmitInterruptOn && tdre);
    }

    // ---- the logic ------------------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var now = context.Time;
        var e = context.ReadInput(Enable, Levels);
        var previous = _lastE;
        _lastE = e;
        _ctsHigh = context.ReadInput(ClearToSend, Levels).IsHigh();

        var selected = context.ReadInput(ChipSelect0, Levels).IsHigh() && context.ReadInput(ChipSelect1, Levels).IsHigh() &&
                       context.ReadInput(ChipSelect2, Levels).IsLow();
        var reading = context.ReadInput(ReadWrite, Levels).IsHigh();
        var dataRegister = context.ReadInput(RegisterSelect, Levels).IsHigh();

        if (!context.IsInitializing)
        {
            Clocks(context, now);
            Carrier(context);
            Serial(context, now);

            if (previous.IsHigh() && e.IsLow() && selected)
            {
                if (reading) AfterRead(dataRegister);
                else Write(dataRegister, ReadBus(context));
            }
        }

        byte? drive = selected && reading && e.IsHigh() ? Read(dataRegister) : null;
        Drive(context, drive, now);
    }

    /// <summary>Each clock's period, from its rising edges: the bit time is that times the divide.</summary>
    private void Clocks(IDigitalContext context, double now)
    {
        var tx = context.ReadInput(TransmitClock, Levels).IsHigh();
        var rx = context.ReadInput(ReceiveClock, Levels).IsHigh();

        if (tx && !_lastTxClock)
        {
            if (!double.IsNaN(_lastTxEdge) && now > _lastTxEdge) _txPeriod = now - _lastTxEdge;
            _lastTxEdge = now;
        }

        if (rx && !_lastRxClock)
        {
            if (!double.IsNaN(_lastRxEdge) && now > _lastRxEdge) _rxPeriod = now - _lastRxEdge;
            _lastRxEdge = now;
        }

        _lastTxClock = tx;
        _lastRxClock = rx;
    }

    /// <summary>/DCD going high latches DCD and resets the receiver; while it stays high, DCD follows it.</summary>
    private void Carrier(IDigitalContext context)
    {
        var high = context.ReadInput(CarrierDetect, Levels).IsHigh();
        if (high && !_lastDcdPin && !InReset)
        {
            _dcd = true;
            _statusReadWithDcd = false;
            NotifyValueChanged();
        }

        if (high)
        {
            _rdrf = false;
            _line.Reset();
        }

        // Read out while /DCD was still high: the bit follows the pin from then on.
        if (_dcdFollows)
        {
            _dcd = high;
            if (!high) _dcdFollows = false;
        }

        _lastDcdPin = high;
    }

    private void Configure()
    {
        var word = (_control >> 2) & 7;
        _line.DataBits = word < 4 ? 7 : 8;
        _line.Parity = word switch
        {
            0 or 2 or 6 => SerialParity.Even,
            1 or 3 or 7 => SerialParity.Odd,
            _ => SerialParity.None,
        };
        _line.StopBits = word is 0 or 1 or 4 ? 2 : 1;
        _line.TransmitBitTime = _txPeriod * Divide;
        _line.ReceiveBitTime = _rxPeriod * Divide;
    }

    private void Serial(IDigitalContext context, double now)
    {
        Configure();
        var running = !InReset && !_lastDcdPin;
        var rx = context.ReadInput(Receive, Levels) != LogicState.Low;
        _line.Receive(now, rx, running && _rxPeriod > 0, Received);

        // The transmit data register into the shift register when it is empty.
        if (running && _transmit is { } next && !_line.Sending(now) && _txPeriod > 0)
        {
            _transmit = null;
            _line.Send(now, next);
            TransmittedBytes.Add((byte)next);
            NotifyValueChanged();
        }
    }

    private void Received(int value, bool framing, bool parity, bool brk)
    {
        var mask = (byte)((1 << _line.DataBits) - 1);
        var b = (byte)(value & mask);
        ReceivedBytes.Add(b);

        if (_rdrf)
        {
            // The byte waiting was not read in time: this one is lost, and OVRN shows after that read.
            _overrunPending = true;
        }
        else
        {
            _receive = b;
            _rdrf = true;
            _framing = framing;
            _parity = parity && !framing;
        }

        NotifyValueChanged();
    }

    private byte ReadBus(IDigitalContext context)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(_data[i], Levels).IsHigh()) value |= 1 << i;
        return (byte)value;
    }

    private byte Read(bool dataRegister) => dataRegister ? _receive : StatusValue(_ctsHigh);

    /// <summary>A read's side effects, as E falls.</summary>
    private void AfterRead(bool dataRegister)
    {
        if (!dataRegister)
        {
            if (_dcd) _statusReadWithDcd = true;
            return;
        }

        if (_overrunPending)
        {
            // The byte before the overrun has been read: OVRN shows now, RDRF stays.
            _overrunPending = false;
            _overrun = true;
        }
        else
        {
            _overrun = false;
            _rdrf = false;
        }

        _framing = false;
        _parity = false;

        if (_statusReadWithDcd)
        {
            _dcd = _lastDcdPin;
            _dcdFollows = _lastDcdPin;
            _statusReadWithDcd = false;
        }

        NotifyValueChanged();
    }

    private void Write(bool dataRegister, byte value)
    {
        if (dataRegister)
        {
            if (!InReset) _transmit = value;
        }
        else
        {
            _control = value;
            if ((value & 0x03) == 0x03) MasterReset();
            else _held = false;
        }

        NotifyValueChanged();
    }

    /// <summary>Control bits 1-0 both set: the status cleared, both halves emptied.</summary>
    private void MasterReset()
    {
        _rdrf = _framing = _parity = _overrun = _overrunPending = false;
        _dcd = false;
        _statusReadWithDcd = _dcdFollows = false;
        _transmit = null;
        _line.Reset();
    }

    private void Drive(IDigitalContext context, byte? data, double now)
    {
        var delay = DelayFor(context);
        for (var i = 0; i < 8; i++)
            Request(context, i, data is { } d ? ((d >> i) & 1) != 0 ? LogicState.High : LogicState.Low : LogicState.HighImpedance, delay);

        var mark = !Breaking && (InReset || _line.Level(now));
        Request(context, TxIndex, mark ? LogicState.High : LogicState.Low, delay);

        // /RTS high only for transmitter control 10, and while the chip waits for its first reset.
        var rtsHigh = _held || ((_control >> 5) & 3) == 2;
        Request(context, RtsIndex, rtsHigh ? LogicState.High : LogicState.Low, delay);
        Request(context, IrqIndex, Interrupting(_ctsHigh) ? LogicState.Low : LogicState.HighImpedance, delay);
    }

    /// <summary>The bit edges going out and the samples coming in.</summary>
    public double? NextBreakpointAfter(double time) => _line.NextBreakpointAfter(time);

    public override void ResetLogic()
    {
        base.ResetLogic();
        _control = 0;
        _held = true;
        _receive = 0;
        _transmit = null;
        _rdrf = _framing = _parity = _overrun = _overrunPending = _dcd = _statusReadWithDcd = _dcdFollows = false;
        _lastDcdPin = false;
        _lastE = LogicState.Unknown;
        _lastTxClock = _lastRxClock = false;
        _lastTxEdge = _lastRxEdge = double.NaN;
        _txPeriod = _rxPeriod = 0;
        _ctsHigh = false;
        _line.Reset();
        TransmittedBytes.Clear();
        ReceivedBytes.Clear();
    }

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "RW" => "R/W",
        "CS2" or "IRQ" or "RTS" or "CTS" or "DCD" => "/" + pin.Name,
        _ => pin.Name,
    };

    private sealed record RunState(byte Control, bool Held, byte Receive, int? Transmit, bool Rdrf, bool Framing, bool Parity, bool Overrun,
        bool OverrunPending, bool Dcd, double TxPeriod, double RxPeriod, byte[] Sent, byte[] Received);

    public object CaptureRunState() =>
        new RunState(_control, _held, _receive, _transmit, _rdrf, _framing, _parity, _overrun, _overrunPending, _dcd, _txPeriod, _rxPeriod,
            [.. TransmittedBytes], [.. ReceivedBytes]);

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;
        (_control, _held, _receive, _transmit, _rdrf, _framing, _parity, _overrun, _overrunPending, _dcd, _txPeriod, _rxPeriod) =
            (run.Control, run.Held, run.Receive, run.Transmit, run.Rdrf, run.Framing, run.Parity, run.Overrun, run.OverrunPending, run.Dcd,
                run.TxPeriod, run.RxPeriod);
        TransmittedBytes.Clear();
        TransmittedBytes.AddRange(run.Sent);
        ReceivedBytes.Clear();
        ReceivedBytes.AddRange(run.Received);
        NotifyValueChanged();
    }
}
