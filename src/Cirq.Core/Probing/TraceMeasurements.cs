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
/// <param name="FallTime">Ninety to ten percent on the first clean falling edge, or null.</param>
/// <param name="Overshoot">
/// How far past its settled value the trace went on the way there, as a fraction of the step it
/// took. Null unless the trace actually is a step that settled — see <see cref="Settled"/>.
/// </param>
/// <param name="SettlingTime">
/// From the moment the trace left its starting value to the last moment it was further than
/// <see cref="SettlingBand"/> from where it ended up. Null on the same terms as the overshoot.
/// </param>
/// <param name="PulseWidth">How long the first complete pulse spent above the midpoint, or null.</param>
public readonly record struct TraceMeasurements(
    int Count,
    double Minimum,
    double Maximum,
    double Mean,
    double Rms,
    double? Frequency,
    double? DutyCycle,
    double? RiseTime,
    double? FallTime = null,
    double? Overshoot = null,
    double? SettlingTime = null,
    double? PulseWidth = null)
{
    /// <summary>
    /// How close to its final value a step has to get, as a fraction of the step, before it counts
    /// as settled. Two percent is the figure control loops are specified in; five is the other
    /// common one, and stating which is the only thing that makes a settling time comparable
    /// between two datasheets.
    /// </summary>
    public const double SettlingBand = 0.02;

    /// <summary>
    /// Volts per second on the fastest edge, derived from the rise time the way a datasheet
    /// derives it: the eighty percent of the swing that the ten-to-ninety measurement covers,
    /// divided by how long it took. Null when there is no clean edge to measure.
    /// </summary>
    public double? SlewRate => RiseTime is { } rise and > 0 ? PeakToPeak * 0.8 / rise : null;

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
        var (overshoot, settling) = StepFrom(samples, minimum, maximum);

        return new TraceMeasurements(
            samples.Count, minimum, maximum, mean, rms,
            FrequencyFrom(crossings),
            DutyFrom(samples, crossings, minimum, maximum),
            EdgeTime(samples, minimum, maximum, rising: true),
            EdgeTime(samples, minimum, maximum, rising: false),
            overshoot,
            settling,
            PulseWidthFrom(samples, crossings, minimum, maximum));
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
    /// Ten to ninety percent of the first edge that goes all the way from one to the other without
    /// turning back, in whichever direction was asked for.
    /// <para>
    /// That qualification matters: a ringing edge crosses ninety percent, falls below it and
    /// crosses again, and timing to the last crossing would report a rise time that is mostly
    /// settling. Settling is its own measurement, below, and conflating the two makes both useless.
    /// </para>
    /// <para>
    /// The two directions are one method because they are one measurement read upside down. Two
    /// copies of this drifted apart in every codebase that has ever had them.
    /// </para>
    /// <para>
    /// An edge that happens entirely between two samples gets <b>no</b> time rather than an
    /// interpolated one. Both thresholds are crossed in the same interval and the loop wants them
    /// in different ones, which looks like an oversight and is the right answer: any number
    /// produced there would be a statement about the sample interval rather than about the
    /// circuit. What is true is that the edge is faster than this can resolve.
    /// </para>
    /// </summary>
    private static double? EdgeTime(
        IReadOnlyList<DataPoint> samples, double minimum, double maximum, bool rising)
    {
        var amplitude = maximum - minimum;
        if (amplitude <= 0) return null;

        // The level the edge starts from and the one it arrives at. A fall runs from ninety down
        // to ten, which is the same two lines crossed the other way about.
        var from = minimum + (amplitude * (rising ? 0.1 : 0.9));
        var to = minimum + (amplitude * (rising ? 0.9 : 0.1));

        double? leftAt = null;

        for (var i = 1; i < samples.Count; i++)
        {
            var previous = samples[i - 1].Value;
            var value = samples[i].Value;

            if (leftAt is null)
            {
                if (Crossed(previous, value, from, rising))
                    leftAt = Interpolate(samples[i - 1], samples[i], from);

                continue;
            }

            // Fell back past the starting line before arriving: it was not the edge.
            if (rising ? value < from : value > from)
            {
                leftAt = null;
                continue;
            }

            if (!Crossed(previous, value, to, rising)) continue;

            var span = Interpolate(samples[i - 1], samples[i], to) - leftAt.Value;

            return span > 0 ? span : null;
        }

        return null;
    }

    /// <summary>True when the trace went through a level between two samples, the right way.</summary>
    private static bool Crossed(double previous, double value, double level, bool rising) =>
        rising ? previous < level && value >= level : previous > level && value <= level;

    /// <summary>
    /// How far past its final value the trace went, and how long it took to stay near it.
    /// <para>
    /// Both are answers about a <b>step</b>, and both are null for anything that is not one. That
    /// is the whole difficulty: overshoot on a sine is a number the arithmetic will happily
    /// produce — the peak is above the mean, after all — and it means nothing at all, so it has to
    /// be refused rather than computed. A trace counts as a step when it ends up somewhere
    /// different from where it started and is not still moving when the samples run out.
    /// </para>
    /// <para>
    /// The settling time is measured from when the trace <i>left</i> its starting value rather than
    /// from the first sample, because a capture that begins a millisecond before the edge would
    /// otherwise report a millisecond of settling that is really a millisecond of waiting.
    /// </para>
    /// </summary>
    private static (double? Overshoot, double? SettlingTime) StepFrom(
        IReadOnlyList<DataPoint> samples, double minimum, double maximum)
    {
        var amplitude = maximum - minimum;
        if (amplitude <= 0) return (null, null);

        // Where it settled: averaged over a tenth of the capture, because by then it is flat by
        // assumption and a single noisy sample should not decide the answer.
        var tail = Math.Max(samples.Count / 10, 1);
        var finished = Average(samples, samples.Count - tail, samples.Count);

        // Where it began: a handful of samples and no more. Averaging the first tenth the way the
        // last tenth is averaged looks symmetrical and is wrong, because the trace is flat at the
        // end and is not at the beginning — that is the whole of what a step is. A one millisecond
        // exponential inside a fifty millisecond capture is already at 0.8 a tenth of the way in,
        // so the "starting value" came out as four fifths of the step and every step in the suite
        // was rejected for not having moved far enough.
        var started = Average(samples, 0, Math.Min(3, samples.Count));

        var step = finished - started;

        // It has to have moved by a good part of its own range to be a step rather than a wobble
        // on top of one level — which is what a sine, a ripple and a clock all are. A third rather
        // than a half, so that a badly damped step which overshoots by most of its own size again
        // still counts as the step it plainly is.
        if (Math.Abs(step) < amplitude * (1.0 / 3.0)) return (null, null);

        var band = Math.Abs(step) * SettlingBand;

        // And it has to have stopped moving. A ramp that is still climbing at the last sample has
        // not settled anywhere, so it has no settling time and no overshoot.
        for (var i = samples.Count - tail; i < samples.Count; i++)
            if (Math.Abs(samples[i].Value - finished) > band)
                return (null, null);

        // Past the far side of the step: the peak beyond it, as a fraction of the step taken.
        var beyond = step > 0 ? maximum - finished : finished - minimum;
        var overshoot = Math.Max(beyond, 0) / Math.Abs(step);

        var left = Departure(samples, started, band);
        var last = LastOutside(samples, finished, band);

        var settling = left is { } from && last > from ? last - from : (double?)null;

        return (overshoot, settling);
    }

    /// <summary>When the trace first got further than the band from where it began.</summary>
    private static double? Departure(IReadOnlyList<DataPoint> samples, double started, double band)
    {
        foreach (var sample in samples)
            if (Math.Abs(sample.Value - started) > band)
                return sample.Time;

        return null;
    }

    /// <summary>The last moment the trace was further than the band from where it ended up.</summary>
    private static double LastOutside(IReadOnlyList<DataPoint> samples, double finished, double band)
    {
        for (var i = samples.Count - 1; i >= 0; i--)
            if (Math.Abs(samples[i].Value - finished) > band)
                return samples[i].Time;

        return samples[0].Time;
    }

    private static double Average(IReadOnlyList<DataPoint> samples, int from, int to)
    {
        var sum = 0.0;
        var count = 0;

        for (var i = Math.Max(from, 0); i < Math.Min(to, samples.Count); i++)
        {
            sum += samples[i].Value;
            count++;
        }

        return count == 0 ? 0 : sum / count;
    }

    /// <summary>
    /// How long the first complete pulse spent above the midpoint.
    /// <para>
    /// Measured between a rising crossing and the falling one after it, so a pulse cut off by the
    /// end of the capture is not reported as a short one. This is the figure a datasheet gives as
    /// a minimum for a reset line or a clock, and the duty cycle does not answer it: the same duty
    /// at twice the frequency is half the pulse.
    /// </para>
    /// </summary>
    private static double? PulseWidthFrom(
        IReadOnlyList<DataPoint> samples, List<double> crossings, double minimum, double maximum)
    {
        if (crossings.Count == 0) return null;

        var middle = Midpoint(minimum, maximum);
        var start = crossings[0];

        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i].Time <= start) continue;

            if (samples[i - 1].Value <= middle || samples[i].Value > middle) continue;

            var fell = Interpolate(samples[i - 1], samples[i], middle);

            return fell > start ? fell - start : null;
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
