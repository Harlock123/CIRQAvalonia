using System.Numerics;
using Cirq.Core.Primitives;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>
/// How the samples are weighted before the transform.
/// <para>
/// This matters more than it sounds. A transform assumes the block of samples repeats for ever,
/// and unless the block happens to hold a whole number of cycles the two ends do not meet — the
/// discontinuity is a step, a step has energy at every frequency, and that energy smears across
/// the whole spectrum and buries anything small. Tapering the ends to zero removes it.
/// </para>
/// </summary>
public enum SpectrumWindow
{
    /// <summary>
    /// No weighting. Right only when the block holds exactly a whole number of cycles, which in
    /// practice means a synthetic signal you chose the block length for.
    /// </summary>
    Rectangular,

    /// <summary>
    /// A raised cosine. The general-purpose choice, and what this uses unless told otherwise: it
    /// costs a little resolution and buys about sixty decibels of freedom from leakage, which is
    /// the difference between seeing a modulation sideband and not.
    /// </summary>
    Hann,

    /// <summary>
    /// Wider still in the main lobe but with the lowest sidelobes of the three, for picking a
    /// small component out from beside a large one.
    /// </summary>
    BlackmanHarris,
}

/// <summary>What a spectrum was asked for.</summary>
/// <param name="Window">How the block is weighted.</param>
/// <param name="Size">
/// Points in the transform, rounded down to a power of two. More is finer resolution and a longer
/// stretch of signal; less follows a changing signal more closely.
/// </param>
public sealed record SpectrumRequest(SpectrumWindow Window = SpectrumWindow.Hann, int Size = 4096);

/// <summary>
/// One trace's spectrum: what frequencies are in it and how much of each.
/// </summary>
/// <param name="Frequencies">Bin centres in hertz, from DC up to the Nyquist limit.</param>
/// <param name="Magnitudes">
/// Amplitude in the trace's own units — a one volt sine reads one volt at its own frequency, not
/// a half or a two or an arbitrary scaling. Getting that right is what makes the plot readable as
/// a measurement rather than as a shape.
/// </param>
/// <param name="SampleRate">The uniform rate the samples were resampled onto, in hertz.</param>
/// <param name="FirstCleanBin">
/// The first bin past the DC term's own main lobe. A window does not put DC in one bin — it
/// spreads it over the width of its main lobe, which for a Blackman-Harris is four bins either
/// side. On a single-supply circuit the DC term is usually the largest thing in the spectrum, so
/// anything inside that lobe is DC's skirt rather than a component of the signal.
/// </param>
/// <param name="NoiseBandwidth">
/// The window's equivalent noise bandwidth, in bins: the mean of its squared weights over the
/// square of their mean. One for a rectangular window, 1.5 for a Hann, about 2 for a
/// Blackman-Harris.
/// <para>
/// It is here because a windowed sine does not sit in one bin, so recovering its amplitude means
/// adding up the power across its lobe — and that sum comes out this many times too large. Anyone
/// measuring a component by its energy rather than by its peak needs the number to divide back
/// out.
/// </para>
/// </param>
public sealed record SpectrumResult(
    IReadOnlyList<double> Frequencies,
    IReadOnlyList<double> Magnitudes,
    double SampleRate,
    int FirstCleanBin = 1,
    double NoiseBandwidth = 1.0)
{
    /// <summary>
    /// Half the width of a window's main lobe, in bins — so a component at bin <c>m</c> occupies
    /// <c>m ± this</c>, and two components closer together than twice it cannot be told apart.
    /// </summary>
    public int LobeHalfWidth => Math.Max(FirstCleanBin - 1, 1);

    /// <summary>How the block was weighted, named from the lobe width it left behind.</summary>
    public string Window() => LobeHalfWidth switch
    {
        <= 1 => "rectangular",
        2 => "Hann",
        _ => "Blackman-Harris",
    };

    /// <summary>Spacing between bins, which is the resolution.</summary>
    public double Resolution => Frequencies.Count > 1 ? Frequencies[1] - Frequencies[0] : 0;

    /// <summary>Highest frequency the block can describe.</summary>
    public double Nyquist => SampleRate / 2.0;

    /// <summary>True when there was not enough signal to transform.</summary>
    public bool IsEmpty => Frequencies.Count == 0;

    /// <summary>Magnitude in decibels relative to one unit, floored so a null is not minus infinity.</summary>
    public double Decibels(int index) => 20.0 * Math.Log10(Math.Max(Magnitudes[index], 1e-12));

    /// <summary>
    /// The strongest component above DC, which is the number most people open a spectrum for:
    /// "what frequency is this". Null when there is nothing but DC.
    /// </summary>
    public (double Frequency, double Magnitude)? Peak()
    {
        var best = -1;

        // Past the DC lobe. Bin 0 is the DC term, and the bins immediately after it are that same
        // term smeared by the window — on a 5 V rail carrying a 0.4 V ripple, bin 1 is a couple of
        // volts of DC skirt and picking it as the peak would report the rail rather than the
        // ripple.
        for (var i = Math.Max(FirstCleanBin, 1); i < Magnitudes.Count; i++)
            if (best < 0 || Magnitudes[i] > Magnitudes[best]) best = i;

        return best < 0 ? null : (Frequencies[best], Magnitudes[best]);
    }

    public static SpectrumResult Empty => new([], [], 0);
}

/// <summary>
/// Turns a trace into a spectrum.
/// <para>
/// Half of what this library teaches lives in the frequency domain and has been invisible until
/// now. A modulated carrier has sidebands; a rectifier's output has harmonics at multiples of the
/// line frequency; a filter's job is described entirely by what it does to each one; the noise
/// source is defined by its spectrum and band-limiting; an oscillator's purity is the thing that
/// is either there or not. A picture of the waveform shows none of that.
/// </para>
/// <para>
/// The awkward part is that the solver's time steps are <b>not uniform</b> — it shortens them at
/// an edge and lengthens them across a flat stretch, and a transform assumes an even spacing. So
/// the samples are resampled onto a uniform grid first, by interpolating between the ones either
/// side. That is honest as long as the grid is finer than the detail in the signal, which it is
/// whenever the solver was taking steps small enough to draw the waveform in the first place.
/// </para>
/// </summary>
public static class Spectrum
{
    /// <summary>
    /// Transforms the samples between two times.
    /// </summary>
    public static SpectrumResult Of(
        IReadOnlyList<DataPoint> samples, double from, double to, SpectrumRequest? request = null)
    {
        request ??= new SpectrumRequest();

        if (samples.Count < 4 || to <= from) return SpectrumResult.Empty;

        // Only what is inside the window, and only if there is enough of it to be worth a
        // transform. A handful of points has no spectrum worth the name.
        var first = -1;
        var last = -1;

        for (var i = 0; i < samples.Count; i++)
        {
            if (samples[i].Time < from) continue;
            if (samples[i].Time > to) break;

            if (first < 0) first = i;
            last = i;
        }

        if (first < 0 || last - first < 3) return SpectrumResult.Empty;

        var available = last - first + 1;

        // A transform no larger than the data supports: padding out to a fixed size would invent
        // resolution the samples do not contain.
        var size = Math.Min(
            Fourier.PowerOfTwoAtMost(request.Size),
            Fourier.PowerOfTwoAtMost(available));

        if (size < 4) return SpectrumResult.Empty;

        var start = samples[first].Time;
        var span = samples[last].Time - start;

        if (span <= 0) return SpectrumResult.Empty;

        var step = span / size;
        var sampleRate = 1.0 / step;

        var block = new Complex[size];
        var cursor = first;

        var coherentGain = 0.0;

        for (var i = 0; i < size; i++)
        {
            var time = start + (i * step);

            // The samples are in order, so walking a cursor forward is linear overall rather than
            // a search per point.
            while (cursor + 1 <= last && samples[cursor + 1].Time < time) cursor++;

            var value = Interpolate(samples, cursor, last, time);
            var weight = Weight(request.Window, i, size);

            coherentGain += weight;
            block[i] = new Complex(value * weight, 0.0);
        }

        // The window's average, which is how much it attenuated the signal. Dividing it back out
        // is what makes a one volt sine read one volt whichever window was used.
        coherentGain /= size;
        if (coherentGain <= 0) coherentGain = 1.0;

        // The window's equivalent noise bandwidth: mean of the squared weights over the square of
        // their mean. Carried on the result so a harmonic's amplitude can be recovered from the
        // power across its lobe rather than from whichever bin the peak happened to land in.
        var meanSquare = 0.0;
        for (var i = 0; i < size; i++)
        {
            var weight = Weight(request.Window, i, size);
            meanSquare += weight * weight;
        }

        meanSquare /= size;

        var noiseBandwidth = meanSquare / (coherentGain * coherentGain);

        Fourier.Transform(block);

        var bins = (size / 2) + 1;
        var frequencies = new double[bins];
        var magnitudes = new double[bins];

        for (var i = 0; i < bins; i++)
        {
            frequencies[i] = i * sampleRate / size;

            // Single-sided: every bin but DC and Nyquist has a mirror image in the upper half of
            // the transform, and the amplitude of the real sine is the two of them together.
            var mirrored = i > 0 && i < size / 2;
            var scale = (mirrored ? 2.0 : 1.0) / (size * coherentGain);

            magnitudes[i] = block[i].Magnitude * scale;
        }

        return new SpectrumResult(
            frequencies, magnitudes, sampleRate, FirstCleanBin(request.Window), noiseBandwidth);
    }

    /// <summary>
    /// The first bin clear of the DC term's main lobe, which is where each window's first null
    /// falls: one bin for a rectangular window, two for a Hann, four for a Blackman-Harris.
    /// </summary>
    private static int FirstCleanBin(SpectrumWindow window) => window switch
    {
        SpectrumWindow.Rectangular => 2,
        SpectrumWindow.Hann => 3,
        _ => 5,
    };

    private static double Interpolate(
        IReadOnlyList<DataPoint> samples, int index, int last, double time)
    {
        if (index >= last) return samples[last].Value;

        var a = samples[index];
        var b = samples[index + 1];
        var span = b.Time - a.Time;

        if (span <= 0) return b.Value;

        var fraction = Math.Clamp((time - a.Time) / span, 0.0, 1.0);

        return a.Value + (fraction * (b.Value - a.Value));
    }

    /// <summary>The window's weight at one position. Standard coefficients, written out.</summary>
    private static double Weight(SpectrumWindow window, int index, int size)
    {
        if (window == SpectrumWindow.Rectangular) return 1.0;

        var x = 2.0 * Math.PI * index / size;

        if (window == SpectrumWindow.Hann) return 0.5 - (0.5 * Math.Cos(x));

        // Four-term Blackman-Harris.
        return 0.35875
               - (0.48829 * Math.Cos(x))
               + (0.14128 * Math.Cos(2 * x))
               - (0.01168 * Math.Cos(3 * x));
    }
}
