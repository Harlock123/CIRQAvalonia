using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The scope's measurement readouts and time cursors, driven without a window.
/// </summary>
public class ScopeMeasurementTests
{
    private static (ScopeViewModel, SignalProbe) Rig(
        double hertz = 1e3, double amplitude = 2.0, double seconds = 20e-3)
    {
        var circuit = new Circuit();
        var scope = new ScopeViewModel(circuit);

        var resistor = circuit.Add(new Cirq.Components.Passive.Resistor(1e3));
        var probe = scope.AddProbe(resistor.A, "Sig");

        var step = 1.0 / (hertz * 200);
        for (var t = 0.0; t <= seconds; t += step)
            probe.Record(t, amplitude * Math.Sin(2 * Math.PI * hertz * t));

        return (scope, probe);
    }

    [Fact]
    public void MeasuringFillsEachTraceWithWhatItIsDoing()
    {
        var (scope, probe) = Rig();

        scope.Measure(0, 20e-3);

        var m = probe.Measurements;

        Assert.True(m.IsValid);
        Assert.Equal(4.0, m.PeakToPeak, 1);
        Assert.NotNull(m.Frequency);
        Assert.Equal(1e3, m.Frequency!.Value, 20.0);

        // The compact line the trace list shows, and the full set behind its tooltip.
        Assert.Contains("pp", probe.Summary);
        Assert.Contains("Hz", probe.Summary);
        Assert.Contains("RMS", probe.SummaryDetail);
        Assert.Contains("Duty cycle", probe.SummaryDetail);
    }

    [Fact]
    public void AHiddenTraceIsNotMeasured()
    {
        var (scope, probe) = Rig();

        probe.IsVisible = false;
        scope.Measure(0, 20e-3);

        Assert.False(probe.Measurements.IsValid);
        Assert.Empty(probe.Summary);
    }

    [Fact]
    public void MeasurementFollowsTheWindowRatherThanTheWholeHistory()
    {
        var circuit = new Circuit();
        var scope = new ScopeViewModel(circuit);
        var resistor = circuit.Add(new Cirq.Components.Passive.Resistor(1e3));
        var probe = scope.AddProbe(resistor.A, "Sig");

        // Quiet, then loud.
        for (var t = 0.0; t <= 10e-3; t += 1e-6) probe.Record(t, 0.0);
        for (var t = 10e-3; t <= 20e-3; t += 1e-6)
            probe.Record(t, 3.0 * Math.Sin(2 * Math.PI * 1e3 * t));

        scope.Measure(0, 9e-3);
        Assert.Equal(0.0, probe.Measurements.PeakToPeak, 6);

        scope.Measure(11e-3, 8e-3);
        Assert.Equal(6.0, probe.Measurements.PeakToPeak, 1);
    }

    // ---- cursors -----------------------------------------------------------

    [Fact]
    public void TurningCursorsOnPlacesThemAcrossTheWindow()
    {
        var (scope, _) = Rig();

        scope.ShowCursors = true;
        scope.Measure(0, 30e-3);

        Assert.Equal(10e-3, scope.CursorA, 6);
        Assert.Equal(20e-3, scope.CursorB, 6);
        Assert.NotEmpty(scope.CursorReadout);
    }

    [Fact]
    public void TheCursorsReportTheGapAndItsReciprocal()
    {
        var (scope, _) = Rig();

        scope.ShowCursors = true;
        scope.Measure(0, 30e-3);

        // Straddle exactly one cycle of the 1 kHz sine. 1/Δt should then read 1 kHz, which is what
        // anybody actually uses cursors for.
        scope.CursorA = 2e-3;
        scope.CursorB = 3e-3;
        scope.Measure(0, 30e-3);

        // Through the same formatter the readout uses, so this asserts the value rather than the
        // house style for writing it — SiPrefix has its own tests for the latter.
        Assert.Contains($"Δt {SiPrefix.Format(1e-3, "s")}", scope.CursorReadout);
        Assert.Contains($"1/Δt {SiPrefix.Format(1e3, "Hz")}", scope.CursorReadout);
    }

    [Fact]
    public void TheCursorsReportWhatEachTraceDidBetweenThem()
    {
        var (scope, probe) = Rig();

        scope.ShowCursors = true;
        scope.Measure(0, 30e-3);

        // A quarter cycle apart, from a zero crossing to the positive peak: the trace moves by
        // the full amplitude, which is 2 V.
        scope.CursorA = 2.0e-3;
        scope.CursorB = 2.25e-3;
        scope.Measure(0, 30e-3);

        Assert.Contains($"{probe.Label} Δ {SiPrefix.Format(2.0, "V")}", scope.CursorReadout);
    }

    [Fact]
    public void TurningCursorsOffClearsTheReadout()
    {
        var (scope, _) = Rig();

        scope.ShowCursors = true;
        scope.Measure(0, 30e-3);
        Assert.NotEmpty(scope.CursorReadout);

        scope.ShowCursors = false;
        scope.Measure(0, 30e-3);
        Assert.Empty(scope.CursorReadout);
    }

    /// <summary>
    /// A cursor lands between samples far more often than on one, and snapping to the nearest
    /// would make the reading jump as the trace scrolls underneath it.
    /// </summary>
    [Fact]
    public void AValueBetweenTwoSamplesIsInterpolated()
    {
        DataPoint[] samples = [new(0, 0), new(1, 10)];

        Assert.Equal(2.5, ScopeViewModel.ValueAt(samples, 0.25)!.Value, 9);
        Assert.Equal(0.0, ScopeViewModel.ValueAt(samples, 0)!.Value, 9);
        Assert.Equal(10.0, ScopeViewModel.ValueAt(samples, 1)!.Value, 9);

        // Outside the samples there is no answer, rather than the nearest end pretending to be one.
        Assert.Null(ScopeViewModel.ValueAt(samples, -0.5));
        Assert.Null(ScopeViewModel.ValueAt(samples, 1.5));
        Assert.Null(ScopeViewModel.ValueAt([], 0.5));
    }
}
