using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Core.Probing;
using Cirq.Engine.Simulation;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The examples built for the analyses — each one asserted against the thing it exists to show,
/// and against the fact that it shows it <i>as shipped</i>, with nothing to set up first.
/// </summary>
public class AnalysisExampleTests
{
    private static MainWindowViewModel Load(string name)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();

        Examples.All.Single(e => e.Name == name).Build(vm);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        return vm;
    }

    // ---- XY -----------------------------------------------------------------

    [Fact]
    public void TheHysteresisExampleOpensAlreadyInXyWithTheInputAlongTheBottom()
    {
        var vm = Load("Hysteresis Loop");

        Assert.Equal(ScopeLayout.Xy, vm.Scope.Layout);
        Assert.True(vm.Scope.IsXy);

        var (horizontal, vertical) = vm.Scope.XyPairs();

        Assert.Equal("Input", horizontal.Label);
        Assert.Equal("Output", Assert.Single(vertical).Label);
    }

    /// <summary>
    /// The loop itself: at the same input voltage the output is in two different places depending
    /// on which way the input was going. That is what a loop <i>is</i>, and it is the thing a DC
    /// sweep cannot draw.
    /// </summary>
    [Fact]
    public void AndItDrawsAnActualLoop()
    {
        var vm = Load("Hysteresis Loop");
        var sim = vm.Simulation.Simulator!;

        sim.Run(12e-3);

        var (horizontal, vertical) = vm.Scope.XyPairs();
        var (x, y) = ScopeViewModel.XySeries(horizontal, vertical[0], 0, 12e-3);

        Assert.True(x.Length > 200);

        // Find the input voltage where the output is highest and where it is lowest, then look for
        // points near the middle of the input range sitting at both output levels.
        var high = y.Max();
        var low = y.Min();
        var middle = (high + low) / 2;

        var lowX = new List<double>();
        var highX = new List<double>();

        for (var i = 0; i < x.Length; i++)
        {
            if (y[i] > middle) highX.Add(x[i]);
            else lowX.Add(x[i]);
        }

        Assert.NotEmpty(lowX);
        Assert.NotEmpty(highX);

        // The two branches overlap in input voltage: there is a band of input where the output is
        // sometimes high and sometimes low, and the width of that band is the hysteresis.
        var overlap = Math.Min(highX.Max(), lowX.Max()) - Math.Max(highX.Min(), lowX.Min());

        Assert.True(overlap > 0.05, $"the two branches only overlapped by {overlap:F3} V");
    }

    // ---- tolerance ----------------------------------------------------------

    [Fact]
    public void TheBridgeReadsNothingNominallyAndSomethingOnceToleranceIsAllowedFor()
    {
        var vm = Load("Resistor Bridge");

        // Four parts with a tolerance, ready for the analysis with nothing to set up.
        var model = new MonteCarloViewModel(vm.Circuit) { Trials = 400 };
        model.Run();

        Assert.Equal(4, model.Varied.Count);

        var row = Assert.Single(model.Rows);

        // Nominally the two midpoints are identical and the bridge reads nothing at all.
        Assert.Equal(0.0, row.Trace.Nominal, 6);

        // In practice, hundreds of millivolts either way.
        Assert.True(row.Trace.Maximum > 0.1, $"the bridge only reached {row.Trace.Maximum:F3} V");
        Assert.True(row.Trace.Minimum < -0.1);
    }

    [Fact]
    public void AndItIsProbedDifferentiallyBecauseBothEndsSitAtHalfTheSupply()
    {
        var vm = Load("Resistor Bridge");

        var probe = Assert.Single(vm.Circuit.Probes);

        Assert.Equal(ProbeKind.Differential, probe.Kind);
        Assert.NotNull(probe.ReferenceTerminal);

        var sim = vm.Simulation.Simulator!;

        // The measurement is nothing; each end on its own is half the supply.
        Assert.Equal(0.0, sim.SampleProbe(probe), 6);
        Assert.Equal(5.0, sim.NodeVoltage(probe.TargetTerminal!), 3);
    }

    // ---- derived probes -----------------------------------------------------

    [Fact]
    public void TheShuntExampleReadsMillivoltsAcrossItAndVoltsAgainstGround()
    {
        var vm = Load("High-Side Sensing");
        var sim = vm.Simulation.Simulator!;

        var across = vm.Circuit.Probes.Single(p => p.Label == "Across the shunt");

        // 24 V across 12.1 ohms is 1.98 A; through a tenth of an ohm that is 198 mV.
        var current = 24.0 / 12.1;

        Assert.Equal(current * 0.1, sim.SampleProbe(across), 4);

        // Where the same point against ground is the rail, and the measurement is lost in it.
        Assert.InRange(sim.NodeVoltage(across.TargetTerminal!), 23.9, 24.0);
    }

    [Fact]
    public void AndReportsWhatTheMeasurementCostsAgainstWhatTheLoadGets()
    {
        var vm = Load("High-Side Sensing");
        var sim = vm.Simulation.Simulator!;

        var shunt = Math.Abs(sim.SampleProbe(vm.Circuit.Probes.Single(p => p.Label == "Shunt power")));
        var load = Math.Abs(sim.SampleProbe(vm.Circuit.Probes.Single(p => p.Label == "Load power")));

        var current = 24.0 / 12.1;

        Assert.Equal(current * current * 0.1, shunt, 3);
        Assert.Equal(current * current * 12.0, load, 2);

        // Under a percent of the power, which is the trade a sense resistor is.
        Assert.InRange(shunt / load, 0.001, 0.02);
    }

    // ---- temperature --------------------------------------------------------

    [Fact]
    public void TheThermometerIsHeldAtAConstantCurrentSoItsDropTracksTemperature()
    {
        var vm = Load("Diode Thermometer");

        // A current source, not a resistor from a rail — which is the whole design.
        Assert.Single(vm.Circuit.Components.OfType<Cirq.Components.Sources.DcCurrentSource>());
        Assert.Empty(vm.Circuit.Components.OfType<Resistor>());

        var sim = vm.Simulation.Simulator!;
        var diode = vm.Circuit.Components.OfType<Diode>().Single();

        var sweep = new DcSweep(sim).Run(new DcSweepRequest(
            SweepTarget.OverTemperature(-40, 125, 34)));

        Assert.Equal(0, sweep.FailedPoints);

        var drops = sweep.Curves[0].Traces[0].Values;

        // A straight line down, which is what makes it usable as a thermometer at all.
        for (var i = 1; i < drops.Count; i++)
            Assert.True(drops[i] < drops[i - 1]);

        var slope = (drops[^1] - drops[0]) / (sweep.X[^1] - sweep.X[0]) * 1000;

        Assert.InRange(slope, -2.2, -1.8);
    }

    [Fact]
    public void AndTheDcSweepOffersTemperatureWithNothingToSetUp()
    {
        var vm = Load("Diode Thermometer");

        var model = new DcSweepViewModel(vm.Circuit);
        model.Sweep = model.Options.Single(o => o.IsTemperature);

        model.Run();

        var curve = Assert.Single(model.Curves);

        Assert.Equal(-40, model.Start);
        Assert.Equal(125, model.Stop);
        Assert.True(curve.Y[0] > curve.Y[^1]);
    }
}
