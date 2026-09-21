using Cirq.Components.Hierarchy;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;
using Cirq.UI.Services;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>Saving a block so it can be placed more than once.</summary>
public class BlockLibraryTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"cirq-blocks-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>A two-resistor divider grouped into a block, ready to be saved.</summary>
    private static (Circuit, Subcircuit) Divider(double top = 1e3, double bottom = 1e3)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(12.0));
        var upper = circuit.Add(new Resistor(top));
        var lower = circuit.Add(new Resistor(bottom));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, upper.A);
        circuit.Connect(upper.B, lower.A);
        circuit.Connect(lower.B, ground.Pin);

        return (circuit, Grouping.Group(circuit, [upper, lower], "Divider")!);
    }

    [Fact]
    public void ABlockCanBeSavedAndComesBackWithItsContents()
    {
        var (_, block) = Divider();
        var library = new BlockLibrary(_path);

        var saved = library.Save("Divider", block);

        Assert.Equal("Divider", saved.Name);
        Assert.Equal(2, saved.Parts);
        Assert.Equal(2, saved.Pins);
        Assert.Contains("2 parts", saved.Summary);

        var target = new Circuit();
        var made = library.Create("Divider", target);

        Assert.NotNull(made);
        Assert.Equal(2, made!.InnerComponents.Count);
        Assert.Equal(2, made.Ports.Count);
        Assert.Equal("Divider", made.BlockName);
    }

    [Fact]
    public void ItSurvivesTheApplicationBeingRestarted()
    {
        var (_, block) = Divider();

        new BlockLibrary(_path).Save("Divider", block);

        // A fresh library over the same file, as a new run of the application would have.
        var reopened = new BlockLibrary(_path);

        Assert.Single(reopened.Blocks);
        Assert.True(reopened.Contains("divider"));
        Assert.NotNull(reopened.Create("Divider", new Circuit()));
    }

    /// <summary>
    /// The point of the library: two instances that are genuinely independent, and a circuit that
    /// solves with both of them in it.
    /// </summary>
    [Fact]
    public void TwoInstancesAreIndependentAndBothWork()
    {
        var (_, block) = Divider(1e3, 3e3);
        var library = new BlockLibrary(_path);
        library.Save("Divider", block);

        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(12.0));
        var ground = circuit.Add(new Ground());
        circuit.Connect(supply.Negative, ground.Pin);

        var first = library.Create("Divider", circuit)!;
        circuit.Components.Add(first);

        var second = library.Create("Divider", circuit)!;
        circuit.Components.Add(second);

        // Nothing is shared: different objects, different identities, different names.
        Assert.NotSame(first, second);
        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Name, second.Name);

        foreach (var a in first.Descendants())
        foreach (var b in second.Descendants())
        {
            Assert.NotSame(a, b);
            Assert.NotEqual(a.Id, b.Id);
            Assert.NotEqual(a.Name, b.Name);
        }

        // Wire both across the supply and check each divides it.
        foreach (var instance in new[] { first, second })
        {
            circuit.Connect(instance.Terminals[0], supply.Positive);
            circuit.Connect(instance.Terminals[1], ground.Pin);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        foreach (var instance in new[] { first, second })
        {
            var upper = instance.InnerComponents.OfType<Resistor>().Single(r => r.Resistance == 1e3);

            // 12 V across 4k tapped at 3k.
            Assert.Equal(9.0, sim.NodeVoltage(upper.B), 6);
        }
    }

    [Fact]
    public void EditingOneInstanceDoesNotTouchTheOther()
    {
        var (_, block) = Divider();
        var library = new BlockLibrary(_path);
        library.Save("Divider", block);

        var circuit = new Circuit();

        var first = library.Create("Divider", circuit)!;
        circuit.Components.Add(first);

        var second = library.Create("Divider", circuit)!;
        circuit.Components.Add(second);

        first.InnerComponents.OfType<Resistor>().First().Resistance = 47e3;

        // A copy, not a reference — and the docs say so rather than implying a link.
        Assert.All(second.InnerComponents.OfType<Resistor>(), r => Assert.Equal(1e3, r.Resistance));
    }

    [Fact]
    public void SavingUnderTheSameNameReplacesWhatWasThere()
    {
        var library = new BlockLibrary(_path);

        var (_, first) = Divider(1e3, 1e3);
        library.Save("Divider", first);

        var (_, second) = Divider(2e3, 2e3);
        library.Save("Divider", second);

        Assert.Single(library.Blocks);

        var made = library.Create("Divider", new Circuit())!;

        Assert.All(made.InnerComponents.OfType<Resistor>(), r => Assert.Equal(2e3, r.Resistance));
    }

    [Fact]
    public void ABlockCanBeTakenOutAgain()
    {
        var (_, block) = Divider();
        var library = new BlockLibrary(_path);
        library.Save("Divider", block);

        Assert.True(library.Remove("divider"));
        Assert.Empty(library.Blocks);
        Assert.Null(library.Create("Divider", new Circuit()));
        Assert.False(library.Remove("Divider"));
    }

    [Fact]
    public void ANamelessBlockIsRefused()
    {
        var (_, block) = Divider();
        var library = new BlockLibrary(_path);

        Assert.Throws<ArgumentException>(() => library.Save("   ", block));
    }

    [Fact]
    public void ADamagedLibraryFileIsAnEmptyLibraryRatherThanACrash()
    {
        File.WriteAllText(_path, "{ this is not json");

        var library = new BlockLibrary(_path);

        Assert.Empty(library.Blocks);
        Assert.Null(library.Create("anything", new Circuit()));
    }

    [Fact]
    public void ANestedBlockSurvivesTheLibraryToo()
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

        var library = new BlockLibrary(_path);
        library.Save("Outer", outer);

        var made = library.Create("Outer", new Circuit())!;
        var nested = Assert.Single(made.InnerComponents.OfType<Subcircuit>());

        Assert.Equal("Inner", nested.BlockName);
        Assert.Equal(2, nested.InnerComponents.Count);
    }
}

/// <summary>The library window's own logic, driven as the menu drives it.</summary>
public class BlockLibraryViewModelTests : IDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"cirq-blocks-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    /// <summary>
    /// The library is pointed at a temporary file throughout. It is the one thing in the
    /// application that writes outside the document, and a test run that edited somebody's own
    /// saved blocks would be a poor trade for the convenience.
    /// </summary>
    private MainWindowViewModel New()
    {
        var vm = new MainWindowViewModel { Blocks = new BlockLibrary(_path) };
        vm.Circuit.Clear();

        return vm;
    }

    private (MainWindowViewModel, Subcircuit) Rig()
    {
        var vm = New();

        var supply = vm.Circuit.Add(new DcVoltageSource(12.0));
        var top = vm.Circuit.Add(new Resistor(1e3));
        var bottom = vm.Circuit.Add(new Resistor(1e3));
        var ground = vm.Circuit.Add(new Ground());

        vm.Circuit.Connect(supply.Negative, ground.Pin);
        vm.Circuit.Connect(supply.Positive, top.A);
        vm.Circuit.Connect(top.B, bottom.A);
        vm.Circuit.Connect(bottom.B, ground.Pin);

        top.IsSelected = true;
        bottom.IsSelected = true;
        vm.GroupSelectionCommand.Execute(null);

        return (vm, vm.Circuit.Components.OfType<Subcircuit>().Single());
    }

    [Fact]
    public void SavingWithoutANameIsRefusedAndSaysWhy()
    {
        var (vm, _) = Rig();
        var model = new BlockLibraryViewModel(vm) { NameToSave = "   " };

        model.SaveCommand.Execute(null);

        Assert.Contains("needs a name", model.Status);
        Assert.True(model.IsEmpty);
    }

    [Fact]
    public void SavingWithNoBlockSelectedSaysWhatToDo()
    {
        var vm = New();

        var model = new BlockLibraryViewModel(vm) { NameToSave = "Anything" };
        model.SaveCommand.Execute(null);

        Assert.Contains("Select a block", model.Status);
    }

    [Fact]
    public void PlacingWithNothingChosenSaysSo()
    {
        var vm = New();

        var model = new BlockLibraryViewModel(vm);
        model.PlaceCommand.Execute(null);

        Assert.Contains("Choose a block", model.Status);
    }

    [Fact]
    public void APlacedBlockLandsOnTheCanvasSelectedAndTheEngineIsMarkedStale()
    {
        var (vm, _) = Rig();

        var model = new BlockLibraryViewModel(vm) { NameToSave = "Divider" };
        model.SaveCommand.Execute(null);

        Assert.False(model.IsEmpty);
        Assert.Empty(model.NameToSave);

        var before = vm.Circuit.Components.Count;

        model.PlaceCommand.Execute(null);

        Assert.Equal(before + 1, vm.Circuit.Components.Count);
        Assert.IsType<Subcircuit>(vm.SelectedComponent);
        Assert.True(vm.IsModified);
    }
}
