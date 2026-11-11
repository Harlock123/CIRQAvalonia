using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>
/// The NE567 tone decoder: its OUTPUT (pin 8, open collector) goes low while a tone near its own frequency is at INPUT.
/// <para>
/// Its oscillator is real: pin 5 swings rail to rail, R1 from pin 5 to pin 6 charges and discharges C1 on pin 6, and the
/// swing turns round at 0.366 and 0.634 of the supply, which makes f0 = 1/(1.1·R1·C1) as the datasheet has it. So the
/// R and C on the canvas set the centre. The phase-locked loop is taken by what it does: the input (AC-coupled, biased
/// through 20 kΩ to half the supply) is timed cycle by cycle, and it locks once four cycles running are within the
/// bandwidth of f0 and above 25 mV. The bandwidth is a setting here (14 % at most, as the datasheet's largest input
/// gives); on the chip C2 on the loop filter sets it. C2 and C3 on pins 2 and 1 see the chip's 3.9 k and 4.7 k.
/// </para>
/// </summary>
public sealed partial class Ne567 : DigitalComponent
{
    private const int Oscillator = 0, Detect = 1;
    private bool _high;
    private double _lastOscRise = double.NaN, _oscPeriod;
    private double _lastUp = double.NaN, _period, _peak, _cyclePeak;
    private bool _wasPositive;
    private int _hits;
    private double _mean = double.NaN, _lastT;

    public Ne567()
    {
        PropagationDelay = 0;
        var pins = new Terminal[9];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 8));
        OutputFilter = Pin(1, "OUT FILT", TerminalType.Passive);
        LoopFilter = Pin(2, "LOOP FILT", TerminalType.Passive);
        Input = Pin(3, "IN", TerminalType.Input);
        Vcc = Pin(4, "V+", TerminalType.Power);
        TimingR = Pin(5, "TIM R", TerminalType.Output);
        TimingC = Pin(6, "TIM C", TerminalType.Input);
        Gnd = Pin(7, "GND", TerminalType.Ground);
        Output = Pin(8, "OUT", TerminalType.Output);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Input, TimingC], [TimingR, Output]);
    }

    public Terminal OutputFilter { get; }
    public Terminal LoopFilter { get; }
    public Terminal Input { get; }
    public Terminal Vcc { get; }
    public Terminal TimingR { get; }
    public Terminal TimingC { get; }
    public Terminal Gnd { get; }
    public Terminal Output { get; }

    /// <summary>How far either side of f0 it locks, as a percentage of f0 end to end.</summary>
    [ObservableProperty]
    public partial double BandwidthPercent { get; set; } = 14;

    public override string ComponentType => "NE567";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => Locked ? $"NE567 locked, {Cirq.Core.Units.SiPrefix.Format(CentreFrequency, "Hz")}" : "NE567";

    /// <summary>The oscillator's frequency, measured: f0.</summary>
    public double CentreFrequency => _oscPeriod > 0 ? 1 / _oscPeriod : 0;

    /// <summary>The input's frequency, measured over the last cycle.</summary>
    public double InputFrequency => _period > 0 ? 1 / _period : 0;

    public bool Locked => _hits >= 4;

    public override int InternalNodeCount => 1;

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (vcc, gnd, bias) = (system.Node(Vcc), system.Node(Gnd), system.InternalNode(this));
        system.StampResistor(vcc, bias, 10e3);
        system.StampResistor(bias, gnd, 10e3);
        system.StampResistor(system.Node(Input), bias, 20e3);
        system.StampResistor(system.Node(LoopFilter), bias, 3.9e3);
        system.StampResistor(system.Node(OutputFilter), bias, 4.7e3);
        system.StampResistor(system.Node(TimingC), gnd, 100e6);
        system.StampCurrentSource(vcc, gnd, 7e-3);

        var supply = Math.Max(system.IterationVoltage(vcc) - system.IterationVoltage(gnd), 0);
        system.StampTheveninSource(system.Branch(this, Oscillator), system.Node(TimingR), gnd, GetOutputState(Oscillator) == LogicState.High ? supply : 0, 50);
        system.StampTheveninSource(system.Branch(this, Detect), system.Node(Output), gnd, 0.2, GetOutputState(Detect) == LogicState.Low ? 10 : 1e9);
    }

    public override void EvaluateLogic(IDigitalContext context)
    {
        var t = context.Time;
        var ground = context.NodeVoltage(Gnd);
        var supply = context.NodeVoltage(Vcc) - ground;
        if (supply < 4)
        {
            context.Schedule(this, Oscillator, LogicState.Low, 0);
            context.Schedule(this, Detect, LogicState.HighImpedance, 0);
            return;
        }

        // The oscillator: turn round at 0.634 and 0.366 of the supply on the timing capacitor.
        var vc = context.NodeVoltage(TimingC) - ground;
        if (_high && vc > 0.634 * supply) _high = false;
        else if (!_high && vc < 0.366 * supply)
        {
            _high = true;
            if (!double.IsNaN(_lastOscRise)) _oscPeriod = t - _lastOscRise;
            _lastOscRise = t;
        }

        context.Schedule(this, Oscillator, _high ? LogicState.High : LogicState.Low, 0);

        // The input, timed one cycle at a time about its own average: the bias, wherever the filter capacitors have it.
        var v = context.NodeVoltage(Input) - ground;
        if (double.IsNaN(_mean)) _mean = v;
        else _mean += (v - _mean) * Math.Min(1, Math.Max(t - _lastT, 0) / 2e-3);
        _lastT = t;
        var x = v - _mean;
        _cyclePeak = Math.Max(_cyclePeak, Math.Abs(x));
        if (!_wasPositive && x > 5e-3)
        {
            _wasPositive = true;
            if (!double.IsNaN(_lastUp))
            {
                _period = t - _lastUp;
                _peak = _cyclePeak;
                var f0 = CentreFrequency;
                var inBand = f0 > 0 && _peak > 25e-3 && Math.Abs((InputFrequency / f0) - 1) < BandwidthPercent / 200;
                _hits = inBand ? Math.Min(_hits + 1, 8) : Math.Max(_hits - 2, 0);
            }

            _lastUp = t;
            _cyclePeak = 0;
        }
        else if (_wasPositive && x < -5e-3) _wasPositive = false;

        // No tone for a few periods: it lets go.
        if (CentreFrequency > 0 && !double.IsNaN(_lastUp) && t - _lastUp > 4 / CentreFrequency) _hits = 0;

        context.Schedule(this, Detect, Locked ? LogicState.Low : LogicState.HighImpedance, 0);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        (_high, _lastOscRise, _oscPeriod, _lastUp, _period, _peak, _cyclePeak, _wasPositive, _hits) = (true, double.NaN, 0, double.NaN, 0, 0, 0, false, 0);
        (_mean, _lastT) = (double.NaN, 0);
    }
}

/// <summary>The XR2206's output shape on pin 2.</summary>
public enum Xr2206Shape { Sine, Triangle }

/// <summary>
/// The XR2206 function generator: sine or triangle on pin 2, a square on pin 11.
/// <para>
/// Its timing is real. Pin 7 is held 3 V above ground, so the timing resistor from it sets a current I = 3 V / R, and the
/// chip pushes that current one way and then the other through the timing capacitor between pins 5 and 6, turning
/// round at ±0.75 V across it. That makes f = 1/(R·C), as the datasheet says, from the parts on the canvas. The
/// triangle is shaped into the sine (pins 13 and 14 shorted through about 200 Ω on the chip; <see cref="Shape"/> here),
/// <see cref="Amplitude"/> sets its size (pin 3's resistor on the chip, about 60 mV a kΩ), and it rides on BIAS, half
/// the supply. SYNCO, open collector, is low for the half of each cycle the triangle falls.
/// </para>
/// </summary>
public sealed partial class Xr2206 : CircuitComponent, IBreakpointSource
{
    private const double Turn = 0.75;
    private int _direction = 1;
    private double _lastVc, _lastTime, _slope;
    private double _vc;

    public Xr2206()
    {
        var pins = new Terminal[17];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 16));
        Pin(1, "AMSI", TerminalType.Input);
        Output = Pin(2, "STO", TerminalType.Output);
        Pin(3, "MO", TerminalType.Passive);
        Vcc = Pin(4, "VCC", TerminalType.Power);
        Capacitor1 = Pin(5, "TC1", TerminalType.Passive);
        Capacitor2 = Pin(6, "TC2", TerminalType.Passive);
        TimingResistor = Pin(7, "TR1", TerminalType.Passive);
        Pin(8, "TR2", TerminalType.Passive);
        Pin(9, "FSKI", TerminalType.Input);
        Bias = Pin(10, "BIAS", TerminalType.Output);
        Sync = Pin(11, "SYNCO", TerminalType.Output);
        Gnd = Pin(12, "GND", TerminalType.Ground);
        Pin(13, "WAVEA1", TerminalType.Passive);
        Pin(14, "WAVEA2", TerminalType.Passive);
        Pin(15, "SYMA1", TerminalType.Passive);
        Pin(16, "SYMA2", TerminalType.Passive);
        Terminals = [.. pins.Skip(1)];
    }

    public Terminal Output { get; }
    public Terminal Vcc { get; }
    public Terminal Capacitor1 { get; }
    public Terminal Capacitor2 { get; }
    public Terminal TimingResistor { get; }
    public Terminal Bias { get; }
    public Terminal Sync { get; }
    public Terminal Gnd { get; }

    [ObservableProperty]
    public partial Xr2206Shape Shape { get; set; } = Xr2206Shape.Sine;

    /// <summary>The output's peak, in volts.</summary>
    [ObservableProperty]
    [Operable("Amplitude", Minimum = 0, Maximum = 3, Unit = "V")]
    public partial double Amplitude { get; set; } = 1.0;

    public override string ComponentType => "XR2206";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => $"XR2206 {Shape.ToString().ToLowerInvariant()}";

    public override int VoltageSourceCount => 3;

    /// <summary>The voltage across the timing capacitor, TC1 − TC2, at the last accepted point.</summary>
    public double CapacitorVoltage => _vc;

    private double Shaped(double vc)
    {
        var x = Math.Clamp(vc / Turn, -1, 1);
        return Shape == Xr2206Shape.Sine ? Math.Sin(x * Math.PI / 2) : x;
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (gnd, vcc, c1, c2) = (system.Node(Gnd), system.Node(Vcc), system.Node(Capacitor1), system.Node(Capacitor2));
        var bias = system.Node(Bias);

        // TR1 held at 3 V; its current is what charges the capacitor.
        var timing = system.Branch(this, 0);
        system.StampVoltageSource(timing, system.Node(TimingResistor), gnd, 3.0);

        // The current I = −i(timing) pushed into TC1 and out of TC2, one way or the other; not in a DC solve, where the
        // capacitor is open and the current would have nowhere to go but the 10 MΩ.
        var push = state.IsTransient ? _direction : 0;
        system.Add(c1, timing, push);
        system.Add(c2, timing, -push);
        system.StampResistor(c1, bias, 10e6);
        system.StampResistor(c2, bias, 10e6);

        // BIAS at half the supply, through 50 Ω.
        var biasBranch = system.Branch(this, 1);
        system.StampVcvs(biasBranch, bias, gnd, vcc, gnd, 0.5);

        // The output, on BIAS, through 600 Ω.
        var vc = system.IterationVoltage(c1) - system.IterationVoltage(c2);
        system.StampTheveninSource(system.Branch(this, 2), system.Node(Output), bias, Amplitude * Shaped(vc), 600);

        system.StampConductance(system.Node(Sync), gnd, _direction < 0 ? 1.0 / 50 : 1e-9);
        system.StampCurrentSource(vcc, gnd, 12e-3);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        _vc = system.NodeVoltage(Capacitor1) - system.NodeVoltage(Capacitor2);
        var dt = state.Time - _lastTime;
        if (dt > 0) _slope = (_vc - _lastVc) / dt;
        (_lastVc, _lastTime) = (_vc, state.Time);
        if (_direction > 0 && _vc >= Turn) _direction = -1;
        else if (_direction < 0 && _vc <= -Turn) _direction = 1;
    }

    /// <summary>When the ramp will reach the turn, from how fast it's going: so the step lands on it.</summary>
    public double? NextBreakpointAfter(double time)
    {
        if (Math.Abs(_slope) < 1e-6) return null;
        var target = _direction > 0 ? Turn : -Turn;
        var dt = (target - _vc) / _slope;
        return dt > 1e-9 ? time + dt : null;
    }

    public override void ResetState()
    {
        (_direction, _lastVc, _lastTime, _slope, _vc) = (1, 0, 0, 0, 0);
    }
}

/// <summary>
/// The 74141 (and the Soviet K155ID1): BCD to decimal for a Nixie tube. Of its ten outputs, the one the BCD input
/// counts is pulled low, the others let go; they are open-collector transistors that stand off the 60 V or so left
/// across an unlit cathode, which is what lets a 5 V counter drive a tube on 170 V. Codes 10-15 light nothing.
/// </summary>
public sealed partial class Ic74141 : DigitalIc
{
    private static readonly int[] OutputPins = [16, 15, 8, 9, 13, 14, 11, 10, 1, 2];
    private readonly Terminal[] _outputs = new Terminal[10];

    public Ic74141() : base(16)
    {
        PropagationDelay = 50e-9;
        var pins = new Terminal[17];
        Terminal Pin(int n, string name, TerminalType type) => pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 16));
        for (var d = 0; d < 10; d++) _outputs[d] = Pin(OutputPins[d], d.ToString(System.Globalization.CultureInfo.InvariantCulture), TerminalType.Output);
        A = Pin(3, "A", TerminalType.Input);
        D = Pin(4, "D", TerminalType.Input);
        Vcc = Pin(5, "VCC", TerminalType.Power);
        B = Pin(6, "B", TerminalType.Input);
        C = Pin(7, "C", TerminalType.Input);
        Gnd = Pin(12, "GND", TerminalType.Ground);
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([A, B, C, D], _outputs);
    }

    public Terminal A { get; }
    public Terminal B { get; }
    public Terminal C { get; }
    public Terminal D { get; }

    public IReadOnlyList<Terminal> Outputs => _outputs;

    public override string PartNumber => "74141";

    public override bool LabelsPins => true;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var code = (context.ReadInput(A, Levels).IsHigh() ? 1 : 0) | (context.ReadInput(B, Levels).IsHigh() ? 2 : 0)
                   | (context.ReadInput(C, Levels).IsHigh() ? 4 : 0) | (context.ReadInput(D, Levels).IsHigh() ? 8 : 0);
        for (var d = 0; d < 10; d++) Request(context, d, d == code ? LogicState.Low : LogicState.HighImpedance, DelayFor(context));
    }
}

/// <summary>
/// A Nixie tube (an IN-14, say): ten cathodes shaped as digits in neon, and a mesh anode in front. A cathode glows once
/// the voltage from the anode to it strikes the gas (170 V), and keeps glowing down to the maintaining voltage (140 V);
/// above that it takes current through the discharge's few kΩ, so the anode resistor sets how bright. The digit drawn
/// is the cathode with current in it.
/// </summary>
public sealed partial class NixieTube : CircuitComponent
{
    private readonly bool[] _glowing = new bool[10];
    private readonly double[] _current = new double[10];

    public NixieTube()
    {
        var terminals = new List<Terminal> { new("a", "A", TerminalType.Passive, new Point(0, -60)) };
        for (var d = 0; d < 10; d++) terminals.Add(new Terminal($"k{d}", $"K{d}", TerminalType.Passive, new Point(-45 + (d * 10), 60)));
        Terminals = terminals;
    }

    public Terminal Anode => Terminals[0];

    /// <summary>A digit's cathode.</summary>
    public Terminal Cathode(int digit) => Terminals[1 + digit];

    [ObservableProperty]
    public partial double StrikeVoltage { get; set; } = 170;

    [ObservableProperty]
    public partial double MaintainingVoltage { get; set; } = 140;

    /// <summary>The discharge's resistance once struck, in ohms.</summary>
    [ObservableProperty]
    public partial double DischargeResistance { get; set; } = 4e3;

    public override string ComponentType => "Nixie Tube";

    public override string DesignatorPrefix => "V";

    public override string ValueLabel => Digit is { } d ? $"IN-14 showing {d}" : "IN-14";

    public override bool IsNonlinear => true;

    /// <summary>The digit lit, or null when none is (or more than one, which a tube shouldn't be asked to do).</summary>
    public int? Digit
    {
        get
        {
            var lit = Enumerable.Range(0, 10).Where(d => _current[d] > 0.2e-3).ToList();
            return lit.Count == 1 ? lit[0] : null;
        }
    }

    /// <summary>How brightly a digit glows, 0 to 1: its current against 2.5 mA.</summary>
    public double Glow(int digit) => Math.Clamp(_current[digit] / 2.5e-3, 0, 1);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var anode = system.Node(Anode);
        const double softness = 1.0;
        for (var d = 0; d < 10; d++)
        {
            var cathode = system.Node(Cathode(d));
            system.StampResistor(anode, cathode, 100e6);
            var threshold = _glowing[d] ? MaintainingVoltage : StrikeVoltage;
            var v = system.IterationVoltage(anode) - system.IterationVoltage(cathode);
            var x = Math.Clamp((v - threshold) / softness, -60, 60);
            var softplus = x > 30 ? x : Math.Log(1 + Math.Exp(x));
            var current = softness * softplus / DischargeResistance;
            var g = Math.Max(1 / (1 + Math.Exp(-x)) / DischargeResistance, 1e-12);
            system.StampNorton(anode, cathode, g, current - (g * v));
        }
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        for (var d = 0; d < 10; d++)
        {
            var v = system.NodeVoltage(Anode) - system.NodeVoltage(Cathode(d));
            var threshold = _glowing[d] ? MaintainingVoltage : StrikeVoltage;
            var x = Math.Clamp(v - threshold, -60, 60);
            _current[d] = (x > 30 ? x : Math.Log(1 + Math.Exp(x))) / DischargeResistance;
            _glowing[d] = _glowing[d] ? v > MaintainingVoltage : v > StrikeVoltage;
        }
    }

    public override void ResetState()
    {
        Array.Clear(_glowing);
        Array.Clear(_current);
    }
}

/// <summary>
/// A 12AX7 (ECC83) triode, half of one: plate, grid and cathode, on Norman Koren's model of the plate current,
/// E1 = (Vpk/Kp)·ln(1 + e^(Kp·(1/μ + Vgk/√(Kvb + Vpk²)))) and Ip = 2·E1^Ex/Kg1, with the 12AX7's μ = 100, Ex = 1.4,
/// Kg1 = 1060, Kp = 600 and Kvb = 300. The grid draws current once it's taken above the cathode, through about 2 kΩ.
/// The heater isn't modelled: it's taken as warm.
/// </summary>
public sealed partial class Triode : CircuitComponent
{
    public Triode()
    {
        Plate = new Terminal("p", "P", TerminalType.Passive, new Point(0, -40));
        Grid = new Terminal("g", "G", TerminalType.Input, new Point(-40, 0));
        Cathode = new Terminal("k", "K", TerminalType.Passive, new Point(0, 40));
        Terminals = [Plate, Grid, Cathode];
    }

    public Terminal Plate { get; }
    public Terminal Grid { get; }
    public Terminal Cathode { get; }

    [ObservableProperty]
    public partial double Mu { get; set; } = 100;

    [ObservableProperty]
    public partial double Ex { get; set; } = 1.4;

    [ObservableProperty]
    public partial double Kg1 { get; set; } = 1060;

    [ObservableProperty]
    public partial double Kp { get; set; } = 600;

    [ObservableProperty]
    public partial double Kvb { get; set; } = 300;

    public override string ComponentType => "Triode";

    public override string DesignatorPrefix => "V";

    public override string ValueLabel => "12AX7";

    public override bool IsNonlinear => true;

    /// <summary>The plate current at the last accepted point, in amps.</summary>
    public double PlateCurrent { get; private set; }

    /// <summary>The plate current at a plate and a grid voltage, both from the cathode: Koren's equation.</summary>
    public double Current(double vpk, double vgk)
    {
        if (vpk <= 0) return 0;
        var x = Kp * ((1 / Mu) + (vgk / Math.Sqrt(Kvb + (vpk * vpk))));
        var ln = x > 30 ? x : Math.Log(1 + Math.Exp(x));
        var e1 = vpk / Kp * ln;
        return e1 <= 0 ? 0 : 2 * Math.Pow(e1, Ex) / Kg1;
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (p, g, k) = (system.Node(Plate), system.Node(Grid), system.Node(Cathode));
        var vpk = Math.Clamp(system.IterationVoltage(p) - system.IterationVoltage(k), -50, 1000);
        var vgk = Math.Clamp(system.IterationVoltage(g) - system.IterationVoltage(k), -200, 50);
        const double h = 1e-3;
        var ip = Current(vpk, vgk);
        var gp = Math.Max((Current(vpk + h, vgk) - Current(vpk - h, vgk)) / (2 * h), 1e-9);
        var gm = (Current(vpk, vgk + h) - Current(vpk, vgk - h)) / (2 * h);
        system.StampConductance(p, k, gp);
        system.StampVccs(p, k, g, k, gm);
        system.StampCurrentSource(p, k, ip - (gp * vpk) - (gm * vgk));

        // Grid current above the cathode, softly: (0.1·ln(1 + e^(vgk/0.1))) / 2 kΩ.
        var x = Math.Clamp(vgk / 0.1, -60, 60);
        var ig = 0.1 * (x > 30 ? x : Math.Log(1 + Math.Exp(x))) / 2e3;
        var gg = Math.Max(1 / (1 + Math.Exp(-x)) / 2e3, 1e-12);
        system.StampNorton(g, k, gg, ig - (gg * vgk));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state) =>
        PlateCurrent = Current(system.NodeVoltage(Plate) - system.NodeVoltage(Cathode), system.NodeVoltage(Grid) - system.NodeVoltage(Cathode));
}
