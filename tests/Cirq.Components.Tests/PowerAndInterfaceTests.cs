using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The resettable fuse, the solid-state relay, the quad op-amp, the microstepping driver and the
/// RS-232 transceiver. Every figure here is checked against what the circuit arithmetic says it
/// should be rather than against whatever the model happened to produce.
/// </summary>
public class PowerAndInterfaceTests
{
    // ---- PPTC ------------------------------------------------------------

    [Fact]
    public void ResettableFuseCarriesItsHoldCurrentIndefinitely()
    {
        var (circuit, fuse, _) = FuseCircuit(load: 20.0, hold: 0.5);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(3.0);

        // 5 V into 20 Ω is 250 mA, half the hold current, so it must not trip however long it runs.
        Assert.False(fuse.IsTripped);
        Assert.Equal(0.25, fuse.Current, 2);
    }

    [Fact]
    public void ResettableFuseTripsOnAFaultAndDoesNotOpen()
    {
        var (circuit, fuse, _) = FuseCircuit(load: 2.0, hold: 0.5);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(3.0);

        Assert.True(fuse.IsTripped);

        // The teaching point: it is not a broken wire. A tripped PPTC keeps a trickle flowing,
        // which is exactly what holds it hot enough to stay tripped.
        Assert.InRange(fuse.Current, 1e-3, 100e-3);
    }

    [Fact]
    public void ResettableFuseComesBackWhenTheFaultIsRemoved()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var fuse = circuit.Add(new ResettableFuse(0.5));
        var fault = circuit.Add(new ToggleSwitch(closed: true));
        var load = circuit.Add(new Resistor(2.0));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, fuse.A);
        circuit.Connect(fuse.B, fault.A);
        circuit.Connect(fault.B, load.A);
        circuit.Connect(load.B, ground.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1.5);

        Assert.True(fuse.IsTripped);

        fault.IsClosed = false;
        sim.Run(8.0);

        // A real one needs the power taken off it to reset, and cooling is much slower than
        // tripping was — which is why this runs for eight seconds after a trip that took one.
        Assert.False(fuse.IsTripped);
    }

    private static (Circuit, ResettableFuse, Resistor) FuseCircuit(double load, double hold)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var fuse = circuit.Add(new ResettableFuse(hold));
        var resistor = circuit.Add(new Resistor(load));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, fuse.A);
        circuit.Connect(fuse.B, resistor.A);
        circuit.Connect(resistor.B, ground.Pin);

        return (circuit, fuse, resistor);
    }

    // ---- solid-state relay ----------------------------------------------

    [Fact]
    public void SolidStateRelayWaitsForAZeroCrossingBeforeItFires()
    {
        var circuit = new Circuit();
        var mains = circuit.Add(new FunctionGenerator(Waveform.Sine, 50, 340.0));
        var relay = circuit.Add(new SolidStateRelay());
        var lamp = circuit.Add(new Resistor(100));
        var drive = circuit.Add(new DcVoltageSource(0.0));
        var series = circuit.Add(new Resistor(470));
        var ground = circuit.Add(new Ground());

        circuit.Connect(mains.Return, ground.Pin);
        circuit.Connect(mains.Output, lamp.A);
        circuit.Connect(lamp.B, relay.LoadA);
        circuit.Connect(relay.LoadB, ground.Pin);
        circuit.Connect(drive.Negative, ground.Pin);
        circuit.Connect(drive.Positive, series.A);
        circuit.Connect(series.B, relay.ControlPositive);
        circuit.Connect(relay.ControlNegative, ground.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        // A quarter cycle in, which on a 50 Hz sine is a peak — the worst moment to switch.
        sim.Run(25e-3);
        Assert.False(relay.IsConducting);

        drive.Voltage = 5.0;

        var commandedAt = sim.Time;
        var firedAt = double.NaN;
        var peak = 0.0;

        while (sim.Time < commandedAt + 30e-3)
        {
            sim.Step();
            if (relay.IsConducting && double.IsNaN(firedAt)) firedAt = sim.Time;
            peak = Math.Max(peak, Math.Abs(relay.LoadCurrent));
        }

        Assert.False(double.IsNaN(firedAt));

        // It was commanded at a peak, so it had to wait. A 50 Hz sine crosses zero every 10 ms,
        // so the wait is somewhere in (0, 10 ms] and nowhere near instant.
        var waited = firedAt - commandedAt;
        Assert.InRange(waited, 1e-3, 10.1e-3);

        // 340 V peak into 100 Ω is 3.4 A, and once it is on it really does conduct.
        Assert.InRange(peak, 1.0, 3.5);
    }

    [Fact]
    public void SolidStateRelayBlocksWhileItIsNotCommanded()
    {
        var circuit = new Circuit();
        var mains = circuit.Add(new FunctionGenerator(Waveform.Sine, 50, 340.0));
        var relay = circuit.Add(new SolidStateRelay());
        var lamp = circuit.Add(new Resistor(100));
        var ground = circuit.Add(new Ground());

        circuit.Connect(mains.Return, ground.Pin);
        circuit.Connect(mains.Output, lamp.A);
        circuit.Connect(lamp.B, relay.LoadA);
        circuit.Connect(relay.LoadB, ground.Pin);
        circuit.Connect(relay.ControlPositive, ground.Pin);
        circuit.Connect(relay.ControlNegative, ground.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var leakage = 0.0;
        while (sim.Time < 40e-3)
        {
            sim.Step();
            leakage = Math.Max(leakage, Math.Abs(relay.LoadCurrent));
        }

        Assert.False(relay.IsConducting);

        // Off is not perfectly open — a real triac leaks — but it is microamps, not amps.
        Assert.InRange(leakage, 0.0, 1e-3);
    }

    // ---- LM324 -----------------------------------------------------------

    [Fact]
    public void QuadOpAmpRunsFourIndependentFollowersFromOneSupply()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var quad = circuit.Add(new QuadOpAmp());
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(quad.PositiveSupply, supply.Positive);
        circuit.Connect(quad.NegativeSupply, ground.Pin);

        var inputs = new[] { 0.5, 1.5, 2.5, 3.5 };

        for (var i = 0; i < 4; i++)
        {
            var (plus, minus, output) = quad.Channel(i);
            var source = circuit.Add(new DcVoltageSource(inputs[i]));

            circuit.Connect(source.Negative, ground.Pin);
            circuit.Connect(source.Positive, plus);
            circuit.Connect(minus, output);

            var load = circuit.Add(new Resistor(10e3));
            circuit.Connect(output, load.A);
            circuit.Connect(load.B, ground.Pin);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-3);

        for (var i = 0; i < 4; i++)
        {
            var (_, _, output) = quad.Channel(i);

            // The fourth one is asked for 3.5 V on a 5 V rail, and an LM324 cannot get within
            // 1.5 V of its positive supply — so it saturates rather than following. That is the
            // part's defining limitation and the reason the MCP6002 exists.
            if (i == 3)
            {
                Assert.True(quad.IsChannelSaturated(i));
                Assert.InRange(sim.NodeVoltage(output), 3.4, 3.6);
                continue;
            }

            Assert.False(quad.IsChannelSaturated(i));
            Assert.Equal(inputs[i], sim.NodeVoltage(output), 2);
        }
    }

    // ---- A4988 -----------------------------------------------------------

    [Fact]
    public void StepperDriverRegulatesCoilCurrentToItsLimit()
    {
        var (circuit, driver, motor, _) = DriverCircuit(limit: 0.8);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var peak = 0.0;
        var chopped = 0;

        while (sim.Time < 60e-3)
        {
            sim.Step();
            peak = Math.Max(peak, Math.Abs(driver.WindingCurrent(0)));
            if (driver.IsChopping(0)) chopped++;
        }

        // The supply is 12 V into about 2.8 Ω, which unregulated would be 4.3 A. It holds 0.8.
        Assert.InRange(peak, 0.78, 0.85);
        Assert.True(chopped > 0, "the driver never chopped, so it was not regulating at all");
        Assert.True(motor.Angle > 0);
    }

    [Fact]
    public void StepperDriverReversesTheWindingRatherThanSwitchingItOff()
    {
        var (circuit, driver, _, _) = DriverCircuit(limit: 0.8);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var mostPositive = 0.0;
        var mostNegative = 0.0;

        while (sim.Time < 60e-3)
        {
            sim.Step();
            mostPositive = Math.Max(mostPositive, driver.WindingCurrent(0));
            mostNegative = Math.Min(mostNegative, driver.WindingCurrent(0));
        }

        // Both signs, at the limit each way. A unipolar drive would only ever manage one of these,
        // and this is the whole difference an H-bridge makes.
        Assert.InRange(mostPositive, 0.78, 0.85);
        Assert.InRange(mostNegative, -0.85, -0.78);
    }

    [Fact]
    public void StepperDriverMicrostepsIntoTheAnglesBetweenFullSteps()
    {
        var (circuit, driver, _, _) = DriverCircuit(limit: 0.8, microstepPins: 0b011);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1e-3);

        Assert.Equal(8, driver.Microsteps);

        // Eight microsteps to a full step means 32 to an electrical cycle, so the targets are a
        // sine and a cosine sampled every 11.25°. Every level in between the full-step ones has
        // to appear, which is exactly what microstepping is.
        var seen = new HashSet<int>();

        while (sim.Time < 120e-3)
        {
            sim.Step();
            seen.Add((int)Math.Round(driver.TargetCurrent(0) * 100));
        }

        // Full stepping would only ever command +80, 0 or -80.
        var between = seen.Count(v => Math.Abs(v) > 2 && Math.Abs(v) < 78);
        Assert.True(between >= 4, $"only {between} intermediate current levels were commanded");
    }

    [Fact]
    public void StepperDriverStopsDrivingWhenDisabled()
    {
        var (circuit, driver, _, enable) = DriverCircuit(limit: 0.8);

        enable.Voltage = 5.0;

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(20e-3);

        Assert.False(driver.IsEnabled);
        Assert.InRange(Math.Abs(driver.WindingCurrent(0)), 0.0, 1e-3);
    }

    private static (Circuit, StepperDriver, StepperMotor, DcVoltageSource) DriverCircuit(
        double limit, int microstepPins = 0)
    {
        var circuit = new Circuit();
        var logic = circuit.Add(new DcVoltageSource(5.0));
        var motorSupply = circuit.Add(new DcVoltageSource(12.0));
        var driver = circuit.Add(new StepperDriver { CurrentLimit = limit });
        var motor = circuit.Add(new StepperMotor
        {
            Wiring = StepperWiring.Bipolar,
            CoilResistance = 2.0,
            CoilInductance = 2e-3,
        });
        var clock = circuit.Add(new ClockSource(200.0));
        var enable = circuit.Add(new DcVoltageSource(0.0));
        var ground = circuit.Add(new Ground());

        circuit.Connect(logic.Negative, ground.Pin);
        circuit.Connect(motorSupply.Negative, ground.Pin);
        circuit.Connect(enable.Negative, ground.Pin);

        circuit.Connect(driver.Vcc, logic.Positive);
        circuit.Connect(driver.Gnd, ground.Pin);
        circuit.Connect(driver.Motor, motorSupply.Positive);
        circuit.Connect(driver.Step, clock.Out);
        circuit.Connect(driver.Direction, logic.Positive);
        circuit.Connect(driver.Enable, enable.Positive);

        var mode = new[] { driver.Ms1, driver.Ms2, driver.Ms3 };
        for (var i = 0; i < 3; i++)
            circuit.Connect(mode[i], (microstepPins & (1 << i)) != 0 ? logic.Positive : ground.Pin);

        // Bipolar: C1-C3 is one winding, C2-C4 the other.
        circuit.Connect(driver.OutputA1, motor.Coils[0]);
        circuit.Connect(driver.OutputB1, motor.Coils[2]);
        circuit.Connect(driver.OutputA2, motor.Coils[1]);
        circuit.Connect(driver.OutputB2, motor.Coils[3]);

        return (circuit, driver, motor, enable);
    }

    // ---- MAX232 ----------------------------------------------------------

    [Fact]
    public void Max232MakesBothLineRailsFromASingleFiveVoltSupply()
    {
        var (circuit, max, _) = Rs232Circuit();

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(20e-6);

        // Twice the supply, less the switch drops the stacking costs. Nowhere near ±12 V, which
        // is the surprise for anybody who has only read the standard.
        Assert.InRange(max.PositiveRail, 8.0, 8.6);
        Assert.InRange(max.NegativeRail, -8.6, -8.0);
    }

    [Fact]
    public void Max232InvertsTtlOntoTheLineAndBackAgain()
    {
        var (circuit, max, transmit) = Rs232Circuit();

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        transmit.State = true;
        sim.Run(20e-6);

        // A TTL one is a mark, and a mark is the negative level. This catches everybody once.
        Assert.True(max.LineVoltage(0) < -5.0);
        Assert.True(max.IsReceivingMark(1));
        Assert.True(sim.NodeVoltage(max.ReceiverOutputs[1]) > 2.4);

        transmit.State = false;
        sim.Run(20e-6);

        Assert.True(max.LineVoltage(0) > 5.0);
        Assert.False(max.IsReceivingMark(1));
        Assert.True(sim.NodeVoltage(max.ReceiverOutputs[1]) < 0.8);
    }

    [Fact]
    public void Max232ReceiverHasHysteresisAroundItsThreshold()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var max = circuit.Add(new Max232());
        var line = circuit.Add(new DcVoltageSource(-5.0));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(max.Vcc, supply.Positive);
        circuit.Connect(max.Gnd, ground.Pin);
        circuit.Connect(line.Negative, ground.Pin);
        circuit.Connect(line.Positive, max.ReceiverInputs[0]);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        var rising = double.NaN;
        for (var v = -5.0; v < 5.0; v += 0.01)
        {
            line.Voltage = v;
            sim.Run(2e-6);
            if (max.IsReceivingMark(0)) continue;

            rising = v;
            break;
        }

        var falling = double.NaN;
        for (var v = 5.0; v > -5.0; v -= 0.01)
        {
            line.Voltage = v;
            sim.Run(2e-6);
            if (!max.IsReceivingMark(0)) continue;

            falling = v;
            break;
        }

        // It decides in different places depending on which way it is going, and the gap is the
        // hysteresis. Without it, a long line's noise would make the receiver chatter on an edge.
        Assert.False(double.IsNaN(rising));
        Assert.False(double.IsNaN(falling));
        Assert.Equal(max.ReceiverHysteresis, rising - falling, 1);
    }

    [Fact]
    public void Max232PumpSagsWhenItsDriversAreOverloaded()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var max = circuit.Add(new Max232());
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(max.Vcc, supply.Positive);
        circuit.Connect(max.Gnd, ground.Pin);

        // RS-232 allows down to 3 kΩ. This is 200 Ω on both drivers, which is fifteen times that.
        for (var i = 0; i < 2; i++)
        {
            var load = circuit.Add(new Resistor(200.0));
            circuit.Connect(max.DriverOutputs[i], load.A);
            circuit.Connect(load.B, ground.Pin);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(20e-6);

        // The pump is a real source with a real output resistance, so loading it pulls it in, and
        // the line level goes with it until it is no longer RS-232 at all.
        Assert.True(max.PositiveRail < 6.5);
        Assert.True(Math.Abs(max.LineVoltage(0)) < 5.0);
        Assert.NotEmpty(max.Violations);
    }

    private static (Circuit, Max232, LogicToggle) Rs232Circuit()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var max = circuit.Add(new Max232());
        var transmit = circuit.Add(new LogicToggle());
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(max.Vcc, supply.Positive);
        circuit.Connect(max.Gnd, ground.Pin);
        circuit.Connect(max.DriverInputs[0], transmit.Out);

        // The driver looped straight back into the other channel's receiver, which is what a
        // loopback plug on the end of a serial cable does.
        circuit.Connect(max.DriverOutputs[0], max.ReceiverInputs[1]);

        return (circuit, max, transmit);
    }
}
