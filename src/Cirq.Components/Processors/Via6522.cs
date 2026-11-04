using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// The 6522 Versatile Interface Adapter: two 8-bit ports with a direction register each, four
/// handshake lines, two 16-bit timers and an interrupt controller, on a 6502's bus. In Ben Eater's
/// computer it is what drives the LCD.
/// <para>
/// <b>The bus.</b> Selected while CS1 is high and CS2B low; RS0-RS3 pick one of sixteen registers.
/// A read drives the data lines while PHI2 is high and takes effect — clearing the flags a read
/// clears — as PHI2 falls; a write takes the data bus as PHI2 falls. The timers count on the same
/// edge, once a cycle.
/// </para>
/// <para>
/// <b>The ports.</b> A one in a DDR bit makes that pin an output, driving the ORA or ORB bit; a zero
/// lets it go so the pin can be read. Reading port B gives the output register for output bits and
/// the pins for inputs; reading port A always gives the pins. With latching on (ACR bits 0 and 1)
/// the inputs are captured on the active CA1 or CB1 edge and a read gives the capture.
/// </para>
/// <para>
/// <b>Handshaking.</b> CA1 and CB1 are edge inputs (PCR picks which edge) that set their interrupt
/// flags. CA2 and CB2 are inputs on either edge, independent or cleared by port access, or outputs:
/// a handshake (low on access to port A — writes only for port B — high again on the next CA1/CB1
/// edge), a one-cycle pulse, or held low or high by hand.
/// </para>
/// <para>
/// <b>Timers.</b> Timer 1 counts down from its latch every cycle and flags when it passes zero —
/// once after a load in one-shot mode, every N + 2 cycles in free-run, where it reloads itself. With
/// ACR bit 7 set it also drives PB7: low from the load to the time-out in one-shot mode, inverting
/// at each time-out in free-run, which is a square wave with no software at all. Timer 2 is a
/// one-shot counting cycles or, with ACR bit 5, falling edges on PB6.
/// </para>
/// <para>
/// <b>Interrupts.</b> IFR holds the flags, IER says which of them pull IRQB low, and IFR bit 7 reads
/// as one while any enabled flag is set. IRQB is open-drain, as on the MOS part, so it needs a
/// pull-up (the 6502's IRQB line normally has one). Writing ones to IFR clears those flags; writing
/// IER with bit 7 set enables the bits given and with it clear disables them.
/// </para>
/// <para>
/// Not modelled: the shift register (its register reads and writes, but nothing shifts and its flag
/// never sets), and timing finer than a whole cycle.
/// </para>
/// <para>
/// Pinout: 1=VSS, 2-9=PA0-PA7, 10-17=PB0-PB7, 18=CB1, 19=CB2, 20=VCC, 21=IRQB, 22=R/WB, 23=CS2B,
/// 24=CS1, 25=PHI2, 26-33=D7-D0, 34=RESB, 35-38=RS3-RS0, 39=CA2, 40=CA1.
/// </para>
/// </summary>
public sealed class Via6522 : DigitalIc
{
    private const int IfrCa2 = 0x01, IfrCa1 = 0x02, IfrSr = 0x04, IfrCb2 = 0x08, IfrCb1 = 0x10, IfrT2 = 0x20, IfrT1 = 0x40;

    private readonly Terminal[] _pa = new Terminal[8];
    private readonly Terminal[] _pb = new Terminal[8];
    private readonly Terminal[] _data = new Terminal[8];
    private readonly Terminal[] _rs = new Terminal[4];

    // Output indices: PA0-7 = 0-7, PB0-7 = 8-15, D0-7 = 16-23, CA2 = 24, CB2 = 25, IRQB = 26.
    private const int DataIndex = 16, Ca2Index = 24, Cb2Index = 25, IrqIndex = 26;

    private byte _ora, _orb, _ddra, _ddrb, _acr, _pcr, _ifr, _ier, _sr;
    private byte _ira, _irb;
    private ushort _t1, _t1Latch, _t2;
    private byte _t2LatchLow;
    private bool _t1Armed, _t2Armed, _t1Reload, _t1Loaded, _t2Loaded;
    private bool _pb7;
    private bool _ca2Out = true, _cb2Out = true;
    private bool _ca2Pulse, _cb2Pulse;

    private LogicState _lastPhi2 = LogicState.Unknown;
    private bool _lastCa1, _lastCb1, _lastCa2, _lastCb2, _lastPb6;

    public Via6522() : base(40)
    {
        PropagationDelay = 40e-9;

        var pins = new Terminal[41];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 40));

        Gnd = Pin(1, "VSS", TerminalType.Ground);
        for (var i = 0; i < 8; i++) _pa[i] = Pin(2 + i, $"PA{i}", TerminalType.Bidirectional);
        for (var i = 0; i < 8; i++) _pb[i] = Pin(10 + i, $"PB{i}", TerminalType.Bidirectional);
        Cb1 = Pin(18, "CB1", TerminalType.Input);
        Cb2 = Pin(19, "CB2", TerminalType.Bidirectional);
        Vcc = Pin(20, "VCC", TerminalType.Power);
        Irq = Pin(21, "IRQB", TerminalType.Bidirectional);
        ReadWrite = Pin(22, "RWB", TerminalType.Input);
        ChipSelect2 = Pin(23, "CS2B", TerminalType.Input);
        ChipSelect1 = Pin(24, "CS1", TerminalType.Input);
        Clock = Pin(25, "PHI2", TerminalType.Input);
        for (var i = 0; i < 8; i++) _data[i] = Pin(33 - i, $"D{i}", TerminalType.Bidirectional);
        Reset = Pin(34, "RESB", TerminalType.Input);
        for (var i = 0; i < 4; i++) _rs[i] = Pin(38 - i, $"RS{i}", TerminalType.Input);
        Ca2 = Pin(39, "CA2", TerminalType.Bidirectional);
        Ca1 = Pin(40, "CA1", TerminalType.Input);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [Clock, ReadWrite, ChipSelect1, ChipSelect2, Reset, .. _rs, Ca1, Cb1, Ca2, Cb2, .. _pa, .. _pb, .. _data],
            [.. _pa, .. _pb, .. _data, Ca2, Cb2, Irq]);
    }

    public override string PartNumber => "6522";

    public override string ComponentType => "6522 VIA";

    public override bool LabelsPins => true;

    public IReadOnlyList<Terminal> PortA => _pa;

    public IReadOnlyList<Terminal> PortB => _pb;

    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>RS0-RS3, the register select inputs.</summary>
    public IReadOnlyList<Terminal> RegisterSelect => _rs;

    public Terminal Clock { get; }

    public Terminal ReadWrite { get; }

    /// <summary>CS1, active high.</summary>
    public Terminal ChipSelect1 { get; }

    /// <summary>CS2B, active low.</summary>
    public Terminal ChipSelect2 { get; }

    public Terminal Reset { get; }

    /// <summary>IRQB: open-drain, low while an enabled interrupt flag is set.</summary>
    public Terminal Irq { get; }

    public Terminal Ca1 { get; }

    public Terminal Ca2 { get; }

    public Terminal Cb1 { get; }

    public Terminal Cb2 { get; }

    public override string ValueLabel => $"A {_ora:X2}/{_ddra:X2} B {_orb:X2}/{_ddrb:X2}";

    // ---- what a test or an agent wants to see -----------------------------------

    public byte OutputA => _ora;

    public byte OutputB => _orb;

    public byte DirectionA => _ddra;

    public byte DirectionB => _ddrb;

    public byte InterruptFlags => (byte)(_ifr | ((_ifr & _ier & 0x7F) != 0 ? 0x80 : 0));

    public byte InterruptEnable => _ier;

    public ushort Timer1 => _t1;

    public ushort Timer2 => _t2;

    public byte AuxiliaryControl => _acr;

    public byte PeripheralControl => _pcr;

    // ---- the logic ------------------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var phi2 = context.ReadInput(Clock, Levels);
        var previous = _lastPhi2;
        _lastPhi2 = phi2;

        if (context.ReadInput(Reset, Levels).IsLow())
        {
            ResetRegisters();
            DriveOutputs(context, driveData: false, 0);
            return;
        }

        var selected = context.ReadInput(ChipSelect1, Levels).IsHigh() && context.ReadInput(ChipSelect2, Levels).IsLow();
        var reading = context.ReadInput(ReadWrite, Levels).IsHigh();
        var register = 0;
        for (var i = 0; i < 4; i++)
            if (context.ReadInput(_rs[i], Levels).IsHigh()) register |= 1 << i;

        Handshakes(context);

        if (!context.IsInitializing && previous.IsHigh() && phi2.IsLow())
        {
            // The end of a cycle: the access takes effect, and the timers count.
            _t1Loaded = _t2Loaded = false;

            if (selected)
            {
                if (reading) AfterRead(register);
                else Write(register, ReadBus(context));
            }

            Tick();
        }

        var driveData = selected && reading && phi2.IsHigh();
        DriveOutputs(context, driveData, driveData ? Read(register, context) : (byte)0);
    }

    private byte ReadBus(IDigitalContext context)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(_data[i], Levels).IsHigh()) value |= 1 << i;

        return (byte)value;
    }

    private byte Pins(IDigitalContext context, Terminal[] port)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(port[i], Levels).IsHigh()) value |= 1 << i;

        return (byte)value;
    }

    /// <summary>What a read of a register returns. No side effects: those happen as the cycle ends.</summary>
    private byte Read(int register, IDigitalContext context) => register switch
    {
        0 => (byte)((_orb & _ddrb) | (((_acr & 0x02) != 0 ? _irb : Pins(context, _pb)) & ~_ddrb)),
        1 or 15 => (_acr & 0x01) != 0 ? _ira : Pins(context, _pa),
        2 => _ddrb,
        3 => _ddra,
        4 => (byte)_t1,
        5 => (byte)(_t1 >> 8),
        6 => (byte)_t1Latch,
        7 => (byte)(_t1Latch >> 8),
        8 => (byte)_t2,
        9 => (byte)(_t2 >> 8),
        10 => _sr,
        11 => _acr,
        12 => _pcr,
        13 => InterruptFlags,
        _ => (byte)(_ier | 0x80),
    };

    /// <summary>The flags a read clears, as it ends.</summary>
    private void AfterRead(int register)
    {
        switch (register)
        {
            case 0:
                ClearPortFlags(IfrCb1, IfrCb2, Cb2Independent);
                break;
            case 1:
                ClearPortFlags(IfrCa1, IfrCa2, Ca2Independent);
                if (Ca2Mode is 4 or 5) _ca2Out = false;
                if (Ca2Mode == 5) _ca2Pulse = true;
                break;
            case 4:
                _ifr &= unchecked((byte)~IfrT1);
                break;
            case 8:
                _ifr &= unchecked((byte)~IfrT2);
                break;
            case 10:
                _ifr &= unchecked((byte)~IfrSr);
                break;
        }
    }

    private void Write(int register, byte value)
    {
        switch (register)
        {
            case 0:
                _orb = value;
                ClearPortFlags(IfrCb1, IfrCb2, Cb2Independent);
                if (Cb2Mode is 4 or 5) _cb2Out = false;
                if (Cb2Mode == 5) _cb2Pulse = true;
                break;
            case 1:
                _ora = value;
                ClearPortFlags(IfrCa1, IfrCa2, Ca2Independent);
                if (Ca2Mode is 4 or 5) _ca2Out = false;
                if (Ca2Mode == 5) _ca2Pulse = true;
                break;
            case 15:
                _ora = value;
                break;
            case 2:
                _ddrb = value;
                break;
            case 3:
                _ddra = value;
                break;
            case 4 or 6:
                _t1Latch = (ushort)((_t1Latch & 0xFF00) | value);
                break;
            case 5:
                _t1Latch = (ushort)((_t1Latch & 0x00FF) | (value << 8));
                _t1 = _t1Latch;
                _ifr &= unchecked((byte)~IfrT1);
                _t1Armed = true;
                _t1Reload = false;
                _t1Loaded = true;
                _pb7 = false;
                break;
            case 7:
                _t1Latch = (ushort)((_t1Latch & 0x00FF) | (value << 8));
                _ifr &= unchecked((byte)~IfrT1);
                break;
            case 8:
                _t2LatchLow = value;
                break;
            case 9:
                _t2 = (ushort)((value << 8) | _t2LatchLow);
                _ifr &= unchecked((byte)~IfrT2);
                _t2Armed = true;
                _t2Loaded = true;
                break;
            case 10:
                _sr = value;
                _ifr &= unchecked((byte)~IfrSr);
                break;
            case 11:
                _acr = value;
                break;
            case 12:
                _pcr = value;
                if (Ca2Mode == 6) _ca2Out = false;
                if (Ca2Mode == 7) _ca2Out = true;
                if (Cb2Mode == 6) _cb2Out = false;
                if (Cb2Mode == 7) _cb2Out = true;
                if (Ca2Mode < 4) _ca2Out = true;
                if (Cb2Mode < 4) _cb2Out = true;
                break;
            case 13:
                _ifr &= (byte)~(value & 0x7F);
                break;
            default:
                if ((value & 0x80) != 0) _ier |= (byte)(value & 0x7F);
                else _ier &= (byte)~(value & 0x7F);
                break;
        }

        NotifyValueChanged();
    }

    private int Ca2Mode => (_pcr >> 1) & 7;

    private int Cb2Mode => (_pcr >> 5) & 7;

    private bool Ca2Independent => Ca2Mode is 1 or 3;

    private bool Cb2Independent => Cb2Mode is 1 or 3;

    private void ClearPortFlags(int edgeFlag, int lineFlag, bool independent)
    {
        _ifr &= (byte)~edgeFlag;
        if (!independent) _ifr &= (byte)~lineFlag;
    }

    /// <summary>The edges on CA1, CB1, CA2 and CB2 (when they are inputs) and PB6 (for counting).</summary>
    private void Handshakes(IDigitalContext context)
    {
        var ca1 = context.ReadInput(Ca1, Levels).IsHigh();
        var cb1 = context.ReadInput(Cb1, Levels).IsHigh();
        var ca2 = context.ReadInput(Ca2, Levels).IsHigh();
        var cb2 = context.ReadInput(Cb2, Levels).IsHigh();
        var pb6 = context.ReadInput(_pb[6], Levels).IsHigh();

        if (!context.IsInitializing)
        {
            if (Active(_lastCa1, ca1, (_pcr & 0x01) != 0))
            {
                _ifr |= IfrCa1;
                if ((_acr & 0x01) != 0) _ira = Pins(context, _pa);
                if (Ca2Mode == 4) _ca2Out = true;
            }

            if (Active(_lastCb1, cb1, (_pcr & 0x10) != 0))
            {
                _ifr |= IfrCb1;
                if ((_acr & 0x02) != 0) _irb = Pins(context, _pb);
                if (Cb2Mode == 4) _cb2Out = true;
            }

            if (Ca2Mode < 4 && Active(_lastCa2, ca2, Ca2Mode >= 2)) _ifr |= IfrCa2;
            if (Cb2Mode < 4 && Active(_lastCb2, cb2, Cb2Mode >= 2)) _ifr |= IfrCb2;

            // Timer 2 counting pulses: a falling edge on PB6 is a count.
            if ((_acr & 0x20) != 0 && _lastPb6 && !pb6)
            {
                _t2--;
                if (_t2 == 0 && _t2Armed)
                {
                    _ifr |= IfrT2;
                    _t2Armed = false;
                }
            }
        }

        _lastCa1 = ca1;
        _lastCb1 = cb1;
        _lastCa2 = ca2;
        _lastCb2 = cb2;
        _lastPb6 = pb6;
    }

    private static bool Active(bool before, bool now, bool rising) => rising ? !before && now : before && !now;

    /// <summary>One cycle of the timers, at the end of the cycle.</summary>
    private void Tick()
    {
        // A pulse output lasts the one cycle after the access that started it.
        if (_ca2Pulse) _ca2Pulse = false;
        else if (Ca2Mode == 5) _ca2Out = true;
        if (_cb2Pulse) _cb2Pulse = false;
        else if (Cb2Mode == 5) _cb2Out = true;

        if (!_t1Loaded)
        {
            if (_t1Reload)
            {
                _t1 = _t1Latch;
                _t1Reload = false;
            }
            else if (_t1 == 0)
            {
                var freeRun = (_acr & 0x40) != 0;

                if (freeRun || _t1Armed)
                {
                    _ifr |= IfrT1;
                    _pb7 = freeRun ? !_pb7 : true;
                }

                if (!freeRun) _t1Armed = false;

                if (freeRun) _t1Reload = true;
                else _t1 = 0xFFFF;
            }
            else
            {
                _t1--;
            }
        }

        if (!_t2Loaded && (_acr & 0x20) == 0)
        {
            if (_t2 == 0 && _t2Armed)
            {
                _ifr |= IfrT2;
                _t2Armed = false;
            }

            _t2--;
        }
    }

    private void DriveOutputs(IDigitalContext context, bool driveData, byte value)
    {
        var delay = DelayFor(context);

        for (var i = 0; i < 8; i++)
        {
            var a = (_ddra & (1 << i)) != 0;
            Request(context, i, a ? Level((_ora >> i) & 1) : LogicState.HighImpedance, delay);

            var b = (_ddrb & (1 << i)) != 0;
            var bit = (_orb >> i) & 1;
            if (i == 7 && (_acr & 0x80) != 0)
            {
                b = true;
                bit = _pb7 ? 1 : 0;
            }

            Request(context, 8 + i, b ? Level(bit) : LogicState.HighImpedance, delay);
            Request(context, DataIndex + i, driveData ? Level((value >> i) & 1) : LogicState.HighImpedance, delay);
        }

        Request(context, Ca2Index, Ca2Mode >= 4 ? Level(_ca2Out ? 1 : 0) : LogicState.HighImpedance, delay);
        Request(context, Cb2Index, Cb2Mode >= 4 ? Level(_cb2Out ? 1 : 0) : LogicState.HighImpedance, delay);
        Request(context, IrqIndex, (_ifr & _ier & 0x7F) != 0 ? LogicState.Low : LogicState.HighImpedance, delay);
    }

    private static LogicState Level(int bit) => bit != 0 ? LogicState.High : LogicState.Low;

    /// <summary>What RESB does: every register cleared but the timers, latches and shift register.</summary>
    private void ResetRegisters()
    {
        _ora = _orb = _ddra = _ddrb = _acr = _pcr = _ifr = _ier = 0;
        _ira = _irb = 0;
        _t1Armed = _t2Armed = _t1Reload = _t1Loaded = _t2Loaded = false;
        _pb7 = false;
        _ca2Out = _cb2Out = true;
        _ca2Pulse = _cb2Pulse = false;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        ResetRegisters();
        _t1 = _t1Latch = _t2 = 0;
        _t2LatchLow = 0;
        _sr = 0;
        _lastPhi2 = LogicState.Unknown;
        _lastCa1 = _lastCb1 = _lastCa2 = _lastCb2 = _lastPb6 = false;
    }

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "RWB" => "R/WB",
        _ => pin.Name,
    };
}
