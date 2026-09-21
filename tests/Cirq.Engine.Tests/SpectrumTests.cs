using System.Numerics;
using Cirq.Core.Primitives;
using Cirq.Engine.Numerics;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// The FFT and the spectrum built on it, checked against signals whose content is known exactly.
/// </summary>
public class SpectrumTests
{
    private static List<DataPoint> Signal(Func<double, double> f, double seconds, double rate)
    {
        List<DataPoint> points = [];
        var step = 1.0 / rate;

        for (var t = 0.0; t <= seconds; t += step) points.Add(new DataPoint(t, f(t)));

        return points;
    }

    private static int BinNear(SpectrumResult s, double hertz)
    {
        var best = 0;
        for (var i = 1; i < s.Frequencies.Count; i++)
            if (Math.Abs(s.Frequencies[i] - hertz) < Math.Abs(s.Frequencies[best] - hertz)) best = i;

        return best;
    }

    /// <summary>Amplitude at a frequency, taken over the bins a windowed peak is spread across.</summary>
    private static double AmplitudeAt(SpectrumResult s, double hertz)
    {
        var centre = BinNear(s, hertz);
        var best = 0.0;

        for (var i = Math.Max(0, centre - 2); i <= Math.Min(s.Magnitudes.Count - 1, centre + 2); i++)
            best = Math.Max(best, s.Magnitudes[i]);

        return best;
    }

    // ---- the transform itself ---------------------------------------------

    [Fact]
    public void TheTransformOfAConstantIsAllInTheFirstBin()
    {
        var values = new Complex[8];
        Array.Fill(values, new Complex(1.0, 0.0));

        Fourier.Transform(values);

        Assert.Equal(8.0, values[0].Magnitude, 9);
        for (var i = 1; i < 8; i++) Assert.Equal(0.0, values[i].Magnitude, 9);
    }

    [Fact]
    public void ALengthThatIsNotAPowerOfTwoIsRefusedRatherThanQuietlyWrong()
    {
        Assert.Throws<ArgumentException>(() => Fourier.Transform(new Complex[6]));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 2)]
    [InlineData(1000, 512)]
    [InlineData(4096, 4096)]
    public void ThePowerOfTwoHelperRoundsDown(int given, int expected)
    {
        Assert.Equal(expected, Fourier.PowerOfTwoAtMost(given));
    }

    // ---- amplitude ---------------------------------------------------------

    /// <summary>
    /// The scaling test, and the one that makes the plot a measurement rather than a shape: a two
    /// volt sine has to read two volts at its own frequency.
    /// </summary>
    [Theory]
    [InlineData(SpectrumWindow.Hann)]
    [InlineData(SpectrumWindow.BlackmanHarris)]
    [InlineData(SpectrumWindow.Rectangular)]
    public void ASineReadsItsOwnAmplitudeAtItsOwnFrequency(SpectrumWindow window)
    {
        var samples = Signal(t => 2.0 * Math.Sin(2 * Math.PI * 1e3 * t), 0.2, 100e3);

        var s = Spectrum.Of(samples, 0, 0.2, new SpectrumRequest(window, 8192));

        Assert.False(s.IsEmpty);
        Assert.Equal(2.0, AmplitudeAt(s, 1e3), 0.15);
    }

    [Fact]
    public void TheDcTermLandsInTheFirstBinAtItsOwnValue()
    {
        var samples = Signal(t => 3.3 + (0.5 * Math.Sin(2 * Math.PI * 2e3 * t)), 0.1, 100e3);

        var s = Spectrum.Of(samples, 0, 0.1, new SpectrumRequest(SpectrumWindow.Hann, 4096));

        Assert.Equal(3.3, s.Magnitudes[0], 0.1);
        Assert.Equal(0.5, AmplitudeAt(s, 2e3), 0.08);
    }

    [Fact]
    public void ThePeakIsFoundAboveDcRatherThanBeingTheDcTerm()
    {
        // A big DC term and a small tone: the peak that matters is the tone.
        var samples = Signal(t => 5.0 + (0.4 * Math.Sin(2 * Math.PI * 3e3 * t)), 0.1, 100e3);

        var s = Spectrum.Of(samples, 0, 0.1, new SpectrumRequest(SpectrumWindow.Hann, 4096));

        var peak = s.Peak();

        Assert.NotNull(peak);
        Assert.Equal(3e3, peak!.Value.Frequency, 60.0);
    }

    // ---- what the spectrum is actually for ---------------------------------

    /// <summary>
    /// A square wave is its fundamental plus odd harmonics at 1/3, 1/5, 1/7 of the amplitude, and
    /// no even ones at all. It is the textbook series, and it is the thing a spectrum exists to
    /// make visible.
    /// </summary>
    [Fact]
    public void ASquareWaveShowsItsOddHarmonicsAndNoEvenOnes()
    {
        var samples = Signal(
            t => Math.Sin(2 * Math.PI * 1e3 * t) >= 0 ? 1.0 : -1.0, 0.2, 200e3);

        var s = Spectrum.Of(samples, 0, 0.2, new SpectrumRequest(SpectrumWindow.Hann, 16384));

        var fundamental = AmplitudeAt(s, 1e3);

        // 4/pi for a unit square wave.
        Assert.Equal(4.0 / Math.PI, fundamental, 0.06);

        // Odd harmonics fall as 1/n.
        Assert.Equal(fundamental / 3.0, AmplitudeAt(s, 3e3), fundamental * 0.06);
        Assert.Equal(fundamental / 5.0, AmplitudeAt(s, 5e3), fundamental * 0.06);
        Assert.Equal(fundamental / 7.0, AmplitudeAt(s, 7e3), fundamental * 0.06);

        // And the even ones are simply not there.
        Assert.True(AmplitudeAt(s, 2e3) < fundamental * 0.05);
        Assert.True(AmplitudeAt(s, 4e3) < fundamental * 0.05);
    }

    /// <summary>
    /// Amplitude modulation puts a pair of sidebands either side of the carrier, spaced by the
    /// modulating frequency, each at half the depth. Seeing that is the whole reason the AM
    /// example exists, and until now it was invisible.
    /// </summary>
    [Fact]
    public void AModulatedCarrierHasSidebandsEitherSideOfIt()
    {
        const double carrier = 20e3;
        const double audio = 1e3;
        const double depth = 0.5;

        var samples = Signal(
            t => (1 + (depth * Math.Sin(2 * Math.PI * audio * t))) * Math.Sin(2 * Math.PI * carrier * t),
            0.05, 500e3);

        var s = Spectrum.Of(samples, 0, 0.05, new SpectrumRequest(SpectrumWindow.BlackmanHarris, 16384));

        var centre = AmplitudeAt(s, carrier);
        var lower = AmplitudeAt(s, carrier - audio);
        var upper = AmplitudeAt(s, carrier + audio);

        Assert.Equal(1.0, centre, 0.12);

        // Each sideband is half the modulation depth of the carrier.
        Assert.Equal(depth / 2.0, lower, 0.06);
        Assert.Equal(depth / 2.0, upper, 0.06);

        // And nothing at twice the spacing, which would mean distortion rather than modulation.
        Assert.True(AmplitudeAt(s, carrier + (2 * audio)) < 0.03);
    }

    /// <summary>
    /// The reason a window is applied at all. A tone that does not fit a whole number of times
    /// into the block smears across the whole spectrum without one.
    /// </summary>
    [Fact]
    public void WindowingKeepsAnAwkwardToneFromSmearingAcrossEverything()
    {
        // Deliberately not a whole number of cycles in the block.
        var samples = Signal(t => Math.Sin(2 * Math.PI * 1234.5 * t), 0.1, 100e3);

        var plain = Spectrum.Of(samples, 0, 0.1, new SpectrumRequest(SpectrumWindow.Rectangular, 4096));
        var hann = Spectrum.Of(samples, 0, 0.1, new SpectrumRequest(SpectrumWindow.Hann, 4096));

        // Far from the tone, where there should be nothing at all.
        var farPlain = AmplitudeAt(plain, 20e3);
        var farHann = AmplitudeAt(hann, 20e3);

        Assert.True(farHann < farPlain / 10.0,
            $"windowing left {farHann:G3} against {farPlain:G3} unwindowed");
    }

    // ---- the awkward bits --------------------------------------------------

    /// <summary>
    /// The solver's time steps are not uniform, so the samples are resampled before transforming.
    /// This is the test that the resampling does not invent or lose the signal.
    /// </summary>
    [Fact]
    public void UnevenlySpacedSamplesStillGiveTheRightFrequency()
    {
        var random = new Random(7);

        List<DataPoint> samples = [];
        var t = 0.0;

        while (t < 0.1)
        {
            samples.Add(new DataPoint(t, 1.5 * Math.Sin(2 * Math.PI * 2e3 * t)));

            // Steps varying over a factor of five, as a solver's do around an edge.
            t += 1e-6 * (0.5 + (random.NextDouble() * 2.0));
        }

        var s = Spectrum.Of(samples, 0, 0.1, new SpectrumRequest(SpectrumWindow.Hann, 8192));

        var peak = s.Peak();

        Assert.NotNull(peak);
        Assert.Equal(2e3, peak!.Value.Frequency, 30.0);
        Assert.Equal(1.5, peak.Value.Magnitude, 0.15);
    }

    [Fact]
    public void TheBinsRunFromDcToNyquistAndSayWhatTheirResolutionIs()
    {
        var samples = Signal(t => Math.Sin(2 * Math.PI * 1e3 * t), 0.1, 50e3);

        var s = Spectrum.Of(samples, 0, 0.1, new SpectrumRequest(SpectrumWindow.Hann, 1024));

        Assert.Equal(0.0, s.Frequencies[0], 9);
        Assert.Equal(s.Nyquist, s.Frequencies[^1], s.Resolution);
        Assert.True(s.Resolution > 0);

        // Half the transform size plus the DC bin.
        Assert.Equal((1024 / 2) + 1, s.Frequencies.Count);
    }

    [Fact]
    public void TooLittleSignalGivesNothingRatherThanNonsense()
    {
        Assert.True(Spectrum.Of([], 0, 1).IsEmpty);
        Assert.True(Spectrum.Of([new DataPoint(0, 1), new DataPoint(1, 2)], 0, 1).IsEmpty);

        // A window with no samples in it.
        var samples = Signal(t => Math.Sin(t), 0.01, 10e3);
        Assert.True(Spectrum.Of(samples, 5, 6).IsEmpty);
    }

    [Fact]
    public void TheTransformIsNoLargerThanTheSamplesSupport()
    {
        // 600 samples asked to fill a 4096-point transform: it uses 512 rather than inventing
        // resolution the data does not contain.
        var samples = Signal(t => Math.Sin(2 * Math.PI * 100 * t), 0.06, 10e3);

        var s = Spectrum.Of(samples, 0, 0.06, new SpectrumRequest(SpectrumWindow.Hann, 4096));

        Assert.True(s.Frequencies.Count <= (512 / 2) + 1,
            $"transformed {s.Frequencies.Count} bins from {samples.Count} samples");
    }
}
