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

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>The spectra from the last transform, one per visible probe.</summary>
    public ObservableCollection<SpectrumCurve> Curves { get; } = [];

    public bool HasCurves => Curves.Count > 0;

    /// <summary>Raised when new curves are ready, so the view can redraw.</summary>
    public event EventHandler? CurvesChanged;

    partial void OnWindowChanged(SpectrumWindow value) => Run();

    partial void OnSizeChanged(int value) => Run();

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
            Status = $"The transform failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasCurves));
            CurvesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Transform()
    {
        Curves.Clear();

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
