using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// Net labels: two labels with the same name are one net, and the circuit solves as though a wire
/// ran between them.
/// </summary>
public class NetLabelTests
{
    [Fact]
    public void TwoLabelsWithTheSameNameAreOneNet()
    {
        var circuit = new Circuit();
        var left = circuit.Add(new Resistor(1e3));
        var right = circuit.Add(new Resistor(1e3));
        var a = circuit.Add(new NetLabel("BUS"));
        var b = circuit.Add(new NetLabel("BUS"));

        circuit.Connect(left.B, a.Pin);
        circuit.Connect(right.A, b.Pin);

        var netlist = circuit.BuildNetlist();

        Assert.Equal(netlist.IndexOf(left.B), netlist.IndexOf(right.A));
    }

    [Fact]
    public void DifferentNamesStayApart()
    {
        var circuit = new Circuit();
        var left = circuit.Add(new Resistor(1e3));
        var right = circuit.Add(new Resistor(1e3));

        circuit.Connect(left.B, circuit.Add(new NetLabel("ONE")).Pin);
        circuit.Connect(right.A, circuit.Add(new NetLabel("TWO")).Pin);

        var netlist = circuit.BuildNetlist();

        Assert.NotEqual(netlist.IndexOf(left.B), netlist.IndexOf(right.A));
    }

    /// <summary>
    /// VCC, Vcc and "vcc " are the same rail to everybody except a string comparison.
    /// </summary>
    [Theory]
    [InlineData("VCC", "vcc")]
    [InlineData("Reset", "RESET")]
    [InlineData(" CLK", "CLK ")]
    public void NamesMatchWithoutRegardToCaseOrSurroundingSpace(string first, string second)
    {
        var circuit = new Circuit();
        var left = circuit.Add(new Resistor(1e3));
        var right = circuit.Add(new Resistor(1e3));

        circuit.Connect(left.B, circuit.Add(new NetLabel(first)).Pin);
        circuit.Connect(right.A, circuit.Add(new NetLabel(second)).Pin);

        var netlist = circuit.BuildNetlist();

        Assert.Equal(netlist.IndexOf(left.B), netlist.IndexOf(right.A));
    }

    [Fact]
    public void ABlankLabelConnectsToNothing()
    {
        var circuit = new Circuit();
        var left = circuit.Add(new Resistor(1e3));
        var right = circuit.Add(new Resistor(1e3));

        circuit.Connect(left.B, circuit.Add(new NetLabel(string.Empty)).Pin);
        circuit.Connect(right.A, circuit.Add(new NetLabel("   ")).Pin);

        var netlist = circuit.BuildNetlist();

        Assert.NotEqual(netlist.IndexOf(left.B), netlist.IndexOf(right.A));
    }

    [Fact]
    public void TheNetTakesItsNameFromTheLabelOnIt()
    {
        var circuit = new Circuit();
        var resistor = circuit.Add(new Resistor(1e3));
        circuit.Connect(resistor.B, circuit.Add(new NetLabel("SENSE")).Pin);

        var netlist = circuit.BuildNetlist();

        Assert.Equal("SENSE", netlist.NetOf(resistor.B).Name);
    }

    /// <summary>
    /// The real test: a circuit that only works if the labels are carrying the connection, solved
    /// against the answer the same circuit gives when it is wired instead.
    /// </summary>
    [Fact]
    public void ADividerSplitAcrossLabelsSolvesTheSameAsOneWiredTogether()
    {
        double Midpoint(bool byLabel)
        {
            var circuit = new Circuit();
            var supply = circuit.Add(new DcVoltageSource(9.0));
            var top = circuit.Add(new Resistor(2e3));
            var bottom = circuit.Add(new Resistor(1e3));
            var ground = circuit.Add(new Ground());

            circuit.Connect(supply.Negative, ground.Pin);
            circuit.Connect(supply.Positive, top.A);
            circuit.Connect(bottom.B, ground.Pin);

            if (byLabel)
            {
                circuit.Connect(top.B, circuit.Add(new NetLabel("MID")).Pin);
                circuit.Connect(bottom.A, circuit.Add(new NetLabel("MID")).Pin);
            }
            else
            {
                circuit.Connect(top.B, bottom.A);
            }

            var sim = new CircuitSimulator(circuit);
            sim.Reset();
            sim.SolveOperatingPoint();

            return sim.NodeVoltage(top.B);
        }

        // 9 V across 3k, tapped at 1k: three volts.
        Assert.Equal(3.0, Midpoint(byLabel: false), 6);
        Assert.Equal(3.0, Midpoint(byLabel: true), 6);
    }

    [Fact]
    public void ThreeLabelsWithOneNameMakeOneNetNotTwo()
    {
        var circuit = new Circuit();
        var one = circuit.Add(new Resistor(1e3));
        var two = circuit.Add(new Resistor(1e3));
        var three = circuit.Add(new Resistor(1e3));

        circuit.Connect(one.B, circuit.Add(new NetLabel("RAIL")).Pin);
        circuit.Connect(two.A, circuit.Add(new NetLabel("RAIL")).Pin);
        circuit.Connect(three.A, circuit.Add(new NetLabel("RAIL")).Pin);

        var netlist = circuit.BuildNetlist();

        Assert.Equal(netlist.IndexOf(one.B), netlist.IndexOf(two.A));
        Assert.Equal(netlist.IndexOf(two.A), netlist.IndexOf(three.A));
    }

    /// <summary>
    /// A label on a net that also has a ground on it does not stop it being ground — ground wins,
    /// as it must, since it is the datum everything else is measured against.
    /// </summary>
    [Fact]
    public void ALabelledNetThatTouchesGroundIsStillGround()
    {
        var circuit = new Circuit();
        var resistor = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(resistor.B, ground.Pin);
        circuit.Connect(resistor.B, circuit.Add(new NetLabel("AGND")).Pin);

        var netlist = circuit.BuildNetlist();

        Assert.Equal(Netlist.GroundIndex, netlist.IndexOf(resistor.B));
        Assert.True(netlist.NetOf(resistor.B).IsGround);
    }

    [Fact]
    public void ALabelStampsNothingSoItCannotChangeAnAnswer()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var load = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, ground.Pin);
        circuit.Connect(load.A, circuit.Add(new NetLabel("OUT")).Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        // Still the whole five volts: a label draws no current.
        Assert.Equal(5.0, sim.NodeVoltage(load.A), 9);
    }
}
