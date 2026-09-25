using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Verification;

namespace Cirq.Engine.Tests;

/// <summary>
/// The measurements a datasheet is actually written in: fall time, overshoot, settling, pulse
/// width and slew rate.
/// <para>
/// Every one of these is checked against a waveform built from a formula, so the expected answer is
/// arithmetic rather than a recording. A second-order step response has a closed-form overshoot —
/// <c>exp(−πζ/√(1−ζ²))</c> — and if this disagrees with that then it is this that is wrong.
/// </para>
/// </summary>
public class StepMeasurementTests
{
    /// <summary>
    /// A pulse train with edges of a stated width: high for <paramref name="duty"/> of each cycle,
    /// with linear ramps of <paramref name="edge"/> seconds either side of the flat parts.
    /// </summary>
    private static double Trapezoid(double t, double hertz, double duty, double edge)
    {
        var period = 1.0 / hertz;
        var phase = (t - (Math.Floor(t / period) * period)) / period;

        var rise = edge / period;
        var high = duty;

        if (phase < rise) return phase / rise;
        if (phase < high) return 1.0;
        if (phase < high + rise) return 1.0 - ((phase - high) / rise);

        return 0.0;
    }

    private static List<DataPoint> Sample(double duration, int count, Func<double, double> shape)
    {
        List<DataPoint> points = [];

        for (var i = 0; i < count; i++)
        {
            var t = duration * i / (count - 1.0);
            points.Add(new DataPoint(t, shape(t)));
        }

        return points;
    }

    /// <summary>
    /// A second-order step response with damping ζ overshoots by exp(−πζ/√(1−ζ²)). That is the
    /// textbook result, it depends on nothing but the damping, and it is the only reason to trust
    /// an overshoot measurement at all.
    /// </summary>
    [Theory]
    [InlineData(0.1, 0.7292)]
    [InlineData(0.3, 0.3720)]
    [InlineData(0.5, 0.1630)]
    [InlineData(0.7, 0.0460)]
    public void OvershootMatchesTheClosedFormForASecondOrderStep(double zeta, double expected)
    {
        const double wn = 1000.0;

        var wd = wn * Math.Sqrt(1 - (zeta * zeta));

        // The standard underdamped step response, run long enough to have settled flat.
        var samples = Sample(0.08, 20_000, t =>
            1.0 - (Math.Exp(-zeta * wn * t) *
                   (Math.Cos(wd * t) + (zeta / Math.Sqrt(1 - (zeta * zeta)) * Math.Sin(wd * t)))));

        var measured = TraceMeasurements.OfAll(samples).Overshoot;

        Assert.NotNull(measured);
        Assert.Equal(expected, measured.Value, 0.01);
    }

    /// <summary>
    /// A first-order step does not overshoot at all, and reporting a fraction of a percent of one
    /// would be reporting the sampling rather than the circuit.
    /// </summary>
    [Fact]
    public void AFirstOrderStepHasNoOvershoot()
    {
        var samples = Sample(0.05, 5000, t => 1.0 - Math.Exp(-t / 1e-3));

        Assert.Equal(0.0, TraceMeasurements.OfAll(samples).Overshoot!.Value, 1e-6);
    }

    /// <summary>
    /// A first-order step is within two percent of its final value after <c>ln(50)</c> time
    /// constants, and it left the two percent band around its <i>starting</i> value after
    /// <c>ln(50/49)</c> of one. The settling time is the gap between those, which is exactly
    /// <c>τ·ln(49)</c> — a number anybody can check, and one this has no way to fudge.
    /// <para>
    /// Timing from the departure rather than from the first sample is what makes the measurement
    /// mean anything: a capture that began a millisecond before the edge would otherwise report a
    /// millisecond of settling that is really a millisecond of waiting.
    /// </para>
    /// </summary>
    [Fact]
    public void SettlingTimeIsWhereTheExponentialSaysItIs()
    {
        const double tau = 1e-3;

        var samples = Sample(0.02, 20_000, t => 1.0 - Math.Exp(-t / tau));

        var settling = TraceMeasurements.OfAll(samples).SettlingTime;

        Assert.NotNull(settling);
        Assert.Equal(Math.Log(49.0) * tau, settling.Value, tau * 0.01);
    }

    /// <summary>
    /// And the waiting really is excluded. The same edge with a millisecond of flat line in front
    /// of it settles in the same time, not a millisecond longer.
    /// </summary>
    [Fact]
    public void WaitingBeforeTheEdgeIsNotSettling()
    {
        const double tau = 1e-3;
        const double wait = 5e-3;

        var prompt = TraceMeasurements.OfAll(
            Sample(0.02, 20_000, t => 1.0 - Math.Exp(-t / tau)));

        var delayed = TraceMeasurements.OfAll(
            Sample(0.025, 25_000, t => t < wait ? 0.0 : 1.0 - Math.Exp(-(t - wait) / tau)));

        Assert.Equal(prompt.SettlingTime!.Value, delayed.SettlingTime!.Value, tau * 0.02);
    }

    /// <summary>
    /// Neither is measured on something that is not a step. The arithmetic would happily produce a
    /// number for a sine — its peak is above its mean, after all — and the number would mean
    /// nothing, so it has to be refused rather than computed.
    /// </summary>
    [Fact]
    public void NeitherIsMeasuredOnSomethingThatIsNotAStep()
    {
        var sine = TraceMeasurements.OfAll(Sample(0.01, 4000, t => Math.Sin(2 * Math.PI * 1000 * t)));

        Assert.Null(sine.Overshoot);
        Assert.Null(sine.SettlingTime);
    }

    /// <summary>A ramp still climbing when the samples run out has not settled anywhere.</summary>
    [Fact]
    public void ARampThatIsStillMovingHasNotSettled()
    {
        var ramp = TraceMeasurements.OfAll(Sample(0.01, 2000, t => t * 100.0));

        Assert.Null(ramp.SettlingTime);
        Assert.Null(ramp.Overshoot);
    }

    /// <summary>
    /// An exponential's ten-to-ninety time is <c>ln(9)</c> time constants, going up or coming down.
    /// The two directions are one measurement read upside down and must agree.
    /// </summary>
    [Fact]
    public void RiseAndFallAreTheSameMeasurementBothWaysUp()
    {
        const double tau = 1e-4;

        var expected = Math.Log(9.0) * tau;

        var rising = TraceMeasurements.OfAll(Sample(2e-3, 20_000, t => 1.0 - Math.Exp(-t / tau)));
        var falling = TraceMeasurements.OfAll(Sample(2e-3, 20_000, t => Math.Exp(-t / tau)));

        Assert.Equal(expected, rising.RiseTime!.Value, tau * 0.05);
        Assert.Equal(expected, falling.FallTime!.Value, tau * 0.05);
    }

    /// <summary>
    /// Slew rate is the eighty percent of the swing the rise time covers, over how long it took —
    /// the way a datasheet derives it from the same edge.
    /// </summary>
    [Fact]
    public void SlewRateIsTheSwingOverTheEdge()
    {
        // A clean ten volt ramp over a microsecond, then flat: 10 V/µs by construction, and
        // the ten-to-ninety portion of it slews at exactly the same rate.
        var samples = Sample(4e-6, 8000, t => Math.Min(t / 1e-6, 1.0) * 10.0);

        var measured = TraceMeasurements.OfAll(samples).SlewRate;

        Assert.NotNull(measured);
        Assert.Equal(1e7, measured.Value, 1e7 * 0.02);
    }

    /// <summary>
    /// Pulse width is not the duty cycle. A quarter duty at one kilohertz is a quarter millisecond
    /// high; the same quarter duty at two kilohertz is half that, and a reset line is specified in
    /// microseconds rather than in percent.
    /// </summary>
    [Theory]
    [InlineData(1000.0, 0.25, 250e-6)]
    [InlineData(2000.0, 0.25, 125e-6)]
    [InlineData(1000.0, 0.60, 600e-6)]
    public void PulseWidthIsTimeRatherThanFraction(double hertz, double duty, double expected)
    {
        var samples = Sample(5e-3, 50_000, t =>
        {
            var phase = (t * hertz) - Math.Floor(t * hertz);
            return phase < duty ? 1.0 : 0.0;
        });

        var measured = TraceMeasurements.OfAll(samples);

        Assert.Equal(expected, measured.PulseWidth!.Value, expected * 0.02);

        // And the duty cycle still says what it always said, so the two are not the same reading
        // under two names.
        Assert.Equal(duty, measured.DutyCycle!.Value, 0.02);
    }

    /// <summary>
    /// An edge that happens entirely between two samples has no measurable time, and gets none.
    /// <para>
    /// The alternative would be to interpolate one, and the number that came out would be a
    /// statement about the sample interval rather than about the circuit — "this edge took about
    /// as long as one step of the solver", dressed as a measurement. What is true is that the edge
    /// is faster than anything here can resolve, and null is how that is said.
    /// </para>
    /// </summary>
    [Fact]
    public void AnEdgeFasterThanTheSamplingHasNoMeasuredTime()
    {
        var instant = TraceMeasurements.OfAll(Sample(5e-3, 5000, t =>
            ((t * 1000) - Math.Floor(t * 1000)) < 0.4 ? 1.0 : 0.0));

        Assert.Null(instant.RiseTime);
        Assert.Null(instant.FallTime);
        Assert.Null(instant.SlewRate);

        // The same wave with edges wide enough to land samples on does have them.
        var resolved = TraceMeasurements.OfAll(Sample(5e-3, 50_000, t => Trapezoid(t, 1000.0, 0.4, 2e-5)));

        Assert.NotNull(resolved.RiseTime);
        Assert.NotNull(resolved.FallTime);
    }

    /// <summary>Every new quantity reads back in words rather than as its own enum name.</summary>
    [Theory]
    [InlineData(SpecQuantity.FallTime, "fall time")]
    [InlineData(SpecQuantity.Overshoot, "overshoot")]
    [InlineData(SpecQuantity.SettlingTime, "settling time")]
    [InlineData(SpecQuantity.PulseWidth, "pulse width")]
    [InlineData(SpecQuantity.SlewRate, "slew rate")]
    public void EveryNewQuantityHasWords(SpecQuantity quantity, string words) =>
        Assert.Equal(words, SpecWords.Of(quantity));

    /// <summary>
    /// And every one of them is reachable through a requirement, on a waveform that has it.
    /// <para>
    /// A waveform per quantity, rather than one waveform for all five, because no single waveform
    /// has all five: a rising step has no falling edge and no pulse, and a square wave never
    /// settles anywhere. Asking a step for its pulse width and expecting a number was this test's
    /// own mistake before it was written this way round.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(SpecQuantity.FallTime, false)]
    [InlineData(SpecQuantity.Overshoot, true)]
    [InlineData(SpecQuantity.SettlingTime, true)]
    [InlineData(SpecQuantity.PulseWidth, false)]
    [InlineData(SpecQuantity.SlewRate, true)]
    public void EveryNewQuantityIsReadableOnAWaveformThatHasIt(SpecQuantity quantity, bool useStep)
    {
        var samples = useStep
            // A settling underdamped step: it overshoots, it settles, and it has a rising edge.
            ? Sample(0.05, 20_000, t =>
                1.0 - (Math.Exp(-0.3 * 1000 * t) *
                       (Math.Cos(954 * t) + (0.3 / Math.Sqrt(1 - 0.09) * Math.Sin(954 * t)))))
            // A trapezoidal wave: it has pulses and falling edges wide enough to measure, and it
            // settles at neither level. Edges of a finite width rather than instant ones, because
            // an instant edge has no measurable time — see below.
            : Sample(5e-3, 50_000, t => Trapezoid(t, 1000.0, 0.4, 2e-5));

        Assert.NotNull(SpecCheck.Read(quantity, TraceMeasurements.OfAll(samples)));
    }

    /// <summary>
    /// A requirement written in one of the new quantities is checked like any other, and a settling
    /// time under a millisecond either passes or does not.
    /// </summary>
    [Fact]
    public void ARequirementInANewQuantityIsChecked()
    {
        var samples = Sample(0.02, 20_000, t => 1.0 - Math.Exp(-t / 1e-3));

        var measured = TraceMeasurements.OfAll(samples);

        var tight = SpecCheck.Evaluate(
            new DesignSpec
            {
                Trace = "out",
                Quantity = SpecQuantity.SettlingTime,
                Comparison = SpecComparison.AtMost,
                Limit = 1e-3,
                Unit = "s",
            },
            measured);

        var loose = SpecCheck.Evaluate(
            new DesignSpec
            {
                Trace = "out",
                Quantity = SpecQuantity.SettlingTime,
                Comparison = SpecComparison.AtMost,
                Limit = 10e-3,
                Unit = "s",
            },
            measured);

        // It settles in about 3.9 ms, so one of those holds and the other does not.
        Assert.False(tight.Passed);
        Assert.True(loose.Passed);
    }
}
