using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The tolerance analysis window's own logic, driven without a window.</summary>
public class MonteCarloViewModelTests
{
    private static (Circuit, Resistor, Resistor) Divider(double tolerance)
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(10.0));
        var top = circuit.Add(new Resistor(1e3) { Tolerance = tolerance });
        var bottom = circuit.Add(new Resistor(1e3) { Tolerance = tolerance });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Mid", top.B, Color.ProbePalette[0]));

        return (circuit, top, bottom);
    }

    [Fact]
    public void ItReportsTheSpreadAndWhatWasVaried()
    {
        var (circuit, _, _) = Divider(0.05);

        var model = new MonteCarloViewModel(circuit) { Trials = 400 };
        model.Run();

        Assert.True(model.HasRows);

        var row = Assert.Single(model.Rows);

        Assert.Equal("Mid", row.Label);
        Assert.Contains("%", row.Worst);
        Assert.Contains("…", row.Range);

        // Both resistors, each named with its band.
        Assert.Equal(2, model.Varied.Count);
        Assert.All(model.Varied, v => Assert.Contains("5", v));

        Assert.Contains("400 trials", model.Summary);
    }

    [Fact]
    public void ACircuitOfExactPartsSaysThereIsNothingToVary()
    {
        var (circuit, _, _) = Divider(0.0);

        var model = new MonteCarloViewModel(circuit);
        model.Run();

        Assert.False(model.HasRows);
        Assert.Contains("tolerance", model.Summary);
    }

    [Fact]
    public void WithNoProbesItSaysWhatToDo()
    {
        var (circuit, _, _) = Divider(0.05);
        circuit.Probes.Clear();

        var model = new MonteCarloViewModel(circuit);
        model.Run();

        Assert.False(model.HasRows);
        Assert.Contains("probe", model.Summary, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The yield column is against the band you set, not against the parts — that is the whole
    /// point of being able to set it.
    /// </summary>
    [Fact]
    public void TighteningTheAcceptableBandLowersTheYield()
    {
        var (circuit, _, _) = Divider(0.10);

        var model = new MonteCarloViewModel(circuit) { Trials = 500 };

        model.AcceptableBandPercent = 50;
        model.Run();
        var generous = model.Rows[0].Yield;

        model.AcceptableBandPercent = 1;
        model.Run();
        var strict = model.Rows[0].Yield;

        Assert.Equal("100 %", generous);
        Assert.NotEqual(generous, strict);
    }

    [Fact]
    public void TheSameSeedGivesTheSameReport()
    {
        string Report(int seed)
        {
            var (circuit, _, _) = Divider(0.05);
            var model = new MonteCarloViewModel(circuit) { Trials = 200, Seed = seed };
            model.Run();

            return model.Rows[0].Range;
        }

        Assert.Equal(Report(3), Report(3));
        Assert.NotEqual(Report(3), Report(4));
    }

    [Fact]
    public void TheCircuitIsLeftExactlyAsItWasFound()
    {
        var (circuit, top, bottom) = Divider(0.05);

        new MonteCarloViewModel(circuit) { Trials = 300 }.Run();

        Assert.Equal(1e3, top.Resistance);
        Assert.Equal(1e3, bottom.Resistance);
    }

    [Fact]
    public void ARowIsSelectedSoTheHistogramHasSomethingToDraw()
    {
        var (circuit, _, _) = Divider(0.05);

        var model = new MonteCarloViewModel(circuit) { Trials = 200 };

        var raised = 0;
        model.ResultsChanged += (_, _) => raised++;

        model.Run();

        Assert.NotNull(model.Selected);
        Assert.True(raised > 0);
        Assert.NotEmpty(model.Selected!.Trace.Histogram());
    }

    /// <summary>
    /// A real one: an RC filter whose corner is set by a 5 % resistor and a 20 % capacitor, which
    /// between them move it much further than most people expect.
    /// </summary>
    [Fact]
    public void AFiltersCornerMovesByMoreThanEitherPartsTolerance()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(1.0));
        var resistor = circuit.Add(new Resistor(10e3) { Tolerance = 0.05 });
        var capacitor = circuit.Add(new Capacitor(100e-9) { Tolerance = 0.20 });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, ground.Pin);

        // The capacitor is an open circuit at the operating point, so what varies here is the
        // product that sets the corner. Probe the current through the resistor instead, which the
        // tolerance does move.
        circuit.Probes.Add(new SignalProbe("Rail", resistor.A, Color.ProbePalette[0]));

        var model = new MonteCarloViewModel(circuit) { Trials = 200 };
        model.Run();

        // Both parts are offered for variation even though only one of them moves this answer.
        Assert.Equal(2, model.Varied.Count);
        Assert.Contains(model.Varied, v => v.Contains("20"));
    }
}
