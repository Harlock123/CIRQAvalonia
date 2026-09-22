using Cirq.Core.Topology;
using Cirq.Engine.Simulation;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>The spectrum window's own logic, driven without a window.</summary>
public class SpectrumViewModelTests
{
    private static (Circuit, ScopeViewModel) Rig(Func<double, double> signal, double seconds = 0.1)
    {
        var circuit = new Circuit();
        var scope = new ScopeViewModel(circuit);
        var resistor = circuit.Add(new Cirq.Components.Passive.Resistor(1e3));
        var probe = scope.AddProbe(resistor.A, "Sig");

        for (var t = 0.0; t <= seconds; t += 1e-5) probe.Record(t, signal(t));

        return (circuit, scope);
    }

    [Fact]
    public void ItFindsTheFrequencyOfTheTraceTheScopeRecorded()
    {
        var (circuit, _) = Rig(t => 2.0 * Math.Sin(2 * Math.PI * 1e3 * t));

        var model = new SpectrumViewModel(circuit);
        model.Run();

        var curve = Assert.Single(model.Curves);

        Assert.True(model.HasCurves);
        Assert.Equal(curve.Frequencies.Count, curve.Magnitudes.Count);
        Assert.Equal(curve.Frequencies.Count, curve.Decibels.Count);

        // The peak is where the tone is, and it is the right size.
        var peak = 0;
        for (var i = 3; i < curve.Magnitudes.Count; i++)
            if (curve.Magnitudes[i] > curve.Magnitudes[peak]) peak = i;

        Assert.Equal(1e3, curve.Frequencies[peak], 40.0);
        Assert.Equal(2.0, curve.Magnitudes[peak], 0.2);

        Assert.Contains("peaks at", model.Status);
        Assert.Contains("per bin", model.Status);
    }

    /// <summary>
    /// The thing a spectrum is for: a square wave's odd harmonics, which are simply not visible
    /// in a picture of the waveform.
    /// </summary>
    [Fact]
    public void ASquareWaveShowsItsHarmonics()
    {
        var (circuit, _) = Rig(t => Math.Sin(2 * Math.PI * 500 * t) >= 0 ? 1.0 : -1.0, 0.2);

        var model = new SpectrumViewModel(circuit) { Size = 8192 };
        model.Run();

        var curve = model.Curves[0];

        double At(double hertz)
        {
            var best = 0;
            for (var i = 1; i < curve.Frequencies.Count; i++)
                if (Math.Abs(curve.Frequencies[i] - hertz) < Math.Abs(curve.Frequencies[best] - hertz))
                    best = i;

            var peak = 0.0;
            for (var i = Math.Max(0, best - 2); i <= Math.Min(curve.Magnitudes.Count - 1, best + 2); i++)
                peak = Math.Max(peak, curve.Magnitudes[i]);

            return peak;
        }

        var fundamental = At(500);

        Assert.Equal(fundamental / 3.0, At(1500), fundamental * 0.1);
        Assert.True(At(1000) < fundamental * 0.1, "an even harmonic appeared in a square wave");
    }

    [Fact]
    public void ChangingTheWindowOrSizeTransformsAgainOnItsOwn()
    {
        var (circuit, _) = Rig(t => Math.Sin(2 * Math.PI * 1e3 * t));

        var model = new SpectrumViewModel(circuit);
        model.Run();

        var raised = 0;
        model.CurvesChanged += (_, _) => raised++;

        model.Window = SpectrumWindow.BlackmanHarris;
        Assert.True(raised >= 1);

        model.Size = 1024;
        Assert.True(raised >= 2);

        // Fewer points is fewer bins, which is what choosing a size is for.
        Assert.True(model.Curves[0].Frequencies.Count <= (1024 / 2) + 1);
    }

    [Fact]
    public void SwitchingToDecibelsRedrawsWithoutTransformingAgain()
    {
        var (circuit, _) = Rig(t => Math.Sin(2 * Math.PI * 1e3 * t));

        var model = new SpectrumViewModel(circuit);
        model.Run();

        var before = model.Curves[0];

        model.IsLogarithmic = false;

        // Same curve object: the magnitudes and the decibels were both computed the first time,
        // so the axis choice is a drawing decision rather than another transform.
        Assert.Same(before, model.Curves[0]);
        Assert.Equal("Amplitude", model.VerticalLabel);

        model.IsLogarithmic = true;
        Assert.Equal("Magnitude (dB)", model.VerticalLabel);
    }

    [Fact]
    public void WithNoProbesItSaysSo()
    {
        var model = new SpectrumViewModel(new Circuit());
        model.Run();

        Assert.Empty(model.Curves);
        Assert.False(model.HasCurves);
        Assert.Contains("probe", model.Status, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AProbeWithNoTraceYetIsNamedRatherThanIgnored()
    {
        var circuit = new Circuit();
        var scope = new ScopeViewModel(circuit);
        var resistor = circuit.Add(new Cirq.Components.Passive.Resistor(1e3));
        scope.AddProbe(resistor.A, "Quiet");

        var model = new SpectrumViewModel(circuit);
        model.Run();

        Assert.Empty(model.Curves);
        Assert.Contains("Quiet", model.Status);
    }

    [Fact]
    public void AHiddenTraceIsNotTransformed()
    {
        var (circuit, scope) = Rig(t => Math.Sin(2 * Math.PI * 1e3 * t));

        scope.Probes[0].IsVisible = false;

        var model = new SpectrumViewModel(circuit);
        model.Run();

        Assert.Empty(model.Curves);
    }

    // ---- distortion --------------------------------------------------------

    /// <summary>
    /// The number an amplifier is sold on, next to the picture that cannot give it to you: a sine
    /// with a tenth of a second harmonic on it is ten percent distorted, and looks like a sine.
    /// </summary>
    [Fact]
    public void ADistortedSineIsMeasuredAsWellAsDrawn()
    {
        var (circuit, _) = Rig(t =>
            Math.Sin(2 * Math.PI * 1e3 * t) + (0.1 * Math.Sin(2 * Math.PI * 2e3 * t)), 0.2);

        var model = new SpectrumViewModel(circuit) { Size = 8192, FundamentalHz = 1e3 };
        model.Run();

        var row = Assert.Single(model.Distortion);

        Assert.True(model.HasDistortion);
        Assert.True(row.Analysis.IsUsable, row.Analysis.Problem);

        Assert.Equal(10.0, row.Analysis.ThdPercent, 0.3);
        Assert.Contains("%", row.Thd);

        // And the list says which harmonic it is, which is the diagnosis rather than the figure.
        Assert.Contains("H2", row.Detail);
    }

    /// <summary>A clean sine measures clean, and says so rather than printing a row of zeroes.</summary>
    [Fact]
    public void ACleanSineMeasuresClean()
    {
        var (circuit, _) = Rig(t => Math.Sin(2 * Math.PI * 1e3 * t), 0.2);

        var model = new SpectrumViewModel(circuit) { Size = 8192, FundamentalHz = 1e3 };
        model.Run();

        Assert.True(model.Distortion[0].Analysis.ThdPercent < 0.5);
    }

    /// <summary>
    /// A square wave is the textbook shape, and it is also the clearest demonstration of why
    /// there are two figures rather than one.
    /// <para>
    /// Its harmonics are the odd ones, the nth at 1/n of the fundamental, running on for ever. Nine
    /// harmonics therefore account for √(1/3² + 1/5² + 1/7² + 1/9²) — 42.9 % — and <b>that is all
    /// THD can ever say</b>, because THD is the sum of the harmonics you asked it to look at. The
    /// true figure, the tail of the series included, is √(π²/8 − 1) = 48.3 %, and THD+N gets most
    /// of the way there without being told where to look, because it weighs everything that is not
    /// the fundamental.
    /// </para>
    /// </summary>
    [Fact]
    public void ASquareWavesDistortionIsOddAndTheTwoFiguresDifferForAReason()
    {
        var (circuit, _) = Rig(t => Math.Sin(2 * Math.PI * 500 * t) >= 0 ? 1.0 : -1.0, 0.2);

        var model = new SpectrumViewModel(circuit) { Size = 8192, FundamentalHz = 500 };
        model.Run();

        var analysis = model.Distortion[0].Analysis;

        var nineHarmonics = 100 * Math.Sqrt(
            new[] { 3, 5, 7, 9 }.Sum(n => 1.0 / (n * n)));

        Assert.Equal(nineHarmonics, analysis.ThdPercent, 1.0);

        // Everything the harmonic list could not reach still counts here.
        Assert.True(analysis.ThdPlusNoisePercent > analysis.ThdPercent + 3,
            $"THD+N {analysis.ThdPlusNoisePercent:0.0}% should exceed THD {analysis.ThdPercent:0.0}%");

        var odd = analysis.Harmonics.Where(h => h.Order > 1 && !h.IsEven).Sum(h => h.Relative * h.Relative);
        var even = analysis.Harmonics.Where(h => h.IsEven).Sum(h => h.Relative * h.Relative);

        Assert.True(even < odd / 100, $"even {even:0.#####} against odd {odd:0.#####}");
    }

    /// <summary>
    /// Stating the fundamental changes the answer when a harmonic is the biggest thing present,
    /// and the window offers the choice for exactly that case.
    /// </summary>
    [Fact]
    public void TheFundamentalCanBeStatedRatherThanGuessed()
    {
        var (circuit, _) = Rig(t =>
            (0.2 * Math.Sin(2 * Math.PI * 1e3 * t)) + Math.Sin(2 * Math.PI * 2e3 * t), 0.2);

        var guessed = new SpectrumViewModel(circuit) { Size = 8192 };
        guessed.Run();

        var stated = new SpectrumViewModel(circuit) { Size = 8192, FundamentalHz = 1e3 };
        stated.Run();

        Assert.Equal(2e3, guessed.Distortion[0].Analysis.Fundamental, 30.0);
        Assert.Equal(1e3, stated.Distortion[0].Analysis.Fundamental, 30.0);
    }

    /// <summary>
    /// Changing the harmonic count re-measures rather than waiting to be told to, the way the
    /// window and point count already do.
    /// </summary>
    [Fact]
    public void ChangingTheSettingsRemeasures()
    {
        var (circuit, _) = Rig(t => Math.Sin(2 * Math.PI * 1e3 * t), 0.2);

        var model = new SpectrumViewModel(circuit) { Size = 8192 };
        model.Run();

        Assert.Equal(9, model.Distortion[0].Analysis.HarmonicsRequested);

        model.HarmonicCount = 3;

        Assert.Equal(3, model.Distortion[0].Analysis.HarmonicsRequested);
    }

    /// <summary>Nothing recorded means nothing measured, rather than a row of made-up numbers.</summary>
    [Fact]
    public void WithNothingRecordedThereIsNoDistortionRow()
    {
        var circuit = new Circuit();
        circuit.Add(new Cirq.Components.Passive.Resistor(1e3));

        var model = new SpectrumViewModel(circuit);
        model.Run();

        Assert.False(model.HasDistortion);
        Assert.Empty(model.Distortion);
    }

    /// <summary>
    /// A trace with nothing in it gets a row that says why rather than a figure: a flat line has
    /// no fundamental, so it has no distortion either.
    /// </summary>
    [Fact]
    public void AFlatTraceSaysWhyItCannotBeMeasured()
    {
        var (circuit, _) = Rig(_ => 0.0, 0.2);

        var model = new SpectrumViewModel(circuit) { Size = 8192 };
        model.Run();

        var row = Assert.Single(model.Distortion);

        Assert.False(row.Analysis.IsUsable);
        Assert.NotEmpty(row.Detail);
    }
}
