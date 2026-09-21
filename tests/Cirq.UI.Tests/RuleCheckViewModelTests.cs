using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The rule-check window's own logic, driven without a window.</summary>
public class RuleCheckViewModelTests
{
    private static Circuit GoodDivider()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var top = circuit.Add(new Resistor(1e3));
        var bottom = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        return circuit;
    }

    [Fact]
    public void ACleanCircuitSaysSoRatherThanShowingAnEmptyList()
    {
        var model = new RuleCheckViewModel(GoodDivider());

        Assert.True(model.IsClean);
        Assert.Empty(model.Rows);
        Assert.Contains("Nothing to report", model.Summary);
    }

    [Fact]
    public void ProblemsAreListedWithErrorsFirstAndCounted()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var dangling = circuit.Add(new Resistor(1e3));

        circuit.Connect(supply.Positive, dangling.A);

        var model = new RuleCheckViewModel(circuit);

        Assert.False(model.IsClean);
        Assert.NotEmpty(model.Rows);
        Assert.True(model.Rows[0].IsError);
        Assert.Contains("error", model.Summary);
    }

    [Fact]
    public void RunningItAgainReplacesTheListRatherThanAppendingToIt()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var dangling = circuit.Add(new Resistor(1e3));
        circuit.Connect(supply.Positive, dangling.A);

        var model = new RuleCheckViewModel(circuit);
        var first = model.Rows.Count;

        model.Run();

        Assert.Equal(first, model.Rows.Count);
    }

    [Fact]
    public void FixingTheCircuitAndRunningAgainClearsTheList()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var load = circuit.Add(new Resistor(1e3));

        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, supply.Negative);

        var model = new RuleCheckViewModel(circuit);
        Assert.False(model.IsClean);

        // The missing ground, which is what it was complaining about.
        var ground = circuit.Add(new Ground());
        circuit.Connect(supply.Negative, ground.Pin);

        model.Run();

        Assert.True(model.IsClean);
    }

    [Fact]
    public void ChoosingAFindingAsksForItsPartsToBeShown()
    {
        var circuit = GoodDivider();
        var stray = circuit.Add(new NetLabel("TYPO"));
        circuit.Connect(stray.Pin, circuit.Components.OfType<Resistor>().First().A);

        var model = new RuleCheckViewModel(circuit);

        IReadOnlyList<CircuitComponent>? revealed = null;
        model.RevealRequested += (_, components) => revealed = components;

        model.Reveal(model.Rows.First(r => r.Finding.Rule == "label-alone"));

        Assert.NotNull(revealed);
        Assert.Contains(stray, revealed!);
    }

    [Fact]
    public void AFindingAboutTheWholeCircuitHasNothingToShowAndDoesNotAsk()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var load = circuit.Add(new Resistor(1e3));
        circuit.Connect(supply.Positive, load.A);
        circuit.Connect(load.B, supply.Negative);

        var model = new RuleCheckViewModel(circuit);

        var asked = false;
        model.RevealRequested += (_, _) => asked = true;

        var row = model.Rows.First(r => r.Finding.Rule == "no-ground");

        Assert.Equal("circuit", row.Where);

        model.Reveal(row);

        Assert.False(asked);
    }

    [Fact]
    public void RevealingSelectsThePartsOnTheCanvasAndNothingElse()
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();

        var first = vm.Circuit.Add(new Resistor(1e3));
        var second = vm.Circuit.Add(new Resistor(2e3));

        second.IsSelected = true;

        vm.Reveal([first]);

        Assert.True(first.IsSelected);
        Assert.False(second.IsSelected);
        Assert.Same(first, vm.SelectedComponent);
    }
}
