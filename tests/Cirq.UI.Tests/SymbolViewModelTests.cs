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
/// Drawing a block's symbol.
/// <para>
/// Every block used to be the same grey box with its pins alternating down the sides — fine for
/// hiding a section, useless for making a part, because what tells somebody at a glance that this
/// is an amplifier and that is a regulator is its shape and where its pins are.
/// </para>
/// <para>
/// The thing that must never break is that this is <b>only</b> drawing. A pin that has moved or been
/// renamed is the same pin: the wire on it is still on it, the probe watching it still watches it,
/// and the circuit solves to the same answer. Most of what is checked here is that.
/// </para>
/// </summary>
public class SymbolViewModelTests
{
    private static (Circuit Circuit, Subcircuit Block, Resistor Top, Resistor Bottom) Divider()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(12.0) { Name = "V1" });
        var top = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var bottom = circuit.Add(new Resistor(1e3) { Name = "R2" });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        return (circuit, Grouping.Group(circuit, [top, bottom], "Divider")!, top, bottom);
    }

    [Fact]
    public void ItStartsFromWhateverTheBlockLooksLikeNow()
    {
        var (_, block, _, _) = Divider();
        var vm = new SymbolViewModel(block);

        Assert.Equal("Divider", vm.Label);
        Assert.Equal(block.HalfWidth * 2, vm.Width);
        Assert.Equal(block.HalfHeight * 2, vm.Height);
        Assert.Equal(block.Ports.Count, vm.Pins.Count);

        // A block nobody has drawn puts its pins down the sides, alternating.
        Assert.Equal(PinSide.Left, vm.Pins[0].Side);
        Assert.Equal(PinSide.Right, vm.Pins[1].Side);
    }

    [Fact]
    public void ChangingTheSizeChangesTheSymbol()
    {
        var (_, block, _, _) = Divider();
        var vm = new SymbolViewModel(block);

        var changed = 0;
        vm.Changed += (_, _) => changed++;

        vm.Width = 200;
        vm.Height = 120;

        Assert.Equal(100, block.HalfWidth);
        Assert.Equal(60, block.HalfHeight);
        Assert.True(block.HasOwnSymbol);
        Assert.True(changed >= 2);
    }

    [Fact]
    public void APinGoesOnWhicheverEdgeItIsPutOn()
    {
        var (_, block, _, _) = Divider();
        var vm = new SymbolViewModel(block) { Width = 160, Height = 100 };

        vm.Pins[0].Side = PinSide.Top;
        vm.Pins[0].Along = -30;

        var pin = block.Ports[0].Outer;

        Assert.Equal(-30, pin.CanvasOffset.X, 1e-9);
        Assert.Equal(-50, pin.CanvasOffset.Y, 1e-9);

        vm.Pins[1].Side = PinSide.Bottom;
        vm.Pins[1].Along = 20;

        Assert.Equal(20, block.Ports[1].Outer.CanvasOffset.X, 1e-9);
        Assert.Equal(50, block.Ports[1].Outer.CanvasOffset.Y, 1e-9);
    }

    /// <summary>
    /// A pin cannot be further along an edge than the edge is long, however hard somebody types.
    /// </summary>
    [Fact]
    public void APinStaysOnTheBody()
    {
        var (_, block, _, _) = Divider();
        var vm = new SymbolViewModel(block) { Width = 120, Height = 80 };

        vm.Pins[0].Side = PinSide.Left;
        vm.Pins[0].Along = 900;

        Assert.InRange(block.Ports[0].Outer.CanvasOffset.Y, -40, 40);
    }

    /// <summary>The thing that must never break: moving a pin is drawing, not rewiring.</summary>
    [Fact]
    public void MovingAPinChangesNothingElectrical()
    {
        var (circuit, block, top, _) = Divider();

        // Something wired to the block from outside, and a probe watching a node inside it.
        var probe = new SignalProbe("Mid", top.B, default);
        circuit.Probes.Add(probe);

        double Solve()
        {
            var simulator = new CircuitSimulator(circuit);

            simulator.Reset();
            simulator.SolveOperatingPoint();
            simulator.ResolveProbes();

            return simulator.SampleProbe(probe);
        }

        var before = Solve();

        var vm = new SymbolViewModel(block) { Width = 220, Height = 160 };

        vm.Pins[0].Side = PinSide.Top;
        vm.Pins[0].Name = "IN";
        vm.Pins[1].Side = PinSide.Bottom;
        vm.Pins[1].Name = "OUT";

        Assert.Equal(before, Solve(), 1e-9);

        // The wires are still on the pins they were on.
        Assert.All(circuit.Wires, w =>
        {
            Assert.NotNull(w.SourceTerminal);
            Assert.NotNull(w.TargetTerminal);
        });

        Assert.Equal(6.0, before, 1e-6);
    }

    [Fact]
    public void RenamingAPinRenamesWhatIsDrawnAndNothingElse()
    {
        var (circuit, block, _, _) = Divider();
        var vm = new SymbolViewModel(block);

        var pin = block.Ports[0].Outer;
        var uid = pin.Uid;

        vm.Pins[0].Name = "IN+";

        Assert.Equal("IN+", pin.Name);
        Assert.Equal(uid, pin.Uid);

        // Still the same terminal as far as everything else is concerned.
        Assert.Contains(circuit.Wires, w => pin.Equals(w.SourceTerminal) || pin.Equals(w.TargetTerminal));
    }

    [Fact]
    public void ArrangeForMePutsThemBackDownTheSides()
    {
        var (_, block, _, _) = Divider();
        var vm = new SymbolViewModel(block) { Width = 260, Height = 200 };

        vm.Pins[0].Side = PinSide.Top;
        vm.Pins[1].Side = PinSide.Bottom;

        vm.ArrangeCommand.Execute(null);

        Assert.Equal(PinSide.Left, vm.Pins[0].Side);
        Assert.Equal(PinSide.Right, vm.Pins[1].Side);
        Assert.False(block.HasOwnSymbol);
    }

    /// <summary>A symbol somebody drew is part of the drawing, so it is saved with it.</summary>
    [Fact]
    public void ADrawnSymbolSurvivesARoundTrip()
    {
        var (circuit, block, _, _) = Divider();

        var vm = new SymbolViewModel(block) { Label = "Attenuator", Width = 180, Height = 140 };

        vm.Pins[0].Side = PinSide.Top;
        vm.Pins[0].Along = -40;
        vm.Pins[0].Name = "IN";

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit;
        var reopened = back.Components.OfType<Subcircuit>().Single();

        Assert.Equal("Attenuator", reopened.BlockName);
        Assert.Equal(180, reopened.SymbolWidth);
        Assert.Equal(140, reopened.SymbolHeight);
        Assert.True(reopened.HasOwnSymbol);

        var pin = reopened.Ports[0].Outer;

        Assert.Equal("IN", pin.Name);
        Assert.Equal(-40, pin.CanvasOffset.X, 1e-9);
        Assert.Equal(-70, pin.CanvasOffset.Y, 1e-9);
    }

    /// <summary>
    /// And a block nobody has drawn writes nothing about its symbol, so a file from before this
    /// existed is byte-for-byte what it was.
    /// </summary>
    [Fact]
    public void AnUndrawnBlockSaysNothingAboutItsSymbol()
    {
        var (circuit, _, _, _) = Divider();

        var json = CircuitSerializer.ToJson(circuit);

        Assert.DoesNotContain("\"Width\"", json);
        Assert.DoesNotContain("\"Height\"", json);
    }

    [Fact]
    public void ThePreviewIsTheBlockItself()
    {
        var (_, block, _, _) = Divider();
        var vm = new SymbolViewModel(block);

        Assert.Same(block, Assert.Single(vm.Preview.Components));
    }
}
