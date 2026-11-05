using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// The Zilog Z80 SIO and DART, two-channel serial controllers: the asynchronous half of the SIO,
/// which is all of the DART — the <see cref="Z80Sio"/> (the SIO/0's pinout) and the
/// <see cref="Z80Dart"/>, the same chip asynchronous-only, with ring-indicator inputs where the SIO
/// has /SYNC.
/// <para>
/// <b>The bus.</b> B/A picks the channel (low A), C/D the data (low) or control (high) register; see
/// <see cref="Z80Peripheral"/> for the cycle. The control port reaches the write registers WR0-WR7
/// and the read registers RR0-RR2 through a pointer: bits 2-0 of a WR0 write say which register the
/// next control access is, after which the pointer goes back to 0.
/// </para>
/// <list type="bullet">
/// <item><b>WR0</b>: the pointer, and a command in bits 5-3 — 010 reset the external/status
/// interrupts, 011 channel reset, 100 enable the interrupt on the next received character, 101 reset
/// the transmitter's interrupt pending, 110 error reset, 111 return from interrupt (channel A,
/// for a chip not watching the bus for RETI); bits 7-6 = 11 reset the transmit underrun latch.</item>
/// <item><b>WR1</b>: bit 0 external/status interrupts, bit 1 transmit interrupts, bit 2 (channel B)
/// status affects vector, bits 4-3 the receive interrupt — 00 none, 01 on the first character, 10 on
/// every character with a parity error special, 11 on every character — and bits 7-5 /W/RDY:
/// enable, ready (1) or wait (0), on receive (1) or transmit (0).</item>
/// <item><b>WR2</b> (channel B): the interrupt vector.</item>
/// <item><b>WR3</b>: bit 0 receiver on, bit 5 auto enables (the receiver only while /DCD is low, the
/// transmitter only while /CTS is low), bits 7-6 the receive bits per character (00 5, 01 7, 10 6,
/// 11 8).</item>
/// <item><b>WR4</b>: bit 0 parity on, bit 1 even, bits 3-2 the stop bits (01 one, 10 one and a half,
/// 11 two; 00 is the SIO's synchronous modes, not modelled), bits 7-6 the clock rate (x1, x16, x32,
/// x64).</item>
/// <item><b>WR5</b>: bit 1 RTS, bit 3 transmitter on, bit 4 send a break, bits 6-5 the transmit bits
/// per character, bit 7 DTR.</item>
/// <item><b>RR0</b>: bit 0 a character waiting, bit 1 (channel A) an interrupt pending in the chip,
/// bit 2 the transmit buffer empty, bit 3 DCD, bit 4 SYNC on the SIO (RI on the DART), bit 5 CTS —
/// each a 1 while its pin is low — bit 6 the transmit underrun latch, bit 7 a break. Bits 3, 4, 5 and
/// 7 hold what they were when an external/status interrupt was raised until it is reset.</item>
/// <item><b>RR1</b>: bit 0 all sent, bit 4 a parity error, bit 5 a receive overrun (both held until an
/// error reset), bit 6 the waiting character's framing error.</item>
/// <item><b>RR2</b> (channel B): the vector — with status affects vector, bits 3-1 say which source
/// is the highest pending (B transmit 000, B external 001, B receive 010, B special 011, then A the
/// same with bit 3 set; 011 when nothing is pending).</item>
/// </list>
/// <para>
/// <b>Characters.</b> Each channel has a transmit buffer in front of its shift register and a
/// three-byte receive FIFO behind its own; a fourth character arriving over a full FIFO overwrites
/// the last and is flagged as an overrun. A framing error, an overrun, and with WR1's mode 10 a
/// parity error, make a special receive condition, which interrupts — through its own vector code —
/// until an error reset. A break (every bit low, the stop bit too) sets RR0's bit 7, puts a null in the
/// FIFO, and with its end raises an external/status interrupt each way. The transmit interrupt comes
/// when the buffer empties into the shift register, not merely because it is empty.
/// </para>
/// <para>
/// <b>The clocks.</b> /TxC and /RxC clock each half at 1, 16, 32 or 64 times the bit rate; the part
/// measures each clock's period from its edges and times the bits from it, as the 6850 does, so the
/// rate follows whatever drives the pin. On the SIO/0 channel B's two share one pin, /RxTxCB.
/// <b>Interrupts</b> rank channel A's receive, transmit and external/status, then channel B's, in
/// the daisy chain, with one vector written to channel B's WR2.
/// </para>
/// <para>
/// Simplifications: the synchronous modes (monosync, bisync, SDLC/HDLC, CRC, the sync characters in
/// WR6 and WR7) are not modelled — WR4's stop bits 00 leave both halves idle; the x1 rate samples
/// mid-bit, where the chip wants the clock synchronised to the data; a character starts at the next
/// time point after it is written; /RTS follows WR5 at once (the chip holds it until the transmitter is
/// empty); and RR1's residue codes read 0.
/// </para>
/// </summary>
public abstract class Z80SerialController : Z80Peripheral, IBreakpointSource, IRunState
{
    // Outputs: D0-D7 = 0-7, then per channel TxD, /RTS, /DTR, /W/RDY (A at 8, B at 12), /INT = 16, IEO = 17.
    private const int ChannelOutputs = 8;

    private static readonly int[] DataPins = [40, 1, 39, 2, 38, 3, 37, 4];

    private readonly SerialChannel[] _channels;
    private readonly Terminal[] _receive = new Terminal[2];
    private readonly Terminal[] _receiveClock = new Terminal[2];
    private readonly Terminal[] _transmitClock = new Terminal[2];
    private readonly Terminal[] _cts = new Terminal[2];
    private readonly Terminal[] _dcd = new Terminal[2];
    private readonly Terminal[] _sync = new Terminal[2];
    private readonly Action<int, bool, bool, bool>[] _received;
    private bool _lastReset;

    /// <summary>One channel: its registers, its two halves and its interrupts.</summary>
    private sealed class SerialChannel
    {
        public readonly byte[] Wr = new byte[8];
        public int Pointer;
        public readonly SerialLine Transmitter = new();
        public readonly SerialLine Receiver = new();
        public readonly List<(byte Value, bool Parity, bool Framing, bool Overrun)> Fifo = [];
        public int? Buffer;
        public bool AllSent = true;
        public bool TransmitPending;
        public bool ExternalPending;
        public bool SpecialPending;
        public bool FirstArmed = true;
        public bool FirstLatched;
        public bool ParityLatched, OverrunLatched;
        public bool Break;
        public bool Underrun = true;
        public byte? HeldStatus;
        public bool Dcd, Cts, Sync;
        public bool StatusSeen;
        public bool LastTxClock, LastRxClock;
        public double LastTxEdge = double.NaN, LastRxEdge = double.NaN;
        public double TxPeriod, RxPeriod;
        public List<byte> Sent = [];
        public List<byte> Received = [];

        public int ReceiveMode => (Wr[1] >> 3) & 3;

        public int Factor => (Wr[4] >> 6) switch { 0 => 1, 1 => 16, 2 => 32, _ => 64 };

        public bool Asynchronous => (Wr[4] & 0x0C) != 0;

        public bool AutoEnables => (Wr[3] & 0x20) != 0;

        public void Reset()
        {
            Array.Clear(Wr, 0, 2);
            Array.Clear(Wr, 3, 5);
            Pointer = 0;
            Transmitter.Reset();
            Receiver.Reset();
            Fifo.Clear();
            Buffer = null;
            AllSent = true;
            TransmitPending = ExternalPending = SpecialPending = FirstLatched = false;
            FirstArmed = true;
            ParityLatched = OverrunLatched = Break = false;
            Underrun = true;
            HeldStatus = null;
        }
    }

    private protected Z80SerialController(bool dart) : base(40, 6)
    {
        IsDart = dart;
        PropagationDelay = 50e-9;
        _channels = [new(), new()];
        _received = [(v, f, p, b) => Received(0, v, f, p, b), (v, f, p, b) => Received(1, v, f, p, b)];

        var pins = new Terminal[41];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 40));

        var syncName = dart ? "RI" : "SYNC";
        for (var i = 0; i < 8; i++) SetDataPin(i, Pin(DataPins[i], $"D{i}", TerminalType.Bidirectional));
        var interrupt = Pin(5, "INT", TerminalType.Bidirectional);
        InterruptEnableIn = Pin(6, "IEI", TerminalType.Input);
        var ieo = Pin(7, "IEO", TerminalType.Output);
        M1 = Pin(8, "M1", TerminalType.Input);
        Vcc = Pin(9, "VCC", TerminalType.Power);
        var wrdyA = Pin(10, "WRDYA", TerminalType.Bidirectional);
        _sync[0] = Pin(11, syncName + "A", TerminalType.Input);
        _receive[0] = Pin(12, "RxDA", TerminalType.Input);
        _receiveClock[0] = Pin(13, "RxCA", TerminalType.Input);
        _transmitClock[0] = Pin(14, "TxCA", TerminalType.Input);
        var txdA = Pin(15, "TxDA", TerminalType.Output);
        var dtrA = Pin(16, "DTRA", TerminalType.Output);
        var rtsA = Pin(17, "RTSA", TerminalType.Output);
        _cts[0] = Pin(18, "CTSA", TerminalType.Input);
        _dcd[0] = Pin(19, "DCDA", TerminalType.Input);
        Clock = Pin(20, "CLK", TerminalType.Input);
        Reset = Pin(21, "RESET", TerminalType.Input);
        _dcd[1] = Pin(22, "DCDB", TerminalType.Input);
        _cts[1] = Pin(23, "CTSB", TerminalType.Input);
        var rtsB = Pin(24, "RTSB", TerminalType.Output);
        var dtrB = Pin(25, "DTRB", TerminalType.Output);
        var txdB = Pin(26, "TxDB", TerminalType.Output);
        _receiveClock[1] = _transmitClock[1] = Pin(27, "RxTxCB", TerminalType.Input);
        _receive[1] = Pin(28, "RxDB", TerminalType.Input);
        _sync[1] = Pin(29, syncName + "B", TerminalType.Input);
        var wrdyB = Pin(30, "WRDYB", TerminalType.Bidirectional);
        Gnd = Pin(31, "GND", TerminalType.Ground);
        ReadStrobe = Pin(32, "RD", TerminalType.Input);
        ControlData = Pin(33, "CD", TerminalType.Input);
        ChannelSelect = Pin(34, "BA", TerminalType.Input);
        ChipEnable = Pin(35, "CE", TerminalType.Input);
        IoRequest = Pin(36, "IORQ", TerminalType.Input);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [ChipEnable, ChannelSelect, ControlData, M1, IoRequest, ReadStrobe, InterruptEnableIn, Clock, Reset,
                _receive[0], _receive[1], _receiveClock[0], _transmitClock[0], _receiveClock[1], _cts[0], _cts[1], _dcd[0], _dcd[1], _sync[0], _sync[1], .. Data],
            [.. Data, txdA, rtsA, dtrA, wrdyA, txdB, rtsB, dtrB, wrdyB, interrupt, ieo]);
    }

    /// <summary>True for the DART: ring indicators in place of /SYNC.</summary>
    public bool IsDart { get; }

    private protected override int IntIndex => 16;

    private protected override int IeoIndex => 17;

    public override string ComponentType => PartNumber;

    /// <summary>C/D: low the data register, high the control register.</summary>
    public Terminal ControlData { get; }

    /// <summary>B/A: low channel A, high channel B.</summary>
    public Terminal ChannelSelect { get; }

    /// <summary>/RESET: low resets both channels.</summary>
    public Terminal Reset { get; }

    public Terminal TransmitA => OutputTerminals[ChannelOutputs];

    public Terminal TransmitB => OutputTerminals[ChannelOutputs + 4];

    public Terminal ReceiveA => _receive[0];

    public Terminal ReceiveB => _receive[1];

    /// <summary>/RxCA: channel A's receive clock.</summary>
    public Terminal ReceiveClockA => _receiveClock[0];

    /// <summary>/TxCA: channel A's transmit clock.</summary>
    public Terminal TransmitClockA => _transmitClock[0];

    /// <summary>/RxTxCB: channel B's one clock for both halves.</summary>
    public Terminal ClockB => _receiveClock[1];

    public Terminal RequestToSendA => OutputTerminals[ChannelOutputs + 1];

    public Terminal RequestToSendB => OutputTerminals[ChannelOutputs + 5];

    public Terminal DataTerminalReadyA => OutputTerminals[ChannelOutputs + 2];

    public Terminal DataTerminalReadyB => OutputTerminals[ChannelOutputs + 6];

    /// <summary>/W/RDYA: a wait for the Z80's /WAIT (open drain) or a ready for a DMA controller.</summary>
    public Terminal WaitReadyA => OutputTerminals[ChannelOutputs + 3];

    public Terminal WaitReadyB => OutputTerminals[ChannelOutputs + 7];

    public Terminal ClearToSendA => _cts[0];

    public Terminal ClearToSendB => _cts[1];

    public Terminal CarrierDetectA => _dcd[0];

    public Terminal CarrierDetectB => _dcd[1];

    /// <summary>/SYNCA on the SIO, /RIA on the DART.</summary>
    public Terminal SyncA => _sync[0];

    /// <summary>/SYNCB on the SIO, /RIB on the DART.</summary>
    public Terminal SyncB => _sync[1];

    public override IReadOnlyList<string> InterruptSources { get; } =
        ["A receive", "A transmit", "A external/status", "B receive", "B transmit", "B external/status"];

    // ---- what a test or an agent wants to see -----------------------------------

    /// <summary>A write register as last written: WR0-WR7 (WR2 is channel B's).</summary>
    public byte WriteRegisterValue(bool b, int register) => _channels[b ? 1 : 0].Wr[register];

    /// <summary>A read register as a read would give it now: RR0, RR1, or RR2 (channel B).</summary>
    public byte ReadRegisterValue(bool b, int register) => register switch
    {
        1 => Rr1(_channels[b ? 1 : 0]),
        2 when b => Rr2(),
        _ => Rr0(b ? 1 : 0),
    };

    /// <summary>The transmit and receive rates the clocks and WR4 give, in baud; 0 until a clock has been seen.</summary>
    public (double Transmit, double Receive) Baud(bool b)
    {
        var c = _channels[b ? 1 : 0];
        return (c.TxPeriod > 0 ? 1.0 / (c.TxPeriod * c.Factor) : 0, c.RxPeriod > 0 ? 1.0 / (c.RxPeriod * c.Factor) : 0);
    }

    /// <summary>The character format WR3, WR4 and WR5 set: <c>8N1</c>, <c>7E2</c>, or <c>sync</c>.</summary>
    public string Format(bool b)
    {
        var c = _channels[b ? 1 : 0];
        if (!c.Asynchronous) return "sync";
        var parity = (c.Wr[4] & 1) == 0 ? "N" : (c.Wr[4] & 2) != 0 ? "E" : "O";
        var stop = ((c.Wr[4] >> 2) & 3) switch { 1 => "1", 2 => "1.5", _ => "2" };
        return string.Create(CultureInfo.InvariantCulture, $"{Bits(c.Wr[5] >> 5)}{parity}{stop}");
    }

    /// <summary>The characters waiting in the receive FIFO.</summary>
    public int Waiting(bool b) => _channels[b ? 1 : 0].Fifo.Count;

    /// <summary>Every byte a channel has sent, in order.</summary>
    public IReadOnlyList<byte> Transmitted(bool b) => _channels[b ? 1 : 0].Sent;

    /// <summary>Every byte a channel has received, in order — the ones an overrun lost included.</summary>
    public IReadOnlyList<byte> ReceivedBytes(bool b) => _channels[b ? 1 : 0].Received;

    /// <summary>What a channel has sent, as text, anything unprintable a dot.</summary>
    public string TransmittedText(bool b) => string.Concat(_channels[b ? 1 : 0].Sent.Select(x => x is >= 0x20 and < 0x7F ? (char)x : '.'));

    public override string ValueLabel
    {
        get
        {
            var (a, _) = Baud(false);
            var sent = _channels[0].Sent.Count + _channels[1].Sent.Count;
            return a > 0
                ? string.Create(CultureInfo.InvariantCulture, $"A {a:0} baud {Format(false)}{(sent > 0 ? $", sent {sent}" : string.Empty)}")
                : PartNumber;
        }
    }

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "INT" or "M1" or "RD" or "IORQ" or "CE" or "RESET" => "/" + pin.Name,
        "WRDYA" => "/W/RDYA",
        "WRDYB" => "/W/RDYB",
        "CD" => "C/D",
        "BA" => "B/A",
        "VCC" => "+5V",
        "RxCA" or "TxCA" or "RxTxCB" or "DTRA" or "DTRB" or "RTSA" or "RTSB" or "CTSA" or "CTSB" or "DCDA" or "DCDB" or "SYNCA" or "SYNCB" or "RIA" or "RIB" => "/" + pin.Name,
        _ => pin.Name,
    };

    private static int Bits(int code) => (code & 3) switch { 0 => 5, 1 => 7, 2 => 6, _ => 8 };

    // ---- the logic --------------------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var now = context.Time;
        var reset = context.ReadInput(Reset, Levels).IsLow();

        if (!context.IsInitializing)
        {
            if (reset && !_lastReset)
            {
                _channels[0].Reset();
                _channels[1].Reset();
                ResetChain();
                NotifyValueChanged();
            }

            if (!reset)
            {
                for (var i = 0; i < 2; i++)
                {
                    Clocks(context, i, now);
                    Status(context, i);
                    Serial(context, i, now);
                }
            }
        }

        _lastReset = reset;
        EvaluateBus(context);
        Drive(context, now);
    }

    /// <summary>Each clock's period, from its rising edges.</summary>
    private void Clocks(IDigitalContext context, int index, double now)
    {
        var c = _channels[index];
        var tx = context.ReadInput(_transmitClock[index], Levels).IsHigh();
        var rx = context.ReadInput(_receiveClock[index], Levels).IsHigh();

        if (tx && !c.LastTxClock)
        {
            if (!double.IsNaN(c.LastTxEdge) && now > c.LastTxEdge) c.TxPeriod = now - c.LastTxEdge;
            c.LastTxEdge = now;
        }

        if (rx && !c.LastRxClock)
        {
            if (!double.IsNaN(c.LastRxEdge) && now > c.LastRxEdge) c.RxPeriod = now - c.LastRxEdge;
            c.LastRxEdge = now;
        }

        c.LastTxClock = tx;
        c.LastRxClock = rx;
    }

    /// <summary>/DCD, /CTS and /SYNC (/RI): a change raises an external/status interrupt, RR0's bits held as the change left them.</summary>
    private void Status(IDigitalContext context, int index)
    {
        var c = _channels[index];
        var dcd = context.ReadInput(_dcd[index], Levels).IsLow();
        var cts = context.ReadInput(_cts[index], Levels).IsLow();
        var sync = context.ReadInput(_sync[index], Levels).IsLow();

        var changed = c.StatusSeen && (dcd != c.Dcd || cts != c.Cts || sync != c.Sync);
        c.Dcd = dcd;
        c.Cts = cts;
        c.Sync = sync;
        c.StatusSeen = true;
        if (changed) External(index);
    }

    /// <summary>An external/status change: the interrupt if enabled, and RR0 held as the change left it.</summary>
    private void External(int index)
    {
        var c = _channels[index];
        if ((c.Wr[1] & 0x01) == 0) return;
        c.HeldStatus ??= Rr0(index);
        c.ExternalPending = true;
        NotifyValueChanged();
    }

    private void Configure(SerialChannel c)
    {
        var parity = (c.Wr[4] & 1) == 0 ? SerialParity.None : (c.Wr[4] & 2) != 0 ? SerialParity.Even : SerialParity.Odd;
        var stop = ((c.Wr[4] >> 2) & 3) switch { 1 => 1.0, 2 => 1.5, _ => 2.0 };
        c.Transmitter.DataBits = Bits(c.Wr[5] >> 5);
        c.Receiver.DataBits = Bits(c.Wr[3] >> 6);
        c.Transmitter.Parity = c.Receiver.Parity = parity;
        c.Transmitter.StopBits = c.Receiver.StopBits = stop;
        c.Transmitter.TransmitBitTime = c.TxPeriod * c.Factor;
        c.Receiver.ReceiveBitTime = c.RxPeriod * c.Factor;
    }

    private void Serial(IDigitalContext context, int index, double now)
    {
        var c = _channels[index];
        Configure(c);

        var rx = context.ReadInput(_receive[index], Levels) != LogicState.Low;
        var receiving = c.Asynchronous && (c.Wr[3] & 0x01) != 0 && (!c.AutoEnables || c.Dcd) && c.RxPeriod > 0;
        c.Receiver.Receive(now, rx, receiving, _received[index]);

        // The end of a break: the line back to marking.
        if (c.Break && rx)
        {
            c.Break = false;
            External(index);
        }

        var sending = c.Transmitter.Sending(now);
        if (!sending && !c.AllSent)
        {
            c.AllSent = true;
            NotifyValueChanged();
        }

        // The buffer into the shift register when it is free, the transmitter on and (with auto enables) /CTS low.
        var enabled = c.Asynchronous && (c.Wr[5] & 0x08) != 0 && (!c.AutoEnables || c.Cts) && c.TxPeriod > 0;
        if (enabled && c.Buffer is { } next && !sending)
        {
            c.Buffer = null;
            c.Transmitter.Send(now, next);
            c.AllSent = false;
            c.Sent.Add((byte)next);
            if ((c.Wr[1] & 0x02) != 0) c.TransmitPending = true;
            NotifyValueChanged();
        }
    }

    private void Received(int index, int value, bool framing, bool parity, bool brk)
    {
        var c = _channels[index];
        var b = (byte)(value & ((1 << c.Receiver.DataBits) - 1));
        c.Received.Add(b);

        if (brk)
        {
            if (!c.Break)
            {
                c.Break = true;
                External(index);
            }

            framing = parity = false;
            b = 0;
        }

        var overrun = false;
        if (c.Fifo.Count == 3)
        {
            // A fourth over a full FIFO: the last one is lost, and the new one carries the overrun.
            c.Fifo.RemoveAt(2);
            overrun = true;
        }

        c.Fifo.Add((b, parity, framing, overrun));
        if (c.ReceiveMode == 1 && c.FirstArmed)
        {
            c.FirstArmed = false;
            c.FirstLatched = true;
        }

        if (c.Fifo.Count == 1) TopArrived(c);
        NotifyValueChanged();
    }

    /// <summary>A character has reached the top of the FIFO: its errors latch, and make a special receive condition.</summary>
    private static void TopArrived(SerialChannel c)
    {
        if (c.Fifo.Count == 0) return;
        var (_, parity, framing, overrun) = c.Fifo[0];
        if (parity) c.ParityLatched = true;
        if (overrun) c.OverrunLatched = true;
        var special = framing || overrun || (parity && c.ReceiveMode == 2);
        if (special && c.ReceiveMode != 0) c.SpecialPending = true;
    }

    private void Drive(IDigitalContext context, double now)
    {
        var delay = DelayFor(context);
        for (var i = 0; i < 2; i++)
        {
            var c = _channels[i];
            var first = ChannelOutputs + (i * 4);
            var mark = (c.Wr[5] & 0x10) == 0 && c.Transmitter.Level(now);
            Request(context, first, mark ? LogicState.High : LogicState.Low, delay);
            Request(context, first + 1, (c.Wr[5] & 0x02) != 0 ? LogicState.Low : LogicState.High, delay);
            Request(context, first + 2, (c.Wr[5] & 0x80) != 0 ? LogicState.Low : LogicState.High, delay);
            Request(context, first + 3, WaitReady(i), delay);
        }
    }

    /// <summary>
    /// /W/RDY: as a ready, low while the transmit buffer is empty (or a character waits, on receive);
    /// as a wait, pulled low while the Z80 reads or writes the channel's data register and must wait
    /// for it — the buffer full, or nothing received. Disabled, a ready is high and a wait floats.
    /// </summary>
    private LogicState WaitReady(int index)
    {
        var c = _channels[index];
        var enabled = (c.Wr[1] & 0x80) != 0;
        var ready = (c.Wr[1] & 0x40) != 0;
        var receive = (c.Wr[1] & 0x20) != 0;
        if (!enabled) return ready ? LogicState.High : LogicState.HighImpedance;

        if (ready)
        {
            var go = receive ? c.Fifo.Count > 0 : c.Buffer is null;
            return go ? LogicState.Low : LogicState.High;
        }

        var dataRegister = index;      // B/A with C/D low
        var stall = CurrentAccess == dataRegister && (receive ? CurrentAccessReads && c.Fifo.Count == 0 : !CurrentAccessReads && c.Buffer is not null);
        return stall ? LogicState.Low : LogicState.HighImpedance;
    }

    // ---- the registers --------------------------------------------------------------

    private protected override int SelectedRegister(IDigitalContext context) =>
        (context.ReadInput(ChannelSelect, Levels).IsHigh() ? 1 : 0) | (context.ReadInput(ControlData, Levels).IsHigh() ? 2 : 0);

    private protected override byte ReadRegister(int register)
    {
        var index = register & 1;
        var c = _channels[index];
        if ((register & 2) == 0) return c.Fifo.Count > 0 ? c.Fifo[0].Value : (byte)0;

        return c.Pointer switch
        {
            1 => Rr1(c),
            2 when index == 1 => Rr2(),
            _ => Rr0(index),
        };
    }

    private protected override void AfterRead(int register)
    {
        var c = _channels[register & 1];
        if ((register & 2) != 0)
        {
            c.Pointer = 0;
            return;
        }

        if (c.Fifo.Count == 0) return;
        c.Fifo.RemoveAt(0);
        TopArrived(c);
        if (c.Fifo.Count == 0) c.FirstLatched = false;
    }

    private byte Rr0(int index)
    {
        var c = _channels[index];
        var value = (c.Fifo.Count > 0 ? 0x01 : 0) | (c.Buffer is null ? 0x04 : 0) | (c.Dcd ? 0x08 : 0) | (c.Sync ? 0x10 : 0) | (c.Cts ? 0x20 : 0) |
                    (c.Underrun ? 0x40 : 0) | (c.Break ? 0x80 : 0);
        if (c.HeldStatus is { } held) value = (value & 0x47) | (held & 0xB8);
        if (index == 0 && Enumerable.Range(0, 6).Any(Pending)) value |= 0x02;
        return (byte)value;
    }

    private static byte Rr1(SerialChannel c)
    {
        var framing = c.Fifo.Count > 0 && c.Fifo[0].Framing;
        return (byte)((c.AllSent ? 0x01 : 0) | (c.ParityLatched ? 0x10 : 0) | (c.OverrunLatched ? 0x20 : 0) | (framing ? 0x40 : 0));
    }

    /// <summary>RR2: the vector, with the highest pending source in bits 3-1 when status affects vector.</summary>
    private byte Rr2()
    {
        var vector = _channels[1].Wr[2];
        if ((_channels[1].Wr[1] & 0x04) == 0) return vector;
        for (var source = 0; source < 6; source++)
            if (Pending(source)) return VectorWithStatus(source);
        return (byte)((vector & 0xF1) | (3 << 1));
    }

    private byte VectorWithStatus(int source)
    {
        var channel = _channels[source / 3];
        var code = (source % 3) switch
        {
            0 => channel.SpecialPending ? 3 : 2,
            1 => 0,
            _ => 1,
        };
        if (source < 3) code |= 4;
        return (byte)((_channels[1].Wr[2] & 0xF1) | (code << 1));
    }

    private protected override void WriteRegister(int register, byte value)
    {
        var index = register & 1;
        var c = _channels[index];
        if ((register & 2) == 0)
        {
            c.Buffer = value;
            c.TransmitPending = false;
            return;
        }

        var pointer = c.Pointer;
        c.Pointer = 0;
        if (pointer != 0)
        {
            // The DART has no WR6 or WR7, and WR2 is channel B's alone.
            if (IsDart && pointer >= 6) return;
            if (pointer == 2 && index == 0) return;
            c.Wr[pointer] = value;
            if (pointer == 1 && (value & 0x18) == 0) c.FirstLatched = false;
            return;
        }

        c.Wr[0] = value;
        c.Pointer = value & 7;
        if ((value & 0xC0) == 0xC0) c.Underrun = false;

        switch ((value >> 3) & 7)
        {
            case 2:
                c.ExternalPending = false;
                c.HeldStatus = null;
                break;

            case 3:
            {
                var vector = c.Wr[2];
                c.Reset();
                c.Wr[2] = vector;
                break;
            }

            case 4:
                c.FirstArmed = true;
                break;

            case 5:
                c.TransmitPending = false;
                break;

            case 6:
                c.ParityLatched = c.OverrunLatched = false;
                c.SpecialPending = false;
                TopArrived(c);
                c.SpecialPending = false;
                break;

            case 7:
                if (index == 0) EndService();
                break;
        }
    }

    // ---- the chain ------------------------------------------------------------------

    private protected override bool Pending(int source)
    {
        var c = _channels[source / 3];
        return (source % 3) switch
        {
            0 => c.ReceiveMode != 0 && (c.SpecialPending || (c.Fifo.Count > 0 && (c.ReceiveMode != 1 || c.FirstLatched))),
            1 => c.TransmitPending && (c.Wr[1] & 0x02) != 0,
            _ => c.ExternalPending && (c.Wr[1] & 0x01) != 0,
        };
    }

    private protected override byte VectorFor(int source) =>
        (_channels[1].Wr[1] & 0x04) != 0 ? VectorWithStatus(source) : _channels[1].Wr[2];

    private protected override void Acknowledged(int source)
    {
        // The first-character interrupt is spent once taken; the others last until their cause is dealt with.
        var c = _channels[source / 3];
        if (source % 3 == 0 && c.ReceiveMode == 1 && !c.SpecialPending) c.FirstLatched = false;
    }

    /// <summary>The bit edges going out and the samples coming in, on both channels.</summary>
    public double? NextBreakpointAfter(double time)
    {
        double? next = null;
        void Consider(double? at)
        {
            if (at is { } t && (next is null || t < next)) next = t;
        }

        foreach (var c in _channels)
        {
            Consider(c.Transmitter.NextBreakpointAfter(time));
            Consider(c.Receiver.NextBreakpointAfter(time));
        }

        return next;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        foreach (var c in _channels)
        {
            c.Reset();
            c.Wr[2] = 0;
            c.StatusSeen = false;
            c.LastTxClock = c.LastRxClock = false;
            c.LastTxEdge = c.LastRxEdge = double.NaN;
            c.TxPeriod = c.RxPeriod = 0;
            c.Sent.Clear();
            c.Received.Clear();
        }

        _lastReset = false;
    }

    private sealed record ChannelState(byte[] Wr, (byte, bool, bool, bool)[] Fifo, int? Buffer, bool Pending, bool External, bool Special,
        double TxPeriod, double RxPeriod, byte[] Sent, byte[] Received);

    private sealed record RunState(ChannelState A, ChannelState B, ChainState Chain);

    private static ChannelState Capture(SerialChannel c) =>
        new([.. c.Wr], [.. c.Fifo], c.Buffer, c.TransmitPending, c.ExternalPending, c.SpecialPending, c.TxPeriod, c.RxPeriod, [.. c.Sent], [.. c.Received]);

    private static void Adopt(SerialChannel c, ChannelState s)
    {
        Array.Copy(s.Wr, c.Wr, 8);
        c.Fifo.Clear();
        c.Fifo.AddRange(s.Fifo);
        c.Buffer = s.Buffer;
        c.TransmitPending = s.Pending;
        c.ExternalPending = s.External;
        c.SpecialPending = s.Special;
        c.TxPeriod = s.TxPeriod;
        c.RxPeriod = s.RxPeriod;
        c.Sent = [.. s.Sent];
        c.Received = [.. s.Received];
    }

    public object CaptureRunState() => new RunState(Capture(_channels[0]), Capture(_channels[1]), CaptureChain());

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;
        Adopt(_channels[0], run.A);
        Adopt(_channels[1], run.B);
        AdoptChain(run.Chain);
        NotifyValueChanged();
    }
}

/// <summary>
/// The Zilog Z80 SIO/0: two serial channels for the Z80, here in their asynchronous modes — see
/// <see cref="Z80SerialController"/>. The SIO/1 and SIO/2 are the same chip bonded differently: the
/// SIO/1 gives up /DTRB, the SIO/2 /SYNCB, each to give channel B a receive and a transmit clock pin
/// of their own.
/// <para>
/// Pinout (DIP-40): 1 = D1, 2 = D3, 3 = D5, 4 = D7, 5 = /INT, 6 = IEI, 7 = IEO, 8 = /M1, 9 = +5V,
/// 10 = /W/RDYA, 11 = /SYNCA, 12 = RxDA, 13 = /RxCA, 14 = /TxCA, 15 = TxDA, 16 = /DTRA, 17 = /RTSA,
/// 18 = /CTSA, 19 = /DCDA, 20 = CLK, 21 = /RESET, 22 = /DCDB, 23 = /CTSB, 24 = /RTSB, 25 = /DTRB,
/// 26 = TxDB, 27 = /RxTxCB, 28 = RxDB, 29 = /SYNCB, 30 = /W/RDYB, 31 = GND, 32 = /RD, 33 = C/D,
/// 34 = B/A, 35 = /CE, 36 = /IORQ, 37 = D6, 38 = D4, 39 = D2, 40 = D0.
/// </para>
/// </summary>
public sealed class Z80Sio() : Z80SerialController(dart: false)
{
    public override string PartNumber => "Z80 SIO/0";
}

/// <summary>
/// The Zilog Z80 DART, dual asynchronous receiver/transmitter: the SIO's asynchronous channels with
/// nothing synchronous at all — no WR6 or WR7 — and a ring-indicator input, /RI, where the SIO has
/// /SYNC, its level in RR0's bit 4 and its changes an external/status interrupt.
/// <para>
/// Pinout (DIP-40): as the SIO/0's, with 11 = /RIA and 29 = /RIB.
/// </para>
/// </summary>
public sealed class Z80Dart() : Z80SerialController(dart: true)
{
    public override string PartNumber => "Z80 DART";
}
