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
}
