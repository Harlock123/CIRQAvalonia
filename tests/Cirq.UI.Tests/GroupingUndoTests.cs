using Cirq.Components.Hierarchy;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Undoing a grouping. A snapshot history is driven by change notifications, and grouping is
/// several notifications that only make sense together — so these are the tests that would have
/// caught the parts vanishing.
/// </summary>
public class GroupingUndoTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"cirq-blocks-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private (MainWindowViewModel, Resistor Top, Resistor Bottom) Rig()
    {
        var vm = new MainWindowViewModel { Blocks = new BlockLibrary(_path) };
        vm.Circuit.Clear();

        var supply = vm.Circuit.Add(new DcVoltageSource(12.0));
        var top = vm.Circuit.Add(new Resistor(1e3));
        var bottom = vm.Circuit.Add(new Resistor(3e3));
        var ground = vm.Circuit.Add(new Ground());

        vm.Circuit.Connect(supply.Negative, ground.Pin);
        vm.Circuit.Connect(supply.Positive, top.A);
        vm.Circuit.Connect(top.B, bottom.A);
        vm.Circuit.Connect(bottom.B, ground.Pin);

        return (vm, top, bottom);
    }

    private static void Group(MainWindowViewModel vm, params CircuitComponent[] parts)
    {
        foreach (var component in vm.Circuit.Components) component.IsSelected = parts.Contains(component);

        vm.GroupSelectionCommand.Execute(null);
    }

    /// <summary>
    /// The bug. Grouping removes each part and adds the block; recorded separately, one undo
    /// landed between those and the parts were gone from a state that never existed.
    /// </summary>
    [Fact]
    public void UndoingAGroupingGivesThePartsBackRatherThanLosingThem()
    {
        var (vm, top, bottom) = Rig();

        var before = vm.Circuit.Components.Count;

        Group(vm, top, bottom);

        Assert.Single(vm.Circuit.Components.OfType<Subcircuit>());

        vm.UndoCommand.Execute(null);

        // Everything back, and no block left behind.
        Assert.Equal(before, vm.Circuit.Components.Count);
        Assert.Empty(vm.Circuit.Components.OfType<Subcircuit>());
        Assert.Equal(2, vm.Circuit.Components.OfType<Resistor>().Count());
    }

    [Fact]
    public void AndTheCircuitStillSolvesToWhatItDidBeforeTheGrouping()
    {
        var (vm, top, bottom) = Rig();

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        // 12 V across 4k tapped at 3k.
        var midpoint = vm.Simulation.Simulator!.NodeVoltage(top.B);

        Assert.Equal(9.0, midpoint, 6);

        Group(vm, top, bottom);
        vm.UndoCommand.Execute(null);

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        // The restored parts are fresh objects, so the node is found through the new circuit.
        var restored = vm.Circuit.Components.OfType<Resistor>().Single(r => r.Resistance == 1e3);

        Assert.Equal(midpoint, vm.Simulation.Simulator!.NodeVoltage(restored.B), 6);
    }

    [Fact]
    public void GroupingCostsExactlyOneUndoStep()
    {
        var (vm, top, bottom) = Rig();

        Group(vm, top, bottom);

        Assert.Equal("Group into Block", vm.History.UndoLabel);

        // One step takes it all the way back; a second would have to be the edit before it.
        vm.UndoCommand.Execute(null);

        Assert.Equal(4, vm.Circuit.Components.Count);
        Assert.Contains("Group into Block", vm.StatusMessage);
    }

    [Fact]
    public void AndItCanBeRedone()
    {
        var (vm, top, bottom) = Rig();

        Group(vm, top, bottom);
        vm.UndoCommand.Execute(null);
        vm.RedoCommand.Execute(null);

        var block = Assert.Single(vm.Circuit.Components.OfType<Subcircuit>());

        Assert.Equal(2, block.InnerComponents.Count);

        // Two crossings: the supply into the top and the bottom out to ground. The midpoint stays
        // inside, which is the whole point of grouping the pair.
        Assert.Equal(2, block.Ports.Count);
        Assert.Empty(vm.Circuit.Components.OfType<Resistor>());
    }

    // ---- ungroup ------------------------------------------------------------

    [Fact]
    public void UndoingAnUngroupingPutsTheBlockBackWhole()
    {
        var (vm, top, bottom) = Rig();

        Group(vm, top, bottom);
        vm.UngroupSelectionCommand.Execute(null);

        Assert.Empty(vm.Circuit.Components.OfType<Subcircuit>());
        Assert.Equal("Ungroup Block", vm.History.UndoLabel);

        vm.UndoCommand.Execute(null);

        var block = Assert.Single(vm.Circuit.Components.OfType<Subcircuit>());

        Assert.Equal(2, block.InnerComponents.Count);
        Assert.Equal(2, block.Ports.Count);
    }

    // ---- placing from the library -------------------------------------------

    [Fact]
    public void UndoingAPlacedBlockRemovesItAndItsContentsTogether()
    {
        var (vm, top, bottom) = Rig();

        Group(vm, top, bottom);

        var block = vm.Circuit.Components.OfType<Subcircuit>().Single();
        vm.Blocks.Save("Divider", block);

        var before = vm.Circuit.Components.Count;

        vm.PlaceBlock("Divider");

        Assert.Equal(before + 1, vm.Circuit.Components.Count);
        Assert.Equal(2, vm.Circuit.Components.OfType<Subcircuit>().Count());

        vm.UndoCommand.Execute(null);

        // The placed instance and everything inside it, gone together — and the one that was
        // already there untouched.
        Assert.Equal(before, vm.Circuit.Components.Count);
        Assert.Single(vm.Circuit.Components.OfType<Subcircuit>());
    }

    // ---- the scope itself ---------------------------------------------------

    [Fact]
    public void AGestureRecordsOneStepHoweverManyChangesItMakes()
    {
        var circuit = new Circuit();
        var history = new UndoHistory();

        circuit.Add(new Resistor(1e3));
        history.Reset(circuit);

        Assert.False(history.CanUndo);

        using (history.Gesture(circuit, "Add three"))
        {
            circuit.Add(new Resistor(2e3));
            circuit.Add(new Resistor(3e3));
            circuit.Add(new Resistor(4e3));
        }

        Assert.True(history.CanUndo);
        Assert.Equal("Add three", history.UndoLabel);

        // And one step goes back past all three.
        var json = history.Undo()!;

        Assert.Single(Cirq.Components.Serialization.CircuitSerializer
            .FromJson(json).Circuit.Components.OfType<Resistor>());
    }

    [Fact]
    public void NestedGesturesStillCostOneStepAndTheOuterLabelWins()
    {
        var circuit = new Circuit();
        var history = new UndoHistory();

        circuit.Add(new Resistor(1e3));
        history.Reset(circuit);

        using (history.Gesture(circuit, "Outer"))
        {
            circuit.Add(new Resistor(2e3));

            using (history.Gesture(circuit, "Inner"))
            {
                circuit.Add(new Resistor(3e3));
            }

            circuit.Add(new Resistor(4e3));
        }

        Assert.Equal("Outer", history.UndoLabel);

        var json = history.Undo()!;

        Assert.Single(Cirq.Components.Serialization.CircuitSerializer
            .FromJson(json).Circuit.Components.OfType<Resistor>());

        Assert.False(history.CanUndo);
    }

    /// <summary>
    /// A gesture that changes nothing records nothing, so it does not cost a keystroke of undo
    /// that appears to do nothing.
    /// </summary>
    [Fact]
    public void AGestureThatChangesNothingRecordsNothing()
    {
        var circuit = new Circuit();
        var history = new UndoHistory();

        circuit.Add(new Resistor(1e3));
        history.Reset(circuit);

        using (history.Gesture(circuit, "Did nothing")) { }

        Assert.False(history.CanUndo);
    }

    [Fact]
    public void AGestureInsideASuspendedSpanRecordsNothingBecauseSomethingElseIsInCharge()
    {
        var circuit = new Circuit();
        var history = new UndoHistory();

        circuit.Add(new Resistor(1e3));
        history.Reset(circuit);

        history.Suspend();

        using (history.Gesture(circuit, "During a restore"))
        {
            circuit.Add(new Resistor(2e3));
        }

        history.Resume(circuit);

        Assert.False(history.CanUndo);
    }
}
