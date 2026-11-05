using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Processors;

/// <summary>
/// The Motorola MC68681 DUART: two serial ports, a baud-rate generator, a 16-bit counter/timer, a
/// six-bit input port and an eight-bit output port, on a 68000's asynchronous bus — the serial chip
/// of most 68000 single-board computers.
/// <para>
/// <b>The bus.</b> /CS low selects it; RS1-RS4 (A1-A4 on a 68000, with the chip on the odd bytes,
/// D0-D7) pick one of sixteen registers, each a different one to read and to write. A read drives
/// the data lines while /CS is low; a write takes the data bus as /CS goes high again, and a read's
/// side effects (taking a byte from the receiver, the counter's start and stop commands) happen
/// then too. /DTACK is open drain: low <see cref="DtackDelay"/> after /CS, released with it. With
/// /INTR asserted, /IACK low puts the interrupt vector register on the bus, with /DTACK.
/// </para>
/// <list type="bullet">
/// <item><b>Read</b>: 0 MR1A/MR2A, 1 SRA, 3 RHRA, 4 IPCR, 5 ISR, 6 CUR, 7 CLR, 8 MR1B/MR2B, 9 SRB, B RHRB,
/// C IVR, D the input port, E start counter, F stop counter.</item>
/// <item><b>Write</b>: 0 MR1A/MR2A, 1 CSRA, 2 CRA, 3 THRA, 4 ACR, 5 IMR, 6 CTUR, 7 CTLR, 8 MR1B/MR2B,
/// 9 CSRB, A CRB, B THRB, C IVR, D OPCR, E set output port bits, F reset output port bits.</item>
/// </list>
/// <para>
/// <b>Each channel</b> has MR1 (bits per character, parity, RxRDY or FIFO-full interrupt, error
/// mode) and MR2 (stop length 0.563 to 2 bits, CTS enabling the transmitter on IP0/IP1, the
/// channel mode — normal, automatic echo, local loopback, remote loopback) behind one address, the
/// first access after a reset or a "reset MR pointer" command reaching MR1 and the rest MR2. CSR
/// picks the receive and transmit rates from the generator's two sets (ACR bit 7), 50 to 38,400
/// baud from a 3.6864 MHz crystal, or the counter/timer's output as a 16× clock. CR enables and
/// disables the receiver and transmitter and gives the commands: reset the MR pointer, the receiver,
/// the transmitter, the error status or the break interrupt; start and stop a break. The receiver
/// has a three-character FIFO, each character with its own received-break, framing and parity
/// errors (or, in block mode, accumulated until reset), and an overrun flag; the transmitter a
/// holding register in front of its shift register.
/// </para>
/// <para>
/// <b>The counter/timer</b> (ACR bits 4-6) counts the crystal ÷ 16, the crystal, either
/// transmitter's 1× clock, or IP2 (÷ 1 or ÷ 16). As a timer it reloads from CTUR:CTLR each time it
/// reaches zero, a square wave of period 2 × preload, which OP3 can carry; as a counter it counts
/// down from the preload after a start command and keeps going past zero until a stop. Each sets
/// the counter-ready interrupt (the timer once a period); the stop command clears it.
/// </para>
/// <para>
/// <b>Ports and interrupts.</b> IP0-IP5 read at the pins; IPCR has IP0-IP3 and a change flag for each,
/// cleared by reading it, which ACR bits 0-3 can turn into an interrupt. The output port register is
/// set and cleared a bit at a time by the two commands, and a set bit drives its pin low; OPCR can
/// give OP3 to the counter/timer and OP4-OP7 to /RxRDYA, /RxRDYB, /TxRDYA and /TxRDYB. ISR holds eight
/// interrupt sources, IMR masks them, and /INTR (open drain) is low while any unmasked one is set.
/// </para>
/// <para>
/// Simplifications: the crystal is not simulated — <see cref="CrystalFrequency"/> stands in for it
/// and X2 is there to be drawn; the external IP3-IP5 clocks (CSR codes E and F) and OP2's clock
/// outputs are not modelled; RTS is not driven automatically (MR1 bit 7, MR2 bit 5) — OP0 and OP1
/// are ordinary outputs; multidrop mode sends its address bit but does not filter on it; and the
/// bus timing is a fixed access time and /DTACK delay rather than the datasheet's every parameter.
/// </para>
/// <para>
/// Pinout (DIP-40): 1 = RS1, 2 = IP3, 3 = RS2, 4 = IP1, 5 = RS3, 6 = RS4, 7 = IP0, 8 = R/W, 9 = /DTACK,
/// 10 = RxDB, 11 = TxDB, 12 = OP1, 13 = OP3, 14 = OP5, 15 = OP7, 16 = D1, 17 = D3, 18 = D5, 19 = D7,
/// 20 = GND, 21 = /INTR, 22 = D6, 23 = D4, 24 = D2, 25 = D0, 26 = OP6, 27 = OP4, 28 = OP2, 29 = OP0,
/// 30 = TxDA, 31 = RxDA, 32 = X1/CLK, 33 = X2, 34 = /RESET, 35 = /CS, 36 = IP2, 37 = /IACK, 38 = IP5,
/// 39 = IP4, 40 = VCC.
/// </para>
/// </summary>
public sealed partial class Duart68681 : DigitalIc, IBreakpointSource, IRunState
{
    // Outputs: D0-D7 = 0-7, TxDA = 8, TxDB = 9, OP0-OP7 = 10-17, /INTR = 18, /DTACK = 19.
    private const int TxAIndex = 8, TxBIndex = 9, OpIndex = 10, IntrIndex = 18, DtackIndex = 19;

    /// <summary>The rates of the two baud-rate sets with a 3.6864 MHz crystal, by CSR code 0-C.</summary>
    private static readonly double[][] Rates =
    [
        [50, 110, 134.5, 200, 300, 600, 1200, 1050, 2400, 4800, 7200, 9600, 38400],
        [75, 110, 134.5, 150, 300, 600, 1200, 2000, 2400, 4800, 1800, 9600, 19200],
    ];

    private static readonly double[] StopLengths =
        [0.563, 0.625, 0.688, 0.750, 0.813, 0.875, 0.938, 1.000, 1.563, 1.625, 1.688, 1.750, 1.813, 1.875, 1.938, 2.000];

    private readonly Terminal[] _data = new Terminal[8];
    private readonly Terminal[] _rs = new Terminal[4];
    private readonly Terminal[] _ip = new Terminal[6];
    private readonly Terminal[] _op = new Terminal[8];

    private readonly Channel _a = new();
    private readonly Channel _b = new();

    private byte _acr, _isrLatched, _imr, _ivr = 0x0F, _opr, _opcr, _ipcr;
    private ushort _preload;
    private int _lastInputs = -1;

    // The counter/timer: running, its count at _ctTime, and the timer's output.
    private bool _ctRunning;
    private int _ctCount;
    private double _ctTime;
    private bool _ctOutput = true;
    private bool _ctReady;
    private bool _lastIp2 = true;
    private int _ip2Prescale;

    // The bus access in progress.
    private bool _selected;
    private bool _acknowledging;
    private int _register;
    private bool _reading;

    public Duart68681() : base(40)
    {
        PropagationDelay = 100e-9;

        var pins = new Terminal[41];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 40));

        _rs[0] = Pin(1, "RS1", TerminalType.Input);
        _ip[3] = Pin(2, "IP3", TerminalType.Input);
        _rs[1] = Pin(3, "RS2", TerminalType.Input);
        _ip[1] = Pin(4, "IP1", TerminalType.Input);
        _rs[2] = Pin(5, "RS3", TerminalType.Input);
        _rs[3] = Pin(6, "RS4", TerminalType.Input);
        _ip[0] = Pin(7, "IP0", TerminalType.Input);
        ReadWrite = Pin(8, "RW", TerminalType.Input);
        Dtack = Pin(9, "DTACK", TerminalType.Bidirectional);
        ReceiveB = Pin(10, "RxDB", TerminalType.Input);
        TransmitB = Pin(11, "TxDB", TerminalType.Output);
        _op[1] = Pin(12, "OP1", TerminalType.Output);
        _op[3] = Pin(13, "OP3", TerminalType.Output);
        _op[5] = Pin(14, "OP5", TerminalType.Output);
        _op[7] = Pin(15, "OP7", TerminalType.Output);
        _data[1] = Pin(16, "D1", TerminalType.Bidirectional);
        _data[3] = Pin(17, "D3", TerminalType.Bidirectional);
        _data[5] = Pin(18, "D5", TerminalType.Bidirectional);
        _data[7] = Pin(19, "D7", TerminalType.Bidirectional);
        Gnd = Pin(20, "GND", TerminalType.Ground);
        Interrupt = Pin(21, "INTR", TerminalType.Bidirectional);
        _data[6] = Pin(22, "D6", TerminalType.Bidirectional);
        _data[4] = Pin(23, "D4", TerminalType.Bidirectional);
        _data[2] = Pin(24, "D2", TerminalType.Bidirectional);
        _data[0] = Pin(25, "D0", TerminalType.Bidirectional);
        _op[6] = Pin(26, "OP6", TerminalType.Output);
        _op[4] = Pin(27, "OP4", TerminalType.Output);
        _op[2] = Pin(28, "OP2", TerminalType.Output);
        _op[0] = Pin(29, "OP0", TerminalType.Output);
        TransmitA = Pin(30, "TxDA", TerminalType.Output);
        ReceiveA = Pin(31, "RxDA", TerminalType.Input);
        Pin(32, "X1", TerminalType.Passive);
        Pin(33, "X2", TerminalType.Passive);
        Reset = Pin(34, "RESET", TerminalType.Input);
        ChipSelect = Pin(35, "CS", TerminalType.Input);
        _ip[2] = Pin(36, "IP2", TerminalType.Input);
        InterruptAcknowledge = Pin(37, "IACK", TerminalType.Input);
        _ip[5] = Pin(38, "IP5", TerminalType.Input);
        _ip[4] = Pin(39, "IP4", TerminalType.Input);
        Vcc = Pin(40, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [ChipSelect, ReadWrite, Reset, InterruptAcknowledge, .. _rs, ReceiveA, ReceiveB, .. _ip, .. _data],
            [.. _data, TransmitA, TransmitB, .. _op, Interrupt, Dtack]);

        HardwareReset();
    }

    public override string PartNumber => "MC68681";

    public override string ComponentType => "68681 DUART";

    public override bool LabelsPins => true;

    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>RS1-RS4, the register select: A1-A4 on a 68000.</summary>
    public IReadOnlyList<Terminal> RegisterSelect => _rs;

    /// <summary>IP0-IP5.</summary>
    public IReadOnlyList<Terminal> InputPort => _ip;

    /// <summary>OP0-OP7.</summary>
    public IReadOnlyList<Terminal> OutputPort => _op;

    public Terminal ChipSelect { get; }

    public Terminal ReadWrite { get; }

    /// <summary>/DTACK: open drain, low while it answers.</summary>
    public Terminal Dtack { get; }

    /// <summary>/INTR: open drain, low while an unmasked interrupt is set.</summary>
    public Terminal Interrupt { get; }

    /// <summary>/IACK: low in an interrupt acknowledge, for the vector.</summary>
    public Terminal InterruptAcknowledge { get; }

    /// <summary>/RESET, active low.</summary>
    public Terminal Reset { get; }

    public Terminal TransmitA { get; }

    public Terminal ReceiveA { get; }

    public Terminal TransmitB { get; }

    public Terminal ReceiveB { get; }

    /// <summary>The crystal on X1 and X2, in hertz: 3.6864 MHz gives the standard rates.</summary>
    [ObservableProperty]
    public partial double CrystalFrequency { get; set; } = 3.6864e6;

    /// <summary>How long after /CS falls /DTACK goes low: the chip's access time.</summary>
    public double DtackDelay => 150e-9;

    public override string ValueLabel => string.Create(CultureInfo.InvariantCulture,
        $"A {(_a.TransmitterOn || _a.ReceiverOn ? $"{TransmitBaud(_a):0} baud" : "off")}, B {(_b.TransmitterOn || _b.ReceiverOn ? $"{TransmitBaud(_b):0} baud" : "off")}");

    // ---- what a test or an agent wants to see -----------------------------------

    /// <summary>Every byte channel A has sent, in order.</summary>
    public IReadOnlyList<byte> TransmittedA => _a.Sent;

    public IReadOnlyList<byte> TransmittedB => _b.Sent;

    /// <summary>Every byte channel A has received, in order.</summary>
    public IReadOnlyList<byte> ReceivedA => _a.Received;

    public IReadOnlyList<byte> ReceivedB => _b.Received;

    /// <summary>What channel A has sent, as text, anything unprintable a dot.</summary>
    public string TransmittedTextA => Text(_a.Sent);

    public string TransmittedTextB => Text(_b.Sent);

    private static string Text(IEnumerable<byte> bytes) => string.Concat(bytes.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.'));

    public byte StatusA => Status(_a);

    public byte StatusB => Status(_b);

    public (byte Mr1, byte Mr2, byte Csr) ModeA => (_a.Mr1, _a.Mr2, _a.Csr);

    public (byte Mr1, byte Mr2, byte Csr) ModeB => (_b.Mr1, _b.Mr2, _b.Csr);

    /// <summary>The interrupt status register: what is set, masked or not.</summary>
    public byte InterruptStatus => Isr;

    public byte InterruptMask => _imr;

    public byte InterruptVector => _ivr;

    public byte AuxiliaryControl => _acr;

    public byte OutputPortRegister => _opr;

    public byte OutputPortConfiguration => _opcr;

    /// <summary>CTUR:CTLR.</summary>
    public ushort CounterPreload => _preload;

    /// <summary>The counter/timer's count at the last time point.</summary>
    public int CounterValue => _ctCount & 0xFFFF;

    public bool CounterRunning => _ctRunning;

    /// <summary>The timer's square wave, as OP3 would carry it.</summary>
    public bool CounterOutput => _ctOutput;

    /// <summary>A channel's transmit rate, in baud.</summary>
    public double BaudRateA => TransmitBaud(_a);

    public double BaudRateB => TransmitBaud(_b);

    /// <summary>The two channels' state as the agent tool reports it.</summary>
    internal (byte Mr1, byte Mr2, byte Csr, byte Status, bool Tx, bool Rx, int Fifo, string Mode) Describe(bool b)
    {
        var c = b ? _b : _a;
        var mode = ((c.Mr2 >> 6) & 3) switch { 0 => "normal", 1 => "automatic echo", 2 => "local loopback", _ => "remote loopback" };
        return (c.Mr1, c.Mr2, c.Csr, Status(c), c.TransmitterOn, c.ReceiverOn, c.Fifo.Count, mode);
    }

    // ---- the channels --------------------------------------------------------------

    private sealed class Channel
    {
        public byte Mr1, Mr2, Csr;
        public bool PointerAtMr2;
        public bool TransmitterOn, ReceiverOn;
        public int? Holding;
        public bool Breaking;
        public readonly Queue<(byte Value, byte Errors)> Fifo = new();
        public bool Overrun;
        public byte BlockErrors;
        public bool BreakChange;
        public readonly SerialLine Line = new();
        public readonly List<byte> Sent = [];
        public readonly List<byte> Received = [];
        public bool LastLevel = true;

        public void ResetAll()
        {
            Mr1 = Mr2 = Csr = 0;
            PointerAtMr2 = false;
            TransmitterOn = ReceiverOn = false;
            Holding = null;
            Breaking = false;
            Fifo.Clear();
            Overrun = false;
            BlockErrors = 0;
            BreakChange = false;
            Line.Reset();
        }
    }

    private double Scale => CrystalFrequency / 3.6864e6;

    private double Baud(int code)
    {
        if (code <= 0xC) return Rates[(_acr >> 7) & 1][code] * Scale;
        if (code == 0xD)
        {
            // The counter/timer's output as a 16× clock: two terminal counts a cycle.
            var tick = CounterTick();
            return tick > 0 && _preload > 0 ? 1.0 / (2 * _preload * tick) / 16 : 0;
        }

        return 0;
    }

    private double TransmitBaud(Channel c) => Baud(c.Csr & 0x0F);

    private double ReceiveBaud(Channel c) => Baud(c.Csr >> 4);

    private void Configure(Channel c)
    {
        var line = c.Line;
        line.DataBits = 5 + (c.Mr1 & 3);
        line.Parity = ((c.Mr1 >> 3) & 3) switch
        {
            0 => (c.Mr1 & 0x04) != 0 ? SerialParity.Odd : SerialParity.Even,
            1 or 3 => (c.Mr1 & 0x04) != 0 ? SerialParity.Mark : SerialParity.Space,
            _ => SerialParity.None,
        };
        line.StopBits = StopLengths[c.Mr2 & 0x0F];
        var tx = TransmitBaud(c);
        var rx = ReceiveBaud(c);
        line.TransmitBitTime = tx > 0 ? 1 / tx : 0;
        line.ReceiveBitTime = rx > 0 ? 1 / rx : 0;
    }

    private byte Status(Channel c)
    {
        var top = c.Fifo.Count > 0 ? c.Fifo.Peek().Errors : (byte)0;
        var errors = (c.Mr1 & 0x20) != 0 ? c.BlockErrors : top;
        return (byte)(errors
                      | (c.Overrun ? 0x10 : 0)
                      | (c.TransmitterOn && c.Holding is null && !c.Line.Sending(_now) ? 0x08 : 0)
                      | (c.TransmitterOn && c.Holding is null ? 0x04 : 0)
                      | (c.Fifo.Count >= 3 ? 0x02 : 0)
                      | (c.Fifo.Count > 0 ? 0x01 : 0));
    }

    private double _now;

    private byte Isr
    {
        get
        {
            var rxA = (_a.Mr1 & 0x40) != 0 ? _a.Fifo.Count >= 3 : _a.Fifo.Count > 0;
            var rxB = (_b.Mr1 & 0x40) != 0 ? _b.Fifo.Count >= 3 : _b.Fifo.Count > 0;
            return (byte)((_a.TransmitterOn && _a.Holding is null ? 0x01 : 0)
                          | (rxA ? 0x02 : 0)
                          | (_a.BreakChange ? 0x04 : 0)
                          | (_ctReady ? 0x08 : 0)
                          | (_b.TransmitterOn && _b.Holding is null ? 0x10 : 0)
                          | (rxB ? 0x20 : 0)
                          | (_b.BreakChange ? 0x40 : 0)
                          | (_isrLatched & 0x80));
        }
    }

    // ---- the logic -----------------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var now = context.Time;
        _now = now;

        if (context.ReadInput(Reset, Levels).IsLow())
        {
            HardwareReset();
            Drive(context, null, false);
            return;
        }

        var selected = context.ReadInput(ChipSelect, Levels).IsLow();
        var acknowledging = context.ReadInput(InterruptAcknowledge, Levels).IsLow() && (Isr & _imr) != 0;

        if (!context.IsInitializing)
        {
            Inputs(context);
            UpdateCounter(now);
            Serial(context, _a, ReceiveA, now);
            Serial(context, _b, ReceiveB, now);

            if (selected && !_selected)
            {
                // The access begins: what it is to is taken now.
                _register = 0;
                for (var i = 0; i < 4; i++)
                    if (context.ReadInput(_rs[i], Levels).IsHigh()) _register |= 1 << i;
                _reading = context.ReadInput(ReadWrite, Levels).IsHigh();
            }
            else if (!selected && _selected)
            {
                // ...and takes effect as /CS goes high.
                if (_reading) AfterRead(_register, now);
                else Write(_register, ReadBus(context), now);
            }
        }

        _selected = selected;
        _acknowledging = acknowledging && !selected;

        byte? drive = null;
        if (selected && _reading) drive = Read(_register, context);
        else if (_acknowledging) drive = _ivr;

        Drive(context, drive, selected || _acknowledging);
    }

    private byte ReadBus(IDigitalContext context)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(_data[i], Levels).IsHigh()) value |= 1 << i;
        return (byte)value;
    }

    private int InputPins(IDigitalContext context)
    {
        var value = 0;
        for (var i = 0; i < 6; i++)
            if (!context.ReadInput(_ip[i], Levels).IsLow()) value |= 1 << i;
        return value;
    }

    /// <summary>The input port: IP0-IP3's change flags, and IP2 as the counter's clock.</summary>
    private void Inputs(IDigitalContext context)
    {
        var pins = InputPins(context);
        if (_lastInputs >= 0)
        {
            var changed = (pins ^ _lastInputs) & 0x0F;
            if (changed != 0)
            {
                _ipcr |= (byte)(changed << 4);
                if (((changed << 4) & (_acr << 4) & 0xF0) != 0) _isrLatched |= 0x80;
                NotifyValueChanged();
            }

            // IP2 as the counter/timer's clock: a count on each falling edge.
            var ip2 = (pins & 0x04) != 0;
            if (_lastIp2 && !ip2 && Ip2Counts) CountIp2();
            _lastIp2 = ip2;
        }

        _lastInputs = pins;
        _ipcr = (byte)((_ipcr & 0xF0) | (pins & 0x0F));
    }

    private int CounterMode => (_acr >> 4) & 7;

    private bool TimerMode => CounterMode >= 4;

    private bool Ip2Counts => CounterMode is 0 or 4 or 5;

    /// <summary>Seconds per count for a time-based source; zero for IP2.</summary>
    private double CounterTick() => CounterMode switch
    {
        1 => _a.Line.TransmitBitTime > 0 ? _a.Line.TransmitBitTime : 0,
        2 => _b.Line.TransmitBitTime > 0 ? _b.Line.TransmitBitTime : 0,
        3 or 7 => 16 / CrystalFrequency,
        6 => 1 / CrystalFrequency,
        _ => 0,
    };

    private void CountIp2()
    {
        if (CounterMode == 5)
        {
            if (++_ip2Prescale < 16) return;
            _ip2Prescale = 0;
        }

        Tick(1);
    }

    /// <summary>Counts the counter/timer on by <paramref name="ticks"/>: reloads, toggles and ready flags as it goes.</summary>
    private void Tick(long ticks)
    {
        if (!_ctRunning || ticks <= 0) return;

        if (TimerMode)
        {
            var half = _preload == 0 ? 0x10000 : _preload;
            while (ticks > 0)
            {
                if (ticks < _ctCount)
                {
                    _ctCount -= (int)ticks;
                    break;
                }

                ticks -= _ctCount;
                _ctCount = half;
                _ctOutput = !_ctOutput;
                if (!_ctOutput) _ctReady = true;
            }

            return;
        }

        if (ticks >= _ctCount && _ctCount > 0) _ctReady = true;
        _ctCount = (int)((_ctCount - ticks) & 0xFFFF);
        if (_ctCount == 0 && ticks > 0) _ctReady = true;
    }

    /// <summary>Brings a time-based counter/timer up to <paramref name="now"/>.</summary>
    private void UpdateCounter(double now)
    {
        var tick = CounterTick();
        if (!_ctRunning || tick <= 0)
        {
            _ctTime = now;
            return;
        }

        var ticks = (long)Math.Floor(((now - _ctTime) / tick) + 1e-6);
        if (ticks <= 0) return;
        _ctTime += ticks * tick;
        Tick(ticks);
    }

    private void StartCounter(double now)
    {
        _ctTime = now;
        _ctCount = _preload == 0 ? 0x10000 : _preload;
        _ctRunning = true;
        if (TimerMode) _ctOutput = true;
    }

    /// <summary>One channel's serial lines: what arrives on RxD, and the transmitter moving on.</summary>
    private void Serial(IDigitalContext context, Channel c, Terminal receive, double now)
    {
        Configure(c);
        var mode = (c.Mr2 >> 6) & 3;

        // Local loopback feeds the receiver from the transmitter, whatever is on RxD.
        var rx = mode == 2 ? c.Line.Level(now) && !c.Breaking : context.ReadInput(receive, Levels) != LogicState.Low;
        c.Line.Receive(now, rx, c.ReceiverOn, (value, framing, parity, brk) => Received(c, (byte)value, framing, parity, brk));

        // The transmitter: the holding register into the shift register when it is empty.
        if (!c.Line.Sending(now) && c.Holding is { } next && c.TransmitterOn && c.Line.TransmitBitTime > 0)
        {
            var ctsPin = c == _a ? _ip[0] : _ip[1];
            var cts = (c.Mr2 & 0x10) == 0 || context.ReadInput(ctsPin, Levels).IsLow();
            if (cts)
            {
                c.Holding = null;
                c.Line.Send(now, next);
                c.Sent.Add((byte)next);
                NotifyValueChanged();
            }
        }
    }

    private void Received(Channel c, byte value, bool framing, bool parity, bool brk)
    {
        var mask = (byte)((1 << c.Line.DataBits) - 1);
        value &= mask;
        var errors = (byte)((brk ? 0x80 : 0) | (framing && !brk ? 0x40 : 0) | (parity && !brk ? 0x20 : 0));
        if (brk) c.BreakChange = true;
        c.BlockErrors |= errors;
        c.Received.Add(value);

        if (c.Fifo.Count >= 3) c.Overrun = true;
        else c.Fifo.Enqueue((brk ? (byte)0 : value, errors));

        // Automatic echo sends each character straight back out.
        if (((c.Mr2 >> 6) & 3) == 1 && !brk) c.Holding ??= value;
        NotifyValueChanged();
    }

    /// <summary>What a read of a register returns. Its side effects wait for /CS to go high.</summary>
    private byte Read(int register, IDigitalContext context)
    {
        UpdateCounter(_now);
        return register switch
        {
            0 => _a.PointerAtMr2 ? _a.Mr2 : _a.Mr1,
            1 => Status(_a),
            3 => _a.Fifo.Count > 0 ? _a.Fifo.Peek().Value : (byte)0,
            4 => _ipcr,
            5 => Isr,
            6 => (byte)(CounterValue >> 8),
            7 => (byte)CounterValue,
            8 => _b.PointerAtMr2 ? _b.Mr2 : _b.Mr1,
            9 => Status(_b),
            0xB => _b.Fifo.Count > 0 ? _b.Fifo.Peek().Value : (byte)0,
            0xC => _ivr,
            0xD => (byte)(InputPins(context) | 0xC0),
            _ => 0xFF,
        };
    }

    private void AfterRead(int register, double now)
    {
        switch (register)
        {
            case 0:
                _a.PointerAtMr2 = true;
                break;
            case 8:
                _b.PointerAtMr2 = true;
                break;
            case 3:
                if (_a.Fifo.Count > 0) _a.Fifo.Dequeue();
                break;
            case 0xB:
                if (_b.Fifo.Count > 0) _b.Fifo.Dequeue();
                break;
            case 4:
                _ipcr &= 0x0F;
                _isrLatched &= 0x7F;
                break;
            case 0xE:
                StartCounter(now);
                break;
            case 0xF:
                // Stop: a counter stops; either way the ready flag clears.
                if (!TimerMode) _ctRunning = false;
                _ctReady = false;
                break;
        }

        NotifyValueChanged();
    }

    private void Write(int register, byte value, double now)
    {
        switch (register)
        {
            case 0: WriteMode(_a, value); break;
            case 8: WriteMode(_b, value); break;
            case 1: _a.Csr = value; break;
            case 9: _b.Csr = value; break;
            case 2: Command(_a, value); break;
            case 0xA: Command(_b, value); break;
            case 3: _a.Holding = value; break;
            case 0xB: _b.Holding = value; break;
            case 4:
            {
                var wasTimer = TimerMode;
                _acr = value;
                if (TimerMode && !wasTimer) StartCounter(now);
                if (!TimerMode && wasTimer) _ctRunning = false;
                break;
            }

            case 5: _imr = value; break;
            case 6: _preload = (ushort)((_preload & 0x00FF) | (value << 8)); break;
            case 7: _preload = (ushort)((_preload & 0xFF00) | value); break;
            case 0xC: _ivr = value; break;
            case 0xD: _opcr = value; break;
            case 0xE: _opr |= value; break;
            case 0xF: _opr &= (byte)~value; break;
        }

        NotifyValueChanged();
    }

    private static void WriteMode(Channel c, byte value)
    {
        if (c.PointerAtMr2) c.Mr2 = value;
        else
        {
            c.Mr1 = value;
            c.PointerAtMr2 = true;
        }
    }

    private void Command(Channel c, byte value)
    {
        switch ((value >> 4) & 7)
        {
            case 1: c.PointerAtMr2 = false; break;
            case 2:
                c.ReceiverOn = false;
                c.Fifo.Clear();
                c.Overrun = false;
                c.BlockErrors = 0;
                break;
            case 3:
                c.TransmitterOn = false;
                c.Holding = null;
                c.Line.StopSending();
                break;
            case 4:
                c.Overrun = false;
                c.BlockErrors = 0;
                break;
            case 5: c.BreakChange = false; break;
            case 6: c.Breaking = true; break;
            case 7: c.Breaking = false; break;
        }

        switch ((value >> 2) & 3)
        {
            case 1: c.TransmitterOn = true; break;
            case 2: c.TransmitterOn = false; break;
        }

        switch (value & 3)
        {
            case 1: c.ReceiverOn = true; break;
            case 2: c.ReceiverOn = false; break;
        }
    }

    private bool TransmitLevel(Channel c, double now, Terminal receive, IDigitalContext context)
    {
        var mode = (c.Mr2 >> 6) & 3;
        if (c.Breaking) return false;

        // Remote loopback sends RxD straight back out; local loopback holds TxD marking.
        if (mode == 3) return context.ReadInput(receive, Levels) != LogicState.Low;
        if (mode == 2) return true;
        return c.Line.Level(now);
    }

    private void Drive(IDigitalContext context, byte? data, bool answering)
    {
        var delay = DelayFor(context);
        var now = context.Time;

        for (var i = 0; i < 8; i++)
            Request(context, i, data is { } d ? ((d >> i) & 1) != 0 ? LogicState.High : LogicState.Low : LogicState.HighImpedance,
                data is null ? Math.Min(delay, 40e-9) : delay);

        Request(context, TxAIndex, TransmitLevel(_a, now, ReceiveA, context) ? LogicState.High : LogicState.Low, delay);
        Request(context, TxBIndex, TransmitLevel(_b, now, ReceiveB, context) ? LogicState.High : LogicState.Low, delay);

        for (var i = 0; i < 8; i++)
        {
            var low = (_opr & (1 << i)) != 0;
            switch (i)
            {
                case 3 when ((_opcr >> 2) & 3) == 1:
                    low = TimerMode ? !_ctOutput : _ctReady;
                    break;
                case 4 when (_opcr & 0x10) != 0:
                    low = (Isr & 0x02) != 0;
                    break;
                case 5 when (_opcr & 0x20) != 0:
                    low = (Isr & 0x20) != 0;
                    break;
                case 6 when (_opcr & 0x40) != 0:
                    low = (Isr & 0x01) != 0;
                    break;
                case 7 when (_opcr & 0x80) != 0:
                    low = (Isr & 0x10) != 0;
                    break;
            }

            Request(context, OpIndex + i, low ? LogicState.Low : LogicState.High, delay);
        }

        Request(context, IntrIndex, (Isr & _imr) != 0 ? LogicState.Low : LogicState.HighImpedance, delay);
        Request(context, DtackIndex, answering ? LogicState.Low : LogicState.HighImpedance,
            answering ? (context.IsInitializing ? 0 : DtackDelay) : Math.Min(delay, 40e-9));
    }

    /// <summary>The bit edges going out, the samples coming in, and the counter/timer's terminal counts.</summary>
    public double? NextBreakpointAfter(double time)
    {
        double? next = null;

        void Consider(double? at)
        {
            if (at is { } t && t > time + 1e-12 && (next is null || t < next)) next = t;
        }

        Consider(_a.Line.NextBreakpointAfter(time));
        Consider(_b.Line.NextBreakpointAfter(time));

        var tick = CounterTick();
        if (_ctRunning && tick > 0 && _ctCount > 0) Consider(_ctTime + (_ctCount * tick));
        return next;
    }

    private void HardwareReset()
    {
        _a.ResetAll();
        _b.ResetAll();
        _acr = _isrLatched = _imr = _opr = _opcr = _ipcr = 0;
        _ivr = 0x0F;
        _ctRunning = false;
        _ctReady = false;
        _ctOutput = true;
        _ip2Prescale = 0;
        _selected = false;
        _acknowledging = false;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        HardwareReset();
        _preload = 0;
        _ctCount = 0;
        _ctTime = 0;
        _lastInputs = -1;
        _lastIp2 = true;
        _a.Sent.Clear();
        _b.Sent.Clear();
        _a.Received.Clear();
        _b.Received.Clear();
    }

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "RW" => "R/W",
        "DTACK" or "INTR" or "IACK" or "RESET" or "CS" => "/" + pin.Name,
        "X1" => "X1/CLK",
        _ => pin.Name,
    };

    private sealed record RunState(byte[] SentA, byte[] SentB, byte[] ReceivedA, byte[] ReceivedB, byte Isr, byte Imr, byte Opr, byte Acr, ushort Preload);

    /// <summary>What an agent's run hands back: the bytes each channel moved, and the registers that matter.</summary>
    public object CaptureRunState() =>
        new RunState([.. _a.Sent], [.. _b.Sent], [.. _a.Received], [.. _b.Received], _isrLatched, _imr, _opr, _acr, _preload);

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;
        _a.Sent.Clear();
        _a.Sent.AddRange(run.SentA);
        _b.Sent.Clear();
        _b.Sent.AddRange(run.SentB);
        _a.Received.Clear();
        _a.Received.AddRange(run.ReceivedA);
        _b.Received.Clear();
        _b.Received.AddRange(run.ReceivedB);
        _isrLatched = run.Isr;
        _imr = run.Imr;
        _opr = run.Opr;
        _acr = run.Acr;
        _preload = run.Preload;
        NotifyValueChanged();
    }

    partial void OnCrystalFrequencyChanged(double value) => NotifyValueChanged();
}
