using Cirq.Core.Primitives;

namespace Cirq.Core.Probing;

/// <summary>Which way a trace was going when it crossed.</summary>
public enum EdgeDirection
{
    /// <summary>Whichever comes first. What a gate's propagation delay is quoted over.</summary>
    Either,

    Rising,

    Falling,
}

/// <summary>
/// The measurements that need two traces rather than one.
/// <para>
/// Everything else here is about a single waveform, and the most-quoted number on any logic
/// datasheet is not: a propagation delay is the gap between one signal moving and another moving
/// because of it. A library shipping twenty-one 74xx parts, fifteen 40xx parts and an event
/// scheduler had no way to measure the figure those parts are sold on.
/// </para>
/// <para>
/// Every one of these is a time between two <b>edges</b>, so they all rest on the same question —
/// when did this trace cross its own midpoint, and which way was it going. Getting that one answer
/// right is most of the work; the four measurements are arithmetic on top of it.
/// </para>
/// </summary>
public static class TraceTiming
{
    /// <summary>
    /// How much of its own range a trace has to move away from the midpoint before a crossing back
    /// over it counts as an edge.
    /// <para>
    /// Without it a noisy trace crosses its midpoint many times per edge and every measurement here
    /// becomes a measurement of the noise. It is the same hysteresis the single-trace measurements
    /// use, and the same reason a 74HC14 exists.
    /// </para>
    /// </summary>
    public const double Deadband = 0.1;

    /// <summary>
    /// When a trace crossed its own midpoint, and which way it was going each time.
    /// </summary>
    public static List<(double Time, bool Rising)> Edges(IReadOnlyList<DataPoint> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        List<(double, bool)> edges = [];

        if (samples.Count < 2) return edges;

        var minimum = double.MaxValue;
        var maximum = double.MinValue;

        foreach (var sample in samples)
        {
            minimum = Math.Min(minimum, sample.Value);
            maximum = Math.Max(maximum, sample.Value);
        }

        var amplitude = maximum - minimum;
        if (amplitude <= 0) return edges;

        var middle = (minimum + maximum) / 2.0;
        var band = amplitude * Deadband;

        // Which side the trace has properly committed to. Null until it has been clear of the
        // midpoint once, so a capture that begins mid-edge does not report that as a crossing.
        bool? low = samples[0].Value < middle - band ? true
            : samples[0].Value > middle + band ? false
            : null;

        for (var i = 1; i < samples.Count; i++)
        {
            var value = samples[i].Value;

            if (low is null)
            {
                if (value < middle - band) low = true;
                else if (value > middle + band) low = false;

                continue;
            }

            var rising = low.Value;

            // It has to reach the far side of the band, not merely the midpoint: touching the
            // middle and falling back is not an edge, it is a wobble.
            if (rising ? value <= middle + band : value >= middle - band) continue;

            edges.Add((Cross(samples, i, middle), rising));

            low = !rising;
        }

        return edges;
    }

    /// <summary>
    /// When the trace passed the level, interpolated between the two samples either side — walking
    /// back from where it was noticed, because it may have crossed several samples ago.
    /// </summary>
    private static double Cross(IReadOnlyList<DataPoint> samples, int noticed, double level)
    {
        for (var i = noticed; i > 0; i--)
        {
            var a = samples[i - 1];
            var b = samples[i];

            var straddles = (a.Value - level) * (b.Value - level) <= 0 && a.Value != b.Value;

            if (!straddles) continue;

            var span = b.Value - a.Value;

            return a.Time + ((level - a.Value) / span * (b.Time - a.Time));
        }

        return samples[noticed].Time;
    }

    /// <summary>
    /// The delay from an edge on one trace to the next edge on another.
    /// <para>
    /// Measured from the first edge on the cause that has an edge on the effect after it, so a
    /// capture that starts with the output already settled does not pair an input edge with an
    /// output edge from before it. Null when there is no such pair.
    /// </para>
    /// </summary>
    /// <param name="cause">The trace that moved first — an input, a clock.</param>
    /// <param name="effect">The trace that moved because of it.</param>
    /// <param name="direction">Which edges of the cause to time from.</param>
    public static double? PropagationDelay(
        IReadOnlyList<DataPoint> cause,
        IReadOnlyList<DataPoint> effect,
        EdgeDirection direction = EdgeDirection.Either)
    {
        var from = Edges(cause).Where(e => Matches(e.Rising, direction)).ToList();
        var to = Edges(effect);

        foreach (var (time, _) in from)
        {
            foreach (var (answer, _) in to)
            {
                if (answer < time) continue;

                return answer - time;
            }
        }

        return null;
    }

    /// <summary>
    /// How far apart two traces that are supposed to move together actually do — the worst pairing
    /// across the capture, since skew is a limit and a limit is about the worst case.
    /// <para>
    /// Each edge is paired with the nearest one going the same way on the other trace, and a
    /// pairing further apart than <b>half a cycle</b> is thrown away as being with the wrong cycle
    /// altogether.
    /// </para>
    /// <para>
    /// Both of the simpler rules were tried and both are wrong at an end of the capture. Pairing
    /// purely by nearest lets an edge whose partner falls outside the window pair with the previous
    /// cycle's, and two traces twelve nanoseconds apart come back as very nearly a whole period of
    /// skew. Pairing purely in order — the nth against the nth — breaks the other way round: a
    /// trace shifted by twelve nanoseconds can begin the capture in the opposite state, giving it
    /// one extra leading edge, and every pair after that is off by a cycle. The half-cycle guard is
    /// what makes nearest safe, and it also throws away the unpaired edges at both ends, which is
    /// the only honest thing to do with them.
    /// </para>
    /// </summary>
    public static double? Skew(IReadOnlyList<DataPoint> first, IReadOnlyList<DataPoint> second)
    {
        var a = Edges(first);
        var b = Edges(second);

        if (a.Count == 0 || b.Count == 0) return null;

        // How long a cycle is, taken from whichever trace has more edges to say. Beyond half of
        // this, two edges are from different cycles however close they look.
        var window = Cycle(a) ?? Cycle(b);

        double? worst = null;

        foreach (var (time, rising) in a)
        {
            double? nearest = null;

            foreach (var (other, otherRising) in b)
            {
                if (otherRising != rising) continue;

                var gap = Math.Abs(other - time);

                if (nearest is null || gap < nearest) nearest = gap;
            }

            if (nearest is null) continue;
            if (window is { } limit && nearest > limit / 2) continue;

            if (worst is null || nearest > worst) worst = nearest;
        }

        return worst;
    }

    /// <summary>
    /// The typical time between one edge and the next going the same way, or null when there are
    /// not enough edges to say. The median rather than the mean, so one long gap — a burst with a
    /// pause in it — does not stretch the window and let a wrong-cycle pairing through.
    /// </summary>
    private static double? Cycle(List<(double Time, bool Rising)> edges)
    {
        List<double> intervals = [];

        foreach (var rising in (bool[])[true, false])
        {
            var times = edges.Where(e => e.Rising == rising).Select(e => e.Time).ToList();

            for (var i = 1; i < times.Count; i++) intervals.Add(times[i] - times[i - 1]);
        }

        if (intervals.Count == 0) return null;

        intervals.Sort();

        return intervals[intervals.Count / 2];
    }

    /// <summary>
    /// How long the data was already stable before the clock edge that sampled it — the worst case
    /// across the capture, since setup time is a minimum a part demands and what matters is the
    /// tightest one that actually occurred.
    /// <para>
    /// Null when no clock edge has data settled before it, which is the honest answer for a capture
    /// that begins mid-transaction.
    /// </para>
    /// </summary>
    public static double? SetupTime(
        IReadOnlyList<DataPoint> data,
        IReadOnlyList<DataPoint> clock,
        EdgeDirection active = EdgeDirection.Rising)
    {
        var clocks = Edges(clock).Where(e => Matches(e.Rising, active)).Select(e => e.Time).ToList();
        var changes = Edges(data).Select(e => e.Time).ToList();

        if (clocks.Count == 0 || changes.Count == 0) return null;

        double? worst = null;

        foreach (var edge in clocks)
        {
            // The last time the data moved before this clock edge.
            double? last = null;

            foreach (var change in changes)
            {
                if (change > edge) break;

                last = change;
            }

            if (last is null) continue;

            var setup = edge - last.Value;

            if (worst is null || setup < worst) worst = setup;
        }

        return worst;
    }

    /// <summary>
    /// And how long it stayed stable afterwards. The mirror of <see cref="SetupTime"/>, and the
    /// other half of the pair every synchronous datasheet quotes.
    /// </summary>
    public static double? HoldTime(
        IReadOnlyList<DataPoint> data,
        IReadOnlyList<DataPoint> clock,
        EdgeDirection active = EdgeDirection.Rising)
    {
        var clocks = Edges(clock).Where(e => Matches(e.Rising, active)).Select(e => e.Time).ToList();
        var changes = Edges(data).Select(e => e.Time).ToList();

        if (clocks.Count == 0 || changes.Count == 0) return null;

        double? worst = null;

        foreach (var edge in clocks)
        {
            double? next = null;

            foreach (var change in changes)
            {
                if (change < edge) continue;

                next = change;
                break;
            }

            if (next is null) continue;

            var hold = next.Value - edge;

            if (worst is null || hold < worst) worst = hold;
        }

        return worst;
    }

    private static bool Matches(bool rising, EdgeDirection wanted) => wanted switch
    {
        EdgeDirection.Rising => rising,
        EdgeDirection.Falling => !rising,
        _ => true,
    };
}
