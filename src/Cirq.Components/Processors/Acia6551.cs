using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Processors;

/// <summary>
/// The 6551 Asynchronous Communications Interface Adapter: a UART for a 6502's bus, with its own
/// baud-rate generator running from a 1.8432 MHz crystal.
/// <para>
/// <b>Registers</b>, by RS1 and RS0: 0 the data register (writing sends a byte, reading takes the
/// last received one), 1 the status register (writing it is a programmed reset), 2 the command
/// register and 3 the control register.
/// </para>
/// <list type="bullet">
/// <item><b>Control</b>: bits 0-3 pick the baud rate — 50 to 19,200 from the crystal, 1111 being
/// 19,200 — bits 5-6 the word length (8, 7, 6 or 5 bits) and bit 7 two stop bits rather than one.</item>
/// <item><b>Command</b>: bit 0 (DTR) enables the receiver, bit 1 set disables its interrupt, bits
/// 2-3 set RTSB and the transmit interrupt (01 enables it), bit 4 echoes received bytes straight
/// back out, bits 5-7 the parity (xx0 none, 001 odd, 011 even, 101 mark, 111 space).</item>
/// <item><b>Status</b>: bit 0 parity error, 1 framing error, 2 overrun, 3 receive data full, 4
/// transmit data empty, 5 and 6 the DCDB and DSRB pins, 7 an interrupt has occurred (cleared by
/// reading the status).</item>
/// </list>
/// <para>
/// The transmitter sends while CTSB is low; the receiver samples the middle of each bit on its own
/// clock, as a UART does, so a mismatched rate gives definite wrong bytes and framing errors. IRQB is
/// open-drain and needs a pull-up. Connect TxD to a serial terminal's RX and the terminal's TX to
/// RxD, both ends at the same rate.
/// </para>
/// <para>
/// Simplifications: the crystal is not simulated — <see cref="CrystalFrequency"/> stands in for it,
/// and XTLI and XTLO are there to be drawn — and baud select 0000 (a 16× clock on RxC) is taken as
/// the crystal itself, so it runs at the crystal ÷ 16 (115,200 baud at 1.8432 MHz). Break, and the
/// W65C51N's well-known transmit-empty bug, are not modelled: this is the R6551's behaviour.
/// </para>
/// <para>
/// Pinout (DIP-28): 1=VSS, 2=CS0, 3=CS1B, 4=RESB, 5=RxC, 6=XTLI, 7=XTLO, 8=RTSB, 9=CTSB, 10=TxD,
/// 11=DTRB, 12=RxD, 13=RS0, 14=RS1, 15=VDD, 16=DCDB, 17=DSRB, 18-25=D0-D7, 26=IRQB, 27=PHI2, 28=R/WB.
/// </para>
/// </summary>
public sealed partial class Acia6551 : DigitalIc, IBreakpointSource
{
    private readonly Terminal[] _data = new Terminal[8];
    private readonly Terminal[] _rs = new Terminal[2];

    // Outputs: D0-D7 = 0-7, TxD = 8, RTSB = 9, DTRB = 10, IRQB = 11.
    private const int TxIndex = 8, RtsIndex = 9, DtrIndex = 10, IrqIndex = 11;

    /// <summary>The crystal divided by these gives each baud select's rate: 1.8432 MHz ÷ 96 is 19,200.</summary>
    private static readonly double[] Divisors =
    [
        16, 36864, 24576, 16769, 13696, 12288, 6144, 3072, 1536, 1024, 768, 512, 384, 256, 192, 96,
    ];

    private byte _control, _command, _receive, _transmit;
    private bool _transmitFull, _receiveFull, _parityError, _framingError, _overrun, _interrupt;

    // Transmitter.
    private bool[] _frame = [];
    private double _frameStart = double.NaN;

    // Receiver.
    private bool _lastRx = true;
    private double _sampleAt = double.NaN;
    private int _sampleIndex;
    private int _shift;
    private bool _parityBad;

    private LogicState _lastPhi2 = LogicState.Unknown;

    public Acia6551() : base(28)
    {
        PropagationDelay = 40e-9;

        var pins = new Terminal[29];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 28));

        Gnd = Pin(1, "VSS", TerminalType.Ground);
        ChipSelect0 = Pin(2, "CS0", TerminalType.Input);
        ChipSelect1 = Pin(3, "CS1B", TerminalType.Input);
        Reset = Pin(4, "RESB", TerminalType.Input);
        Pin(5, "RxC", TerminalType.Passive);
        Pin(6, "XTLI", TerminalType.Passive);
        Pin(7, "XTLO", TerminalType.Passive);
        var rts = Pin(8, "RTSB", TerminalType.Output);
        ClearToSend = Pin(9, "CTSB", TerminalType.Input);
        Transmit = Pin(10, "TxD", TerminalType.Output);
        var dtr = Pin(11, "DTRB", TerminalType.Output);
        Receive = Pin(12, "RxD", TerminalType.Input);
        _rs[0] = Pin(13, "RS0", TerminalType.Input);
        _rs[1] = Pin(14, "RS1", TerminalType.Input);
        Vcc = Pin(15, "VDD", TerminalType.Power);
        CarrierDetect = Pin(16, "DCDB", TerminalType.Input);
        DataSetReady = Pin(17, "DSRB", TerminalType.Input);
        for (var i = 0; i < 8; i++) _data[i] = Pin(18 + i, $"D{i}", TerminalType.Bidirectional);
        Irq = Pin(26, "IRQB", TerminalType.Bidirectional);
        Clock = Pin(27, "PHI2", TerminalType.Input);
        ReadWrite = Pin(28, "RWB", TerminalType.Input);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [Clock, ReadWrite, ChipSelect0, ChipSelect1, Reset, .. _rs, ClearToSend, Receive, CarrierDetect, DataSetReady, .. _data],
            [.. _data, Transmit, rts, dtr, Irq]);
    }

    public override string PartNumber => "6551";

    public override string ComponentType => "6551 ACIA";

    public override bool LabelsPins => true;

    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>RS0 and RS1.</summary>
    public IReadOnlyList<Terminal> RegisterSelect => _rs;

    /// <summary>CS0, active high.</summary>
    public Terminal ChipSelect0 { get; }

    /// <summary>CS1B, active low.</summary>
    public Terminal ChipSelect1 { get; }

    public Terminal Reset { get; }

    public Terminal Clock { get; }

    public Terminal ReadWrite { get; }

    /// <summary>TxD: serial data out.</summary>
    public Terminal Transmit { get; }

    /// <summary>RxD: serial data in.</summary>
    public Terminal Receive { get; }

    /// <summary>CTSB: low lets the transmitter send. Tie it low if nothing drives it.</summary>
    public Terminal ClearToSend { get; }

    public Terminal CarrierDetect { get; }

    public Terminal DataSetReady { get; }

    /// <summary>IRQB: open-drain, low while an interrupt is pending.</summary>
    public Terminal Irq { get; }

    /// <summary>The crystal on XTLI and XTLO, in hertz. 1.8432 MHz gives the standard rates exactly.</summary>
    [ObservableProperty]
    public partial double CrystalFrequency { get; set; } = 1.8432e6;

    /// <summary>The rate the control register has chosen, in baud.</summary>
    public double BaudRate => CrystalFrequency / Divisors[_control & 0x0F];

    /// <summary>One bit at that rate, in seconds.</summary>
    private double BitTime => 1.0 / BaudRate;

    public override string ValueLabel => $"{BaudRate:0} baud{(TransmittedBytes.Count > 0 ? $", sent {TransmittedBytes.Count}" : string.Empty)}";

    /// <summary>Every byte it has sent, in order.</summary>
    public List<byte> TransmittedBytes { get; } = [];

    /// <summary>Every byte it has received, in order.</summary>
    public List<byte> ReceivedBytes { get; } = [];

    public byte Status => (byte)(
        (_parityError ? 0x01 : 0) | (_framingError ? 0x02 : 0) | (_overrun ? 0x04 : 0) | (_receiveFull ? 0x08 : 0) |
        (!_transmitFull ? 0x10 : 0) | (_dcd ? 0x20 : 0) | (_dsr ? 0x40 : 0) | (_interrupt ? 0x80 : 0));

    public byte Command => _command;

    public byte Control => _control;

    private bool _dcd, _dsr;

    private int WordLength => 8 - ((_control >> 5) & 3);

    private int StopBits => (_control & 0x80) != 0 ? 2 : 1;

    private bool ParityOn => (_command & 0x20) != 0;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var phi2 = context.ReadInput(Clock, Levels);
        var previous = _lastPhi2;
        _lastPhi2 = phi2;
        var now = context.Time;

        _dcd = context.ReadInput(CarrierDetect, Levels).IsHigh();
        _dsr = context.ReadInput(DataSetReady, Levels).IsHigh();

        if (context.ReadInput(Reset, Levels).IsLow())
        {
            HardwareReset();
            Drive(context, false, 0);
            return;
        }

        var selected = context.ReadInput(ChipSelect0, Levels).IsHigh() && context.ReadInput(ChipSelect1, Levels).IsLow();
        var reading = context.ReadInput(ReadWrite, Levels).IsHigh();
        var register = (context.ReadInput(_rs[0], Levels).IsHigh() ? 1 : 0) | (context.ReadInput(_rs[1], Levels).IsHigh() ? 2 : 0);

        if (!context.IsInitializing)
        {
            if (previous.IsHigh() && phi2.IsLow() && selected)
            {
                if (reading) AfterRead(register);
                else Write(register, ReadBus(context));
            }

            ReceiveStep(context, now);
            TransmitStep(context, now);
        }

        var driveData = selected && reading && phi2.IsHigh();
        Drive(context, driveData, driveData ? Read(register) : (byte)0);
    }

    private byte ReadBus(IDigitalContext context)
    {
        var value = 0;
        for (var i = 0; i < 8; i++)
            if (context.ReadInput(_data[i], Levels).IsHigh()) value |= 1 << i;

        return (byte)value;
    }

    private byte Read(int register) => register switch
    {
        0 => _receive,
        1 => Status,
        2 => _command,
        _ => _control,
    };

    private void AfterRead(int register)
    {
        if (register == 0)
        {
            _receiveFull = false;
            _parityError = _framingError = _overrun = false;
        }
        else if (register == 1)
        {
            _interrupt = false;
        }
    }

    private void Write(int register, byte value)
    {
        switch (register)
        {
            case 0:
                _transmit = value;
                _transmitFull = true;
                break;
            case 1:
                // A programmed reset: the command register's low five bits and the overrun flag.
                _command &= 0xE0;
                _overrun = false;
                break;
            case 2:
                _command = value;
                break;
            default:
                _control = value;
                break;
        }

        NotifyValueChanged();
    }

    private void TransmitStep(IDigitalContext context, double now)
    {
        if (_frame.Length > 0)
        {
            var index = (int)Math.Floor(((now - _frameStart) / BitTime) + 1e-6);
            if (index < _frame.Length) return;

            _frame = [];
            _frameStart = double.NaN;
        }

        var cts = context.ReadInput(ClearToSend, Levels).IsLow();
        if (!_transmitFull || !cts) return;

        _frame = Frame(_transmit);
        _frameStart = now;
        _transmitFull = false;
        TransmittedBytes.Add(_transmit);

        // Transmit data empty: an interrupt if the command register asks for one.
        if (((_command >> 2) & 3) == 1) _interrupt = true;
        NotifyValueChanged();
    }

    /// <summary>A frame: start bit, the data least significant bit first, parity if on, the stop bits.</summary>
    private bool[] Frame(byte value)
    {
        List<bool> bits = [false];
        var ones = 0;
        for (var i = 0; i < WordLength; i++)
        {
            var bit = (value & (1 << i)) != 0;
            if (bit) ones++;
            bits.Add(bit);
        }

        if (ParityOn) bits.Add(ParityBit(ones));
        for (var i = 0; i < StopBits; i++) bits.Add(true);
        return [.. bits];
    }

    /// <summary>The parity bit the command register asks for, given how many data bits are ones.</summary>
    private bool ParityBit(int ones) => ((_command >> 6) & 3) switch
    {
        0 => ones % 2 == 0,     // odd
        1 => ones % 2 != 0,     // even
        2 => true,              // mark
        _ => false,             // space
    };

    private bool TransmitLevel(double now)
    {
        if (_frame.Length == 0) return true;
        var index = (int)Math.Floor(((now - _frameStart) / BitTime) + 1e-6);
        return index < 0 || index >= _frame.Length || _frame[index];
    }

    private void ReceiveStep(IDigitalContext context, double now)
    {
        var rx = context.ReadInput(Receive, Levels) != LogicState.Low;
        var enabled = (_command & 0x01) != 0;

        if (enabled && double.IsNaN(_sampleAt) && _lastRx && !rx)
        {
            _sampleIndex = 0;
            _shift = 0;
            _parityBad = false;
            _sampleAt = now + (BitTime / 2);
        }

        _lastRx = rx;

        while (!double.IsNaN(_sampleAt) && now >= _sampleAt - 1e-12)
        {
            Sample(rx);
            if (!double.IsNaN(_sampleAt)) _sampleAt += BitTime;
        }
    }

    private void Sample(bool high)
    {
        if (_sampleIndex == 0)
        {
            if (high)
            {
                _sampleAt = double.NaN;
                return;
            }

            _sampleIndex++;
            return;
        }

        if (_sampleIndex <= WordLength)
        {
            if (high) _shift |= 1 << (_sampleIndex - 1);
            _sampleIndex++;
            return;
        }

        if (ParityOn && _sampleIndex == WordLength + 1)
        {
            if (high != ParityBit(System.Numerics.BitOperations.PopCount((uint)_shift))) _parityBad = true;
            _sampleIndex++;
            return;
        }

        // The stop bit.
        if (_receiveFull) _overrun = true;
        _receive = (byte)_shift;
        _receiveFull = true;
        _framingError = !high;
        _parityError = _parityBad;
        ReceivedBytes.Add(_receive);

        if ((_command & 0x02) == 0 && (_command & 0x01) != 0) _interrupt = true;

        // Echo mode sends what arrived straight back out.
        if ((_command & 0x10) != 0 && !_transmitFull)
        {
            _transmit = _receive;
            _transmitFull = true;
        }

        _sampleAt = double.NaN;
        _sampleIndex = 0;
        NotifyValueChanged();
    }

    private void Drive(IDigitalContext context, bool driveData, byte value)
    {
        var delay = DelayFor(context);

        for (var i = 0; i < 8; i++)
            Request(context, i, driveData ? ((value >> i) & 1) != 0 ? LogicState.High : LogicState.Low : LogicState.HighImpedance, delay);

        Request(context, TxIndex, TransmitLevel(context.Time) ? LogicState.High : LogicState.Low, delay);
        Request(context, RtsIndex, ((_command >> 2) & 3) == 0 ? LogicState.High : LogicState.Low, delay);
        Request(context, DtrIndex, (_command & 0x01) != 0 ? LogicState.Low : LogicState.High, delay);
        Request(context, IrqIndex, _interrupt ? LogicState.Low : LogicState.HighImpedance, delay);
    }

    /// <summary>The bit boundaries the transmitter changes on and the instants the receiver samples at.</summary>
    public double? NextBreakpointAfter(double time)
    {
        double? next = null;

        if (_frame.Length > 0)
        {
            for (var i = 1; i <= _frame.Length; i++)
            {
                var at = _frameStart + (i * BitTime);
                if (at > time + 1e-12)
                {
                    next = at;
                    break;
                }
            }
        }

        if (!double.IsNaN(_sampleAt) && _sampleAt > time && (next is null || _sampleAt < next)) next = _sampleAt;
        return next;
    }

    private void HardwareReset()
    {
        _control = 0;
        _command = 0x02;
        _transmitFull = _receiveFull = _parityError = _framingError = _overrun = _interrupt = false;
        _frame = [];
        _frameStart = double.NaN;
        _sampleAt = double.NaN;
        _sampleIndex = 0;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        HardwareReset();
        _receive = _transmit = 0;
        _lastRx = true;
        _lastPhi2 = LogicState.Unknown;
        TransmittedBytes.Clear();
        ReceivedBytes.Clear();
    }

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "RWB" => "R/WB",
        _ => pin.Name,
    };

    /// <summary>The bytes sent so far, as text, with anything unprintable shown as a dot.</summary>
    public string TransmittedText => string.Concat(TransmittedBytes.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.'));

    partial void OnCrystalFrequencyChanged(double value) => NotifyValueChanged();
}
