using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>What a <see cref="Ppi8255"/> is set to and holding, for readouts and tests.</summary>
/// <param name="Control">The last mode word written (bit 7 set).</param>
/// <param name="ModeA">Group A's mode: 0, 1 or 2.</param>
/// <param name="ModeB">Group B's mode: 0 or 1.</param>
/// <param name="AInput">Port A is an input (in modes 0 and 1).</param>
/// <param name="BInput">Port B is an input.</param>
/// <param name="CUpperInput">PC4-PC7, where they are plain I/O, are inputs.</param>
/// <param name="CLowerInput">PC0-PC3, where they are plain I/O, are inputs.</param>
/// <param name="OutputA">Port A's output latch.</param>
/// <param name="OutputB">Port B's output latch.</param>
/// <param name="OutputC">Port C's output latch.</param>
/// <param name="InputA">Port A's input latch (strobed modes).</param>
/// <param name="InputB">Port B's input latch (strobed modes).</param>
/// <param name="PortC">What a read of port C gives now: pins, latch and the status bits.</param>
/// <param name="IbfA">Port A's input buffer full.</param>
/// <param name="ObfA">Port A's output buffer full (/OBF A low).</param>
/// <param name="IntrA">INTR A (PC3) is high.</param>
/// <param name="IbfB">Port B's input buffer full.</param>
/// <param name="ObfB">Port B's output buffer full (/OBF B low).</param>
/// <param name="IntrB">INTR B (PC0) is high.</param>
/// <param name="InteA">INTE A in mode 1 (INTE 1, the output's, in mode 2).</param>
/// <param name="Inte2">INTE 2, port A's input interrupt enable in mode 2.</param>
/// <param name="InteB">INTE B.</param>
public readonly record struct Ppi8255State(byte Control, int ModeA, int ModeB, bool AInput, bool BInput, bool CUpperInput, bool CLowerInput,
    byte OutputA, byte OutputB, byte OutputC, byte InputA, byte InputB, byte PortC,
    bool IbfA, bool ObfA, bool IntrA, bool IbfB, bool ObfB, bool IntrB, bool InteA, bool Inte2, bool InteB);

/// <summary>
/// The Intel 8255A programmable peripheral interface: three 8-bit ports for the 8080 and 8085 —
/// plain I/O in mode 0, strobed with a handshake on port C in mode 1, and port A bidirectional in
/// mode 2.
/// <para>
/// <b>The bus.</b> A1 and A0 pick port A (00), port B (01), port C (10) or the control register
/// (11); see <see cref="IntelPeripheral"/> for the cycle. The control register cannot be read on
/// the NMOS part; here a read of it gives the mode word last written, as the CMOS 82C55A does.
/// </para>
/// <para>
/// <b>The control word.</b> With bit 7 set it is a mode word: bits 6-5 group A's mode (00 mode 0,
/// 01 mode 1, 1x mode 2), bit 4 port A input, bit 3 PC4-PC7 input, bit 2 group B's mode, bit 1 port
/// B input, bit 0 PC0-PC3 input. A mode word clears every output latch and the handshake and
/// interrupt flip-flops. With bit 7 clear it sets or resets one bit of port C: bits 3-1 the bit,
/// bit 0 set (1) or reset (0). On a bit that is a strobe or acknowledge input in mode 1 or 2 it
/// sets that port's interrupt enable instead: INTE A is PC4 for an input and PC6 for an output,
/// INTE B is PC2, and in mode 2 INTE 1 (output) is PC6 and INTE 2 (input) PC4.
/// </para>
/// <para>
/// <b>Mode 0</b>: each port, and each half of port C, an input or an output. An input reads its
/// pins as they stand; an output drives its latch and reads it back.
/// <b>Mode 1</b> on port A or B: as an input, /STB low (PC4 for A, PC2 for B) loads the pins into
/// the input latch and raises IBF (PC5, PC1); /STB going high raises INTR (PC3, PC0) if the INTE is
/// set; a read of the port drops INTR as /RD falls and IBF as it rises. As an output, a write drops
/// INTR as /WR falls and takes /OBF (PC7, PC1) low as it rises; /ACK low (PC6, PC2) raises /OBF,
/// and /ACK going high raises INTR if the INTE is set. INTR also rises when the INTE is set while
/// the port is already in that state (an output with /OBF high, an input with IBF high).
/// <b>Mode 2</b> on port A: both at once — /STB (PC4) loads the input latch and raises IBF (PC5),
/// a write takes /OBF (PC7) low, and the outputs are driven only while /ACK (PC6) is low; INTR A
/// (PC3) is the input's request (with INTE 2) or the output's (with INTE 1). The port C bits a mode
/// does not use stay plain I/O, by the port C direction bits. A read of port C gives, in place of
/// the strobe and acknowledge pins, their INTE flip-flops — the status word.
/// </para>
/// <para>
/// <b>RESET</b> high puts every port in mode 0 as an input and clears the latches.
/// </para>
/// <para>
/// Simplifications: the handshake flip-flops change as the strobes cross their thresholds, with
/// none of the datasheet's minimum pulse widths checked; port B has no Darlington drive and the
/// ports none of the NMOS part's weak pull-ups; a mode word written while a strobed transfer is
/// half done simply clears it.
/// </para>
/// <para>
/// Pinout (DIP-40): 1 = PA3, 2 = PA2, 3 = PA1, 4 = PA0, 5 = /RD, 6 = /CS, 7 = GND, 8 = A1, 9 = A0,
/// 10 = PC7, 11 = PC6, 12 = PC5, 13 = PC4, 14 = PC0, 15 = PC1, 16 = PC2, 17 = PC3, 18-25 = PB0-PB7,
/// 26 = +5V, 27 = D7, 28 = D6, 29 = D5, 30 = D4, 31 = D3, 32 = D2, 33 = D1, 34 = D0, 35 = RESET,
/// 36 = /WR, 37 = PA7, 38 = PA6, 39 = PA5, 40 = PA4.
/// </para>
/// </summary>
public sealed class Ppi8255 : IntelPeripheral, IRunState
{
    // Outputs: D0-D7 = 0-7, PA0-PA7 = 8-15, PB0-PB7 = 16-23, PC0-PC7 = 24-31.
    private const int PortAIndex = 8, PortBIndex = 16, PortCIndex = 24;

    private const byte ResetControl = 0x9B;

    private static readonly int[] DataPins = [34, 33, 32, 31, 30, 29, 28, 27];
    private static readonly int[] PortAPins = [4, 3, 2, 1, 40, 39, 38, 37];
    private static readonly int[] PortCPins = [14, 15, 16, 17, 13, 12, 11, 10];

    private readonly Terminal[] _a = new Terminal[8];
    private readonly Terminal[] _b = new Terminal[8];
    private readonly Terminal[] _c = new Terminal[8];
    private readonly Port[] _ports = [new(), new()];

    private byte _control = ResetControl;
    private byte _outputC;
    private bool _inte2, _inte4, _inte6;
    private bool _lastReset;
    private byte _pinsA, _pinsB, _pinsC;

    /// <summary>A strobed port's latches and flip-flops.</summary>
    private sealed class Port
    {
        public byte Output;
        public byte Input;
        public bool Ibf;
        public bool Obf;
        public bool IntrIn;
        public bool IntrOut;
        public bool ConditionIn;
        public bool ConditionOut;

        public void Clear()
        {
            Output = Input = 0;
            Ibf = Obf = IntrIn = IntrOut = ConditionIn = ConditionOut = false;
        }

        public Port Copy() => (Port)MemberwiseClone();
    }

    public Ppi8255() : base(40)
    {
        PropagationDelay = 50e-9;

        var pins = new Terminal[41];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 40));

        for (var i = 0; i < 8; i++) SetDataPin(i, Pin(DataPins[i], $"D{i}", TerminalType.Bidirectional));
        for (var i = 0; i < 8; i++) _a[i] = Pin(PortAPins[i], $"PA{i}", TerminalType.Bidirectional);
        for (var i = 0; i < 8; i++) _b[i] = Pin(18 + i, $"PB{i}", TerminalType.Bidirectional);
        for (var i = 0; i < 8; i++) _c[i] = Pin(PortCPins[i], $"PC{i}", TerminalType.Bidirectional);
        ReadStrobe = Pin(5, "RD", TerminalType.Input);
        ChipSelect = Pin(6, "CS", TerminalType.Input);
        Gnd = Pin(7, "GND", TerminalType.Ground);
        A1 = Pin(8, "A1", TerminalType.Input);
        A0 = Pin(9, "A0", TerminalType.Input);
        Vcc = Pin(26, "VCC", TerminalType.Power);
        Reset = Pin(35, "RESET", TerminalType.Input);
        WriteStrobe = Pin(36, "WR", TerminalType.Input);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([ChipSelect, ReadStrobe, WriteStrobe, A0, A1, Reset, .. Data, .. _a, .. _b, .. _c],
            [.. Data, .. _a, .. _b, .. _c]);
    }

    public override string PartNumber => "8255A";

    public Terminal A0 { get; }

    public Terminal A1 { get; }

    /// <summary>RESET: high puts every port in mode 0 as an input.</summary>
    public Terminal Reset { get; }

    public IReadOnlyList<Terminal> PortA => _a;

    public IReadOnlyList<Terminal> PortB => _b;

    /// <summary>Port C: plain I/O, or the handshake lines of the strobed modes.</summary>
    public IReadOnlyList<Terminal> PortC => _c;

    private int ModeA => (_control & 0x40) != 0 ? 2 : (_control >> 5) & 1;

    private int ModeB => (_control >> 2) & 1;

    private bool AInput => (_control & 0x10) != 0;

    private bool BInput => (_control & 0x02) != 0;

    private bool UpperInput => (_control & 0x08) != 0;

    private bool LowerInput => (_control & 0x01) != 0;

    /// <summary>What each bit of port C is in the present modes.</summary>
    private enum Role
    {
        In,
        Out,
        HandshakeIn,
        HandshakeOut,
    }

    private Role RoleOf(int bit)
    {
        var plain = bit >= 4 ? UpperInput ? Role.In : Role.Out : LowerInput ? Role.In : Role.Out;
        if (ModeB == 1 && bit <= 2) return bit == 2 ? Role.HandshakeIn : Role.HandshakeOut;
        switch (ModeA)
        {
            case 2:
                if (bit >= 3) return bit is 4 or 6 ? Role.HandshakeIn : Role.HandshakeOut;
                break;
            case 1:
                if (bit == 3) return Role.HandshakeOut;
                if (AInput && bit is 4 or 5) return bit == 4 ? Role.HandshakeIn : Role.HandshakeOut;
                if (!AInput && bit is 6 or 7) return bit == 6 ? Role.HandshakeIn : Role.HandshakeOut;
                break;
        }

        return plain;
    }

    /// <summary>The state, for readouts and tests.</summary>
    public Ppi8255State Describe()
    {
        var a = _ports[0];
        var b = _ports[1];
        return new Ppi8255State(_control, ModeA, ModeB, AInput, BInput, UpperInput, LowerInput, a.Output, b.Output, _outputC, a.Input, b.Input,
            ReadPortC(), a.Ibf, a.Obf, IntrA, b.Ibf, b.Obf, IntrB, ModeA == 1 && AInput ? _inte4 : _inte6, _inte4, _inte2);
    }

    public override string ValueLabel =>
        string.Create(CultureInfo.InvariantCulture, $"A{ModeA} {(ModeA == 2 ? "bi" : AInput ? "in" : "out")} B{ModeB} {(BInput ? "in" : "out")}");

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "CS" or "RD" or "WR" => "/" + pin.Name,
        "VCC" => "+5V",
        _ => pin.Name,
    };

    // ---- the handshakes ---------------------------------------------------------------------

    private bool IntrA => ModeA >= 1 && (_ports[0].IntrIn || _ports[0].IntrOut);

    private bool IntrB => ModeB == 1 && (_ports[1].IntrIn || _ports[1].IntrOut);

    private bool StrobedInput(int port) => port == 0 ? ModeA == 2 || (ModeA == 1 && AInput) : ModeB == 1 && BInput;

    private bool StrobedOutput(int port) => port == 0 ? ModeA == 2 || (ModeA == 1 && !AInput) : ModeB == 1 && !BInput;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var reset = context.ReadInput(Reset, Levels).IsHigh();
        _pinsA = ReadPins(context, _a);
        _pinsB = ReadPins(context, _b);
        _pinsC = ReadPins(context, _c);

        if (!context.IsInitializing && reset && !_lastReset)
        {
            _control = ResetControl;
            ClearAll();
            ResetBus();
            NotifyValueChanged();
        }

        _lastReset = reset;

        if (reset)
        {
            DrivePorts(context);
            return;
        }

        EvaluateBus(context);

        if (!context.IsInitializing)
        {
            // A write to a strobed output drops its INTR as /WR falls.
            if (CurrentAccess is 0 or 1 && !CurrentAccessReads && StrobedOutput(CurrentAccess)) _ports[CurrentAccess].IntrOut = false;

            for (var p = 0; p < 2; p++) Handshake(p);
        }

        DrivePorts(context);
    }

    /// <summary>/STB and /ACK, and the INTR each port raises when its condition comes true.</summary>
    private void Handshake(int index)
    {
        var port = _ports[index];
        var strobeBit = index == 0 ? 4 : 2;
        var ackBit = index == 0 ? 6 : 2;
        var pins = index == 0 ? _pinsA : _pinsB;

        if (StrobedInput(index))
        {
            var strobe = (_pinsC & (1 << strobeBit)) != 0;
            if (!strobe)
            {
                port.Input = pins;
                if (!port.Ibf) NotifyValueChanged();
                port.Ibf = true;
            }

            var inte = index == 0 ? _inte4 : _inte2;
            var condition = strobe && port.Ibf && inte;
            if (condition && !port.ConditionIn) port.IntrIn = true;
            port.ConditionIn = condition;
        }

        if (StrobedOutput(index))
        {
            var ack = (_pinsC & (1 << ackBit)) != 0;
            if (!ack && port.Obf)
            {
                port.Obf = false;
                NotifyValueChanged();
            }

            var inte = index == 0 ? _inte6 : _inte2;
            var condition = ack && !port.Obf && inte;
            if (condition && !port.ConditionOut) port.IntrOut = true;
            port.ConditionOut = condition;
        }
    }

    private void DrivePorts(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var reset = context.ReadInput(Reset, Levels).IsHigh();

        LogicState Bit(byte value, int bit) => ((value >> bit) & 1) != 0 ? LogicState.High : LogicState.Low;

        // Port A: an output in modes 0 and 1, in mode 2 only while /ACK is low.
        var driveA = !reset && (ModeA == 2 ? (_pinsC & 0x40) == 0 : !AInput);
        var driveB = !reset && !BInput;
        for (var i = 0; i < 8; i++)
        {
            Request(context, PortAIndex + i, driveA ? Bit(_ports[0].Output, i) : LogicState.HighImpedance, delay);
            Request(context, PortBIndex + i, driveB ? Bit(_ports[1].Output, i) : LogicState.HighImpedance, delay);
        }

        var status = StatusOutputs();
        for (var i = 0; i < 8; i++)
        {
            var level = reset
                ? LogicState.HighImpedance
                : RoleOf(i) switch
                {
                    Role.Out => Bit(_outputC, i),
                    Role.HandshakeOut => Bit(status, i),
                    _ => LogicState.HighImpedance,
                };
            Request(context, PortCIndex + i, level, delay);
        }
    }

    /// <summary>The handshake outputs as port C bits: INTR A on PC3, IBF A on PC5, /OBF A on PC7, INTR B on PC0, IBF B or /OBF B on PC1.</summary>
    private byte StatusOutputs()
    {
        var a = _ports[0];
        var b = _ports[1];
        var value = 0;
        if (IntrA) value |= 0x08;
        if (a.Ibf) value |= 0x20;
        if (!a.Obf) value |= 0x80;
        if (IntrB) value |= 0x01;
        if (BInput ? b.Ibf : !b.Obf) value |= 0x02;
        return (byte)value;
    }

    private byte ReadPins(IDigitalContext context, Terminal[] pins)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(pins[i], Levels).IsHigh()) value |= 1 << i;
        return (byte)value;
    }

    // ---- the registers ----------------------------------------------------------------------

    private protected override int SelectedRegister(IDigitalContext context) =>
        (context.ReadInput(A0, Levels).IsHigh() ? 1 : 0) | (context.ReadInput(A1, Levels).IsHigh() ? 2 : 0);

    private protected override byte ReadRegister(int register) => register switch
    {
        0 => ModeA == 0 ? AInput ? _pinsA : _ports[0].Output : ModeA == 2 || AInput ? _ports[0].Input : _ports[0].Output,
        1 => ModeB == 0 ? BInput ? _pinsB : _ports[1].Output : BInput ? _ports[1].Input : _ports[1].Output,
        2 => ReadPortC(),
        _ => _control,
    };

    /// <summary>Port C as a read gives it: plain inputs from the pins, plain outputs from the latch, the handshake outputs as they are, and the INTEs in place of the strobe and acknowledge pins.</summary>
    private byte ReadPortC()
    {
        var status = StatusOutputs();
        var value = 0;
        for (var i = 0; i < 8; i++)
        {
            var bit = RoleOf(i) switch
            {
                Role.In => (_pinsC >> i) & 1,
                Role.Out => (_outputC >> i) & 1,
                Role.HandshakeOut => (status >> i) & 1,
                _ => i switch { 2 => _inte2 ? 1 : 0, 4 => _inte4 ? 1 : 0, _ => _inte6 ? 1 : 0 },
            };
            value |= bit << i;
        }

        return (byte)value;
    }

    private protected override void ReadStarted(int register)
    {
        // A read of a strobed input drops its INTR as /RD falls.
        if (register is 0 or 1 && StrobedInput(register)) _ports[register].IntrIn = false;
    }

    private protected override void AfterRead(int register)
    {
        // ... and IBF as it rises.
        if (register is 0 or 1 && StrobedInput(register)) _ports[register].Ibf = false;
    }

    private protected override void WriteRegister(int register, byte value)
    {
        switch (register)
        {
            case 0:
            case 1:
            {
                var port = _ports[register];
                port.Output = value;
                if (StrobedOutput(register)) port.Obf = true;
                break;
            }

            case 2:
            {
                // Only the bits that are plain outputs (or plain inputs, latched) take a write.
                for (var i = 0; i < 8; i++)
                    if (RoleOf(i) is Role.In or Role.Out) _outputC = (byte)((_outputC & ~(1 << i)) | (value & (1 << i)));
                break;
            }

            default:
                if ((value & 0x80) != 0)
                {
                    _control = value;
                    ClearAll();
                }
                else
                {
                    SetBit((value >> 1) & 7, (value & 1) != 0);
                }

                break;
        }
    }

    /// <summary>The bit set/reset word: a plain bit's latch, or the INTE behind a strobe or acknowledge pin.</summary>
    private void SetBit(int bit, bool set)
    {
        switch (RoleOf(bit))
        {
            case Role.HandshakeIn:
                if (bit == 2) _inte2 = set;
                else if (bit == 4) _inte4 = set;
                else _inte6 = set;
                // An INTE set on a port already waiting raises its INTR at once.
                for (var p = 0; p < 2; p++) Handshake(p);
                break;

            case Role.HandshakeOut:
                break;

            default:
                _outputC = (byte)(set ? _outputC | (1 << bit) : _outputC & ~(1 << bit));
                break;
        }
    }

    private void ClearAll()
    {
        foreach (var port in _ports) port.Clear();
        _outputC = 0;
        _inte2 = _inte4 = _inte6 = false;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _control = ResetControl;
        ClearAll();
        _lastReset = false;
        _pinsA = _pinsB = _pinsC = 0;
    }

    private sealed record RunState(byte Control, byte OutputC, bool Inte2, bool Inte4, bool Inte6, Port A, Port B);

    public object CaptureRunState() => new RunState(_control, _outputC, _inte2, _inte4, _inte6, _ports[0].Copy(), _ports[1].Copy());

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;
        _control = run.Control;
        _outputC = run.OutputC;
        _inte2 = run.Inte2;
        _inte4 = run.Inte4;
        _inte6 = run.Inte6;
        _ports[0] = run.A.Copy();
        _ports[1] = run.B.Copy();
        NotifyValueChanged();
    }
}
