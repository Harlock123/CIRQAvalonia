using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

public class UndoHistoryTests
{
    private static MainWindowViewModel Fresh()
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        vm.History.Reset(vm.Circuit);
        return vm;
    }

    [Fact]
    public void ThereIsNothingToUndoOnAFreshDocument()
    {
        using var vm = Fresh();

        Assert.False(vm.History.CanUndo);
        Assert.False(vm.History.CanRedo);
    }

    [Fact]
    public void AddingAComponentCanBeUndoneAndRedone()
    {
        using var vm = Fresh();

        vm.Circuit.Add(new Resistor(1e3));
        Assert.Single(vm.Circuit.Components);
        Assert.True(vm.History.CanUndo);

        vm.UndoCommand.Execute(null);
        Assert.Empty(vm.Circuit.Components);
        Assert.True(vm.History.CanRedo);

        vm.RedoCommand.Execute(null);
        Assert.Single(vm.Circuit.Components);
    }

    /// <summary>The menu has to say what it is about to undo, not just "Undo".</summary>
    [Fact]
    public void TheHistoryNamesWhatItWillUndo()
    {
        using var vm = Fresh();

        vm.Circuit.Add(new Resistor(1e3));

        Assert.Equal("Add Resistor", vm.History.UndoLabel);

        vm.UndoCommand.Execute(null);
        Assert.Equal("Add Resistor", vm.History.RedoLabel);
    }

    /// <summary>Several steps back in order, then forward again to where it started.</summary>
    [Fact]
    public void ItWalksBackAndForwardThroughSeveralEdits()
    {
        using var vm = Fresh();

        for (var i = 0; i < 5; i++) vm.Circuit.Add(new Resistor(1e3));
        Assert.Equal(5, vm.Circuit.Components.Count);

        for (var i = 4; i >= 0; i--)
        {
            vm.UndoCommand.Execute(null);
            Assert.Equal(i, vm.Circuit.Components.Count);
        }

        Assert.False(vm.History.CanUndo);

        for (var i = 1; i <= 5; i++)
        {
            vm.RedoCommand.Execute(null);
            Assert.Equal(i, vm.Circuit.Components.Count);
        }
    }

    /// <summary>Editing after undoing abandons the branch that was undone.</summary>
    [Fact]
    public void AnEditAfterAnUndoClearsTheRedoBranch()
    {
        using var vm = Fresh();

        vm.Circuit.Add(new Resistor(1e3));
        vm.Circuit.Add(new Capacitor(1e-6));

        vm.UndoCommand.Execute(null);
        Assert.True(vm.History.CanRedo);

        vm.Circuit.Add(new Inductor(1e-3));

        Assert.False(vm.History.CanRedo);
    }

    /// <summary>Parameter edits are part of the document, so they come back too.</summary>
    [Fact]
    public void AChangedParameterIsRestored()
    {
        using var vm = Fresh();

        var resistor = vm.Circuit.Add(new Resistor(1e3));
        resistor.Resistance = 4.7e3;

        vm.UndoCommand.Execute(null);

        // Restoring replaces the instances, so the value is read off the circuit rather than the
        // old object, which is exactly the thing a snapshot restore has to get right.
        Assert.Equal(1e3, vm.Circuit.Components.OfType<Resistor>().Single().Resistance);
    }

    /// <summary>Wires and probes are part of the document as much as the parts are.</summary>
    [Fact]
    public void WiresAndProbesAreRestoredWithTheParts()
    {
        using var vm = Fresh();

        var source = vm.Circuit.Add(new DcVoltageSource(5.0));
        var load = vm.Circuit.Add(new Resistor(100));
        var gnd = vm.Circuit.Add(new Ground());

        vm.Circuit.Connect(source.Positive, load.A);
        vm.Circuit.Connect(load.B, gnd.Pin);
        vm.Circuit.Connect(source.Negative, gnd.Pin);
        vm.Scope.AddProbe(load.A, "Load");

        var wires = vm.Circuit.Wires.Count;
        Assert.Single(vm.Circuit.Probes);

        vm.UndoCommand.Execute(null);           // removes the probe
        Assert.Empty(vm.Circuit.Probes);

        vm.RedoCommand.Execute(null);
        Assert.Single(vm.Circuit.Probes);
        Assert.Equal(wires, vm.Circuit.Wires.Count);
    }

    /// <summary>
    /// A drag moves a component on every pointer movement. Each gesture has to cost one step, or
    /// undoing a drag across the canvas would take a hundred keystrokes.
    /// </summary>
    [Fact]
    public void ADragCostsOneUndoStepRatherThanOnePerMovement()
    {
        using var vm = Fresh();
        var part = vm.Circuit.Add(new Resistor(1e3));
        part.X = 0;
        part.Y = 0;

        vm.BeginInteractiveEdit("Move Resistor");
        for (var i = 1; i <= 50; i++)
        {
            part.X = i * 10;
            part.Y = i * 4;
        }
        vm.EndInteractiveEdit();

        Assert.Equal(500, part.X);

        vm.UndoCommand.Execute(null);

        var moved = vm.Circuit.Components.OfType<Resistor>().Single();
        Assert.Equal(0, moved.X);
        Assert.Equal(0, moved.Y);
    }

    /// <summary>Opening or starting a document leaves nothing to step back into.</summary>
    [Fact]
    public void LoadingAnExampleStartsAFreshHistory()
    {
        using var vm = Fresh();
        vm.Circuit.Add(new Resistor(1e3));
        Assert.True(vm.History.CanUndo);

        vm.LoadExampleCommand.Execute(Examples.All.First());

        Assert.False(vm.History.CanUndo);
        Assert.False(vm.History.CanRedo);
    }

    /// <summary>The history is bounded, so a long session does not grow without limit.</summary>
    [Fact]
    public void TheHistoryIsBounded()
    {
        var history = new Cirq.UI.Services.UndoHistory { Depth = 5 };
        var circuit = new Cirq.Core.Topology.Circuit();
        history.Reset(circuit);

        for (var i = 0; i < 20; i++)
        {
            circuit.Add(new Resistor(1e3));
            history.Capture(circuit, $"Add {i}");
        }

        var steps = 0;
        while (history.Undo() is not null) steps++;

        Assert.Equal(5, steps);
    }

    /// <summary>A change that changes nothing should not cost a keystroke of undo.</summary>
    [Fact]
    public void SettingAValueToWhatItAlreadyWasRecordsNothing()
    {
        using var vm = Fresh();
        var resistor = vm.Circuit.Add(new Resistor(1e3));

        var before = vm.History.CanUndo;
        resistor.Resistance = 1e3;

        Assert.Equal(before, vm.History.CanUndo);
        Assert.Equal("Add Resistor", vm.History.UndoLabel);
    }
}

public class DocumentDirtyTrackingTests
{
    /// <summary>
    /// Running a circuit is not editing it. Parts describe themselves as they run — a triac says
    /// so when it fires — and treating that as an edit put an asterisk in the title bar and a
    /// "discard changes?" prompt in the way of anyone who had only pressed Run.
    /// </summary>
    [Fact]
    public void RunningASimulationDoesNotModifyTheDocument()
    {
        using var vm = new MainWindowViewModel();
        vm.LoadExampleCommand.Execute(Examples.All.Single(e => e.Name == "Lamp Dimmer"));

        Assert.False(vm.IsModified);

        vm.Simulation.Simulator!.Run(100e-3);

        Assert.False(vm.IsModified);
        Assert.False(vm.History.CanUndo);
    }

    /// <summary>But editing it is.</summary>
    [Fact]
    public void EditingAValueDoesModifyTheDocument()
    {
        using var vm = new MainWindowViewModel();
        Assert.False(vm.IsModified);

        vm.Circuit.Components.OfType<Resistor>().First().Resistance = 22e3;

        Assert.True(vm.IsModified);
        Assert.True(vm.History.CanUndo);
    }
}
