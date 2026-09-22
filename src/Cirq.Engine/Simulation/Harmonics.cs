namespace Cirq.Engine.Simulation;

/// <summary>One component of a distorted signal: which multiple of the fundamental, and how big.</summary>
/// <param name="Order">1 for the fundamental, 2 for the second harmonic, and so on.</param>
/// <param name="Frequency">Where it actually is, in hertz.</param>
/// <param name="Amplitude">Its amplitude in the trace's own units.</param>
/// <param name="Relative">Its amplitude as a fraction of the fundamental's.</param>
public sealed record Harmonic(int Order, double Frequency, double Amplitude, double Relative)
{
    /// <summary>How far below the fundamental it sits, in decibels. Negative for anything smaller.</summary>
    public double Decibels => 20.0 * Math.Log10(Math.Max(Relative, 1e-12));

    /// <summary>
    /// Whether it is an even multiple. Worth separating, because the two kinds come from
    /// different faults: even harmonics mean the waveform is <b>asymmetric</b> — a single-ended
    /// stage clipping one side, a half-wave rectifier — and odd ones mean it is symmetric but not
    /// straight, which is what both rails clipping and a crossover notch look like.
    /// </summary>
    public bool IsEven => Order % 2 == 0;

    public override string ToString() =>
        $"H{Order} {Frequency:0.###} Hz  {Amplitude:0.#####}  ({Decibels:0.0} dBc)";
}

/// <summary>
/// What a harmonic analysis found.
/// </summary>
/// <param name="Fundamental">The frequency everything was measured against, in hertz.</param>
/// <param name="FundamentalAmplitude">Its amplitude in the trace's own units.</param>
/// <param name="Harmonics">The fundamental and its multiples, in order, as far as the band allows.</param>
/// <param name="Thd">
/// Total harmonic distortion as a fraction: the harmonics added in quadrature over the
/// fundamental. Multiply by 100 for the percentage everybody quotes.
/// </param>
/// <param name="ThdPlusNoise">
/// The same, but against <i>everything</i> that is not the fundamental — harmonics, noise,
/// intermodulation, hum, anything. Always the larger of the two, and the one a real distortion
/// analyser measures, because it does not need to know where to look.
/// </param>
/// <param name="HarmonicsInBand">How many multiples fitted below the Nyquist limit.</param>
/// <param name="HarmonicsRequested">How many were asked for.</param>
/// <param name="Problem">Why the answer should not be believed, or null when it can be.</param>
public sealed record HarmonicAnalysis(
    double Fundamental,
    double FundamentalAmplitude,
    IReadOnlyList<Harmonic> Harmonics,
    double Thd,
    double ThdPlusNoise,
    int HarmonicsInBand,
    int HarmonicsRequested,
    string? Problem = null)
{
    public bool IsUsable => Problem is null && Fundamental > 0;

    /// <summary>THD as the percentage a datasheet quotes.</summary>
    public double ThdPercent => Thd * 100.0;

    /// <summary>THD in decibels relative to the carrier, which is how RF and audio quote it.</summary>
    public double ThdDecibels => 20.0 * Math.Log10(Math.Max(Thd, 1e-12));

    public double ThdPlusNoisePercent => ThdPlusNoise * 100.0;

    /// <summary>
    /// True when the band ran out before the harmonics did, so the THD is a floor rather than a
    /// figure: whatever is above the Nyquist limit was not counted and cannot have been.
    /// </summary>
    public bool IsTruncated => HarmonicsInBand < HarmonicsRequested;

    public static HarmonicAnalysis Unusable(string problem) =>
        new(0, 0, [], 0, 0, 0, 0, problem);
}

/// <summary>
/// Measures distortion: drive a circuit with a sine, and find out what came back out.
/// <para>
/// This is the number an amplifier is sold on, and it is invisible on a waveform. A stage with one
/// percent distortion looks exactly like a sine on a screen — you cannot see it, you can only
/// measure it. The spectrum was already here; this is what turns the picture into a figure that
/// can be compared against a datasheet or against the same circuit before a change.
/// </para>
/// <para>
/// It also says <i>which</i> harmonics, and that is the diagnosis rather than the symptom. Even
/// harmonics mean an asymmetric waveform — one side clipping, a single-ended stage running out of
/// headroom in one direction. Odd harmonics mean a symmetric distortion: both rails clipping, or
/// the crossover notch of a class-B output stage. A number tells you how bad it is; the pattern
/// tells you what it is.
/// </para>
/// </summary>
public static class Harmonics
{
    /// <summary>
    /// Finds the fundamental and its multiples in a spectrum.
    /// </summary>
    /// <param name="spectrum">The spectrum to measure.</param>
    /// <param name="fundamental">
    /// The frequency to measure against, or null to take the largest component above DC. Stating
    /// it is better when you know it: at high distortion a harmonic can be the largest thing in
    /// the spectrum, and measuring everything against the wrong one is worse than not measuring.
    /// </param>
    /// <param name="count">How many harmonics to look for, the fundamental not included.</param>
    public static HarmonicAnalysis Of(
        SpectrumResult spectrum, double? fundamental = null, int count = 9)
    {
        ArgumentNullException.ThrowIfNull(spectrum);

        if (spectrum.IsEmpty) return HarmonicAnalysis.Unusable("There is nothing recorded to measure.");

        var resolution = spectrum.Resolution;

        if (resolution <= 0) return HarmonicAnalysis.Unusable("The spectrum has no resolution.");

        var f0 = fundamental ?? spectrum.Peak()?.Frequency ?? 0;

        if (f0 <= 0)
            return HarmonicAnalysis.Unusable("No fundamental was found — the trace is flat, or all DC.");

        // One bin wider than the window's own lobe. A real signal almost never puts a component
        // exactly on a bin centre — 1 kHz measured over an arbitrary stretch of a transient lands
        // at bin 341.3 — and a lobe centred on a third of a bin runs a third of a bin past where
        // it would sit if it were centred. Summing only the nominal lobe clips that tail off and
        // reads about a percent low, on every harmonic, in the same direction.
        var reach = spectrum.LobeHalfWidth + 1;

        // Kept fractional. A tone almost never lands on a bin centre — 1 kHz measured over an
        // arbitrary stretch sits at bin 341.3 — and rounding it before multiplying by the harmonic
        // number multiplies the rounding too, so the ninth harmonic would be looked for three bins
        // from where it is.
        var exact = f0 / resolution;
        var centre = (int)Math.Round(exact);

        // The fundamental has to clear DC's lobe, or the two overlap and what is measured is part
        // of the rail rather than the signal.
        if (centre <= spectrum.FirstCleanBin)
            return HarmonicAnalysis.Unusable(
                $"{f0:0.###} Hz is inside the DC term's own lobe. Record a longer stretch, or " +
                "use a higher fundamental.");

        // Harmonics have to be far enough apart to be separate things. Below that the lobes run
        // into each other and every harmonic is partly the one next door.
        if (centre < (2 * reach) + 1)
            return HarmonicAnalysis.Unusable(
                $"{f0:0.###} Hz is only {centre} bin{(centre == 1 ? string.Empty : "s")} up, and a " +
                $"{spectrum.Window()} window smears each component over {(2 * reach) + 1}. Record a " +
                "longer stretch so the bins are finer.");

        // Power across each lobe rather than the height of one bin. A component almost never sits
        // exactly on a bin centre, and a window spreads it over its main lobe either way — so the
        // tallest bin under-reads by up to a decibel and a half, and by a different amount for
        // every harmonic. Adding the lobe up is immune to where it landed.
        var fundamentalPower = LobePower(spectrum, centre, reach);

        if (fundamentalPower <= 0)
            return HarmonicAnalysis.Unusable($"There is nothing at {f0:0.###} Hz to measure against.");

        List<Harmonic> harmonics = [];

        var distortionPower = 0.0;
        var inBand = 0;

        for (var order = 1; order <= count + 1; order++)
        {
            var bin = (int)Math.Round(exact * order);

            // Past the Nyquist limit there is nothing to find — and what is up there has folded
            // back down into the band as something else, which is why the truncation is reported.
            if (bin + reach >= spectrum.Magnitudes.Count) break;

            var power = LobePower(spectrum, bin, reach);
            var relative = Math.Sqrt(power / fundamentalPower);

            harmonics.Add(new Harmonic(
                order,
                LobeCentre(spectrum, bin, reach),
                Amplitude(power, spectrum.NoiseBandwidth),
                relative));

            if (order == 1) continue;

            distortionPower += power;
            inBand++;
        }

        // Everything that is not the fundamental and not DC: harmonics, noise, hum, whatever a
        // mixer put there. A real distortion analyser measures this, by notching the fundamental
        // out and weighing what is left — it does not need to know where to look.
        var residualPower = 0.0;

        for (var bin = spectrum.FirstCleanBin; bin < spectrum.Magnitudes.Count; bin++)
        {
            if (Math.Abs(bin - centre) <= reach) continue;

            residualPower += spectrum.Magnitudes[bin] * spectrum.Magnitudes[bin];
        }

        return new HarmonicAnalysis(
            Fundamental: LobeCentre(spectrum, centre, reach),
            FundamentalAmplitude: Amplitude(fundamentalPower, spectrum.NoiseBandwidth),
            Harmonics: harmonics,
            Thd: Math.Sqrt(distortionPower / fundamentalPower),
            ThdPlusNoise: Math.Sqrt(residualPower / fundamentalPower),
            HarmonicsInBand: inBand,
            HarmonicsRequested: count);
    }

    /// <summary>The power in one component's main lobe, which is where all of it is.</summary>
    private static double LobePower(SpectrumResult spectrum, int centre, int reach)
    {
        var total = 0.0;

        for (var bin = Math.Max(centre - reach, 0); bin <= centre + reach; bin++)
        {
            if (bin >= spectrum.Magnitudes.Count) break;

            total += spectrum.Magnitudes[bin] * spectrum.Magnitudes[bin];
        }

        return total;
    }

    /// <summary>
    /// Where a component actually is, rather than which bin it is nearest.
    /// <para>
    /// The lobe's centre of energy, which for a symmetric window is the tone itself. Reporting the
    /// bin centre instead would round every frequency to the resolution — and then multiply that
    /// rounding by the harmonic number, so a third harmonic of a 1 kHz tone would be reported
    /// three bins out.
    /// </para>
    /// </summary>
    private static double LobeCentre(SpectrumResult spectrum, int centre, int reach)
    {
        var weighted = 0.0;
        var total = 0.0;

        for (var bin = Math.Max(centre - reach, 0); bin <= centre + reach; bin++)
        {
            if (bin >= spectrum.Magnitudes.Count) break;

            var power = spectrum.Magnitudes[bin] * spectrum.Magnitudes[bin];

            weighted += spectrum.Frequencies[bin] * power;
            total += power;
        }

        return total > 0 ? weighted / total : spectrum.Frequencies[Math.Min(centre, spectrum.Frequencies.Count - 1)];
    }

    /// <summary>
    /// An amplitude back out of a lobe's power. The window spreads a component's energy over its
    /// noise bandwidth, so the sum across the lobe is that many times too large — dividing it out
    /// is what makes a one volt sine read one volt whichever window was chosen.
    /// </summary>
    private static double Amplitude(double power, double noiseBandwidth) =>
        Math.Sqrt(power / Math.Max(noiseBandwidth, 1e-9));
}
