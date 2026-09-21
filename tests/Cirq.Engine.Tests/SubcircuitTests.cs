using Cirq.Components.Hierarchy;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// Blocks. The test that matters is that grouping changes nothing: a circuit with part of it
/// drawn as a block has to solve to the same numbers as the same circuit drawn loose, because it
/// is the same circuit.
/// </summary>
public class SubcircuitTests
{
    /// <summary>
    /// A supply feeding a divider, with a load across the lower half. The divider is what gets
    /// grouped; the supply and the load stay outside, so two wires cross the boundary.
    /// </summary>
    private static (Circuit Circuit, Resistor Top, Resistor Bottom, Resistor Load, Terminal Mid)
        Divider()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(12.0));
        var top = circuit.Add(new Resistor(1e3));
        var bottom = circuit.Add(new Resistor(1e3));
        var load = circuit.Add(new Resistor(10e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);
        circuit.Connect(load.A, top.B);
        circuit.Connect(load.B, ground.Pin);

        return (circuit, top, bottom, load, top.B);
    }

    private static double Solve(Circuit circuit, Terminal at)
    {
        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return sim.NodeVoltage(at);
    }

    // ---- the whole point ---------------------------------------------------

    [Fact]
    public void GroupingPartOfACircuitDoesNotChangeWhatItSolvesTo()
    {
        var (circuit, top, bottom, _, mid) = Divider();

        var loose = Solve(circuit, mid);

        var block = Grouping.Group(circuit, [top, bottom], "Divider");

        Assert.NotNull(block);

        // The same node, still reachable — the parts were moved, not replaced.
        Assert.Equal(loose, Solve(circuit, mid), 12);
    }

    [Fact]
    public void AndUngroupingItDoesNotEither()
    {
        var (circuit, top, bottom, _, mid) = Divider();

        var loose = Solve(circuit, mid);

        var block = Grouping.Group(circuit, [top, bottom], "Divider")!;
        var released = Grouping.Ungroup(circuit, block);

        Assert.Equal(loose, Solve(circuit, mid), 12);

        // The same objects came back, not copies of them.
        Assert.Contains(top, released);
        Assert.Contains(bottom, released);
        Assert.DoesNotContain(block, circuit.Components);
    }

    // ---- the boundary ------------------------------------------------------

    [Fact]
    public void APinAppearsWhereverAWireCrossedTheBoundary()
    {
        var (circuit, top, bottom, _, _) = Divider();

        var block = Grouping.Group(circuit, [top, bottom], "Divider")!;

        // Three crossings: the supply into the top, the load onto the midpoint, and the bottom to
        // ground. The midpoint is one pin however many outside wires land on it.
        Assert.Equal(3, block.Ports.Count);
        Assert.Equal(block.Ports.Count, block.Terminals.Count);

        // Named after where they came from, which is what makes a block readable from outside.
        Assert.All(block.Ports, p => Assert.Contains(".", p.Outer.Name));
    }

    [Fact]
    public void TwoWiresOntoOneInnerTerminalShareOnePin()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var inner = circuit.Add(new Resistor(1e3));
        var second = circuit.Add(new Resistor(2e3));
        var a = circuit.Add(new Resistor(3e3));
        var b = circuit.Add(new Resistor(4e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, inner.A);
        circuit.Connect(inner.B, second.A);
        circuit.Connect(second.B, ground.Pin);

        // Two separate outside parts, both landing on the same inner terminal.
        circuit.Connect(a.A, inner.B);
        circuit.Connect(b.A, inner.B);
        circuit.Connect(a.B, ground.Pin);
        circuit.Connect(b.B, ground.Pin);

        var block = Grouping.Group(circuit, [inner, second], "Pair")!;

        // Supply into inner.A, ground off second.B, and one shared pin at inner.B.
        Assert.Equal(3, block.Ports.Count);
    }

    [Fact]
    public void WiresWhollyInsideGoInsideAndAreNotLeftOnTheSheet()
    {
        var (circuit, top, bottom, _, _) = Divider();

        var wiresBefore = circuit.Wires.Count;

        var block = Grouping.Group(circuit, [top, bottom], "Divider")!;

        // The one between the two resistors moved in.
        Assert.Single(block.InnerWires);
        Assert.Equal(wiresBefore - 1, circuit.Wires.Count);
    }

    [Fact]
    public void GroupingFewerThanTwoPartsDoesNothing()
    {
        var (circuit, top, _, _, _) = Divider();

        Assert.Null(Grouping.Group(circuit, [top], "One"));
        Assert.Null(Grouping.Group(circuit, [], "None"));
        Assert.Contains(top, circuit.Components);
    }

    // ---- what is inside still works ----------------------------------------

    [Fact]
    public void ANonLinearPartInsideABlockStillSolvesAsItself()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var series = circuit.Add(new Resistor(1e3));
        var diode = circuit.Add(new Diode());
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, series.A);
        circuit.Connect(series.B, diode.Anode);
        circuit.Connect(diode.Cathode, ground.Pin);

        var loose = Solve(circuit, diode.Anode);

        Grouping.Group(circuit, [series, diode], "Clamp");

        Assert.Equal(loose, Solve(circuit, diode.Anode), 9);

        // And it is a real diode drop rather than something linear.
        Assert.InRange(loose, 0.4, 0.9);
    }

    [Fact]
    public void ATransientRunsThroughABlockUnchanged()
    {
        double Settled(bool grouped)
        {
            var circuit = new Circuit();
            var supply = circuit.Add(new DcVoltageSource(5.0));
            var resistor = circuit.Add(new Resistor(1e3));
            var capacitor = circuit.Add(new Capacitor(1e-6));
            var ground = circuit.Add(new Ground());

            circuit.Connect(supply.Negative, ground.Pin);
            circuit.Connect(supply.Positive, resistor.A);
            circuit.Connect(resistor.B, capacitor.A);
            circuit.Connect(capacitor.B, ground.Pin);

            if (grouped) Grouping.Group(circuit, [resistor, capacitor], "RC");

            var sim = new CircuitSimulator(circuit);
            sim.Settings.UseInitialConditions = true;
            sim.Reset();
            sim.SolveOperatingPoint();
            sim.Run(1e-3);

            return sim.NodeVoltage(capacitor.A);
        }

        // One time constant is 1 ms, so it should be at about 63 % either way.
        var loose = Settled(false);

        Assert.Equal(loose, Settled(true), 6);
        Assert.InRange(loose, 5.0 * 0.6, 5.0 * 0.68);
    }

    // ---- blocks inside blocks ----------------------------------------------

    [Fact]
    public void ABlockInsideABlockIsFlattenedTheSameWay()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(9.0));
        var one = circuit.Add(new Resistor(1e3));
        var two = circuit.Add(new Resistor(1e3));
        var three = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, one.A);
        circuit.Connect(one.B, two.A);
        circuit.Connect(two.B, three.A);
        circuit.Connect(three.B, ground.Pin);

        var loose = Solve(circuit, two.A);

        // Group two of them, then group that block with the third.
        var inner = Grouping.Group(circuit, [two, three], "Lower")!;
        var outer = Grouping.Group(circuit, [one, inner], "All")!;

        Assert.Contains(inner, outer.InnerComponents);

        // Four descendants: the resistor, the nested block, and the two resistors inside that.
        // The block counts — it is a component of the document, even though it stamps nothing.
        Assert.Equal(4, outer.Descendants().Count());
        Assert.Equal(3, outer.Descendants().OfType<Resistor>().Count());

        // Three equal resistors across nine volts, tapped after the first: six volts.
        Assert.Equal(loose, Solve(circuit, two.A), 9);
        Assert.Equal(6.0, loose, 6);
    }

    [Fact]
    public void TheFlattenedListHasEveryPartAtEveryDepthExactlyOnce()
    {
        var circuit = new Circuit();
        var a = circuit.Add(new Resistor(1e3));
        var b = circuit.Add(new Resistor(2e3));
        var c = circuit.Add(new Resistor(3e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(a.B, b.A);
        circuit.Connect(b.B, c.A);
        circuit.Connect(c.B, ground.Pin);

        var inner = Grouping.Group(circuit, [b, c], "Inner")!;
        var outer = Grouping.Group(circuit, [a, inner], "Outer")!;

        var flat = Flattening.Flatten(circuit.Components).ToList();

        foreach (var part in new CircuitComponent[] { a, b, c, ground, inner, outer })
            Assert.Single(flat, x => ReferenceEquals(x, part));
    }
}

/// <summary>A block has to survive a save, or grouping is something you do once and lose.</summary>
public class SubcircuitSerializationTests
{
    private static (Circuit, Terminal Mid) Built()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(12.0));
        var top = circuit.Add(new Resistor(1e3));
        var bottom = circuit.Add(new Resistor(3e3));
        var load = circuit.Add(new Resistor(100e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);
        circuit.Connect(load.A, top.B);
        circuit.Connect(load.B, ground.Pin);

        Grouping.Group(circuit, [top, bottom], "Divider");

        return (circuit, top.B);
    }

    private static double Solve(Circuit circuit, Terminal at)
    {
        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return sim.NodeVoltage(at);
    }

    [Fact]
    public void ABlockComesBackWithItsContentsItsWiresAndItsPins()
    {
        var (circuit, _) = Built();

        var json = Cirq.Components.Serialization.CircuitSerializer.ToJson(circuit);
        var result = Cirq.Components.Serialization.CircuitSerializer.FromJson(json);

        Assert.Empty(result.Warnings);

        var block = result.Circuit.Components.OfType<Subcircuit>().Single();

        Assert.Equal("Divider", block.BlockName);
        Assert.Equal(2, block.InnerComponents.Count);
        Assert.Single(block.InnerWires);
        Assert.Equal(3, block.Ports.Count);

        // The contents are inside the block and not also loose on the sheet.
        Assert.DoesNotContain(result.Circuit.Components, c => block.InnerComponents.Contains(c));
    }

    /// <summary>The test that actually matters: it still solves to the same numbers.</summary>
    [Fact]
    public void AndItStillSolvesToTheSameAnswer()
    {
        var (circuit, mid) = Built();

        var before = Solve(circuit, mid);

        var json = Cirq.Components.Serialization.CircuitSerializer.ToJson(circuit);
        var reloaded = Cirq.Components.Serialization.CircuitSerializer.FromJson(json).Circuit;

        var block = reloaded.Components.OfType<Subcircuit>().Single();

        // The midpoint is the far end of the upper resistor, which is inside the block. Any
        // terminal in the flattened netlist can be read, port or not.
        var upper = block.InnerComponents.OfType<Resistor>().Single(r => r.Resistance == 1e3);
        var innerMid = upper.B;

        // 12 V across 4k tapped at 3k is 9 V, give or take the 100k load.
        Assert.InRange(before, 8.9, 9.0);
        Assert.Equal(before, Solve(reloaded, innerMid), 9);
    }

    [Fact]
    public void ABlockInsideABlockSurvivesToo()
    {
        var circuit = new Circuit();
        var a = circuit.Add(new Resistor(1e3));
        var b = circuit.Add(new Resistor(2e3));
        var c = circuit.Add(new Resistor(3e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(a.B, b.A);
        circuit.Connect(b.B, c.A);
        circuit.Connect(c.B, ground.Pin);

        var inner = Grouping.Group(circuit, [b, c], "Inner")!;
        Grouping.Group(circuit, [a, inner], "Outer");

        var json = Cirq.Components.Serialization.CircuitSerializer.ToJson(circuit);
        var reloaded = Cirq.Components.Serialization.CircuitSerializer.FromJson(json).Circuit;

        var outer = Assert.Single(reloaded.Components.OfType<Subcircuit>());

        Assert.Equal("Outer", outer.BlockName);

        var nested = Assert.Single(outer.InnerComponents.OfType<Subcircuit>());

        Assert.Equal("Inner", nested.BlockName);
        Assert.Equal(2, nested.InnerComponents.Count);
    }

    [Fact]
    public void AFileWithNoBlocksInItSaysNothingAboutThem()
    {
        var circuit = new Circuit();
        var resistor = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());
        circuit.Connect(resistor.B, ground.Pin);

        Assert.DoesNotContain("\"block\"", Cirq.Components.Serialization.CircuitSerializer.ToJson(circuit),
            StringComparison.OrdinalIgnoreCase);
    }
}
