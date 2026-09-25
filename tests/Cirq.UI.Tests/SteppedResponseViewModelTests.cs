using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The frequency-response window with a family of curves on it.
/// <para>
/// The arithmetic is checked in the engine's own tests; what matters here is that the family
/// reaches the plot as separate, labelled curves, and that the summary says where each pass turned
/// over — which is the thing somebody was going to read off the plot by eye.
/// </para>
/// </summary>
public class SteppedResponseViewModelTests
{
    private static (Circuit Circuit, Resistor R) LowPass()
    {
        var circuit = new Circuit();

        var source = circuit.Add(new FunctionGenerator { Name = "FG1", AcMagnitude = 1.0 });
        var r = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var c = circuit.Add(new Capacitor(1e-7) { Name = "C1" });
        var ground = circuit.Add(new Ground { Name = "GND1" });

        circuit.Wires.Add(new WireSegment { SourceTerminal = source.Output, TargetTerminal = r.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = r.B, TargetTerminal = c.A });
        circuit.Wires.Add(new WireSegment { SourceTerminal = c.B, TargetTerminal = ground.Pin });
        circuit.Wires.Add(new WireSegment { SourceTerminal = source.Return, TargetTerminal = ground.Pin });

        circuit.Probes.Add(new SignalProbe { Label = "out", TargetTerminal = c.A });

        return (circuit, r);
    }

    /// <summary>One curve per pass, each saying which pass it is.</summary>
    [Fact]
    public void AFamilyArrivesAsSeparateLabelledCurves()
    {
        var (circuit, r) = LowPass();

        var model = new FrequencyResponseViewModel(circuit)
        {
            IsStepping = true,
            Step = new SweepOption(r, nameof(Resistor.Resistance), "Ω"),
            StepStart = 1e3,
            StepStop = 4e3,
            StepCount = 4,
        };

        model.Run();

        Assert.Equal(4, model.Curves.Count);
        Assert.All(model.Curves, c => Assert.StartsWith("out @ R1 = ", c.Label, StringComparison.Ordinal));

        // And they are four different curves, not the same one four times.
        Assert.Equal(4, model.Curves.Select(c => c.Decibels[^1]).Distinct().Count());
    }

    /// <summary>Unstepped, it is one curve, exactly as it always was.</summary>
    [Fact]
    public void WithoutSteppingItIsOneCurve()
    {
        var (circuit, _) = LowPass();

        var model = new FrequencyResponseViewModel(circuit);
        model.Run();

        var curve = Assert.Single(model.Curves);

        Assert.Equal("out", curve.Label);
        Assert.Contains("−3 dB", model.Status, StringComparison.Ordinal);
    }

    /// <summary>
    /// The summary says where each pass turned over. Reading four corners off a plot by eye is
    /// exactly what this replaces.
    /// </summary>
    [Fact]
    public void TheSummaryGivesACornerPerPass()
    {
        var (circuit, r) = LowPass();

        var model = new FrequencyResponseViewModel(circuit)
        {
            IsStepping = true,
            Step = new SweepOption(r, nameof(Resistor.Resistance), "Ω"),
            StepStart = 1e3,
            StepStop = 2e3,
            StepCount = 2,
        };

        model.Run();

        Assert.Contains("R1 stepped", model.Status, StringComparison.Ordinal);

        // Two corners, an octave apart because the resistance doubled.
        Assert.Equal(2, model.Status.Split("−3 dB at").Length - 1);
    }

    /// <summary>
    /// Choosing something to step fills the range from whatever it is set to, so the defaults land
    /// in the right decade instead of being zero to one for a 4k7 resistor.
    /// </summary>
    [Fact]
    public void ChoosingAParameterFillsInItsOwnRange()
    {
        var (circuit, _) = LowPass();

        var model = new FrequencyResponseViewModel(circuit);

        model.Step = model.Parameters.Single(
            o => o.Component?.Name == "R1" && o.PropertyName == nameof(Resistor.Resistance));

        Assert.Equal(1e3, model.StepStart, 1e-9);
        Assert.Equal(4e3, model.StepStop, 1e-9);
    }

    /// <summary>The picker offers the same things the DC sweep's does, including the temperature.</summary>
    [Fact]
    public void ThePickerOffersWhatTheDcSweepOffers()
    {
        var (circuit, _) = LowPass();

        var response = new FrequencyResponseViewModel(circuit).Parameters
            .Select(o => o.Display)
            .ToHashSet();

        var dc = new DcSweepViewModel(circuit).Options
            .Select(o => o.Display)
            .ToHashSet();

        Assert.Equal(dc, response);
        Assert.Contains("Circuit · Temperature", response);
    }
}
