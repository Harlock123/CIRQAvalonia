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
public partial class Atmega328p : DigitalIc, IBreakpointSource, IRunState, IFirmwareChip
{
    /// <summary>The internal pull-ups, between 20 and 50 kΩ on the datasheet.</summary>
    public const double PullUpResistance = 35e3;

    /// <summary>
    /// The datasheet's input levels at 5 V, which the pins are read with: high from 0.6·VCC and low below 0.3·VCC, so a
    /// TTL part's 3.4 V high reads high, as it does on a real Uno. The outputs swing as <see cref="LogicLevels.Cmos5V"/>
    /// says, and that is the part's saved setting.
    /// </summary>
    public static LogicLevels AvrLevels { get; } = LogicLevels.Cmos5V with { Name = "AVR 5V", Vih = 3.0, Vil = 1.5 };

    private readonly AvrCore _core;
    private readonly Terminal[] _gpio;
    private readonly (int Port, int Bit)[] _map;
    private readonly Terminal[] _adc;
    private double _cpuTime;
    private LogicState[] _driving = new LogicState[20];

    // The Uno's own rails (5V, IOREF, 3V3) and its ground pins after the first, stamped as sources.
    private readonly List<(Terminal Terminal, double Volts)> _rails = [];
    private readonly List<Terminal> _grounds = [];

    public Atmega328p() : this(uno: false)
    {
    }

    /// <summary>The chip, as a DIP-28, or (<paramref name="uno"/>) on an Arduino Uno's header.</summary>
    protected Atmega328p(bool uno) : this(uno ? 1 : 0)
    {
    }

    /// <summary>The packages: 0 the ATmega328P's DIP-28, 1 an Arduino Uno's header, 2 an ATtiny85's DIP-8.</summary>
    protected Atmega328p(int layout) : base(layout == 2 ? 8 : 28)
    {
        _core = new AvrCore { Tiny = layout == 2 };
        var count = layout == 2 ? 5 : 20;
        _gpio = new Terminal[count];
        _map = new (int, int)[count];
        _adc = new Terminal[layout == 2 ? 4 : 6];
        _driving = new LogicState[count];
        if (layout == 2)
        {
            Terminals = BuildTiny();
            ConfigurePins([Reset, .. _gpio], [.. _gpio]);
            _core.Reset();
            return;
        }

        if (layout == 1)
        {
            Terminals = BuildUno();
            ConfigurePins([Reset, .. _gpio], [.. _gpio]);
            _core.Reset();
            return;
        }

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

    /// <summary>The Uno's header: its pins in the board's order and places, the chip's ports and ADC behind them.</summary>
    private List<Terminal> BuildUno()
    {
        PropagationDelay = 5e-9;
        Levels = LogicLevels.Cmos5V;
        var profile = Boards.BoardProfile.ArduinoUno;
        var all = new List<Terminal>();
        var at = 0;
        Terminal? vcc = null, reset = null, aref = null;
        var pins = profile.LeftPins.Select((p, i) => (Pin: p, Offset: Boards.BoardPackage.PinOffset(profile, i, left: true)))
            .Concat(profile.RightPins.Select((p, i) => (Pin: p, Offset: Boards.BoardPackage.PinOffset(profile, i, left: false))));
        var gpio = new SortedDictionary<int, (Terminal Terminal, int Port, int Bit)>();
        foreach (var (pin, offset) in pins)
        {
            var id = $"p{pin.Number}";
            Terminal t;
            switch (pin.Name)
            {
                case "5V":
                    t = new Terminal(id, pin.Name, TerminalType.Power, offset);
                    vcc = t;
                    _rails.Add((t, 5.0));
                    break;
                case "IOREF":
                    t = new Terminal(id, pin.Name, TerminalType.Power, offset);
                    _rails.Add((t, 5.0));
                    break;
                case "3V3":
                    t = new Terminal(id, pin.Name, TerminalType.Power, offset);
                    _rails.Add((t, 3.3));
                    break;
                case "GND":
                    t = new Terminal(id, pin.Name, TerminalType.Ground, offset);
                    _grounds.Add(t);
                    break;
                case "RESET":
                    t = reset = new Terminal(id, pin.Name, TerminalType.Input, offset);
                    break;
                case "AREF":
                    t = aref = new Terminal(id, pin.Name, TerminalType.Passive, offset);
                    break;
                case var name when name.StartsWith('D') && int.TryParse(name[1..], out var d):
                    t = new Terminal(id, pin.Label, TerminalType.Bidirectional, offset);
                    gpio[d] = (t, d < 8 ? 2 : 0, d < 8 ? d : d - 8);
                    break;
                case var name when name.StartsWith('A') && int.TryParse(name[1..], out var a):
                    t = new Terminal(id, pin.Name, TerminalType.Bidirectional, offset);
                    gpio[100 + a] = (t, 1, a);
                    _adc[a] = t;
                    break;
                default:
                    t = new Terminal(id, pin.Name, TerminalType.Passive, offset);   // VIN: the board's input, not modelled
                    break;
            }

            all.Add(t);
        }

        foreach (var (_, (terminal, port, bit)) in gpio)
        {
            _gpio[at] = terminal;
            _map[at++] = (port, bit);
        }

        Vcc = vcc!;
        Gnd = _grounds[0];
        Gnd2 = _grounds.Count > 1 ? _grounds[1] : _grounds[0];
        Reset = reset!;
        Avcc = vcc!;
        Aref = aref!;
        _grounds.RemoveAt(0);
        return all;
    }

    /// <summary>The flash the part has: 32 KB, or the ATtiny85's 8 KB.</summary>
    public int FlashBytes => _core.Tiny ? 8192 : AvrCore.FlashWords * 2;

    /// <summary>
    /// The ATtiny85's DIP-8: PB5 is RESET (pin 1, pulled up), PB3 and PB4 pins 2 and 3, GND 4, PB0-PB2 pins 5-7 and VCC 8.
    /// ADC0-ADC3 are on PB5, PB2, PB4 and PB3, INT0 on PB2, Timer0's OC0A and OC0B on PB0 and PB1.
    /// </summary>
    private List<Terminal> BuildTiny()
    {
        PropagationDelay = 5e-9;
        Levels = LogicLevels.Cmos5V;
        ClockFrequency = 8e6;
        var pins = new Terminal[9];
        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 8));
        Reset = Pin(1, "PB5/RESET", TerminalType.Input);
        var pb3 = Pin(2, "PB3", TerminalType.Bidirectional);
        var pb4 = Pin(3, "PB4", TerminalType.Bidirectional);
        Gnd = Pin(4, "GND", TerminalType.Ground);
        var pb0 = Pin(5, "PB0", TerminalType.Bidirectional);
        var pb1 = Pin(6, "PB1", TerminalType.Bidirectional);
        var pb2 = Pin(7, "PB2", TerminalType.Bidirectional);
        Vcc = Pin(8, "VCC", TerminalType.Power);
        Terminal[] gpio = [pb0, pb1, pb2, pb3, pb4];
        for (var i = 0; i < 5; i++)
        {
            _gpio[i] = gpio[i];
            _map[i] = (0, i);
        }

        (_adc[0], _adc[1], _adc[2], _adc[3]) = (Reset, pb2, pb4, pb3);
        Avcc = Vcc;
        Aref = Vcc;
        Gnd2 = Gnd;
        return [.. pins.Skip(1)];
    }

    /// <summary>Its rails, and its ground pins after the first, as sources: the Uno feeds the circuit round it.</summary>
    public override int VoltageSourceCount => base.VoltageSourceCount + _rails.Count + _grounds.Count;

    public override string PartNumber => "ATmega328P";

    public override string ComponentType => "ATmega328P";

    public override bool LabelsPins => true;

    public Terminal Reset { get; private set; } = null!;

    /// <summary>The crystal's pins on the DIP; the Uno has its crystal on the board.</summary>
    public Terminal? Xtal1 { get; }

    public Terminal? Xtal2 { get; }

    public Terminal Avcc { get; private set; } = null!;

    public Terminal Aref { get; private set; } = null!;

    public Terminal Gnd2 { get; private set; } = null!;

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
        if (image.Blocks.Any(b => b.Address + b.Bytes.Length > FlashBytes))
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

    // ---- the debugger ----

    /// <summary>Byte addresses in flash it stops at, before running the instruction there.</summary>
    public HashSet<int> Breakpoints { get; } = [];

    /// <summary>Stopped at a breakpoint or after a single step: the firmware waits, its pins held, until it's resumed.</summary>
    public bool Halted { get; private set; }

    /// <summary>Why it stopped: "breakpoint" or "step"; null while it runs.</summary>
    public string? HaltReason { get; private set; }

    private bool _skipBreakpoint, _stepOne;

    /// <summary>Lets it run on, past the breakpoint it stopped at.</summary>
    public void Resume()
    {
        if (!Halted) return;
        Halted = false;
        HaltReason = null;
        _skipBreakpoint = true;
    }

    /// <summary>Runs one instruction (or takes one interrupt), then stops again.</summary>
    public void StepInstruction()
    {
        Halted = false;
        HaltReason = null;
        _skipBreakpoint = true;
        _stepOne = true;
    }

    /// <summary>Stops it where it is, as a breakpoint would.</summary>
    public void Break()
    {
        Halted = true;
        HaltReason = "paused";
    }

    /// <summary>How far ahead of the circuit it runs between samples of its pins, in seconds.</summary>
    public double Window =>
        _core.TwiBusy ? Math.Clamp(_core.TwiQuarter / ClockFrequency, 0.2e-6, 200e-6)
        : _core.RxEnabled ? Math.Clamp(_core.BitCycles / ClockFrequency / 8, 1e-6, 200e-6)
        : 200e-6;

    /// <summary>Where the firmware has run to, or a window on: the circuit is never let step past the chip unwatched.</summary>
    public double? NextBreakpointAfter(double time) => _cpuTime > time + 1e-12 ? _cpuTime : time + (_fresh ? 1e-9 : Window);

    // Just powered up or reset, it hasn't looked ahead yet: it wants a time point straight away to start from, or the
    // circuit would stride past its first edges with nobody watching.
    private bool _fresh = true;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);
        // A rail or a spare ground pin left unconnected is on ground's own node: its branch carries nothing.
        var branch = base.VoltageSourceCount;
        var reference = system.Node(Gnd);
        foreach (var (terminal, volts) in _rails.Concat(_grounds.Select(g => (g, 0.0))))
        {
            var b = system.Branch(this, branch++);
            if (system.Node(terminal) == reference) system.Add(b, b, 1.0);
            else system.StampVoltageSource(b, system.Node(terminal), reference, volts);
        }
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
            _fresh = true;
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

        // Stopped in the debugger: time goes on for the circuit, not for the firmware.
        if (Halted)
        {
            _cpuTime = time;
            return;
        }

        // Still ahead of the circuit from last time: nothing to do until it catches up.
        if (_cpuTime > time + 1e-12) return;

        _fresh = false;
        Sample(context);
        var period = 1.0 / Math.Max(ClockFrequency, 1);
        var limit = time + Window;
        // Behind the circuit (its first step, or a long one): the firmware catches up, up to a tenth of a second of it.
        if (_cpuTime < time - 0.1) _cpuTime = time - 0.1;
        var changed = false;
        while (_cpuTime < limit && !changed)
        {
            if (!_skipBreakpoint && Breakpoints.Count > 0 && Breakpoints.Contains(_core.Pc * 2))
            {
                Halted = true;
                HaltReason = "breakpoint";
                break;
            }

            _skipBreakpoint = false;
            var cycles = _core.Step();
            if (_stepOne)
            {
                _cpuTime += cycles * period;
                _stepOne = false;
                Halted = true;
                HaltReason = "step";
                break;
            }

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
            if (context.ReadInput(_gpio[i], AvrLevels).IsHigh()) ports[_map[i].Port] |= (byte)(1 << _map[i].Bit);
        Array.Copy(ports, _core.PinInputs, 3);
        var ground = context.NodeVoltage(Gnd);
        for (var i = 0; i < _adc.Length; i++) _core.AnalogInputs[i] = context.NodeVoltage(_adc[i]) - ground;
        _core.AvccVolts = context.NodeVoltage(Avcc) - ground;
        _core.ArefVolts = context.NodeVoltage(Aref) - ground;
        if (_core.AvccVolts < 0.5) _core.AvccVolts = context.NodeVoltage(Vcc) - ground;
        if (_core.Tiny) _core.PinInputs[2] = (byte)(_core.PinInputs[0] & 0x04);   // INT0 is PB2 on the tiny
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
        _fresh = true;
        Halted = false;
        HaltReason = null;
        _stepOne = _skipBreakpoint = false;
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
