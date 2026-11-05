using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// The Intel 8251A USART, the serial port of the 8080 and 8085 families: a transmitter with a
/// one-character buffer in front of its shift register, a receiver with one character behind its
/// own, and the modem lines — here in its asynchronous mode.
/// <para>
/// <b>The bus.</b> See <see cref="IntelPeripheral"/>. C/D low is the data register — a read gives
/// the character received, a write the character to send — and C/D high the control port: a read
/// gives the status, a write is the mode instruction, a sync character or a command instruction,
/// according to where the chip is in its sequence.
/// </para>
/// <para>
/// <b>The sequence.</b> After RESET (the pin high) or a command with IR set, the chip is idle and
/// the next control write is the <b>mode instruction</b>. A synchronous mode (baud factor 00) is
/// followed by one or two sync characters (mode bit 7 set: one); an asynchronous one goes straight
/// on; every control write after that is a <b>command instruction</b>. A chip in an unknown state
/// is put right by three $00s and then $40: in the mode state they are a synchronous mode with two
/// sync characters, in the command state they do nothing, and either way the $40 is an internal
/// reset — as Intel's note on power-up says.
/// </para>
/// <list type="bullet">
/// <item><b>Mode</b> (asynchronous): bits 1-0 the baud factor — 01 x1, 10 x16, 11 x64 (00 is
/// synchronous); bits 3-2 the character length, 5 to 8 bits; bit 4 parity on, bit 5 even; bits 7-6
/// the stop bits sent — 01 one, 10 one and a half, 11 two (the receiver needs only one).</item>
/// <item><b>Command</b>: bit 0 TxEN, bit 1 DTR (the /DTR pin low), bit 2 RxE, bit 3 SBRK (TxD held
/// low), bit 4 ER (clears PE, OE and FE), bit 5 RTS (the /RTS pin low), bit 6 IR (an internal
/// reset, back to the mode state), bit 7 EH (enter hunt; nothing in asynchronous mode).</item>
/// <item><b>Status</b>: bit 0 TxRDY (the transmit buffer empty — unlike the pin, whatever TxEN and
/// /CTS are), bit 1 RxRDY, bit 2 TxEMPTY, bit 3 PE, bit 4 OE, bit 5 FE, bit 6 SYNDET/BRKDET, bit 7
/// DSR (the /DSR pin low). Bits 1, 2 and 6 are the pins.</item>
/// </list>
/// <para>
/// <b>Characters.</b> The transmitter starts only while TxEN is set and /CTS is low, moving the
/// buffer into its shift register; the <b>TxRDY</b> pin is the buffer empty with TxEN set and /CTS
/// low, and <b>TxEMPTY</b> is high while there is nothing to shift out (or the transmitter is
/// disabled). With RxE set the receiver finds a start bit, checks it mid-bit, samples the middle of
/// each bit and needs one stop bit: a character sets <b>RxRDY</b> until it is read, unused high
/// bits read as zeros, a stop bit found low sets FE and a wrong parity bit PE, and a character
/// finished while RxRDY is still set replaces the one waiting and sets OE. RxE off holds RxRDY low.
/// <b>BRKDET</b> goes high when RxD has stayed low through two whole characters, stop bits
/// included, and low as soon as RxD is high again.
/// </para>
/// <para>
/// <b>The clocks.</b> /TxC and /RxC clock each half at 1, 16 or 64 times the bit rate; the part
/// measures each clock's period from its rising edges and times the bits from it, as the 6850 and
/// the Z80 SIO do, so the rate follows whatever drives the pins — an 8253 or the 8155's timer, or
/// an oscillator. TxD and RxD are ordinary logic, for the Serial Terminal and the UART bus decode.
/// CLK is the system clock the chip runs on; it must be more than 30 times the bit rate, and is
/// not otherwise used here.
/// </para>
/// <para>
/// Simplifications: the <b>synchronous modes are not modelled</b> — a synchronous mode instruction
/// and its sync characters are taken in the sequence, so the reset idiom works, but then both
/// halves stay idle (TxD marking, nothing received), EH does nothing and SYNDET stays low (it is an
/// output only; external sync detect is not modelled). At x1 the receiver samples mid-bit, where
/// the chip wants /RxC synchronised to the data; a character starts at the next time point after it
/// is written rather than on a /TxC falling edge; TxRDY falls as the write ends, not as /WR falls;
/// and the status is up to date at once rather than within 28 CLK periods.
/// </para>
/// <para>
/// Pinout (DIP-28): 1 = D2, 2 = D3, 3 = RxD, 4 = GND, 5 = D4, 6 = D5, 7 = D6, 8 = D7, 9 = /TxC,
/// 10 = /WR, 11 = /CS, 12 = C/D, 13 = /RD, 14 = RxRDY, 15 = TxRDY, 16 = SYNDET/BRKDET, 17 = /CTS,
/// 18 = TxEMPTY, 19 = TxD, 20 = CLK, 21 = RESET, 22 = /DSR, 23 = /RTS, 24 = /DTR, 25 = /RxC,
/// 26 = +5V, 27 = D0, 28 = D1.
/// </para>
/// </summary>
public sealed class Usart8251 : IntelPeripheral, IBreakpointSource, IRunState
{
    // Outputs: D0-D7 = 0-7, then TxD, RxRDY, TxRDY, TxEMPTY, SYNDET/BRKDET, /RTS, /DTR.
    private const int TxIndex = 8, RxRdyIndex = 9, TxRdyIndex = 10, TxEmptyIndex = 11, BrkIndex = 12, RtsIndex = 13, DtrIndex = 14;

    private static readonly int[] DataPins = [27, 28, 1, 2, 5, 6, 7, 8];

    /// <summary>Where the chip is in its control sequence.</summary>
    private enum Expecting
    {
        Mode,
        Sync1,
        Sync2,
        Command,
    }

    private readonly SerialLine _transmitter = new();
    private readonly SerialLine _receiver = new();

    private Expecting _expecting = Expecting.Mode;
    private byte _mode;
    private byte _command;
    private readonly byte[] _sync = new byte[2];
    private int? _buffer;
    private byte _received;
    private bool _rxRdy, _parityError, _overrun, _framing, _break;
    private bool _ctsLow, _dsrLow;
    private bool _lastReset;
    private bool _lastRx = true;
    private double _lowSince = double.NaN;

    private bool _lastTxClock, _lastRxClock;
    private double _lastTxEdge = double.NaN, _lastRxEdge = double.NaN;
    private double _txPeriod, _rxPeriod;

    public Usart8251() : base(28)
    {
        PropagationDelay = 50e-9;

        var pins = new Terminal[29];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 28));

        for (var i = 0; i < 8; i++) SetDataPin(i, Pin(DataPins[i], $"D{i}", TerminalType.Bidirectional));
        Receive = Pin(3, "RxD", TerminalType.Input);
        Gnd = Pin(4, "GND", TerminalType.Ground);
        TransmitClock = Pin(9, "TxC", TerminalType.Input);
        WriteStrobe = Pin(10, "WR", TerminalType.Input);
        ChipSelect = Pin(11, "CS", TerminalType.Input);
        ControlData = Pin(12, "CD", TerminalType.Input);
        ReadStrobe = Pin(13, "RD", TerminalType.Input);
        var rxRdy = Pin(14, "RxRDY", TerminalType.Output);
        var txRdy = Pin(15, "TxRDY", TerminalType.Output);
        var brk = Pin(16, "SYNDET", TerminalType.Output);
        ClearToSend = Pin(17, "CTS", TerminalType.Input);
        var txEmpty = Pin(18, "TxEMPTY", TerminalType.Output);
        var txd = Pin(19, "TxD", TerminalType.Output);
        Clock = Pin(20, "CLK", TerminalType.Input);
        Reset = Pin(21, "RESET", TerminalType.Input);
        DataSetReady = Pin(22, "DSR", TerminalType.Input);
        var rts = Pin(23, "RTS", TerminalType.Output);
        var dtr = Pin(24, "DTR", TerminalType.Output);
        ReceiveClock = Pin(25, "RxC", TerminalType.Input);
        Vcc = Pin(26, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [ChipSelect, ReadStrobe, WriteStrobe, ControlData, Receive, TransmitClock, ReceiveClock, ClearToSend, DataSetReady, Clock, Reset, .. Data],
            [.. Data, txd, rxRdy, txRdy, txEmpty, brk, rts, dtr]);
    }

    public override string PartNumber => "8251A";

    public override string ComponentType => "8251A USART";

    /// <summary>C/D: low the data register, high the control port.</summary>
    public Terminal ControlData { get; }

    /// <summary>RESET: high puts the chip in its idle state, waiting for a mode instruction.</summary>
    public Terminal Reset { get; }

    /// <summary>CLK: the system clock (CLK OUT on an 8085).</summary>
    public Terminal Clock { get; }

    public Terminal Receive { get; }

    public Terminal Transmit => OutputTerminals[TxIndex];

    /// <summary>/TxC: the transmitter's clock, 1, 16 or 64 times the bit rate.</summary>
    public Terminal TransmitClock { get; }

    /// <summary>/RxC: the receiver's clock.</summary>
    public Terminal ReceiveClock { get; }

    /// <summary>/CTS: low lets the transmitter send. Tie it low if nothing drives it.</summary>
    public Terminal ClearToSend { get; }

    /// <summary>/DSR: a general-purpose input, read as status bit 7.</summary>
    public Terminal DataSetReady { get; }

    public Terminal ReceiverReady => OutputTerminals[RxRdyIndex];

    public Terminal TransmitterReady => OutputTerminals[TxRdyIndex];

    public Terminal TransmitterEmpty => OutputTerminals[TxEmptyIndex];

    /// <summary>SYNDET/BRKDET: break detect in asynchronous mode.</summary>
    public Terminal BreakDetect => OutputTerminals[BrkIndex];

    public Terminal RequestToSend => OutputTerminals[RtsIndex];

    public Terminal DataTerminalReady => OutputTerminals[DtrIndex];

    // ---- what a test or an agent wants to see -----------------------------------

    /// <summary>The mode instruction last written.</summary>
    public byte Mode => _mode;

    /// <summary>The command instruction last written (IR and ER, which act once, left out).</summary>
    public byte Command => _command;

    /// <summary>The status register as a read would give it now.</summary>
    public byte Status => StatusValue();

    /// <summary>What the next control write will be: "mode", "sync character 1", "sync character 2" or "command".</summary>
    public string Expects => _expecting switch
    {
        Expecting.Mode => "mode",
        Expecting.Sync1 => "sync character 1",
        Expecting.Sync2 => "sync character 2",
        _ => "command",
    };

    /// <summary>The mode is synchronous (baud factor 00): not modelled, both halves idle.</summary>
    public bool Synchronous => (_mode & 3) == 0;

    /// <summary>The baud factor the mode picks: 1, 16 or 64; 0 before a mode or in synchronous mode.</summary>
    public int Factor => (_mode & 3) switch { 1 => 1, 2 => 16, 3 => 64, _ => 0 };

    /// <summary>Bits per character, 5 to 8.</summary>
    public int CharacterBits => 5 + ((_mode >> 2) & 3);

    /// <summary>The character format as text: <c>8N1</c>, <c>7E2</c>, <c>5O1.5</c>, or <c>sync</c>.</summary>
    public string Format
    {
        get
        {
            if (Synchronous) return "sync";
            var parity = (_mode & 0x10) == 0 ? "N" : (_mode & 0x20) != 0 ? "E" : "O";
            var stop = (_mode >> 6) switch { 2 => "1.5", 3 => "2", _ => "1" };
            return string.Create(CultureInfo.InvariantCulture, $"{CharacterBits}{parity}{stop}");
        }
    }

    /// <summary>The transmit rate /TxC and the factor give, in baud; 0 until the clock has been seen.</summary>
    public double TransmitBaud => _txPeriod > 0 && Factor > 0 ? 1.0 / (_txPeriod * Factor) : 0;

    /// <summary>The receive rate /RxC and the factor give, in baud.</summary>
    public double ReceiveBaud => _rxPeriod > 0 && Factor > 0 ? 1.0 / (_rxPeriod * Factor) : 0;

    /// <summary>A break is being detected on RxD.</summary>
    public bool BreakDetected => _break;

    /// <summary>Every byte it has sent, in order.</summary>
    public List<byte> TransmittedBytes { get; } = [];

    /// <summary>Every byte it has received, in order — the ones an overrun lost included.</summary>
    public List<byte> ReceivedBytes { get; } = [];

    /// <summary>What it has sent, as text, anything unprintable a dot.</summary>
    public string TransmittedText => string.Concat(TransmittedBytes.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.'));

    public override string ValueLabel => _expecting != Expecting.Command
        ? "awaiting " + Expects
        : Synchronous
            ? "8251A sync (idle)"
            : string.Create(CultureInfo.InvariantCulture, $"{TransmitBaud:0} baud {Format}{(TransmittedBytes.Count > 0 ? $", sent {TransmittedBytes.Count}" : string.Empty)}");

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "CS" or "RD" or "WR" or "CTS" or "DSR" or "RTS" or "DTR" or "TxC" or "RxC" => "/" + pin.Name,
        "CD" => "C/D",
        "SYNDET" => "SYNDET/BD",
        "VCC" => "+5V",
        _ => pin.Name,
    };

    private bool Running => _expecting == Expecting.Command && !Synchronous;

    private bool TxEnabled => (_command & 0x01) != 0;

    private bool RxEnabled => (_command & 0x04) != 0;

    private bool Sending(double now) => _transmitter.Sending(now);

    private double _now;

    private bool TxEmpty => !Sending(_now) && (_buffer is null || !TxEnabled || !Running);

    private byte StatusValue() =>
        (byte)((_buffer is null ? 0x01 : 0) | (_rxRdy ? 0x02 : 0) | (TxEmpty ? 0x04 : 0) | (_parityError ? 0x08 : 0) |
               (_overrun ? 0x10 : 0) | (_framing ? 0x20 : 0) | (_break ? 0x40 : 0) | (_dsrLow ? 0x80 : 0));

    // ---- the logic ------------------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var now = context.Time;
        _now = now;
        var reset = context.ReadInput(Reset, Levels).IsHigh();
        _ctsLow = context.ReadInput(ClearToSend, Levels).IsLow();
        _dsrLow = context.ReadInput(DataSetReady, Levels).IsLow();

        if (!context.IsInitializing)
        {
            if (reset && !_lastReset)
            {
                InternalReset();
                NotifyValueChanged();
            }

            if (!reset)
            {
                Clocks(context, now);
                Serial(context, now);
            }
        }

        _lastReset = reset;
        if (reset) ResetBus();
        else EvaluateBus(context);
        Drive(context, now);
    }

    /// <summary>Each clock's period, from its rising edges.</summary>
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

    private void Configure()
    {
        var parity = (_mode & 0x10) == 0 ? SerialParity.None : (_mode & 0x20) != 0 ? SerialParity.Even : SerialParity.Odd;
        _transmitter.DataBits = _receiver.DataBits = CharacterBits;
        _transmitter.Parity = _receiver.Parity = parity;
        _transmitter.StopBits = (_mode >> 6) switch { 2 => 1.5, 3 => 2.0, _ => 1.0 };
        _receiver.StopBits = 1;
        _transmitter.TransmitBitTime = _txPeriod * Factor;
        _receiver.ReceiveBitTime = _rxPeriod * Factor;
    }

    /// <summary>The length of a whole character on the line, in receive bit times: start, data, parity and one stop bit.</summary>
    private int FrameBits => 1 + CharacterBits + ((_mode & 0x10) != 0 ? 1 : 0) + 1;

    private void Serial(IDigitalContext context, double now)
    {
        Configure();
        var rx = context.ReadInput(Receive, Levels) != LogicState.Low;
        _receiver.Receive(now, rx, Running && RxEnabled && _rxPeriod > 0, Received);

        // Break detect: RxD low through two whole characters; cleared as it comes back high.
        if (!rx && _lastRx) _lowSince = now;
        if (rx)
        {
            _lowSince = double.NaN;
            if (_break)
            {
                _break = false;
                NotifyValueChanged();
            }
        }
        else if (Running && !_break && !double.IsNaN(_lowSince) && _rxPeriod > 0 && now >= BreakAt - 1e-12)
        {
            _break = true;
            NotifyValueChanged();
        }

        _lastRx = rx;

        // The buffer into the shift register when it is free, the transmitter enabled and /CTS low.
        if (Running && TxEnabled && _ctsLow && _buffer is { } next && !Sending(now) && _txPeriod > 0)
        {
            _buffer = null;
            _transmitter.Send(now, next);
            TransmittedBytes.Add((byte)next);
            NotifyValueChanged();
        }
    }

    private double BreakAt => _lowSince + (2 * FrameBits * _rxPeriod * Math.Max(Factor, 1));

    private void Received(int value, bool framing, bool parity, bool brk)
    {
        var b = (byte)(value & ((1 << CharacterBits) - 1));
        ReceivedBytes.Add(b);
        if (_rxRdy) _overrun = true;
        _received = b;
        _rxRdy = true;
        if (framing) _framing = true;
        if (parity) _parityError = true;
        NotifyValueChanged();
    }

    private void Drive(IDigitalContext context, double now)
    {
        var delay = DelayFor(context);
        var breaking = (_command & 0x08) != 0;
        var mark = !breaking && (!Running || _transmitter.Level(now));
        Request(context, TxIndex, mark ? LogicState.High : LogicState.Low, delay);
        Request(context, RxRdyIndex, _rxRdy ? LogicState.High : LogicState.Low, delay);
        var txRdy = _buffer is null && TxEnabled && _ctsLow && _expecting == Expecting.Command;
        Request(context, TxRdyIndex, txRdy ? LogicState.High : LogicState.Low, delay);
        Request(context, TxEmptyIndex, TxEmpty ? LogicState.High : LogicState.Low, delay);
        Request(context, BrkIndex, _break ? LogicState.High : LogicState.Low, delay);
        Request(context, RtsIndex, (_command & 0x20) != 0 ? LogicState.Low : LogicState.High, delay);
        Request(context, DtrIndex, (_command & 0x02) != 0 ? LogicState.Low : LogicState.High, delay);
    }

    // ---- the registers --------------------------------------------------------------

    private protected override int SelectedRegister(IDigitalContext context) => context.ReadInput(ControlData, Levels).IsHigh() ? 1 : 0;

    private protected override byte ReadRegister(int register) => register == 0 ? _received : StatusValue();

    private protected override void AfterRead(int register)
    {
        if (register == 0) _rxRdy = false;
    }

    private protected override void WriteRegister(int register, byte value)
    {
        if (register == 0)
        {
            _buffer = value & ((1 << CharacterBits) - 1);
            return;
        }

        switch (_expecting)
        {
            case Expecting.Mode:
                _mode = value;
                _expecting = (value & 3) != 0 ? Expecting.Command : Expecting.Sync1;
                break;

            case Expecting.Sync1:
                _sync[0] = value;
                _expecting = (_mode & 0x80) != 0 ? Expecting.Command : Expecting.Sync2;
                break;

            case Expecting.Sync2:
                _sync[1] = value;
                _expecting = Expecting.Command;
                break;

            default:
                if ((value & 0x40) != 0)
                {
                    InternalReset();
                    return;
                }

                if ((value & 0x10) != 0) _parityError = _overrun = _framing = false;
                _command = (byte)(value & ~0x50);
                if (!RxEnabled) _rxRdy = false;
                break;
        }
    }

    /// <summary>RESET or IR: idle, waiting for a mode instruction; the commands, errors and both halves cleared.</summary>
    private void InternalReset()
    {
        _expecting = Expecting.Mode;
        _command = 0;
        _buffer = null;
        _rxRdy = _parityError = _overrun = _framing = _break = false;
        _transmitter.Reset();
        _receiver.Reset();
        _lowSince = double.NaN;
    }

    /// <summary>The bit edges going out, the samples coming in, and the moment a held-low RxD becomes a break.</summary>
    public double? NextBreakpointAfter(double time)
    {
        double? next = null;
        void Consider(double? at)
        {
            if (at is { } t && t > time && (next is null || t < next)) next = t;
        }

        Consider(_transmitter.NextBreakpointAfter(time));
        Consider(_receiver.NextBreakpointAfter(time));
        if (Running && !_break && !double.IsNaN(_lowSince) && _rxPeriod > 0) Consider(BreakAt);
        return next;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        InternalReset();
        _mode = 0;
        _received = 0;
        _sync[0] = _sync[1] = 0;
        _lastReset = false;
        _lastRx = true;
        _ctsLow = _dsrLow = false;
        _lastTxClock = _lastRxClock = false;
        _lastTxEdge = _lastRxEdge = double.NaN;
        _txPeriod = _rxPeriod = 0;
        TransmittedBytes.Clear();
        ReceivedBytes.Clear();
    }

    private sealed record RunState(int Expecting, byte Mode, byte Command, int? Buffer, byte Received, bool RxRdy, bool Parity, bool Overrun,
        bool Framing, bool Break, double TxPeriod, double RxPeriod, byte[] Sent, byte[] ReceivedBytes);

    public object CaptureRunState() =>
        new RunState((int)_expecting, _mode, _command, _buffer, _received, _rxRdy, _parityError, _overrun, _framing, _break, _txPeriod, _rxPeriod,
            [.. TransmittedBytes], [.. ReceivedBytes]);

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;
        _expecting = (Expecting)run.Expecting;
        (_mode, _command, _buffer, _received, _rxRdy, _parityError, _overrun, _framing, _break, _txPeriod, _rxPeriod) =
            (run.Mode, run.Command, run.Buffer, run.Received, run.RxRdy, run.Parity, run.Overrun, run.Framing, run.Break, run.TxPeriod, run.RxPeriod);
        TransmittedBytes.Clear();
        TransmittedBytes.AddRange(run.Sent);
        ReceivedBytes.Clear();
        ReceivedBytes.AddRange(run.ReceivedBytes);
        NotifyValueChanged();
    }
}
