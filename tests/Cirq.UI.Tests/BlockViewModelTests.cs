using Cirq.Components.Hierarchy;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Looking inside a block, and measuring in there.
/// <para>
/// The measurement is the half that matters: a block's contents take part in the solve as ordinary
/// parts, so a node inside one is a real node with a real voltage — and until this window there was
/// no way to click it, because the canvas only draws what is on the sheet. The tests here follow a
/// probe all the way through: attached inside, resolved by the simulator, reading the voltage the
/// divider's arithmetic says it should.
/// </para>
/// </summary>
public class BlockViewModelTests
{
    /// <summary>A 12 V supply into a 1k/1k divider, with the divider grouped into a block.</summary>
    private static (Circuit Circuit, Subcircuit Block, Resistor Top, Resistor Bottom) Divider()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(12.0) { Name = "V1" });
        var upper = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var lower = circuit.Add(new Resistor(1e3) { Name = "R2" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, upper.A);
        circuit.Connect(upper.B, lower.A);
        circuit.Connect(lower.B, ground.Pin);

        var block = Grouping.Group(circuit, [upper, lower], "Divider")!;

        return (circuit, block, upper, lower);
    }

    [Fact]
    public void TheContentsAreTheBlocksOwnParts()
    {
        var (circuit, block, top, bottom) = Divider();
        var vm = new BlockViewModel(block, circuit);

        Assert.Equal(block.InnerComponents.Count, vm.Contents.Components.Count);
        Assert.Contains(top, vm.Contents.Components);
        Assert.Contains(bottom, vm.Contents.Components);

        // The same objects, so a value edited in the window is edited in the block.
        vm.Contents.Components.OfType<Resistor>().First(r => r.Name == "R1").Resistance = 4.7e3;

        Assert.Equal(4.7e3, top.Resistance, 1e-9);
    }

    [Fact]
    public void ItSaysWhatIsInThere()
    {
        var (circuit, block, _, _) = Divider();

        var vm = new BlockViewModel(block, circuit);

        Assert.Contains("2 part(s)", vm.Summary);

        // And the window is titled with both the designator and what the block is.
        Assert.Equal("X1 — Divider", vm.Name);
    }

    [Fact]
    public void AnEmptyBlockSaysSo()
    {
        var circuit = new Circuit();
        var block = circuit.Add(new Subcircuit { Name = "U1" });

        Assert.Contains("nothing in it", new BlockViewModel(block, circuit).Summary);
    }

    // ---- probing inside ------------------------------------------------------

    [Fact]
    public void AProbeAttachedInsideGoesOnTheCircuitOutside()
    {
        var (circuit, block, top, _) = Divider();
        var vm = new BlockViewModel(block, circuit);

        var announced = 0;
        vm.ProbesChanged += (_, _) => announced++;

        var probe = vm.Probe(top.B);

        // On the parent, which is the circuit being simulated and the one the scope shows.
        Assert.Contains(probe, circuit.Probes);
        Assert.Same(top.B, probe.TargetTerminal);
        Assert.Equal(1, announced);

        // And drawn in the window as well.
        Assert.Contains(probe, vm.Contents.Probes);

        // Named for where it is — the block's designator and then the terminal — because "R1.B"
        // alone, on a sheet with no R1 on it, is a label nobody can place.
        Assert.Equal("X1 · R1.B", probe.Label);
    }

    [Fact]
    public void ProbingTheSameNodeTwiceIsOneProbe()
    {
        var (circuit, block, top, _) = Divider();
        var vm = new BlockViewModel(block, circuit);

        var first = vm.Probe(top.B);
        var again = vm.Probe(top.B);

        Assert.Same(first, again);
        Assert.Single(circuit.Probes);
    }

    /// <summary>
    /// The whole point, followed through: the probe resolves against the netlist — which opens
    /// blocks out before it resolves anything — and reads the six volts the divider makes.
    /// </summary>
    [Fact]
    public void TheProbeReadsTheNodeInsideTheBlock()
    {
        var (circuit, block, top, _) = Divider();
        var vm = new BlockViewModel(block, circuit);

        var probe = vm.Probe(top.B);

        var simulator = new CircuitSimulator(circuit);

        simulator.Reset();
        simulator.SolveOperatingPoint();
        simulator.ResolveProbes();

        Assert.Equal(6.0, simulator.SampleProbe(probe), 1e-6);
    }

    /// <summary>
    /// And it survives a save. The serializer loads a block's contents into the circuit before
    /// moving them inside, so a probe pointing at one is resolved like any other.
    /// </summary>
    [Fact]
    public void AProbeInsideABlockSurvivesARoundTrip()
    {
        var (circuit, block, top, _) = Divider();

        new BlockViewModel(block, circuit).Probe(top.B);

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit;

        var probe = Assert.Single(back.Probes);

        Assert.NotNull(probe.TargetTerminal);

        // It points at something inside the block rather than at something on the sheet.
        var reopened = back.Components.OfType<Subcircuit>().Single();

        Assert.Contains(probe.TargetTerminal!.Owner!, reopened.InnerComponents);

        // And it still reads six volts.
        var simulator = new CircuitSimulator(back);

        simulator.Reset();
        simulator.SolveOperatingPoint();
        simulator.ResolveProbes();

        Assert.Equal(6.0, simulator.SampleProbe(probe), 1e-6);
    }

    [Fact]
    public void ProbesAlreadyPointingInsideAreShownWhenItIsOpened()
    {
        var (circuit, block, top, _) = Divider();

        circuit.Probes.Add(new SignalProbe("Mid", top.B, default));

        var vm = new BlockViewModel(block, circuit);

        Assert.Single(vm.Contents.Probes);
    }

    [Fact]
    public void AProbeOnTheSheetIsNotDrawnInsideTheBlock()
    {
        var (circuit, block, _, _) = Divider();

        var supply = circuit.Components.OfType<DcVoltageSource>().Single();

        circuit.Probes.Add(new SignalProbe("Rail", supply.Positive, default));

        Assert.Empty(new BlockViewModel(block, circuit).Contents.Probes);
    }
}
