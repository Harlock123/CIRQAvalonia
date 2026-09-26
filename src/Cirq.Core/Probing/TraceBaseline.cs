using Cirq.Core.Primitives;
using Cirq.Core.Units;

namespace Cirq.Core.Probing;

/// <summary>What one trace was doing when the baseline was taken.</summary>
/// <param name="Label">The probe's name, which is how it is matched on the way back.</param>
/// <param name="Unit">What it was measured in.</param>
/// <param name="Samples">
/// The shape, thinned to at most <see cref="TraceBaseline.ShapePoints"/> points. Enough to tell
/// whether the waveform moved and nowhere near enough to be the waveform.
/// </param>
public sealed record BaselineTrace(string Label, string Unit, IReadOnlyList<DataPoint> Samples)
{
    /// <summary>What it measured, worked out when it was stored rather than on the way back.</summary>
    public TraceMeasurements Measurements { get; init; } = TraceMeasurements.OfAll(Samples);

    /// <summary>How long the capture ran for.</summary>
    public double Seconds => Samples.Count == 0 ? 0 : Samples[^1].Time - Samples[0].Time;
}

/// <summary>
/// What the circuit produced once, kept so that what it produces next can be held against it.
/// <para>
/// Requirements assert the handful of things somebody thought to assert. This is the rest: the
/// whole answer, recorded, so that a change three chapters away from what you edited shows up as a
/// change rather than as something noticed six weeks later. It is the difference between a suite of
/// tests and a screenshot of the last run, and it is the thing that makes every other analysis here
/// safe to build on.
/// </para>
/// <para>
/// It stores the <b>shape</b> and the <b>measurements</b> rather than the samples. A capture is ten
/// thousand points per probe and a circuit file is a text file somebody may want to read; two
/// hundred and fifty-six points is plenty to see that a waveform moved, and the measurements — which
/// are what anybody would compare by eye anyway — are twelve numbers.
/// </para>
/// </summary>
/// <param name="Taken">When it was recorded.</param>
/// <param name="Note">Whatever the person who took it wanted to say about it.</param>
/// <param name="Traces">One per probe that was recording.</param>
public sealed record TraceBaseline(
    DateTimeOffset Taken,
    string Note,
    IReadOnlyList<BaselineTrace> Traces)
{
    /// <summary>
    /// How many points of each trace are kept.
    /// <para>
    /// Chosen to be legible rather than faithful. The question a baseline answers is "did this
    /// move", and a curve at this resolution answers it for anything that moved by an amount worth
    /// noticing. A baseline that kept every sample would make a saved circuit mostly waveform, and
    /// the thing it would buy — catching a change in a single sample of ten thousand — is a change
    /// the solver's own step size could have invented.
    /// </para>
    /// </summary>
    public const int ShapePoints = 256;

    public static TraceBaseline Empty { get; } = new(DateTimeOffset.MinValue, string.Empty, []);

    public bool IsEmpty => Traces.Count == 0;

    /// <summary>Records what a set of traces is doing now.</summary>
    public static TraceBaseline From(
        IEnumerable<(string Label, string Unit, IReadOnlyList<DataPoint> Samples)> traces,
        string note = "")
    {
        ArgumentNullException.ThrowIfNull(traces);

        List<BaselineTrace> kept = [];

        foreach (var (label, unit, samples) in traces)
        {
            if (samples.Count < 2) continue;

            // Measured from every sample and then thinned, in that order. Measuring the thinned
            // curve would give a rise time quantised to the thinning, which is a measurement of
            // this class rather than of the circuit.
            kept.Add(new BaselineTrace(label, unit, Thin(samples))
            {
                Measurements = TraceMeasurements.OfAll(samples),
            });
        }

        return new TraceBaseline(DateTimeOffset.Now, note, kept);
    }

    /// <summary>Evenly spaced samples through a capture, keeping both ends.</summary>
    private static List<DataPoint> Thin(IReadOnlyList<DataPoint> samples)
    {
        if (samples.Count <= ShapePoints) return [.. samples];

        List<DataPoint> kept = new(ShapePoints);

        for (var i = 0; i < ShapePoints; i++)
            kept.Add(samples[(int)((long)i * (samples.Count - 1) / (ShapePoints - 1))]);

        return kept;
    }
}

/// <summary>One measurement that moved between the baseline and now.</summary>
/// <param name="Quantity">What moved, in words.</param>
/// <param name="Was">What it was.</param>
/// <param name="Now">What it is.</param>
public sealed record MeasurementChange(string Quantity, double? Was, double? Now)
{
    /// <summary>
    /// The change as a fraction of what it was, or NaN when it cannot be one — a figure that
    /// appeared, disappeared, or was zero to begin with.
    /// </summary>
    public double Fraction =>
        Was is { } was && Now is { } now && Math.Abs(was) > 1e-30
            ? (now - was) / Math.Abs(was)
            : double.NaN;

    /// <summary>True when the figure was there before and is not now, or the other way round.</summary>
    public bool Appeared => Was is null && Now is not null;

    public bool Disappeared => Was is not null && Now is null;
}

/// <summary>How one trace compares with how it was.</summary>
/// <param name="Label">The probe.</param>
/// <param name="Unit">What it is measured in.</param>
/// <param name="Changes">The measurements that moved by more than the threshold.</param>
/// <param name="WorstDeviation">
/// The largest difference between the two shapes at the same moment, in the trace's own units, or
/// null when the two cannot be laid over each other at all.
/// </param>
/// <param name="Missing">True when the baseline had this trace and the circuit no longer does.</param>
/// <param name="Added">True when the circuit has it and the baseline did not.</param>
public sealed record TraceComparison(
    string Label,
    string Unit,
    IReadOnlyList<MeasurementChange> Changes,
    double? WorstDeviation,
    bool Missing = false,
    bool Added = false)
{
    /// <summary>True when nothing about this trace moved.</summary>
    public bool IsUnchanged => !Missing && !Added && Changes.Count == 0;

    /// <summary>The worst deviation as a fraction of how big the trace is, or NaN.</summary>
    public double RelativeDeviation { get; init; } = double.NaN;
}

/// <summary>What changed since the baseline was taken.</summary>
/// <param name="Baseline">When the baseline was recorded, and what was said about it.</param>
/// <param name="Traces">One per trace, on either side.</param>
public sealed record BaselineComparison(TraceBaseline Baseline, IReadOnlyList<TraceComparison> Traces)
{
    public static BaselineComparison None { get; } = new(TraceBaseline.Empty, []);

    /// <summary>The traces that moved, appeared or went away.</summary>
    public IEnumerable<TraceComparison> Moved => Traces.Where(t => !t.IsUnchanged);

    public bool IsUnchanged => Traces.All(t => t.IsUnchanged);

    /// <summary>The whole thing in a sentence, which is what somebody reads first.</summary>
    public string Summary()
    {
        if (Baseline.IsEmpty) return "No baseline recorded yet.";

        if (Traces.Count == 0) return "Nothing is recording, so there is nothing to compare.";

        var moved = Moved.ToList();

        if (moved.Count == 0)
            return $"Nothing moved. {Traces.Count} trace{(Traces.Count == 1 ? "" : "s")} match the " +
                   $"baseline taken {Age()}.";

        var worst = moved
            .Where(t => !double.IsNaN(t.RelativeDeviation))
            .OrderByDescending(t => t.RelativeDeviation)
            .FirstOrDefault();

        var names = string.Join(", ", moved.Take(3).Select(t => t.Label));
        var rest = moved.Count > 3 ? $" and {moved.Count - 3} more" : string.Empty;

        return worst is null
            ? $"{names}{rest} changed since the baseline taken {Age()}."
            : $"{names}{rest} changed since the baseline taken {Age()} — worst is {worst.Label}, " +
              $"off by {SiPrefix.Format(worst.WorstDeviation ?? 0, worst.Unit)} " +
              $"({worst.RelativeDeviation * 100:0.#}% of its swing).";
    }

    private string Age()
    {
        var span = DateTimeOffset.Now - Baseline.Taken;

        return span.TotalMinutes < 1 ? "a moment ago"
            : span.TotalHours < 1 ? $"{span.TotalMinutes:0} minutes ago"
            : span.TotalDays < 1 ? $"{span.TotalHours:0} hours ago"
            : $"{span.TotalDays:0} days ago";
    }
}

/// <summary>
/// Holds a set of traces against a baseline and says what moved.
/// <para>
/// The threshold is the whole design problem. Compared exactly, every run differs from every other:
/// the solver picks its own time steps, and a step landing a nanosecond elsewhere moves a measured
/// rise time in the last few digits. A comparison that reported that would report it every time,
/// and a report that is always non-empty is a report nobody reads. So a change has to be big enough
/// to be a change in the circuit rather than in the arithmetic.
/// </para>
/// </summary>
public static class BaselineCheck
{
    /// <summary>
    /// How far a measurement has to move to count, as a fraction of what it was. One percent is
    /// comfortably above what re-running the same circuit produces and comfortably below anything a
    /// person would call the same answer.
    /// </summary>
    public const double Threshold = 0.01;

    /// <summary>Compares what is recording now against what was recorded then.</summary>
    public static BaselineComparison Against(
        TraceBaseline baseline,
        IEnumerable<(string Label, string Unit, IReadOnlyList<DataPoint> Samples)> traces)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(traces);

        var now = TraceBaseline.From(traces);

        List<TraceComparison> comparisons = [];

        foreach (var was in baseline.Traces)
        {
            var current = now.Traces.FirstOrDefault(t => t.Label == was.Label);

            if (current is null)
            {
                comparisons.Add(new TraceComparison(was.Label, was.Unit, [], null, Missing: true));
                continue;
            }

            comparisons.Add(Compare(was, current));
        }

        // A trace the baseline never saw is a change too — a probe added since is a probe nothing
        // has ever been checked against.
        foreach (var current in now.Traces)
            if (!baseline.Traces.Any(t => t.Label == current.Label))
                comparisons.Add(new TraceComparison(current.Label, current.Unit, [], null, Added: true));

        return new BaselineComparison(baseline, comparisons);
    }

    private static TraceComparison Compare(BaselineTrace was, BaselineTrace now)
    {
        List<MeasurementChange> changes = [];

        var swing = Math.Max(was.Measurements.PeakToPeak, now.Measurements.PeakToPeak);

        foreach (var (name, before, after, scaling) in Quantities(was.Measurements, now.Measurements))
        {
            if (before is null && after is null) continue;

            if (before is null || after is null)
            {
                changes.Add(new MeasurementChange(name, before, after));
                continue;
            }

            var scale = scaling switch
            {
                // Against the trace's own swing, which is what makes a millivolt of extra ripple on
                // a five volt swing read as a small change. A trace that does not move has no swing
                // to measure against, and dividing by it would leave a rail that fell from 5 V to
                // 3.3 V reported as unchanged — so a flat trace is judged against its own level
                // instead. That is the commonest thing anybody baselines: a supply.
                Scaling.Swing => swing > 1e-30
                    ? swing
                    : Math.Max(Math.Abs(before.Value), Math.Abs(after.Value)),

                Scaling.Fraction => 1.0,

                // The larger of the two, so a figure that fell to nearly nothing counts as having
                // moved rather than dividing by something close to zero.
                _ => Math.Max(Math.Abs(before.Value), Math.Abs(after.Value)),
            };

            if (scale < 1e-30) continue;

            if (Math.Abs(after.Value - before.Value) / scale > Threshold)
                changes.Add(new MeasurementChange(name, before, after));
        }

        var (deviation, relative) = Deviation(was, now);

        return new TraceComparison(was.Label, was.Unit, changes, deviation)
        {
            RelativeDeviation = relative,
        };
    }

    /// <summary>
    /// The largest gap between the two shapes, and what fraction of the trace's own swing that is.
    /// <para>
    /// Sampled at the baseline's own times and interpolated on the new curve, because the two were
    /// taken with different step sizes and comparing them point for point would compare a sample at
    /// one moment with a sample at another. The relative figure is the one worth reading: half a
    /// volt out on a five volt swing is a different statement from half a volt out on five
    /// millivolts.
    /// </para>
    /// </summary>
    private static (double? Worst, double Relative) Deviation(BaselineTrace was, BaselineTrace now)
    {
        if (was.Samples.Count < 2 || now.Samples.Count < 2) return (null, double.NaN);

        var worst = 0.0;

        foreach (var point in was.Samples)
        {
            var here = ValueAt(now.Samples, point.Time);
            if (here is null) continue;

            worst = Math.Max(worst, Math.Abs(here.Value - point.Value));
        }

        var swing = Math.Max(was.Measurements.PeakToPeak, now.Measurements.PeakToPeak);

        // The same fallback as above, and for the same reason: "half a volt out" means something
        // different on a five volt swing and on a rail that is supposed to sit still, but it means
        // nothing at all as a fraction of zero.
        if (swing <= 1e-30)
            swing = Math.Max(Math.Abs(was.Measurements.Mean), Math.Abs(now.Measurements.Mean));

        return (worst, swing > 1e-30 ? worst / swing : double.NaN);
    }

    /// <summary>The curve's value at a moment, or null when that moment is outside it.</summary>
    private static double? ValueAt(IReadOnlyList<DataPoint> samples, double time)
    {
        if (time < samples[0].Time || time > samples[^1].Time) return null;

        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i].Time < time) continue;

            var span = samples[i].Time - samples[i - 1].Time;

            if (span <= 0) return samples[i].Value;

            var t = (time - samples[i - 1].Time) / span;

            return samples[i - 1].Value + (t * (samples[i].Value - samples[i - 1].Value));
        }

        return samples[^1].Value;
    }

    /// <summary>
    /// What a measurement's change should be judged against.
    /// <para>
    /// Not every quantity can be compared with itself, and the mean is the one that proves it. The
    /// mean of a symmetric waveform is numerically zero — a few times ten to the minus seventeen,
    /// whatever the last bit of the arithmetic happened to land on — so two runs of the <i>same</i>
    /// circuit differ by several percent of it, and a comparison that measured the mean against the
    /// mean reported a change every single time. Against the waveform's swing, which is what it is
    /// a mean <i>of</i>, the same difference is one part in ten to the sixteenth.
    /// </para>
    /// </summary>
    private enum Scaling
    {
        /// <summary>Judged against the quantity's own size: frequencies and times span decades.</summary>
        Own,

        /// <summary>Judged against how big the waveform is, for anything in the trace's units.</summary>
        Swing,

        /// <summary>Judged against one, for the quantities that are already fractions.</summary>
        Fraction,
    }

    /// <summary>Every measurement, paired between the two sides, in the words a person would use.</summary>
    private static IEnumerable<(string Name, double? Before, double? After, Scaling Scale)> Quantities(
        TraceMeasurements was, TraceMeasurements now)
    {
        yield return ("minimum", was.Minimum, now.Minimum, Scaling.Swing);
        yield return ("maximum", was.Maximum, now.Maximum, Scaling.Swing);
        yield return ("mean", was.Mean, now.Mean, Scaling.Swing);
        yield return ("RMS", was.Rms, now.Rms, Scaling.Swing);
        yield return ("peak to peak", was.PeakToPeak, now.PeakToPeak, Scaling.Swing);
        yield return ("frequency", was.Frequency, now.Frequency, Scaling.Own);
        yield return ("duty cycle", was.DutyCycle, now.DutyCycle, Scaling.Fraction);
        yield return ("rise time", was.RiseTime, now.RiseTime, Scaling.Own);
        yield return ("fall time", was.FallTime, now.FallTime, Scaling.Own);
        yield return ("overshoot", was.Overshoot, now.Overshoot, Scaling.Fraction);
        yield return ("settling time", was.SettlingTime, now.SettlingTime, Scaling.Own);
        yield return ("pulse width", was.PulseWidth, now.PulseWidth, Scaling.Own);
    }
}
