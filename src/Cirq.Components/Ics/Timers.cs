using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Components.Ics;

/// <summary>
/// The ICM7555 (or TLC555): the 555 in CMOS. The same pins and the same thresholds, so the same R and C give the same
/// frequency, but its output swings to within a few tens of millivolts of both rails rather than dropping 1.7 V, it
/// runs from 2 V up, and it takes microamps rather than milliamps.
/// </summary>
public sealed partial class Cmos7555 : Ne555
{
    public Cmos7555()
    {
        OutputSaturationDrop = 0.05;
        Levels = LogicLevels.Cmos5V with { OutputResistance = 30.0 };
    }

    public override string ComponentType => "7555";

    public override string ValueLabel => "ICM7555";
}

/// <summary>
/// The NE556: two 555s in a 14-pin DIP, sharing VCC (pin 14) and GND (pin 7), each with its own divider, comparators,
/// latch, discharge and output. Timer 1 is DISCH 1, THRES 2, CTRL 3, RESET 4, OUT 5 and TRIG 6; timer 2 TRIG 8, OUT 9,
/// RESET 10, CTRL 11, THRES 12 and DISCH 13. Each behaves exactly as an <see cref="Ne555"/>.
/// </summary>
public sealed partial class Ne556 : DigitalComponent
{
    private const double Divider = Ne555.DividerResistance;

    private readonly bool[] _latched = new bool[2];
    private readonly (Terminal Discharge, Terminal Threshold, Terminal Control, Terminal Reset, Terminal Out, Terminal Trigger)[] _timer;

    public Ne556()
    {
        PropagationDelay = 100e-9;
        Levels = LogicLevels.Ttl with { Vih = 2.0, Vil = 0.8, Voh = 3.3, Vol = 0.1, OutputResistance = 10.0 };
        var pins = new Terminal[15];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 14));
        _timer =
        [
            (Pin(1, "DISCH1", TerminalType.Bidirectional), Pin(2, "THRES1", TerminalType.Input), Pin(3, "CTRL1", TerminalType.Passive),
                Pin(4, "RESET1", TerminalType.Input), Pin(5, "OUT1", TerminalType.Output), Pin(6, "TRIG1", TerminalType.Input)),
            (Pin(13, "DISCH2", TerminalType.Bidirectional), Pin(12, "THRES2", TerminalType.Input), Pin(11, "CTRL2", TerminalType.Passive),
                Pin(10, "RESET2", TerminalType.Input), Pin(9, "OUT2", TerminalType.Output), Pin(8, "TRIG2", TerminalType.Input)),
        ];
        Gnd = Pin(7, "GND", TerminalType.Ground);
        Vcc = Pin(14, "VCC", TerminalType.Power);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _timer.SelectMany(t => new[] { t.Trigger, t.Threshold, t.Reset })], [_timer[0].Out, _timer[1].Out]);
    }

    public Terminal Gnd { get; }

    public Terminal Vcc { get; }

    public override string ComponentType => "NE556";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => "NE556";

    /// <summary>Whether a timer's output is high.</summary>
    public bool IsOutputHigh(int timer) => _latched[timer];

    public override int InternalNodeCount => 2;

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var vcc = system.Node(Vcc);
        var gnd = system.Node(Gnd);
        var supply = system.IterationVoltage(vcc) - system.IterationVoltage(gnd);
        for (var i = 0; i < 2; i++)
        {
            var t = _timer[i];
            var ctrl = system.Node(t.Control);
            var lower = system.InternalNode(this, i);
            system.StampConductance(vcc, ctrl, 1.0 / Divider);
            system.StampConductance(ctrl, lower, 1.0 / Divider);
            system.StampConductance(lower, gnd, 1.0 / Divider);
            foreach (var input in new[] { t.Threshold, t.Trigger, t.Reset })
                system.StampConductance(system.Node(input), gnd, 1.0 / Levels.InputResistance);
            system.StampConductance(system.Node(t.Discharge), gnd, _latched[i] ? 1e-9 : 0.1);
            var high = Math.Max(supply - 1.7, 0);
            var driven = GetOutputState(i) switch { LogicState.High => high, LogicState.Low => Levels.Vol, _ => high * 0.5 };
            system.StampTheveninSource(system.Branch(this, i), system.Node(t.Out), gnd, driven, Levels.OutputResistance);
        }
    }

    public override void EvaluateLogic(IDigitalContext context)
    {
        var ground = context.NodeVoltage(Gnd);
        var powered = context.NodeVoltage(Vcc) - ground >= 2.0;
        for (var i = 0; i < 2; i++)
        {
            var t = _timer[i];
            if (!powered)
            {
                _latched[i] = false;
                context.Schedule(this, i, LogicState.HighImpedance, DelayFor(context));
                continue;
            }

            var upper = context.NodeVoltage(t.Control) - ground;
            if (context.NodeVoltage(t.Reset) - ground < 0.7) _latched[i] = false;
            else
            {
                if (context.NodeVoltage(t.Threshold) - ground > upper) _latched[i] = false;
                if (context.NodeVoltage(t.Trigger) - ground < upper * 0.5) _latched[i] = true;
            }

            context.Schedule(this, i, _latched[i] ? LogicState.High : LogicState.Low, DelayFor(context));
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_latched);
    }
}

/// <summary>
/// The LM3914 dot/bar display driver: ten comparators along a divider of ten 1 kΩ resistors from RLO to RHI, each
/// lighting its LED when SIG is above its tap (LED1 at a tenth of the way up, LED10 at RHI), all of those below in bar
/// mode (MODE to V+) or only the top one in dot mode (MODE left open).
/// <para>
/// Its 1.25 V reference sits between REFOUT and REFADJ, and the LED outputs are constant-current sinks of about ten
/// times the current drawn from REFOUT, so the resistor from REFOUT (usually to RHI and on to ground) sets both the
/// full scale and the LED brightness, as the datasheet describes: 1.25 V across 1.2 kΩ is about 1 mA, so 10 mA an LED.
/// </para>
/// </summary>
public sealed partial class Lm3914 : CircuitComponent
{
    private const double Step = 1e3;
    private readonly Terminal[] _leds = new Terminal[10];
    private readonly bool[] _on = new bool[10];
    private double _referenceCurrent;

    public Lm3914()
    {
        var pins = new Terminal[19];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 18));
        _leds[0] = Pin(1, "LED1", TerminalType.Passive);
        Negative = Pin(2, "V-", TerminalType.Ground);
        Positive = Pin(3, "V+", TerminalType.Power);
        Low = Pin(4, "RLO", TerminalType.Passive);
        Signal = Pin(5, "SIG", TerminalType.Input);
        High = Pin(6, "RHI", TerminalType.Passive);
        ReferenceOut = Pin(7, "REFOUT", TerminalType.Output);
        ReferenceAdjust = Pin(8, "REFADJ", TerminalType.Passive);
        Mode = Pin(9, "MODE", TerminalType.Input);
        _leds[9] = Pin(10, "LED10", TerminalType.Passive);
        for (var led = 9; led >= 2; led--) _leds[led - 1] = Pin(20 - led, $"LED{led}", TerminalType.Passive);
        Terminals = [.. pins.Skip(1)];
    }

    public Terminal Negative { get; }
    public Terminal Positive { get; }
    public Terminal Low { get; }
    public Terminal Signal { get; }
    public Terminal High { get; }
    public Terminal ReferenceOut { get; }
    public Terminal ReferenceAdjust { get; }
    public Terminal Mode { get; }

    public IReadOnlyList<Terminal> Leds => _leds;

    public override string ComponentType => "LM3914";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => $"LM3914 {_on.Count(o => o)}/10";

    public override bool IsNonlinear => true;

    public override int InternalNodeCount => 9;

    public override int VoltageSourceCount => 1;

    /// <summary>Which LED outputs are sinking current, at the last solve.</summary>
    public IReadOnlyList<bool> Lit => _on;

    /// <summary>What each lit LED is given, in amps: ten times the reference's load.</summary>
    public double LedCurrent => 10 * Math.Max(_referenceCurrent, 0);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var minus = system.Node(Negative);
        var plus = system.Node(Positive);

        // The reference: REFOUT 1.25 V above REFADJ.
        var reference = system.Branch(this);
        system.StampVoltageSource(reference, system.Node(ReferenceOut), system.Node(ReferenceAdjust), 1.25);
        _referenceCurrent = -system.IterationValue(reference);

        // The ladder.
        var nodes = new int[11];
        nodes[0] = system.Node(Low);
        nodes[10] = system.Node(High);
        for (var i = 1; i < 10; i++) nodes[i] = system.InternalNode(this, i - 1);
        for (var i = 0; i < 10; i++) system.StampResistor(nodes[i], nodes[i + 1], Step);

        // SIG and MODE are high-impedance inputs; the chip itself draws a few milliamps.
        system.StampResistor(system.Node(Signal), minus, 10e6);
        system.StampResistor(system.Node(Mode), minus, 10e6);
        system.StampCurrentSource(plus, minus, 3e-3);

        var signal = system.IterationVoltage(system.Node(Signal));
        var bar = system.IterationVoltage(system.Node(Mode)) > system.IterationVoltage(plus) - 1.0;
        var top = -1;
        for (var i = 0; i < 10; i++)
        {
            var tap = system.IterationVoltage(nodes[i + 1]);
            var hysteresis = _on[i] ? -3e-3 : 3e-3;
            if (signal > tap + hysteresis) top = i;
        }

        var current = LedCurrent;
        for (var i = 0; i < 10; i++)
        {
            _on[i] = bar ? i <= top : i == top;
            var pin = system.Node(_leds[i]);
            if (!_on[i] || current < 1e-6)
            {
                system.StampConductance(pin, minus, 1e-9);
                continue;
            }

            // A current sink with a little compliance: I·(1 − e^(−v/0.15)), so it lets go as the pin nears V−.
            var v = system.IterationVoltage(pin) - system.IterationVoltage(minus);
            var e = Math.Exp(-Math.Max(v, -1) / 0.15);
            var i0 = current * (1 - e);
            var g = Math.Max(current * e / 0.15, 1e-9);
            system.StampNorton(pin, minus, g, i0 - (g * v));
        }
    }
}
