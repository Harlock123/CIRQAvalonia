using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// The Zilog Z80 PIO, parallel input/output: two 8-bit ports, each with a mode, a handshake and an
/// interrupt of its own, on the Z80's bus and in its interrupt daisy chain.
/// <para>
/// <b>The bus.</b> B/A picks the port (low A), C/D the data (low) or control (high) register; there is
/// no /WR — see <see cref="Z80Peripheral"/>. A control write is one of four words, told apart by its
/// low bits: <c>xxxxxxx0</c> the port's interrupt vector; <c>mm xx 1111</c> the mode — 0 output,
/// 1 input, 2 bidirectional (port A only), 3 bit control, which takes the next control byte as the
/// I/O register (a 1 makes that line an input); <c>ealm 0111</c> the interrupt control — e enables,
/// a picks AND (1) or OR of the monitored lines, l active high (1) or low, and m says the mask
/// follows as the next byte (a 1 masks a line out of the logic); <c>e xxx 0011</c> sets or clears the
/// enable alone. Reading the control register gives nothing useful on the NMOS part ($FF here).
/// </para>
/// <list type="bullet">
/// <item><b>Mode 0, output.</b> A write to the data register puts the byte on the port and raises RDY;
/// the peripheral's /STB pulse says it has taken it — RDY falls as /STB rises, and the port
/// interrupts. A read gives the output register.</item>
/// <item><b>Mode 1, input.</b> /STB low latches the port's lines into the input register; as it rises
/// RDY falls and the port interrupts. A read gives the latched byte and raises RDY again — the first
/// RDY comes from a read, so a program starts with one.</item>
/// <item><b>Mode 2, bidirectional</b> (port A, with port B in mode 3): output with ARDY and /ASTB — the
/// port's drivers on only while /ASTB is low — and input with BRDY and /BSTB, both handshakes
/// interrupting through port A's vector.</item>
/// <item><b>Mode 3, bit control.</b> Each line an input or an output as the I/O register says; a read
/// gives the pins of the inputs and the register of the outputs. The port interrupts when the logic
/// of its monitored input lines becomes true: with OR, any of them at the active level; with AND,
/// all of them. RDY stays low and /STB is ignored.</item>
/// </list>
/// <para>
/// <b>Interrupts</b> are port A then port B in priority, each with its own vector, for interrupt
/// mode 2. <b>Reset</b>: at power-on, and when /M1 stays low with neither /RD nor /IORQ for three
/// CLK rising edges (no Z80 cycle does that; a reset circuit does), both ports go to mode 1 with
/// interrupts disabled and every line masked; the vectors are kept.
/// </para>
/// <para>
/// Simplifications: the handshakes act on the strobe edges at once, not on CLK; a mode 3 interrupt
/// is the logic condition becoming true, so one already true when interrupts are enabled waits for
/// the next time; and the port lines are ordinary three-state logic, with no Darlington drive on
/// port B.
/// </para>
/// <para>
/// Pinout (DIP-40): 1 = D2, 2 = D7, 3 = D6, 4 = /CE, 5 = C/D, 6 = B/A, 7-10 = PA7-PA4, 11 = GND,
/// 12-15 = PA3-PA0, 16 = /ASTB, 17 = /BSTB, 18 = ARDY, 19 = D0, 20 = D1, 21 = BRDY, 22 = IEO,
/// 23 = /INT, 24 = IEI, 25 = CLK, 26 = +5V, 27-34 = PB7-PB0, 35 = /RD, 36 = /IORQ, 37 = /M1, 38 = D3,
/// 39 = D4, 40 = D5.
/// </para>
/// </summary>
public sealed class Z80Pio : Z80Peripheral, IRunState
{
    // Outputs: D0-D7 = 0-7, PA0-PA7 = 8-15, PB0-PB7 = 16-23, ARDY = 24, BRDY = 25, /INT = 26, IEO = 27.
    private const int PortAIndex = 8, PortBIndex = 16, ArdyIndex = 24, BrdyIndex = 25;

    private static readonly int[] DataPins = [19, 20, 1, 38, 39, 40, 3, 2];

    private readonly Terminal[] _portA = new Terminal[8];
    private readonly Terminal[] _portB = new Terminal[8];
    private readonly PortState[] _ports = [new(), new()];

    private bool _lastAstb = true, _lastBstb = true;
    private LogicState _lastClock = LogicState.Unknown;
    private int _m1Clocks;

    /// <summary>One port's registers and handshake.</summary>
    private sealed class PortState
    {
        public int Mode = 1;
        public byte Output;
        public byte Input;
        public byte Direction = 0xFF;
        public byte Mask = 0xFF;
        public byte Vector;
        public bool InterruptEnabled;
        public bool And;
        public bool ActiveHigh;
        public bool ExpectDirection;
        public bool ExpectMask;
        public bool Ready;
        public bool Pending;
        public bool Condition;

        public void Reset()
        {
            Mode = 1;
            Mask = 0xFF;
            Direction = 0xFF;
            InterruptEnabled = And = ActiveHigh = ExpectDirection = ExpectMask = Ready = Pending = Condition = false;
        }

        public PortState Copy() => (PortState)MemberwiseClone();
    }

    public Z80Pio() : base(40, 2)
    {
        PropagationDelay = 50e-9;

        var pins = new Terminal[41];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 40));

        for (var i = 0; i < 8; i++) SetDataPin(i, Pin(DataPins[i], $"D{i}", TerminalType.Bidirectional));
        ChipEnable = Pin(4, "CE", TerminalType.Input);
        ControlData = Pin(5, "CD", TerminalType.Input);
        PortSelect = Pin(6, "BA", TerminalType.Input);
        for (var i = 0; i < 4; i++) _portA[7 - i] = Pin(7 + i, $"PA{7 - i}", TerminalType.Bidirectional);
        Gnd = Pin(11, "GND", TerminalType.Ground);
        for (var i = 0; i < 4; i++) _portA[3 - i] = Pin(12 + i, $"PA{3 - i}", TerminalType.Bidirectional);
        StrobeA = Pin(16, "ASTB", TerminalType.Input);
        StrobeB = Pin(17, "BSTB", TerminalType.Input);
        var ardy = Pin(18, "ARDY", TerminalType.Output);
        var brdy = Pin(21, "BRDY", TerminalType.Output);
        var ieo = Pin(22, "IEO", TerminalType.Output);
        var interrupt = Pin(23, "INT", TerminalType.Bidirectional);
        InterruptEnableIn = Pin(24, "IEI", TerminalType.Input);
        Clock = Pin(25, "CLK", TerminalType.Input);
        Vcc = Pin(26, "VCC", TerminalType.Power);
        for (var i = 0; i < 8; i++) _portB[7 - i] = Pin(27 + i, $"PB{7 - i}", TerminalType.Bidirectional);
        ReadStrobe = Pin(35, "RD", TerminalType.Input);
        IoRequest = Pin(36, "IORQ", TerminalType.Input);
        M1 = Pin(37, "M1", TerminalType.Input);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [ChipEnable, ControlData, PortSelect, M1, IoRequest, ReadStrobe, InterruptEnableIn, Clock, StrobeA, StrobeB, .. Data, .. _portA, .. _portB],
            [.. Data, .. _portA, .. _portB, ardy, brdy, interrupt, ieo]);
    }

    private protected override int IntIndex => 26;

    private protected override int IeoIndex => 27;

    public override string PartNumber => "Z80 PIO";

    public override string ComponentType => "Z80 PIO";

    public IReadOnlyList<Terminal> PortA => _portA;

    public IReadOnlyList<Terminal> PortB => _portB;

    /// <summary>C/D: low the data register, high the control register.</summary>
    public Terminal ControlData { get; }

    /// <summary>B/A: low port A, high port B.</summary>
    public Terminal PortSelect { get; }

    /// <summary>/ASTB: port A's strobe (in mode 2, the output strobe).</summary>
    public Terminal StrobeA { get; }

    /// <summary>/BSTB: port B's strobe (in mode 2, port A's input strobe).</summary>
    public Terminal StrobeB { get; }

    public Terminal ReadyA => OutputTerminals[ArdyIndex];

    public Terminal ReadyB => OutputTerminals[BrdyIndex];

    public override IReadOnlyList<string> InterruptSources { get; } = ["port A", "port B"];

    // ---- what a test or an agent wants to see -----------------------------------

    /// <summary>A port's registers: mode, output, input, I/O register (mode 3), mask, vector, interrupt control and handshake.</summary>
    public (int Mode, byte Output, byte Input, byte Direction, byte Mask, byte Vector, bool InterruptEnabled, bool And, bool ActiveHigh, bool Ready, bool Pending) Describe(bool b)
    {
        var p = _ports[b ? 1 : 0];
        return (p.Mode, p.Output, p.Input, p.Direction, p.Mask, p.Vector, p.InterruptEnabled, p.And, p.ActiveHigh, p.Ready, p.Pending);
    }

    public override string ValueLabel =>
        string.Create(CultureInfo.InvariantCulture, $"A mode {_ports[0].Mode} B mode {_ports[1].Mode}");

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "CE" or "ASTB" or "BSTB" or "INT" or "RD" or "IORQ" or "M1" => "/" + pin.Name,
        "CD" => "C/D",
        "BA" => "B/A",
        "VCC" => "+5V",
        _ => pin.Name,
    };

    // ---- the logic --------------------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var astb = !context.ReadInput(StrobeA, Levels).IsLow();
        var bstb = !context.ReadInput(StrobeB, Levels).IsLow();
        _pinsSeen[0] = ReadPort(context, _portA);
        _pinsSeen[1] = ReadPort(context, _portB);

        if (!context.IsInitializing)
        {
            ResetByM1(context);
            Strobes(astb, bstb);
            BitControl(0);
            BitControl(1);
        }

        _lastAstb = astb;
        _lastBstb = bstb;

        EvaluateBus(context);
        DrivePorts(context, astb);
    }

    /// <summary>/M1 low with neither /RD nor /IORQ for three CLK rises: the PIO's reset.</summary>
    private void ResetByM1(IDigitalContext context)
    {
        var clock = context.ReadInput(Clock, Levels);
        var rose = clock.IsHigh() && _lastClock.IsLow();
        _lastClock = clock;
        if (!rose) return;

        var lone = context.ReadInput(M1, Levels).IsLow() && !context.ReadInput(ReadStrobe, Levels).IsLow() &&
                   !context.ReadInput(IoRequest, Levels).IsLow();
        _m1Clocks = lone ? _m1Clocks + 1 : 0;
        if (_m1Clocks == 3) ResetPorts();
    }

    private void ResetPorts()
    {
        _ports[0].Reset();
        _ports[1].Reset();
        ResetChain();
        NotifyValueChanged();
    }

    /// <summary>The strobes' edges: input latched while /STB is low, RDY dropped and an interrupt as it rises.</summary>
    private void Strobes(bool astb, bool bstb)
    {
        var a = _ports[0];
        var b = _ports[1];

        if (a.Mode == 1 && !astb) a.Input = _pinsSeen[0];
        if (a.Mode == 2 && !bstb) a.Input = _pinsSeen[0];
        if (b.Mode == 1 && !bstb) b.Input = _pinsSeen[1];

        if (astb && !_lastAstb && a.Mode is 0 or 1 or 2) Handshake(a);

        if (bstb && !_lastBstb)
        {
            if (a.Mode == 2)
            {
                // Port A's input half: BRDY falls, through port A's interrupt.
                _inputReadyA = false;
                if (a.InterruptEnabled) a.Pending = true;
                NotifyValueChanged();
            }
            else if (b.Mode is 0 or 1)
            {
                Handshake(b);
            }
        }
    }

    /// <summary>In mode 2, BRDY is port A's input ready, kept apart from its output ready on ARDY.</summary>
    private bool _inputReadyA;

    private static void Handshake(PortState port)
    {
        port.Ready = false;
        if (port.InterruptEnabled) port.Pending = true;
    }

    /// <summary>Mode 3: the port interrupts as its monitored inputs' logic becomes true.</summary>
    private void BitControl(int index)
    {
        var port = _ports[index];
        if (port.Mode != 3) return;

        var condition = Condition(port, _pinsSeen[index]);
        if (condition && !port.Condition && port.InterruptEnabled) port.Pending = true;
        port.Condition = condition;
    }

    private static bool Condition(PortState port, byte pins)
    {
        var monitored = (byte)(port.Direction & ~port.Mask);
        if (monitored == 0) return false;
        var active = (byte)((port.ActiveHigh ? pins : ~pins) & monitored);
        return port.And ? active == monitored : active != 0;
    }

    private byte ReadPort(IDigitalContext context, Terminal[] port)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(port[i], Levels).IsHigh()) value |= 1 << i;
        return (byte)value;
    }

    private void DrivePorts(IDigitalContext context, bool astb)
    {
        var delay = DelayFor(context);
        for (var p = 0; p < 2; p++)
        {
            var port = _ports[p];
            var driven = port.Mode switch
            {
                0 => 0xFF,
                2 => astb ? 0 : 0xFF,
                3 => ~port.Direction & 0xFF,
                _ => 0,
            };

            var first = p == 0 ? PortAIndex : PortBIndex;
            for (var i = 0; i < 8; i++)
            {
                var state = (driven & (1 << i)) == 0
                    ? LogicState.HighImpedance
                    : (port.Output & (1 << i)) != 0 ? LogicState.High : LogicState.Low;
                Request(context, first + i, state, delay);
            }
        }

        var a = _ports[0];
        var b = _ports[1];
        var ardy = a.Mode != 3 && a.Ready;
        var brdy = a.Mode == 2 ? _inputReadyA : b.Mode != 3 && b.Ready;
        Request(context, ArdyIndex, ardy ? LogicState.High : LogicState.Low, delay);
        Request(context, BrdyIndex, brdy ? LogicState.High : LogicState.Low, delay);
    }

    // ---- the registers --------------------------------------------------------------

    private protected override int SelectedRegister(IDigitalContext context) =>
        (context.ReadInput(PortSelect, Levels).IsHigh() ? 1 : 0) | (context.ReadInput(ControlData, Levels).IsHigh() ? 2 : 0);

    private protected override byte ReadRegister(int register)
    {
        if ((register & 2) != 0) return 0xFF;
        var port = _ports[register & 1];
        return port.Mode switch
        {
            0 => port.Output,
            3 => (byte)((_pinsSeen[register & 1] & port.Direction) | (port.Output & ~port.Direction)),
            _ => port.Input,
        };
    }

    /// <summary>The port lines as last read, for a mode 3 read.</summary>
    private readonly byte[] _pinsSeen = new byte[2];

    private protected override void AfterRead(int register)
    {
        if ((register & 2) != 0) return;
        var port = _ports[register & 1];
        if (port.Mode == 1) port.Ready = true;
        if (port.Mode == 2) _inputReadyA = true;
    }

    private protected override void WriteRegister(int register, byte value)
    {
        var port = _ports[register & 1];
        if ((register & 2) == 0)
        {
            port.Output = value;
            if (port.Mode is 0 or 2) port.Ready = true;
            return;
        }

        if (port.ExpectDirection)
        {
            port.ExpectDirection = false;
            port.Direction = value;
            port.Condition = false;
            return;
        }

        if (port.ExpectMask)
        {
            port.ExpectMask = false;
            port.Mask = value;
            port.Condition = false;
            return;
        }

        if ((value & 0x01) == 0)
        {
            port.Vector = value;
        }
        else if ((value & 0x0F) == 0x0F)
        {
            port.Mode = value >> 6;
            port.Ready = false;
            port.ExpectDirection = port.Mode == 3;
            if (register == 2 && port.Mode == 2) _inputReadyA = false;
        }
        else if ((value & 0x0F) == 0x07)
        {
            port.InterruptEnabled = (value & 0x80) != 0;
            port.And = (value & 0x40) != 0;
            port.ActiveHigh = (value & 0x20) != 0;
            port.ExpectMask = (value & 0x10) != 0;
            if (!port.InterruptEnabled) port.Pending = false;
        }
        else if ((value & 0x0F) == 0x03)
        {
            port.InterruptEnabled = (value & 0x80) != 0;
        }
    }

    // ---- the chain ------------------------------------------------------------------

    private protected override bool Pending(int source) => _ports[source].Pending && _ports[source].InterruptEnabled;

    private protected override byte VectorFor(int source) => _ports[source].Vector;

    private protected override void Acknowledged(int source) => _ports[source].Pending = false;

    public override void ResetLogic()
    {
        base.ResetLogic();
        _ports[0].Reset();
        _ports[1].Reset();
        _ports[0].Output = _ports[1].Output = 0;
        _ports[0].Input = _ports[1].Input = 0;
        _ports[0].Vector = _ports[1].Vector = 0;
        _inputReadyA = false;
        _lastAstb = _lastBstb = true;
        _lastClock = LogicState.Unknown;
        _m1Clocks = 0;
    }

    private sealed record RunState(PortState A, PortState B, bool InputReadyA, ChainState Chain);

    public object CaptureRunState() => new RunState(_ports[0].Copy(), _ports[1].Copy(), _inputReadyA, CaptureChain());

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;
        _ports[0] = run.A.Copy();
        _ports[1] = run.B.Copy();
        _inputReadyA = run.InputReadyA;
        AdoptChain(run.Chain);
        NotifyValueChanged();
    }
}
