using Cirq.Core.Primitives;
using Cirq.Core.Probing;

namespace Cirq.Engine.Tests;

/// <summary>
/// Finding the edge a capture lines up on.
/// <para>
/// The arithmetic is checked against waveforms whose crossings are known exactly — a sine crosses
/// zero going up at every whole period, a ramp crosses a level once — so an edge found half a
/// sample late shows up as a number rather than as a picture that looks slightly wrong.
/// </para>
/// </summary>
public class ScopeTriggerTests
{
    private static List<DataPoint> Sine(double frequency, double amplitude, double duration, double interval)
    {
        List<DataPoint> samples = [];

        for (var t = 0.0; t <= duration; t += interval)
            samples.Add(new DataPoint(t, amplitude * Math.Sin(2 * Math.PI * frequency * t)));

        return samples;
    }

    [Fact]
    public void ARisingEdgeIsFoundWhereTheWaveformActuallyCrosses()
    {
        // A kilohertz sine crosses zero upwards at 0, 1, 2, 3 ms.
        var samples = Sine(1e3, 1.0, 5e-3, 1e-6);

        var found = ScopeTrigger.Find(samples, 0, TriggerSlope.Rising, noLaterThan: 3.5e-3);

        Assert.NotNull(found);
        Assert.Equal(3e-3, found!.Value, 1e-6);
    }

    [Fact]
    public void AFallingEdgeIsTheOtherHalfOfTheCycle()
    {
        var samples = Sine(1e3, 1.0, 5e-3, 1e-6);

        var found = ScopeTrigger.Find(samples, 0, TriggerSlope.Falling, noLaterThan: 3.2e-3);

        // Downward crossings are at 0.5, 1.5, 2.5 ms.
        Assert.Equal(2.5e-3, found!.Value, 1e-6);
    }

    /// <summary>
    /// Between samples, not at one. A scope that snapped to the nearest sample would jitter by up to
    /// a sample interval, and on a fast edge that is most of the edge.
    /// </summary>
    [Fact]
    public void TheCrossingIsInterpolated()
    {
        List<DataPoint> ramp = [new(0, 0), new(1, 10)];

        var found = ScopeTrigger.Find(ramp, 2.5, TriggerSlope.Rising, noLaterThan: 1);

        Assert.Equal(0.25, found!.Value, 1e-12);
    }

    [Fact]
    public void AnEdgeTooLateToDrawIsNotUsed()
    {
        var samples = Sine(1e3, 1.0, 5e-3, 1e-6);

        // Nothing has been recorded past 1.5 ms that the display would need, so the 1 ms edge is the
        // newest one that gives a steady picture.
        var found = ScopeTrigger.Find(samples, 0, TriggerSlope.Rising, noLaterThan: 1.5e-3);

        Assert.Equal(1e-3, found!.Value, 1e-6);
    }

    [Fact]
    public void AWaveformThatNeverReachesTheLevelTriggersNothing()
    {
        var samples = Sine(1e3, 1.0, 5e-3, 1e-6);

        Assert.Null(ScopeTrigger.Find(samples, 2.0, TriggerSlope.Rising, noLaterThan: 5e-3));
    }

    /// <summary>
    /// A trace that is already above the level at its first sample has not been seen to cross it.
    /// Counting that as an edge is how a display jumps to the start of the buffer the moment it is
    /// cleared.
    /// </summary>
    [Fact]
    public void StartingAboveTheLevelIsNotACrossing()
    {
        List<DataPoint> held = [new(0, 5), new(1, 5), new(2, 5)];

        Assert.Null(ScopeTrigger.Find(held, 1.0, TriggerSlope.Rising, noLaterThan: 2));
    }

    /// <summary>
    /// Noise on a slow edge crosses the level many times. Without a band to fall back past, every
    /// one of those counts and the picture jumps between them.
    /// </summary>
    [Fact]
    public void HysteresisRejectsAnEdgeThatIsOnlyNoise()
    {
        List<DataPoint> noisy =
        [
            new(0.0, -1.0),
            new(0.1, 0.05), new(0.2, -0.02), new(0.3, 0.04), new(0.4, -0.01),
            new(0.5, 1.0),
        ];

        // With no band, the first wobble past zero is an edge.
        var jumpy = ScopeTrigger.Find(noisy, 0, TriggerSlope.Rising, noLaterThan: 1);
        Assert.Equal(0.3, jumpy!.Value, 0.15);

        // With half a volt of band, the signal has to come back below −0.5 before another edge
        // counts, so only the real one at the start survives.
        var steady = ScopeTrigger.Find(noisy, 0, TriggerSlope.Rising, noLaterThan: 1, hysteresis: 0.5);
        Assert.NotNull(steady);
        Assert.InRange(steady!.Value, 0.0, 0.11);
    }

    /// <summary>
    /// Single-shot asks a different question — when did it happen, not where was it last — and the
    /// answer must stay the same as more samples arrive.
    /// </summary>
    [Fact]
    public void SingleShotTakesTheFirstEdgeAfterArmingAndKeepsIt()
    {
        var samples = Sine(1e3, 1.0, 5e-3, 1e-6);

        var armed = 1.2e-3;

        var first = ScopeTrigger.Find(samples, 0, TriggerSlope.Rising, noLaterThan: 3.5e-3, after: armed);

        Assert.Equal(2e-3, first!.Value, 1e-6);

        // More of the same waveform later does not move it.
        var later = ScopeTrigger.Find(samples, 0, TriggerSlope.Rising, noLaterThan: 4.5e-3, after: armed);

        Assert.Equal(first.Value, later!.Value, 1e-12);
    }

    [Fact]
    public void TooFewSamplesAreNotAnEdge()
    {
        Assert.Null(ScopeTrigger.Find([], 0, TriggerSlope.Rising, 1));
        Assert.Null(ScopeTrigger.Find([new DataPoint(0, 5)], 1, TriggerSlope.Rising, 1));
    }
}
