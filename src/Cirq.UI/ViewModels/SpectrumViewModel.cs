using System.Collections.ObjectModel;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Engine.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>One probe's spectrum, ready to draw.</summary>
/// <param name="Label">The probe's name.</param>
/// <param name="Frequencies">Bin centres in hertz.</param>
/// <param name="Magnitudes">Amplitude in the probe's own units.</param>
/// <param name="Decibels">The same, in dB relative to one unit.</param>
public sealed record SpectrumCurve(
    string Label,
    IReadOnlyList<double> Frequencies,
    IReadOnlyList<double> Magnitudes,
    IReadOnlyList<double> Decibels);

/// <summary>One probe's distortion, as the window lists it.</summary>
public sealed record DistortionRow(string Label, string Unit, HarmonicAnalysis Analysis)
{
    public string Fundamental => SiPrefix.Format(Analysis.Fundamental, "Hz");

    public string Amplitude => SiPrefix.Format(Analysis.FundamentalAmplitude, Unit);

    /// <summary>
    /// THD as a percentage, or in parts per million once it is small enough that a percentage is
    /// all leading zeroes. Under a thousandth of a percent it is reported as a limit rather than a
    /// figure: below that the answer is the transform's own floor rather than the circuit's.
    /// </summary>
    public string Thd => Describe(Analysis.ThdPercent);

    public string ThdPlusNoise => Describe(Analysis.ThdPlusNoisePercent);

    public string Detail
    {
        get
        {
            if (!Analysis.IsUsable) return Analysis.Problem ?? string.Empty;

            var parts = Analysis.Harmonics
                .Where(h => h.Order > 1 && h.Relative > 1e-4)
                .OrderByDescending(h => h.Relative)
                .Take(4)
                .Select(h => $"H{h.Order} {h.Decibels:0.0} dBc");

            var line = string.Join("   ", parts);

            if (line.Length == 0) line = "no harmonic above −80 dBc";

            return Analysis.IsTruncated
                ? $"{line}   ·   only {Analysis.HarmonicsInBand} of " +
                  $"{Analysis.HarmonicsRequested} harmonics fit below {SiPrefix.Format(Nyquist, "Hz")}"
                : line;
        }
    }

    /// <summary>Where the band ran out, so a truncated answer can say where.</summary>
    public double Nyquist { get; init; }

    private static string Describe(double percent) => percent switch
    {
        < 1e-3 => "< 0.001 %",
        < 0.01 => $"{percent * 1e4:0.#} ppm",
        < 1 => $"{percent:0.000} %",
        < 10 => $"{percent:0.00} %",
        _ => $"{percent:0.0} %",
    };
}

/// <summary>
/// The spectrum window: what frequencies are in the traces the scope has already recorded.
/// <para>
/// It is not the same thing as the frequency response, and the difference is worth being clear
/// about. The response sweeps a small signal across a range and asks what the circuit <i>does</i>
/// to each frequency — it is a property of the circuit. This takes the waveform the circuit
/// actually produced and asks what is <i>in</i> it. One is a measurement of a filter; the other is
/// a measurement of a signal.
/// </para>
/// <para>
/// Half of what this library teaches lives here and has been invisible. A modulated carrier has
/// sidebands. A rectifier's output has harmonics at multiples of the line frequency, and a
/// full-wave one has them at twice the spacing of a half-wave one. A square wave is its
/// fundamental plus the odd harmonics. An amplifier driven into clipping grows harmonics that
/// were not in its input. None of that is visible in a picture of the waveform.
/// </para>
/// </summary>
public sealed partial class SpectrumViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public SpectrumViewModel(Circuit circuit)
    {
        _circuit = circuit;
    }

    /// <summary>How the block is weighted before transforming.</summary>
    [ObservableProperty]
    public partial SpectrumWindow Window { get; set; } = SpectrumWindow.Hann;

    /// <summary>The windows offered in the picker.</summary>
    public static IReadOnlyList<SpectrumWindow> WindowOptions { get; } =
        Enum.GetValues<SpectrumWindow>();

    /// <summary>Points in the transform, rounded down to a power of two.</summary>
    [ObservableProperty]
    public partial int Size { get; set; } = 4096;

    public static IReadOnlyList<int> SizeOptions { get; } = [512, 1024, 2048, 4096, 8192, 16384];

    /// <summary>
    /// True to plot decibels rather than the probe's own units. Logarithmic is how a spectrum is
    /// normally read — the interesting parts are usually the small ones, and a harmonic at a
    /// hundredth of the fundamental is invisible on a linear axis and obvious on a log one.
    /// </summary>
    [ObservableProperty]
    public partial bool IsLogarithmic { get; set; } = true;

    /// <summary>
    /// How many harmonics to look for when measuring distortion. Nine is the usual count: past
    /// that they are almost always below the floor, and on anything but a very low fundamental
    /// they have run past the Nyquist limit anyway.
    /// </summary>
    [ObservableProperty]
    public partial int HarmonicCount { get; set; } = 9;

    public static IReadOnlyList<int> HarmonicCountOptions { get; } = [3, 5, 7, 9, 15, 25];

    /// <summary>
    /// The fundamental to measure against, in hertz, or zero to take the largest component in
    /// each trace.
    /// <para>
    /// Worth stating when you know it. At heavy distortion a harmonic can be the largest thing in
    /// the spectrum — a badly biased stage can put more energy in the second harmonic than in the
    /// fundamental — and measuring everything against the wrong one gives an answer that is not
    /// wrong so much as about a different question.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double FundamentalHz { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>The spectra from the last transform, one per visible probe.</summary>
    public ObservableCollection<SpectrumCurve> Curves { get; } = [];

    /// <summary>
    /// What the same traces measure as distortion — the number an amplifier is sold on, and the
    /// one thing in this window that a picture of the spectrum cannot give you by eye.
    /// </summary>
    public ObservableCollection<DistortionRow> Distortion { get; } = [];

    public bool HasCurves => Curves.Count > 0;

    public bool HasDistortion => Distortion.Count > 0;

    /// <summary>Raised when new curves are ready, so the view can redraw.</summary>
    public event EventHandler? CurvesChanged;

    partial void OnWindowChanged(SpectrumWindow value) => Run();

    partial void OnSizeChanged(int value) => Run();

    partial void OnHarmonicCountChanged(int value) => Run();

    partial void OnFundamentalHzChanged(double value) => Run();

    partial void OnIsLogarithmicChanged(bool value) => CurvesChanged?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    public void Run()
    {
        IsBusy = true;

        try
        {
            Transform();
        }
        catch (Exception ex)
        {
            Curves.Clear();
            Distortion.Clear();
            Status = $"The transform failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasCurves));
            OnPropertyChanged(nameof(HasDistortion));
            CurvesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Transform()
    {
        Curves.Clear();
        Distortion.Clear();

        var probes = _circuit.Probes.Where(p => p.IsVisible).ToList();

        if (probes.Count == 0)
        {
            Status = "Nothing to transform — put a probe on a node and run the circuit.";
            return;
        }

        var request = new SpectrumRequest(Window, Size);

        List<string> notes = [];

        foreach (var probe in probes)
        {
            var samples = probe.HistoryBuffer.ToArray();

            if (samples.Length < 8)
            {
                notes.Add($"{probe.Label} has no trace yet");
                continue;
            }

            var result = Spectrum.Of(samples, samples[0].Time, samples[^1].Time, request);

            if (result.IsEmpty)
            {
                notes.Add($"{probe.Label} has too short a trace to transform");
                continue;
            }

            List<double> decibels = [];
            for (var i = 0; i < result.Magnitudes.Count; i++) decibels.Add(result.Decibels(i));

            Curves.Add(new SpectrumCurve(
                probe.Label, result.Frequencies, result.Magnitudes, decibels));

            Distortion.Add(new DistortionRow(
                probe.Label,
                probe.Unit,
                Harmonics.Of(result, FundamentalHz > 0 ? FundamentalHz : null, HarmonicCount))
            {
                Nyquist = result.Nyquist,
            });

            if (result.Peak() is { } peak)
            {
                notes.Add($"{probe.Label} peaks at {SiPrefix.Format(peak.Frequency, "Hz")} " +
                          $"({SiPrefix.Format(peak.Magnitude, probe.Unit)})");
            }

            // Said once: it is a property of the transform rather than of any one trace.
            if (notes.Count > 0 && Curves.Count == 1)
                notes.Insert(0, $"{SiPrefix.Format(result.Resolution, "Hz")} per bin, " +
                                $"up to {SiPrefix.Format(result.Nyquist, "Hz")}");
        }

        Status = notes.Count == 0
            ? "Nothing to transform."
            : string.Join("   ·   ", notes);
    }

    /// <summary>What the vertical axis is measuring, for the plot's label.</summary>
    public string VerticalLabel => IsLogarithmic ? "Magnitude (dB)" : "Amplitude";
}
