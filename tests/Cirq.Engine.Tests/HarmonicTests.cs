using Cirq.Core.Primitives;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// Measuring distortion, against signals whose distortion is known exactly because they were
/// built out of the harmonics being looked for.
/// <para>
/// This is the number an amplifier is sold on and it is invisible on a screen: a stage at one
/// percent looks like a sine. Every answer here is checked against the arithmetic of the signal
/// that was synthesised, never against what the code happened to print.
/// </para>
/// </summary>
public class HarmonicTests
{
    private const double Rate = 48000.0;

    /// <summary>Points in the transform.</summary>
    private const int Size = 16384;

    /// <summary>
    /// A frequency that lands exactly on a bin centre, given a block of <see cref="Size"/> points
    /// at <see cref="Rate"/>. Used so the arithmetic being checked is the harmonic analysis rather
    /// than the transform's own resolution: a tone between two bins is a real and separate
    /// question, asked by its own test below.
    /// </summary>
    private static double OnBin(int bin) => bin * Rate / Size;

    /// <summary>
    /// A signal made of stated harmonics.
    /// <para>
    /// One sample longer than the transform, deliberately. The spectrum resamples onto a uniform
    /// grid spanning first sample to last, so a block of exactly <c>Size</c> samples gives a grid
    /// step a shade shorter than the sample step, and every point is then a linear blend of two
    /// neighbours — which is a gentle low-pass, and attenuates a harmonic more than it attenuates
    /// the fundamental. With <c>Size + 1</c> samples the grid lands on the samples and the
    /// interpolation is exact.
    /// </para>
    /// </summary>
    private static List<DataPoint> Signal(
        double fundamental, params (int Order, double Amplitude)[] parts) =>
        Sampled(Size + 1, t =>
        {
            var value = 0.0;

            foreach (var (order, amplitude) in parts)
                value += amplitude * Math.Sin(2 * Math.PI * fundamental * order * t);

            return value;
        });

    private static List<DataPoint> Sampled(int count, Func<double, double> f) =>
        [.. Enumerable.Range(0, count).Select(i => new DataPoint(i / Rate, f(i / Rate)))];

    private static HarmonicAnalysis Measure(
        IReadOnlyList<DataPoint> samples, double? fundamental = null, int count = 9,
        SpectrumWindow window = SpectrumWindow.Hann)
    {
        var spectrum = Spectrum.Of(
            samples, samples[0].Time, samples[^1].Time,
            new SpectrumRequest(window, Size));

        return Harmonics.Of(spectrum, fundamental, count);
    }

    // ---- the fundamental ---------------------------------------------------

    [Fact]
    public void APureSineHasTheAmplitudeAndFrequencyItWasBuiltWith()
    {
        var result = Measure(Signal(OnBin(341), (1, 2.0)));

        Assert.True(result.IsUsable, result.Problem);
        Assert.Equal(OnBin(341), result.Fundamental, 0.5);
        Assert.Equal(2.0, result.FundamentalAmplitude, 0.01);
    }

    /// <summary>
    /// The amplitude comes out right whichever window was used. It is recovered from the energy
    /// across the lobe rather than from the tallest bin, so the window's own spreading divides
    /// back out.
    /// </summary>
    [Theory]
    [InlineData(SpectrumWindow.Rectangular)]
    [InlineData(SpectrumWindow.Hann)]
    [InlineData(SpectrumWindow.BlackmanHarris)]
    public void TheAmplitudeDoesNotDependOnTheWindow(SpectrumWindow window)
    {
        var result = Measure(Signal(OnBin(341), (1, 2.0)), window: window);

        Assert.Equal(2.0, result.FundamentalAmplitude, 0.02);
    }

    // ---- the distortion figure ---------------------------------------------

    /// <summary>
    /// A pure sine has no distortion. Not exactly zero — the window has skirts and the arithmetic
    /// is finite — but far below anything a circuit would show.
    /// </summary>
    [Fact]
    public void APureSineMeasuresAsUndistorted()
    {
        var result = Measure(Signal(OnBin(341), (1, 1.0)));

        Assert.True(result.ThdPercent < 0.01, $"{result.ThdPercent:0.0000}% is not clean");
    }

    /// <summary>
    /// One harmonic at a tenth of the fundamental is ten percent distortion, by the definition of
    /// the word.
    /// </summary>
    [Fact]
    public void OneHarmonicAtATenthIsTenPercent()
    {
        var result = Measure(Signal(OnBin(341), (1, 1.0), (2, 0.1)));

        Assert.Equal(10.0, result.ThdPercent, 0.05);
    }

    /// <summary>
    /// Harmonics add in quadrature, not linearly: 3 % and 4 % is 5 %, because it is the powers
    /// that add.
    /// </summary>
    [Fact]
    public void HarmonicsAddInQuadrature()
    {
        var result = Measure(Signal(OnBin(341), (1, 1.0), (2, 0.03), (3, 0.04)));

        Assert.Equal(5.0, result.ThdPercent, 0.05);
    }

    /// <summary>Each harmonic is reported at the amplitude it was built with.</summary>
    [Fact]
    public void EachHarmonicComesBackAtItsOwnAmplitude()
    {
        var result = Measure(Signal(OnBin(341), (1, 1.0), (2, 0.05), (3, 0.02), (5, 0.01)));

        double At(int order) => result.Harmonics.Single(h => h.Order == order).Amplitude;

        Assert.Equal(1.00, At(1), 0.005);
        Assert.Equal(0.05, At(2), 0.005);
        Assert.Equal(0.02, At(3), 0.005);
        Assert.Equal(0.01, At(5), 0.005);

        // And the ones that were never there are not there.
        Assert.True(At(4) < 0.002, $"H4 should be nothing, not {At(4):0.#####}");
    }

    /// <summary>
    /// Frequencies land on the multiples they are supposed to — for the harmonics that are
    /// actually there. The frequency of a component is where its energy is, so a harmonic that
    /// was never in the signal has no frequency to report and its amplitude is what says so.
    /// </summary>
    [Fact]
    public void TheHarmonicsAreAtMultiplesOfTheFundamental()
    {
        var result = Measure(Signal(OnBin(341), (1, 1.0), (2, 0.1), (3, 0.1)));

        var present = result.Harmonics.Where(h => h.Relative > 0.01).ToList();

        Assert.Equal([1, 2, 3], present.Select(h => h.Order));

        foreach (var harmonic in present)
            Assert.Equal(OnBin(341) * harmonic.Order, harmonic.Frequency, 0.5);
    }

    /// <summary>A harmonic a tenth of the fundamental is 20 dB below it.</summary>
    [Fact]
    public void TheDecibelFigureIsRelativeToTheFundamental()
    {
        var result = Measure(Signal(OnBin(341), (1, 1.0), (2, 0.1)));

        Assert.Equal(0.0, result.Harmonics.Single(h => h.Order == 1).Decibels, 0.05);
        Assert.Equal(-20.0, result.Harmonics.Single(h => h.Order == 2).Decibels, 0.05);
    }

    // ---- which harmonics, which fault --------------------------------------

    /// <summary>
    /// A square wave is the textbook case: odd harmonics only, the nth at 1/n of the fundamental,
    /// and the fundamental itself 4/π times the wave's own amplitude.
    /// </summary>
    [Fact]
    public void ASquareWaveHasOddHarmonicsFallingAsOneOverN()
    {
        var result = Measure(
            Sampled(Size + 1, t => Math.Sign(Math.Sin(2 * Math.PI * OnBin(341) * t))), OnBin(341));

        Assert.Equal(4.0 / Math.PI, result.FundamentalAmplitude, 0.01);

        foreach (var harmonic in result.Harmonics)
        {
            if (harmonic.IsEven)
            {
                Assert.True(harmonic.Relative < 0.01,
                    $"H{harmonic.Order} should be absent, not {harmonic.Relative:0.####}");
                continue;
            }

            Assert.Equal(1.0 / harmonic.Order, harmonic.Relative, 0.01);
        }
    }

    /// <summary>
    /// A symmetric distortion makes odd harmonics and a lopsided one makes even harmonics, which
    /// is the whole diagnostic value of the list: both rails clipping looks different from one
    /// side running out of headroom.
    /// </summary>
    [Fact]
    public void ClippingBothSidesMakesOddHarmonicsAndClippingOneMakesEven()
    {
        const int size = 16384;
        List<DataPoint> symmetric = [];
        List<DataPoint> lopsided = [];

        for (var i = 0; i < size; i++)
        {
            var t = i / Rate;
            var v = 1.5 * Math.Sin(2 * Math.PI * 1000 * t);

            symmetric.Add(new DataPoint(t, Math.Clamp(v, -1.0, 1.0)));
            lopsided.Add(new DataPoint(t, Math.Min(v, 1.0)));
        }

        var both = Measure(symmetric, 1000);
        var one = Measure(lopsided, 1000);

        double Even(HarmonicAnalysis a) =>
            a.Harmonics.Where(h => h.IsEven).Sum(h => h.Relative * h.Relative);

        Assert.True(Even(both) < Even(one) / 10,
            $"clipping both sides should be almost free of even harmonics: " +
            $"{Even(both):0.######} against {Even(one):0.######}");

        Assert.True(both.ThdPercent > 1, $"clipping should distort, not {both.ThdPercent:0.##}%");
    }

    // ---- THD+N -------------------------------------------------------------

    /// <summary>
    /// With nothing but harmonics present the two figures agree, because the harmonics <i>are</i>
    /// everything that is not the fundamental.
    /// </summary>
    [Fact]
    public void WithOnlyHarmonicsPresentThdAndThdPlusNoiseAgree()
    {
        var result = Measure(Signal(OnBin(341), (1, 1.0), (2, 0.05), (3, 0.03)));

        Assert.Equal(result.Thd, result.ThdPlusNoise, 0.002);
    }

    /// <summary>
    /// Put something in that is not a harmonic and only THD+N sees it — which is the reason to
    /// have both. A tone at 3.1 times the fundamental is what an intermodulation product looks
    /// like, and the harmonic list steps straight over it.
    /// </summary>
    [Fact]
    public void SomethingThatIsNotAHarmonicShowsUpOnlyInThdPlusNoise()
    {
        var result = Measure(
            Sampled(Size + 1, t =>
                Math.Sin(2 * Math.PI * OnBin(341) * t)
                + (0.1 * Math.Sin(2 * Math.PI * OnBin(1057) * t))),
            OnBin(341));

        Assert.True(result.ThdPercent < 1.0, $"no harmonics, so THD should be small: {result.ThdPercent:0.##}%");
        Assert.Equal(10.0, result.ThdPlusNoisePercent, 0.5);
    }

    // ---- refusing to mislead -----------------------------------------------

    /// <summary>
    /// Above the Nyquist limit there is nothing to count, and what is up there has folded back
    /// into the band as something it is not. The result says it was truncated rather than passing
    /// off a partial sum as the answer.
    /// </summary>
    [Fact]
    public void RunningOutOfBandIsReportedRatherThanIgnored()
    {
        // 10 kHz at 48 kHz sampling: the second harmonic is in, the third is not.
        var result = Measure(Signal(OnBin(3413), (1, 1.0), (2, 0.1)), OnBin(3413));

        Assert.True(result.IsTruncated);
        Assert.True(result.HarmonicsInBand < result.HarmonicsRequested);
    }

    /// <summary>
    /// A fundamental only a few bins up cannot be separated from its own neighbours, and an answer
    /// worked out from overlapping lobes would be arithmetic rather than a measurement.
    /// </summary>
    [Fact]
    public void AFundamentalTooLowToResolveIsRefused()
    {
        var samples = Signal(OnBin(341), (1, 1.0));

        var spectrum = Spectrum.Of(
            samples, samples[0].Time, samples[^1].Time, new SpectrumRequest(SpectrumWindow.Hann, Size));

        // One bin up, which is inside the DC term's own skirt.
        var result = Harmonics.Of(spectrum, spectrum.Resolution);

        Assert.False(result.IsUsable);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void AFlatTraceIsRefusedRatherThanMeasured()
    {
        var result = Measure(Sampled(4096, _ => 0.0));

        Assert.False(result.IsUsable);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void NothingRecordedIsRefusedRatherThanMeasured()
    {
        var result = Harmonics.Of(SpectrumResult.Empty);

        Assert.False(result.IsUsable);
        Assert.NotNull(result.Problem);
    }

    /// <summary>
    /// The fundamental can be stated rather than guessed, and stating it matters: at heavy
    /// distortion a harmonic can be the largest thing in the spectrum, and measuring everything
    /// against the wrong one is worse than not measuring at all.
    /// </summary>
    [Fact]
    public void StatingTheFundamentalBeatsGuessingItWhenAHarmonicIsBigger()
    {
        var samples = Signal(OnBin(341), (1, 0.2), (2, 1.0));

        var guessed = Measure(samples);
        var stated = Measure(samples, OnBin(341));

        Assert.Equal(OnBin(682), guessed.Fundamental, 0.5);
        Assert.Equal(OnBin(341), stated.Fundamental, 0.5);

        // Against the real fundamental the second harmonic is five times bigger: 500 %.
        Assert.Equal(500.0, stated.ThdPercent, 5.0);
    }

    // ---- what a real capture costs -----------------------------------------

    /// <summary>
    /// The tests above put every tone exactly on a bin centre, which a real capture never does:
    /// you stop the run when you stop it, and 1 kHz over that stretch lands somewhere between two
    /// bins. This is the same measurement made deliberately awkwardly, and it says what that
    /// costs — a fraction of a percent, in a known direction.
    /// <para>
    /// Two things cause it. The lobe straddles bins, which is handled by summing a bin wider than
    /// the window's own lobe. And the solver's uneven time steps are resampled onto a uniform grid
    /// by interpolating between neighbours, which is a gentle low-pass — so it takes a little more
    /// off a harmonic than off the fundamental, and the figure reads slightly <i>low</i>. Erring
    /// downwards on distortion is worth knowing about; a number that flattered a circuit by a
    /// percent would be worse than one that did not.
    /// </para>
    /// </summary>
    [Fact]
    public void AToneBetweenTwoBinsIsMeasuredToAFractionOfAPercent()
    {
        // Not a multiple of the bin spacing, and a block the grid cannot land on.
        var samples = Sampled(Size, t =>
            Math.Sin(2 * Math.PI * 1000 * t) + (0.1 * Math.Sin(2 * Math.PI * 2000 * t)));

        var result = Measure(samples, 1000);

        Assert.True(result.IsUsable, result.Problem);

        Assert.Equal(1000.0, result.Fundamental, 1.0);
        Assert.Equal(1.0, result.FundamentalAmplitude, 0.01);

        // Ten percent, to within a percent of itself, and never reading high.
        Assert.InRange(result.ThdPercent, 9.85, 10.0);
    }

    /// <summary>
    /// And the same tone measured with each window, which is the practical question: does it
    /// matter which one is chosen. It does not, for the two that taper — a rectangular window on
    /// a block that does not hold whole cycles leaks across the whole spectrum, which is exactly
    /// what the window documentation says it is for and not for.
    /// </summary>
    [Theory]
    [InlineData(SpectrumWindow.Hann)]
    [InlineData(SpectrumWindow.BlackmanHarris)]
    public void ATaperedWindowMeasuresAnAwkwardToneAsWellAsATidyOne(SpectrumWindow window)
    {
        var samples = Sampled(Size, t =>
            Math.Sin(2 * Math.PI * 1000 * t) + (0.1 * Math.Sin(2 * Math.PI * 2000 * t)));

        var result = Measure(samples, 1000, window: window);

        Assert.Equal(10.0, result.ThdPercent, 0.2);
    }
}
