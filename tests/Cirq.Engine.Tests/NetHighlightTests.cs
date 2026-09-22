using Cirq.Components.Hierarchy;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Engine.Tests;

/// <summary>
/// Picking a net out on the drawing: everything that is electrically the same point.
/// <para>
/// The cases that matter are the ones where the drawing does not show it. A rail named
/// <c>VCC</c> in six places is six pieces of text with no line between them; a block's pin joins
/// to something inside that is not on the sheet at all. Following the wires as drawn gets both
/// wrong, so this asks the netlist — the same one the solver uses.
/// </para>
/// </summary>
public class NetHighlightTests
{
    [Fact]
    public void AWireLightsUpBothEndsAndEverythingJoinedToThem()
    {
        var circuit = new Circuit();

        var r1 = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var r2 = circuit.Add(new Resistor(2e3) { Name = "R2" });
        var r3 = circuit.Add(new Resistor(3e3) { Name = "R3" });

        // R1 - R2 - R3 in a chain, so the middle node touches two parts and the far one none.
        var joined = circuit.Connect(r1.B, r2.A);
        circuit.Connect(r2.B, r3.A);

        var net = NetHighlighting.For(circuit, joined);

        Assert.NotNull(net);
        Assert.Equal(["R1", "R2"], net.Components);
        Assert.Contains(joined, net.Wires);
        Assert.Equal(2, net.Terminals.Count);
    }

    /// <summary>
    /// The case the feature exists for. Two points joined only by having the same net label have
    /// nothing on the drawing between them, and this is the only way to see that they are one net.
    /// </summary>
    [Fact]
    public void PointsJoinedOnlyByANameAreOnTheSameNet()
    {
        var circuit = new Circuit();

        var r1 = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var r2 = circuit.Add(new Resistor(2e3) { Name = "R2" });

        var labelA = circuit.Add(new NetLabel("VCC") { Name = "L1" });
        var labelB = circuit.Add(new NetLabel("VCC") { Name = "L2" });

        // No wire between the two halves at all.
        var wireA = circuit.Connect(r1.A, labelA.Pin);
        var wireB = circuit.Connect(r2.A, labelB.Pin);

        var net = NetHighlighting.For(circuit, wireA);

        Assert.NotNull(net);

        Assert.Contains("R1", net.Components);
        Assert.Contains("R2", net.Components);

        // Both wires light up, though neither touches the other.
        Assert.Contains(wireA, net.Wires);
        Assert.Contains(wireB, net.Wires);

        // And the net is called what the label calls it.
        Assert.Equal("VCC", net.Name);
    }

    /// <summary>Two different names are two different nets, which is the other half of the claim.</summary>
    [Fact]
    public void PointsUnderDifferentNamesAreNotJoined()
    {
        var circuit = new Circuit();

        var r1 = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var r2 = circuit.Add(new Resistor(2e3) { Name = "R2" });

        var wireA = circuit.Connect(r1.A, circuit.Add(new NetLabel("VCC")).Pin);
        circuit.Connect(r2.A, circuit.Add(new NetLabel("VDD")).Pin);

        var net = NetHighlighting.For(circuit, wireA);

        Assert.NotNull(net);
        Assert.Contains("R1", net.Components);
        Assert.DoesNotContain("R2", net.Components);
    }

    /// <summary>
    /// A part inside a block is on the net too — the block is only a line drawn round part of the
    /// drawing — but its internal wiring is not on the sheet, so nothing invisible is lit up.
    /// </summary>
    [Fact]
    public void ABlocksPinReachesWhatIsInsideIt()
    {
        var circuit = new Circuit();

        var inner1 = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var inner2 = circuit.Add(new Resistor(2e3) { Name = "R2" });
        circuit.Connect(inner1.B, inner2.A);

        var outside = circuit.Add(new Resistor(3e3) { Name = "R3" });
        var crossing = circuit.Connect(inner1.A, outside.A);

        var block = Grouping.Group(circuit, [inner1, inner2], "Stage");

        Assert.NotNull(block);

        // The wire that crossed the boundary is still on the sheet, now reaching the block's pin.
        var net = NetHighlighting.For(circuit, crossing);

        Assert.NotNull(net);

        Assert.Contains("R1", net.Components);
        Assert.Contains("R3", net.Components);

        // Only sheet wires light up: the block's insides are not drawn.
        Assert.All(net.Wires, w => Assert.Contains(w, circuit.Wires));
    }

    [Fact]
    public void TheGroundNetKnowsItIsGround()
    {
        var circuit = new Circuit();

        var resistor = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        var wire = circuit.Connect(resistor.B, ground.Pin);

        var net = NetHighlighting.For(circuit, wire);

        Assert.NotNull(net);
        Assert.True(net.IsGround);
        Assert.Equal("GND", net.Name);
    }

    /// <summary>A part joined to a net twice is still one part, not two entries.</summary>
    [Fact]
    public void APartOnANetTwiceIsNamedOnce()
    {
        var circuit = new Circuit();

        var resistor = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var other = circuit.Add(new Resistor(2e3) { Name = "R2" });

        // Both ends of R2 onto the same node, which is a short but a legal drawing.
        var wire = circuit.Connect(resistor.A, other.A);
        circuit.Connect(other.A, other.B);

        var net = NetHighlighting.For(circuit, wire);

        Assert.NotNull(net);
        Assert.Equal(["R1", "R2"], net.Components);
    }

    /// <summary>The summary is the answer, so it names what is on the net rather than counting it.</summary>
    [Fact]
    public void TheSummaryNamesWhatIsOnTheNet()
    {
        var circuit = new Circuit();

        var r1 = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var r2 = circuit.Add(new Resistor(2e3) { Name = "R2" });

        var wire = circuit.Connect(r1.A, r2.A);
        var net = NetHighlighting.For(circuit, wire);

        Assert.NotNull(net);
        Assert.Contains("R1", net.Summary);
        Assert.Contains("R2", net.Summary);
    }

    /// <summary>A long net is trimmed rather than filling the status bar, and says how many it left out.</summary>
    [Fact]
    public void ALongNetIsTrimmedAndSaysSo()
    {
        var circuit = new Circuit();

        var first = circuit.Add(new Resistor(1e3) { Name = "R1" });
        WireSegment? wire = null;

        for (var i = 2; i <= 12; i++)
        {
            var next = circuit.Add(new Resistor(1e3) { Name = $"R{i}" });
            wire ??= circuit.Connect(first.A, next.A);

            if (wire is not null && i > 2) circuit.Connect(first.A, next.A);
        }

        var net = NetHighlighting.For(circuit, wire);

        Assert.NotNull(net);
        Assert.Contains("more", net.Summary);
    }

    // ---- refusing rather than guessing -------------------------------------

    [Fact]
    public void ATerminalThatIsNotInTheCircuitHasNoNet()
    {
        var circuit = new Circuit();
        circuit.Add(new Resistor(1e3));

        var stranger = new Resistor(1e3);

        Assert.Null(NetHighlighting.For(circuit, stranger.A));
    }

    [Fact]
    public void NothingAskedForIsNothingReturned()
    {
        var circuit = new Circuit();

        Assert.Null(NetHighlighting.For(circuit, (Terminal?)null));
        Assert.Null(NetHighlighting.For(circuit, (WireSegment?)null));
    }
}
