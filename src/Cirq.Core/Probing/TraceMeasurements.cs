using Cirq.Core.Primitives;

namespace Cirq.Core.Probing;

/// <summary>
/// What a trace is doing, worked out from the samples themselves.
/// <para>
/// Every example in this library has a number in it — the ripple on a supply, the frequency an
/// oscillator settled at, the duty cycle a servo is being sent, how long an edge takes through a
/// gate — and until now every one of them had to be read off the screen by eye or described in the
/// prose. These are the readouts on the front of any real scope, and they exist for the same
/// reason: a picture of a waveform answers "what shape" and almost never answers "how much".
/// </para>
/// <para>
/// The periodic figures are <b>optional on purpose</b>. A frequency measured from less than two
/// cycles is a guess, and a duty cycle measured off a sine is meaningless. Where the samples do
/// not support a figure it is null and the panel leaves it out, rather than printing a number
/// nobody should rely on.
/// </para>
/// </summary>
/// <param name="Count">How many samples went into it.</param>
/// <param name="Minimum">Lowest value in the window.</param>
/// <param name="Maximum">Highest.</param>
/// <param name="Mean">The DC average — what an AC-coupled trace has removed.</param>
/// <param name="Rms">Root mean square, which is the figure a multimeter shows.</param>
/// <param name="Frequency">Hertz, or null when fewer than two cycles are on screen.</param>
/// <param name="DutyCycle">Fraction of a cycle spent above the midpoint, or null.</param>
/// <param name="RiseTime">Ten to ninety percent on the first clean rising edge, or null.</param>
public readonly record struct TraceMeasurements(
    int Count,
    double Minimum,
    double Maximum,
    double Mean,
    double Rms,
    double? Frequency,
    double? DutyCycle,
    double? RiseTime)
{
    /// <summary>Peak to peak, which is the number people mean by "how big is it".</summary>
    public double PeakToPeak => Maximum - Minimum;

    /// <summary>One cycle in seconds, from the frequency.</summary>
    public double? Period => Frequency is { } f and > 0 ? 1.0 / f : null;

    /// <summary>True when there were enough samples to measure anything at all.</summary>
    public bool IsValid => Count >= 2;

    /// <summary>Nothing measured — what an empty or single-sample window gives.</summary>
    public static TraceMeasurements None => new(0, 0, 0, 0, 0, null, null, null);

    /// <summary>
    /// Measures the samples that fall between two times, inclusive.
    /// <para>
    /// A window rather than the whole buffer, because a scope measures what is on the screen. A
    /// trace whose source was turned up halfway through has two different amplitudes in its
    /// history and only one of them is the answer to "what is it doing now".
    /// </para>
    /// </summary>
    public static TraceMeasurements Of(IReadOnlyList<DataPoint> samples, double from, double to)
    {
        List<DataPoint> window = [];

        foreach (var sample in samples)
        {
            if (sample.Time < from) continue;
            if (sample.Time > to) break;

            window.Add(sample);
        }

        return OfAll(window);
    }

    /// <summary>Measures every sample given, without windowing.</summary>
    public static TraceMeasurements OfAll(IReadOnlyList<DataPoint> samples)
    {
        if (samples.Count < 2) return None;

        var minimum = double.MaxValue;
        var maximum = double.MinValue;
        var sum = 0.0;
        var squares = 0.0;

        foreach (var sample in samples)
        {
            minimum = Math.Min(minimum, sample.Value);
            maximum = Math.Max(maximum, sample.Value);
            sum += sample.Value;
            squares += sample.Value * sample.Value;
        }

        var mean = sum / samples.Count;
        var rms = Math.Sqrt(squares / samples.Count);

        var crossings = RisingCrossings(samples, minimum, maximum);

        return new TraceMeasurements(
            samples.Count, minimum, maximum, mean, rms,
            FrequencyFrom(crossings),
            DutyFrom(samples, crossings, minimum, maximum),
            RiseTimeFrom(samples, minimum, maximum));
    }

    /// <summary>
    /// The midpoint of the waveform, which is what the periodic measurements trigger on. A scope
    /// triggers at a level you choose; a measurement has to choose one, and halfway between the
    /// extremes is the only choice that works for a sine, a square and a ramp alike.
    /// </summary>
    private static double Midpoint(double minimum, double maximum) => (minimum + maximum) / 2.0;

    /// <summary>
    /// Times at which the trace crosses its own midpoint going upwards, interpolated between the
    /// two samples either side so the answer is not quantised to the sample interval.
    /// <para>
    /// The hysteresis is what makes this usable on anything real. Without it a trace with noise on
    /// it crosses the midpoint dozens of times per edge and the measured frequency is the noise's
    /// rather than the signal's — which is the same failure the 74HC14 exists to fix, and it
    /// applies just as much to measuring as to switching.
    /// </para>
    /// </summary>
    private static List<double> RisingCrossings(
        IReadOnlyList<DataPoint> samples, double minimum, double maximum)
    {
        List<double> crossings = [];

        var amplitude = maximum - minimum;
        if (amplitude <= 0) return crossings;

        var middle = Midpoint(minimum, maximum);
        var deadband = amplitude * 0.1;

        // Armed once the trace has gone properly low, so only a real excursion counts.
        var armed = samples[0].Value < middle - deadband;

        for (var i = 1; i < samples.Count; i++)
        {
            var value = samples[i].Value;

            if (!armed)
            {
                if (value < middle - deadband) armed = true;
                continue;
            }

            if (value < middle) continue;

            // Crossed. Interpolate between this sample and the one before it.
            var previous = samples[i - 1];
            var span = value - previous.Value;

            var time = Math.Abs(span) < 1e-300
                ? samples[i].Time
                : previous.Time + ((middle - previous.Value) / span * (samples[i].Time - previous.Time));

            crossings.Add(time);
            armed = false;
        }

        return crossings;
    }

    /// <summary>
    /// Hertz from the average interval between crossings. Two crossings is one period and the
    /// least that can be measured; the average over however many there are is steadier than any
    /// single one.
    /// </summary>
    private static double? FrequencyFrom(List<double> crossings)
    {
        if (crossings.Count < 2) return null;

        var span = crossings[^1] - crossings[0];
        if (span <= 0) return null;

        return (crossings.Count - 1) / span;
    }

    /// <summary>
    /// The fraction of one whole cycle spent above the midpoint, measured over the cycles that
    /// are complete. Meaningful for a square wave and for a PWM signal, which is what anybody
    /// asking for it has.
    /// </summary>
    private static double? DutyFrom(
        IReadOnlyList<DataPoint> samples, List<double> crossings, double minimum, double maximum)
    {
        if (crossings.Count < 2) return null;

        var middle = Midpoint(minimum, maximum);
        var start = crossings[0];
        var end = crossings[^1];

        // Time-weighted rather than sample-counted: the solver's step is not uniform, so counting
        // samples above the line would weight a finely stepped edge as heavily as a long flat top.
        var above = 0.0;
        var total = 0.0;

        for (var i = 1; i < samples.Count; i++)
        {
            var left = Math.Max(samples[i - 1].Time, start);
            var right = Math.Min(samples[i].Time, end);

            if (right <= left) continue;

            var width = right - left;
            total += width;

            // The midpoint of the interval decides which side it counts towards, which is exact
            // for a flat segment and splits an edge evenly — and an edge is short either way.
            if ((samples[i - 1].Value + samples[i].Value) / 2.0 > middle) above += width;
        }

        return total > 0 ? above / total : null;
    }

    /// <summary>
    /// Ten to ninety percent of the first rising edge that goes all the way from one to the other
    /// without turning back. That qualification matters: a ringing edge crosses ninety percent,
    /// falls below it and crosses again, and timing to the last crossing would report a rise time
    /// that is mostly settling.
    /// </summary>
    private static double? RiseTimeFrom(
        IReadOnlyList<DataPoint> samples, double minimum, double maximum)
    {
        var amplitude = maximum - minimum;
        if (amplitude <= 0) return null;

        var low = minimum + (amplitude * 0.1);
        var high = minimum + (amplitude * 0.9);

        double? leftAt = null;

        for (var i = 1; i < samples.Count; i++)
        {
            var previous = samples[i - 1].Value;
            var value = samples[i].Value;

            // Left the ten percent line going up: start the clock.
            if (leftAt is null)
            {
                if (previous < low && value >= low) leftAt = Interpolate(samples[i - 1], samples[i], low);
                continue;
            }

            // Fell back below it before arriving: it was not the edge, so wait for the next one.
            if (value < low)
            {
                leftAt = null;
                continue;
            }

            if (previous < high && value >= high)
            {
                var arrived = Interpolate(samples[i - 1], samples[i], high);
                var rise = arrived - leftAt.Value;

                return rise > 0 ? rise : null;
            }
        }

        return null;
    }

    private static double Interpolate(DataPoint a, DataPoint b, double level)
    {
        var span = b.Value - a.Value;

        return Math.Abs(span) < 1e-300
            ? b.Time
            : a.Time + ((level - a.Value) / span * (b.Time - a.Time));
    }
}
