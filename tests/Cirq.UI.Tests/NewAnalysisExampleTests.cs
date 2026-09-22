using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Engine.Simulation;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The examples added so the newer analyses have something to be opened on.
/// <para>
/// An analysis nobody has an example for is close to invisible however well documented, so these
/// exist to be found — and each one asserts the thing it was built to show, because an example
/// that stopped demonstrating its own point would be worse than none.
/// </para>
/// </summary>
public class NewAnalysisExampleTests
{
    private static MainWindowViewModel Load(string name)
    {
        var vm = new MainWindowViewModel();

        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == name).Build(vm);

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        return vm;
    }

    /// <summary>
    /// The crossover notch: a band around zero where neither transistor conducts, so the output
    /// does not move while the input does. Measured as the flat spot, which is what it is.
    /// </summary>
    [Fact]
    public void TheCrossoverExampleHasANotchInIt()
    {
        using var vm = Load("Crossover Distortion");

        var sim = vm.Simulation.Simulator!;
        sim.Run(3e-3);

        var output = vm.Circuit.Probes.Single(p => p.Label == "Output").HistoryBuffer.ToArray();

        Assert.NotEmpty(output);

        // Both halves swing, so the stage is working rather than simply off.
        Assert.True(output.Max(s => s.Value) > 1.0, "it should swing positive");
        Assert.True(output.Min(s => s.Value) < -1.0, "and negative");

        // And there is a band around zero it spends longer in than a sine would: the notch.
        var near = output.Count(s => Math.Abs(s.Value) < 0.5) / (double)output.Length;

        Assert.True(near > 0.12,
            $"a class-B pair should dwell near zero — only {near:P0} of the samples are there");
    }

    /// <summary>
    /// And it measures as odd-harmonic, because the notch is symmetric — the same distortion on
    /// both halves of the waveform.
    /// </summary>
    [Fact]
    public void TheCrossoverExampleMeasuresAsOddHarmonicDistortion()
    {
        using var vm = Load("Crossover Distortion");

        var generator = vm.Circuit.Components.OfType<Cirq.Components.Sources.FunctionGenerator>().Single();
        var fundamental = generator.Frequency;

        // A hundred samples a cycle puts a hundred cycles in the probe's ten thousand points.
        vm.Simulation.Settings.ProbeSampleInterval = 1.0 / fundamental / 100.0;
        vm.Simulation.Settings.MaxTimeStep = 1.0 / fundamental / 500.0;
        vm.Simulation.Settings.TimeStep = 1.0 / fundamental / 500.0;

        var sim = vm.Simulation.Simulator!;
        sim.Run(20 / fundamental);

        foreach (var probe in vm.Circuit.Probes) probe.HistoryBuffer.Clear();

        sim.Run(100 / fundamental);

        var samples = vm.Circuit.Probes.Single(p => p.Label == "Output").HistoryBuffer.ToArray();

        var spectrum = Spectrum.Of(samples, samples[0].Time, samples[^1].Time,
            new SpectrumRequest(SpectrumWindow.BlackmanHarris, 8192));

        var harmonics = Harmonics.Of(spectrum, fundamental);

        Assert.True(harmonics.IsUsable, harmonics.Problem);
        Assert.True(harmonics.ThdPercent > 1,
            $"a class-B pair should distort measurably, not {harmonics.ThdPercent:0.##}%");

        var odd = harmonics.Harmonics.Where(h => h.Order > 1 && !h.IsEven).Sum(h => h.Relative * h.Relative);
        var even = harmonics.Harmonics.Where(h => h.IsEven).Sum(h => h.Relative * h.Relative);

        Assert.True(odd > even * 3,
            $"a symmetric notch is odd-harmonic: odd {odd:0.#####}, even {even:0.#####}");
    }

    /// <summary>
    /// The preamp example is the same gain twice, from a hundredfold difference in impedance — so
    /// it has to be the same gain, and it has to be far noisier on the big-resistor side.
    /// </summary>
    [Fact]
    public void ThePreampExampleIsTheSameGainAndVeryDifferentNoise()
    {
        using var vm = Load("Low-Noise Preamp");

        var amps = vm.Circuit.Components.OfType<Cirq.Components.Ics.OperationalAmplifier>().ToList();

        Assert.Equal(2, amps.Count);

        double Noise(string label)
        {
            var model = new NoiseViewModel(vm.Circuit)
            {
                Output = vm.Circuit.Probes.Single(p => p.Label == label),
                StartHz = 10,
                StopHz = 1e5,
            };

            model.Run();

            Assert.True(model.HasResult, model.Status);

            return model.Rms;
        }

        var quiet = Noise("Quiet out");
        var noisy = Noise("Noisy out");

        Assert.True(noisy > quiet * 3,
            $"the big-resistor stage should be far noisier: {noisy:E2} against {quiet:E2}");
    }

    /// <summary>
    /// And the ranking changes with it, which is the lesson: small resistors make it the
    /// amplifier's own voltage noise, large ones make it the resistors and the current noise.
    /// </summary>
    [Fact]
    public void ThePreampExampleShowsTheRankingChanging()
    {
        using var vm = Load("Low-Noise Preamp");

        string Leader(string label)
        {
            var model = new NoiseViewModel(vm.Circuit)
            {
                Output = vm.Circuit.Probes.Single(p => p.Label == label),
                StartHz = 10,
                StopHz = 1e5,
            };

            model.Run();

            return model.Contributors[0].Name;
        }

        Assert.EndsWith("voltage noise", Leader("Quiet out"), StringComparison.Ordinal);
        Assert.DoesNotContain("voltage noise", Leader("Noisy out"), StringComparison.Ordinal);
    }

    /// <summary>
    /// The Loop Stability example is what the parameter-step window is best opened on too: step
    /// the load capacitance and watch the ringing that the phase margin predicted.
    /// </summary>
    [Fact]
    public void TheStabilityExampleAlsoServesTheParameterStep()
    {
        using var vm = Load("Loop Stability");

        vm.Circuit.Components.OfType<ToggleSwitch>().Single().IsClosed = true;

        var capacitor = vm.Circuit.Components.OfType<Capacitor>().Single();

        var model = new TransientStepViewModel(vm.Circuit)
        {
            Parameter = new TransientStepViewModel(vm.Circuit).Options.Single(o =>
                o.Component == capacitor && o.PropertyName == nameof(Capacitor.Capacitance)),
            Start = 1e-9,
            Stop = 400e-9,
            Count = 4,
            Duration = 400e-6,
        };

        model.Run();

        // Two probes in the example, so four runs make eight curves.
        var output = model.Curves
            .Where(c => c.Label.StartsWith("Output", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(4, output.Count);

        // More capacitance is less phase margin is more overshoot: the last curve should ring
        // further past its own settling value than the first.
        double Overshoot(StepCurve curve) =>
            curve.Values.Max() - curve.Values[^1];

        var least = output[0];
        var most = output[^1];

        Assert.True(Overshoot(most) > Overshoot(least),
            $"more load should ring more: {Overshoot(most):F3} against {Overshoot(least):F3}");
    }
}
