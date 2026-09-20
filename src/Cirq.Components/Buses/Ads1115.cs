using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// An ADS1115: a sixteen-bit analog-to-digital converter on the I²C bus.
/// <para>
/// Everything else on this bus deals in bytes that were already digital — memory, pins, the time.
/// This one reaches into the circuit it is sitting in and reports what the voltage on a node
/// actually is, which is the point of putting an ADC on a bus at all.
/// </para>
/// <para>
/// Three things about it are worth getting wrong here rather than on a bench.
/// </para>
/// <para>
/// <b>The gain setting is a trap.</b> The programmable amplifier sets what counts as full scale,
/// and it defaults to a range far smaller than the supply. Feed a part set to ±2.048 V a three
/// volt signal and it does not complain: it returns 32767, the largest number it has, and goes on
/// returning it however much further the input rises. A reading pinned at full scale is the only
/// symptom, and it looks exactly like a reading.
/// </para>
/// <para>
/// <b>Conversion takes time.</b> At eight samples a second a conversion takes 125 ms, and reading
/// the register more often than that returns the same answer again — the converter has not
/// finished a new one. Polling harder does not sample faster.
/// </para>
/// <para>
/// <b>It measures a difference.</b> In single-ended mode the other side is the chip's own ground,
/// so anything between that ground and the ground your signal is referenced to is added to every
/// reading. Differential mode is there because that is often not zero.
/// </para>
/// </summary>
public sealed partial class Ads1115 : I2cSlave
{
    /// <summary>Full-scale range for each of the eight gain settings, in volts.</summary>
    private static readonly double[] FullScaleRanges =
        [6.144, 4.096, 2.048, 1.024, 0.512, 0.256, 0.256, 0.256];

    /// <summary>Samples per second for each of the eight data-rate settings.</summary>
    private static readonly double[] DataRates = [8, 16, 32, 64, 128, 250, 475, 860];

    private readonly Terminal[] _inputs = new Terminal[4];

    private int _pointer;
    private bool _pointerReceived;
    private int _writeShift;
    private int _writeBytes;

    private int _conversion;
    private double _lastConversionAt = double.NegativeInfinity;

    public Ads1115()
        : base(new Point(50, -20), new Point(50, 20), new Point(-50, -44), new Point(-50, 44))
    {
        Address = 0x48;

        // The analog inputs down the left with the supply pins, the bus on the right: signals in
        // one side and two wires out the other is how one of these is actually wired.
        for (var i = 0; i < 4; i++)
            _inputs[i] = new Terminal($"a{i}", $"A{i}", TerminalType.Input, new Point(-50, -21 + (i * 14)));

        Terminals = [Vcc, Sda, Scl, Gnd, .. _inputs];
    }

    /// <summary>One of the four analog inputs, numbered as the datasheet numbers them.</summary>
    public Terminal Input(int channel) => _inputs[channel];

    /// <summary>
    /// The configuration register, exactly as it is over the bus.
    /// <para>
    /// A real part powers up at 0x8583 — single shot, and measuring the difference between A0 and
    /// A1, which does nothing useful until something writes to it. This starts in continuous mode
    /// on A0 against ground instead, so that placing one and running gives a reading; write the
    /// register to get the datasheet's behaviour back.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial int Configuration { get; set; } = 0x4283;

    public override string ComponentType => "I2C ADC";

    public override string ValueLabel =>
        IsClipping ? $"{LastVoltage:0.###} V (full scale)" : $"{LastVoltage:0.###} V";

    /// <summary>Which input arrangement the configuration selects, 0-7 as the datasheet has it.</summary>
    public int Multiplexer => (Configuration >> 12) & 0x07;

    /// <summary>Full-scale voltage the gain setting corresponds to.</summary>
    public double FullScale => FullScaleRanges[(Configuration >> 9) & 0x07];

    /// <summary>Conversions per second the data-rate setting corresponds to.</summary>
    public double DataRate => DataRates[(Configuration >> 5) & 0x07];

    /// <summary>True while it is converting continuously rather than one reading at a time.</summary>
    public bool IsContinuous => ((Configuration >> 8) & 1) == 0;

    /// <summary>The last completed conversion, as the sixteen-bit signed number the register holds.</summary>
    public int LastValue => _conversion;

    /// <summary>That reading turned back into volts.</summary>
    public double LastVoltage => _conversion / 32767.0 * FullScale;

    /// <summary>
    /// True when the reading has hit the end of the scale. It is not an error the part reports —
    /// it just stops changing, which is why it is worth showing.
    /// </summary>
    public bool IsClipping => Math.Abs(_conversion) >= 32767;

    /// <summary>How long one conversion takes, in seconds.</summary>
    public double ConversionSeconds => 1.0 / Math.Max(DataRate, 1.0);

    public override void EvaluateLogic(IDigitalContext context)
    {
        Convert(context);
        base.EvaluateLogic(context);
    }

    /// <summary>
    /// Takes a new reading if one is due. Reading the register more often than this gives the
    /// previous answer again, which is the behaviour of the real part and the reason its data
    /// rate matters.
    /// </summary>
    private void Convert(IDigitalContext context)
    {
        if (context.Time - _lastConversionAt < ConversionSeconds) return;

        _lastConversionAt = context.Time;

        var measured = MeasuredVoltage(context);
        var counts = measured / FullScale * 32767.0;

        _conversion = (int)Math.Round(Math.Clamp(counts, -32768, 32767));
    }

    /// <summary>The voltage the multiplexer setting selects, referred to the chip's own ground.</summary>
    private double MeasuredVoltage(IDigitalContext context)
    {
        double At(int channel) => context.NodeVoltage(_inputs[channel]) - context.NodeVoltage(Gnd);

        return Multiplexer switch
        {
            0 => At(0) - At(1),
            1 => At(0) - At(3),
            2 => At(1) - At(3),
            3 => At(2) - At(3),
            4 => At(0),
            5 => At(1),
            6 => At(2),
            _ => At(3),
        };
    }

    // ---- the bus side ----------------------------------------------------

    protected override bool OnAddressed(bool reading)
    {
        if (!reading)
        {
            _pointerReceived = false;
            _writeBytes = 0;
        }

        return true;
    }

    protected override bool OnByteReceived(int value)
    {
        // A write is the register pointer, then optionally the two bytes to put there.
        if (!_pointerReceived)
        {
            _pointer = value & 0x03;
            _pointerReceived = true;
            _writeBytes = 0;
            return true;
        }

        _writeShift = _writeBytes == 0 ? value << 8 : (_writeShift & 0xFF00) | value;
        _writeBytes++;

        if (_writeBytes == 2 && _pointer == 1)
        {
            Configuration = _writeShift & 0xFFFF;

            // Writing the config is what starts a single-shot conversion, so the next reading is
            // due immediately rather than one period after the last one.
            _lastConversionAt = double.NegativeInfinity;
            _writeBytes = 0;
        }

        return true;
    }

    private int _readBytes;

    protected override int OnByteRequested()
    {
        var register = _pointer switch
        {
            1 => Configuration,
            _ => _conversion & 0xFFFF,
        };

        // Sixteen bits, most significant byte first, as everything on this bus does it.
        var value = _readBytes == 0 ? (register >> 8) & 0xFF : register & 0xFF;

        _readBytes = _readBytes == 0 ? 1 : 0;
        return value;
    }

    protected override void OnTransferEnded() => _readBytes = 0;

    public override void ResetLogic()
    {
        base.ResetLogic();

        _pointer = 0;
        _pointerReceived = false;
        _writeShift = 0;
        _writeBytes = 0;
        _readBytes = 0;

        _conversion = 0;
        _lastConversionAt = double.NegativeInfinity;
    }

    partial void OnConfigurationChanged(int value) => NotifyValueChanged();
}
