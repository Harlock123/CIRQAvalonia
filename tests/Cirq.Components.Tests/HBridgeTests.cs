using Cirq.Components.Electromechanical;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The point of an H-bridge is that it can put the supply on either end of the motor, so these
/// check all four input combinations rather than only the two that drive.
/// </summary>
public class HBridgeTests
{
    private sealed record Rig(
        CircuitSimulator Sim,
        HBridge Bridge,
        Resistor Load,
        DcVoltageSource Enable,
        DcVoltageSource In1,
        DcVoltageSource In2);

    /// <summary>
    /// A resistor stands in for the motor here: it makes the current easy to reason about and
    /// keeps the inductance out of tests that are about the switches.
    /// </summary>
    private static Rig Build(double motorSupply = 12.0, double load = 12.0)
    {
        var circuit = new Circuit();

        var logic = circuit.Add(new DcVoltageSource(5.0));
        var motor = circuit.Add(new DcVoltageSource(motorSupply));
        var enable = circuit.Add(new DcVoltageSource(0.0));
        var in1 = circuit.Add(new DcVoltageSource(0.0));
        var in2 = circuit.Add(new DcVoltageSource(0.0));

        var bridge = circuit.Add(new HBridge());
        var resistor = circuit.Add(new Resistor(load));
        var gnd = circuit.Add(new Ground());

        foreach (var source in new[] { logic, motor, enable, in1, in2 })
            circuit.Connect(source.Negative, gnd.Pin);

        circuit.Connect(bridge.LogicSupply, logic.Positive);
        circuit.Connect(bridge.MotorSupply, motor.Positive);
        circuit.Connect(bridge.Gnd, gnd.Pin);
        circuit.Connect(bridge.Enable, enable.Positive);
        circuit.Connect(bridge.Input1, in1.Positive);
        circuit.Connect(bridge.Input2, in2.Positive);

        circuit.Connect(bridge.Output1, resistor.A);
        circuit.Connect(resistor.B, bridge.Output2);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-5 });
        sim.Reset();
        sim.SolveOperatingPoint();

        return new Rig(sim, bridge, resistor, enable, in1, in2);
    }

    private static void Drive(Rig rig, bool enable, bool in1, bool in2)
    {
        rig.Enable.Voltage = enable ? 5.0 : 0.0;
        rig.In1.Voltage = in1 ? 5.0 : 0.0;
        rig.In2.Voltage = in2 ? 5.0 : 0.0;
        rig.Sim.Run(1e-3);
    }

    /// <summary>
    /// One input high and the other low puts the supply across the motor one way — less what the
    /// two switches in the path keep for themselves, which at 1.2 Ω each against a 12 Ω motor is
    /// a sixth of it. That loss is exactly why a driver's on-resistance is the number on the front
    /// of its datasheet.
    /// </summary>
    [Fact]
    public void OneWayRoundDrivesForward()
    {
        var rig = Build();
        Drive(rig, enable: true, in1: true, in2: false);

        Assert.Equal(BridgeState.Forward, rig.Bridge.State);

        var across = rig.Sim.NodeVoltage(rig.Bridge.Output1) - rig.Sim.NodeVoltage(rig.Bridge.Output2);
        var expected = 12.0 * 12.0 / (12.0 + (2 * rig.Bridge.OnResistance));

        Assert.Equal(expected, across, 0.2);
    }

    /// <summary>And the other way round reverses it, which is the whole reason the part exists.</summary>
    [Fact]
    public void TheOtherWayRoundDrivesBackwards()
    {
        var rig = Build();
        Drive(rig, enable: true, in1: false, in2: true);

        Assert.Equal(BridgeState.Reverse, rig.Bridge.State);

        var across = rig.Sim.NodeVoltage(rig.Bridge.Output1) - rig.Sim.NodeVoltage(rig.Bridge.Output2);
        var expected = -12.0 * 12.0 / (12.0 + (2 * rig.Bridge.OnResistance));

        Assert.Equal(expected, across, 0.2);
    }

    /// <summary>The current reverses with it, which is what the motor actually responds to.</summary>
    [Fact]
    public void TheCurrentReversesWithTheDirection()
    {
        var rig = Build();

        Drive(rig, enable: true, in1: true, in2: false);
        var forward = rig.Bridge.OutputCurrent;

        Drive(rig, enable: true, in1: false, in2: true);
        var reverse = rig.Bridge.OutputCurrent;

        Assert.True(forward > 0.5);
        Assert.True(reverse < -0.5);
        Assert.Equal(forward, -reverse, Math.Abs(forward) * 0.05);
    }

    /// <summary>
    /// Both inputs the same is a brake, not an off: the outputs are tied to the same rail, so the
    /// motor is shorted to itself with nothing across it.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothInputsTheSameBrakes(bool high)
    {
        var rig = Build();
        Drive(rig, enable: true, in1: high, in2: high);

        Assert.Equal(BridgeState.Braking, rig.Bridge.State);

        var across = rig.Sim.NodeVoltage(rig.Bridge.Output1) - rig.Sim.NodeVoltage(rig.Bridge.Output2);
        Assert.True(Math.Abs(across) < 0.1, $"{across:0.000} V — a braked motor has nothing across it");
    }

    /// <summary>
    /// Disabling is coasting, and it is not the same as braking. Both outputs are released, so
    /// neither is tied to anything.
    /// </summary>
    [Fact]
    public void DisablingCoastsRatherThanBrakes()
    {
        var rig = Build();

        Drive(rig, enable: true, in1: true, in2: false);
        Assert.Equal(BridgeState.Forward, rig.Bridge.State);

        Drive(rig, enable: false, in1: true, in2: false);

        Assert.Equal(BridgeState.Coasting, rig.Bridge.State);
        Assert.True(Math.Abs(rig.Bridge.OutputCurrent) < 1e-3, "a coasting bridge drives nothing");
    }

    /// <summary>A real driver decodes the inputs so both halves of a leg cannot be on together.</summary>
    [Fact]
    public void TheInterlockStopsShootThrough()
    {
        var rig = Build();
        Drive(rig, enable: true, in1: true, in2: true);

        Assert.Equal(BridgeState.Braking, rig.Bridge.State);
        Assert.Empty(rig.Bridge.Violations);
    }

    /// <summary>
    /// With the interlock defeated it is a dead short from the supply to ground through two
    /// switches — a great deal of current, no torque, and a part that will not survive it.
    /// </summary>
    [Fact]
    public void WithoutTheInterlockItIsAShortAcrossTheSupply()
    {
        var rig = Build();
        rig.Bridge.PreventShootThrough = false;

        Drive(rig, enable: true, in1: true, in2: true);

        Assert.Equal(BridgeState.ShootThrough, rig.Bridge.State);
        Assert.NotEmpty(rig.Bridge.Violations);
        Assert.Contains("short across the motor supply", rig.Bridge.Violations[0]);

        // Twelve volts through two switches of about an ohm each is amps, and all of it is heat.
        Assert.True(rig.Bridge.SwitchDissipation > 10.0,
            $"{rig.Bridge.SwitchDissipation:0.#} W should be enormous");
    }

    /// <summary>An unwired input reads low rather than floating the matrix.</summary>
    [Fact]
    public void UnwiredInputsReadLow()
    {
        var circuit = new Circuit();

        var logic = circuit.Add(new DcVoltageSource(5.0));
        var motor = circuit.Add(new DcVoltageSource(12.0));
        var bridge = circuit.Add(new HBridge());
        var load = circuit.Add(new Resistor(12));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(logic.Negative, gnd.Pin);
        circuit.Connect(motor.Negative, gnd.Pin);
        circuit.Connect(bridge.LogicSupply, logic.Positive);
        circuit.Connect(bridge.MotorSupply, motor.Positive);
        circuit.Connect(bridge.Gnd, gnd.Pin);
        circuit.Connect(bridge.Output1, load.A);
        circuit.Connect(load.B, bridge.Output2);

        // Enable, IN1 and IN2 all left unconnected.
        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-5 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-3);

        Assert.Equal(BridgeState.Coasting, bridge.State);
    }

    /// <summary>
    /// The motor is an inductance, and when the switches open its current has to go somewhere.
    /// The body diodes are where — without them the output would fly to whatever voltage the
    /// solver needed to stop the current dead.
    /// </summary>
    [Fact]
    public void TheBodyDiodesCatchTheInductiveKick()
    {
        var circuit = new Circuit();

        var logic = circuit.Add(new DcVoltageSource(5.0));
        var motor = circuit.Add(new DcVoltageSource(12.0));
        var enable = circuit.Add(new DcVoltageSource(5.0));
        var in1 = circuit.Add(new DcVoltageSource(5.0));
        var in2 = circuit.Add(new DcVoltageSource(0.0));

        var bridge = circuit.Add(new HBridge());
        var winding = circuit.Add(new Inductor(10e-3));
        var resistance = circuit.Add(new Resistor(12));
        var gnd = circuit.Add(new Ground());

        foreach (var s in new[] { logic, motor, enable, in1, in2 })
            circuit.Connect(s.Negative, gnd.Pin);

        circuit.Connect(bridge.LogicSupply, logic.Positive);
        circuit.Connect(bridge.MotorSupply, motor.Positive);
        circuit.Connect(bridge.Gnd, gnd.Pin);
        circuit.Connect(bridge.Enable, enable.Positive);
        circuit.Connect(bridge.Input1, in1.Positive);
        circuit.Connect(bridge.Input2, in2.Positive);

        circuit.Connect(bridge.Output1, winding.A);
        circuit.Connect(winding.B, resistance.A);
        circuit.Connect(resistance.B, bridge.Output2);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6, MaxTimeStep = 2e-6 });
        sim.Reset();
        sim.SolveOperatingPoint();

        // Let the current build, then cut the drive.
        sim.Run(5e-3);
        enable.Voltage = 0.0;

        var worst = 0.0;
        for (var i = 0; i < 400; i++)
        {
            sim.Run(2e-6);
            worst = Math.Max(worst, Math.Abs(sim.NodeVoltage(bridge.Output1)));
        }

        // Clamped to a diode drop or so outside the rails, not to hundreds of volts.
        Assert.True(worst < 20.0, $"{worst:0.#} V — the body diodes should have clamped this");
    }
}
