using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The two new answers in the tolerance and sweep windows, driven without a window.</summary>
public class CornerAndSolveViewModelTests
{
    private static (Circuit Circuit, Resistor Top, Resistor Bottom) Divider(double tolerance = 0.05)
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0));
        var top = circuit.Add(new Resistor(10e3) { Name = "R1", Tolerance = tolerance });
        var bottom = circuit.Add(new Resistor(10e3) { Name = "R2", Tolerance = tolerance });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Mid", bottom.A, default));

        return (circuit, top, bottom);
    }

    // ---- worst case --------------------------------------------------------

    [Fact]
    public void TheWorstCaseIsFoundAndBothCornersAreListed()
    {
        var (circuit, _, _) = Divider();

        var model = new MonteCarloViewModel(circuit);
        model.FindCornersCommand.Execute(null);

        Assert.True(model.HasCorners);
        Assert.Equal(2, model.Corners.Count);

        Assert.Contains(model.Corners, c => c.Contains("Highest") && c.Contains("R1 low"));
        Assert.Contains(model.Corners, c => c.Contains("Lowest") && c.Contains("R1 high"));

        Assert.Contains("nominal", model.CornerSummary);
        Assert.Contains("solves", model.CornerSummary);
    }

    /// <summary>
    /// The summary contrasts the handful of solves it took against the number of corners there
    /// are, which is the whole argument for doing it this way.
    /// </summary>
    [Fact]
    public void TheSummarySaysHowMuchCheaperItWasThanTryingThemAll()
    {
        var (circuit, _, _) = Divider();

        var model = new MonteCarloViewModel(circuit);
        model.FindCornersCommand.Execute(null);

        Assert.Contains("rather than 4 corners", model.CornerSummary);
    }

    [Fact]
    public void WithNothingTolerancedItSaysSoRatherThanShowingCorners()
    {
        var (circuit, _, _) = Divider(tolerance: 0);

        var model = new MonteCarloViewModel(circuit);
        model.FindCornersCommand.Execute(null);

        Assert.False(model.HasCorners);
        Assert.Contains("tolerance", model.CornerSummary, StringComparison.OrdinalIgnoreCase);
    }

    // ---- solving for a value -----------------------------------------------

    /// <summary>
    /// Two volts out of a ten-volt divider with a 10 kΩ top needs 2.5 kΩ at the bottom, and the
    /// window also says which part you could actually buy.
    /// </summary>
    [Fact]
    public void ItSolvesForTheValueAndNamesTheNearestPart()
    {
        var (circuit, _, bottom) = Divider();

        var model = new DcSweepViewModel(circuit)
        {
            Sweep = new DcSweepViewModel(circuit).Options.Single(o =>
                o.Component == bottom && o.PropertyName == nameof(Resistor.Resistance)),
            Start = 100,
            Stop = 100e3,
            TargetValue = 2.0,
        };

        model.SolveForValueCommand.Execute(null);

        Assert.True(model.HasSolveResult);

        Assert.Contains("R2", model.SolveResult);
        Assert.Contains("2.5k", model.SolveResult);
        Assert.Contains("E24", model.SolveResult);
        Assert.Contains("2.4k", model.SolveResult);
    }

    /// <summary>
    /// A target the range cannot reach is reported with what the range can do, which tells you
    /// which way to widen it.
    /// </summary>
    [Fact]
    public void ATargetOutOfReachSaysWhatTheRangeCanDo()
    {
        var (circuit, _, bottom) = Divider();

        var model = new DcSweepViewModel(circuit)
        {
            Sweep = new DcSweepViewModel(circuit).Options.Single(o =>
                o.Component == bottom && o.PropertyName == nameof(Resistor.Resistance)),
            Start = 100,
            Stop = 1e3,
            TargetValue = 9.0,
        };

        model.SolveForValueCommand.Execute(null);

        Assert.True(model.HasSolveResult);
        Assert.Contains("Widen the range", model.SolveResult);
    }

    [Fact]
    public void WithNoProbeItSaysSo()
    {
        var (circuit, _, _) = Divider();
        circuit.Probes.Clear();

        var model = new DcSweepViewModel(circuit) { TargetValue = 2.0 };
        model.SolveForValueCommand.Execute(null);

        Assert.Contains("put a probe", model.SolveResult, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Solving leaves the circuit exactly as it was.</summary>
    [Fact]
    public void SolvingChangesNothingAboutTheCircuit()
    {
        var (circuit, _, bottom) = Divider();

        var model = new DcSweepViewModel(circuit)
        {
            Sweep = new DcSweepViewModel(circuit).Options.Single(o =>
                o.Component == bottom && o.PropertyName == nameof(Resistor.Resistance)),
            Start = 100,
            Stop = 100e3,
            TargetValue = 2.0,
        };

        model.SolveForValueCommand.Execute(null);

        Assert.Equal(10e3, bottom.Resistance, 6);
    }
}
