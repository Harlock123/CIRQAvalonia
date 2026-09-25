using System.Collections.ObjectModel;
using Cirq.Core.Topology;
using Cirq.Core.Units;
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

        Parameters.Add(SweepOption.Temperature(circuit));

        foreach (var option in SweepOption.Discover(circuit)) Parameters.Add(option);

        Step = Parameters.FirstOrDefault(o => !o.IsTemperature);
    }

    /// <summary>
    /// Everything on the canvas that could be stepped. The same list the DC sweep offers, and
    /// deliberately the same: "which of these can I vary" should not have two different answers
    /// depending on which window is asking.
    /// </summary>
    public ObservableCollection<SweepOption> Parameters { get; } = [];

    /// <summary>
    /// Whether the sweep is run once per value of a parameter rather than once.
    /// <para>
    /// Off by default, because one curve is what somebody opening this window usually wants and a
    /// family of five takes five times as long to produce.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial bool IsStepping { get; set; }

    /// <summary>What to step.</summary>
    [ObservableProperty]
    public partial SweepOption? Step { get; set; }

    [ObservableProperty]
    public partial double StepStart { get; set; }

    [ObservableProperty]
    public partial double StepStop { get; set; } = 1.0;

    /// <summary>How many values. Four or five is a family; twenty is a smear.</summary>
    [ObservableProperty]
    public partial int StepCount { get; set; } = 4;

    /// <summary>
    /// Choosing something to step fills the range in from whatever it is set to now, so the
    /// defaults are in the right decade rather than being zero to one for a 4k7 resistor.
    /// </summary>
    partial void OnStepChanged(SweepOption? value)
    {
        if (value is null) return;

        var current = value.Current;

        StepStart = current;
        StepStop = Math.Abs(current) < 1e-12 ? 1.0 : current * 4.0;
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

        var request = new AcSweepRequest(StartHz, StopHz, Math.Max(PointsPerDecade, 2));

        if (IsStepping && Step is not null)
        {
            Family(simulator, request);
            return;
        }

        var result = new AcSweep(simulator).Run(request);

        Collect(result, string.Empty);

        Status = Summarise(result);
    }

    /// <summary>
    /// One sweep per value, all on the same axes.
    /// <para>
    /// What the plot is read for is how the response <i>moves</i> — where the corner goes as the
    /// capacitor changes, which feedback resistor stops it peaking — and that is a question about
    /// the family rather than about any one curve in it. So the summary is the corners against the
    /// values rather than a corner per trace.
    /// </para>
    /// </summary>
    private void Family(CircuitSimulator simulator, AcSweepRequest request)
    {
        var target = Step!.IsTemperature
            ? SweepTarget.OverTemperature(StepStart, StepStop, Math.Max(StepCount, 2))
            : new SweepTarget(Step.Component, Step.PropertyName, StepStart, StepStop, Math.Max(StepCount, 2));

        var family = new SteppedAcSweep(simulator).Run(new AcStepRequest(request, target));

        var name = Step.IsTemperature ? "T" : Step.Component!.Name;

        foreach (var pass in family.Runs)
        {
            if (pass.Result is null) continue;

            Collect(pass.Result, $" @ {name} = {SiPrefix.Format(pass.Value, Step.Unit)}");
        }

        List<string> parts = [];

        foreach (var pass in family.Runs)
        {
            var value = SiPrefix.Format(pass.Value, Step.Unit);

            if (pass.Result is null)
            {
                parts.Add($"{value}: would not solve");
                continue;
            }

            // The first probe's corner stands for the pass. A family with four probes and five
            // values is twenty corners, which is a table rather than a line.
            var corner = pass.Result.Traces
                .Select(t => pass.Result.CornerOf(t.Label))
                .FirstOrDefault(c => c is not null);

            parts.Add(corner is null
                ? $"{value}: no corner in span"
                : $"{value}: −3 dB at {SiPrefix.Format(corner.Value, "Hz")}");
        }

        Status = parts.Count == 0
            ? "Nothing came back from any pass."
            : $"{name} stepped — {string.Join("   ·   ", parts)}";
    }

    /// <summary>Turns one sweep's traces into curves, with a suffix for which pass they came from.</summary>
    private void Collect(AcSweepResult result, string suffix)
    {
        foreach (var trace in result.Traces)
        {
            List<double> decibels = [];
            List<double> degrees = [];

            for (var i = 0; i < result.Frequencies.Count; i++)
            {
                decibels.Add(trace.Decibels(i));
                degrees.Add(trace.Degrees(i));
            }

            Curves.Add(new ResponseCurve(
                trace.Label + suffix, result.Frequencies, decibels, degrees));
        }
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
