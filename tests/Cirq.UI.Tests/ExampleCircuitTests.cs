using Cirq.Components.Passive;
using Cirq.Components.Nonlinear;
using Cirq.Components.Boards;
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

public class RegulatedSupplyExampleTests
{
    /// <summary>
    /// Reported from the running app: the 7805 example showed just under half a volt instead of
    /// five. The example is built with an output capacitor and the editor runs from initial
    /// conditions, so switch-on drew a brief inrush that the thermal model read as heat.
    /// </summary>
    [Fact]
    public void TheSevenEightOhFiveExampleActuallyRegulatesToFiveVolts()
    {
        using var vm = new MainWindowViewModel();
        Examples.LoadRegulatedSupply(vm);
        vm.Simulation.InvalidateTopology();
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var regulator = vm.Circuit.Components.OfType<VoltageRegulator>().Single();
        vm.Simulation.Simulator!.Run(5e-3);

        Assert.Equal(12.0, vm.Simulation.Simulator!.NodeVoltage(regulator.Input), 0.1);
        Assert.Equal(5.0, vm.Simulation.Simulator!.NodeVoltage(regulator.Output), 0.05);
        Assert.False(regulator.IsThermallyShutDown);
        Assert.False(regulator.IsInDropout);
    }

    [Fact]
    public void TheExampleHoldsFiveVoltsForTheWholeScopeWindow()
    {
        using var vm = new MainWindowViewModel();
        Examples.LoadRegulatedSupply(vm);
        vm.Simulation.InvalidateTopology();
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var regulator = vm.Circuit.Components.OfType<VoltageRegulator>().Single();
        var simulator = vm.Simulation.Simulator!;

        // Past the startup ramp, so the readout on screen is steady rather than still climbing.
        simulator.Run(1e-3);
        var lowest = double.MaxValue;
        simulator.TimePointAccepted += _ =>
            lowest = Math.Min(lowest, simulator.NodeVoltage(regulator.Output));

        simulator.Run(10e-3);

        Assert.True(lowest > 4.9, $"the output sagged to {lowest:0.000} V after settling");
    }
}

/// <summary>
/// The Raspberry Pi example is the one circuit that exercises a development board, so it is worth
/// checking it behaves rather than merely compiling: the board's own rule checks must stay quiet,
/// and each of the three pin roles must actually do its job.
/// </summary>
public class RaspberryPiExampleTests
{
    private static (MainWindowViewModel Vm, DeveloperBoard Board) Load()
    {
        var vm = new MainWindowViewModel();
        Examples.LoadRaspberryPiGpio(vm);
        vm.Simulation.InvalidateTopology();
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        return (vm, vm.Circuit.Components.OfType<DeveloperBoard>().Single());
    }

    [Fact]
    public void TheLedsAreResistoredWithinThePinsCurrentRating()
    {
        var (vm, board) = Load();
        using var _ = vm;

        vm.Simulation.Simulator!.Run(1e-3);
        board.CheckLimits(vm.Simulation.Simulator!.System);

        // An example that trips the board's own warnings would be teaching the wrong thing.
        Assert.Empty(board.Violations);
        Assert.InRange(board.GpioCurrent, 0.001, 0.016);
    }

    [Fact]
    public void TheButtonPinIdlesHighOnTheInternalPullUpAndFallsWhenPressed()
    {
        var (vm, board) = Load();
        using var _ = vm;

        var pin = board.Pin("GPIO25");
        var button = vm.Circuit.Components.OfType<PushButton>().Single();

        vm.Simulation.Simulator!.Run(1e-3);
        var released = vm.Simulation.Simulator!.NodeVoltage(pin);

        button.IsPressed = true;
        vm.Simulation.InvalidateTopology();
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        vm.Simulation.Simulator!.Run(1e-3);
        var pressed = vm.Simulation.Simulator!.NodeVoltage(pin);

        Assert.True(released > 3.0, $"idle sat at {released:0.00} V, expected the pull-up to hold it high");
        Assert.True(pressed < 0.3, $"pressed sat at {pressed:0.00} V, expected the button to pull it down");
    }

    [Fact]
    public void TheDrivenPinsActuallySwitchOverTheScopeWindow()
    {
        var (vm, board) = Load();
        using var _ = vm;

        var simulator = vm.Simulation.Simulator!;
        double clockLow = double.MaxValue, clockHigh = double.MinValue;
        double patternLow = double.MaxValue, patternHigh = double.MinValue;

        simulator.TimePointAccepted += _ =>
        {
            var clock = simulator.NodeVoltage(board.Pin("GPIO18"));
            var pattern = simulator.NodeVoltage(board.Pin("GPIO23"));
            clockLow = Math.Min(clockLow, clock);
            clockHigh = Math.Max(clockHigh, clock);
            patternLow = Math.Min(patternLow, pattern);
            patternHigh = Math.Max(patternHigh, pattern);
        };

        // One screen's worth at the timebase the example sets.
        simulator.Run(10e-3);

        Assert.True(clockHigh - clockLow > 2.0, $"clock pin only moved {clockHigh - clockLow:0.00} V");
        Assert.True(patternHigh - patternLow > 2.0, $"pattern pin only moved {patternHigh - patternLow:0.00} V");
    }
}

/// <summary>
/// The supply example is the one circuit that chains all three power parts together, so it is
/// worth checking it actually delivers rather than merely compiling.
/// </summary>
public class LinearPowerSupplyExampleTests
{
    private static (MainWindowViewModel Vm, VoltageRegulator Reg, BridgeRectifier Bridge) Load()
    {
        var vm = new MainWindowViewModel();
        Examples.LoadLinearPowerSupply(vm);
        vm.Simulation.InvalidateTopology();
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        return (vm,
            vm.Circuit.Components.OfType<VoltageRegulator>().Single(),
            vm.Circuit.Components.OfType<BridgeRectifier>().Single());
    }

    [Fact]
    public void ItSettlesAtTwelveVolts()
    {
        var (vm, reg, _) = Load();
        using var _guard = vm;

        vm.Simulation.Simulator!.Run(150e-3);

        Assert.Equal(12.0, vm.Simulation.Simulator!.NodeVoltage(reg.Output), 0.2);
        Assert.False(reg.IsInDropout);
        Assert.False(reg.IsThermallyShutDown);
    }

    /// <summary>
    /// The two probes have to actually show the contrast the example exists to demonstrate: a
    /// sawtooth on the reservoir, a flat line on the regulated rail.
    /// </summary>
    [Fact]
    public void TheReservoirRipplesAndTheRegulatedRailDoesNot()
    {
        var (vm, reg, bridge) = Load();
        using var _guard = vm;

        var simulator = vm.Simulation.Simulator!;
        simulator.Run(150e-3);

        double inLow = double.MaxValue, inHigh = double.MinValue;
        double outLow = double.MaxValue, outHigh = double.MinValue;
        simulator.TimePointAccepted += _ =>
        {
            var vin = simulator.NodeVoltage(bridge.Positive);
            var vout = simulator.NodeVoltage(reg.Output);
            inLow = Math.Min(inLow, vin); inHigh = Math.Max(inHigh, vin);
            outLow = Math.Min(outLow, vout); outHigh = Math.Max(outHigh, vout);
        };

        simulator.Run(40e-3);

        Assert.True(inHigh - inLow > 0.3, $"the reservoir only rippled {(inHigh - inLow) * 1000:0} mV");
        Assert.True(outHigh - outLow < 0.05, $"the regulated rail rippled {(outHigh - outLow) * 1000:0} mV");
    }

    [Fact]
    public void NoPartInTheExampleIsUsedOutsideItsRatings()
    {
        var (vm, reg, _) = Load();
        using var _guard = vm;

        vm.Simulation.Simulator!.Run(150e-3);

        foreach (var capacitor in vm.Circuit.Components.OfType<ElectrolyticCapacitor>())
            Assert.Empty(capacitor.Violations);

        Assert.False(reg.IsThermallyShutDown);
    }

    [Fact]
    public void ItAsksForRealTimeBecauseFiftyHertzIsUnwatchableAtTheDefaultSpeed()
    {
        var (vm, _, _) = Load();
        using var _guard = vm;

        // A 50 Hz cycle at 1/1000 would be fifty seconds of wall time.
        Assert.Equal(1.0, vm.Simulation.SpeedFactor);
    }
}
