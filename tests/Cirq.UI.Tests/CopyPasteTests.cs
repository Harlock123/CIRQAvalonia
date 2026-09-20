using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Copying a selected part and pasting a duplicate of it, which is the one editing gesture the
/// schematic did not have.
/// </summary>
public class CopyPasteTests
{
    [Fact]
    public void CopyingAPartAndPastingItGivesASecondOne()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var original = vm.Circuit.Add(new Resistor(47e3) { X = 100, Y = 200 });
        vm.SelectedComponent = original;

        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        Assert.Equal(2, vm.Circuit.Components.Count);

        var copy = vm.Circuit.Components.OfType<Resistor>().Single(r => !ReferenceEquals(r, original));

        Assert.Equal(47e3, copy.Resistance);
        Assert.NotSame(original, copy);
    }

    /// <summary>
    /// A copy is a new part, not a second reference to the old one — a different identity and a
    /// designator of its own, or the netlist would have two R1s in it.
    /// </summary>
    [Fact]
    public void ThePasteIsANewPartWithItsOwnName()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var original = vm.Circuit.Add(new Capacitor(100e-9));
        vm.SelectedComponent = original;

        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        var copy = vm.Circuit.Components.Single(c => !ReferenceEquals(c, original));

        Assert.NotEqual(original.Id, copy.Id);
        Assert.NotEqual(original.Name, copy.Name);
        Assert.False(string.IsNullOrWhiteSpace(copy.Name));

        // And no two parts in the circuit share a designator.
        var names = vm.Circuit.Components.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>
    /// Every editable property comes across, not just the obvious one. This goes through the same
    /// path that saving and loading uses, so a part that reloads correctly copies correctly.
    /// </summary>
    [Fact]
    public void EveryParameterComesAcross()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var original = vm.Circuit.Add(new FunctionGenerator(Waveform.Triangle, 2.5e3, 7.0)
        {
            DcOffset = 1.25,
            DutyCycle = 0.3,
            OutputResistance = 75,
            X = -40,
            Y = 90,
            RotationDegrees = 90,
        });

        vm.SelectedComponent = original;
        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        var copy = vm.Circuit.Components.OfType<FunctionGenerator>().Single(c => !ReferenceEquals(c, original));

        Assert.Equal(Waveform.Triangle, copy.Shape);
        Assert.Equal(2.5e3, copy.Frequency);
        Assert.Equal(7.0, copy.AmplitudePeakToPeak);
        Assert.Equal(1.25, copy.DcOffset);
        Assert.Equal(0.3, copy.DutyCycle);
        Assert.Equal(75, copy.OutputResistance);
        Assert.Equal(90, copy.RotationDegrees);
    }

    /// <summary>
    /// A model reference is a property like any other, and one that a field-by-field copy is
    /// exactly the sort of thing to miss.
    /// </summary>
    [Fact]
    public void AndSoDoesTheModelOnAPartThatHasOne()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var original = vm.Circuit.Add(new OperationalAmplifier(OpAmpModel.Mcp6002));
        vm.SelectedComponent = original;

        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        var copy = vm.Circuit.Components.OfType<OperationalAmplifier>()
            .Single(c => !ReferenceEquals(c, original));

        Assert.Equal("MCP6002", copy.Model.Name);
    }

    /// <summary>
    /// A part whose pins are decided at construction time has to be rebuilt with the same shape,
    /// which is the case a copy is most likely to get wrong.
    /// </summary>
    [Fact]
    public void AGateKeepsItsFunctionAndItsInputCount()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var original = vm.Circuit.Add(new LogicGate(GateFunction.Nand, 3));
        vm.SelectedComponent = original;

        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        var copy = vm.Circuit.Components.OfType<LogicGate>().Single(c => !ReferenceEquals(c, original));

        Assert.Equal(GateFunction.Nand, copy.Function);
        Assert.Equal(3, copy.InputTerminals.Count);
    }

    /// <summary>
    /// The copy is taken at the moment you press Ctrl+C. Editing the original afterwards must not
    /// reach into the clipboard, which it would if the clipboard held the part itself.
    /// </summary>
    [Fact]
    public void EditingTheOriginalAfterCopyingDoesNotChangeWhatIsPasted()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var original = vm.Circuit.Add(new Resistor(10e3));
        vm.SelectedComponent = original;

        vm.CopySelectionCommand.Execute(null);

        original.Resistance = 1e6;
        vm.PasteCommand.Execute(null);

        var copy = vm.Circuit.Components.OfType<Resistor>().Single(r => !ReferenceEquals(r, original));

        Assert.Equal(10e3, copy.Resistance);
    }

    /// <summary>And deleting the original does not empty the clipboard either.</summary>
    [Fact]
    public void AndDeletingTheOriginalLeavesTheClipboardIntact()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var original = vm.Circuit.Add(new Resistor(2.2e3));
        vm.SelectedComponent = original;

        vm.CopySelectionCommand.Execute(null);
        vm.Circuit.Components.Remove(original);

        vm.PasteCommand.Execute(null);

        var copy = Assert.Single(vm.Circuit.Components);
        Assert.Equal(2.2e3, ((Resistor)copy).Resistance);
    }

    /// <summary>
    /// A pasted copy lands beside what it came from rather than exactly on top, and each further
    /// paste moves along again — four presses give four parts you can tell apart.
    /// </summary>
    [Fact]
    public void RepeatedPastesCascadeInsteadOfStacking()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var original = vm.Circuit.Add(new Resistor(1e3) { X = 0, Y = 0 });
        vm.SelectedComponent = original;

        vm.CopySelectionCommand.Execute(null);

        for (var i = 0; i < 4; i++) vm.PasteCommand.Execute(null);

        var positions = vm.Circuit.Components
            .Select(c => (c.X, c.Y))
            .ToList();

        Assert.Equal(5, positions.Count);
        Assert.Equal(positions.Count, positions.Distinct().Count());

        // And none of them landed on the original.
        Assert.Single(positions, p => p == (0.0, 0.0));
    }

    /// <summary>The pasted part is selected, because it is the one about to be moved.</summary>
    [Fact]
    public void ThePastedPartBecomesTheSelection()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var original = vm.Circuit.Add(new Led());
        vm.SelectedComponent = original;

        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        Assert.NotNull(vm.SelectedComponent);
        Assert.NotSame(original, vm.SelectedComponent);
        Assert.Contains(vm.SelectedComponent!, vm.Circuit.Components);
    }

    /// <summary>Pasting is refused rather than guessed at when nothing has been copied.</summary>
    [Fact]
    public void PastingWithAnEmptyClipboardDoesNothing()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        Assert.False(vm.PasteCommand.CanExecute(null));

        vm.PasteCommand.Execute(null);

        Assert.Empty(vm.Circuit.Components);
    }

    /// <summary>And copying with nothing selected says so rather than throwing.</summary>
    [Fact]
    public void CopyingWithNothingSelectedSaysSo()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.SelectedComponent = null;

        vm.CopySelectionCommand.Execute(null);

        Assert.False(vm.Clipboard.HasContent);
        Assert.Contains("Nothing selected", vm.StatusMessage);
    }

    /// <summary>
    /// A paste is an ordinary edit, so it goes on the undo stack like any other — which it gets
    /// by being a change to the circuit's components rather than by asking.
    /// </summary>
    [Fact]
    public void APasteCanBeUndone()
    {
        using var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        vm.SelectedComponent = vm.Circuit.Add(new Resistor(330));
        vm.CopySelectionCommand.Execute(null);
        vm.PasteCommand.Execute(null);

        Assert.Equal(2, vm.Circuit.Components.Count);
        Assert.True(vm.History.CanUndo);

        vm.UndoCommand.Execute(null);

        Assert.Single(vm.Circuit.Components);
    }

    /// <summary>
    /// The clipboard itself, without a window round it: what it hands out is a duplicate each
    /// time, so two pastes are two parts and not the same one twice.
    /// </summary>
    [Fact]
    public void TheClipboardHandsOutADifferentPartEachTime()
    {
        var clipboard = new ComponentClipboard();
        var circuit = new Circuit();

        Assert.False(clipboard.HasContent);

        clipboard.Copy(new Inductor(10e-3) { SeriesResistance = 4 });
        Assert.True(clipboard.HasContent);
        Assert.Equal("Inductor", clipboard.HeldDescription);

        var first = Assert.Single(clipboard.PasteInto(circuit));
        var second = Assert.Single(clipboard.PasteInto(circuit));

        Assert.NotSame(first, second);
        Assert.Equal(4.0, ((Inductor)second).SeriesResistance);

        clipboard.Clear();
        Assert.False(clipboard.HasContent);
        Assert.Empty(clipboard.PasteInto(circuit));
    }

    /// <summary>
    /// Every part in the palette survives being cloned. This is the same path save and load take,
    /// so a type that cannot be copied is a type that cannot be reopened either.
    /// </summary>
    [Fact]
    public void EveryPaletteEntryCanBeCopied()
    {
        foreach (var item in ComponentCatalog.AllItems)
        {
            var original = item.Create();
            original.Name = $"{original.DesignatorPrefix}1";

            List<string> warnings = [];
            var copy = CircuitSerializer.Clone(original, warnings);

            Assert.Equal(original.GetType(), copy.GetType());
            Assert.Empty(warnings);
            Assert.Equal(string.Empty, copy.Name);

            // Pins have to come back too: a part rebuilt with the wrong shape cannot be wired.
            Assert.Equal(original.Terminals.Count, copy.Terminals.Count);
        }
    }
}
