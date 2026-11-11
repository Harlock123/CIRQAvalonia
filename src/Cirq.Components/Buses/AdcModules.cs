using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// An HX711 load-cell amplifier module: a 24-bit ADC with a gain of 128 (or 64) on channel A and 32 on channel B, and
/// the excitation for the bridge on E+ (4.3 V from its regulator, or just under VCC on a lower supply).
/// <para>
/// DOUT goes low when a reading is ready, 10 or 80 times a second (<see cref="FastRate"/>). The host then gives SCK 25,
/// 26 or 27 pulses: each rising edge puts the next bit on DOUT, the 24 bits of the reading most significant first, as
/// two's complement; the pulses after the 24th choose the next conversion: 25 channel A at 128, 26 channel B at 32, 27
/// channel A at 64. Full scale is ±½·AVDD / gain. SCK held high for over 60 µs powers it down; it comes back at A, 128.
/// </para>
/// </summary>
public sealed partial class Hx711 : DigitalIc, IBreakpointSource
{
    private const int DataOut = 0;
    private int _gain = 128;
    private bool _channelB;
    private int _pending = 128;
    private bool _pendingB;
    private double _readyAt = 0.1;
    private bool _ready;
    private int _code;
    private int _pulses;
    private bool _lastSck;
    private double _sckRose;
    private bool _down;

    public Hx711() : base(10)
    {
        PropagationDelay = 0.1e-6;
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(40, 30));
        Data = new Terminal("dt", "DT", TerminalType.Output, new Point(40, 10));
        Clock = new Terminal("sck", "SCK", TerminalType.Input, new Point(40, -10));
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(40, -30));
        ExcitationPlus = new Terminal("e+", "E+", TerminalType.Output, new Point(-40, -40));
        ExcitationMinus = new Terminal("e-", "E-", TerminalType.Passive, new Point(-40, -20));
        AMinus = new Terminal("a-", "A-", TerminalType.Passive, new Point(-40, 0));
        APlus = new Terminal("a+", "A+", TerminalType.Passive, new Point(-40, 20));
        BMinus = new Terminal("b-", "B-", TerminalType.Passive, new Point(-40, 40));
        BPlus = new Terminal("b+", "B+", TerminalType.Passive, new Point(-40, 60));
        Terminals = [ExcitationPlus, ExcitationMinus, AMinus, APlus, BMinus, BPlus, Vcc, Clock, Data, Gnd];
        ConfigurePins([Clock], [Data]);
    }

    public Terminal Data { get; }
    public Terminal Clock { get; }
    public Terminal ExcitationPlus { get; }
    public Terminal ExcitationMinus { get; }
    public Terminal APlus { get; }
    public Terminal AMinus { get; }
    public Terminal BPlus { get; }
    public Terminal BMinus { get; }

    /// <summary>80 readings a second (RATE high) rather than 10.</summary>
    [ObservableProperty]
    public partial bool FastRate { get; set; }

    /// <summary>The last reading, a signed 24-bit count.</summary>
    public int Reading => _code;

    public int Gain => _gain;

    public bool ChannelB => _channelB;

    public override string PartNumber => "HX711";

    public override string ComponentType => "Load Cell Amplifier";

    public override string ValueLabel => $"HX711 {_code}";

    public override bool LabelsPins => true;

    public override int VoltageSourceCount => base.VoltageSourceCount + 1;

    private double Period => FastRate ? 1.0 / 80 : 1.0 / 10;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);
        var (vcc, gnd) = (system.Node(Vcc), system.Node(Gnd));
        var supply = system.IterationVoltageAcross(vcc, gnd);
        system.StampTheveninSource(system.Branch(this, base.VoltageSourceCount), system.Node(ExcitationPlus), gnd,
            Math.Clamp(supply - 0.2, 0, 4.3), 1);
        system.StampResistor(system.Node(ExcitationMinus), gnd, 0.1);
        foreach (var input in new[] { APlus, AMinus, BPlus, BMinus }) system.StampResistor(system.Node(input), gnd, 100e6);
    }

    public double? NextBreakpointAfter(double time) => !_down && _readyAt > time + 1e-12 ? _readyAt : null;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var t = context.Time;
        var sck = context.ReadInput(Clock, Levels).IsHigh();
        if (sck && !_lastSck) _sckRose = t;

        if (sck && !_down && t - _sckRose > 60e-6)
        {
            _down = true;
            (_pulses, _ready) = (0, false);
        }
        else if (_down && !sck)
        {
            _down = false;
            (_gain, _channelB, _pending, _pendingB) = (128, false, 128, false);
            _readyAt = t + Period;
        }

        if (!_down)
        {
            if (sck && !_lastSck && (_ready || _pulses > 0))
            {
                _pulses++;
                _ready = false;
                (_pending, _pendingB) = _pulses switch { 25 => (128, false), 26 => (32, true), >= 27 => (64, false), _ => (_pending, _pendingB) };
            }
            else if (!sck && _lastSck && _pulses >= 25)
            {
                // Each falling edge past the 24th bit (re)starts the next conversion at the gain counted so far.
                (_gain, _channelB) = (_pending, _pendingB);
                _readyAt = t + Period;
            }

            // A conversion done, unless a reading is being clocked out: DOUT falls to say so.
            if ((_pulses == 0 || (_pulses >= 25 && !sck)) && t >= _readyAt - 1e-12)
            {
                var (plus, minus) = _channelB ? (BPlus, BMinus) : (APlus, AMinus);
                var avdd = Math.Max(context.NodeVoltage(ExcitationPlus) - context.NodeVoltage(Gnd), 1e-3);
                var x = (context.NodeVoltage(plus) - context.NodeVoltage(minus)) / (0.5 * avdd / _gain);
                _code = (int)Math.Clamp(Math.Round(x * 8388608), -8388608, 8388607);
                (_ready, _pulses) = (true, 0);
                _readyAt = t + Period;
                NotifyValueChanged();
            }
        }

        _lastSck = sck;
        var level = _down ? LogicState.High
            : _pulses is > 0 and <= 24 ? (((_code >> (24 - _pulses)) & 1) != 0 ? LogicState.High : LogicState.Low)
            : _ready && _pulses == 0 ? LogicState.Low : LogicState.High;
        Request(context, DataOut, level, DelayFor(context));
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        (_gain, _channelB, _pending, _pendingB, _ready, _code, _pulses, _lastSck, _down) = (128, false, 128, false, false, 0, 0, false, false);
        _readyAt = Period;
    }
}

/// <summary>
/// A MAX6675 type-K thermocouple converter module: the thermocouple on T+ and T− (T− to ground), its reading on SPI as
/// one 16-bit word — read only, no command — while CS is low: bit 15 a dummy 0, bits 14-3 the temperature in quarter
/// degrees from 0 to 1023.75 °C, bit 2 high when the thermocouple is open, bits 1-0 zero. SO puts out the top bit as CS
/// falls and the next on each falling SCK, so the host reads on the rising edges. It compensates for its own cold
/// junction (<see cref="ColdJunctionTemperature"/>) at 41.276 µV a degree. A real one wants 220 ms between readings;
/// this one has its answer at every CS.
/// </summary>
public sealed partial class Max6675 : DigitalIc
{
    private const int SerialOut = 0;
    private bool _lastCs = true, _lastSck;
    private int _word, _bit;

    public Max6675() : base(7)
    {
        PropagationDelay = 50e-9;
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(40, -30));
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(40, -15));
        Clock = new Terminal("sck", "SCK", TerminalType.Input, new Point(40, 0));
        ChipSelect = new Terminal("cs", "CS", TerminalType.Input, new Point(40, 15));
        SerialData = new Terminal("so", "SO", TerminalType.Output, new Point(40, 30));
        ThermocouplePlus = new Terminal("t+", "T+", TerminalType.Passive, new Point(-40, -10));
        ThermocoupleMinus = new Terminal("t-", "T-", TerminalType.Passive, new Point(-40, 10));
        Terminals = [Gnd, Vcc, Clock, ChipSelect, SerialData, ThermocouplePlus, ThermocoupleMinus];
        ConfigurePins([Clock, ChipSelect], [SerialData]);
    }

    public Terminal Clock { get; }
    public Terminal ChipSelect { get; }
    public Terminal SerialData { get; }
    public Terminal ThermocouplePlus { get; }
    public Terminal ThermocoupleMinus { get; }

    /// <summary>The temperature of the module itself, where the thermocouple's wires meet copper, in °C.</summary>
    [ObservableProperty]
    public partial double ColdJunctionTemperature { get; set; } = 25.0;

    /// <summary>The temperature it last reported, in °C, or null with the thermocouple open.</summary>
    public double? Temperature { get; private set; }

    public override string PartNumber => "MAX6675";

    public override string ComponentType => "Thermocouple Converter";

    public override string ValueLabel => Temperature is { } t ? $"MAX6675 {t:0.00} °C" : "MAX6675 open";

    public override bool LabelsPins => true;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        // A weak pull-up on T+, which is how it sees a thermocouple that has come off.
        system.StampResistor(system.Node(ThermocouplePlus), system.Node(Vcc), 10e6);
        system.StampResistor(system.Node(ThermocoupleMinus), system.Node(Gnd), 1e6);
    }

    /// <summary>The word it would send for a thermocouple voltage.</summary>
    public int WordFor(double microvolts, bool open)
    {
        if (open) return 0x0004;
        var celsius = ColdJunctionTemperature + (microvolts / 41.276);
        var quarters = (int)Math.Clamp(Math.Round(celsius * 4), 0, 4095);
        return quarters << 3;
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var cs = context.ReadInput(ChipSelect, Levels).IsHigh();
        var sck = context.ReadInput(Clock, Levels).IsHigh();
        if (!cs && _lastCs)
        {
            var v = context.NodeVoltage(ThermocouplePlus) - context.NodeVoltage(ThermocoupleMinus);
            var open = v > 0.1;
            _word = WordFor(v * 1e6, open);
            Temperature = open ? null : (_word >> 3) / 4.0;
            _bit = 15;
            NotifyValueChanged();
        }
        else if (!cs && !sck && _lastSck && _bit > 0) _bit--;

        (_lastCs, _lastSck) = (cs, sck);
        Request(context, SerialOut, cs ? LogicState.HighImpedance : ((_word >> _bit) & 1) != 0 ? LogicState.High : LogicState.Low,
            DelayFor(context));
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        (_lastCs, _lastSck, _word, _bit) = (true, false, 0, 15);
    }
}
