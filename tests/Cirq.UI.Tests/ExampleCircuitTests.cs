using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// End-to-end coverage of the shipped examples. Each one is built through the same view model the
/// menu uses, compiled, and run, so a broken symbol, a mis-wired pin or a non-converging circuit
/// shows up as a failing test rather than as a dead menu entry.
/// </summary>
public class ExampleCircuitTests
{
    public static TheoryData<string> ExampleNames()
    {
        var data = new TheoryData<string>();
        foreach (var example in Examples.All) data.Add(example.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void EveryExampleCompilesAndRuns(string name)
    {
        var example = Examples.All.Single(e => e.Name == name);

        using var viewModel = new MainWindowViewModel();
        viewModel.Circuit.Clear();
        example.Build(viewModel);

        var controller = viewModel.Simulation;
        Assert.True(controller.Rebuild(), $"'{name}' failed to compile: {controller.Status}");

        var simulator = controller.Simulator!;

        // Run a slice of transient analysis; a convergence failure throws out of Step.
        var settled = simulator.Time;
        for (var i = 0; i < 400; i++) simulator.Step();

        Assert.True(simulator.Time > settled, $"'{name}' did not advance in time.");
        Assert.False(controller.HasError, $"'{name}' reported: {controller.Status}");
    }

    [Theory]
    [MemberData(nameof(ExampleNames))]
    public void EveryExampleArrivesWithProbesAttached(string name)
    {
        var example = Examples.All.Single(e => e.Name == name);

        using var viewModel = new MainWindowViewModel();
        viewModel.Circuit.Clear();
        viewModel.Scope.ClearProbes();
        example.Build(viewModel);

        Assert.NotEmpty(viewModel.Scope.Probes);
        Assert.All(viewModel.Scope.Probes, p => Assert.NotNull(p.TargetTerminal));
    }

    [Fact]
    public void ProbesRecordSamplesAsTheSimulationAdvances()
    {
        using var viewModel = new MainWindowViewModel();
        var controller = viewModel.Simulation;

        Assert.True(controller.Rebuild());
        var simulator = controller.Simulator!;

        for (var i = 0; i < 5000; i++) simulator.Step();

        var probe = viewModel.Scope.Probes.First();
        Assert.True(probe.HistoryBuffer.Count > 10,
            $"Only {probe.HistoryBuffer.Count} samples were recorded.");

        // Samples must be in increasing time order for the scope to plot them.
        var samples = probe.HistoryBuffer.ToArray();
        for (var i = 1; i < samples.Length; i++)
            Assert.True(samples[i].Time >= samples[i - 1].Time, "Probe samples went backwards in time.");
    }

    [Fact]
    public void ProbeSamplingIsDecimatedToTheScopeTimebase()
    {
        using var viewModel = new MainWindowViewModel();
        viewModel.Scope.TimebasePerDivision = 1e-3;     // 10 ms window -> 2.5 us between samples.

        var controller = viewModel.Simulation;
        Assert.True(controller.Rebuild());
        var simulator = controller.Simulator!;

        var interval = controller.Settings.ProbeSampleInterval;
        Assert.True(interval > 0, "The scope did not set a sample interval.");

        for (var i = 0; i < 4000; i++) simulator.Step();

        var probe = viewModel.Scope.Probes.First();
        var samples = probe.HistoryBuffer.ToArray();

        Assert.True(samples.Length > 2, "Decimation discarded everything.");
        // Consecutive samples must be at least one interval apart, which is the whole point.
        for (var i = 1; i < samples.Length; i++)
            Assert.True(samples[i].Time - samples[i - 1].Time >= interval * 0.999,
                $"Samples {i - 1} and {i} were only {samples[i].Time - samples[i - 1].Time:g3}s apart.");
    }

    [Fact]
    public void TheApplicationOpensOnACompiledCircuit()
    {
        // The window is expected to appear with something already running, not a blank canvas.
        using var viewModel = new MainWindowViewModel();

        Assert.NotEmpty(viewModel.Circuit.Components);
        Assert.NotEmpty(viewModel.Circuit.Wires);
        Assert.NotEmpty(viewModel.Scope.Probes);
        Assert.NotNull(viewModel.Simulation.Simulator);
        Assert.False(viewModel.Simulation.HasError);
        Assert.StartsWith("Ready", viewModel.Simulation.Status);
    }

    [Fact]
    public void LoadingAnExampleReplacesThePreviousCircuit()
    {
        using var viewModel = new MainWindowViewModel();
        Assert.Contains(viewModel.Circuit.Components, c => c is Cirq.Components.Sources.FunctionGenerator);

        var counter = Examples.All.Single(e => e.Name == "Decade Counter");
        viewModel.LoadExampleCommand.Execute(counter);

        Assert.Equal("7490 decade counter", viewModel.Circuit.Title);
        Assert.Contains(viewModel.Circuit.Components, c => c is Cirq.Components.Digital.Ic7490);
        // The previous circuit is gone, not merged into the new one.
        Assert.DoesNotContain(viewModel.Circuit.Components, c => c is Cirq.Components.Sources.FunctionGenerator);
        Assert.False(viewModel.Simulation.HasError, viewModel.Simulation.Status);
    }

    [Fact]
    public void EveryPaletteEntryCanBePlacedAndNamed()
    {
        using var viewModel = new MainWindowViewModel();
        viewModel.Circuit.Clear();

        foreach (var item in ComponentCatalog.AllItems)
        {
            var component = item.Create();
            viewModel.Circuit.Add(component);
            Assert.False(string.IsNullOrWhiteSpace(component.Name), $"{item.Name} was placed unnamed.");
            Assert.NotEmpty(component.Terminals);
        }

        // Reference designators must be unique so the netlist can be read.
        var names = viewModel.Circuit.Components.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
