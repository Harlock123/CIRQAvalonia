using Cirq.Components.Nonlinear;
using Cirq.Core.Topology;
using Cirq.Components.Sources;
using Cirq.Engine.Simulation;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Distortion measured on circuits that actually distort, rather than on signals built out of the
/// harmonics being looked for.
/// <para>
/// This is where the feature has to earn its keep: an amplifier at one percent looks exactly like
/// a sine on the scope, and the only way to know is to measure it. Every figure here is checked
/// against what the circuit must do rather than against what it did last time.
/// </para>
/// </summary>
public class DistortionExampleTests
{
    /// <summary>
    /// Runs the circuit and measures one probe's distortion.
    /// <para>
    /// The sampling is set from the fundamental rather than left at whatever the scope's timebase
    /// was, and that is not a convenience: a probe's history is ten thousand points, so how much
    /// <i>time</i> it holds is decided entirely by how finely it is sampled. At the timebase these
    /// examples open on, ten thousand points is a few milliseconds — six cycles of a kilohertz
    /// tone, which is nowhere near enough resolution to separate a fundamental from DC, and the
    /// analysis says so rather than guessing. A hundred samples per cycle puts a hundred cycles in
    /// the buffer and the fundamental a hundred bins up, which is room to work in.
    /// </para>
    /// </summary>
    private static HarmonicAnalysis Measure(
        MainWindowViewModel vm, string probeLabel, double fundamental)
    {
        var sim = vm.Simulation.Simulator!;
        var period = 1.0 / fundamental;

        vm.Simulation.Settings.ProbeSampleInterval = period / 100.0;
        vm.Simulation.Settings.TimeStep = period / 500.0;
        vm.Simulation.Settings.MaxTimeStep = period / 500.0;

        // Settle first: a supply charging or an amplifier finding its bias is a transient, and a
        // transient in the block is broadband energy that would be counted as distortion.
        sim.Run(40 * period);

        foreach (var p in vm.Circuit.Probes) p.HistoryBuffer.Clear();

        sim.Run(100 * period);

        var probe = vm.Circuit.Probes.Single(p =>
            p.Label.Contains(probeLabel, StringComparison.OrdinalIgnoreCase));

        var samples = probe.HistoryBuffer.ToArray();

        var spectrum = Spectrum.Of(
            samples, samples[0].Time, samples[^1].Time,
            new SpectrumRequest(SpectrumWindow.BlackmanHarris, 8192));

        return Harmonics.Of(spectrum, fundamental);
    }

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
    /// An op-amp inside its linear range is clean; the same op-amp asked for more than its rails
    /// can give is not. That is the measurement, and nothing on the scope distinguishes the first
    /// case from the second until the flats appear.
    /// </summary>
    [Fact]
    public void AnAmplifierDistortsWhenItIsAskedForMoreThanItsRails()
    {
        using var vm = Load("Inverting Amplifier");

        var generator = vm.Circuit.Components.OfType<FunctionGenerator>().Single();
        var probe = vm.Circuit.Probes.Last();

        generator.Shape = Waveform.Sine;
        generator.Frequency = 1e3;
        generator.AmplitudePeakToPeak = 0.4;      // ×10 is 4 Vpp, well inside the rails

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var clean = Measure(vm, probe.Label, 1e3);

        Assert.True(clean.IsUsable, clean.Problem);
        Assert.True(clean.ThdPercent < 1.0,
            $"inside its rails it should be clean, not {clean.ThdPercent:0.##}%");

        // Now ask for forty volts peak-to-peak out of a supply that has nothing like it.
        generator.AmplitudePeakToPeak = 4.0;

        vm.Simulation.InvalidateTopology();
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var clipped = Measure(vm, probe.Label, 1e3);

        Assert.True(clipped.ThdPercent > 10.0,
            $"driven well past its rails it should be badly distorted, not {clipped.ThdPercent:0.##}%");

        // Clipping both rails is symmetric, so the harmonics are the odd ones.
        var odd = clipped.Harmonics.Where(h => h.Order > 1 && !h.IsEven).Sum(h => h.Relative * h.Relative);
        var even = clipped.Harmonics.Where(h => h.IsEven).Sum(h => h.Relative * h.Relative);

        Assert.True(odd > even * 5,
            $"symmetric clipping should be odd-harmonic: odd {odd:0.####}, even {even:0.####}");
    }

    /// <summary>
    /// A half-wave rectified sine is the textbook lopsided waveform, and its Fourier series is
    /// known exactly: a DC term, the fundamental at half the peak, and then <b>only even</b>
    /// harmonics, the 2nth at 2A/π(4n²−1). No odd harmonic appears at all.
    /// <para>
    /// So the second harmonic should sit at 4/3π — 42.4 % — of the fundamental, the fourth at
    /// 4/15π, and the third and fifth at nothing. That is the whole diagnostic claim the harmonic
    /// list makes, checked against arithmetic rather than against a previous run: throwing away
    /// one half of a waveform is what produces even harmonics.
    /// </para>
    /// </summary>
    [Fact]
    public void AHalfWaveRectifiedSineHasOnlyEvenHarmonics()
    {
        var vm = new MainWindowViewModel();

        vm.Circuit.Clear();
        vm.Scope.ClearProbes();

        // A big swing, so the diode's own drop is a small perturbation on the shape rather than
        // most of it.
        var generator = vm.Circuit.Add(new FunctionGenerator(Waveform.Sine, 50, 48.0));
        var diode = vm.Circuit.Add(new Diode(DiodeModel.D1N4001));
        var load = vm.Circuit.Add(new Cirq.Components.Passive.Resistor(10e3));
        var ground = vm.Circuit.Add(new Ground());

        vm.Circuit.Connect(generator.Return, ground.Pin);
        vm.Circuit.Connect(generator.Output, diode.Anode);
        vm.Circuit.Connect(diode.Cathode, load.A);
        vm.Circuit.Connect(load.B, ground.Pin);

        vm.Scope.AddProbe(load.A, "Rectified");

        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var analysis = Measure(vm, "Rectified", 50);

        Assert.True(analysis.IsUsable, analysis.Problem);

        double Relative(int order) => analysis.Harmonics.Single(h => h.Order == order).Relative;

        // The even ones, where the series says they are.
        Assert.Equal(4.0 / (3.0 * Math.PI), Relative(2), 0.02);
        Assert.Equal(4.0 / (15.0 * Math.PI), Relative(4), 0.02);

        // And the odd ones, which are not there at all.
        Assert.True(Relative(3) < 0.03, $"H3 should be absent, not {Relative(3):0.####}");
        Assert.True(Relative(5) < 0.03, $"H5 should be absent, not {Relative(5):0.####}");

        // Which is the diagnosis: lopsided, so even-harmonic.
        var even = analysis.Harmonics.Where(h => h.IsEven).Sum(h => h.Relative * h.Relative);
        var odd = analysis.Harmonics.Where(h => h.Order > 1 && !h.IsEven).Sum(h => h.Relative * h.Relative);

        Assert.True(even > odd * 20, $"even {even:0.####} should dominate odd {odd:0.####}");

        vm.Dispose();
    }
}
