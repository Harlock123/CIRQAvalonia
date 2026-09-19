using Cirq.Components.Digital;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class Ic74xxTests
{
    private static SimulationSettings FastSettings => new() { TimeStep = 2e-9, MaxTimeStep = 2e-9 };

    /// <summary>Wires a 5 V supply into an IC's power pins and returns the supply rail terminal.</summary>
    private static Terminal Power(Circuit circuit, DigitalIc ic)
    {
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(ic.Vcc, supply.Positive);
        circuit.Connect(ic.Gnd, gnd.Pin);
        return supply.Positive;
    }

    private static bool IsHigh(CircuitSimulator sim, Terminal t) => sim.NodeVoltage(t) > LogicLevels.Ttl.Vih;

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, false)]
    public void Ic7400ImplementsNandOnEveryGate(bool a, bool b, bool expected)
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7400());
        Power(circuit, ic);

        var toggles = new LogicToggle[8];
        for (var gate = 0; gate < 4; gate++)
        {
            var (pinA, pinB, _) = ic.Gate(gate);
            toggles[gate * 2] = circuit.Add(new LogicToggle(a));
            toggles[gate * 2 + 1] = circuit.Add(new LogicToggle(b));
            circuit.Connect(toggles[gate * 2].Out, pinA);
            circuit.Connect(toggles[gate * 2 + 1].Out, pinB);
        }

        var sim = new CircuitSimulator(circuit, FastSettings);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-9);

        for (var gate = 0; gate < 4; gate++)
            Assert.Equal(expected, IsHigh(sim, ic.Gate(gate).Y));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Ic7408ImplementsAnd(bool a, bool b, bool expected)
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7408());
        Power(circuit, ic);

        var (pinA, pinB, pinY) = ic.Gate(0);
        circuit.Connect(circuit.Add(new LogicToggle(a)).Out, pinA);
        circuit.Connect(circuit.Add(new LogicToggle(b)).Out, pinB);

        var sim = new CircuitSimulator(circuit, FastSettings);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-9);

        Assert.Equal(expected, IsHigh(sim, pinY));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void Ic7432ImplementsOr(bool a, bool b, bool expected)
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7432());
        Power(circuit, ic);

        var (pinA, pinB, pinY) = ic.Gate(0);
        circuit.Connect(circuit.Add(new LogicToggle(a)).Out, pinA);
        circuit.Connect(circuit.Add(new LogicToggle(b)).Out, pinB);

        var sim = new CircuitSimulator(circuit, FastSettings);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-9);

        Assert.Equal(expected, IsHigh(sim, pinY));
    }

    [Fact]
    public void Ic7404InvertsAllSixChannelsIndependently()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7404());
        Power(circuit, ic);

        var toggles = new LogicToggle[6];
        for (var i = 0; i < 6; i++)
        {
            toggles[i] = circuit.Add(new LogicToggle(i % 2 == 0));
            circuit.Connect(toggles[i].Out, ic.Inverter(i).A);
        }

        var sim = new CircuitSimulator(circuit, FastSettings);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-9);

        for (var i = 0; i < 6; i++)
            Assert.Equal(i % 2 != 0, IsHigh(sim, ic.Inverter(i).Y));
    }

    [Fact]
    public void AnUnpoweredPackageReleasesItsOutputs()
    {
        // No Vcc connection: the outputs must float rather than quietly work.
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7400());
        var gnd = circuit.Add(new Ground());
        circuit.Connect(ic.Gnd, gnd.Pin);

        var (pinA, pinB, pinY) = ic.Gate(0);
        circuit.Connect(circuit.Add(new LogicToggle(false)).Out, pinA);
        circuit.Connect(circuit.Add(new LogicToggle(false)).Out, pinB);

        var sim = new CircuitSimulator(circuit, FastSettings);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-9);

        Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(0));
        Assert.False(IsHigh(sim, pinY));
    }

    [Fact]
    public void Ic7474LatchesDataOnTheRisingClockEdge()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7474());
        var rail = Power(circuit, ic);
        var (clear, data, clock, preset, q, qNot) = ic.Section(0);

        var clk = circuit.Add(new ClockSource(1e6));
        var d = circuit.Add(new LogicToggle(true));

        circuit.Connect(clk.Out, clock);
        circuit.Connect(d.Out, data);
        circuit.Connect(clear, rail);     // Preset and clear held inactive.
        circuit.Connect(preset, rail);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();

        // D is high: the first rising edge should set Q.
        sim.Run(1.2e-6);
        Assert.True(IsHigh(sim, q));
        Assert.False(IsHigh(sim, qNot));

        // Drop D and let another edge through.
        d.State = false;
        sim.Run(2e-6);
        Assert.False(IsHigh(sim, q));
        Assert.True(IsHigh(sim, qNot));
    }

    [Fact]
    public void Ic7474AsynchronousClearBeatsTheClock()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7474());
        var rail = Power(circuit, ic);
        var (clear, data, clock, preset, q, _) = ic.Section(0);

        var clk = circuit.Add(new ClockSource(1e6));
        var clearToggle = circuit.Add(new LogicToggle(true));

        circuit.Connect(clk.Out, clock);
        circuit.Connect(data, rail);              // D tied high.
        circuit.Connect(preset, rail);
        circuit.Connect(clearToggle.Out, clear);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(1.2e-6);
        Assert.True(IsHigh(sim, q));

        // Assert clear (active low): Q drops without waiting for a clock edge.
        clearToggle.State = false;
        sim.Run(200e-9);
        Assert.False(IsHigh(sim, q));
    }

    [Fact]
    public void Ic7474DividesTheClockByTwoWhenWiredAsAToggle()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7474());
        var rail = Power(circuit, ic);
        var (clear, data, clock, preset, q, qNot) = ic.Section(0);

        var clk = circuit.Add(new ClockSource(1e6));
        circuit.Connect(clk.Out, clock);
        circuit.Connect(qNot, data);        // Q' back to D makes it divide by two.
        circuit.Connect(clear, rail);
        circuit.Connect(preset, rail);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();

        var edges = new List<double>();
        var wasHigh = IsHigh(sim, q);
        while (sim.Time < 20e-6)
        {
            sim.Step();
            var isHigh = IsHigh(sim, q);
            if (isHigh && !wasHigh) edges.Add(sim.Time);
            wasHigh = isHigh;
        }

        Assert.True(edges.Count >= 5, $"Expected a divided clock, saw {edges.Count} edges.");
        var period = (edges[^1] - edges[0]) / (edges.Count - 1);
        Assert.Equal(2e-6, period, 2e-6 * 0.05);
    }

    [Fact]
    public void Ic7490CountsZeroToNineInBcd()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7490());
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(ic.Vcc, supply.Positive);
        circuit.Connect(ic.Gnd, gnd.Pin);

        var clk = circuit.Add(new ClockSource(1e6));
        circuit.Connect(clk.Out, ic.ClockA);
        circuit.Connect(ic.Qa, ic.ClockB);      // QA into CKB gives the BCD sequence.
        circuit.Connect(ic.Reset0A, gnd.Pin);
        circuit.Connect(ic.Reset0B, gnd.Pin);
        circuit.Connect(ic.Reset9A, gnd.Pin);
        circuit.Connect(ic.Reset9B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();

        // Sample the count just before each falling clock edge, which is when it is stable.
        var observed = new List<int>();
        var wasHigh = true;
        while (sim.Time < 13e-6)
        {
            sim.Step();
            var isHigh = sim.NodeVoltage(clk.Out) > LogicLevels.Ttl.Vih;
            if (!isHigh && wasHigh)
            {
                // Let the ripple settle before reading.
                sim.Run(100e-9);
                observed.Add(ic.Count);
            }
            wasHigh = isHigh;
        }

        Assert.True(observed.Count >= 11, $"Only captured {observed.Count} counts.");

        // The sequence must be 0..9 wrapping back to 0.
        var start = observed.IndexOf(0);
        Assert.True(start >= 0, "Counter never reached zero.");
        for (var i = 0; i < 10 && start + i < observed.Count; i++)
            Assert.Equal(i, observed[start + i]);
    }

    [Fact]
    public void Ic7490ResetPinsForceZeroAndNine()
    {
        var circuit = new Circuit();
        var ic = circuit.Add(new Ic7490());
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(ic.Vcc, supply.Positive);
        circuit.Connect(ic.Gnd, gnd.Pin);

        var clk = circuit.Add(new ClockSource(1e6));
        circuit.Connect(clk.Out, ic.ClockA);
        circuit.Connect(ic.Qa, ic.ClockB);

        var r0 = circuit.Add(new LogicToggle(false));
        var r9 = circuit.Add(new LogicToggle(false));
        circuit.Connect(r0.Out, ic.Reset0A);
        circuit.Connect(r0.Out, ic.Reset0B);
        circuit.Connect(r9.Out, ic.Reset9A);
        circuit.Connect(r9.Out, ic.Reset9B);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(3.5e-6);

        r9.State = true;
        sim.Run(500e-9);
        Assert.Equal(9, ic.Count);

        r9.State = false;
        r0.State = true;
        sim.Run(500e-9);
        Assert.Equal(0, ic.Count);
    }
}
