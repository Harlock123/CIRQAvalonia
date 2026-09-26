using Cirq.Core.Primitives;

namespace Cirq.Core.Probing;

/// <summary>When the scope should start drawing.</summary>
public enum TriggerMode
{
    /// <summary>Never: the display follows the newest samples, which is what it has always done.</summary>
    Off,

    /// <summary>On an edge when there is one, and on the newest samples when there is not.</summary>
    Auto,

    /// <summary>On an edge, and on nothing else — the display holds its last capture until one comes.</summary>
    Normal,

    /// <summary>On the next edge after arming, once, and then hold it.</summary>
    Single,
}

/// <summary>Which way the signal has to be going through the level.</summary>
public enum TriggerSlope
{
    Rising,
    Falling,
}

/// <summary>
/// Finds the edge a capture should be lined up on.
/// <para>
/// This is what makes a repeating waveform stand still. Without it the display shows the newest
/// samples, so a sine whose period does not divide the window exactly slides sideways a little on
/// every repaint, and reading anything off it means reading a moving target. A trigger fixes one
/// feature of the waveform to one place on the screen and lets everything else be measured against
/// it.
/// </para>
/// <para>
/// The other half of what it is for is the event that happens once. A glitch, a start-up transient,
/// a fault: by the time anybody has seen it and reached for the pause key it is several screens into
/// the past. A single-shot trigger catches it at the instant it happens and stops.
/// </para>
/// </summary>
public static class ScopeTrigger
{
    /// <summary>
    /// The edge to line the display up on, or null when the samples hold none.
    /// </summary>
    /// <param name="samples">The trace, oldest first.</param>
    /// <param name="level">The value the signal has to cross.</param>
    /// <param name="slope">Which way it has to be going.</param>
    /// <param name="noLaterThan">
    /// The latest edge worth using. A capture is only steady once everything to the right of the
    /// trigger has actually been recorded, so the caller passes the newest sample time less the
    /// part of the window that follows the trigger — and an edge later than that is one whose
    /// display would grow as the samples arrived, which is the sliding this exists to stop.
    /// </param>
    /// <param name="after">
    /// Only consider edges after this instant, and take the <i>first</i> rather than the last. This
    /// is single-shot: the question is not "where was the most recent edge" but "when did it
    /// happen", and the answer must not change afterwards.
    /// </param>
    /// <param name="hysteresis">
    /// How far the signal has to fall back past the level before another edge counts. Noise on a
    /// slow edge crosses the level a dozen times; without a band to leave, every one of those is an
    /// edge and the display jumps between them.
    /// </param>
    public static double? Find(
        IReadOnlyList<DataPoint> samples,
        double level,
        TriggerSlope slope,
        double noLaterThan,
        double? after = null,
        double hysteresis = 0)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count < 2) return null;

        var band = Math.Abs(hysteresis);
        var armLevel = slope == TriggerSlope.Rising ? level - band : level + band;

        // Armed means the signal has been clearly on the far side since the last edge. Starting
        // disarmed means a trace that begins already past the level does not count as having
        // crossed it: nothing was seen to happen.
        var armed = false;
        double? latest = null;

        for (var i = 1; i < samples.Count; i++)
        {
            var previous = samples[i - 1];
            var current = samples[i];

            if (slope == TriggerSlope.Rising)
            {
                if (previous.Value <= armLevel) armed = true;

                if (armed && previous.Value < level && current.Value >= level)
                {
                    armed = false;

                    if (Accept(Interpolate(previous, current, level)) is { } hit) return hit;
                }
            }
            else
            {
                if (previous.Value >= armLevel) armed = true;

                if (armed && previous.Value > level && current.Value <= level)
                {
                    armed = false;

                    if (Accept(Interpolate(previous, current, level)) is { } hit) return hit;
                }
            }
        }

        return latest;

        // Returns non-null only to stop early, which single-shot does: it wants the first edge after
        // arming and must go on saying so however many more arrive.
        double? Accept(double time)
        {
            if (after is { } armedAt)
                return time > armedAt && time <= noLaterThan ? time : null;

            if (time <= noLaterThan) latest = time;

            return null;
        }
    }

    /// <summary>
    /// Where between two samples the signal actually reached the level. A scope that snapped the
    /// trigger to the nearest sample would jitter by up to one sample interval, which on a fast edge
    /// is most of the edge.
    /// </summary>
    private static double Interpolate(DataPoint from, DataPoint to, double level)
    {
        var span = to.Value - from.Value;

        if (Math.Abs(span) < double.Epsilon) return to.Time;

        var fraction = Math.Clamp((level - from.Value) / span, 0, 1);

        return from.Time + ((to.Time - from.Time) * fraction);
    }
}
