using Cirq.Components.Passive;
using Cirq.Core.Topology;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>Plotting one trace against another instead of against time.</summary>
public class XyModeTests
{
    private static (ScopeViewModel Scope, SignalProbeSet Probes) Rig(
        Func<double, double> x, Func<double, double> y, double seconds = 4e-3, double step = 1e-6)
    {
        var circuit = new Circuit();
        var scope = new ScopeViewModel(circuit);

        var a = circuit.Add(new Resistor(1e3));
        var b = circuit.Add(new Resistor(2e3));

        var horizontal = scope.AddProbe(a.A, "X");
        var vertical = scope.AddProbe(b.A, "Y");

        for (var t = 0.0; t <= seconds; t += step)
        {
            horizontal.Record(t, x(t));
            vertical.Record(t, y(t));
        }

        return (scope, new SignalProbeSet(horizontal, vertical));
    }

    private sealed record SignalProbeSet(
        Cirq.Core.Probing.SignalProbe X, Cirq.Core.Probing.SignalProbe Y);

    [Fact]
    public void TheFirstVisibleTraceIsTheHorizontalAxisUnlessOneIsChosen()
    {
        var (scope, probes) = Rig(t => t, t => t);

        var (horizontal, vertical) = scope.XyPairs();

        Assert.Same(probes.X, horizontal);
        Assert.Same(probes.Y, Assert.Single(vertical));

        // Choosing the other one swaps them round.
        scope.XyHorizontal = probes.Y;

        (horizontal, vertical) = scope.XyPairs();

        Assert.Same(probes.Y, horizontal);
        Assert.Same(probes.X, Assert.Single(vertical));
    }

    [Fact]
    public void AHiddenTraceIsNeitherAxisNorACurve()
    {
        var (scope, probes) = Rig(t => t, t => t);

        probes.Y.IsVisible = false;

        var (horizontal, vertical) = scope.XyPairs();

        Assert.Same(probes.X, horizontal);
        Assert.Empty(vertical);
    }

    /// <summary>
    /// A straight line through the origin: y = 2x should come out as exactly that, whatever the
    /// two traces were doing in time.
    /// </summary>
    [Fact]
    public void TheCurveIsOneTraceAgainstTheOther()
    {
        var (scope, probes) = Rig(
            t => Math.Sin(2 * Math.PI * 1e3 * t),
            t => 2.0 * Math.Sin(2 * Math.PI * 1e3 * t));

        var (x, y) = ScopeViewModel.XySeries(probes.X, probes.Y, 0, 4e-3);

        Assert.True(x.Length > 100);
        Assert.Equal(x.Length, y.Length);

        for (var i = 0; i < x.Length; i++)
            Assert.Equal(2.0 * x[i], y[i], 6);
    }

    /// <summary>
    /// Two sines a quarter cycle apart draw a circle — the Lissajous figure that is how phase was
    /// measured before anything had a phase meter.
    /// </summary>
    [Fact]
    public void AQuarterCycleOfPhaseDrawsACircle()
    {
        var (_, probes) = Rig(
            t => Math.Sin(2 * Math.PI * 1e3 * t),
            t => Math.Sin((2 * Math.PI * 1e3 * t) + (Math.PI / 2)));

        var (x, y) = ScopeViewModel.XySeries(probes.X, probes.Y, 0, 4e-3);

        // Every point is on the unit circle, and the figure reaches all the way round.
        for (var i = 0; i < x.Length; i++)
            Assert.Equal(1.0, (x[i] * x[i]) + (y[i] * y[i]), 0.02);

        Assert.True(x.Max() > 0.95 && x.Min() < -0.95);
        Assert.True(y.Max() > 0.95 && y.Min() < -0.95);
    }

    /// <summary>
    /// In phase, the same two traces collapse to a straight line — which is exactly how the
    /// technique is used: turn something until the ellipse closes.
    /// </summary>
    [Fact]
    public void InPhaseTheSameFigureCollapsesToALine()
    {
        var (_, probes) = Rig(
            t => Math.Sin(2 * Math.PI * 1e3 * t),
            t => Math.Sin(2 * Math.PI * 1e3 * t));

        var (x, y) = ScopeViewModel.XySeries(probes.X, probes.Y, 0, 4e-3);

        for (var i = 0; i < x.Length; i++) Assert.Equal(x[i], y[i], 6);
    }

    /// <summary>
    /// The pairing is by time, not by position in the buffers — a probe added later, or one whose
    /// kind was changed and history cleared, is shorter than its neighbour.
    /// </summary>
    [Fact]
    public void TracesOfDifferentLengthsArePairedByTimeRatherThanByIndex()
    {
        var circuit = new Circuit();
        var scope = new ScopeViewModel(circuit);
        var r = circuit.Add(new Resistor(1e3));

        var horizontal = scope.AddProbe(r.A, "X");
        var vertical = scope.AddProbe(r.B, "Y");

        // The horizontal trace is finely sampled; the vertical one coarsely, and starting later.
        for (var t = 0.0; t <= 1e-3; t += 1e-6) horizontal.Record(t, t * 1000);
        for (var t = 2e-4; t <= 1e-3; t += 1e-5) vertical.Record(t, t * 2000);

        var (x, y) = ScopeViewModel.XySeries(horizontal, vertical, 0, 1e-3);

        Assert.True(x.Length > 10);

        // y = 2x wherever both existed, interpolated across the coarse trace's gaps.
        for (var i = 0; i < x.Length; i++) Assert.Equal(2.0 * x[i], y[i], 3);
    }

    [Fact]
    public void ATraceWithNothingRecordedGivesNoCurveRatherThanThrowing()
    {
        var circuit = new Circuit();
        var scope = new ScopeViewModel(circuit);
        var r = circuit.Add(new Resistor(1e3));

        var horizontal = scope.AddProbe(r.A, "X");
        var vertical = scope.AddProbe(r.B, "Y");

        var (x, y) = ScopeViewModel.XySeries(horizontal, vertical, 0, 1e-3);

        Assert.Empty(x);
        Assert.Empty(y);
    }

    [Fact]
    public void SwitchingToXyIsAnnouncedSoTheAxisPickerCanAppear()
    {
        var (scope, _) = Rig(t => t, t => t);

        var raised = 0;
        scope.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ScopeViewModel.IsXy)) raised++; };

        Assert.False(scope.IsXy);

        scope.Layout = ScopeLayout.Xy;

        Assert.True(scope.IsXy);
        Assert.Equal(1, raised);
    }
}
