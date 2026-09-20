using Cirq.Components.Nonlinear;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>What the bridge is doing with its outputs.</summary>
public enum BridgeState
{
    /// <summary>Everything off. The motor is free to spin down on its own.</summary>
    Coasting,

    /// <summary>OUT1 to the supply, OUT2 to ground.</summary>
    Forward,

    /// <summary>The other way round.</summary>
    Reverse,

    /// <summary>Both outputs tied to the same rail, shorting the motor to stop it quickly.</summary>
    Braking,

    /// <summary>Both halves of one leg on at once — a short across the supply, not a drive.</summary>
    ShootThrough,
}

/// <summary>
/// An H-bridge: four switches that let a motor be driven either way.
/// <para>
/// A single transistor can only turn a motor on. Getting it to run backwards means being able to
/// put the supply on either terminal and ground on the other, and the only way to do that is four
/// switches in an H around the motor — which is where the name comes from and why there is no
/// simpler arrangement.
/// </para>
/// <para>
/// The two inputs choose what happens, and it is worth knowing all four combinations rather than
/// just the two that drive: one high and one low is forward, the other way round is reverse,
/// <b>both low is a brake</b> — the motor is shorted to itself, its own back-EMF drags it to a
/// halt, and that is emphatically not the same as switching off — and disabling the bridge is
/// <b>coasting</b>, where it simply spins down. A driver that stops dead when you cut the drive is
/// braking, and one that does not is coasting; people usually want one and wire up the other.
/// </para>
/// <para>
/// <b>Shoot-through</b> is the failure this part exists to let you make safely. Turn the top and
/// bottom of the same leg on together and there is a path from the supply to ground through two
/// transistors and no motor at all — a dead short, limited only by how good the switches are.
/// A real driver has dead time built in to stop it; a bridge built from four of the MOSFETs in
/// this library does not, and this reports it rather than quietly melting.
/// </para>
/// <para>
/// The four <b>body diodes</b> are modelled too, because a motor is an inductance and the moment
/// the switches open its current has to go somewhere. Without them the outputs would fly to
/// whatever voltage the solver needed to stop the current; with them the current freewheels round
/// the bridge as it really does.
/// </para>
/// </summary>
public sealed partial class HBridge : CircuitComponent
{
    private const double DiodeSaturation = 1e-12;

    private readonly double[] _previousDiode = new double[4];
    private bool _limitedThisIteration;

    public HBridge()
    {
        LogicSupply = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-40, -40));
        Enable = new Terminal("en", "EN", TerminalType.Input, new Point(-40, -14));
        Input1 = new Terminal("in1", "IN1", TerminalType.Input, new Point(-40, 4));
        Input2 = new Terminal("in2", "IN2", TerminalType.Input, new Point(-40, 22));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-40, 44));

        MotorSupply = new Terminal("vm", "VM", TerminalType.Power, new Point(40, -40));
        Output1 = new Terminal("out1", "OUT1", TerminalType.Passive, new Point(40, -8));
        Output2 = new Terminal("out2", "OUT2", TerminalType.Passive, new Point(40, 16));

        Terminals = [LogicSupply, MotorSupply, Enable, Input1, Input2, Output1, Output2, Gnd];
    }

    /// <summary>Logic supply, which the input thresholds are referred to.</summary>
    public Terminal LogicSupply { get; }

    /// <summary>
    /// Motor supply, separate from the logic one — the whole reason a driver exists rather than
    /// wiring the motor to a pin.
    /// </summary>
    public Terminal MotorSupply { get; }

    /// <summary>Low releases every switch, so the motor coasts. This is where PWM goes.</summary>
    public Terminal Enable { get; }

    public Terminal Input1 { get; }

    public Terminal Input2 { get; }

    public Terminal Output1 { get; }

    public Terminal Output2 { get; }

    public Terminal Gnd { get; }

    /// <summary>Resistance of one switch when it is on, in ohms.</summary>
    [ObservableProperty]
    public partial double OnResistance { get; set; } = 1.2;

    /// <summary>Resistance of one switch when it is off, in ohms.</summary>
    [ObservableProperty]
    public partial double OffResistance { get; set; } = 1e9;

    /// <summary>Resistance the inputs present, so an unwired one reads low rather than floating.</summary>
    [ObservableProperty]
    public partial double InputResistance { get; set; } = 1e6;

    /// <summary>
    /// Whether the driver refuses to turn both halves of a leg on at once. Real parts decode the
    /// inputs so that it cannot happen; turning this off is how you find out why they bother.
    /// </summary>
    [ObservableProperty]
    public partial bool PreventShootThrough { get; set; } = true;

    public override string ComponentType => "H-Bridge";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => State switch
    {
        BridgeState.Forward => "forward",
        BridgeState.Reverse => "reverse",
        BridgeState.Braking => "braking",
        BridgeState.ShootThrough => "shoot-through",
        _ => "coasting",
    };

    public override bool IsNonlinear => true;

    /// <summary>What the bridge is doing at the last solved point.</summary>
    public BridgeState State { get; private set; } = BridgeState.Coasting;

    /// <summary>Current out of OUT1, in amps. Positive is out of the bridge and into the motor.</summary>
    public double OutputCurrent { get; private set; }

    /// <summary>Power being thrown away in the switches themselves, in watts.</summary>
    public double SwitchDissipation { get; private set; }

    // Which of the four switches are on, in the order: high-1, low-1, high-2, low-2.
    private bool _high1, _low1, _high2, _low2;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var gnd = system.Node(Gnd);
        var supply = system.Node(MotorSupply);
        var out1 = system.Node(Output1);
        var out2 = system.Node(Output2);

        _limitedThisIteration = false;

        // The inputs have to have something on them, or an unwired one is a floating node and the
        // matrix is singular. A real driver's inputs leak to ground too, so an input left off
        // reads low rather than reading whatever the solver felt like.
        var leak = Math.Max(InputResistance, 1.0);
        system.StampResistor(system.Node(Enable), gnd, leak);
        system.StampResistor(system.Node(Input1), gnd, leak);
        system.StampResistor(system.Node(Input2), gnd, leak);

        Decide(system);

        Switch(system, supply, out1, _high1);
        Switch(system, out1, gnd, _low1);
        Switch(system, supply, out2, _high2);
        Switch(system, out2, gnd, _low2);

        // The body diodes, which are what the motor's current freewheels through when the
        // switches open. Anode at the output for the low-side pair, cathode at the output for the
        // high-side pair, which is the way round a bridge of N-channel devices has them.
        Clamp(system, state, 0, gnd, out1);
        Clamp(system, state, 1, out1, supply);
        Clamp(system, state, 2, gnd, out2);
        Clamp(system, state, 3, out2, supply);
    }

    private void Switch(MnaSystem system, int from, int to, bool on) =>
        system.StampConductance(from, to, 1.0 / Math.Max(on ? OnResistance : OffResistance, 1e-9));

    private void Clamp(MnaSystem system, SimulationState state, int index, int anode, int cathode)
    {
        var vt = state.ThermalVoltage;
        var raw = system.IterationVoltageAcross(anode, cathode);

        var limited = Diode.LimitJunctionVoltage(
            raw, _previousDiode[index], vt, Junction.CriticalVoltage(DiodeSaturation, vt));

        if (Math.Abs(limited - raw) > 1e-12) _limitedThisIteration = true;
        _previousDiode[index] = limited;

        var (current, conductance) = Junction.Evaluate(limited, DiodeSaturation, vt);

        system.StampNorton(anode, cathode, conductance, current - (conductance * limited));
    }

    public override bool HasConverged(MnaSystem system, SimulationState state) => !_limitedThisIteration;

    /// <summary>Reads the three inputs and works out which switches that asks for.</summary>
    private void Decide(MnaSystem system)
    {
        var reference = system.IterationVoltage(system.Node(Gnd));
        var logic = system.IterationVoltage(system.Node(LogicSupply)) - reference;

        // Half the logic supply, which is where a CMOS input decides.
        var threshold = Math.Max(logic, 1.0) * 0.5;

        bool High(Terminal pin) =>
            system.IterationVoltage(system.Node(pin)) - reference > threshold;

        var enabled = High(Enable) && logic > 1.5;
        var in1 = High(Input1);
        var in2 = High(Input2);

        if (!enabled)
        {
            _high1 = _low1 = _high2 = _low2 = false;
            State = BridgeState.Coasting;
            return;
        }

        // Checked before the brake case, not after it. A driver decodes both-inputs-high into a
        // brake; a bridge wired from discrete transistors, where each input drives one half of a
        // leg directly, turns the top and bottom of both legs on at once instead. That is the
        // case this models, and putting it after the brake branch made it unreachable.
        if (!PreventShootThrough && in1 && in2)
        {
            _high1 = _low1 = _high2 = _low2 = true;
            State = BridgeState.ShootThrough;
            return;
        }

        if (in1 == in2)
        {
            // Both inputs the same ties the outputs together: the motor is shorted to itself and
            // its own back-EMF stops it. Both high brakes to the supply, both low to ground, and
            // either is a brake rather than an off.
            _high1 = _high2 = in1;
            _low1 = _low2 = !in1;
            State = BridgeState.Braking;
            return;
        }

        _high1 = in1;
        _low1 = !in1;
        _high2 = in2;
        _low2 = !in2;

        State = in2 ? BridgeState.Reverse : BridgeState.Forward;
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var v1 = system.NodeVoltage(Output1);
        var v2 = system.NodeVoltage(Output2);
        var supply = system.NodeVoltage(MotorSupply);
        var gnd = system.NodeVoltage(Gnd);

        // Whatever is flowing out of OUT1 has to have come through one of its two switches.
        var throughHigh = _high1 ? (supply - v1) / Math.Max(OnResistance, 1e-9) : 0.0;
        var throughLow = _low1 ? (v1 - gnd) / Math.Max(OnResistance, 1e-9) : 0.0;

        OutputCurrent = throughHigh - throughLow;

        var magnitude = Math.Abs(OutputCurrent);
        var switchesInPath = State is BridgeState.Forward or BridgeState.Reverse ? 2 : 1;

        SwitchDissipation = magnitude * magnitude * OnResistance * switchesInPath;

        // Re-reading the decision at the solved point rather than the iteration point, so the
        // state reported is the one the answer was actually produced with.
        if (State == BridgeState.ShootThrough)
        {
            var shortCurrent = (supply - gnd) / Math.Max(OnResistance * 2, 1e-9);
            SwitchDissipation = shortCurrent * shortCurrent * OnResistance * 2;
        }
    }

    /// <summary>What is wrong with how the bridge is being driven, if anything.</summary>
    public IReadOnlyList<string> Violations
    {
        get
        {
            List<string> found = [];

            if (State == BridgeState.ShootThrough)
            {
                found.Add($"both halves of a leg are on at once — that is a short across the motor " +
                          $"supply through two switches, throwing away {SwitchDissipation:0.#} W " +
                          "and doing nothing whatsoever to the motor");
            }

            return found;
        }
    }

    public override void ResetState()
    {
        State = BridgeState.Coasting;
        OutputCurrent = 0;
        SwitchDissipation = 0;

        _high1 = _low1 = _high2 = _low2 = false;

        Array.Clear(_previousDiode);
        _limitedThisIteration = false;
    }
}
