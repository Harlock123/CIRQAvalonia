using Cirq.Core.Primitives;
using Cirq.Core.Probing;

namespace Cirq.Engine.Tests;

/// <summary>
/// What the circuit produced once, held against what it produces now.
/// <para>
/// The property that matters most is the one that is easiest to get wrong in the flattering
/// direction: a comparison that reports nothing whatever you do is a comparison nobody will notice
/// is broken. So the tests here pair every "this did not move" with a "and this did".
/// </para>
/// </summary>
public class BaselineTests
{
    private static List<DataPoint> Sine(double amplitude = 1.0, double hertz = 1000.0, int count = 2000) =>
        [.. Enumerable.Range(0, count).Select(i =>
        {
            var t = i / (hertz * 20.0);
            return new DataPoint(t, amplitude * Math.Sin(2 * Math.PI * hertz * t));
        })];

    private static (string, string, IReadOnlyList<DataPoint>)[] One(
        string label, IReadOnlyList<DataPoint> samples) => [(label, "V", samples)];

    /// <summary>A circuit compared against itself has not moved.</summary>
    [Fact]
    public void TheSameRunMatchesItself()
    {
        var traces = One("out", Sine());

        var baseline = TraceBaseline.From(traces);

        var comparison = BaselineCheck.Against(baseline, traces);

        Assert.True(comparison.IsUnchanged);
        Assert.Contains("Nothing moved", comparison.Summary(), StringComparison.Ordinal);
    }

    /// <summary>
    /// And a run that did move says which measurements moved and by how much. Without this the test
    /// above passes just as happily on a comparison that reports nothing at all.
    /// </summary>
    [Fact]
    public void AChangedRunSaysWhatChanged()
    {
        var baseline = TraceBaseline.From(One("out", Sine(amplitude: 1.0)));

        var comparison = BaselineCheck.Against(baseline, One("out", Sine(amplitude: 1.5)));

        Assert.False(comparison.IsUnchanged);

        var trace = Assert.Single(comparison.Traces);

        Assert.Contains(trace.Changes, c => c.Quantity == "peak to peak");
        Assert.Contains(trace.Changes, c => c.Quantity == "RMS");

        // Half as big again, so the peak to peak moved by half.
        var peak = trace.Changes.Single(c => c.Quantity == "peak to peak");

        Assert.Equal(0.5, peak.Fraction, 0.02);
    }

    /// <summary>
    /// A change below the threshold is not reported. Every run differs from every other in the last
    /// few digits — the solver picks its own steps — and a report that is never empty is a report
    /// nobody reads.
    /// </summary>
    [Fact]
    public void AChangeTooSmallToMeanAnythingIsNotReported()
    {
        var baseline = TraceBaseline.From(One("out", Sine(amplitude: 1.0)));

        // A tenth of the threshold.
        var comparison = BaselineCheck.Against(
            baseline, One("out", Sine(amplitude: 1.0 + (BaselineCheck.Threshold / 10))));

        Assert.True(comparison.IsUnchanged);
    }

    /// <summary>And a change just over it is.</summary>
    [Fact]
    public void AChangeJustOverTheThresholdIs()
    {
        var baseline = TraceBaseline.From(One("out", Sine(amplitude: 1.0)));

        var comparison = BaselineCheck.Against(
            baseline, One("out", Sine(amplitude: 1.0 + (BaselineCheck.Threshold * 3))));

        Assert.False(comparison.IsUnchanged);
    }

    /// <summary>
    /// The shapes are compared as well as the measurements, because two waveforms can measure the
    /// same and be entirely different: a sine and a triangle of the same peak to peak have the same
    /// minimum, maximum and frequency, and only the shape tells them apart.
    /// </summary>
    [Fact]
    public void TheShapeIsComparedAndNotOnlyTheNumbers()
    {
        const double hertz = 1000.0;
        const int count = 2000;

        List<DataPoint> triangle = [.. Enumerable.Range(0, count).Select(i =>
        {
            var t = i / (hertz * 20.0);
            var phase = (t * hertz) - Math.Floor(t * hertz);

            return new DataPoint(t, phase < 0.5 ? (phase * 4) - 1 : 3 - (phase * 4));
        })];

        var baseline = TraceBaseline.From(One("out", Sine()));

        var comparison = BaselineCheck.Against(baseline, One("out", triangle));

        var trace = Assert.Single(comparison.Traces);

        Assert.NotNull(trace.WorstDeviation);
        Assert.True(trace.WorstDeviation > 0.1,
            $"a sine and a triangle differ by more than {trace.WorstDeviation}");
    }

    /// <summary>A probe the baseline never saw is a change too, and says which kind.</summary>
    [Fact]
    public void AddedAndMissingTracesAreBothReported()
    {
        var baseline = TraceBaseline.From([
            ("a", "V", Sine()),
            ("b", "V", Sine()),
        ]);

        var comparison = BaselineCheck.Against(baseline, [
            ("b", "V", (IReadOnlyList<DataPoint>)Sine()),
            ("c", "V", Sine()),
        ]);

        Assert.True(comparison.Traces.Single(t => t.Label == "a").Missing);
        Assert.True(comparison.Traces.Single(t => t.Label == "c").Added);
        Assert.True(comparison.Traces.Single(t => t.Label == "b").IsUnchanged);
    }

    /// <summary>
    /// A measurement that appeared, or went away, is reported as that rather than as a percentage.
    /// There is no percentage to be had between a number and nothing, and inventing one is the kind
    /// of thing a report does once before nobody believes it again.
    /// </summary>
    [Fact]
    public void AMeasurementThatAppearedIsNotAPercentage()
    {
        // Flat: no crossings, so no frequency.
        List<DataPoint> flat = [.. Enumerable.Range(0, 1000).Select(i => new DataPoint(i * 1e-5, 1.0))];

        var baseline = TraceBaseline.From(One("out", flat));

        var comparison = BaselineCheck.Against(baseline, One("out", Sine()));

        var change = comparison.Traces.Single().Changes.SingleOrDefault(c => c.Quantity == "frequency");

        Assert.NotNull(change);
        Assert.True(change.Appeared);
        Assert.True(double.IsNaN(change.Fraction));
    }

    /// <summary>
    /// A baseline keeps a manageable number of points, and keeps both ends of the capture. It is
    /// stored in a file somebody may open in an editor, and ten thousand points per probe would
    /// make that file mostly waveform.
    /// </summary>
    [Fact]
    public void ABaselineIsThinnedButKeepsItsEnds()
    {
        var samples = Sine(count: 10_000);

        var trace = Assert.Single(TraceBaseline.From(One("out", samples)).Traces);

        Assert.True(trace.Samples.Count <= TraceBaseline.ShapePoints,
            $"{trace.Samples.Count} points is more than the limit");

        Assert.Equal(samples[0].Time, trace.Samples[0].Time, 1e-12);
        Assert.Equal(samples[^1].Time, trace.Samples[^1].Time, 1e-12);
    }

    /// <summary>
    /// The measurements are taken from every sample and only then is the shape thinned. Measuring
    /// the thinned curve would give a rise time quantised to the thinning, which is a measurement
    /// of the thinning rather than of the circuit.
    /// </summary>
    [Fact]
    public void MeasurementsAreTakenBeforeTheThinning()
    {
        const double tau = 1e-5;

        List<DataPoint> step = [.. Enumerable.Range(0, 20_000)
            .Select(i => i * 1e-8)
            .Select(t => new DataPoint(t, 1.0 - Math.Exp(-t / tau)))];

        var trace = Assert.Single(TraceBaseline.From(One("out", step)).Traces);

        // ln(9) time constants, to the accuracy of the full capture rather than of 256 points.
        Assert.Equal(Math.Log(9.0) * tau, trace.Measurements.RiseTime!.Value, tau * 0.02);

        // And the thinned shape on its own could not have produced that: its samples are nearly a
        // microsecond apart, which is the whole edge.
        Assert.True(TraceMeasurements.OfAll(trace.Samples).RiseTime is null or > (tau * 1.5),
            "the thinned curve is too coarse to measure this edge, which is why it is not used to");
    }

    /// <summary>Nothing recorded says so, rather than reporting that nothing changed.</summary>
    [Fact]
    public void NoBaselineIsNotTheSameAsNothingChanged()
    {
        var comparison = BaselineCheck.Against(TraceBaseline.Empty, One("out", Sine()));

        Assert.Contains("No baseline", comparison.Summary(), StringComparison.Ordinal);
    }

    /// <summary>The summary names the trace that moved furthest, because that is where to look.</summary>
    [Fact]
    public void TheSummaryNamesTheWorstTrace()
    {
        var baseline = TraceBaseline.From([
            ("small", "V", Sine(amplitude: 1.0)),
            ("big", "V", Sine(amplitude: 1.0)),
        ]);

        var comparison = BaselineCheck.Against(baseline, [
            ("small", "V", (IReadOnlyList<DataPoint>)Sine(amplitude: 1.02)),
            ("big", "V", Sine(amplitude: 3.0)),
        ]);

        Assert.Contains("worst is big", comparison.Summary(), StringComparison.Ordinal);
    }
}
