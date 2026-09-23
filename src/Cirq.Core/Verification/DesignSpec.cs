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

    private string Label =>
        $"{Trace} {Quantity.ToString().ToLowerInvariant()}";

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
                $"Nothing to measure yet: {spec.Quantity.ToString().ToLowerInvariant()} needs more " +
                "of the waveform than the scope has recorded.");
        }

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
