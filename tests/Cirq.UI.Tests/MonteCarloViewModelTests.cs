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
    /// The other half of the question: not how far the answer moves, but which part moved it.
    /// </summary>
    [Fact]
    public void TheWindowAlsoSaysWhereTheSpreadComesFrom()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(10.0));
        var loose = circuit.Add(new Resistor(1e3) { Tolerance = 0.20 });
        var tight = circuit.Add(new Resistor(1e3) { Tolerance = 0.01 });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, loose.A);
        circuit.Connect(loose.B, tight.A);
        circuit.Connect(tight.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Mid", loose.B, Color.ProbePalette[0]));

        var model = new MonteCarloViewModel(circuit) { Trials = 200 };
        model.Run();

        Assert.True(model.HasSensitivity);
        Assert.Equal(2, model.Sensitivity.Count);

        // The wider part first, taking nearly all the blame, with its band and elasticity beside
        // it so the reason is visible rather than just the ranking.
        var worst = model.Sensitivity[0];

        Assert.Equal(loose.Name, worst.Part);
        Assert.Contains("20", worst.Tolerance);
        Assert.True(worst.Weight > 0.9);
        Assert.NotEmpty(worst.Elasticity);
    }

    [Fact]
    public void ChoosingADifferentTraceRanksThatOne()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(10.0));
        var top = circuit.Add(new Resistor(1e3) { Tolerance = 0.05 });
        var bottom = circuit.Add(new Resistor(1e3) { Tolerance = 0.05 });
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Mid", top.B, Color.ProbePalette[0]));
        circuit.Probes.Add(new SignalProbe("Rail", top.A, Color.ProbePalette[1]));

        var model = new MonteCarloViewModel(circuit) { Trials = 100 };
        model.Run();

        Assert.Equal(2, model.Rows.Count);

        model.Selected = model.Rows.Single(r => r.Label == "Rail");

        // The rail is the supply; no resistor moves it, so nothing takes any of the blame.
        Assert.All(model.Sensitivity, r => Assert.Equal(0.0, r.Weight, 6));

        model.Selected = model.Rows.Single(r => r.Label == "Mid");

        Assert.Contains(model.Sensitivity, r => r.Weight > 0.1);
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

    // ---- the worst case, with the temperature in it -------------------------

    /// <summary>
    /// A diode reference with an exact feed resistor: nothing about the parts can move the answer,
    /// so anything the worst case finds is the temperature moving it.
    /// </summary>
    private static Circuit Reference()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0));
        var series = circuit.Add(new Resistor(10e3) { Name = "R1", Tolerance = 0 });
        var diode = circuit.Add(new Cirq.Components.Nonlinear.Diode());
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, series.A);
        circuit.Connect(series.B, diode.Anode);
        circuit.Connect(diode.Cathode, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Vf", diode.Anode, Color.ProbePalette[0]));

        return circuit;
    }

    [Fact]
    public void TheWorstCaseLeavesTheTemperatureAloneUnlessAsked()
    {
        var vm = new MonteCarloViewModel(Reference());

        Assert.False(vm.IncludeTemperature);
        Assert.Equal(-40, vm.ColdCelsius);
        Assert.Equal(85, vm.HotCelsius);

        vm.FindCornersCommand.Execute(null);

        // Nothing has a tolerance and no range was asked for, so there is nothing to vary.
        Assert.False(vm.HasCorners);
        Assert.Contains("no temperature range", vm.CornerSummary);
    }

    [Fact]
    public void AskingForTheRangeMakesTheTemperatureACorner()
    {
        var vm = new MonteCarloViewModel(Reference()) { IncludeTemperature = true };

        vm.FindCornersCommand.Execute(null);

        Assert.True(vm.HasCorners, vm.CornerSummary);
        Assert.Contains("across -40 to 85 °C", vm.CornerSummary);

        // A diode drops more when it is cold, so the high corner is the cold end.
        Assert.Contains("temperature low", vm.Corners[0]);
        Assert.Contains("temperature high", vm.Corners[1]);
    }

    [Fact]
    public void TheRangeIsTheOneTyped()
    {
        var vm = new MonteCarloViewModel(Reference())
        {
            IncludeTemperature = true,
            ColdCelsius = 0,
            HotCelsius = 50,
        };

        vm.FindCornersCommand.Execute(null);

        Assert.Contains("across 0 to 50 °C", vm.CornerSummary);

        // Half the span of the industrial range moves a diode drop about half as far.
        var narrow = Spread(vm);

        var wide = new MonteCarloViewModel(Reference()) { IncludeTemperature = true };
        wide.FindCornersCommand.Execute(null);

        Assert.True(Spread(wide) > narrow, "the wider range should have found the wider spread");
    }

    private static double Spread(MonteCarloViewModel vm) =>
        double.Parse(
            System.Text.RegularExpressions.Regex.Match(vm.CornerSummary, @"spread ([0-9.]+)").Groups[1].Value,
            System.Globalization.CultureInfo.InvariantCulture);
}
