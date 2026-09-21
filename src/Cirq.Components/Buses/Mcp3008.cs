using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// MCP3008: eight analog inputs, ten bits, read over SPI.
/// <para>
/// The I²C side of the palette has six devices on it and SPI had a shift register, which is not a
/// fair picture of the two buses — an ADC is the commonest thing anybody hangs off an SPI port,
/// and on a Raspberry Pi, which has no analog inputs at all, it is very nearly compulsory.
/// </para>
/// <para>
/// The exchange is worth watching because SPI is <b>full duplex</b> and this part uses that
/// properly: the bits going out and the bits coming back overlap. Three bytes go down MOSI — a
/// start bit, then single-ended or differential and three address bits, then padding — and the
/// answer comes back up MISO underneath them, arriving before the master has finished asking. A
/// conversion here is one transaction rather than the write-then-read pair an I²C ADC needs.
/// </para>
/// <para>
/// The reading is <b>ratiometric</b>: the result is the input as a fraction of <c>VREF</c>, not an
/// absolute voltage. Tie VREF to the same rail that feeds the sensor and supply noise cancels out,
/// because both move together. Tie it to a reference instead and the reading is absolute but the
/// sensor's own supply noise is not rejected. That choice is the whole of ADC accuracy in practice
/// and it is made by which wire goes where.
/// </para>
/// <para>
/// Pinout: 1-8 = CH0-CH7, 9 = DGND, 10 = /CS, 11 = DIN, 12 = DOUT, 13 = CLK, 14 = AGND,
/// 15 = VREF, 16 = VDD.
/// </para>
/// </summary>
public sealed partial class Mcp3008 : DigitalIc
{
    private const int Channels = 8;
    private const int DataOut = 0;

    private readonly Terminal[] _inputs = new Terminal[Channels];

    private LogicState _lastClock = LogicState.Low;
    private bool _selected;
    private bool _started;
    private int _bitsSeen;
    private int _command;
    private int _shiftOut;
    private int _outBits;

    public Mcp3008() : base(16)
    {
        PropagationDelay = 120e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        for (var i = 0; i < Channels; i++) _inputs[i] = Pin(1 + i, $"CH{i}", TerminalType.Input);

        Gnd = Pin(9, "DGND", TerminalType.Ground);
        ChipSelect = Pin(10, "CS", TerminalType.Input);
        DataIn = Pin(11, "DIN", TerminalType.Input);
        DataOutPin = Pin(12, "DOUT", TerminalType.Output);
        Clock = Pin(13, "CLK", TerminalType.Input);
        AnalogGround = Pin(14, "AGND", TerminalType.Ground);
        Reference = Pin(15, "VREF", TerminalType.Input);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([ChipSelect, DataIn, Clock], [DataOutPin]);
    }

    /// <summary>The eight analog inputs, CH0 through CH7.</summary>
    public IReadOnlyList<Terminal> Inputs => _inputs;

    /// <summary>Active low, and it also resets the exchange — raising it abandons a conversion.</summary>
    public Terminal ChipSelect { get; }

    /// <summary>From the master: the start bit and the channel it wants.</summary>
    public Terminal DataIn { get; }

    /// <summary>To the master: the ten bits of the answer, most significant first.</summary>
    public Terminal DataOutPin { get; }

    public Terminal Clock { get; }

    /// <summary>The analog ground the inputs are measured against.</summary>
    public Terminal AnalogGround { get; }

    /// <summary>Full scale. The reading is a fraction of this, not an absolute voltage.</summary>
    public Terminal Reference { get; }

    /// <summary>Resolution in bits. The part is ten; the property is here to be read, not set.</summary>
    public int Resolution => 10;

    /// <summary>The highest code it can return.</summary>
    public int FullScale => (1 << Resolution) - 1;

    public override string PartNumber => "MCP3008";

    public override string ComponentType => "SPI ADC";

    public override string ValueLabel => LastChannel < 0
        ? "8 × 10-bit"
        : $"CH{LastChannel} = {LastCode}";

    /// <summary>The channel of the last conversion, or −1 before there has been one.</summary>
    public int LastChannel { get; private set; } = -1;

    /// <summary>The code from the last conversion, 0 to 1023.</summary>
    public int LastCode { get; private set; }

    /// <summary>What that code works out to in volts, against the reference at the time.</summary>
    public double LastVoltage { get; private set; }

    /// <summary>
    /// True while a conversation is in progress. Named for the transfer rather than the chip
    /// select, because <c>IsSelected</c> on a component already means the canvas has it selected.
    /// </summary>
    public bool IsTransferring => _selected;

    /// <summary>
    /// True when an input was above the reference and the answer saturated. It is the commonest
    /// thing to get wrong on an ADC and it does not look like an error — the reading simply stops
    /// following the input.
    /// </summary>
    public bool IsClipping { get; private set; }

    public IReadOnlyList<string> Violations => IsClipping
        ? [$"CH{LastChannel} is above VREF, so the reading has stopped following it — " +
           "either scale the input down or raise the reference"]
        : [];

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var selected = context.ReadInput(ChipSelect, Levels).IsLow();

        if (!selected)
        {
            // Deselecting abandons whatever was in progress, which is how a master recovers from
            // a transfer that went wrong: raise CS and start again.
            _selected = false;
            _started = false;
            _bitsSeen = 0;
            _command = 0;
            _outBits = 0;

            context.Schedule(this, DataOut, LogicState.Low, delay);
            _lastClock = context.ReadInput(Clock, Levels);
            return;
        }

        if (!_selected)
        {
            _selected = true;
            _started = false;
            _bitsSeen = 0;
            _command = 0;
            _outBits = 0;
        }

        var clock = context.ReadInput(Clock, Levels);

        if (_lastClock.IsLow() && clock.IsHigh())
        {
            // Rising edge: the master's bit is valid now.
            var bit = context.ReadInput(DataIn, Levels).IsHigh() ? 1 : 0;

            if (_outBits > 0)
            {
                _outBits--;
            }
            else if (!_started)
            {
                // Leading zeros are ignored until the start bit arrives, which is what lets a
                // master send 0x01 as its first byte and have the part find the 1 at the end of
                // it. Counting five bits from the beginning instead loses alignment for ever.
                _started = bit == 1;
                _command = 0;
                _bitsSeen = 0;
            }
            else
            {
                _command = (_command << 1) | bit;

                // Single-ended or differential, then three address bits: four after the start,
                // and the answer is ready the moment the last of them arrives.
                if (++_bitsSeen == 4)
                {
                    Convert(context, _command & 0x07);

                    // One clock of nothing, then the null bit, then ten of answer, most
                    // significant first. The code is ten bits wide, so the two leading zeros come
                    // for free from the bits above it.
                    //
                    // Twelve rather than eleven because of where the master is looking: this is
                    // what puts the answer where every MCP3008 example expects to find it, which
                    // is `(second & 0x03) << 8 | third` across the last two bytes of the transfer.
                    _shiftOut = LastCode;
                    _outBits = 12;
                    _started = false;
                }
            }
        }

        // The part changes DOUT on the falling edge, so the master sees it settled on the rising
        // one — which is the whole reason SPI has a mode for who moves when.
        if (_lastClock.IsHigh() && clock.IsLow() && _outBits > 0)
        {
            var bit = (_shiftOut & (1 << (_outBits - 1))) != 0;
            context.Schedule(this, DataOut, bit ? LogicState.High : LogicState.Low, delay);
        }

        _lastClock = clock;
    }

    private void Convert(IDigitalContext context, int channel)
    {
        var ground = context.NodeVoltage(AnalogGround);
        var reference = context.NodeVoltage(Reference) - ground;
        var input = context.NodeVoltage(_inputs[channel]) - ground;

        LastChannel = channel;

        if (reference <= 1e-9)
        {
            LastCode = 0;
            LastVoltage = 0;
            IsClipping = false;
            return;
        }

        var fraction = input / reference;

        IsClipping = fraction > 1.0;

        LastCode = (int)Math.Round(Math.Clamp(fraction, 0.0, 1.0) * FullScale);
        LastVoltage = LastCode * reference / FullScale;

        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _lastClock = LogicState.Low;
        _selected = false;
        _started = false;
        _bitsSeen = 0;
        _command = 0;
        _shiftOut = 0;
        _outBits = 0;

        LastChannel = -1;
        LastCode = 0;
        LastVoltage = 0;
        IsClipping = false;
    }
}
