using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Processors;

/// <summary>
/// The ATmega328P, the chip on an Arduino Uno, in its 28-pin DIP: real firmware, as an Intel HEX file avr-gcc or the
/// Arduino tools write, running instruction by instruction at its clock (16 MHz unless set otherwise; the crystal is
/// taken as read rather than simulated, so XTAL1 and XTAL2 are left alone).
/// <para>
/// <b>How it keeps time with the circuit.</b> At each time point the circuit is solved at, it samples its pins (the digital
/// levels, and the analog voltages for the ADC), then runs on from where it had got to, up to a short window ahead
/// (<see cref="Window"/>), with those inputs held. The moment an output pin changes it stops, and asks for that change
/// at that exact moment; the simulator lands a time point there (the chip is a breakpoint source), and it carries on.
/// So every edge it makes is where the firmware put it, to the cycle, and what it reads is at most a window old: 200 µs
/// usually, an eighth of a bit while the serial receiver is listening.
/// </para>
/// <para>
/// The pins are the datasheet's: PB0-PB5, PC0-PC5 and PD0-PD7 as GPIO with their internal pull-ups, PD0 and PD1 as
/// RXD and TXD when the USART takes them, the six PWM outputs on their timers' pins, ADC0-ADC5 on port C, INT0 and INT1
/// on PD2 and PD3, /RESET on PC6 (pulled up). VCC, AVCC and AREF are read for the supply and the ADC's reference.
/// </para>
/// </summary>
public sealed partial class Atmega328p : DigitalIc, IBreakpointSource, IRunState
{
    /// <summary>The internal pull-ups, between 20 and 50 kΩ on the datasheet.</summary>
    public const double PullUpResistance = 35e3;

    private readonly AvrCore _core = new();
    private readonly Terminal[] _gpio = new Terminal[20];
    private readonly (int Port, int Bit)[] _map = new (int, int)[20];
    private readonly Terminal[] _adc = new Terminal[6];
    private double _cpuTime;
    private LogicState[] _driving = new LogicState[20];

    public Atmega328p() : base(28)
    {
        PropagationDelay = 5e-9;
        Levels = LogicLevels.Cmos5V;
        var pins = new Terminal[29];
        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 28));

        Reset = Pin(1, "RESET", TerminalType.Input);
        var at = 0;
        void Gpio(int number, string name, int port, int bit)
        {
            _gpio[at] = Pin(number, name, TerminalType.Bidirectional);
            _map[at++] = (port, bit);
        }

        Gpio(2, "PD0/RXD", 2, 0);
        Gpio(3, "PD1/TXD", 2, 1);
        Gpio(4, "PD2/INT0", 2, 2);
        Gpio(5, "PD3/INT1", 2, 3);
        Gpio(6, "PD4", 2, 4);
        Vcc = Pin(7, "VCC", TerminalType.Power);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        Xtal1 = Pin(9, "XTAL1", TerminalType.Passive);
        Xtal2 = Pin(10, "XTAL2", TerminalType.Passive);
        Gpio(11, "PD5", 2, 5);
        Gpio(12, "PD6", 2, 6);
        Gpio(13, "PD7", 2, 7);
        Gpio(14, "PB0", 0, 0);
        Gpio(15, "PB1", 0, 1);
        Gpio(16, "PB2", 0, 2);
        Gpio(17, "PB3", 0, 3);
        Gpio(18, "PB4", 0, 4);
        Gpio(19, "PB5", 0, 5);
        Avcc = Pin(20, "AVCC", TerminalType.Passive);
        Aref = Pin(21, "AREF", TerminalType.Passive);
        Gnd2 = Pin(22, "GND", TerminalType.Ground);
        for (var i = 0; i < 6; i++)
        {
            Gpio(23 + i, $"PC{i}/ADC{i}", 1, i);
            _adc[i] = pins[23 + i];
        }

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Reset, .. _gpio], [.. _gpio]);
        _core.Reset();
    }

    public override string PartNumber => "ATmega328P";

    public override string ComponentType => "ATmega328P";

    public override bool LabelsPins => true;

    public Terminal Reset { get; }

    public Terminal Xtal1 { get; }

    public Terminal Xtal2 { get; }

    public Terminal Avcc { get; }

    public Terminal Aref { get; }

    public Terminal Gnd2 { get; }

    /// <summary>The GPIO pins, PD0-PD7 then PB0-PB5 then PC0-PC5 in pin order, as <see cref="DigitalComponent.OutputTerminals"/>.</summary>
    public IReadOnlyList<Terminal> Gpio => _gpio;

    /// <summary>The chip's insides, for a test or the agent tools to look at.</summary>
    public AvrCore Core => _core;

    /// <summary>The clock, in hertz. 16 MHz is an Uno's crystal; 8 MHz the internal oscillator.</summary>
    [ObservableProperty]
    public partial double ClockFrequency { get; set; } = 16e6;

    /// <summary>
    /// The firmware, as Intel HEX text (what avr-gcc's <c>avr-objcopy -O ihex</c> and the Arduino IDE's Export Compiled
    /// Binary write). Kept with the circuit, so it opens running the same program.
    /// </summary>
    [ObservableProperty]
    public partial string Firmware { get; set; } = string.Empty;

    partial void OnFirmwareChanged(string value)
    {
        try
        {
            var image = value.Trim().Length == 0 ? new MemoryImage([], "none") : MemoryImage.FromIntelHex(value);
            _core.LoadFlash(image.Blocks);
            FirmwareError = null;
        }
        catch (Exception e) when (e is InvalidDataException or FormatException)
        {
            FirmwareError = e.Message;
        }

        _core.Reset();
        NotifyValueChanged();
    }

    /// <summary>Why the firmware didn't load, or null.</summary>
    public string? FirmwareError { get; private set; }

    /// <summary>How many bytes of flash the firmware fills.</summary>
    public int FirmwareBytes => _core.State.Flash.Count(w => w != 0) * 2;

    public override string ValueLabel => FirmwareError is not null ? "bad firmware"
        : Firmware.Trim().Length == 0 ? "no firmware"
        : string.Create(CultureInfo.InvariantCulture, $"PC ${_core.Pc * 2:X4}");

    /// <summary>Loads a firmware image (Intel HEX, S-records or a binary at address 0) and keeps it as <see cref="Firmware"/>.</summary>
    public string Load(MemoryImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.ByteCount == 0) throw new InvalidDataException("The image has no bytes in it.");
        if (image.Blocks.Any(b => b.Address + b.Bytes.Length > AvrCore.FlashWords * 2))
            throw new InvalidDataException("The image is bigger than the 32 KB flash.");
        var hex = new System.Text.StringBuilder();
        foreach (var (address, bytes) in image.Blocks)
        {
            var text = MemoryImage.ToIntelHex(address, bytes);
            var end = text.LastIndexOf(":00000001FF", StringComparison.Ordinal);
            hex.Append(end >= 0 ? text[..end] : text);
        }

        hex.AppendLine(":00000001FF");
        Firmware = hex.ToString();
        if (FirmwareError is { } error) throw new InvalidDataException(error);
        return $"{image.ByteCount} bytes of firmware";
    }

    /// <summary>How far ahead of the circuit it runs between samples of its pins, in seconds.</summary>
    public double Window => _core.RxEnabled ? Math.Clamp(_core.BitCycles / ClockFrequency / 8, 1e-6, 200e-6) : 200e-6;

    /// <summary>Where the firmware has run to, or a window on: the circuit is never let step past the chip unwatched.</summary>
    public double? NextBreakpointAfter(double time) => _cpuTime > time + 1e-12 ? _cpuTime : time + Window;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);
        var vcc = system.Node(Vcc);
        system.StampResistor(system.Node(Reset), vcc, PullUpResistance);
        for (var i = 0; i < _gpio.Length; i++)
            if (_core.PulledUp(_map[i].Port, _map[i].Bit)) system.StampResistor(system.Node(_gpio[i]), vcc, PullUpResistance);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var time = context.Time;
        if (context.IsInitializing)
        {
            _cpuTime = time;
            Drive(context, time);
            return;
        }

        if (context.ReadInput(Reset, Levels).IsLow())
        {
            _core.Reset();
            _cpuTime = time;
            Drive(context, time);
            return;
        }

        // Still ahead of the circuit from last time: nothing to do until it catches up.
        if (_cpuTime > time + 1e-12) return;

        Sample(context);
        var period = 1.0 / Math.Max(ClockFrequency, 1);
        var limit = time + Window;
        // Behind the circuit (its first step, or a long one): the firmware catches up, up to a tenth of a second of it.
        if (_cpuTime < time - 0.1) _cpuTime = time - 0.1;
        var changed = false;
        while (_cpuTime < limit && !changed)
        {
            var cycles = _core.Step();
            _cpuTime += cycles * period;
            changed = Outputs();
        }

        Drive(context, time);
        NotifyValueChanged();
    }

    private void Sample(IDigitalContext context)
    {
        var ports = new byte[3];
        for (var i = 0; i < _gpio.Length; i++)
            if (context.ReadInput(_gpio[i], Levels).IsHigh()) ports[_map[i].Port] |= (byte)(1 << _map[i].Bit);
        Array.Copy(ports, _core.PinInputs, 3);
        var ground = context.NodeVoltage(Gnd);
        for (var i = 0; i < _adc.Length; i++) _core.AnalogInputs[i] = context.NodeVoltage(_adc[i]) - ground;
        _core.AvccVolts = context.NodeVoltage(Avcc) - ground;
        _core.ArefVolts = context.NodeVoltage(Aref) - ground;
        if (_core.AvccVolts < 0.5) _core.AvccVolts = context.NodeVoltage(Vcc) - ground;
        _core.SampleEdges();
    }

    /// <summary>Whether any pin's level has changed since it was last driven.</summary>
    private bool Outputs()
    {
        for (var i = 0; i < _gpio.Length; i++)
            if (Level(i) != _driving[i]) return true;
        return false;
    }

    private LogicState Level(int i) => _core.Driven(_map[i].Port, _map[i].Bit) switch
    {
        null => LogicState.HighImpedance,
        true => LogicState.High,
        false => LogicState.Low,
    };

    private void Drive(IDigitalContext context, double now)
    {
        var delay = Math.Max(0, _cpuTime - now);
        for (var i = 0; i < _gpio.Length; i++)
        {
            var level = Level(i);
            if (level == _driving[i]) continue;
            _driving[i] = level;
            Request(context, i, level, delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _core.Reset();
        _cpuTime = 0;
        _driving = new LogicState[_gpio.Length];
        Array.Fill(_driving, LogicState.Unknown);
    }

    public object CaptureRunState() => _core.State.Clone();

    public void AdoptRunState(object state)
    {
        if (state is not AvrCore.Registers registers) return;
        _core.State = registers.Clone();
        NotifyValueChanged();
    }
}
