using System.Collections.ObjectModel;
using Cirq.Components.Hierarchy;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Engine.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>
/// The stability window: how much gain goes round a feedback loop, and how close it is to going
/// round it the wrong way.
/// <para>
/// This answers "will it oscillate", which is the single most common reason a circuit that is
/// correct on paper does not work on a bench. A regulator that rings, an amplifier that sings at
/// two megahertz, a servo that hunts — all the same question, and none of them answerable by
/// looking at gain alone. A loop with plenty of gain and no phase margin is an oscillator.
/// </para>
/// </summary>
public sealed partial class StabilityViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public StabilityViewModel(Circuit circuit)
    {
        _circuit = circuit;

        foreach (var probe in Flattening.Flatten(circuit.Components)
                     .OfType<LoopProbe>())
        {
            Probes.Add(probe);
        }

        Probe = Probes.FirstOrDefault();
    }

    /// <summary>The loop probes on the drawing.</summary>
    public ObservableCollection<LoopProbe> Probes { get; } = [];

    /// <summary>Where the loop is being broken.</summary>
    [ObservableProperty]
    public partial LoopProbe? Probe { get; set; }

    /// <summary>
    /// Wide by default, and deliberately wider than a frequency response would be. A loop's DC
    /// gain needs the bottom of the band to be well below the dominant pole, and its crossover can
    /// be anywhere up to the amplifier's gain-bandwidth product.
    /// </summary>
    [ObservableProperty]
    public partial double StartHz { get; set; } = 0.1;

    [ObservableProperty]
    public partial double StopHz { get; set; } = 1e7;

    [ObservableProperty]
    public partial int PointsPerDecade { get; set; } = 25;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>True when the circuit has nowhere to break a loop.</summary>
    public bool HasProbes => Probes.Count > 0;

    public IReadOnlyList<double> Frequencies { get; private set; } = [];

    /// <summary>Loop gain in decibels at each frequency.</summary>
    public IReadOnlyList<double> Decibels { get; private set; } = [];

    /// <summary>Loop phase in degrees, unwrapped.</summary>
    public IReadOnlyList<double> Degrees { get; private set; } = [];

    /// <summary>Where the loop gain passes through one, or null when it does not.</summary>
    public double? CrossoverHz { get; private set; }

    public double? PhaseMarginDegrees { get; private set; }

    public double? GainMarginDb { get; private set; }

    /// <summary>What to make of the margin, in words.</summary>
    public string Verdict { get; private set; } = string.Empty;

    public bool HasResult => Decibels.Count > 0;

    /// <summary>The margin as the window prints it.</summary>
    public string PhaseMargin => PhaseMarginDegrees is { } m ? $"{m:0.#}°" : "—";

    public string GainMargin => GainMarginDb is { } g ? $"{g:0.#} dB" : "—";

    public string Crossover => CrossoverHz is { } f ? SiPrefix.Format(f, "Hz") : "—";

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
            Clear();
            Status = Describe(ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasResult));
            OnPropertyChanged(nameof(PhaseMargin));
            OnPropertyChanged(nameof(GainMargin));
            OnPropertyChanged(nameof(Crossover));
            OnPropertyChanged(nameof(Verdict));
            ResultChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Clear()
    {
        Frequencies = [];
        Decibels = [];
        Degrees = [];
        CrossoverHz = null;
        PhaseMarginDegrees = null;
        GainMarginDb = null;
        Verdict = string.Empty;
    }

    private void Execute()
    {
        Clear();

        if (Probe is null)
        {
            Status = "There is nowhere to break the loop. Put a Loop Probe (in Sources) into the " +
                     "feedback path — between a divider's tap and the input it feeds is the usual " +
                     "place — and run this again.";
            return;
        }

        // Its own simulator, as the other analyses use: this sweeps the circuit many times over
        // and must not disturb what the canvas is doing.
        var simulator = new CircuitSimulator(_circuit);

        simulator.Reset();
        simulator.SolveOperatingPoint();

        var sweep = new AcSweepRequest(StartHz, StopHz, Math.Max(PointsPerDecade, 2));
        var result = new StabilityAnalysis(simulator).Run(new StabilityRequest(sweep, Probe));

        if (!result.IsUsable)
        {
            Status = result.Problem ?? "The analysis found nothing to report.";
            return;
        }

        Frequencies = result.Frequencies;
        Decibels = [.. Enumerable.Range(0, result.LoopGain.Count).Select(result.Decibels)];
        Degrees = result.Degrees;

        CrossoverHz = result.CrossoverHz;
        PhaseMarginDegrees = result.PhaseMarginDegrees;
        GainMarginDb = result.GainMarginDb;
        Verdict = result.Verdict;

        List<string> parts = [];

        if (result.LowFrequencyDecibels is { } dc) parts.Add($"{dc:0.#} dB of loop gain at DC");
        if (CrossoverHz is { } f) parts.Add($"crossing one at {SiPrefix.Format(f, "Hz")}");

        parts.Add(result.Verdict);

        Status = string.Join("   ·   ", parts);
    }

    private static string Describe(Exception ex) => ex switch
    {
        CircuitTopologyException => $"The circuit will not build: {ex.Message}",
        ConvergenceException =>
            "The circuit has no operating point to linearise about: " + ex.Message,
        _ => $"The analysis failed: {ex.Message}",
    };
}
