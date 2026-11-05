using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// The Motorola MC68230 PI/T, parallel interface and timer: two 8-bit handshaking ports, a third of
/// general bits that double as the chip's interrupt and timer pins, and a 24-bit timer, on a
/// 68000's bus.
/// <para>
/// <b>The bus.</b> /CS low selects it; RS1-RS5 (A1-A5 with the chip on the odd bytes) pick a register.
/// It is a synchronous part: /DTACK goes low on the second rising edge of CLK after /CS, and is
/// released with /CS; a write takes the bus as /CS goes high again, when a read's side effects
/// happen too. CLK is normally the processor's clock.
/// </para>
/// <list type="bullet">
/// <item>$00 PGCR (port mode, H34 and H12 enables, H1-H4 senses), $01 PSRR (which pins PC5 and PC6
/// are, the port interrupt priority), $02-$04 PADDR, PBDDR, PCDDR, $05 PIVR, $06 PACR, $07 PBCR,
/// $08 PADR, $09 PBDR, $0A PAAR and $0B PBAR (the pins, read only), $0C PCDR, $0D PSR.</item>
/// <item>$10 TCR, $11 TIVR, $13-$15 CPRH, CPRM, CPRL (the preload), $17-$19 CNTRH, CNTRM, CNTRL
/// (the count, read only), $1A TSR. Every other register reads as zero.</item>
/// </list>
/// <para>
/// <b>Ports A and B</b> in mode 0 (PGCR bits 6-7 = 00) each have three submodes, PACR/PBCR bits 6-7:
/// <i>00, double-buffered input</i> — the asserted edge of H1 (H3) latches the input pins into a
/// two-deep buffer, H1S (H3S) saying there is something to read and reading the data register
/// taking it; <i>01, double-buffered output</i> — writes queue in a two-deep buffer that the pins
/// show a byte at a time, each assertion of H1 (H3) by the peripheral acknowledging one, H1S (H3S)
/// saying there is room; <i>1X, bit I/O</i> — no buffering, H1 (H3) only an edge-sensitive status.
/// H2 (H4) is an edge-sensitive input, an output held negated or asserted, or the handshake: in
/// the interlocked handshake it is asserted while the port is ready (input: room; output: a byte on
/// the pins) and negated by H1 (H3); the pulsed handshake asserts it for four clocks. PGCR's sense
/// bits say whether each H line is active high or low; H12 and H34 enable the pairs. In mode 1 the
/// two ports are one 16-bit port, port A the high byte, run by port B's handshake on H3 and H4 —
/// written port A first, the write to PBDR starting the transfer, read port A first.
/// </para>
/// <para>
/// <b>Port C</b> is eight general bits with PCDDR, except where PSRR or TCR gives a pin a job: PC2
/// TIN, PC3 TOUT, PC5 /PIRQ, PC6 /PIACK, PC7 /TIACK. A port interrupt is H1S-H4S with their enables
/// (PACR/PBCR bit 1 for H1/H3, bit 2 for H2/H4) on /PIRQ, and a /PIACK cycle reads PIVR's top six
/// bits with the source — 00 H1, 01 H2, 10 H3, 11 H4, in PSRR's priority order — in the bottom two.
/// </para>
/// <para>
/// <b>The timer</b> counts CLK ÷ 32, TIN ÷ 32, TIN directly, or CLK ÷ 32 while TIN is high (TCR bits
/// 1-2), down from the preload once enabled (TCR bit 0). At zero it sets ZDS in TSR (cleared by
/// writing a 1) and reloads — or, with TCR bit 4, rolls over to $FFFFFF. TOUT is a port C pin, a
/// square wave toggling at each zero, or /TIRQ, low while ZDS is set and the interrupt is enabled,
/// with /TIACK reading TIVR (TCR bits 5-7).
/// </para>
/// <para>
/// Simplifications: modes 2 and 3 (the bidirectional ports) are not modelled — they behave as mode
/// 0 — nor are DMA requests (PC4) or the H1/H3 "status control" option; the pulsed handshake is four
/// clocks; /PIRQ and /TIRQ are open drain.
/// </para>
/// <para>
/// Pinout (DIP-48): 1-3 = D5-D7, 4-11 = PA0-PA7, 12 = VCC, 13-16 = H1-H4, 17-24 = PB0-PB7, 25-29 =
/// RS5-RS1, 30 = PC0, 31 = PC1, 32 = PC2/TIN, 33 = PC3/TOUT, 34 = PC4/DMAREQ, 35 = PC5/PIRQ,
/// 36 = PC6/PIACK, 37 = PC7/TIACK, 38 = GND, 39 = /RESET, 40 = CLK, 41 = /CS, 42 = /DTACK, 43 = R/W,
/// 44-48 = D0-D4.
/// </para>
/// </summary>
public sealed class Pit68230 : DigitalIc, IRunState
{
    // Outputs: D0-D7 = 0-7, PA0-7 = 8-15, PB0-7 = 16-23, PC0-7 = 24-31, H2 = 32, H4 = 33, /DTACK = 34.
    private const int PaIndex = 8, PbIndex = 16, PcIndex = 24, H2Index = 32, H4Index = 33, DtackIndex = 34;

    private readonly Terminal[] _data = new Terminal[8];
    private readonly Terminal[] _rs = new Terminal[5];
    private readonly Terminal[] _pa = new Terminal[8];
    private readonly Terminal[] _pb = new Terminal[8];
    private readonly Terminal[] _pc = new Terminal[8];
    private readonly Terminal[] _h = new Terminal[4];

    private byte _pgcr, _psrr, _pcddr, _pivr = 0x0F, _pcdr, _tcr, _tivr = 0x0F;
    private int _preload, _counter;
    private bool _zds;
    private bool _tout;
    private int _prescale;

    private readonly Port _a = new(), _b = new();

    private LogicState _lastClock = LogicState.Unknown;
    private readonly bool[] _lastH = new bool[4];
    private int _pulse2, _pulse4;
    private bool _lastTin;

    private bool _selected;
    private int _register;
    private bool _reading;
    private int _dtackClocks;
    private bool _acknowledging;
    private byte _vector;

    public Pit68230() : base(48)
    {
        PropagationDelay = 60e-9;

        var pins = new Terminal[49];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 48));

        for (var i = 5; i < 8; i++) _data[i] = Pin(i - 4, $"D{i}", TerminalType.Bidirectional);
        for (var i = 0; i < 8; i++) _pa[i] = Pin(4 + i, $"PA{i}", TerminalType.Bidirectional);
        Vcc = Pin(12, "VCC", TerminalType.Power);
        _h[0] = Pin(13, "H1", TerminalType.Input);
        _h[1] = Pin(14, "H2", TerminalType.Bidirectional);
        _h[2] = Pin(15, "H3", TerminalType.Input);
        _h[3] = Pin(16, "H4", TerminalType.Bidirectional);
        for (var i = 0; i < 8; i++) _pb[i] = Pin(17 + i, $"PB{i}", TerminalType.Bidirectional);
        for (var i = 0; i < 5; i++) _rs[i] = Pin(29 - i, $"RS{i + 1}", TerminalType.Input);
        string[] pcNames = ["PC0", "PC1", "PC2/TIN", "PC3/TOUT", "PC4/DMAREQ", "PC5/PIRQ", "PC6/PIACK", "PC7/TIACK"];
        for (var i = 0; i < 8; i++) _pc[i] = Pin(30 + i, pcNames[i], TerminalType.Bidirectional);
        Gnd = Pin(38, "GND", TerminalType.Ground);
        Reset = Pin(39, "RESET", TerminalType.Input);
        Clock = Pin(40, "CLK", TerminalType.Input);
        ChipSelect = Pin(41, "CS", TerminalType.Input);
        Dtack = Pin(42, "DTACK", TerminalType.Bidirectional);
        ReadWrite = Pin(43, "RW", TerminalType.Input);
        for (var i = 0; i < 5; i++) _data[i] = Pin(44 + i, $"D{i}", TerminalType.Bidirectional);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [Clock, ChipSelect, ReadWrite, Reset, .. _rs, .. _h, .. _pa, .. _pb, .. _pc, .. _data],
            [.. _data, .. _pa, .. _pb, .. _pc, _h[1], _h[3], Dtack]);
    }

    public override string PartNumber => "MC68230";

    public override string ComponentType => "68230 PI/T";

    public override bool LabelsPins => true;

    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>RS1-RS5: A1-A5 on a 68000.</summary>
    public IReadOnlyList<Terminal> RegisterSelect => _rs;

    public IReadOnlyList<Terminal> PortA => _pa;

    public IReadOnlyList<Terminal> PortB => _pb;

    /// <summary>PC0-PC7, with TIN on PC2, TOUT on PC3, /PIRQ on PC5, /PIACK on PC6 and /TIACK on PC7.</summary>
    public IReadOnlyList<Terminal> PortC => _pc;

    /// <summary>H1-H4: H1 and H3 inputs, H2 and H4 inputs or outputs.</summary>
    public IReadOnlyList<Terminal> Handshake => _h;

    public Terminal Clock { get; }

    public Terminal ChipSelect { get; }

    public Terminal ReadWrite { get; }

    public Terminal Dtack { get; }

    public Terminal Reset { get; }

    public override string ValueLabel => $"A {_a.Output:X2} B {_b.Output:X2} T {((_tcr & 1) != 0 ? _counter.ToString("X6", System.Globalization.CultureInfo.InvariantCulture) : "off")}";

    // ---- what a test or an agent wants to see -----------------------------------

    public byte GeneralControl => _pgcr;

    public byte ServiceRequest => _psrr;

    public (byte Control, byte Direction, byte Output) PortARegisters => (_a.Control, _a.Direction, _a.Output);

    public (byte Control, byte Direction, byte Output) PortBRegisters => (_b.Control, _b.Direction, _b.Output);

    public byte PortCDirection => _pcddr;

    public byte PortCOutput => _pcdr;

    public byte PortStatus => Status(null);

    public byte PortVector => _pivr;

    public byte TimerControl => _tcr;

    public byte TimerVector => _tivr;

    public int TimerPreload => _preload;

    public int TimerCount => _counter;

    public bool ZeroDetected => _zds;

    /// <summary>Bytes the input buffers of port A have latched, in order.</summary>
    public IReadOnlyList<byte> LatchedA => _a.Latched;

    // ---- the ports ------------------------------------------------------------------

    private sealed class Port
    {
        public byte Control, Direction, Output;
        public readonly Queue<int> Buffer = new();
        public readonly List<byte> Latched = [];
        public bool HoldStatus;     // H1S/H3S in bit I/O
        public bool OtherStatus;    // H2S/H4S as an edge-sensitive input
        public bool HandshakeOut;   // H2/H4 asserted, as a handshake output
        public int Pending16 = -1;  // the high byte of a 16-bit write, until PBDR completes it

        public int Submode => Control >> 6;

        public int H2Mode => (Control >> 3) & 7;

        public void Clear()
        {
            Control = Direction = Output = 0;
            Buffer.Clear();
            HoldStatus = OtherStatus = HandshakeOut = false;
            Pending16 = -1;
        }
    }

    private int Mode => _pgcr >> 6;

    private bool Wide => Mode == 1;

    private bool H12Enabled => (_pgcr & 0x10) != 0;

    private bool H34Enabled => (_pgcr & 0x20) != 0;

    /// <summary>H1-H4 asserted, by the sense bits in PGCR.</summary>
    private bool Asserted(IDigitalContext context, int line) =>
        context.ReadInput(_h[line], Levels).IsHigh() == ((_pgcr & (1 << line)) != 0);

    /// <summary>PSR: the H lines' levels and the four status bits. <paramref name="context"/> null gives the last levels.</summary>
    private byte Status(IDigitalContext? context)
    {
        var levels = 0;
        for (var i = 0; i < 4; i++)
        {
            var asserted = context is null ? _lastH[i] : Asserted(context, i);
            if (asserted) levels |= 0x10 << i;
        }

        return (byte)(levels | (H1S ? 1 : 0) | (H2S ? 2 : 0) | (H3S ? 4 : 0) | (H4S ? 8 : 0));
    }

    private bool PrimaryStatus(Port p) => p.Submode switch
    {
        0 => p.Buffer.Count > 0,        // double-buffered input: something to read
        1 => p.Buffer.Count < 2,        // double-buffered output: room to write
        _ => p.HoldStatus,              // bit I/O: the edge seen
    };

    private bool H1S => !Wide && H12Enabled && PrimaryStatus(_a);

    private bool H2S => !Wide && H12Enabled && _a.H2Mode < 4 && _a.OtherStatus;

    private Port Controlling => _b;

    private bool H3S => H34Enabled && PrimaryStatus(Controlling);

    private bool H4S => H34Enabled && _b.H2Mode < 4 && _b.OtherStatus;

    private bool PortInterruptsOn => (_psrr & 0x08) != 0;

    /// <summary>The sources asking for a port interrupt, as H1-H4 bits.</summary>
    private int PortRequests =>
        (H1S && (_a.Control & 0x02) != 0 ? 1 : 0) | (H2S && (_a.Control & 0x04) != 0 ? 2 : 0)
        | (H3S && (_b.Control & 0x02) != 0 ? 4 : 0) | (H4S && (_b.Control & 0x04) != 0 ? 8 : 0);

    private static readonly int[][] Priorities =
    [
        [0, 1, 2, 3], [1, 0, 2, 3], [0, 1, 3, 2], [1, 0, 3, 2],
        [2, 3, 0, 1], [2, 3, 1, 0], [3, 2, 0, 1], [3, 2, 1, 0],
    ];

    private int TopRequest
    {
        get
        {
            var requests = PortRequests;
            foreach (var source in Priorities[_psrr & 7])
                if ((requests & (1 << source)) != 0) return source;
            return -1;
        }
    }

    private int ToutMode => _tcr >> 5;

    private bool TimerInterrupt => ToutMode is 5 or 7 && _zds;

    // ---- the logic -----------------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        if (context.ReadInput(Reset, Levels).IsLow())
        {
            HardwareReset();
            Drive(context, null, false);
            return;
        }

        var clock = context.ReadInput(Clock, Levels);
        var rising = _lastClock.IsLow() && clock.IsHigh();
        _lastClock = clock;

        var selected = context.ReadInput(ChipSelect, Levels).IsLow();

        if (!context.IsInitializing)
        {
            Handshakes(context);
            if (rising) Clocked(context);

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
                else Write(_register, ReadBus(context));
            }

            // An acknowledge: /PIACK or /TIACK low, with an interrupt to answer.
            var piack = (_psrr & 0x10) != 0 && context.ReadInput(_pc[6], Levels).IsLow() && TopRequest >= 0;
            var tiack = ToutMode is 4 or 5 && context.ReadInput(_pc[7], Levels).IsLow() && TimerInterrupt;
            if ((piack || tiack) && !_acknowledging)
            {
                _vector = piack ? (byte)((_pivr & 0xFC) | TopRequest) : _tivr;
                _dtackClocks = 0;
            }

            _acknowledging = piack || tiack;
        }

        _selected = selected;

        byte? drive = null;
        if (selected && _reading) drive = Read(_register, context);
        else if (_acknowledging && !selected) drive = _vector;

        Drive(context, drive, (selected || _acknowledging) && _dtackClocks >= 2);
    }

    /// <summary>A CLK rising edge: the /DTACK count, the pulsed handshakes, and the timer's prescaler.</summary>
    private void Clocked(IDigitalContext context)
    {
        if (_selected || _acknowledging) _dtackClocks++;

        if (_pulse2 > 0 && --_pulse2 == 0) _a.HandshakeOut = false;
        if (_pulse4 > 0 && --_pulse4 == 0) Controlling.HandshakeOut = false;

        if ((_tcr & 1) == 0) return;
        var tin = context.ReadInput(_pc[2], Levels).IsHigh();
        switch ((_tcr >> 1) & 3)
        {
            case 0:
                Prescaled();
                break;
            case 1:
                if (tin) Prescaled();
                break;
        }
    }

    private void Prescaled()
    {
        if (++_prescale < 32) return;
        _prescale = 0;
        Count();
    }

    /// <summary>One count of the timer: zero sets ZDS, toggles TOUT and reloads (or rolls over).</summary>
    private void Count()
    {
        _counter = (_counter - 1) & 0xFFFFFF;
        if (_counter != 0) return;

        _zds = true;
        _tout = !_tout;
        if ((_tcr & 0x10) == 0) _counter = _preload;
        NotifyValueChanged();
    }

    /// <summary>The H lines' edges and the TIN clock.</summary>
    private void Handshakes(IDigitalContext context)
    {
        var tin = context.ReadInput(_pc[2], Levels).IsHigh();
        if ((_tcr & 1) != 0 && ((_tcr >> 1) & 3) >= 2 && !_lastTin && tin)
        {
            if (((_tcr >> 1) & 3) == 2) Prescaled();
            else Count();
        }

        _lastTin = tin;

        for (var line = 0; line < 4; line++)
        {
            var now = Asserted(context, line);
            var edge = now && !_lastH[line];
            _lastH[line] = now;
            if (!edge) continue;

            var enabled = line < 2 ? H12Enabled : H34Enabled;
            if (!enabled) continue;

            switch (line)
            {
                case 0 when !Wide:
                    Strobe(context, _a, () => PortPins(context, _pa), ref _pulse2);
                    break;
                case 2:
                    Strobe(context, Controlling, () => Wide ? (PortPins(context, _pa) << 8) | PortPins(context, _pb) : PortPins(context, _pb), ref _pulse4);
                    break;
                case 1 when !Wide && _a.H2Mode < 4:
                    _a.OtherStatus = true;
                    break;
                case 3 when _b.H2Mode < 4:
                    _b.OtherStatus = true;
                    break;
            }
        }
    }

    /// <summary>H1 or H3 asserted: a byte latched in, or one taken out, or a status edge.</summary>
    private void Strobe(IDigitalContext context, Port p, Func<int> pins, ref int pulse)
    {
        switch (p.Submode)
        {
            case 0:
                if (p.Buffer.Count < 2)
                {
                    var value = pins();
                    p.Buffer.Enqueue(value);
                    p.Latched.Add((byte)value);
                }

                // Interlocked: not ready again until there is room. Pulsed: a short pulse.
                if (p.H2Mode == 6) p.HandshakeOut = p.Buffer.Count < 2;
                if (p.H2Mode == 7 && p.Buffer.Count < 2)
                {
                    p.HandshakeOut = true;
                    pulse = 4;
                }

                break;

            case 1:
                if (p.Buffer.Count > 0) p.Buffer.Dequeue();
                if (p.H2Mode == 6) p.HandshakeOut = p.Buffer.Count > 0;
                if (p.H2Mode == 7 && p.Buffer.Count > 0)
                {
                    p.HandshakeOut = true;
                    pulse = 4;
                }

                break;

            default:
                p.HoldStatus = true;
                break;
        }

        NotifyValueChanged();
    }

    private int PortPins(IDigitalContext context, Terminal[] port)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(port[i], Levels).IsHigh()) value |= 1 << i;
        return value;
    }

    private byte ReadBus(IDigitalContext context)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(_data[i], Levels).IsHigh()) value |= 1 << i;
        return (byte)value;
    }

    /// <summary>A data register as read: a buffered input's oldest byte, or the pins for inputs and the register for outputs.</summary>
    private byte ReadData(IDigitalContext context, Port p, Terminal[] pins, bool high)
    {
        var buffered = Wide ? Controlling : p;
        if (buffered.Submode == 0 && buffered.Buffer.Count > 0)
        {
            var value = buffered.Buffer.Peek();
            return (byte)(Wide ? high ? value >> 8 : value : value);
        }

        var levels = PortPins(context, pins);
        return (byte)((p.Output & p.Direction) | (levels & ~p.Direction));
    }

    private byte Read(int register, IDigitalContext context) => register switch
    {
        0x00 => _pgcr,
        0x01 => _psrr,
        0x02 => _a.Direction,
        0x03 => _b.Direction,
        0x04 => _pcddr,
        0x05 => _pivr,
        0x06 => _a.Control,
        0x07 => _b.Control,
        0x08 => ReadData(context, _a, _pa, high: true),
        0x09 => ReadData(context, _b, _pb, high: false),
        0x0A => (byte)PortPins(context, _pa),
        0x0B => (byte)PortPins(context, _pb),
        0x0C => (byte)((_pcdr & _pcddr) | (PortPins(context, _pc) & ~_pcddr)),
        0x0D => Status(context),
        0x10 => _tcr,
        0x11 => _tivr,
        0x13 => (byte)(_preload >> 16),
        0x14 => (byte)(_preload >> 8),
        0x15 => (byte)_preload,
        0x17 => (byte)(_counter >> 16),
        0x18 => (byte)(_counter >> 8),
        0x19 => (byte)_counter,
        0x1A => (byte)(_zds ? 1 : 0),
        _ => 0,
    };

    /// <summary>A read's side effect, as /CS goes high: reading a buffered input takes the byte.</summary>
    private void AfterRead(int register)
    {
        // In mode 1 the read of port B (the second) takes the 16-bit word; in mode 0 each port's own.
        var port = register switch
        {
            0x08 when !Wide => _a,
            0x09 => Wide ? Controlling : _b,
            _ => null,
        };

        if (port is { Submode: 0 } && port.Buffer.Count > 0)
        {
            port.Buffer.Dequeue();
            if (port.H2Mode == 6) port.HandshakeOut = port.Buffer.Count < 2;
        }

        NotifyValueChanged();
    }

    private void Write(int register, byte value)
    {
        switch (register)
        {
            case 0x00:
                _pgcr = value;
                break;
            case 0x01:
                _psrr = value;
                break;
            case 0x02:
                _a.Direction = value;
                break;
            case 0x03:
                _b.Direction = value;
                break;
            case 0x04:
                _pcddr = value;
                break;
            case 0x05:
                _pivr = (byte)(value & 0xFC);
                break;
            case 0x06:
                WriteControl(_a, value);
                break;
            case 0x07:
                WriteControl(_b, value);
                break;
            case 0x08:
                if (Wide) _b.Pending16 = value;
                else WriteData(_a, value);
                break;
            case 0x09:
                if (Wide) WriteData(Controlling, ((_b.Pending16 < 0 ? _a.Output : _b.Pending16) << 8) | value, wide: true);
                else WriteData(_b, value);
                break;
            case 0x0C:
                _pcdr = value;
                break;
            case 0x0D:
                // Writing ones clears the edge-sensitive statuses.
                if ((value & 1) != 0) _a.HoldStatus = false;
                if ((value & 2) != 0) _a.OtherStatus = false;
                if ((value & 4) != 0) _b.HoldStatus = false;
                if ((value & 8) != 0) _b.OtherStatus = false;
                break;
            case 0x10:
            {
                var starting = (value & 1) != 0 && (_tcr & 1) == 0;
                _tcr = value;
                if (starting)
                {
                    _counter = _preload;
                    _prescale = 0;
                }

                break;
            }

            case 0x11:
                _tivr = value;
                break;
            case 0x13:
                _preload = (_preload & 0x00FFFF) | (value << 16);
                break;
            case 0x14:
                _preload = (_preload & 0xFF00FF) | (value << 8);
                break;
            case 0x15:
                _preload = (_preload & 0xFFFF00) | value;
                break;
            case 0x1A:
                if ((value & 1) != 0) _zds = false;
                break;
        }

        NotifyValueChanged();
    }

    private void WriteControl(Port p, byte value)
    {
        p.Control = value;
        switch (p.H2Mode)
        {
            case 4: p.HandshakeOut = false; break;
            case 5: p.HandshakeOut = true; break;
            case 6: p.HandshakeOut = p.Submode == 0 ? p.Buffer.Count < 2 : p.Buffer.Count > 0; break;
            default: p.HandshakeOut = false; break;
        }
    }

    /// <summary>A write to a data register: a buffered output queues it and starts the handshake; otherwise it goes to the pins.</summary>
    private void WriteData(Port p, int value, bool wide = false)
    {
        if (wide)
        {
            _a.Output = (byte)(value >> 8);
            _b.Output = (byte)value;
            _b.Pending16 = -1;
        }
        else
        {
            p.Output = (byte)value;
        }

        if (p.Submode != 1) return;

        if (p.Buffer.Count < 2) p.Buffer.Enqueue(value);
        if (p.H2Mode is 6 or 7)
        {
            p.HandshakeOut = true;
            if (p.H2Mode == 7)
            {
                if (p == _a) _pulse2 = 4;
                else _pulse4 = 4;
            }
        }
    }

    /// <summary>What an output port's pins show: a buffered output's oldest byte, or the register.</summary>
    private int Shown(Port p, bool high)
    {
        var source = Wide ? Controlling : p;
        if (source.Submode == 1 && source.Buffer.Count > 0)
        {
            var value = source.Buffer.Peek();
            return Wide ? high ? value >> 8 : value & 0xFF : value;
        }

        return p.Output;
    }

    private void Drive(IDigitalContext context, byte? data, bool dtack)
    {
        var delay = DelayFor(context);

        for (var i = 0; i < 8; i++)
            Request(context, i, data is { } d ? Level((d >> i) & 1) : LogicState.HighImpedance, data is null ? Math.Min(delay, 30e-9) : delay);

        var shownA = Shown(_a, high: true);
        var shownB = Shown(_b, high: false);
        for (var i = 0; i < 8; i++)
        {
            Request(context, PaIndex + i, (_a.Direction & (1 << i)) != 0 ? Level((shownA >> i) & 1) : LogicState.HighImpedance, delay);
            Request(context, PbIndex + i, (_b.Direction & (1 << i)) != 0 ? Level((shownB >> i) & 1) : LogicState.HighImpedance, delay);
        }

        // Port C, with its pins' jobs.
        for (var i = 0; i < 8; i++)
        {
            LogicState state = (_pcddr & (1 << i)) != 0 ? Level((_pcdr >> i) & 1) : LogicState.HighImpedance;
            if (i == 3 && ToutMode is 2 or 3) state = Level(_tout ? 1 : 0);
            if (i == 3 && ToutMode >= 4) state = TimerInterrupt ? LogicState.Low : LogicState.HighImpedance;
            if (i == 5 && (_psrr & 0x08) != 0) state = PortInterruptsOn && PortRequests != 0 ? LogicState.Low : LogicState.HighImpedance;
            if (i == 6 && (_psrr & 0x10) != 0) state = LogicState.HighImpedance;
            if (i == 7 && ToutMode is 4 or 5) state = LogicState.HighImpedance;
            if (i == 2 && ((_tcr >> 1) & 3) != 0) state = LogicState.HighImpedance;
            Request(context, PcIndex + i, state, delay);
        }

        Request(context, H2Index, HandshakeLevel(_a, 1, H12Enabled && !Wide), delay);
        Request(context, H4Index, HandshakeLevel(Controlling, 3, H34Enabled), delay);
        Request(context, DtackIndex, dtack ? LogicState.Low : LogicState.HighImpedance, dtack ? delay : Math.Min(delay, 30e-9));
    }

    /// <summary>H2 or H4 as an output — asserted by its sense bit — or released as an input.</summary>
    private LogicState HandshakeLevel(Port p, int line, bool enabled)
    {
        if (p.H2Mode < 4) return LogicState.HighImpedance;
        var asserted = enabled ? p.HandshakeOut : p.H2Mode == 5;
        var activeHigh = (_pgcr & (1 << line)) != 0;
        return Level(asserted == activeHigh ? 1 : 0);
    }

    private static LogicState Level(int bit) => bit != 0 ? LogicState.High : LogicState.Low;

    private void HardwareReset()
    {
        _pgcr = _psrr = _pcddr = _pcdr = _tcr = 0;
        _pivr = 0x0F;
        _tivr = 0x0F;
        _a.Clear();
        _b.Clear();
        _zds = false;
        _tout = false;
        _prescale = 0;
        _pulse2 = _pulse4 = 0;
        _selected = false;
        _acknowledging = false;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        HardwareReset();
        _preload = 0;
        _counter = 0;
        _lastClock = LogicState.Unknown;
        Array.Clear(_lastH);
        _lastTin = false;
        _a.Latched.Clear();
        _b.Latched.Clear();
    }

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "RW" => "R/W",
        "CS" or "DTACK" or "RESET" => "/" + pin.Name,
        _ => pin.Name,
    };

    private sealed record RunState(byte Pgcr, byte Psrr, byte Pcddr, byte Pcdr, byte Tcr, byte Pivr, byte Tivr, int Preload, int Counter, bool Zds,
        (byte, byte, byte) A, (byte, byte, byte) B, byte[] Latched);

    public object CaptureRunState() =>
        new RunState(_pgcr, _psrr, _pcddr, _pcdr, _tcr, _pivr, _tivr, _preload, _counter, _zds,
            (_a.Control, _a.Direction, _a.Output), (_b.Control, _b.Direction, _b.Output), [.. _a.Latched]);

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;
        (_pgcr, _psrr, _pcddr, _pcdr, _tcr, _pivr, _tivr) = (run.Pgcr, run.Psrr, run.Pcddr, run.Pcdr, run.Tcr, run.Pivr, run.Tivr);
        (_preload, _counter, _zds) = (run.Preload, run.Counter, run.Zds);
        (_a.Control, _a.Direction, _a.Output) = run.A;
        (_b.Control, _b.Direction, _b.Output) = run.B;
        _a.Latched.Clear();
        _a.Latched.AddRange(run.Latched);
        NotifyValueChanged();
    }
}
