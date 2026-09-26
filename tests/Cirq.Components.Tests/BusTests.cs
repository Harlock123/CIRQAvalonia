using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// Buses: several named signals, and the taps that join pins to them.
/// <para>
/// The thing worth testing is that a tap is a net label with the arithmetic done — that
/// <c>D</c> and 3 make one net called <c>D3</c>, that two of them are the same node in the matrix,
/// and that nothing about the line drawn beside them takes part in any of it. A bus that
/// <i>looked</i> connected and was not would be the worst failure available here.
/// </para>
/// </summary>
public class BusTests
{
    [Fact]
    public void ATapNamesTheBusAndTheBitRunTogether()
    {
        Assert.Equal("D3", new BusTap("D", 3).NetName);
        Assert.Equal("ADDR11", new BusTap("ADDR", 11).NetName);

        // Trimmed, because a name typed with a space either side is the same bus.
        Assert.Equal("Q0", new BusTap(" Q ", 0).NetName);
    }

    /// <summary>
    /// Two taps of the same signal are one net — which is the whole of what a bus does, and it
    /// happens whether or not anything is drawn between them.
    /// </summary>
    [Fact]
    public void TwoTapsOfTheSameSignalAreOneNode()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(6.0));
        var top = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var bottom = circuit.Add(new Resistor(1e3) { Name = "R2" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(bottom.B, ground.Pin);

        // The two resistors are joined only by the bus: R1.B taps D3, and so does R2.A.
        var first = circuit.Add(new BusTap("D", 3) { Name = "B1" });
        var second = circuit.Add(new BusTap("D", 3) { Name = "B2" });

        circuit.Connect(top.B, first.Pin);
        circuit.Connect(bottom.A, second.Pin);

        var simulator = new CircuitSimulator(circuit);

        simulator.Reset();
        simulator.SolveOperatingPoint();

        // A divider, so the midpoint is half the supply — which it can only be if the two halves
        // are one node.
        Assert.Equal(3.0, simulator.NodeVoltage(top.B), 1e-6);
        Assert.Equal(3.0, simulator.NodeVoltage(bottom.A), 1e-6);
    }

    [Fact]
    public void DifferentSignalsOfOneBusAreDifferentNets()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(6.0));
        var top = circuit.Add(new Resistor(1e3));
        var bottom = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(bottom.B, ground.Pin);

        // D3 and D4 — one character apart, and not the same net.
        circuit.Connect(top.B, circuit.Add(new BusTap("D", 3)).Pin);
        circuit.Connect(bottom.A, circuit.Add(new BusTap("D", 4)).Pin);

        var netlist = circuit.BuildNetlist();

        Assert.NotEqual(netlist.NetOf(top.B).Id, netlist.NetOf(bottom.A).Id);
    }

    /// <summary>
    /// The line is drawing. It has no pins, stamps nothing, and cannot join anything — which is how
    /// every schematic tool works, and the commonest thing people are surprised by.
    /// </summary>
    [Fact]
    public void TheLineIsDrawingRatherThanWiring()
    {
        var line = new Cirq.Components.Annotations.BusLine("D[0..7]");

        Assert.Empty(line.Terminals);
        Assert.IsAssignableFrom<IAnnotation>(line);

        var circuit = new Circuit();
        circuit.Add(line);
        circuit.Add(new Ground());

        // Nothing in the netlist, and nothing in the matrix.
        Assert.Equal(0, circuit.BuildNetlist().NodeCount);
    }

    [Fact]
    public void ATapSurvivesARoundTrip()
    {
        var circuit = new Circuit();

        circuit.Add(new BusTap("ADDR", 7) { Name = "B1" });
        circuit.Add(new Cirq.Components.Annotations.BusLine("ADDR[0..15]", 400) { Name = "BUS1" });

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit;

        var tap = back.Components.OfType<BusTap>().Single();
        var line = back.Components.OfType<Cirq.Components.Annotations.BusLine>().Single();

        Assert.Equal("ADDR", tap.Bus);
        Assert.Equal(7, tap.Bit);
        Assert.Equal("ADDR7", tap.NetName);

        Assert.Equal("ADDR[0..15]", line.Label);
        Assert.Equal(400, line.Length);
    }

    /// <summary>
    /// A tap and a net label of the same name are the same net. They have to be: a tap <i>is</i> a
    /// net label, and a bus that could not be joined to a plain label would be a second, separate
    /// naming scheme.
    /// </summary>
    [Fact]
    public void ATapAndANetLabelOfTheSameNameAreOneNet()
    {
        var circuit = new Circuit();

        var gate = circuit.Add(new LogicGate(GateFunction.Not, 1) { Name = "U1" });
        var resistor = circuit.Add(new Resistor(1e3) { Name = "R1" });

        circuit.Connect(gate.OutputTerminals[0], circuit.Add(new BusTap("Q", 0)).Pin);
        circuit.Connect(resistor.A, circuit.Add(new NetLabel("Q0")).Pin);

        var netlist = circuit.BuildNetlist();

        Assert.Equal(
            netlist.NetOf(gate.OutputTerminals[0]).Id,
            netlist.NetOf(resistor.A).Id);
    }
}
