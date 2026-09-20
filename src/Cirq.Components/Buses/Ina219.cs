using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// An INA219: a shunt across the <b>high side</b> of a rail, an amplifier that measures across it,
/// and an I²C interface. It reports the current, the voltage on the load, and the product of the
/// two.
/// <para>
/// Measuring current is easy in the wrong place. Put the shunt in the ground return and any
/// ordinary amplifier can read it — but then the load's ground is not ground any more, and every
/// other measurement in the circuit is off by whatever the shunt is dropping. High-side sensing
/// keeps the load's ground where it belongs, and costs you an amplifier that can read a few
/// millivolts of difference while both of its inputs sit near the supply.
/// </para>
/// <para>
/// Which is where the trap is, and it is the reason this part is here. A current-sense amplifier
/// has a <b>common-mode range</b>, and it is not the same thing as its supply. This one runs from
/// 3.3 V and reads a shunt sitting anywhere up to <b>26 V</b> — that is the whole point of it —
/// but a rail at 30 V is outside the range, and what you get then is not a reading with an error
/// in it. It is a number with nothing behind it, arriving over the bus looking exactly like a
/// measurement. The part reports that it is out of range, because nothing else would.
/// </para>
/// <para>
/// The second thing worth knowing is that the shunt is a compromise you have to make. Larger
/// gives a better reading and wastes more; 0.1 Ω at an amp is a tenth of a volt gone and a tenth
/// of a watt as heat, which on a 3.3 V rail is a great deal to spend on knowing.
/// </para>
/// </summary>
public sealed partial class Ina219 : I2cSlave
{
    /// <summary>The LSB of the shunt-voltage register: ten microvolts, as the datasheet has it.</summary>
    private const double ShuntLsb = 10e-6;

    /// <summary>The LSB of the bus-voltage register: four millivolts.</summary>
    private const double BusLsb = 4e-3;

    private int _pointer;
    private bool _pointerReceived;
    private int _writeShift;
    private int _writeBytes;
    private int _readBytes;

    private double _shuntVoltage;
    private double _busVoltage;

    public Ina219()
        : base(new Point(50, -20), new Point(50, 20), new Point(-50, -44), new Point(-50, 44))
    {
        Address = 0x40;

        ShuntPlus = new Terminal("vin+", "VIN+", TerminalType.Passive, new Point(-50, -14));
        ShuntMinus = new Terminal("vin-", "VIN-", TerminalType.Passive, new Point(-50, 14));

        Terminals = [Vcc, Sda, Scl, Gnd, ShuntPlus, ShuntMinus];
    }

    /// <summary>The supply side of the shunt.</summary>
    public Terminal ShuntPlus { get; }

    /// <summary>The load side, which is also where the bus voltage is measured.</summary>
    public Terminal ShuntMinus { get; }

    /// <summary>The shunt itself, in ohms. It is inside the part here rather than wired outside.</summary>
    [ObservableProperty]
    public partial double ShuntResistance { get; set; } = 0.1;

    /// <summary>
    /// The highest voltage the inputs may sit at, in volts, whatever the supply is. Twenty-six on
    /// a real INA219, and the number that decides whether it can watch the rail you want watched.
    /// </summary>
    [ObservableProperty]
    public partial double CommonModeLimit { get; set; } = 26.0;

    /// <summary>
    /// Full scale of the shunt amplifier, in volts. The gain setting: ±320 mV by default, and the
    /// smaller settings buy resolution at the cost of the current they can read.
    /// </summary>
    [ObservableProperty]
    public partial double ShuntFullScale { get; set; } = 0.32;

    public override string ComponentType => "I2C Current Sensor";

    public override string ValueLabel =>
        IsOutOfCommonModeRange ? "out of range" : $"{Current * 1e3:0.#} mA";

    /// <summary>Voltage across the shunt at the last solved point.</summary>
    public double ShuntVoltage => _shuntVoltage;

    /// <summary>Voltage on the load side, which is what the bus-voltage register holds.</summary>
    public double BusVoltage => _busVoltage;

    /// <summary>The current through the shunt, in amps.</summary>
    public double Current => _shuntVoltage / Math.Max(ShuntResistance, 1e-6);

    /// <summary>What the load is taking, in watts.</summary>
    public double Power => Current * _busVoltage;

    /// <summary>
    /// True when the inputs are sitting above what the part can cope with. The registers still
    /// contain numbers; they simply do not mean anything.
    /// </summary>
    public bool IsOutOfCommonModeRange { get; private set; }

    /// <summary>True when the current is past what the gain setting can express.</summary>
    public bool IsOverRange => Math.Abs(_shuntVoltage) > ShuntFullScale;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        // The shunt is a resistor in the rail like any other, and it drops what it drops.
        system.StampConductance(
            system.Node(ShuntPlus), system.Node(ShuntMinus), 1.0 / Math.Max(ShuntResistance, 1e-6));
    }

    public override void EvaluateLogic(IDigitalContext context)
    {
        var high = context.NodeVoltage(ShuntPlus) - context.NodeVoltage(Gnd);
        var low = context.NodeVoltage(ShuntMinus) - context.NodeVoltage(Gnd);

        _shuntVoltage = high - low;
        _busVoltage = low;

        IsOutOfCommonModeRange = Math.Max(high, low) > CommonModeLimit;

        base.EvaluateLogic(context);
    }

    // ---- the bus side ----------------------------------------------------

    /// <summary>
    /// The shunt-voltage register, in its own units of ten microvolts. Clamped to the gain
    /// setting's full scale, which is how a real one behaves when it is over-range: the number
    /// stops rising rather than reporting more current.
    /// </summary>
    public int ShuntRegister =>
        (short)Math.Round(Math.Clamp(_shuntVoltage, -ShuntFullScale, ShuntFullScale) / ShuntLsb);

    /// <summary>The bus-voltage register: the reading in the top thirteen bits, flags below.</summary>
    public int BusRegister
    {
        get
        {
            var counts = (int)Math.Round(Math.Max(_busVoltage, 0.0) / BusLsb);

            // Bit 1 is "conversion ready", which this part always is.
            return ((Math.Min(counts, 0x1FFF) & 0x1FFF) << 3) | 0x02;
        }
    }

    protected override bool OnAddressed(bool reading)
    {
        if (!reading)
        {
            _pointerReceived = false;
            _writeBytes = 0;
        }

        _readBytes = 0;
        return true;
    }

    protected override bool OnByteReceived(int value)
    {
        if (!_pointerReceived)
        {
            _pointer = value & 0x07;
            _pointerReceived = true;
            _writeBytes = 0;
            return true;
        }

        // Configuration and calibration writes are accepted and ignored: the model measures what
        // the circuit is doing rather than being told how to.
        _writeShift = _writeBytes == 0 ? value << 8 : (_writeShift & 0xFF00) | value;
        _writeBytes = (_writeBytes + 1) % 2;

        return true;
    }

    protected override int OnByteRequested()
    {
        var register = _pointer switch
        {
            1 => ShuntRegister & 0xFFFF,
            2 => BusRegister,
            3 => (int)Math.Round(Math.Abs(Power) / 20e-3) & 0xFFFF,
            4 => (int)Math.Round(Current / 1e-3) & 0xFFFF,
            _ => 0x399F,     // The configuration register's power-on value.
        };

        var value = _readBytes == 0 ? (register >> 8) & 0xFF : register & 0xFF;

        _readBytes = _readBytes == 0 ? 1 : 0;
        return value;
    }

    protected override void OnTransferEnded() => _readBytes = 0;

    public IReadOnlyList<string> Violations => IsOutOfCommonModeRange
        ?
        [
            $"its inputs are at {Math.Max(_shuntVoltage + _busVoltage, _busVoltage):0.#} V against a " +
            $"{CommonModeLimit:0.#} V common-mode limit — it will still return a number over the bus, " +
            "and that number will not be a measurement",
        ]
        : [];

    public override void ResetLogic()
    {
        base.ResetLogic();

        _pointer = 0;
        _pointerReceived = false;
        _writeShift = 0;
        _writeBytes = 0;
        _readBytes = 0;
        _shuntVoltage = 0;
        _busVoltage = 0;
        IsOutOfCommonModeRange = false;
    }

    partial void OnShuntResistanceChanged(double value) => NotifyValueChanged();
}
