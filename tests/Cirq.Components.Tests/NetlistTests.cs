using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

public class NetlistTests
{
    [Fact]
    public void GroundAlwaysResolvesToNodeMinusOne()
    {
        var circuit = new Circuit();
        var r = circuit.Add(new Resistor(1e3));
        var gnd = circuit.Add(new Ground());
        circuit.Connect(r.B, gnd.Pin);

        var netlist = circuit.BuildNetlist();

        Assert.Equal(Netlist.GroundIndex, netlist.IndexOf(gnd.Pin));
        Assert.Equal(Netlist.GroundIndex, netlist.IndexOf(r.B));
        Assert.NotEqual(Netlist.GroundIndex, netlist.IndexOf(r.A));
    }

    [Fact]
    public void ChainedWiresCollapseIntoOneNet()
    {
        var circuit = new Circuit();
        var r1 = circuit.Add(new Resistor());
        var r2 = circuit.Add(new Resistor());
        var r3 = circuit.Add(new Resistor());

        circuit.Connect(r1.B, r2.A);
        circuit.Connect(r2.A, r3.A);

        var netlist = circuit.BuildNetlist();

        Assert.Equal(netlist.IndexOf(r1.B), netlist.IndexOf(r2.A));
        Assert.Equal(netlist.IndexOf(r2.A), netlist.IndexOf(r3.A));
        Assert.NotEqual(netlist.IndexOf(r1.A), netlist.IndexOf(r1.B));
    }

    [Fact]
    public void SeparateGroundSymbolsShareTheSingleGroundNet()
    {
        var circuit = new Circuit();
        var r1 = circuit.Add(new Resistor());
        var r2 = circuit.Add(new Resistor());
        var g1 = circuit.Add(new Ground());
        var g2 = circuit.Add(new Ground());

        circuit.Connect(r1.B, g1.Pin);
        circuit.Connect(r2.B, g2.Pin);

        var netlist = circuit.BuildNetlist();

        Assert.Equal(Netlist.GroundIndex, netlist.IndexOf(r1.B));
        Assert.Equal(Netlist.GroundIndex, netlist.IndexOf(r2.B));
        Assert.Single(netlist.Nets, n => n.IsGround);
    }

    [Fact]
    public void NodeIndicesAreDenseAndStartAtZero()
    {
        var circuit = new Circuit();
        var r1 = circuit.Add(new Resistor());
        var r2 = circuit.Add(new Resistor());
        var gnd = circuit.Add(new Ground());
        circuit.Connect(r1.B, r2.A);
        circuit.Connect(r2.B, gnd.Pin);

        var netlist = circuit.BuildNetlist();
        var indices = netlist.Nets.Where(n => !n.IsGround).Select(n => n.Index).OrderBy(i => i).ToArray();

        Assert.Equal(Enumerable.Range(0, indices.Length), indices);
    }

    [Fact]
    public void TerminalsOfDistinctComponentsNeverCollide()
    {
        // Two resistors expose identically named pins; they must stay separate nets.
        var circuit = new Circuit();
        var r1 = circuit.Add(new Resistor());
        var r2 = circuit.Add(new Resistor());

        var netlist = circuit.BuildNetlist();

        Assert.NotEqual(netlist.IndexOf(r1.A), netlist.IndexOf(r2.A));
    }

    [Fact]
    public void RemovingAComponentDropsItsWires()
    {
        var circuit = new Circuit();
        var r1 = circuit.Add(new Resistor());
        var r2 = circuit.Add(new Resistor());
        circuit.Connect(r1.B, r2.A);
        Assert.Single(circuit.Wires);

        circuit.Remove(r2);

        Assert.Empty(circuit.Wires);
        Assert.Single(circuit.Components);
    }

    [Fact]
    public void ComponentsAreAutoNamedByDesignatorPrefix()
    {
        var circuit = new Circuit();
        var r1 = circuit.Add(new Resistor());
        var r2 = circuit.Add(new Resistor());
        var c1 = circuit.Add(new Capacitor());

        Assert.Equal("R1", r1.Name);
        Assert.Equal("R2", r2.Name);
        Assert.Equal("C1", c1.Name);
    }
}
