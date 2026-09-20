using System.Collections.ObjectModel;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>One probe's curve, ready to draw.</summary>
/// <param name="Label">The probe's name.</param>
/// <param name="Frequencies">Hertz.</param>
/// <param name="Decibels">Magnitude at each frequency.</param>
/// <param name="Degrees">Phase at each frequency.</param>
public sealed record ResponseCurve(
    string Label,
    IReadOnlyList<double> Frequencies,
    IReadOnlyList<double> Decibels,
    IReadOnlyList<double> Degrees);

/// <summary>
/// The frequency-response window: what to sweep, and what came back.
/// <para>
/// It runs against the circuit as it stands, linearised about its bias point, so what it measures
/// is the circuit on the canvas rather than a copy of it. Probes are the traces — the same ones the
/// oscilloscope uses — so putting a probe somewhere and opening this asks "what does the circuit do
/// to a small signal, here, at every frequency" without any further setting up.
/// </para>
/// </summary>
public sealed partial class FrequencyResponseViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public FrequencyResponseViewModel(Circuit circuit)
    {
        _circuit = circuit;
    }

    /// <summary>Lowest frequency to solve, in hertz.</summary>
    [ObservableProperty]
    public partial double StartHz { get; set; } = 10.0;

    /// <summary>Highest frequency to solve.</summary>
    [ObservableProperty]
    public partial double StopHz { get; set; } = 1e6;

    /// <summary>How finely the span is divided.</summary>
    [ObservableProperty]
    public partial int PointsPerDecade { get; set; } = 25;

    /// <summary>What went wrong, if anything did. Empty when the last sweep worked.</summary>
    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>True while a sweep is running, so the button can say so.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>The curves from the last sweep, one per probe.</summary>
    public ObservableCollection<ResponseCurve> Curves { get; } = [];

    /// <summary>Raised when new curves are ready, so the view can redraw.</summary>
    public event EventHandler? CurvesChanged;

    /// <summary>True when there is something to draw.</summary>
    public bool HasCurves => Curves.Count > 0;

    [RelayCommand]
    public void Run()
    {
        IsBusy = true;

        try
        {
            Sweep();
        }
        catch (Exception ex)
        {
            Curves.Clear();
            Status = Describe(ex);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasCurves));
            CurvesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Sweep()
    {
        Curves.Clear();

        if (_circuit.Probes.Count == 0)
        {
            Status = "Nothing to measure — put a probe on the node you want the response of.";
            return;
        }

        // A sweep of its own simulator, not the one the transport is running: this must not
        // disturb whatever the canvas is doing, and it needs the bias point rather than wherever
        // a running transient has got to.
        var simulator = new CircuitSimulator(_circuit);

        simulator.Reset();
        simulator.SolveOperatingPoint();
        simulator.ResolveProbes();

        var result = new AcSweep(simulator).Run(
            new AcSweepRequest(StartHz, StopHz, Math.Max(PointsPerDecade, 2)));

        foreach (var trace in result.Traces)
        {
            List<double> decibels = [];
            List<double> degrees = [];

            for (var i = 0; i < result.Frequencies.Count; i++)
            {
                decibels.Add(trace.Decibels(i));
                degrees.Add(trace.Degrees(i));
            }

            Curves.Add(new ResponseCurve(trace.Label, result.Frequencies, decibels, degrees));
        }

        Status = Summarise(result);
    }

    /// <summary>
    /// A line of arithmetic off the curves, because the number most people open this window for is
    /// where the response turns over and reading it off a plot by eye is a nuisance.
    /// </summary>
    private static string Summarise(AcSweepResult result)
    {
        List<string> parts = [];

        foreach (var trace in result.Traces)
        {
            if (result.CornerOf(trace.Label) is not { } corner) continue;

            parts.Add($"{trace.Label} −3 dB at {Cirq.Core.Units.SiPrefix.Format(corner, "Hz")}");
        }

        return parts.Count == 0
            ? $"{result.Frequencies.Count} points, no −3 dB corner inside the span"
            : string.Join("   ·   ", parts);
    }

    private static string Describe(Exception ex) => ex switch
    {
        CircuitTopologyException => $"The circuit will not build: {ex.Message}",
        _ => $"The sweep failed: {ex.Message}",
    };
}
