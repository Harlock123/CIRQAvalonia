using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// The Motorola MC6821 Peripheral Interface Adapter — and the MOS 6520, the same chip under another
/// number: two 8-bit ports, each with a data direction register and a control register, and four
/// handshake lines, on a 6800-family bus clocked by E.
/// <para>
/// <b>The bus.</b> Selected while CS0 and CS1 are high and /CS2 low; RS1 and RS0 pick one of four
/// addresses. A read drives the data lines while E is high and takes effect — clearing the flags a
/// read clears — as E falls; a write takes the data bus as E falls. The control registers decide
/// what the other two addresses are: bit 2 of CRA set reaches the peripheral data register A at
/// RS = 0, clear the data direction register A; CRB does the same for RS = 2.
/// </para>
/// <list type="bullet">
/// <item><b>Control register</b> (CRA, and CRB alike): bit 0 enables the CA1 interrupt, bit 1 picks
/// CA1's active edge (0 falling, 1 rising), bit 2 is the DDR/data switch, bits 3-5 control CA2,
/// bit 6 is the CA2 flag and bit 7 the CA1 flag — those two read-only, set by an active edge and
/// cleared by a read of the port's data register.</item>
/// <item><b>CA2 as an input</b> (bit 5 clear): bit 4 its active edge, bit 3 its interrupt enable.</item>
/// <item><b>CA2 as an output</b> (bit 5 set): bits 4-3 = 00, a read strobe with CA1 restore — low as
/// E falls after a read of port A's data, high again on the next active CA1 edge (a handshake);
/// 01, a read strobe with E restore — low for one cycle after the read; 10 and 11, CA2 held at bit 3.
/// CB2 does the same for <i>writes</i> of port B's data, going low on the next rising edge of E,
/// and back high on CB1 or on the rising edge after a cycle the chip was not selected in.</item>
/// <item><b>Interrupts.</b> /IRQA is low while CA1's flag and enable are set, or CA2's (as an
/// input); /IRQB the same for B. Both are open drain, and want a pull-up.</item>
/// </list>
/// <para>
/// <b>The two ports are built differently</b>, and it shows. Port A's lines have pull-ups inside —
/// an input reads high when nothing drives it, and pulling one low takes a standard TTL load of
/// current — and a port A output is a pull-down with that passive pull-up for its high: reading port
/// A always reads the pins, so a heavily loaded output reads as what is really on it. Port B's lines
/// are three-state: an input floats, with no pull-up, and an output drives both ways, strongly
/// enough to turn on a Darlington transistor; reading port B gives the output register for the
/// output bits, whatever the load does to the pin, and the pins for the inputs. CA2 is pulled up
/// like port A; CB2 floats like port B. The pull-ups are stamped resistors
/// (<see cref="PullUpResistance"/>).
/// </para>
/// <para>
/// /RESET clears every register: every line an input, the interrupts off. Not modelled: timing finer
/// than a whole E cycle, and the pull-ups' and drivers' exact curves.
/// </para>
/// <para>
/// Pinout (DIP-40): 1 = VSS, 2-9 = PA0-PA7, 10-17 = PB0-PB7, 18 = CB1, 19 = CB2, 20 = VCC, 21 = R/W,
/// 22 = CS0, 23 = /CS2, 24 = CS1, 25 = E, 26-33 = D7-D0, 34 = /RESET, 35 = RS1, 36 = RS0, 37 = /IRQB,
/// 38 = /IRQA, 39 = CA2, 40 = CA1.
/// </para>
/// </summary>
public abstract class PeripheralInterfaceAdapter : DigitalIc, IRunState
{
    /// <summary>Port A's (and CA2's) pull-ups: about one TTL load, 1.6 mA into a line held low.</summary>
    public const double PullUpResistance = 3e3;

    private readonly Terminal[] _pa = new Terminal[8];
    private readonly Terminal[] _pb = new Terminal[8];
    private readonly Terminal[] _data = new Terminal[8];

    // Outputs: PA0-7 = 0-7, PB0-7 = 8-15, D0-7 = 16-23, CA2 = 24, CB2 = 25, /IRQA = 26, /IRQB = 27.
    private const int DataIndex = 16, Ca2Index = 24, Cb2Index = 25, IrqAIndex = 26, IrqBIndex = 27;

    private byte _ora, _orb, _ddra, _ddrb, _cra, _crb;
    private bool _ca2Out = true, _cb2Out = true;
    private bool _ca2Pulse, _cb2PulseDue, _cb2Pulse, _cb2Deselected;

    private LogicState _lastE = LogicState.Unknown;
    private bool _lastCa1, _lastCb1, _lastCa2, _lastCb2;

    private protected PeripheralInterfaceAdapter(string enable) : base(40)
    {
        PropagationDelay = 50e-9;

        var pins = new Terminal[41];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 40));

        Gnd = Pin(1, "VSS", TerminalType.Ground);
        for (var i = 0; i < 8; i++) _pa[i] = Pin(2 + i, $"PA{i}", TerminalType.Bidirectional);
        for (var i = 0; i < 8; i++) _pb[i] = Pin(10 + i, $"PB{i}", TerminalType.Bidirectional);
        Cb1 = Pin(18, "CB1", TerminalType.Input);
        Cb2 = Pin(19, "CB2", TerminalType.Bidirectional);
        Vcc = Pin(20, "VCC", TerminalType.Power);
        ReadWrite = Pin(21, "RW", TerminalType.Input);
        ChipSelect0 = Pin(22, "CS0", TerminalType.Input);
        ChipSelect2 = Pin(23, "CS2", TerminalType.Input);
        ChipSelect1 = Pin(24, "CS1", TerminalType.Input);
        Enable = Pin(25, enable, TerminalType.Input);
        for (var i = 0; i < 8; i++) _data[i] = Pin(33 - i, $"D{i}", TerminalType.Bidirectional);
        Reset = Pin(34, "RESET", TerminalType.Input);
        RegisterSelect1 = Pin(35, "RS1", TerminalType.Input);
        RegisterSelect0 = Pin(36, "RS0", TerminalType.Input);
        IrqB = Pin(37, "IRQB", TerminalType.Bidirectional);
        IrqA = Pin(38, "IRQA", TerminalType.Bidirectional);
        Ca2 = Pin(39, "CA2", TerminalType.Bidirectional);
        Ca1 = Pin(40, "CA1", TerminalType.Input);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [Enable, ReadWrite, ChipSelect0, ChipSelect1, ChipSelect2, Reset, RegisterSelect0, RegisterSelect1, Ca1, Cb1, Ca2, Cb2, .. _pa, .. _pb, .. _data],
            [.. _pa, .. _pb, .. _data, Ca2, Cb2, IrqA, IrqB]);
    }

    public override bool LabelsPins => true;

    public IReadOnlyList<Terminal> PortA => _pa;

    public IReadOnlyList<Terminal> PortB => _pb;

    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>E (Φ2 on the 6520): the bus clock. An access takes effect as it falls.</summary>
    public Terminal Enable { get; }

    public Terminal ReadWrite { get; }

    /// <summary>CS0, active high.</summary>
    public Terminal ChipSelect0 { get; }

    /// <summary>CS1, active high.</summary>
    public Terminal ChipSelect1 { get; }

    /// <summary>/CS2, active low.</summary>
    public Terminal ChipSelect2 { get; }

    public Terminal RegisterSelect0 { get; }

    public Terminal RegisterSelect1 { get; }

    /// <summary>/RESET, active low: every register cleared.</summary>
    public Terminal Reset { get; }

    /// <summary>/IRQA: open drain, low while port A's interrupt is pending.</summary>
    public Terminal IrqA { get; }

    /// <summary>/IRQB: open drain, low while port B's interrupt is pending.</summary>
    public Terminal IrqB { get; }

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

    public byte ControlA => _cra;

    public byte ControlB => _crb;

    /// <summary>CA2 and CB2 as the chip drives them when they are outputs.</summary>
    public (bool Ca2, bool Cb2) StrobeOutputs => (_ca2Out, _cb2Out);

    /// <summary>/IRQA is being pulled low.</summary>
    public bool IrqAAsserted => ((_cra & 0x80) != 0 && (_cra & 0x01) != 0) || ((_cra & 0x40) != 0 && (_cra & 0x08) != 0 && (_cra & 0x20) == 0);

    /// <summary>/IRQB is being pulled low.</summary>
    public bool IrqBAsserted => ((_crb & 0x80) != 0 && (_crb & 0x01) != 0) || ((_crb & 0x40) != 0 && (_crb & 0x08) != 0 && (_crb & 0x20) == 0);

    // ---- the logic ------------------------------------------------------------------

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        // Port A's passive pull-ups, and CA2's.
        var vcc = system.Node(Vcc);
        foreach (var pin in _pa.Append(Ca2))
            system.StampResistor(system.Node(pin), vcc, PullUpResistance);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var e = context.ReadInput(Enable, Levels);
        var previous = _lastE;
        _lastE = e;

        if (context.ReadInput(Reset, Levels).IsLow())
        {
            ResetRegisters();
            Drive(context, null);
            return;
        }

        var selected = context.ReadInput(ChipSelect0, Levels).IsHigh() && context.ReadInput(ChipSelect1, Levels).IsHigh() &&
                       context.ReadInput(ChipSelect2, Levels).IsLow();
        var reading = context.ReadInput(ReadWrite, Levels).IsHigh();
        var register = (context.ReadInput(RegisterSelect1, Levels).IsHigh() ? 2 : 0) | (context.ReadInput(RegisterSelect0, Levels).IsHigh() ? 1 : 0);

        Handshakes(context);

        if (!context.IsInitializing && previous.IsLow() && e.IsHigh())
        {
            // E rising: CB2's write strobe goes low the cycle after the write, and back high a
            // cycle after one the chip was not selected in.
            if (_cb2PulseDue)
            {
                _cb2PulseDue = false;
                _cb2Out = false;
                _cb2Pulse = Cb2Mode == 5;
                _cb2Deselected = false;
            }
            else if (_cb2Pulse && _cb2Deselected)
            {
                _cb2Out = true;
                _cb2Pulse = false;
            }
        }

        if (!context.IsInitializing && previous.IsHigh() && e.IsLow())
        {
            // E falling: the end of a cycle.
            if (_cb2Pulse && !selected) _cb2Deselected = true;

            if (_ca2Pulse && !selected)
            {
                _ca2Out = true;
                _ca2Pulse = false;
            }

            if (selected)
            {
                if (reading) AfterRead(register);
                else Write(register, ReadBus(context));
            }
        }

        var driveData = selected && reading && e.IsHigh();
        Drive(context, driveData ? Read(register, context) : null);
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

    /// <summary>What a read returns. Port A is the pins, always; port B the output register where it drives.</summary>
    private byte Read(int register, IDigitalContext context) => register switch
    {
        0 => (_cra & 0x04) != 0 ? Pins(context, _pa) : _ddra,
        1 => _cra,
        2 => (_crb & 0x04) != 0 ? (byte)((_orb & _ddrb) | (Pins(context, _pb) & ~_ddrb)) : _ddrb,
        _ => _crb,
    };

    private int Ca2Mode => (_cra >> 3) & 7;

    private int Cb2Mode => (_crb >> 3) & 7;

    /// <summary>A read of a data register clears its port's flags; port A's starts CA2's read strobe.</summary>
    private void AfterRead(int register)
    {
        if (register == 0 && (_cra & 0x04) != 0)
        {
            _cra &= 0x3F;
            if (Ca2Mode is 4 or 5) _ca2Out = false;
            _ca2Pulse = Ca2Mode == 5;
            NotifyValueChanged();
        }
        else if (register == 2 && (_crb & 0x04) != 0)
        {
            _crb &= 0x3F;
            NotifyValueChanged();
        }
    }

    private void Write(int register, byte value)
    {
        switch (register)
        {
            case 0:
                if ((_cra & 0x04) != 0) _ora = value;
                else _ddra = value;
                break;

            case 1:
                _cra = (byte)((_cra & 0xC0) | (value & 0x3F));
                if ((_cra & 0x20) != 0) _cra &= 0xBF;   // CA2 an output: its flag stays clear
                if (Ca2Mode is 6 or 7) _ca2Out = Ca2Mode == 7;
                else if (Ca2Mode < 4) _ca2Out = true;
                break;

            case 2:
                if ((_crb & 0x04) != 0)
                {
                    _orb = value;
                    if (Cb2Mode is 4 or 5) _cb2PulseDue = true;
                }
                else
                {
                    _ddrb = value;
                }

                break;

            default:
                _crb = (byte)((_crb & 0xC0) | (value & 0x3F));
                if ((_crb & 0x20) != 0) _crb &= 0xBF;
                if (Cb2Mode is 6 or 7) _cb2Out = Cb2Mode == 7;
                else if (Cb2Mode < 4) _cb2Out = true;
                break;
        }

        NotifyValueChanged();
    }

    /// <summary>The edges on CA1, CB1, and CA2 and CB2 when they are inputs.</summary>
    private void Handshakes(IDigitalContext context)
    {
        var ca1 = context.ReadInput(Ca1, Levels).IsHigh();
        var cb1 = context.ReadInput(Cb1, Levels).IsHigh();
        var ca2 = context.ReadInput(Ca2, Levels).IsHigh();
        var cb2 = context.ReadInput(Cb2, Levels).IsHigh();

        if (!context.IsInitializing)
        {
            if (Active(_lastCa1, ca1, (_cra & 0x02) != 0))
            {
                _cra |= 0x80;
                if (Ca2Mode == 4) _ca2Out = true;
                NotifyValueChanged();
            }

            if (Active(_lastCb1, cb1, (_crb & 0x02) != 0))
            {
                _crb |= 0x80;
                if (Cb2Mode == 4) _cb2Out = true;
                NotifyValueChanged();
            }

            if ((_cra & 0x20) == 0 && Active(_lastCa2, ca2, (_cra & 0x10) != 0)) _cra |= 0x40;
            if ((_crb & 0x20) == 0 && Active(_lastCb2, cb2, (_crb & 0x10) != 0)) _crb |= 0x40;
        }

        _lastCa1 = ca1;
        _lastCb1 = cb1;
        _lastCa2 = ca2;
        _lastCb2 = cb2;
    }

    private static bool Active(bool before, bool now, bool rising) => rising ? !before && now : before && !now;

    private void Drive(IDigitalContext context, byte? data)
    {
        var delay = DelayFor(context);

        for (var i = 0; i < 8; i++)
        {
            // Port A: a one is the pull-up's, a zero the pull-down's. Port B drives both ways.
            var a = (_ddra & (1 << i)) != 0 && (_ora & (1 << i)) == 0;
            Request(context, i, a ? LogicState.Low : LogicState.HighImpedance, delay);

            var b = (_ddrb & (1 << i)) != 0;
            Request(context, 8 + i, b ? Level((_orb >> i) & 1) : LogicState.HighImpedance, delay);
            Request(context, DataIndex + i, data is { } d ? Level((d >> i) & 1) : LogicState.HighImpedance, delay);
        }

        Request(context, Ca2Index, (_cra & 0x20) != 0 ? Level(_ca2Out ? 1 : 0) : LogicState.HighImpedance, delay);
        Request(context, Cb2Index, (_crb & 0x20) != 0 ? Level(_cb2Out ? 1 : 0) : LogicState.HighImpedance, delay);
        Request(context, IrqAIndex, IrqAAsserted ? LogicState.Low : LogicState.HighImpedance, delay);
        Request(context, IrqBIndex, IrqBAsserted ? LogicState.Low : LogicState.HighImpedance, delay);
    }

    private static LogicState Level(int bit) => bit != 0 ? LogicState.High : LogicState.Low;

    private void ResetRegisters()
    {
        _ora = _orb = _ddra = _ddrb = _cra = _crb = 0;
        _ca2Out = _cb2Out = true;
        _ca2Pulse = _cb2PulseDue = _cb2Pulse = _cb2Deselected = false;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        ResetRegisters();
        _lastE = LogicState.Unknown;
        _lastCa1 = _lastCb1 = _lastCa2 = _lastCb2 = false;
    }

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "RW" => "R/W",
        "CS2" or "RESET" or "IRQA" or "IRQB" => "/" + pin.Name,
        _ => pin.Name,
    };

    private sealed record RunState(byte Ora, byte Orb, byte Ddra, byte Ddrb, byte Cra, byte Crb, bool Ca2, bool Cb2);

    public object CaptureRunState() => new RunState(_ora, _orb, _ddra, _ddrb, _cra, _crb, _ca2Out, _cb2Out);

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;
        (_ora, _orb, _ddra, _ddrb, _cra, _crb, _ca2Out, _cb2Out) = (run.Ora, run.Orb, run.Ddra, run.Ddrb, run.Cra, run.Crb, run.Ca2, run.Cb2);
        NotifyValueChanged();
    }
}

/// <summary>The Motorola MC6821 PIA. See <see cref="PeripheralInterfaceAdapter"/>.</summary>
public sealed class Pia6821() : PeripheralInterfaceAdapter("E")
{
    public override string PartNumber => "MC6821";

    public override string ComponentType => "6821 PIA";
}

/// <summary>
/// The MOS 6520 PIA, MOS Technology's second source for Motorola's 6820 — the PIA in the PET and
/// the Atari 400 and 800. The same pinout, registers, handshakes and interrupts as the 6821; its pin
/// 25 is called Φ2 rather than E. Here it is the 6821 under another number.
/// </summary>
public sealed class Pia6520() : PeripheralInterfaceAdapter("PHI2")
{
    public override string PartNumber => "6520";

    public override string ComponentType => "6520 PIA";
}
