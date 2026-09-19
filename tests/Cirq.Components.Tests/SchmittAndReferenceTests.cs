using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class SchmittTriggerTests
{
    /// <summary>One inverter fed from a variable source, with the package powered from 5 V.</summary>
    private static (CircuitSimulator Sim, Ic74hc14 Ic, DcVoltageSource Input) Rig()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var input = circuit.Add(new DcVoltageSource(0.0));
        var ic = circuit.Add(new Ic74hc14());
        var load = circuit.Add(new Resistor(10e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, gnd.Pin);

        var (a, y) = ic.Inverter(0);
        circuit.Connect(input.Negative, gnd.Pin);
        circuit.Connect(input.Positive, a);
        circuit.Connect(y, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, ic, input);
    }

    /// <summary>
    /// The defining behaviour: sweeping the input up switches at the upper threshold, sweeping it
    /// back down switches at the lower one. A plain inverter would switch at the same place both
    /// ways.
    /// </summary>
    [Fact]
    public void ItSwitchesAtDifferentPointsGoingUpAndComingDown()
    {
        var (sim, ic, input) = Rig();
        var (_, y) = ic.Inverter(0);

        double SweepTo(double volts)
        {
            input.Voltage = volts;

            // Twice: with nothing queued the transient loop takes the whole window in one step,
            // so the first solve is where the gate *sees* the new input and queues its transition
            // one propagation delay later. The second lets that land.
            sim.Run(200e-9);
            sim.Run(200e-9);
            return sim.NodeVoltage(y);
        }

        // Rising: still high at 2.5 V, because the upper threshold is about 2.9 V.
        SweepTo(0.0);
        Assert.True(SweepTo(2.5) > 4.0, "a rising input below VT+ should not have switched yet");
        Assert.True(SweepTo(3.2) < 1.0, "past VT+ the output should have gone low");

        // Falling: still low at 2.5 V, because the lower threshold is about 1.9 V.
        Assert.True(SweepTo(2.5) < 1.0, "a falling input above VT- should stay switched");
        Assert.True(SweepTo(1.5) > 4.0, "past VT- the output should have returned high");
    }

    [Fact]
    public void TheHysteresisBandIsAboutAVoltOnAFiveVoltRail()
    {
        var ic = new Ic74hc14();

        // 74HC14 on 5 V: VT+ near 2.9 and VT- near 1.9.
        Assert.Equal(1.0, ic.HysteresisAt(5.0), 0.15);
        Assert.Equal(5.0 * 0.58, ic.UpperThresholdFraction * 5.0, 0.01);
    }

    /// <summary>The thresholds are fractions of the supply, so they follow the rail down.</summary>
    [Fact]
    public void TheThresholdsScaleWithTheSupply()
    {
        var ic = new Ic74hc14();

        Assert.True(ic.HysteresisAt(3.3) < ic.HysteresisAt(5.0));
        Assert.Equal(ic.HysteresisAt(5.0) * 3.3 / 5.0, ic.HysteresisAt(3.3), 1e-9);
    }

    [Fact]
    public void AllSixInvertersAreIndependent()
    {
        var ic = new Ic74hc14();

        Assert.Equal(6, ic.GateCount);

        var pins = Enumerable.Range(0, 6)
            .SelectMany(i => new[] { ic.Inverter(i).A, ic.Inverter(i).Y })
            .ToList();

        Assert.Equal(pins.Count, pins.Distinct().Count());
    }

    /// <summary>
    /// What the part is bought for: a resistor from output back to input and a capacitor to
    /// ground makes an oscillator out of one gate, and it only works because of the hysteresis —
    /// the capacitor charges to the upper threshold, the output flips, and it discharges to the
    /// lower one.
    /// </summary>
    [Fact]
    public void OneGateWithAnRcMakesAnOscillator()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ic = circuit.Add(new Ic74hc14());
        var resistor = circuit.Add(new Resistor(10e3));
        var capacitor = circuit.Add(new Capacitor(10e-9));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, gnd.Pin);

        var (a, y) = ic.Inverter(0);
        circuit.Connect(y, resistor.A);
        circuit.Connect(resistor.B, a);
        circuit.Connect(a, capacitor.A);
        circuit.Connect(capacitor.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();

        var edges = 0;
        var wasHigh = false;
        sim.TimePointAccepted += _ =>
        {
            var high = sim.NodeVoltage(y) > 2.5;
            if (high && !wasHigh) edges++;
            wasHigh = high;
        };

        sim.Run(5e-3);

        // RC is 100 us and the swing is between the thresholds, so this should free-run at a few
        // kilohertz — the exact figure depends on the thresholds, but it must oscillate.
        Assert.True(edges > 5, $"only {edges} cycles in 5 ms — it is not oscillating");
    }
}

public class ShuntReferenceTests
{
    /// <summary>The basic shunt: reference tied to cathode, fed through a resistor.</summary>
    private static (CircuitSimulator Sim, ShuntReference Reference, Resistor Feed)
        Shunt(double supply, double feedOhms, double loadOhms = 0)
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(supply));
        var feed = circuit.Add(new Resistor(feedOhms));
        var reference = circuit.Add(new ShuntReference());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Negative, gnd.Pin);
        circuit.Connect(source.Positive, feed.A);
        circuit.Connect(feed.B, reference.Cathode);
        circuit.Connect(reference.Cathode, reference.Reference);
        circuit.Connect(reference.Anode, gnd.Pin);

        if (loadOhms > 0)
        {
            var load = circuit.Add(new Resistor(loadOhms));
            circuit.Connect(reference.Cathode, load.A);
            circuit.Connect(load.B, gnd.Pin);
        }

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();
        return (sim, reference, feed);
    }

    [Fact]
    public void TiedToItsOwnReferenceItIsATwoAndAHalfVoltShunt()
    {
        var (sim, reference, _) = Shunt(supply: 12.0, feedOhms: 4.7e3);

        Assert.Equal(2.495, sim.NodeVoltage(reference.Cathode), 0.05);
        Assert.True(reference.IsRegulating);
    }

    /// <summary>A shunt holds its voltage by absorbing whatever the load does not take.</summary>
    [Theory]
    [InlineData(9.0)]
    [InlineData(12.0)]
    [InlineData(24.0)]
    public void ItHoldsItsSetpointAcrossItsSupplyRange(double supply)
    {
        var (sim, reference, _) = Shunt(supply, feedOhms: 4.7e3);

        Assert.Equal(2.495, sim.NodeVoltage(reference.Cathode), 0.05);
    }

    [Fact]
    public void ItAbsorbsWhateverTheLoadLeaves()
    {
        var (unloaded, unloadedRef, _) = Shunt(12.0, 4.7e3);
        var (loaded, loadedRef, _) = Shunt(12.0, 4.7e3, loadOhms: 10e3);

        // Same rail either way; the reference just passes less of it.
        Assert.Equal(unloaded.NodeVoltage(unloadedRef.Cathode),
            loaded.NodeVoltage(loadedRef.Cathode), 0.05);
        Assert.True(loadedRef.CathodeCurrent < unloadedRef.CathodeCurrent,
            "a load should take current the reference would otherwise have sunk");
    }

    /// <summary>
    /// The whole point of the part: a divider from the cathode to the reference scales the
    /// setpoint up, so the cathode sits at 2.495·(1 + R1/R2).
    /// </summary>
    [Theory]
    [InlineData(10e3, 10e3, 4.99)]     // 2 x
    [InlineData(20e3, 10e3, 7.485)]    // 3 x
    [InlineData(10e3, 20e3, 3.74)]     // 1.5 x
    public void ADividerProgramsTheCathodeVoltage(double upper, double lower, double expected)
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(15.0));
        var feed = circuit.Add(new Resistor(2.2e3));
        var reference = circuit.Add(new ShuntReference());
        var top = circuit.Add(new Resistor(upper));
        var bottom = circuit.Add(new Resistor(lower));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Negative, gnd.Pin);
        circuit.Connect(source.Positive, feed.A);
        circuit.Connect(feed.B, reference.Cathode);
        circuit.Connect(reference.Anode, gnd.Pin);

        circuit.Connect(reference.Cathode, top.A);
        circuit.Connect(top.B, reference.Reference);
        circuit.Connect(reference.Reference, bottom.A);
        circuit.Connect(bottom.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.SolveOperatingPoint();

        Assert.Equal(expected, sim.NodeVoltage(reference.Cathode), expected * 0.04);
    }

    /// <summary>
    /// Starved of current it cannot regulate, which is the classic way to size the feed resistor
    /// wrongly and end up with a circuit that almost works.
    /// </summary>
    [Fact]
    public void StarvedOfCurrentItSaysSo()
    {
        // 1 M from a 12 V rail is under 10 uA, far below the milliamp it needs.
        var (_, reference, _) = Shunt(12.0, feedOhms: 1e6);

        Assert.False(reference.IsRegulating);
        Assert.Contains("minimum", string.Join(" | ", reference.Violations));
    }

    [Fact]
    public void ProperlyBiasedItRaisesNothing()
    {
        var (_, reference, _) = Shunt(12.0, feedOhms: 4.7e3);

        Assert.Empty(reference.Violations);
        Assert.True(reference.CathodeCurrent >= reference.MinimumOperatingCurrent);
    }

    [Fact]
    public void AnUnpoweredCircuitIsNotAFault()
    {
        var (_, reference, _) = Shunt(0.0, feedOhms: 4.7e3);

        Assert.Empty(reference.Violations);
    }
}
