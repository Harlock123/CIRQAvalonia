using System.Collections.ObjectModel;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Engine.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>One generator's line in the ranking.</summary>
public sealed record NoiseRow(string Name, double Rms, double Share)
{
    public string Contribution => SiPrefix.Format(Rms, "V");

    public string Percent => Share >= 0.001 ? $"{Share * 100:0.#} %" : "< 0.1 %";

    /// <summary>A bar drawn in text, so the ranking reads at a glance rather than by comparing numbers.</summary>
    public string Bar => new('█', Math.Max((int)Math.Round(Share * 24), Share > 0.005 ? 1 : 0));
}

/// <summary>
/// The noise window: how much a circuit makes, and which part is making it.
/// <para>
/// Noise is the floor under everything — it decides the smallest signal a circuit can be asked to
/// handle — and it is invisible in every other analysis here, because none of them has any noise
/// in it. A transient draws a clean line and a Bode plot draws a clean curve however noisy the
/// circuit actually is.
/// </para>
/// <para>
/// The ranking is the half that changes what you do. "This makes 12 µV" is a number; "and 80 % of
/// it is R3" is an instruction — and it is almost always one part, and almost always not the one
/// people guess.
/// </para>
/// </summary>
public sealed partial class NoiseViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public NoiseViewModel(Circuit circuit)
    {
        _circuit = circuit;

        foreach (var probe in circuit.Probes) Outputs.Add(probe);

        // The last probe rather than the first. People put the input probe on first and the output
        // probe on last, and an input is usually a node driven by a source — which has no
        // impedance, and therefore no noise on it at all. Opening on a guaranteed zero would look
        // like the analysis had failed.
        Output = Outputs.LastOrDefault();
    }

    /// <summary>The probes that could be the output.</summary>
    public ObservableCollection<SignalProbe> Outputs { get; } = [];

    /// <summary>Which node's noise is being measured.</summary>
    [ObservableProperty]
    public partial SignalProbe? Output { get; set; }

    [ObservableProperty]
    public partial double StartHz { get; set; } = 10;

    [ObservableProperty]
    public partial double StopHz { get; set; } = 1e5;

    [ObservableProperty]
    public partial int PointsPerDecade { get; set; } = 20;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>
    /// What the curves read wherever the pointer is. Empty when it is not over the plot, so a
    /// stale reading is never left sitting under a pointer that has moved away.
    /// </summary>
    [ObservableProperty]
    public partial string Readout { get; set; } = string.Empty;

    /// <summary>The density curve, for the plot.</summary>
    public IReadOnlyList<double> Frequencies { get; private set; } = [];

    /// <summary>Output noise in volts per root hertz at each frequency.</summary>
    public IReadOnlyList<double> Density { get; private set; } = [];

    /// <summary>Total output noise across the whole band, in volts RMS.</summary>
    public double Rms { get; private set; }

    /// <summary>Where it comes from, largest first.</summary>
    public ObservableCollection<NoiseRow> Contributors { get; } = [];

    public bool HasResult => Density.Count > 0;

    public bool HasOutputs => Outputs.Count > 0;

    /// <summary>Raised when a new result is ready, so the view can redraw.</summary>
    public event EventHandler? ResultChanged;

    [RelayCommand]
    public void Run()
    {
        IsBusy = true;

        try
        {
            Execute();
        }
        catch (Exception ex)
        {
            Frequencies = [];
            Density = [];
            Rms = 0;
            Contributors.Clear();
            Status = Describe(ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasResult));
            ResultChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Execute()
    {
        Contributors.Clear();
        Frequencies = [];
        Density = [];
        Rms = 0;

        if (Output is null)
        {
            Status = "Nothing to measure — put a probe on the node whose noise you care about.";
            return;
        }

        // Its own simulator, as the other analyses use: this must not disturb the canvas, and it
        // wants the bias point rather than wherever a transient got to.
        var simulator = new CircuitSimulator(_circuit);

        simulator.Reset();
        simulator.SolveOperatingPoint();
        simulator.ResolveProbes();

        var sweep = new AcSweepRequest(StartHz, StopHz, Math.Max(PointsPerDecade, 2));
        var result = new NoiseAnalysis(simulator).Run(new NoiseRequest(sweep, Output));

        if (!result.IsUsable)
        {
            Status = result.Problem ?? "The analysis found nothing to report.";
            return;
        }

        if (result.Rms <= 0)
        {
            Status = $"There is no noise at {Output.Label}. A node held by an ideal source has no " +
                     "impedance for a noise current to work into — probe somewhere the circuit is " +
                     "free to move.";
            return;
        }

        Frequencies = result.Frequencies;
        Density = result.Density;
        Rms = result.Rms;

        foreach (var contributor in result.Contributors)
            Contributors.Add(new NoiseRow(contributor.Name, contributor.Rms, contributor.Share));

        Status = Summarise(result);
    }

    private string Summarise(NoiseResult result)
    {
        List<string> parts =
        [
            $"{SiPrefix.Format(result.Rms, "V")} rms over " +
            $"{SiPrefix.Format(StartHz, "Hz")} to {SiPrefix.Format(StopHz, "Hz")}",
        ];

        if (result.Worst() is { } worst)
        {
            parts.Add($"worst {SiPrefix.Format(worst.Density, "V/√Hz")} at " +
                      $"{SiPrefix.Format(worst.Frequency, "Hz")}");
        }

        if (result.Contributors.Count > 0)
        {
            var top = result.Contributors[0];
            parts.Add($"{top.Percent:0.#} % of it is {top.Name}");
        }

        return string.Join("   ·   ", parts);
    }

    private static string Describe(Exception ex) => ex switch
    {
        CircuitTopologyException => $"The circuit will not build: {ex.Message}",
        ConvergenceException => "The circuit has no bias point to measure noise about: " + ex.Message,
        _ => $"The analysis failed: {ex.Message}",
    };
}
