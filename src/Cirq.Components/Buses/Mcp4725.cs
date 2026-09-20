using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// An MCP4725: a twelve-bit digital-to-analog converter on the I²C bus, and the other half of the
/// <see cref="Ads1115"/>.
/// <para>
/// Everything else on this bus moves numbers about. The ADC turns a voltage into one; this turns
/// one back into a voltage, and with both of them a circuit can finally close a loop — read a
/// sensor, decide something, and drive an analog stage with the answer. Without it the only way
/// out of the digital world is a pin that is either at the rail or at ground.
/// </para>
/// <para>
/// Three things about it are worth knowing before it disappoints you.
/// </para>
/// <para>
/// <b>Its output is its supply.</b> There is no reference pin: full scale is VDD, whatever VDD
/// happens to be. Run it from the same slightly-sagging 5 V rail as everything else and every
/// voltage it produces sags with it — which is fine for a control voltage and useless for a
/// measurement standard. The ADS1115 beside it has a proper internal reference; this does not.
/// </para>
/// <para>
/// <b>It cannot drive anything.</b> The output is a resistor divider behind a small buffer, good
/// for microamps. Hang a load of a few kilohms on it and the voltage is no longer the voltage you
/// asked for; hang a speaker on it and nothing happens at all. A follower from one of the op-amps
/// in this library is the usual answer, which is exactly what a buffer is for.
/// </para>
/// <para>
/// <b>It remembers.</b> A write can go to the register, which is lost at power-off, or to the
/// on-board EEPROM, which is not — so the part can come up at a chosen voltage rather than at
/// zero. That is the difference between the two write commands, and getting it wrong is how a
/// board powers up with its output somewhere surprising.
/// </para>
/// </summary>
public sealed partial class Mcp4725 : I2cSlave
{
    private const int FullScale = 4095;

    private int _writeShift;
    private int _writeBytes;
    private int _command;
    private int _readBytes;
    private double _supply = 5.0;
    private bool _fromBus;

    public Mcp4725()
        : base(new Point(50, -20), new Point(50, 20), new Point(-50, -20), new Point(-50, 20))
    {
        Address = 0x60;

        Output = new Terminal("vout", "VOUT", TerminalType.Output, new Point(50, 0));

        Terminals = [Vcc, Sda, Scl, Gnd, Output];
    }

    /// <summary>The analog output. Buffered, but only just.</summary>
    public Terminal Output { get; }

    /// <summary>
    /// The twelve-bit code in the register, 0 to 4095. Writable directly, so the part is usable
    /// as a bench source without a master on the bus to talk to it — and a value set that way is
    /// stored, so that running the simulation does not immediately throw it away.
    /// </summary>
    [ObservableProperty]
    [Operable("Code", Minimum = 0, Maximum = 4095)]
    public partial int Code { get; set; }

    /// <summary>
    /// The code it powers up at, held in the on-board EEPROM. Written by the "write DAC and
    /// EEPROM" command rather than the ordinary one.
    /// </summary>
    [ObservableProperty]
    public partial int StoredCode { get; set; }

    /// <summary>
    /// Output resistance, in ohms. Small on paper and far too large for anything that draws
    /// current, which is the point being made.
    /// </summary>
    [ObservableProperty]
    public partial double OutputResistance { get; set; } = 1000.0;

    /// <summary>
    /// Power-down mode, 0 to 3 as the datasheet numbers it: 0 is normal, and 1 to 3 pull the
    /// output down through 1 kΩ, 100 kΩ or 500 kΩ respectively.
    /// </summary>
    [ObservableProperty]
    public partial int PowerDownMode { get; set; }

    public override string ComponentType => "I2C DAC";

    public override string ValueLabel => $"{AnalogOutput:0.###} V";

    /// <summary>One extra branch beyond the bus pins, for the analog output.</summary>
    public override int VoltageSourceCount => base.VoltageSourceCount + 1;

    /// <summary>Full scale, which is whatever the supply is — there is no reference pin.</summary>
    public double Reference => Math.Max(_supply, 0.0);

    /// <summary>The voltage the code asks for, before any load pulls it about.</summary>
    public double AnalogOutput => Math.Clamp(Code, 0, FullScale) / (double)FullScale * Reference;

    /// <summary>Volts per step — the smallest change it can make.</summary>
    public double StepVoltage => Reference / (FullScale + 1);

    /// <summary>True while it is powered down and the output is being held low through a resistor.</summary>
    public bool IsPoweredDown => PowerDownMode != 0;

    /// <summary>The resistance actually behind the output, powered down or not.</summary>
    public double EffectiveResistance => PowerDownMode switch
    {
        1 => 1e3,
        2 => 100e3,
        3 => 500e3,
        _ => Math.Max(OutputResistance, 1e-3),
    };

    /// <summary>
    /// Nonlinear, because what it drives depends on a voltage elsewhere in the circuit — its own
    /// supply — rather than only on its inputs. Without saying so it would be stamped once from
    /// whatever the supply node happened to be at the start of the solve, which is nothing.
    /// </summary>
    public override bool IsNonlinear => true;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        _supply = system.IterationVoltage(Vcc) - system.IterationVoltage(Gnd);

        base.StampMatrix(system, state);

        // The analog output rides on its own branch, after the bus pins have taken theirs.
        var branch = system.Branch(this, OutputTerminals.Count);

        system.StampTheveninSource(
            branch, system.Node(Output), system.Node(Gnd),
            IsPoweredDown ? 0.0 : AnalogOutput, EffectiveResistance);
    }

    /// <summary>Current the output is delivering at the last solved point, in amps.</summary>
    public double OutputCurrent(MnaSystem system) => -system.BranchCurrent(this, OutputTerminals.Count);

    // ---- the bus side ----------------------------------------------------

    protected override bool OnAddressed(bool reading)
    {
        if (!reading)
        {
            _writeBytes = 0;
            _command = -1;
        }

        return true;
    }

    protected override bool OnByteReceived(int value)
    {
        // Two shapes of write, told apart by the top three bits. All zero is "fast mode": two
        // bytes and nothing else, the top nibble of the first being the power-down setting and the
        // remaining twelve bits the code. Anything else is a command byte followed by two data
        // bytes — 010 writes the register, 011 writes the EEPROM with it, and that difference is
        // what decides whether the part comes back to this value after a power cycle.
        if (_command < 0)
        {
            if ((value & 0xE0) != 0)
            {
                _command = value;
                _writeBytes = 0;
                return true;
            }

            _command = 0;
            _writeShift = (value & 0x0F) << 8;
            _writeBytes = 1;

            PowerDownMode = (value >> 4) & 0x03;
            return true;
        }

        if (_command == 0 && _writeBytes == 1)
        {
            SetFromBus(_writeShift | (value & 0xFF));
            _writeBytes = 0;
            _command = -1;
            return true;
        }

        // Command form: the twelve bits sit in the top of the two data bytes, not the bottom.
        if (_writeBytes == 0)
        {
            _writeShift = (value & 0xFF) << 4;
            _writeBytes = 1;
            return true;
        }

        SetFromBus(_writeShift | ((value >> 4) & 0x0F));

        // 0x60 writes the register only; 0x40 writes the EEPROM as well, so it comes back after
        // a power cycle.
        if ((_command & 0x60) == 0x60) StoredCode = Code;

        PowerDownMode = (_command >> 1) & 0x03;

        _writeBytes = 0;
        _command = -1;
        return true;
    }

    /// <summary>Writes the register without touching the stored value.</summary>
    private void SetFromBus(int value)
    {
        _fromBus = true;
        Code = value & 0x0FFF;
        _fromBus = false;
    }

    protected override int OnByteRequested()
    {
        // A read gives status, then the register, then what is in the EEPROM.
        var value = _readBytes switch
        {
            0 => 0x80 | ((PowerDownMode & 0x03) << 1),
            1 => (Code >> 4) & 0xFF,
            2 => (Code << 4) & 0xF0,
            3 => (StoredCode >> 8) & 0x0F,
            _ => StoredCode & 0xFF,
        };

        _readBytes = _readBytes >= 4 ? 0 : _readBytes + 1;
        return value;
    }

    protected override void OnTransferEnded()
    {
        _readBytes = 0;
        _writeBytes = 0;
        _command = -1;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _writeShift = 0;
        _writeBytes = 0;
        _readBytes = 0;
        _command = -1;

        // Which is the entire reason the EEPROM is there.
        SetFromBus(StoredCode);
        PowerDownMode = 0;
    }

    partial void OnCodeChanged(int value)
    {
        // Set by hand rather than over the bus: keep it, the way turning a knob on a bench supply
        // keeps it. A write that arrives over I²C only reaches the EEPROM if it asked to.
        if (!_fromBus) StoredCode = value;

        NotifyValueChanged();
    }
}
