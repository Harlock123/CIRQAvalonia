using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Copying a whole selection — several parts and the wires among them — which is what a band
/// selection is for.
/// </summary>
public class GroupCopyPasteTests
{
    /// <summary>
    /// A divider: two resistors with a wire between them, and a third part wired to the outside
    /// world so there is something that must <i>not</i> come along.
    /// </summary>
    private static (MainWindowViewModel Vm, Resistor Top, Resistor Bottom, Resistor Outside) Rig()
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var top = vm.Circuit.Add(new Resistor(10e3) { X = 0, Y = 0 });
        var bottom = vm.Circuit.Add(new Resistor(4.7e3) { X = 0, Y = 100 });
        var outside = vm.Circuit.Add(new Resistor(100) { X = 400, Y = 0 });

        vm.Circuit.Connect(top.B, bottom.A);
        vm.Circuit.Connect(bottom.B, outside.A);

        return (vm, top, bottom, outside);
    }

    private static void Select(MainWindowViewModel vm, params CircuitComponent[] chosen)
    {
        foreach (var component in vm.Circuit.Components)
            component.IsSelected = chosen.Contains(component);
    }

    [Fact]
    public void CopyingAGroupPastesAllOfIt()
    {
        var (vm, top, bottom, _) = Rig();
        using var _vm = vm;

        Select(vm, top, bottom);

        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        // Three originals plus two copies.
        Assert.Equal(5, vm.Circuit.Components.Count);
        Assert.Equal(2, vm.Clipboard.Count);

        var values = vm.Circuit.Components.OfType<Resistor>().Select(r => r.Resistance).ToList();
        Assert.Equal(2, values.Count(v => v == 10e3));
        Assert.Equal(2, values.Count(v => v == 4.7e3));
        Assert.Equal(1, values.Count(v => v == 100));
    }

    /// <summary>
    /// The wire between the two copied parts is copied with them, because both of its ends are in
    /// the group and a duplicate of it has somewhere to attach.
    /// </summary>
    [Fact]
    public void AndTheWireBetweenThemComesWithThem()
    {
        var (vm, top, bottom, _) = Rig();
        using var _vm = vm;

        Select(vm, top, bottom);

        vm.CopySelectionCommand.Execute(null);

        Assert.Equal(1, vm.Clipboard.WireCount);

        vm.PasteCommand.Execute(null);

        // Two wires to start with, and the copied group brings a third.
        Assert.Equal(3, vm.Circuit.Wires.Count);

        var copies = vm.Circuit.Components.Where(c => c.IsSelected).ToList();
        Assert.Equal(2, copies.Count);

        // And the new wire runs between the two copies rather than back to either original.
        var newWire = vm.Circuit.Wires.Single(w =>
            copies.Contains(w.SourceTerminal.Owner!) && copies.Contains(w.TargetTerminal.Owner!));

        Assert.NotSame(top, newWire.SourceTerminal.Owner);
        Assert.NotSame(bottom, newWire.TargetTerminal.Owner);
    }

    /// <summary>
    /// The wire out of the group is not copied. It went to a part that is not being duplicated, so
    /// there is nothing for the copy to attach to and guessing would be worse than dropping it.
    /// </summary>
    [Fact]
    public void ButTheWireLeavingTheGroupIsNotCopied()
    {
        var (vm, top, bottom, outside) = Rig();
        using var _vm = vm;

        Select(vm, top, bottom);

        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        // Nothing new is attached to the part that was left out of the selection.
        var touchingOutside = vm.Circuit.Wires.Count(w =>
            ReferenceEquals(w.SourceTerminal.Owner, outside) || ReferenceEquals(w.TargetTerminal.Owner, outside));

        Assert.Equal(1, touchingOutside);
    }

    /// <summary>
    /// The copy keeps its shape: every part the same distance and direction from the others as it
    /// was copied at, with the whole group moved together.
    /// </summary>
    [Fact]
    public void TheGroupKeepsItsShape()
    {
        var (vm, top, bottom, _) = Rig();
        using var _vm = vm;

        Select(vm, top, bottom);

        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        var copies = vm.Circuit.Components.Where(c => c.IsSelected).OrderBy(c => c.Y).ToList();

        Assert.Equal(2, copies.Count);

        // A hundred apart before, a hundred apart after, and displaced by the same amount each.
        Assert.Equal(bottom.Y - top.Y, copies[1].Y - copies[0].Y);
        Assert.Equal(bottom.X - top.X, copies[1].X - copies[0].X);
        Assert.Equal(copies[0].X - top.X, copies[1].X - bottom.X);
    }

    /// <summary>And the pasted group is what is selected, ready to be dragged into place.</summary>
    [Fact]
    public void ThePastedGroupBecomesTheSelection()
    {
        var (vm, top, bottom, _) = Rig();
        using var _vm = vm;

        Select(vm, top, bottom);

        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        var selected = vm.Circuit.Components.Where(c => c.IsSelected).ToList();

        Assert.Equal(2, selected.Count);
        Assert.DoesNotContain(top, selected);
        Assert.DoesNotContain(bottom, selected);

        // More than one part means there is nothing sensible for the inspector to edit.
        Assert.Null(vm.SelectedComponent);
    }

    /// <summary>
    /// Pasting a group twice gives two groups, each wired inside itself and neither wired to the
    /// other — which is the whole reason the wires are recorded by position in the group.
    /// </summary>
    [Fact]
    public void PastingTwiceGivesTwoSeparatelyWiredGroups()
    {
        var (vm, top, bottom, _) = Rig();
        using var _vm = vm;

        Select(vm, top, bottom);

        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        var firstCopy = vm.Circuit.Components.Where(c => c.IsSelected).ToList();

        vm.PasteCommand.Execute(null);

        var secondCopy = vm.Circuit.Components.Where(c => c.IsSelected).ToList();

        Assert.Equal(2, secondCopy.Count);
        Assert.Empty(firstCopy.Intersect(secondCopy));

        // Four wires: the two originals, one inside each copy.
        Assert.Equal(4, vm.Circuit.Wires.Count);

        // And no wire joins one copy to the other.
        Assert.DoesNotContain(vm.Circuit.Wires, w =>
            (firstCopy.Contains(w.SourceTerminal.Owner!) && secondCopy.Contains(w.TargetTerminal.Owner!))
            || (secondCopy.Contains(w.SourceTerminal.Owner!) && firstCopy.Contains(w.TargetTerminal.Owner!)));

        // The two groups do not land on top of one another either.
        Assert.NotEqual(firstCopy[0].X, secondCopy[0].X);
    }

    /// <summary>
    /// Each part in the copy gets its own designator, so a pasted divider does not arrive with two
    /// more R1s in it.
    /// </summary>
    [Fact]
    public void EveryCopiedPartGetsItsOwnDesignator()
    {
        var (vm, top, bottom, _) = Rig();
        using var _vm = vm;

        Select(vm, top, bottom);
        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        var names = vm.Circuit.Components.Select(c => c.Name).ToList();

        Assert.Equal(7, names.Count);
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// A group with different kinds of part in it, because a copy that only works for resistors
    /// would pass every test above.
    /// </summary>
    [Fact]
    public void AMixedGroupCopiesEveryKindOfPartInIt()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var generator = vm.Circuit.Add(new FunctionGenerator(Waveform.Square, 1e3, 5.0));
        var resistor = vm.Circuit.Add(new Resistor(2.2e3));
        var capacitor = vm.Circuit.Add(new Capacitor(47e-9));
        var ground = vm.Circuit.Add(new Ground());

        vm.Circuit.Connect(generator.Output, resistor.A);
        vm.Circuit.Connect(resistor.B, capacitor.A);
        vm.Circuit.Connect(capacitor.B, ground.Pin);

        foreach (var component in vm.Circuit.Components) component.IsSelected = true;

        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        Assert.Equal(8, vm.Circuit.Components.Count);
        Assert.Equal(6, vm.Circuit.Wires.Count);

        var copies = vm.Circuit.Components.Where(c => c.IsSelected).ToList();

        Assert.Equal(4, copies.Count);
        Assert.Contains(copies, c => c is FunctionGenerator { Shape: Waveform.Square });
        Assert.Contains(copies, c => c is Resistor { Resistance: 2.2e3 });
        Assert.Contains(copies, c => c is Capacitor { Capacitance: 47e-9 });
        Assert.Contains(copies, c => c is Ground);
    }

    /// <summary>
    /// The copied group still works as a circuit. Wires that were recreated on the wrong pins
    /// would leave something that looks right on the canvas and solves differently.
    /// </summary>
    [Fact]
    public void AndTheCopyIsACircuitThatSolvesLikeTheOriginal()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var supply = vm.Circuit.Add(new DcVoltageSource(10.0) { X = 0, Y = 0 });
        var top = vm.Circuit.Add(new Resistor(10e3) { X = 100, Y = 0 });
        var bottom = vm.Circuit.Add(new Resistor(10e3) { X = 200, Y = 0 });
        var ground = vm.Circuit.Add(new Ground { X = 0, Y = 100 });

        vm.Circuit.Connect(supply.Positive, top.A);
        vm.Circuit.Connect(top.B, bottom.A);
        vm.Circuit.Connect(bottom.B, ground.Pin);
        vm.Circuit.Connect(supply.Negative, ground.Pin);

        foreach (var component in vm.Circuit.Components) component.IsSelected = true;

        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        var copies = vm.Circuit.Components.Where(c => c.IsSelected).ToList();
        var copiedTop = copies.OfType<Resistor>().Single(r => r.X == 100 + ComponentClipboard.PasteOffset);

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        vm.Simulation.Simulator!.Run(1e-3);

        // Two equal resistors across ten volts: the midpoint of the copy sits at five, the same
        // as the midpoint of the original it was taken from.
        Assert.Equal(5.0, vm.Simulation.Simulator!.NodeVoltage(copiedTop.B), 0.05);
        Assert.Equal(5.0, vm.Simulation.Simulator!.NodeVoltage(top.B), 0.05);
    }

    /// <summary>
    /// The clipboard on its own: a group of parts with no wires among them copies as a group and
    /// brings no wires with it.
    /// </summary>
    [Fact]
    public void AGroupWithNoWiresBetweenItCopiesCleanly()
    {
        var circuit = new Circuit();
        var first = circuit.Add(new Resistor(1e3));
        var second = circuit.Add(new Resistor(2e3));

        var clipboard = new ComponentClipboard();
        clipboard.Copy([first, second], circuit.Wires);

        Assert.Equal(2, clipboard.Count);
        Assert.Equal(0, clipboard.WireCount);
        Assert.Equal("2 parts", clipboard.HeldDescription);

        var pasted = clipboard.PasteInto(circuit);

        Assert.Equal(2, pasted.Count);
        Assert.Empty(circuit.Wires);
    }

    /// <summary>Copying nothing is not an error, it just leaves the clipboard alone.</summary>
    [Fact]
    public void CopyingAnEmptySelectionHoldsNothing()
    {
        var circuit = new Circuit();
        var clipboard = new ComponentClipboard();

        clipboard.Copy([], circuit.Wires);

        Assert.False(clipboard.HasContent);
        Assert.Empty(clipboard.PasteInto(circuit));
    }
}
