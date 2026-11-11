using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// A motor driver made of half-bridges, each an output switched to the motor supply or to ground as its input says,
/// while its enable is high, and let go (both switches off) while it's low; with a clamp diode from the output to each
/// rail so a motor's current has somewhere to go when the switches open.
/// <para>
/// The switches are bipolar in the real parts, so each has a resistance that stands for its saturation drop at an amp
/// or so (about 1.4 V across the two of a path in an L293D, 1.8 V in an L298); the inputs are TTL, high above 2.3 V.
/// </para>
/// </summary>
public abstract partial class HalfBridgeDriver : CircuitComponent
{
    private readonly double[] _previous;
    private bool _limited;
    private bool[] _high = [], _low = [];

    /// <summary>One half-bridge: its input, its enable and its output, as indices into <see cref="CircuitComponent.Terminals"/>.</summary>
    protected sealed record Half(int Input, int Enable, int Output);

    protected abstract IReadOnlyList<Half> Halves { get; }

    protected abstract Terminal MotorSupply { get; }

    protected abstract IReadOnlyList<Terminal> Grounds { get; }

    protected abstract IReadOnlyList<Terminal> Inputs { get; }

    protected HalfBridgeDriver(int halves) => _previous = new double[halves * 2];

    /// <summary>Each switch's resistance when on, in ohms.</summary>
    [ObservableProperty]
    public partial double OnResistance { get; set; } = 0.7;

    public override string DesignatorPrefix => "U";

    public override bool IsNonlinear => true;

    /// <summary>Whether each output is driven high, driven low or let go, at the last solve.</summary>
    public IReadOnlyList<string> Outputs => [.. Enumerable.Range(0, _high.Length).Select(i => _high[i] ? "high" : _low[i] ? "low" : "off")];

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var gnd = system.Node(Grounds[0]);
        var supply = system.Node(MotorSupply);
        foreach (var g in Grounds.Skip(1)) system.StampResistor(system.Node(g), gnd, 1e-3);
        foreach (var input in Inputs) system.StampResistor(system.Node(input), gnd, 100e3);

        var halves = Halves;
        if (_high.Length != halves.Count) (_high, _low) = (new bool[halves.Count], new bool[halves.Count]);
        _limited = false;
        var reference = system.IterationVoltage(gnd);
        bool High(int terminal) => system.IterationVoltage(system.Node(Terminals[terminal])) - reference > 2.3;

        for (var i = 0; i < halves.Count; i++)
        {
            var h = halves[i];
            var enabled = High(h.Enable);
            var on = High(h.Input);
            _high[i] = enabled && on;
            _low[i] = enabled && !on;
            var output = system.Node(Terminals[h.Output]);
            system.StampConductance(supply, output, 1.0 / (_high[i] ? OnResistance : 1e9));
            system.StampConductance(output, gnd, 1.0 / (_low[i] ? OnResistance : 1e9));
            Clamp(system, state, 2 * i, gnd, output);
            Clamp(system, state, (2 * i) + 1, output, supply);
        }
    }

    private void Clamp(MnaSystem system, SimulationState state, int index, int anode, int cathode)
    {
        const double saturation = 1e-12;
        var vt = state.ThermalVoltage;
        var raw = system.IterationVoltageAcross(anode, cathode);
        var limited = Diode.LimitJunctionVoltage(raw, _previous[index], vt, Junction.CriticalVoltage(saturation, vt));
        if (Math.Abs(limited - raw) > 1e-12) _limited = true;
        _previous[index] = limited;
        var (current, conductance) = Junction.Evaluate(limited, saturation, vt);
        system.StampNorton(anode, cathode, conductance, current - (conductance * limited));
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_limited;

    public override void ResetState()
    {
        Array.Clear(_previous);
        _limited = false;
    }
}

/// <summary>
/// An L293D: four half-bridges in a 16-pin DIP, enabled in pairs (EN1,2 and EN3,4), up to 600 mA each, with the clamp
/// diodes built in (the D). VCC1 is the logic supply, VCC2 the motor's; the four GND pins in the middle are the heatsink.
/// Two halves make an H-bridge for a DC motor, all four a bipolar stepper.
/// </summary>
public sealed partial class L293d : HalfBridgeDriver
{
    private readonly Terminal[] _pins = new Terminal[17];

    public L293d() : base(4)
    {
        Terminal Pin(int n, string name, TerminalType type) => _pins[n] = new Terminal($"p{n}", name, type, DipPackage.PinOffset(n, 16));
        Pin(1, "EN1,2", TerminalType.Input);
        Pin(2, "1A", TerminalType.Input);
        Pin(3, "1Y", TerminalType.Output);
        Pin(4, "GND", TerminalType.Ground);
        Pin(5, "GND", TerminalType.Ground);
        Pin(6, "2Y", TerminalType.Output);
        Pin(7, "2A", TerminalType.Input);
        Pin(8, "VCC2", TerminalType.Power);
        Pin(9, "EN3,4", TerminalType.Input);
        Pin(10, "3A", TerminalType.Input);
        Pin(11, "3Y", TerminalType.Output);
        Pin(12, "GND", TerminalType.Ground);
        Pin(13, "GND", TerminalType.Ground);
        Pin(14, "4Y", TerminalType.Output);
        Pin(15, "4A", TerminalType.Input);
        Pin(16, "VCC1", TerminalType.Power);
        Terminals = [.. _pins.Skip(1)];
        OnResistance = 0.9;
    }

    // Terminals index = pin - 1.
    protected override IReadOnlyList<Half> Halves { get; } = [new(1, 0, 2), new(6, 0, 5), new(9, 8, 10), new(14, 8, 13)];

    protected override Terminal MotorSupply => _pins[8];

    protected override IReadOnlyList<Terminal> Grounds => [_pins[4], _pins[5], _pins[12], _pins[13]];

    protected override IReadOnlyList<Terminal> Inputs => [_pins[1], _pins[2], _pins[7], _pins[9], _pins[10], _pins[15]];

    public override string ComponentType => "L293D";

    public override string ValueLabel => "L293D";

    /// <summary>The logic supply, VCC1.</summary>
    public Terminal LogicSupply => _pins[16];
}

/// <summary>
/// An L298N motor driver module, the red board with the heatsink: two full H-bridges (OUT1/OUT2 and OUT3/OUT4) for two DC
/// motors or one stepper, IN1-IN4 choosing the way each output goes and ENA and ENB enabling each bridge (a jumper on
/// them holds them high; PWM on them sets the speed), up to 2 A a channel from +12V, the clamp diodes on the board.
/// </summary>
public sealed partial class L298nModule : HalfBridgeDriver
{
    public L298nModule() : base(4)
    {
        Terminal T(string id, string name, TerminalType type, double x, double y) => new(id, name, type, new Point(x, y));
        Terminals =
        [
            T("vs", "+12V", TerminalType.Power, -60, -50),        // 0
            T("gnd", "GND", TerminalType.Ground, -60, -30),       // 1
            T("vss", "+5V", TerminalType.Power, -60, -10),        // 2
            T("ena", "ENA", TerminalType.Input, -60, 10),         // 3
            T("in1", "IN1", TerminalType.Input, -60, 30),         // 4
            T("in2", "IN2", TerminalType.Input, -60, 50),         // 5
            T("in3", "IN3", TerminalType.Input, -60, 70),         // 6
            T("in4", "IN4", TerminalType.Input, -60, 90),         // 7
            T("enb", "ENB", TerminalType.Input, -60, 110),        // 8
            T("out1", "OUT1", TerminalType.Output, 60, -30),      // 9
            T("out2", "OUT2", TerminalType.Output, 60, -10),      // 10
            T("out3", "OUT3", TerminalType.Output, 60, 50),       // 11
            T("out4", "OUT4", TerminalType.Output, 60, 70),       // 12
        ];
        OnResistance = 0.9;
    }

    protected override IReadOnlyList<Half> Halves { get; } = [new(4, 3, 9), new(5, 3, 10), new(6, 8, 11), new(7, 8, 12)];

    protected override Terminal MotorSupply => Terminals[0];

    protected override IReadOnlyList<Terminal> Grounds => [Terminals[1]];

    protected override IReadOnlyList<Terminal> Inputs => [Terminals[3], Terminals[4], Terminals[5], Terminals[6], Terminals[7], Terminals[8]];

    public override string ComponentType => "L298N Module";

    public override string ValueLabel => "L298N";
}
