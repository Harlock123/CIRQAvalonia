using Cirq.Components.Hierarchy;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The Group and Ungroup commands, driven through the view model as the menu drives them.</summary>
public class GroupingCommandTests
{
    private static (MainWindowViewModel, Resistor Top, Resistor Bottom) Rig()
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var supply = vm.Circuit.Add(new DcVoltageSource(12.0));
        var top = vm.Circuit.Add(new Resistor(1e3));
        var bottom = vm.Circuit.Add(new Resistor(1e3));
        var ground = vm.Circuit.Add(new Ground());

        vm.Circuit.Connect(supply.Negative, ground.Pin);
        vm.Circuit.Connect(supply.Positive, top.A);
        vm.Circuit.Connect(top.B, bottom.A);
        vm.Circuit.Connect(bottom.B, ground.Pin);

        return (vm, top, bottom);
    }

    [Fact]
    public void GroupingTheSelectionReplacesItWithOneBlock()
    {
        var (vm, top, bottom) = Rig();

        top.IsSelected = true;
        bottom.IsSelected = true;

        vm.GroupSelectionCommand.Execute(null);

        var block = Assert.Single(vm.Circuit.Components.OfType<Subcircuit>());

        Assert.Equal(2, block.InnerComponents.Count);
        Assert.DoesNotContain(top, vm.Circuit.Components);
        Assert.Same(block, vm.SelectedComponent);
        Assert.Contains("Grouped", vm.StatusMessage);
    }

    [Fact]
    public void AndUngroupingItPutsThemBack()
    {
        var (vm, top, bottom) = Rig();

        top.IsSelected = true;
        bottom.IsSelected = true;
        vm.GroupSelectionCommand.Execute(null);

        vm.UngroupSelectionCommand.Execute(null);

        Assert.Empty(vm.Circuit.Components.OfType<Subcircuit>());
        Assert.Contains(top, vm.Circuit.Components);
        Assert.Contains(bottom, vm.Circuit.Components);

        // The released parts are left selected, ready to be moved or regrouped.
        Assert.True(top.IsSelected && bottom.IsSelected);
    }

    [Fact]
    public void TheCircuitStillSolvesToTheSameMidpointThroughBothOperations()
    {
        var (vm, top, bottom) = Rig();

        double Midpoint()
        {
            Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
            return vm.Simulation.Simulator!.NodeVoltage(top.B);
        }

        var loose = Midpoint();

        top.IsSelected = true;
        bottom.IsSelected = true;
        vm.GroupSelectionCommand.Execute(null);

        var grouped = Midpoint();

        vm.UngroupSelectionCommand.Execute(null);

        Assert.Equal(6.0, loose, 6);
        Assert.Equal(loose, grouped, 9);
        Assert.Equal(loose, Midpoint(), 9);
    }

    /// <summary>
    /// The reason grouping moves the parts rather than copying them: a probe attached to something
    /// that ends up inside a block has to go on reading it.
    /// </summary>
    [Fact]
    public void AProbeOnAPartThatGoesInsideABlockKeepsReadingIt()
    {
        var (vm, top, bottom) = Rig();

        vm.Circuit.Probes.Add(new SignalProbe("Mid", top.B, Color.ProbePalette[0]));

        top.IsSelected = true;
        bottom.IsSelected = true;
        vm.GroupSelectionCommand.Execute(null);

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var probe = vm.Circuit.Probes.Single();

        Assert.Equal(6.0, vm.Simulation.Simulator!.SampleProbe(probe), 6);
    }

    [Fact]
    public void GroupingOnePartOrNoneSaysSoRatherThanMakingAPointlessBlock()
    {
        var (vm, top, _) = Rig();

        vm.GroupSelectionCommand.Execute(null);
        Assert.Empty(vm.Circuit.Components.OfType<Subcircuit>());
        Assert.Contains("two or more", vm.StatusMessage);

        vm.SelectedComponent = top;
        vm.GroupSelectionCommand.Execute(null);
        Assert.Empty(vm.Circuit.Components.OfType<Subcircuit>());
    }

    [Fact]
    public void UngroupingSomethingThatIsNotABlockSaysSo()
    {
        var (vm, top, _) = Rig();

        vm.SelectedComponent = top;
        vm.UngroupSelectionCommand.Execute(null);

        Assert.Contains("Select a block", vm.StatusMessage);
        Assert.Contains(top, vm.Circuit.Components);
    }

    [Fact]
    public void GroupingMarksTheDocumentChangedAndTheEngineStale()
    {
        var (vm, top, bottom) = Rig();

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        top.IsSelected = true;
        bottom.IsSelected = true;
        vm.GroupSelectionCommand.Execute(null);

        Assert.True(vm.IsModified);
        Assert.Contains("Reset", vm.Simulation.Status);
    }
}
