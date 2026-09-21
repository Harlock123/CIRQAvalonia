using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The DC sweep window's own logic — what it offers to sweep, what it defaults to, and what comes
/// back — driven without a window, as the rest of the UI is.
/// </summary>
public class DcSweepViewModelTests
{
    private static (Circuit, DcVoltageSource, Resistor) Divider()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(4.0));
        var top = circuit.Add(new Resistor(1e3));
        var bottom = circuit.Add(new Resistor(1e3));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Mid", top.B, Color.ProbePalette[0]));

        return (circuit, supply, top);
    }

    [Fact]
    public void ItOffersEverySettableNumberInTheCircuitAndNoPlacement()
    {
        var (circuit, supply, _) = Divider();
        var model = new DcSweepViewModel(circuit);

        Assert.True(model.HasOptions);

        Assert.Contains(model.Options, o =>
            ReferenceEquals(o.Component, supply) && o.PropertyName == nameof(DcVoltageSource.Voltage));

        Assert.Contains(model.Options, o => o.PropertyName == nameof(Resistor.Resistance));

        // Where a part sits on the canvas is not a circuit parameter.
        Assert.DoesNotContain(model.Options, o => o.PropertyName is "X" or "Y" or "RotationDegrees");
    }

    [Fact]
    public void ItStartsOnASourceBecauseThatIsWhatADcSweepMeans()
    {
        var (circuit, supply, _) = Divider();
        var model = new DcSweepViewModel(circuit);

        Assert.NotNull(model.Sweep);
        Assert.Same(supply, model.Sweep!.Component);
        Assert.Equal(nameof(DcVoltageSource.Voltage), model.Sweep.PropertyName);
    }

    /// <summary>
    /// Landing on 0-to-5 whatever was picked is wrong more often than not — a base current lives
    /// in microamps — so the range comes from what the part is actually set to.
    /// </summary>
    [Fact]
    public void TheRangeDefaultsToSomethingTheSelectedPartCanDo()
    {
        var (circuit, _, _) = Divider();
        var model = new DcSweepViewModel(circuit);

        Assert.Equal(0, model.Start);
        Assert.Equal(4.8, model.Stop, 6);

        // Pick the resistor instead and the range follows it rather than staying in volts.
        model.Sweep = model.Options.First(o => o.PropertyName == nameof(Resistor.Resistance));

        Assert.Equal(1200.0, model.Stop, 6);
    }

    [Fact]
    public void SweepingTheSupplyGivesTheDividerLine()
    {
        var (circuit, _, _) = Divider();

        var model = new DcSweepViewModel(circuit) { Start = 0, Stop = 10, Points = 11 };
        model.Run();

        var curve = Assert.Single(model.Curves);

        Assert.Equal(11, curve.Y.Count);
        Assert.Equal(ProbeKind.Voltage, curve.Kind);

        // Half the input at every point, because that is what two equal resistors are.
        for (var i = 0; i < curve.X.Count; i++)
            Assert.Equal(curve.X[i] / 2, curve.Y[i], 6);

        Assert.Contains("points", model.Status);
        Assert.True(model.HasCurves);
    }

    [Fact]
    public void SteppingASecondParameterGivesOneCurvePerValue()
    {
        var (circuit, _, top) = Divider();

        var model = new DcSweepViewModel(circuit) { Start = 0, Stop = 10, Points = 11 };

        model.Step = model.Options.First(o =>
            ReferenceEquals(o.Component, top) && o.PropertyName == nameof(Resistor.Resistance));
        model.IsStepping = true;
        model.StepStart = 1e3;
        model.StepStop = 3e3;
        model.StepCount = 3;

        model.Run();

        Assert.Equal(3, model.Curves.Count);

        // Each curve is a divider with a different top resistor, so at ten volts in they read
        // 10·1k/2k = 5, 10·1k/3k = 3.33 and 10·1k/4k = 2.5.
        double[] expected = [5.0, 10.0 / 3.0, 2.5];

        for (var i = 0; i < 3; i++)
            Assert.Equal(expected[i], model.Curves[i].Y[^1], 3);

        // And each one says which value of the stepped parameter it belongs to.
        foreach (var curve in model.Curves) Assert.Contains(top.Name, curve.Label);
    }

    [Fact]
    public void ADiodeSweepGivesTheKneeAndTheWindowSaysWhereItPeaked()
    {
        var circuit = new Circuit();
        var supply = circuit.Add(new DcVoltageSource(1.0));
        var diode = circuit.Add(new Diode());
        var series = circuit.Add(new Resistor(220.0));
        var ground = circuit.Add(new Ground());

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, diode.Anode);
        circuit.Connect(diode.Cathode, series.A);
        circuit.Connect(series.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Id", diode.Anode, Color.ProbePalette[0])
        {
            Kind = ProbeKind.Current,
        });

        var model = new DcSweepViewModel(circuit) { Start = 0, Stop = 1.0, Points = 51 };
        model.Run();

        var curve = Assert.Single(model.Curves);

        Assert.Equal(ProbeKind.Current, curve.Kind);

        // Nothing at all below the knee, and milliamps above it: that ratio is the knee.
        var atQuarterVolt = Math.Abs(curve.Y[curve.X.Count / 4]);
        var atFullVolt = Math.Abs(curve.Y[^1]);

        Assert.True(atQuarterVolt < 1e-5, $"the diode was already passing {atQuarterVolt:G3} A at 0.25 V");
        Assert.True(atFullVolt > 1e-3, $"the diode only reached {atFullVolt:G3} A at 1 V");

        Assert.Contains("peaks at", model.Status);
    }

    [Fact]
    public void WithNoProbesItSaysSoRatherThanDrawingNothing()
    {
        var (circuit, _, _) = Divider();
        circuit.Probes.Clear();

        var model = new DcSweepViewModel(circuit);
        model.Run();

        Assert.Empty(model.Curves);
        Assert.False(model.HasCurves);
        Assert.Contains("probe", model.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASweepDoesNotDisturbTheCircuitItRanOn()
    {
        var (circuit, supply, top) = Divider();

        var model = new DcSweepViewModel(circuit) { Start = 0, Stop = 10, Points = 11 };

        model.Step = model.Options.First(o =>
            ReferenceEquals(o.Component, top) && o.PropertyName == nameof(Resistor.Resistance));
        model.IsStepping = true;
        model.StepStart = 1e3;
        model.StepStop = 5e3;
        model.StepCount = 3;

        model.Run();

        Assert.Equal(4.0, supply.Voltage);
        Assert.Equal(1e3, top.Resistance);
    }

    /// <summary>
    /// An empty canvas still has one thing that can be swept — its temperature, which belongs to
    /// the circuit rather than to any part — but nothing to measure, and it says so.
    /// </summary>
    [Fact]
    public void AnEmptyCircuitIsReportedRatherThanThrown()
    {
        var model = new DcSweepViewModel(new Circuit());

        var only = Assert.Single(model.Options);
        Assert.True(only.IsTemperature);

        model.Run();

        Assert.Empty(model.Curves);
        Assert.NotEmpty(model.Status);
    }

    [Fact]
    public void TheCircuitsTemperatureIsOfferedAlongsideThePartsAndDefaultsToItsSpecifiedRange()
    {
        var (circuit, _, _) = Divider();

        var model = new DcSweepViewModel(circuit);

        var temperature = Assert.Single(model.Options, o => o.IsTemperature);

        Assert.Equal("Circuit · Temperature", temperature.Display);
        Assert.Equal(27.0, temperature.Current);

        // Selecting it offers the range parts are specified over rather than zero to a bit above.
        model.Sweep = temperature;

        Assert.Equal(-40, model.Start);
        Assert.Equal(125, model.Stop);

        // And a source is still what it starts on, because that is what "DC sweep" means.
        Assert.False(new DcSweepViewModel(circuit).Sweep!.IsTemperature);
    }
}

/// <summary>The curve tracer example, which exists to be swept rather than run.</summary>
public class CurveTracerExampleTests
{
    [Fact]
    public void TheCurveTracerExampleSweepsOutTheDatasheetFan()
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();

        Examples.All.Single(e => e.Name == "Curve Tracer").Build(vm);

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var model = new DcSweepViewModel(vm.Circuit) { Start = 0, Stop = 5, Points = 26 };

        // The base current source is what a curve tracer steps.
        model.Step = model.Options.First(o =>
            o.Component is Cirq.Components.Sources.DcCurrentSource &&
            o.PropertyName == "Current");

        model.IsStepping = true;
        model.StepStart = 10e-6;
        model.StepStop = 40e-6;
        model.StepCount = 4;

        model.Run();

        Assert.Equal(4, model.Curves.Count);

        // Ordered and separated at every point in the flat region: more base current is more
        // collector current, which is the fan.
        for (var i = 1; i < model.Curves.Count; i++)
        for (var x = 10; x < model.Curves[i].X.Count; x++)
        {
            var below = Math.Abs(model.Curves[i - 1].Y[x]);
            var above = Math.Abs(model.Curves[i].Y[x]);

            Assert.True(above > below, $"curve {i} was not above curve {i - 1} at point {x}");
        }

        // And the spacing is even, because the base is driven by a current rather than a voltage.
        var gaps = new List<double>();
        for (var i = 1; i < model.Curves.Count; i++)
            gaps.Add(Math.Abs(model.Curves[i].Y[20]) - Math.Abs(model.Curves[i - 1].Y[20]));

        Assert.InRange(gaps.Max() - gaps.Min(), 0, gaps.Average() * 0.1);
    }
}
