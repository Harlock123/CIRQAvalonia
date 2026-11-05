using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Processors;

/// <summary>
/// The Motorola MC68901 MFP, multi-function peripheral: eight general-purpose I/O lines with
/// interrupts on either edge, a sixteen-source vectored interrupt controller, four 8-bit timers and a
/// USART — the chip behind the Atari ST's keyboard, MIDI timing and serial port.
/// <para>
/// <b>The bus.</b> /CS and /DS low together are an access; RS1-RS5 (A1-A5, the chip on the odd bytes)
/// pick one of 24 registers. /DTACK goes low on the second CLK rising edge of an access and is
/// released at its end, when a write takes the bus. /IACK with /DS is an interrupt acknowledge: with
/// IEI low and an interrupt pending, the chip puts its vector on the bus with /DTACK; with none, it
/// passes the acknowledge on by taking IEO low, as a daisy chain of MFPs needs.
/// </para>
/// <list type="bullet">
/// <item>$00 GPDR, $01 AER, $02 DDR — the GPIP lines, their active edges, their directions.</item>
/// <item>$03/$04 IERA/B, $05/$06 IPRA/B, $07/$08 ISRA/B, $09/$0A IMRA/B — enable, pending, in service
/// and mask, A for channels 15-8 and B for 7-0. $0B VR: the vector's top four bits and S.</item>
/// <item>$0C TACR, $0D TBCR, $0E TCDCR, $0F-$12 TADR-TDDR — the timers.</item>
/// <item>$13 SCR, $14 UCR, $15 RSR, $16 TSR, $17 UDR — the USART.</item>
/// </list>
/// <para>
/// <b>Interrupts.</b> Sixteen channels, 15 the highest: GPIP7, GPIP6, timer A, receive buffer full,
/// receive error, transmit buffer empty, transmit error, timer B, GPIP5, GPIP4, timer C, timer D and
/// GPIP3-GPIP0. An event on an enabled channel sets it pending; a pending, unmasked channel pulls
/// /IRQ (open drain) low unless one of the same or higher priority is in service. The
/// acknowledge's vector is VR's top four bits with the channel in the bottom four, and clears the
/// pending bit; with S (VR bit 3) set it also sets the in-service bit, which the handler clears by
/// writing a 0 to it (software end of interrupt), and without S nothing is held. Writing a 0 to a
/// pending or in-service bit clears it; disabling a channel clears its pending bit.
/// </para>
/// <para>
/// <b>GPIP.</b> DDR makes a line an output driving GPDR; an input reads the pin. An input's edge —
/// rising with its AER bit set, falling with it clear — is an event on its channel. GPIP4 and GPIP3
/// are also timer A's and B's in event and pulse-width modes, on TAI and TBI.
/// </para>
/// <para>
/// <b>Timers.</b> Each counts down from its data register (0 meaning 256) and at the end of a count
/// reloads it, toggles its output (TAO-TDO) and makes an event. Delay mode counts the timer crystal
/// (<see cref="TimerClockFrequency"/>) through a prescaler of 4, 10, 16, 50, 64, 100 or 200; timers A
/// and B can also count edges on TAI/TBI (event mode, the edge by AER bits 4 and 3) or count the
/// prescaled clock only while TAI/TBI is active (pulse-width mode, with the pulse's end an event on
/// GPIP4/GPIP3). Writing a data register while the timer is stopped loads the count too; while it
/// runs only the reload. A read gives the count. Bit 4 of TACR or TBCR holds the output low.
/// </para>
/// <para>
/// <b>The USART</b> is clocked by its TC and RC pins — usually wired to TDO — at 16 clocks a bit in
/// ÷16 mode (UCR bit 7). Asynchronous frames of five to eight bits, odd, even or no parity, and one,
/// one and a half or two stop bits (UCR); the receiver checks the start bit at its middle and samples
/// each bit there, with buffer full, overrun, parity, frame and break in RSR and its enable in bit 0;
/// the transmitter has a buffer (TSR bit 7 empty), its enable in bit 0, a break (bit 3) and the
/// level SO holds while disabled (bits 1-2: high-impedance, low, high, or looped back to SI).
/// </para>
/// <para>
/// Simplifications: the timer crystal is not simulated — <see cref="TimerClockFrequency"/> stands in
/// for it, and XTAL1 and XTAL2 are there to be drawn; the USART's synchronous mode, sync character,
/// ÷1 clock and the receiver/transmitter ready outputs (RR, TR, DMA) are not modelled — RR and TR
/// stay high; nor is the documented quirk of an AER change itself making an edge.
/// </para>
/// <para>
/// Pinout (DIP-48): 1 = R/W, 2-6 = RS1-RS5, 7 = TC, 8 = SO, 9 = SI, 10 = RC, 11 = VCC, 12 = NC,
/// 13 = TAO, 14 = TBO, 15 = TCO, 16 = TDO, 17 = XTAL1, 18 = XTAL2, 19 = TAI, 20 = TBI, 21 = /RESET,
/// 22-29 = I0-I7, 30 = TR, 31 = RR, 32 = /IRQ, 33 = IEO, 34 = IEI, 35 = CLK, 36 = GND, 37-44 = D0-D7,
/// 45 = /IACK, 46 = /DTACK, 47 = /DS, 48 = /CS.
/// </para>
/// </summary>
public sealed partial class Mfp68901 : DigitalIc, IBreakpointSource, IRunState
{
    // Outputs: D0-D7 = 0-7, I0-I7 = 8-15, TAO-TDO = 16-19, SO = 20, /IRQ = 21, IEO = 22, /DTACK = 23, TR = 24, RR = 25.
    private const int GpipIndex = 8, TimerOutIndex = 16, SoIndex = 20, IrqIndex = 21, IeoIndex = 22, DtackIndex = 23, TrIndex = 24, RrIndex = 25;

    private static readonly int[] Prescales = [0, 4, 10, 16, 50, 64, 100, 200];

    // Channel numbers.
    private const int ChTimerA = 13, ChReceiveFull = 12, ChReceiveError = 11, ChTransmitEmpty = 10, ChTransmitError = 9, ChTimerB = 8, ChTimerC = 5, ChTimerD = 4;
    private static readonly int[] GpipChannels = [0, 1, 2, 3, 6, 7, 14, 15];

    private readonly Terminal[] _data = new Terminal[8];
    private readonly Terminal[] _rs = new Terminal[5];
    private readonly Terminal[] _gpip = new Terminal[8];
    private readonly Terminal[] _timerOut = new Terminal[4];

    private byte _gpdr, _aer, _ddr, _vr, _scr, _ucr, _rsr, _tsr, _udrIn, _udrOut;
    private int _ier, _ipr, _isr, _imr;
    private readonly Timer[] _timers = [new(), new(), new(), new()];
    private int _lastPins = -1;

    // The USART, clocked by TC and RC.
    private bool _lastTc, _lastRc;
    private bool _transmitFull;
    private int[] _txBits = [];
    private int _txIndex = -1;
    private int _txClocks;
    private bool _so = true;
    private int _rxClocks = -1;
    private int _rxIndex;
    private int _rxShift;
    private bool _rxParity;
    private bool _rxAnyHigh;

    private bool _selected;
    private bool _acknowledging;
    private int _register;
    private bool _reading;
    private int _dtackClocks;
    private byte _vector;
    private bool _passOn;
    private LogicState _lastClock = LogicState.Unknown;
    private double _now;

    public Mfp68901() : base(48)
    {
        PropagationDelay = 60e-9;

        var pins = new Terminal[49];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 48));

        ReadWrite = Pin(1, "RW", TerminalType.Input);
        for (var i = 0; i < 5; i++) _rs[i] = Pin(2 + i, $"RS{i + 1}", TerminalType.Input);
        TransmitClock = Pin(7, "TC", TerminalType.Input);
        SerialOut = Pin(8, "SO", TerminalType.Output);
        SerialIn = Pin(9, "SI", TerminalType.Input);
        ReceiveClock = Pin(10, "RC", TerminalType.Input);
        Vcc = Pin(11, "VCC", TerminalType.Power);
        Pin(12, "NC", TerminalType.Passive);
        for (var i = 0; i < 4; i++) _timerOut[i] = Pin(13 + i, $"T{(char)('A' + i)}O", TerminalType.Output);
        Pin(17, "XTAL1", TerminalType.Passive);
        Pin(18, "XTAL2", TerminalType.Passive);
        TimerAInput = Pin(19, "TAI", TerminalType.Input);
        TimerBInput = Pin(20, "TBI", TerminalType.Input);
        Reset = Pin(21, "RESET", TerminalType.Input);
        for (var i = 0; i < 8; i++) _gpip[i] = Pin(22 + i, $"I{i}", TerminalType.Bidirectional);
        var tr = Pin(30, "TR", TerminalType.Output);
        var rr = Pin(31, "RR", TerminalType.Output);
        Irq = Pin(32, "IRQ", TerminalType.Bidirectional);
        InterruptEnableOut = Pin(33, "IEO", TerminalType.Output);
        InterruptEnableIn = Pin(34, "IEI", TerminalType.Input);
        Clock = Pin(35, "CLK", TerminalType.Input);
        Gnd = Pin(36, "GND", TerminalType.Ground);
        for (var i = 0; i < 8; i++) _data[i] = Pin(37 + i, $"D{i}", TerminalType.Bidirectional);
        InterruptAcknowledge = Pin(45, "IACK", TerminalType.Input);
        Dtack = Pin(46, "DTACK", TerminalType.Bidirectional);
        DataStrobe = Pin(47, "DS", TerminalType.Input);
        ChipSelect = Pin(48, "CS", TerminalType.Input);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [Clock, ChipSelect, DataStrobe, ReadWrite, Reset, InterruptAcknowledge, InterruptEnableIn, .. _rs, TransmitClock, ReceiveClock, SerialIn, TimerAInput, TimerBInput, .. _gpip, .. _data],
            [.. _data, .. _gpip, .. _timerOut, SerialOut, Irq, InterruptEnableOut, Dtack, tr, rr]);
    }

    public override string PartNumber => "MC68901";

    public override string ComponentType => "68901 MFP";

    public override bool LabelsPins => true;

    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>RS1-RS5: A1-A5 on a 68000.</summary>
    public IReadOnlyList<Terminal> RegisterSelect => _rs;

    /// <summary>I0-I7, the general-purpose I/O lines.</summary>
    public IReadOnlyList<Terminal> Gpip => _gpip;

    /// <summary>TAO, TBO, TCO, TDO.</summary>
    public IReadOnlyList<Terminal> TimerOutputs => _timerOut;

    public Terminal ChipSelect { get; }

    /// <summary>/DS: /LDS on a 68000, the chip being on the low byte.</summary>
    public Terminal DataStrobe { get; }

    public Terminal ReadWrite { get; }

    public Terminal Dtack { get; }

    public Terminal Irq { get; }

    public Terminal InterruptAcknowledge { get; }

    /// <summary>IEI: low lets this chip answer an acknowledge. Tie it low on the first chip of a chain.</summary>
    public Terminal InterruptEnableIn { get; }

    public Terminal InterruptEnableOut { get; }

    public Terminal Clock { get; }

    public Terminal Reset { get; }

    public Terminal TimerAInput { get; }

    public Terminal TimerBInput { get; }

    public Terminal TransmitClock { get; }

    public Terminal ReceiveClock { get; }

    public Terminal SerialOut { get; }

    public Terminal SerialIn { get; }

    /// <summary>The timer crystal on XTAL1 and XTAL2, in hertz: 2.4576 MHz gives the standard baud rates.</summary>
    [ObservableProperty]
    public partial double TimerClockFrequency { get; set; } = 2.4576e6;

    public override string ValueLabel => string.Create(CultureInfo.InvariantCulture,
        $"GPIP {Pins():X2}, IPR {_ipr:X4}{(TransmittedBytes.Count > 0 ? $", sent {TransmittedBytes.Count}" : "")}");

    // ---- what a test or an agent wants to see -----------------------------------

    public byte GpipData => _gpdr;

    public byte ActiveEdges => _aer;

    public byte Direction => _ddr;

    /// <summary>IERA:IERB, channel 15 in bit 15.</summary>
    public int InterruptEnable => _ier;

    public int InterruptPending => _ipr;

    public int InterruptInService => _isr;

    public int InterruptMask => _imr;

    public byte VectorRegister => _vr;

    /// <summary>A timer's control register (A-D as 0-3), the count, the reload and how many times it has run out.</summary>
    public (int Control, int Count, int Reload, long Timeouts) TimerState(int timer)
    {
        Bring(timer, _now);
        var t = _timers[timer];
        return (t.Control, t.Count, t.Reload, t.Timeouts);
    }

    public byte UsartControl => _ucr;

    public byte ReceiverStatus => _rsr;

    public byte TransmitterStatus => (byte)(_tsr | (_transmitFull ? 0 : 0x80));

    public List<byte> TransmittedBytes { get; } = [];

    public List<byte> ReceivedBytes { get; } = [];

    public string TransmittedText => string.Concat(TransmittedBytes.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.'));

    // ---- the timers ----------------------------------------------------------------

    private sealed class Timer
    {
        public int Control;
        public int Count = 256;
        public int Reload = 256;
        public double Last;
        public double Phase;    // crystal cycles counted toward the next prescaled tick
        public bool Output;
        public bool LastInput;
        public long Timeouts;
        public bool Event;

        public void Clear()
        {
            Control = 0;
            Output = false;
            Phase = 0;
            Event = false;
        }
    }

    private int TimerMode(int timer) => _timers[timer].Control & 0x0F;

    private bool Delay(int timer) => TimerMode(timer) is >= 1 and <= 7;

    private bool PulseWidth(int timer) => TimerMode(timer) >= 9;

    private bool EventCount(int timer) => TimerMode(timer) == 8;

    private int Prescale(int timer) => Prescales[TimerMode(timer) & 7];

    private static readonly int[] TimerChannels = [ChTimerA, ChTimerB, ChTimerC, ChTimerD];

    /// <summary>One count of a timer: at the end of its count it reloads, toggles its output and makes its event.</summary>
    private void CountDown(int timer)
    {
        var t = _timers[timer];
        if (--t.Count > 0) return;
        t.Count = t.Reload;
        t.Output = !t.Output;
        t.Timeouts++;
        Event(TimerChannels[timer]);
        NotifyValueChanged();
    }

    /// <summary>
    /// Brings a crystal-driven timer up to <paramref name="now"/>: the prescaled ticks since it was
    /// last looked at. In pulse-width mode only while its input is active.
    /// </summary>
    private void Bring(int timer, double now)
    {
        var t = _timers[timer];
        if (now <= t.Last)
        {
            t.Last = Math.Max(t.Last, now);
            return;
        }

        var counting = Delay(timer) || (PulseWidth(timer) && t.Event);
        if (counting)
        {
            var cycles = ((now - t.Last) * TimerClockFrequency) + t.Phase;
            var prescale = Prescale(timer);
            var ticks = (long)Math.Floor((cycles / prescale) + 1e-9);
            t.Phase = cycles - (ticks * prescale);
            for (long i = 0; i < ticks; i++) CountDown(timer);
        }
        else
        {
            t.Phase = 0;
        }

        t.Last = now;
    }

    /// <summary>When a crystal-driven timer next runs out, for the solver to land on.</summary>
    private double? NextTimeout(int timer)
    {
        var t = _timers[timer];
        if (!(Delay(timer) || (PulseWidth(timer) && t.Event))) return null;
        var cycles = (t.Count * (double)Prescale(timer)) - t.Phase;
        return t.Last + (cycles / TimerClockFrequency);
    }

    // ---- interrupts -------------------------------------------------------------------

    private void Event(int channel)
    {
        if ((_ier & (1 << channel)) != 0) _ipr |= 1 << channel;
    }

    /// <summary>The highest pending, unmasked channel not blocked by one in service, or −1.</summary>
    private int Requesting
    {
        get
        {
            var candidates = _ipr & _imr;
            for (var channel = 15; channel >= 0; channel--)
            {
                if ((_isr & (1 << channel)) != 0) return -1;
                if ((candidates & (1 << channel)) != 0) return channel;
            }

            return -1;
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
            Drive(context, null, false, false);
            return;
        }

        var clock = context.ReadInput(Clock, Levels);
        var rising = _lastClock.IsLow() && clock.IsHigh();
        _lastClock = clock;

        var strobe = context.ReadInput(DataStrobe, Levels).IsLow();
        var selected = strobe && context.ReadInput(ChipSelect, Levels).IsLow();
        var iack = strobe && context.ReadInput(InterruptAcknowledge, Levels).IsLow();

        if (!context.IsInitializing)
        {
            for (var i = 0; i < 4; i++) Bring(i, now);
            Inputs(context);
            Usart(context);
            if (rising && (_selected || _acknowledging)) _dtackClocks++;

            if (selected && !_selected)
            {
                _register = 0;
                for (var i = 0; i < 5; i++)
                    if (context.ReadInput(_rs[i], Levels).IsHigh()) _register |= 1 << i;
                _reading = context.ReadInput(ReadWrite, Levels).IsHigh();
                _dtackClocks = 0;
            }
            else if (!selected && _selected)
            {
                if (_reading) AfterRead(_register);
                else Write(_register, ReadBus(context), now);
            }

            if (iack && !_acknowledging)
            {
                // The acknowledge: this chip's vector if it is enabled in the chain and has a request.
                var channel = Requesting;
                var enabled = context.ReadInput(InterruptEnableIn, Levels).IsLow();
                _passOn = !(enabled && channel >= 0);
                _dtackClocks = 0;
                if (!_passOn)
                {
                    _vector = (byte)((_vr & 0xF0) | channel);
                    _ipr &= ~(1 << channel);
                    if ((_vr & 0x08) != 0) _isr |= 1 << channel;
                    NotifyValueChanged();
                }
            }
        }

        _selected = selected;
        _acknowledging = iack;

        byte? drive = null;
        if (selected && _reading) drive = Read(_register, context);
        else if (iack && !_passOn) drive = _vector;

        Drive(context, drive, ((selected || (iack && !_passOn)) && _dtackClocks >= 2), iack && _passOn);
    }

    private byte ReadBus(IDigitalContext context)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(_data[i], Levels).IsHigh()) value |= 1 << i;
        return (byte)value;
    }

    private byte _pinLevels;

    private byte Pins() => (byte)((_gpdr & _ddr) | (_pinLevels & ~_ddr));

    /// <summary>The GPIP lines' edges, and TAI and TBI for timers A and B.</summary>
    private void Inputs(IDigitalContext context)
    {
        var pins = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(_gpip[i], Levels).IsHigh()) pins |= 1 << i;
        _pinLevels = (byte)pins;

        if (_lastPins >= 0)
        {
            var changed = pins ^ _lastPins;
            for (var i = 0; i < 8; i++)
            {
                if ((changed & (1 << i)) == 0 || (_ddr & (1 << i)) != 0) continue;
                var rose = (pins & (1 << i)) != 0;
                if (rose == ((_aer & (1 << i)) != 0)) Event(GpipChannels[i]);
            }
        }

        _lastPins = pins;

        TimerInput(context, 0, TimerAInput, 4);
        TimerInput(context, 1, TimerBInput, 3);
    }

    /// <summary>TAI or TBI: an event count's edges, or a pulse width's start and end.</summary>
    private void TimerInput(IDigitalContext context, int timer, Terminal pin, int edgeBit)
    {
        var t = _timers[timer];
        var high = context.ReadInput(pin, Levels).IsHigh();
        var risingActive = (_aer & (1 << edgeBit)) != 0;

        if (high != t.LastInput)
        {
            var active = high == risingActive;
            if (EventCount(timer) && active) CountDown(timer);

            if (PulseWidth(timer))
            {
                Bring(timer, _now);
                // The pulse is the input at the opposite level to the AER edge; its end is that edge.
                t.Event = high != risingActive;
                if (active) Event(GpipChannels[edgeBit]);
            }
        }

        if (PulseWidth(timer) && t.LastInput == high) t.Event = high != risingActive;
        t.LastInput = high;
    }

    // ---- the USART -------------------------------------------------------------------

    private int WordLength => 8 - ((_ucr >> 5) & 3);

    private bool ParityOn => (_ucr & 0x04) != 0;

    private bool EvenParity => (_ucr & 0x02) != 0;

    private int ClocksPerBit => (_ucr & 0x80) != 0 ? 16 : 1;

    /// <summary>The stop length in half bits: 2, 3 or 4.</summary>
    private int StopHalves => ((_ucr >> 3) & 3) switch { 2 => 3, 3 => 4, _ => 2 };

    private void Usart(IDigitalContext context)
    {
        var tc = context.ReadInput(TransmitClock, Levels).IsHigh();
        var rc = context.ReadInput(ReceiveClock, Levels).IsHigh();

        // The transmitter shifts on TC's falling edges; the receiver samples on RC's rising ones.
        if (_lastTc && !tc) TransmitClockEdge();
        if (!_lastRc && rc) ReceiveClockEdge(context);
        _lastTc = tc;
        _lastRc = rc;
    }

    private void TransmitClockEdge()
    {
        var enabled = (_tsr & 0x01) != 0;

        if (_txIndex < 0)
        {
            if (!enabled || !_transmitFull) return;

            // A frame: start, the data, parity, and the stop bits counted in half bits.
            List<int> bits = [0];
            var ones = 0;
            for (var i = 0; i < WordLength; i++)
            {
                var bit = (_udrOut >> i) & 1;
                ones += bit;
                bits.Add(bit);
            }

            if (ParityOn) bits.Add(EvenParity ? ones & 1 : (ones & 1) ^ 1);
            _txBits = [.. bits];
            _txIndex = 0;
            _txClocks = 0;
            _transmitFull = false;
            TransmittedBytes.Add(_udrOut);
            Event(ChTransmitEmpty);
            NotifyValueChanged();
        }

        var perBit = ClocksPerBit;
        var stopClocks = StopHalves * perBit / 2;
        _so = _txIndex < _txBits.Length ? _txBits[_txIndex] != 0 : true;
        if (++_txClocks < (_txIndex < _txBits.Length ? perBit : stopClocks)) return;

        _txClocks = 0;
        _txIndex++;
        if (_txIndex > _txBits.Length) _txIndex = -1;
    }

    private void ReceiveClockEdge(IDigitalContext context)
    {
        if ((_rsr & 0x01) == 0)
        {
            _rxClocks = -1;
            return;
        }

        // Looped back: the transmitter's output, whatever is on SI.
        var loop = ((_tsr >> 1) & 3) == 3;
        var high = loop ? _so : context.ReadInput(SerialIn, Levels) != LogicState.Low;
        var perBit = ClocksPerBit;

        if (_rxClocks < 0)
        {
            if (high) return;
            _rxClocks = 0;
            _rxIndex = 0;
            _rxShift = 0;
            _rxParity = false;
            _rxAnyHigh = false;
            _rsr |= 0x04;     // character in progress
            return;
        }

        _rxClocks++;
        var target = perBit == 1 ? 1 : (perBit / 2) + (_rxIndex * perBit);
        if (_rxClocks < target) return;

        if (_rxIndex == 0)
        {
            if (high)
            {
                _rxClocks = -1;
                _rsr &= 0xFB;
                return;
            }

            _rxIndex++;
            return;
        }

        if (high) _rxAnyHigh = true;
        if (_rxIndex <= WordLength)
        {
            if (high) _rxShift |= 1 << (_rxIndex - 1);
            _rxIndex++;
            return;
        }

        if (ParityOn && _rxIndex == WordLength + 1)
        {
            var ones = System.Numerics.BitOperations.PopCount((uint)_rxShift);
            var expected = EvenParity ? (ones & 1) != 0 : (ones & 1) == 0;
            if (high != expected) _rxParity = true;
            _rxIndex++;
            return;
        }

        // The stop bit.
        _rxClocks = -1;
        var framing = !high;
        var brk = framing && !_rxAnyHigh;
        var full = (_rsr & 0x80) != 0;

        _rsr &= 0xC3;     // character in progress, break, frame and parity: this character's
        if (full) _rsr |= 0x40;
        else
        {
            _udrIn = (byte)_rxShift;
            _rsr |= 0x80;
        }

        if (_rxParity) _rsr |= 0x20;
        if (framing && !brk) _rsr |= 0x10;
        if (brk) _rsr |= 0x08;
        ReceivedBytes.Add((byte)_rxShift);

        var error = full || _rxParity || framing;
        if (error && (_ier & (1 << ChReceiveError)) != 0) Event(ChReceiveError);
        else Event(ChReceiveFull);
        NotifyValueChanged();
    }

    // ---- registers -------------------------------------------------------------------

    private byte Read(int register, IDigitalContext context) => register switch
    {
        0x00 => Pins(),
        0x01 => _aer,
        0x02 => _ddr,
        0x03 => (byte)(_ier >> 8),
        0x04 => (byte)_ier,
        0x05 => (byte)(_ipr >> 8),
        0x06 => (byte)_ipr,
        0x07 => (byte)(_isr >> 8),
        0x08 => (byte)_isr,
        0x09 => (byte)(_imr >> 8),
        0x0A => (byte)_imr,
        0x0B => _vr,
        0x0C => (byte)_timers[0].Control,
        0x0D => (byte)_timers[1].Control,
        0x0E => (byte)(((_timers[2].Control & 7) << 4) | (_timers[3].Control & 7)),
        0x0F or 0x10 or 0x11 or 0x12 => (byte)_timers[register - 0x0F].Count,
        0x13 => _scr,
        0x14 => _ucr,
        0x15 => _rsr,
        0x16 => TransmitterStatus,
        0x17 => _udrIn,
        _ => 0xFF,
    };

    private void AfterRead(int register)
    {
        if (register == 0x17) _rsr &= 0x7F;
        if (register == 0x15) _rsr &= 0xBF;
        NotifyValueChanged();
    }

    private void Write(int register, byte value, double now)
    {
        switch (register)
        {
            case 0x00: _gpdr = value; break;
            case 0x01: _aer = value; break;
            case 0x02: _ddr = value; break;
            case 0x03 or 0x04:
            {
                var shift = register == 0x03 ? 8 : 0;
                _ier = (_ier & ~(0xFF << shift)) | (value << shift);
                _ipr &= _ier;
                break;
            }

            case 0x05: _ipr &= (value << 8) | 0xFF; break;
            case 0x06: _ipr &= 0xFF00 | value; break;
            case 0x07: _isr &= (value << 8) | 0xFF; break;
            case 0x08: _isr &= 0xFF00 | value; break;
            case 0x09: _imr = (_imr & 0x00FF) | (value << 8); break;
            case 0x0A: _imr = (_imr & 0xFF00) | value; break;
            case 0x0B:
                _vr = (byte)(value & 0xF8);
                if ((_vr & 0x08) == 0) _isr = 0;
                break;
            case 0x0C: Control(0, value & 0x1F, now); break;
            case 0x0D: Control(1, value & 0x1F, now); break;
            case 0x0E:
                Control(2, (value >> 4) & 7, now);
                Control(3, value & 7, now);
                break;
            case 0x0F or 0x10 or 0x11 or 0x12:
            {
                var t = _timers[register - 0x0F];
                t.Reload = value == 0 ? 256 : value;
                if ((t.Control & 0x0F) == 0) t.Count = t.Reload;
                break;
            }

            case 0x13: _scr = value; break;
            case 0x14: _ucr = value; break;
            case 0x15: _rsr = (byte)((_rsr & 0xFE) | (value & 0x01)); break;
            case 0x16:
                _tsr = (byte)(value & 0x2F);
                if ((_tsr & 0x01) == 0)
                {
                    _txIndex = -1;
                    _so = true;
                }

                break;
            case 0x17:
                _udrOut = value;
                _transmitFull = true;
                break;
        }

        NotifyValueChanged();
    }

    private void Control(int timer, int value, double now)
    {
        Bring(timer, now);
        var t = _timers[timer];
        var wasStopped = (t.Control & 0x0F) == 0;
        t.Control = value;

        // The output reset bit (A and B only) holds the output low.
        if ((value & 0x10) != 0) t.Output = false;
        if (wasStopped && (value & 0x0F) != 0) t.Phase = 0;
        t.Last = now;
    }

    private void Drive(IDigitalContext context, byte? data, bool dtack, bool passOn)
    {
        var delay = DelayFor(context);

        for (var i = 0; i < 8; i++)
            Request(context, i, data is { } d ? Level((d >> i) & 1) : LogicState.HighImpedance, data is null ? Math.Min(delay, 30e-9) : delay);

        for (var i = 0; i < 8; i++)
            Request(context, GpipIndex + i, (_ddr & (1 << i)) != 0 ? Level((_gpdr >> i) & 1) : LogicState.HighImpedance, delay);

        for (var i = 0; i < 4; i++)
        {
            var t = _timers[i];
            var low = i < 2 && (t.Control & 0x10) != 0;
            Request(context, TimerOutIndex + i, !low && t.Output ? LogicState.High : LogicState.Low, delay);
        }

        // SO: the frame, or while the transmitter is off what TSR's H and L bits say.
        LogicState so;
        if ((_tsr & 0x01) != 0)
        {
            so = (_tsr & 0x08) != 0 && _txIndex < 0 ? LogicState.Low : _so ? LogicState.High : LogicState.Low;
        }
        else
        {
            so = ((_tsr >> 1) & 3) switch { 1 => LogicState.Low, 2 or 3 => LogicState.High, _ => LogicState.HighImpedance };
        }

        Request(context, SoIndex, so, delay);
        Request(context, IrqIndex, Requesting >= 0 ? LogicState.Low : LogicState.HighImpedance, delay);
        Request(context, IeoIndex, passOn ? LogicState.Low : LogicState.High, delay);
        Request(context, DtackIndex, dtack ? LogicState.Low : LogicState.HighImpedance, dtack ? delay : Math.Min(delay, 30e-9));
        Request(context, TrIndex, LogicState.High, delay);
        Request(context, RrIndex, LogicState.High, delay);
    }

    private static LogicState Level(int bit) => bit != 0 ? LogicState.High : LogicState.Low;

    /// <summary>The crystal-driven timers' next time-outs.</summary>
    public double? NextBreakpointAfter(double time)
    {
        double? next = null;
        for (var i = 0; i < 4; i++)
        {
            if (NextTimeout(i) is { } at && at > time + 1e-12 && (next is null || at < next)) next = at;
        }

        return next;
    }

    private void HardwareReset()
    {
        _gpdr = _aer = _ddr = _vr = _scr = _ucr = _rsr = _tsr = 0;
        _ier = _ipr = _isr = _imr = 0;
        foreach (var t in _timers) t.Clear();
        _transmitFull = false;
        _txIndex = -1;
        _so = true;
        _rxClocks = -1;
        _selected = false;
        _acknowledging = false;
        _passOn = false;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        HardwareReset();
        foreach (var t in _timers)
        {
            t.Count = t.Reload = 256;
            t.Last = 0;
            t.Timeouts = 0;
            t.LastInput = false;
        }

        _udrIn = _udrOut = 0;
        _lastPins = -1;
        _lastTc = _lastRc = false;
        _lastClock = LogicState.Unknown;
        TransmittedBytes.Clear();
        ReceivedBytes.Clear();
    }

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "RW" => "R/W",
        "CS" or "DS" or "DTACK" or "IACK" or "IRQ" or "RESET" => "/" + pin.Name,
        _ => pin.Name,
    };

    private sealed record RunState(byte Gpdr, byte Aer, byte Ddr, byte Vr, byte Ucr, byte Rsr, byte Tsr, int Ier, int Ipr, int Isr, int Imr,
        (int Control, int Count, int Reload, long Timeouts)[] Timers, byte[] Sent, byte[] Received);

    public object CaptureRunState() =>
        new RunState(_gpdr, _aer, _ddr, _vr, _ucr, _rsr, _tsr, _ier, _ipr, _isr, _imr,
            [.. _timers.Select(t => (t.Control, t.Count, t.Reload, t.Timeouts))], [.. TransmittedBytes], [.. ReceivedBytes]);

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;
        (_gpdr, _aer, _ddr, _vr, _ucr, _rsr, _tsr) = (run.Gpdr, run.Aer, run.Ddr, run.Vr, run.Ucr, run.Rsr, run.Tsr);
        (_ier, _ipr, _isr, _imr) = (run.Ier, run.Ipr, run.Isr, run.Imr);
        for (var i = 0; i < 4; i++)
            (_timers[i].Control, _timers[i].Count, _timers[i].Reload, _timers[i].Timeouts) = run.Timers[i];
        TransmittedBytes.Clear();
        TransmittedBytes.AddRange(run.Sent);
        ReceivedBytes.Clear();
        ReceivedBytes.AddRange(run.Received);
        NotifyValueChanged();
    }

    partial void OnTimerClockFrequencyChanged(double value) => NotifyValueChanged();
}
