using Cirq.Core.Primitives;
using Cirq.Core.Probing;

namespace Cirq.Engine.Tests;

/// <summary>
/// The scope's automatic measurements, checked against waveforms whose answers are known exactly
/// rather than against anything this code produced before.
/// </summary>
public class TraceMeasurementTests
{
    private static List<DataPoint> Sine(double hertz, double amplitude, double offset, double seconds, int per = 400)
    {
        List<DataPoint> points = [];
        var step = 1.0 / (hertz * per);

        for (var t = 0.0; t <= seconds; t += step)
            points.Add(new DataPoint(t, offset + (amplitude * Math.Sin(2 * Math.PI * hertz * t))));

        return points;
    }

    private static List<DataPoint> Square(double hertz, double low, double high, double duty, double seconds, int per = 400)
    {
        List<DataPoint> points = [];
        var step = 1.0 / (hertz * per);
        var period = 1.0 / hertz;

        for (var t = 0.0; t <= seconds; t += step)
            points.Add(new DataPoint(t, t % period < period * duty ? high : low));

        return points;
    }

    // ---- the plain statistics ---------------------------------------------

    [Fact]
    public void ASineGivesItsOwnAmplitudeMeanAndRms()
    {
        var m = TraceMeasurements.OfAll(Sine(1e3, 2.0, 1.0, 10e-3));

        Assert.True(m.IsValid);
        Assert.Equal(3.0, m.Maximum, 2);
        Assert.Equal(-1.0, m.Minimum, 2);
        Assert.Equal(4.0, m.PeakToPeak, 2);
        Assert.Equal(1.0, m.Mean, 2);

        // RMS of a sine on a DC offset is sqrt(offset² + (A/√2)²) = sqrt(1 + 2) = 1.732.
        Assert.Equal(Math.Sqrt(1.0 + 2.0), m.Rms, 2);
    }

    [Fact]
    public void ASquareWavesRmsIsItsAmplitudeBecauseItIsAlwaysAtOneOrTheOther()
    {
        var m = TraceMeasurements.OfAll(Square(1e3, -5.0, 5.0, 0.5, 10e-3));

        Assert.Equal(5.0, m.Rms, 1);
        Assert.Equal(0.0, m.Mean, 1);
        Assert.Equal(10.0, m.PeakToPeak, 2);
    }

    // ---- frequency ---------------------------------------------------------

    [Theory]
    [InlineData(50.0)]
    [InlineData(1e3)]
    [InlineData(32.768e3)]
    public void TheFrequencyOfASineIsTheFrequencyItWasMadeAt(double hertz)
    {
        var m = TraceMeasurements.OfAll(Sine(hertz, 1.0, 0.0, 10.0 / hertz));

        Assert.NotNull(m.Frequency);
        Assert.Equal(hertz, m.Frequency!.Value, hertz * 0.01);
        Assert.Equal(1.0 / hertz, m.Period!.Value, (1.0 / hertz) * 0.01);
    }

    [Fact]
    public void FewerThanTwoCyclesIsNotAFrequencyAndIsNotGuessedAt()
    {
        // Three quarters of one cycle: enough to see it rising, nowhere near enough to time it.
        var m = TraceMeasurements.OfAll(Sine(1e3, 1.0, 0.0, 0.75e-3));

        Assert.True(m.IsValid);
        Assert.Null(m.Frequency);
        Assert.Null(m.Period);
    }

    [Fact]
    public void AFlatLineHasNoFrequencyRatherThanAnInfiniteOne()
    {
        List<DataPoint> flat = [];
        for (var i = 0; i < 100; i++) flat.Add(new DataPoint(i * 1e-6, 3.3));

        var m = TraceMeasurements.OfAll(flat);

        Assert.Equal(0.0, m.PeakToPeak, 9);
        Assert.Equal(3.3, m.Mean, 6);
        Assert.Null(m.Frequency);
    }

    /// <summary>
    /// The reason there is hysteresis in the crossing detector at all, and the same reason the
    /// 74HC14 is in the palette.
    /// </summary>
    [Fact]
    public void NoiseOnTheEdgesDoesNotMultiplyTheMeasuredFrequency()
    {
        var clean = Square(1e3, 0.0, 5.0, 0.5, 10e-3);
        var random = new Random(1);

        List<DataPoint> noisy = [.. clean.Select(p =>
            new DataPoint(p.Time, p.Value + ((random.NextDouble() - 0.5) * 0.3)))];

        var m = TraceMeasurements.OfAll(noisy);

        Assert.NotNull(m.Frequency);
        Assert.Equal(1e3, m.Frequency!.Value, 50.0);
    }

    // ---- duty cycle --------------------------------------------------------

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    public void TheDutyCycleOfASquareWaveIsWhatItWasMadeWith(double duty)
    {
        var m = TraceMeasurements.OfAll(Square(1e3, 0.0, 5.0, duty, 20e-3));

        Assert.NotNull(m.DutyCycle);
        Assert.Equal(duty, m.DutyCycle!.Value, 2);
    }

    [Fact]
    public void ASineIsHalfAboveItsOwnMidpoint()
    {
        var m = TraceMeasurements.OfAll(Sine(1e3, 1.0, 0.0, 20e-3));

        Assert.NotNull(m.DutyCycle);
        Assert.Equal(0.5, m.DutyCycle!.Value, 2);
    }

    // ---- rise time ---------------------------------------------------------

    [Fact]
    public void TheRiseTimeOfAnExponentialIsTheTextbookMultipleOfItsTimeConstant()
    {
        // A capacitor charging through a resistor. Ten to ninety percent takes ln(9) = 2.197 time
        // constants, which is the figure every datasheet's rise time is quoted against.
        const double tau = 1e-3;

        List<DataPoint> charge = [];
        for (var t = 0.0; t <= 8 * tau; t += tau / 2000.0)
            charge.Add(new DataPoint(t, 5.0 * (1 - Math.Exp(-t / tau))));

        var m = TraceMeasurements.OfAll(charge);

        Assert.NotNull(m.RiseTime);
        Assert.Equal(Math.Log(9.0) * tau, m.RiseTime!.Value, tau * 0.02);
    }

    /// <summary>
    /// A ringing edge crosses ninety percent, falls back and crosses again. Timing to the last
    /// crossing would report a rise time that is mostly settling.
    /// </summary>
    [Fact]
    public void RingingAfterTheEdgeIsNotCountedAsPartOfTheRise()
    {
        const double rise = 1e-6;

        List<DataPoint> edge = [];
        for (var t = 0.0; t <= 50e-6; t += 1e-9)
        {
            var value = t < rise
                ? 5.0 * t / rise
                : 5.0 + (1.5 * Math.Exp(-(t - rise) / 5e-6) * Math.Sin(2 * Math.PI * 1e6 * (t - rise)));

            edge.Add(new DataPoint(t, value));
        }

        var m = TraceMeasurements.OfAll(edge);

        Assert.NotNull(m.RiseTime);

        // The ramp itself, not the ramp plus the ring. Generous, because the overshoot moves where
        // the ten and ninety percent lines sit — but nowhere near the 5 us the ringing lasts.
        Assert.InRange(m.RiseTime!.Value, 0.2e-6, 2e-6);
    }

    [Fact]
    public void AFallingOnlyTraceHasNoRiseTime()
    {
        List<DataPoint> falling = [];
        for (var t = 0.0; t <= 1e-3; t += 1e-6)
            falling.Add(new DataPoint(t, 5.0 * Math.Exp(-t / 1e-4)));

        Assert.Null(TraceMeasurements.OfAll(falling).RiseTime);
    }

    // ---- windowing ---------------------------------------------------------

    [Fact]
    public void MeasuringAWindowMeasuresWhatIsInItAndNotTheRest()
    {
        // Quiet for the first half, then a volt peak to peak.
        List<DataPoint> samples = [];
        for (var t = 0.0; t <= 20e-3; t += 1e-6)
            samples.Add(new DataPoint(t, t < 10e-3 ? 0.0 : Math.Sin(2 * Math.PI * 1e3 * t)));

        var whole = TraceMeasurements.Of(samples, 0, 20e-3);
        var second = TraceMeasurements.Of(samples, 10e-3, 20e-3);
        var first = TraceMeasurements.Of(samples, 0, 9e-3);

        Assert.Equal(2.0, whole.PeakToPeak, 1);
        Assert.Equal(2.0, second.PeakToPeak, 1);
        Assert.Equal(0.0, first.PeakToPeak, 6);
        Assert.Null(first.Frequency);
    }

    [Fact]
    public void AnEmptyOrSingleSampleWindowMeasuresNothingRatherThanThrowing()
    {
        Assert.False(TraceMeasurements.OfAll([]).IsValid);
        Assert.False(TraceMeasurements.OfAll([new DataPoint(0, 1)]).IsValid);
        Assert.False(TraceMeasurements.Of([new DataPoint(0, 1)], 5, 10).IsValid);
    }
}
