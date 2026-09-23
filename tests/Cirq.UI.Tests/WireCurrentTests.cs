using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;
using Cirq.UI.Services;

namespace Cirq.UI.Tests;

/// <summary>
/// Which wire is carrying what, and which way.
/// <para>
/// The direction is the part worth testing. A magnitude that is wrong looks wrong; a direction
/// that is wrong looks like a working feature showing a circuit that runs backwards, which is
/// worse than showing nothing at all.
/// </para>
/// </summary>
public class WireCurrentTests
{
    /// <summary>
    /// Ten volts across a kilohm: ten milliamps round the loop, and no doubt about which way.
    /// <c>V1(+) → R1 → ground → back to V1(−)</c>.
    /// </summary>
    private static (CircuitSimulator Sim, Circuit Circuit, WireSegment Feed, WireSegment Return,
        WireSegment Back, Resistor R) Loop()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var resistor = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        var feed = new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = resistor.A };
        var back = new WireSegment { SourceTerminal = resistor.B, TargetTerminal = ground.Pin };
        var ret = new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin };

        circuit.Wires.Add(feed);
        circuit.Wires.Add(back);
        circuit.Wires.Add(ret);

        var sim = new CircuitSimulator(circuit);

        sim.Reset();
        sim.SolveOperatingPoint();

        return (sim, circuit, feed, back, ret, resistor);
    }

    [Fact]
    public void ASeriesLoopCarriesTheSameCurrentAllTheWayRound()
    {
        var (sim, circuit, feed, back, ret, _) = Loop();

        var currents = WireCurrentsService.For(circuit, sim);

        Assert.Equal(3, currents.Count);

        foreach (var wire in (WireSegment[])[feed, back, ret])
            Assert.Equal(0.01, Math.Abs(currents[wire]), 1e-9);
    }

    /// <summary>
    /// The signs describe one circulation. Positive is from a wire's source end to its target
    /// end, so the feed and the drop to ground both run forwards and the return runs back the
    /// other way — which is the same loop written down twice.
    /// </summary>
    [Fact]
    public void TheDirectionsDescribeOneCirculation()
    {
        var (sim, circuit, feed, back, ret, _) = Loop();

        var currents = WireCurrentsService.For(circuit, sim);

        // Out of the supply and into the resistor.
        Assert.Equal(0.01, currents[feed], 1e-9);

        // Out of the resistor and into ground.
        Assert.Equal(0.01, currents[back], 1e-9);

        // And back up into the supply's other pin, which is the opposite way along this wire.
        Assert.Equal(-0.01, currents[ret], 1e-9);
    }

    /// <summary>
    /// The return wire touches nothing that can report a current — a ground has no model and a
    /// source's branch cannot say which of its pins was meant. It is known anyway, because what
    /// goes into a two-terminal part comes out of it, and that is what carries the answer round
    /// to the half of the loop nobody can measure directly.
    /// </summary>
    [Fact]
    public void TheReturnThroughGroundIsKnownByWhatMustFollow()
    {
        var (sim, circuit, _, _, ret, _) = Loop();

        var currents = WireCurrentsService.For(circuit, sim);

        Assert.True(currents.ContainsKey(ret));
        Assert.NotEqual(0.0, currents[ret]);
    }

    /// <summary>
    /// Reversing the supply reverses every dot on the drawing, and nothing else about it.
    /// </summary>
    [Fact]
    public void ReversingTheSupplyReversesEverything()
    {
        var (sim, circuit, feed, back, ret, _) = Loop();

        var forward = WireCurrentsService.For(circuit, sim);

        circuit.Components.OfType<DcVoltageSource>().Single().Voltage = -10.0;

        var reversed = new CircuitSimulator(circuit);

        reversed.Reset();
        reversed.SolveOperatingPoint();

        var backwards = WireCurrentsService.For(circuit, reversed);

        foreach (var wire in (WireSegment[])[feed, back, ret])
            Assert.Equal(-forward[wire], backwards[wire], 1e-9);
    }

    /// <summary>
    /// Where a junction divides the current, the share each wire takes depends on the whole rest
    /// of the circuit. Those wires are left out rather than guessed at — a drawing that split it
    /// evenly would be confidently wrong on the one part of a schematic somebody would trust it
    /// for.
    /// </summary>
    [Fact]
    public void AJunctionLeavesItsWiresUnknown()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0) { Name = "V1" });
        var top = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var left = circuit.Add(new Resistor(2e3) { Name = "R2" });
        var right = circuit.Add(new Resistor(3e3) { Name = "R3" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        // Three wires meeting on R1's lower pin, and a bridge between the two loads' upper pins,
        // so both ends of every wire in the middle land somewhere the current has already divided.
        var feed = new WireSegment { SourceTerminal = supply.Positive, TargetTerminal = top.A };
        var toLeft = new WireSegment { SourceTerminal = top.B, TargetTerminal = left.A };
        var toRight = new WireSegment { SourceTerminal = top.B, TargetTerminal = right.A };
        var bridge = new WireSegment { SourceTerminal = left.A, TargetTerminal = right.A };

        circuit.Wires.Add(feed);
        circuit.Wires.Add(toLeft);
        circuit.Wires.Add(toRight);
        circuit.Wires.Add(bridge);
        circuit.Wires.Add(new WireSegment { SourceTerminal = left.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = right.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = supply.Negative, TargetTerminal = ground.Pin });

        var sim = new CircuitSimulator(circuit);

        sim.Reset();
        sim.SolveOperatingPoint();

        var currents = WireCurrentsService.For(circuit, sim);

        // The feed is still knowable: it lands on a pin with one wire on it, belonging to a part
        // that can say what is going into it. Everything before a junction carries the whole.
        Assert.True(currents.ContainsKey(feed));

        // The bridge puts the two loads in parallel, so the whole is 1k in series with 2k∥3k.
        var parallel = 1.0 / ((1.0 / 2e3) + (1.0 / 3e3));
        var expected = 10.0 / (1e3 + parallel);

        Assert.Equal(expected, Math.Abs(currents[feed]), expected * 1e-6);

        // Past it, nothing is. Each of these has a divided node at both ends, and there is no way
        // to say what share went which way without solving the rest of the circuit for it.
        Assert.False(currents.ContainsKey(toLeft));
        Assert.False(currents.ContainsKey(toRight));
        Assert.False(currents.ContainsKey(bridge));
    }

    /// <summary>A circuit that has been built but not solved carries nothing, rather than stale
    /// numbers from whatever was on screen before.</summary>
    [Fact]
    public void AnUnsolvedCircuitCarriesNothing()
    {
        var (_, circuit, feed, _, _, _) = Loop();

        // Built, so the matrix exists, but never solved.
        var fresh = new CircuitSimulator(circuit);

        fresh.Reset();

        var currents = WireCurrentsService.For(circuit, fresh);

        Assert.Equal(0.0, currents[feed], 1e-12);
    }
}
