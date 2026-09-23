using ScottPlot;
using Sample = Cirq.Core.Primitives.DataPoint;
using System.Numerics;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// Draws the guide's analysis plots from the analyses themselves.
/// <para>
/// Every picture here is the real output of a real circuit, redrawn whenever the tests run. That
/// is the point: an illustration that cannot show something the code does not do, and that cannot
/// be left behind by a change to the code. Each one also asserts the thing it is illustrating, so
/// a plot that stopped being true would fail rather than quietly become a misleading picture.
/// </para>
/// </summary>
public class GuidePlotTests
{
    private static Ground Gnd(Circuit c) => c.Add(new Ground());

    // ---- frequency response ------------------------------------------------

    /// <summary>The Bode plot: what a filter does to each frequency, magnitude and phase.</summary>
    [Fact]
    public void FrequencyResponse()
    {
        var circuit = new Circuit();

        var source = circuit.Add(new DcVoltageSource(0) { AcMagnitude = 1.0, Name = "V1" });
        var resistor = circuit.Add(new Resistor(1e3));
        var capacitor = circuit.Add(new Capacitor(100e-9));
        var ground = Gnd(circuit);

        circuit.Connect(source.Negative, ground.Pin);
        circuit.Connect(source.Positive, resistor.A);
        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, ground.Pin);

        var probe = new SignalProbe("Out", capacitor.A, default);
        circuit.Probes.Add(probe);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        var result = new AcSweep(sim).Run(new AcSweepRequest(10, 1e6, 30));
        var trace = result.Traces[0];

        // 1 kΩ and 100 nF turn over at 1/2πRC — 1.59 kHz.
        var corner = result.CornerOf("Out");

        Assert.NotNull(corner);
        Assert.Equal(1.0 / (2 * Math.PI * 1e3 * 100e-9), corner.Value, corner.Value * 0.05);

        var x = result.Frequencies.Select(Math.Log10).ToArray();

        DocPlot.Stack("15-frequency-response.png", (magnitude, phase) =>
        {
            DocPlot.Style(magnitude, "Frequency (Hz)", "Gain (dB)");
            DocPlot.DecadeBottom(magnitude);
            DocPlot.Line(magnitude, x, Enumerable.Range(0, x.Length).Select(trace.Decibels), 0);
            DocPlot.Reference(magnitude, -3, "−3 dB");
            DocPlot.Vertical(magnitude, Math.Log10(corner.Value), null);
            DocPlot.Title(magnitude, $"1 kΩ and 100 nF, turning over at {corner.Value / 1e3:0.00} kHz");

            DocPlot.Style(phase, "Frequency (Hz)", "Phase (°)");
            DocPlot.DecadeBottom(phase);
            DocPlot.Line(phase, x, Enumerable.Range(0, x.Length).Select(trace.Degrees), 1);
            DocPlot.Reference(phase, -45, "−45°");
        });
    }

    // ---- DC sweep ----------------------------------------------------------

    /// <summary>The curve tracer: a transistor's output characteristics, one curve per base current.</summary>
    [Fact]
    public void CurveTracer()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(5.0) { Name = "V1" });
        var bias = circuit.Add(new DcCurrentSource(10e-6) { Name = "I1" });
        var transistor = circuit.Add(new BipolarTransistor { Name = "Q1" });
        var ground = Gnd(circuit);

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, transistor.Collector);
        circuit.Connect(bias.Negative, transistor.Base);
        circuit.Connect(bias.Positive, ground.Pin);
        circuit.Connect(transistor.Emitter, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Ic", transistor.Collector, default)
        {
            Kind = ProbeKind.Current,
        });

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        var result = new DcSweep(sim).Run(new DcSweepRequest(
            new SweepTarget(supply, nameof(DcVoltageSource.Voltage), 0, 5, 60),
            new SweepTarget(bias, nameof(DcCurrentSource.Current), 10e-6, 40e-6, 4)));

        Assert.Equal(4, result.Curves.Count);

        var plot = DocPlot.New("V1 Voltage (V)", "Collector current (A)");
        DocPlot.SiLeft(plot);

        for (var i = 0; i < result.Curves.Count; i++)
        {
            var curve = result.Curves[i];

            DocPlot.Line(plot, result.X, curve.Traces[0].Values, i,
                $"Ib = {curve.StepValue * 1e6:0} µA");
        }

        DocPlot.Legend(plot);

        // More base current is more collector current, which is what the fan shows.
        var last = result.Curves.Select(c => Math.Abs(c.Traces[0].Values[^1])).ToList();
        Assert.True(last.SequenceEqual(last.OrderBy(v => v)), "the curves should fan upwards");

        DocPlot.Save(plot, "16-curve-tracer.png");
    }

    // ---- spectrum and distortion -------------------------------------------

    /// <summary>A clipped sine's spectrum, with the harmonics that clipping put there.</summary>
    [Fact]
    public void SpectrumAndDistortion()
    {
        const double rate = 48000.0;
        const int size = 16384;
        const double fundamental = 341 * rate / size;

        List<Sample> samples =
            [.. Enumerable.Range(0, size + 1).Select(i =>
                new Sample(i / rate,
                    Math.Clamp(1.6 * Math.Sin(2 * Math.PI * fundamental * i / rate), -1.0, 1.0)))];

        var spectrum = Spectrum.Of(samples, samples[0].Time, samples[^1].Time,
            new SpectrumRequest(SpectrumWindow.BlackmanHarris, size));

        var harmonics = Harmonics.Of(spectrum, fundamental);

        Assert.True(harmonics.IsUsable, harmonics.Problem);
        Assert.True(harmonics.ThdPercent > 5, $"clipping should distort, not {harmonics.ThdPercent:0.##}%");

        // Symmetric clipping, so the harmonics are the odd ones.
        var odd = harmonics.Harmonics.Where(h => h.Order > 1 && !h.IsEven).Sum(h => h.Relative * h.Relative);
        var even = harmonics.Harmonics.Where(h => h.IsEven).Sum(h => h.Relative * h.Relative);

        Assert.True(odd > even * 10, "clipping both sides is odd-harmonic");

        var plot = DocPlot.New("Frequency (Hz)", "Amplitude (dB)");

        var upTo = spectrum.Frequencies.Count(f => f <= fundamental * 12);

        DocPlot.Line(plot,
            spectrum.Frequencies.Take(upTo),
            Enumerable.Range(0, upTo).Select(spectrum.Decibels), 0);

        foreach (var harmonic in harmonics.Harmonics.Where(h => h.Relative > 0.01))
        {
            var marker = plot.Add.Marker(harmonic.Frequency, 20 * Math.Log10(harmonic.Amplitude));

            marker.Color = DocPlot.Traces[1];
            marker.Size = 9;
            marker.MarkerShape = ScottPlot.MarkerShape.OpenCircle;
            if (harmonic.Order == 1) marker.LegendText = "Fundamental and harmonics";
        }

        plot.Axes.SetLimitsY(-90, 10);
        DocPlot.Title(plot, $"THD {harmonics.ThdPercent:0.0} %  ·  odd harmonics, so the clipping is symmetric");

        DocPlot.Save(plot, "17-distortion.png");
    }


    // ---- stability ---------------------------------------------------------

    /// <summary>
    /// The loop gain of a follower with a capacitive load: the second pole the capacitor makes is
    /// what eats the phase margin, and the plot shows the margin as the gap between the phase
    /// curve and −180° where the gain passes through one.
    /// </summary>
    [Fact]
    public void LoopStability()
    {
        var vm = new MainWindowViewModel();

        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == "Loop Stability").Build(vm);

        vm.Circuit.Components.OfType<ToggleSwitch>().Single().IsClosed = true;

        var model = new StabilityViewModel(vm.Circuit) { StartHz = 1, StopHz = 1e7 };
        model.Run();

        Assert.True(model.HasResult, model.Status);
        Assert.NotNull(model.PhaseMarginDegrees);
        Assert.NotNull(model.CrossoverHz);

        var x = model.Frequencies.Select(Math.Log10).ToArray();
        var at = Math.Log10(model.CrossoverHz.Value);

        DocPlot.Stack("18-stability.png", (gain, phase) =>
        {
            DocPlot.Style(gain, "Frequency (Hz)", "Loop gain (dB)");
            DocPlot.DecadeBottom(gain);
            DocPlot.Line(gain, x, model.Decibels, 0);
            DocPlot.Reference(gain, 0, "0 dB");
            DocPlot.Vertical(gain, at, null);
            DocPlot.Title(gain,
                $"A follower with 100 nF on it — {model.PhaseMargin} of phase margin at {model.Crossover}");

            DocPlot.Style(phase, "Frequency (Hz)", "Loop phase (°)");
            DocPlot.DecadeBottom(phase);
            DocPlot.Line(phase, x, model.Degrees, 1);
            DocPlot.Reference(phase, -180, "−180°");
            DocPlot.Vertical(phase, at, null);
        });

        vm.Dispose();
    }

    // ---- impedance ---------------------------------------------------------

    /// <summary>
    /// A decoupling capacitor's impedance, which is the plot that changes how people lay out a
    /// board: it stops being a capacitor at its series resonance and is an inductor above it.
    /// </summary>
    [Fact]
    public void DecouplingImpedance()
    {
        var circuit = new Circuit();

        var capacitor = circuit.Add(new Capacitor(100e-9) { Name = "C1" });
        var parasitic = circuit.Add(new Inductor(5e-9) { Name = "L1", SeriesResistance = 0 });
        var esr = circuit.Add(new Resistor(0.05) { Name = "R1" });
        var ground = Gnd(circuit);

        circuit.Connect(capacitor.B, parasitic.A);
        circuit.Connect(parasitic.B, esr.A);
        circuit.Connect(esr.B, ground.Pin);

        var probe = new SignalProbe("Rail", capacitor.A, default);
        circuit.Probes.Add(probe);

        var model = new ImpedanceViewModel(circuit) { StartHz = 1e4, StopHz = 1e9 };
        model.Run();

        Assert.True(model.HasResult, model.Status);

        // It really does turn round, and where the textbook says: 1/2π√(LC).
        var resonance = Assert.Single(model.Resonances);

        Assert.True(resonance.Series);
        Assert.Equal(1.0 / (2 * Math.PI * Math.Sqrt(5e-9 * 100e-9)), resonance.Hertz, resonance.Hertz * 0.02);

        var x = model.Frequencies.Select(Math.Log10).ToArray();

        DocPlot.Stack("27-impedance.png", (magnitude, phase) =>
        {
            DocPlot.Style(magnitude, "Frequency (Hz)", "|Z| (Ω)");
            DocPlot.DecadeBottom(magnitude);
            DocPlot.DecadeLeft(magnitude);
            DocPlot.Line(magnitude, x, [.. model.Ohms.Select(Math.Log10)], 0);
            DocPlot.Vertical(magnitude, Math.Log10(resonance.Hertz), null);
            DocPlot.Title(magnitude,
                $"A 100 nF ceramic with 5 nH of lead — a capacitor below " +
                $"{Cirq.Core.Units.SiPrefix.Format(resonance.Hertz, "Hz")}, an inductor above it");

            DocPlot.Style(phase, "Frequency (Hz)", "Phase (°)");
            DocPlot.DecadeBottom(phase);
            DocPlot.Line(phase, x, model.Degrees, 1);
            // No label on the line: the axis already says degrees, and a caption here lands on
            // top of the tick labels.
            DocPlot.Reference(phase, 0, null);
            DocPlot.Vertical(phase, Math.Log10(resonance.Hertz), null);
        });
    }

    // ---- poles and zeros ---------------------------------------------------

    /// <summary>
    /// The s-plane of the same follower the stability plot measures, from the other end: the pair
    /// that eats its phase margin, drawn where it sits.
    /// </summary>
    [Fact]
    public void PoleZeroMap()
    {
        var vm = new MainWindowViewModel();

        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == "Loop Stability").Build(vm);

        vm.Circuit.Components.OfType<ToggleSwitch>().Single().IsClosed = true;

        var model = new PoleZeroViewModel(vm.Circuit);
        model.Run();

        Assert.True(model.HasResult, model.Status);

        var ringing = model.Poles.Where(p => p.IsOscillatory).OrderByDescending(p => p.Q ?? 0).First();

        Assert.True(ringing.Q > 1, $"expected a lightly damped pair, got Q of {ringing.Q}");

        var plot = DocPlot.New("Real (rad/s) — how fast it dies away", "Imaginary (rad/s) — what it rings at");

        DocPlot.SiBottom(plot);
        DocPlot.SiLeft(plot);

        // The imaginary axis, which is the line between a circuit that settles and one that does
        // not. Unlabelled: at this scale it sits hard against the right edge and a caption on it
        // runs off the picture.
        var axis = plot.Add.VerticalLine(0);
        axis.Color = DocPlot.Marker;
        axis.LineWidth = 1;
        axis.LinePattern = ScottPlot.LinePattern.Dashed;

        var poles = plot.Add.ScatterPoints(
            model.Poles.Select(p => p.S.Real).ToArray(),
            model.Poles.Select(p => p.S.Imaginary).ToArray(),
            DocPlot.Traces[0]);

        poles.MarkerShape = ScottPlot.MarkerShape.Cross;
        poles.MarkerSize = 13;
        poles.MarkerLineWidth = 2;
        poles.LegendText = "Poles";

        DocPlot.Legend(plot);
        DocPlot.Title(plot,
            $"The same follower the stability plot measures — a pair at " +
            $"{Cirq.Core.Units.SiPrefix.Format(ringing.Hertz, "Hz")} with a Q of {ringing.Q:0.#}. " +
            "Everything left of the dashed line decays.");

        DocPlot.Save(plot, "28-pole-zero.png", 900, 520);

        vm.Dispose();
    }

    // ---- noise -------------------------------------------------------------

    /// <summary>
    /// An amplifier's output noise across the band, with the flicker rise at the bottom end — and
    /// beside it, where the noise comes from, which is the half that changes what you do.
    /// </summary>
    [Fact]
    public void NoiseDensityAndRanking()
    {
        var vm = new MainWindowViewModel();

        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == "Inverting Amplifier").Build(vm);

        var model = new NoiseViewModel(vm.Circuit) { StartHz = 1, StopHz = 1e5, PointsPerDecade = 30 };
        model.Run();

        Assert.True(model.HasResult, model.Status);
        Assert.NotEmpty(model.Contributors);

        // Flicker noise, which is what makes the bottom of the band the worst part of it.
        Assert.True(model.Density[0] > model.Density[^1],
            "one hertz should be noisier than a hundred kilohertz");

        var plot = DocPlot.New("Frequency (Hz)", "Output noise (V/√Hz)");
        DocPlot.DecadeBottom(plot);

        DocPlot.Line(plot,
            model.Frequencies.Select(Math.Log10),
            model.Density.Select(d => Math.Log10(Math.Max(d, 1e-21))), 0);

        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = v => Cirq.Core.Units.SiPrefix.Format(Math.Pow(10, v), string.Empty),
        };

        var leader = model.Contributors[0];

        DocPlot.Title(plot,
            $"{Cirq.Core.Units.SiPrefix.Format(model.Rms, "V")} rms  ·  " +
            $"{leader.Percent} of it is {leader.Name}");

        DocPlot.Save(plot, "19-noise.png");
        Ranking(model, "20-noise-ranking.png");

        vm.Dispose();
    }

    /// <summary>The contributors as bars, because a ranking is read by length and not by digits.</summary>
    private static void Ranking(NoiseViewModel model, string name)
    {
        var rows = model.Contributors.Take(6).Reverse().ToList();

        var plot = DocPlot.New("Share of the output noise power (%)", string.Empty);

        var bars = plot.Add.Bars(rows.Select((r, i) => new ScottPlot.Bar
        {
            Position = i,
            Value = r.Share * 100,
            FillColor = DocPlot.Traces[0],
            LineColor = DocPlot.Traces[0],
        }).ToArray());

        bars.Horizontal = true;

        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
            [.. rows.Select((r, i) => new ScottPlot.Tick(i, r.Name))]);

        plot.Axes.Left.MajorTickStyle.Length = 0;

        DocPlot.Title(plot, "Where the noise comes from");
        DocPlot.Save(plot, name, 900, 360);
    }

    // ---- stepping a parameter ----------------------------------------------

    /// <summary>
    /// The same step response at four capacitor values, which is the question a DC sweep cannot
    /// answer: not where it ends up, but how it gets there.
    /// </summary>
    [Fact]
    public void SteppedTransient()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(1.0));
        var resistor = circuit.Add(new Resistor(1e3) { Name = "R1" });
        var capacitor = circuit.Add(new Capacitor(1e-6) { Name = "C1", InitialVoltage = 0.0 });
        var ground = Gnd(circuit);

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, resistor.A);
        circuit.Connect(resistor.B, capacitor.A);
        circuit.Connect(capacitor.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Vc", capacitor.A, default));

        var model = new TransientStepViewModel(circuit)
        {
            Parameter = new TransientStepViewModel(circuit).Options.Single(o =>
                o.Component == capacitor && o.PropertyName == nameof(Capacitor.Capacitance)),
            Start = 0.5e-6,
            Stop = 4e-6,
            Count = 4,
            Duration = 20e-3,
        };

        model.Run();

        Assert.Equal(4, model.Curves.Count);

        var plot = DocPlot.New("Time (s)", "Capacitor voltage (V)");
        DocPlot.SiBottom(plot);

        for (var i = 0; i < model.Curves.Count; i++)
        {
            var curve = model.Curves[i];

            DocPlot.Line(plot, curve.Times, curve.Values, i,
                Cirq.Core.Units.SiPrefix.Format(curve.StepValue, "F"));
        }

        DocPlot.Legend(plot);
        DocPlot.Title(plot, "One step response per capacitor value, from the same starting conditions");
        DocPlot.Save(plot, "21-stepped-transient.png");
    }

    // ---- a device curve ----------------------------------------------------

    /// <summary>A diode's exponential, which is the curve the part is specified by.</summary>
    [Fact]
    public void DiodeCurve()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(0.0) { Name = "V1" });
        var diode = circuit.Add(new Diode { Name = "D1" });
        var ground = Gnd(circuit);

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, diode.Anode);
        circuit.Connect(diode.Cathode, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Id", diode.Anode, default) { Kind = ProbeKind.Current });

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        var result = new DcSweep(sim).Run(new DcSweepRequest(
            new SweepTarget(supply, nameof(DcVoltageSource.Voltage), 0, 0.8, 120)));

        var current = result.Curves[0].Traces[0].Values;

        // An exponential: a tenth of a volt more is several times the current.
        Assert.True(Math.Abs(current[^1]) > Math.Abs(current[^20]) * 5,
            "the last tenth of a volt should multiply the current several times over");

        var plot = DocPlot.New("Forward voltage (V)", "Forward current (A)");
        DocPlot.SiLeft(plot);
        DocPlot.Line(plot, result.X, current.Select(Math.Abs), 0);

        DocPlot.Title(plot, "A 1N4148's forward curve — the exponential the part is specified by");
        DocPlot.Save(plot, "22-diode-curve.png");
    }

    // ---- tolerance ---------------------------------------------------------

    /// <summary>
    /// The same divider built two hundred times from parts out of a bag, which is the only
    /// analysis here that asks whether a design survives the parts you can actually buy.
    /// </summary>
    [Fact]
    public void ToleranceSpread()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(10.0));
        var top = circuit.Add(new Resistor(10e3) { Name = "R1", Tolerance = 0.05 });
        var bottom = circuit.Add(new Resistor(10e3) { Name = "R2", Tolerance = 0.05 });
        var ground = Gnd(circuit);

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, ground.Pin);

        circuit.Probes.Add(new SignalProbe("Mid", bottom.A, default));

        var result = new MonteCarlo(circuit).Run(new MonteCarloRequest(400, Seed: 7));
        var trace = result.Traces[0];

        Assert.Equal(5.0, trace.Nominal, 0.01);
        Assert.True(trace.WorstFractionalError > 0.01,
            "five percent resistors should move a divider by more than one percent");

        var buckets = trace.Histogram(25);

        var plot = DocPlot.New("Output (V)", "Trials");

        var bars = plot.Add.Bars(buckets.Select(b => new ScottPlot.Bar
        {
            Position = b.Centre,
            Value = b.Count,
            Size = (buckets[^1].Centre - buckets[0].Centre) / buckets.Length * 0.9,
            FillColor = DocPlot.Traces[0],
            LineColor = DocPlot.Traces[0],
        }).ToArray());

        _ = bars;

        DocPlot.Title(plot,
            $"Two 5 % resistors, {result.Trials} builds — nominal {trace.Nominal:0.00} V, " +
            $"worst {trace.WorstFractionalError:P1} out");

        DocPlot.Save(plot, "23-tolerance.png");
    }

    // ---- temperature -------------------------------------------------------

    /// <summary>
    /// The TL431's bandgap over temperature: an arch, not a line. Both ends of the range sit below
    /// the middle, which is the whole reason the part costs more than a zener.
    /// </summary>
    [Fact]
    public void BandgapOverTemperature()
    {
        var circuit = new Circuit();

        var supply = circuit.Add(new DcVoltageSource(12.0));
        var series = circuit.Add(new Resistor(1e3));
        var shunt = circuit.Add(new ShuntReference { Name = "U1" });
        var ground = Gnd(circuit);

        circuit.Connect(supply.Negative, ground.Pin);
        circuit.Connect(supply.Positive, series.A);
        circuit.Connect(series.B, shunt.Cathode);
        circuit.Connect(shunt.Anode, ground.Pin);
        circuit.Connect(shunt.Reference, shunt.Cathode);

        circuit.Probes.Add(new SignalProbe("Vref", series.B, default));

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.ResolveProbes();

        var result = new DcSweep(sim).Run(new DcSweepRequest(
            SweepTarget.OverTemperature(-40, 125, 56)));

        var values = result.Curves[0].Traces[0].Values;

        // The arch: the middle is above both ends, which no straight line can do.
        var peak = values.Max();
        Assert.True(peak > values[0] && peak > values[^1], "a bandgap is bowed, not sloped");

        var plot = DocPlot.New("Temperature (°C)", "Reference (V)");
        DocPlot.Line(plot, result.X, values, 0);

        DocPlot.Title(plot,
            "A TL431's reference is bowed, not sloped — both ends of the range sit below the middle");

        DocPlot.Save(plot, "24-bandgap.png");
    }
}
