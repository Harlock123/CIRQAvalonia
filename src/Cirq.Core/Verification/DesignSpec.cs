using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Core.Verification;

/// <summary>Which number off a trace a specification is about.</summary>
public enum SpecQuantity
{
    /// <summary>The lowest the trace goes — a rail's droop, a swing's negative peak.</summary>
    Minimum,

    /// <summary>The highest it goes, which is usually the one a part has to survive.</summary>
    Maximum,

    /// <summary>The average, which is what a DC meter reads.</summary>
    Mean,

    /// <summary>Root mean square, which is what an AC meter reads.</summary>
    Rms,

    /// <summary>Peak to peak — ripple, swing, the number people mean by "how big is it".</summary>
    PeakToPeak,

    /// <summary>Hertz.</summary>
    Frequency,

    /// <summary>Fraction of a cycle spent high.</summary>
    DutyCycle,

    /// <summary>Ten to ninety percent, in seconds.</summary>
    RiseTime,

    /// <summary>Ninety to ten percent, in seconds — the other half of an edge specification.</summary>
    FallTime,

    /// <summary>
    /// How far past its settled value a step went, as a fraction. 0.1 is ten percent overshoot,
    /// which is most of what a control loop is judged on.
    /// </summary>
    Overshoot,

    /// <summary>
    /// How long a step took to stay within two percent of where it ended up, in seconds. The other
    /// half of what a control loop is judged on, and the one a fast loop wins on.
    /// </summary>
    SettlingTime,

    /// <summary>
    /// How long the first pulse stayed high, in seconds. Not the same question as the duty cycle:
    /// the same duty at twice the frequency is half the pulse, and a reset line is specified in
    /// microseconds rather than in percent.
    /// </summary>
    PulseWidth,

    /// <summary>Volts per second on the fastest edge — what an op-amp is sold on.</summary>
    SlewRate,

    /// <summary>
    /// Seconds from an edge on <see cref="DesignSpec.Against"/> to the next edge on
    /// <see cref="DesignSpec.Trace"/> — the number on the front of every logic datasheet.
    /// </summary>
    PropagationDelay,

    /// <summary>
    /// The worst gap between two traces that are supposed to move together, in seconds. What a
    /// clock distribution is judged on.
    /// </summary>
    Skew,

    /// <summary>
    /// How long the trace was already stable before the clock edge on <see cref="DesignSpec.Against"/>.
    /// The tightest one that occurred, because a setup time is a minimum a part demands.
    /// </summary>
    SetupTime,

    /// <summary>And how long it stayed stable after that edge.</summary>
    HoldTime,
}

/// <summary>Which quantities need a second trace to mean anything.</summary>
public static class SpecPairs
{
    public static bool NeedsTwo(SpecQuantity quantity) => quantity
        is SpecQuantity.PropagationDelay
        or SpecQuantity.Skew
        or SpecQuantity.SetupTime
        or SpecQuantity.HoldTime;
}

/// <summary>
/// The words for each measurement, in one place.
/// <para>
/// A requirement is read far more often than it is written, and it is read in sentences — "rail
/// peak to peak at most 50 mV". Spelling that off the enum gives "peaktopeak", which is the code
/// leaking into the product.
/// </para>
/// </summary>
public static class SpecWords
{
    public static string Of(SpecQuantity quantity) => quantity switch
    {
        SpecQuantity.PeakToPeak => "peak to peak",
        SpecQuantity.Minimum => "minimum",
        SpecQuantity.Maximum => "maximum",
        SpecQuantity.Mean => "mean",
        SpecQuantity.Rms => "RMS",
        SpecQuantity.Frequency => "frequency",
        SpecQuantity.DutyCycle => "duty cycle",
        SpecQuantity.RiseTime => "rise time",
        SpecQuantity.FallTime => "fall time",
        SpecQuantity.Overshoot => "overshoot",
        SpecQuantity.SettlingTime => "settling time",
        SpecQuantity.PulseWidth => "pulse width",
        SpecQuantity.SlewRate => "slew rate",
        SpecQuantity.PropagationDelay => "propagation delay",
        SpecQuantity.Skew => "skew",
        SpecQuantity.SetupTime => "setup time",
        SpecQuantity.HoldTime => "hold time",
        _ => quantity.ToString(),
    };

    public static string Of(SpecComparison comparison) => comparison switch
    {
        SpecComparison.AtMost => "at most",
        SpecComparison.AtLeast => "at least",
        _ => "within",
    };
}

/// <summary>How a measured value is held against its limit.</summary>
public enum SpecComparison
{
    /// <summary>The measurement must not exceed the limit — ripple, overshoot, dissipation.</summary>
    AtMost,

    /// <summary>The measurement must reach the limit — swing, margin, efficiency.</summary>
    AtLeast,

    /// <summary>The measurement must land within a tolerance of the limit — a regulated output.</summary>
    Within,
}

/// <summary>
/// One thing a circuit is supposed to do, written down in a form that can be checked.
/// <para>
/// A simulator answers "what does this do". A specification is the other half — "and is that
/// right" — and until it is written down somewhere the answer lives in whoever last looked at the
/// trace. Every analysis here already produces numbers; a spec is a name, one of those numbers,
/// and a limit, so that the question gets asked again every time anything changes rather than
/// only when somebody thinks to look.
/// </para>
/// <para>
/// This is also what makes the sweeps worth more than they are singly. "Ripple under fifty
/// millivolts" checked once is a measurement; checked at every corner, or across four hundred
/// Monte Carlo builds, it is the thing a design is actually sold against.
/// </para>
/// </summary>
public partial class DesignSpec : ObservableObject
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>What this requirement is called, in the words of whoever wrote it down.</summary>
    [ObservableProperty]
    public partial string Name { get; set; } = "Requirement";

    /// <summary>The label of the trace to measure. Matched to a probe by name.</summary>
    [ObservableProperty]
    public partial string Trace { get; set; } = string.Empty;

    /// <summary>
    /// The second trace, for the measurements that need one — the input a delay is timed from, the
    /// clock a setup time is measured against, the other half of a skew. Empty for everything else,
    /// and ignored there.
    /// </summary>
    [ObservableProperty]
    public partial string Against { get; set; } = string.Empty;

    [ObservableProperty]
    public partial SpecQuantity Quantity { get; set; } = SpecQuantity.PeakToPeak;

    [ObservableProperty]
    public partial SpecComparison Comparison { get; set; } = SpecComparison.AtMost;

    /// <summary>The number the measurement is held against, in the trace's own units.</summary>
    [ObservableProperty]
    public partial double Limit { get; set; }

    /// <summary>How far either side of the limit still passes, for <see cref="SpecComparison.Within"/>.</summary>
    [ObservableProperty]
    public partial double Tolerance { get; set; }

    /// <summary>The unit to write the numbers in — V, A, Hz, s. Display only.</summary>
    [ObservableProperty]
    public partial string Unit { get; set; } = "V";

    /// <summary>False to keep a requirement on record without holding the design to it today.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    /// <summary>The requirement read back as a sentence, for a report or a tooltip.</summary>
    public string Describe() => Comparison switch
    {
        SpecComparison.AtMost => $"{Label} at most {Format(Limit)}",
        SpecComparison.AtLeast => $"{Label} at least {Format(Limit)}",
        _ => $"{Label} within {Format(Tolerance)} of {Format(Limit)}",
    };

    private string Label => SpecPairs.NeedsTwo(Quantity) && Against.Length > 0
        ? $"{Trace} {SpecWords.Of(Quantity)} from {Against}"
        : $"{Trace} {SpecWords.Of(Quantity)}";

    internal string Format(double value) => SiPrefix.Format(value, Unit);
}

/// <summary>How one specification came out.</summary>
/// <param name="Spec">What was asked for.</param>
/// <param name="Measured">What the circuit did, or null when it could not be measured.</param>
/// <param name="Passed">True, false, or null when there was nothing to measure.</param>
/// <param name="Explanation">The result in words, including why it could not be measured.</param>
public sealed record SpecResult(DesignSpec Spec, double? Measured, bool? Passed, string Explanation)
{
    /// <summary>
    /// How much room is left, as a fraction of the limit. Positive is inside, negative is outside,
    /// and near zero is a design that only passes because nothing in it has drifted yet.
    /// </summary>
    public double? Margin
    {
        get
        {
            if (Measured is not { } measured) return null;

            var scale = Math.Abs(Spec.Comparison == SpecComparison.Within ? Spec.Tolerance : Spec.Limit);

            if (scale < 1e-30) return null;

            return Spec.Comparison switch
            {
                SpecComparison.AtMost => (Spec.Limit - measured) / scale,
                SpecComparison.AtLeast => (measured - Spec.Limit) / scale,
                _ => (Spec.Tolerance - Math.Abs(measured - Spec.Limit)) / scale,
            };
        }
    }
}

/// <summary>Holds a set of specifications against what the circuit actually did.</summary>
public static class SpecCheck
{
    /// <summary>The value a quantity picks out of a set of measurements, or null when it has none.</summary>
    public static double? Read(SpecQuantity quantity, TraceMeasurements measurements) =>
        !measurements.IsValid
            ? null
            : quantity switch
            {
                SpecQuantity.Minimum => measurements.Minimum,
                SpecQuantity.Maximum => measurements.Maximum,
                SpecQuantity.Mean => measurements.Mean,
                SpecQuantity.Rms => measurements.Rms,
                SpecQuantity.PeakToPeak => measurements.PeakToPeak,
                SpecQuantity.Frequency => measurements.Frequency,
                SpecQuantity.DutyCycle => measurements.DutyCycle,
                SpecQuantity.RiseTime => measurements.RiseTime,
                SpecQuantity.FallTime => measurements.FallTime,
                SpecQuantity.Overshoot => measurements.Overshoot,
                SpecQuantity.SettlingTime => measurements.SettlingTime,
                SpecQuantity.PulseWidth => measurements.PulseWidth,
                SpecQuantity.SlewRate => measurements.SlewRate,
                _ => null,
            };

    /// <summary>Holds one specification against one trace's measurements.</summary>
    public static SpecResult Evaluate(DesignSpec spec, TraceMeasurements? measurements)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (measurements is not { } found)
        {
            return new SpecResult(spec, null, null,
                $"No trace called \"{spec.Trace}\" — probe it, or point the requirement at one " +
                "that is there.");
        }

        if (Read(spec.Quantity, found) is not { } measured)
        {
            // A frequency needs cycles and a rise time needs an edge. Saying so is more use than
            // failing the requirement, because the circuit has not been shown to break it.
            return new SpecResult(spec, null, null,
                $"Nothing to measure yet: {SpecWords.Of(spec.Quantity)} needs more of the " +
                "waveform than the scope has recorded.");
        }

        return Judge(spec, measured);
    }

    /// <summary>The verdict and its wording, once there is a number to hold against the limit.</summary>
    private static SpecResult Judge(DesignSpec spec, double measured)
    {
        var passed = spec.Comparison switch
        {
            SpecComparison.AtMost => measured <= spec.Limit,
            SpecComparison.AtLeast => measured >= spec.Limit,
            _ => Math.Abs(measured - spec.Limit) <= Math.Abs(spec.Tolerance),
        };

        return new SpecResult(spec, measured, passed,
            passed
                ? $"{spec.Describe()} — met, at {spec.Format(measured)}"
                : $"{spec.Describe()} — not met: {spec.Format(measured)}");
    }

    /// <summary>
    /// Holds every enabled specification against whatever the named traces measured. Disabled ones
    /// are left out rather than passed, because a requirement nobody is checking has no result.
    /// </summary>
    public static IReadOnlyList<SpecResult> EvaluateAll(
        IEnumerable<DesignSpec> specs, Func<string, TraceMeasurements?> lookup)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(lookup);

        return [.. specs.Where(s => s.IsEnabled).Select(s => Evaluate(s, lookup(s.Trace)))];
    }

    /// <summary>
    /// The same, given the samples rather than the measurements — which is what the measurements
    /// that need two traces require.
    /// <para>
    /// A separate entry point rather than a change to the one above, because most requirements are
    /// about one trace and most callers already have its measurements. This one measures what it
    /// needs from the samples it is handed, which is the only way a delay between two traces can be
    /// asked for at all.
    /// </para>
    /// </summary>
    public static IReadOnlyList<SpecResult> EvaluateAllFrom(
        IEnumerable<DesignSpec> specs, Func<string, IReadOnlyList<DataPoint>?> samples)
    {
        ArgumentNullException.ThrowIfNull(specs);
        ArgumentNullException.ThrowIfNull(samples);

        return [.. specs.Where(s => s.IsEnabled).Select(s => EvaluateFrom(s, samples))];
    }

    /// <summary>
    /// Holds one specification against the traces it names, measuring them itself.
    /// <para>
    /// Named apart from <see cref="Evaluate(DesignSpec, TraceMeasurements?)"/> rather than
    /// overloading it. Both second arguments are nullable reference types, so <c>Evaluate(spec,
    /// null)</c> — which is how a caller says "there is no such trace", and which several tests
    /// say — stops compiling the moment the second one exists. An overload set where passing null
    /// is ambiguous is an overload set that will be got wrong.
    /// </para>
    /// </summary>
    public static SpecResult EvaluateFrom(DesignSpec spec, Func<string, IReadOnlyList<DataPoint>?> samples)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(samples);

        if (!SpecPairs.NeedsTwo(spec.Quantity))
        {
            var found = samples(spec.Trace);

            return Evaluate(spec, found is null ? null : TraceMeasurements.OfAll(found));
        }

        var subject = samples(spec.Trace);
        var against = spec.Against.Length == 0 ? null : samples(spec.Against);

        if (subject is null)
        {
            return new SpecResult(spec, null, null,
                $"No trace called \"{spec.Trace}\" — probe it, or point the requirement at one " +
                "that is there.");
        }

        if (spec.Against.Length == 0)
        {
            return new SpecResult(spec, null, null,
                $"{SpecWords.Of(spec.Quantity)} is measured between two traces, and this " +
                "requirement names only one. Say which trace it is against.");
        }

        if (against is null)
        {
            return new SpecResult(spec, null, null,
                $"No trace called \"{spec.Against}\" to measure against.");
        }

        var measured = spec.Quantity switch
        {
            SpecQuantity.PropagationDelay => TraceTiming.PropagationDelay(against, subject),
            SpecQuantity.Skew => TraceTiming.Skew(subject, against),
            SpecQuantity.SetupTime => TraceTiming.SetupTime(subject, against),
            _ => TraceTiming.HoldTime(subject, against),
        };

        if (measured is not { } value)
        {
            return new SpecResult(spec, null, null,
                $"Nothing to measure yet: {SpecWords.Of(spec.Quantity)} needs an edge on each " +
                "trace, in the right order, and the scope has not recorded one.");
        }

        return Judge(spec, value);
    }

    /// <summary>
    /// The one-line verdict for a whole set: what a person wants to see before they look at any
    /// of it. Anything unmeasured is called out separately from anything failing, because the two
    /// mean opposite things about the design.
    /// </summary>
    public static string Summarise(IReadOnlyList<SpecResult> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        if (results.Count == 0) return "No requirements to check.";

        var failed = results.Count(r => r.Passed == false);
        var unknown = results.Count(r => r.Passed is null);
        var passed = results.Count - failed - unknown;

        if (failed == 0 && unknown == 0)
            return $"All {results.Count} requirements met.";

        List<string> parts = [];

        if (failed > 0) parts.Add($"{failed} not met");
        if (unknown > 0) parts.Add($"{unknown} not measurable");
        if (passed > 0) parts.Add($"{passed} met");

        return string.Join(", ", parts) + ".";
    }
}
