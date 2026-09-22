using System.Collections.ObjectModel;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Engine.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>One trace to draw: a probe, from one pass of the family.</summary>
/// <param name="Label">What goes in the legend, including which value produced it.</param>
/// <param name="Kind">Voltage, current or logic — which decides the axis it belongs on.</param>
/// <param name="Unit">The unit the probe reports in.</param>
/// <param name="Times">Seconds.</param>
/// <param name="Values">What the probe said.</param>
/// <param name="StepValue">The parameter value this pass ran at, for colouring the family.</param>
public sealed record StepCurve(
    string Label,
    ProbeKind Kind,
    string Unit,
    IReadOnlyList<double> Times,
    IReadOnlyList<double> Values,
    double StepValue);

/// <summary>
/// The parameter-step window: run the same transient several times over, with one value changed
/// each time, and lay the results on top of each other.
/// <para>
/// A DC sweep answers where a circuit <i>settles</i> for each value of something. This answers how
/// it <b>gets</b> there, which is the question people ask far more often and have had no way to
/// ask: try three capacitor values and watch the ringing change; try four gate resistors and watch
/// the switching edge; find the feedback resistor where the step response stops overshooting.
/// </para>
/// <para>
/// The trick that makes it useful is that the curves are on one set of axes. Running a circuit
/// four times by hand and looking at four separate pictures tells you much less than laying the
/// four on top of each other does, because what matters is the difference between them.
/// </para>
/// </summary>
public sealed partial class TransientStepViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public TransientStepViewModel(Circuit circuit)
    {
        _circuit = circuit;

        Options.Add(SweepOption.Temperature(circuit));

        foreach (var option in SweepOption.Discover(circuit)) Options.Add(option);

        // A passive is the likelier thing to step here — a source is what a DC sweep is for, and
        // stepping one across a transient usually just scales the picture.
        Parameter = Options.FirstOrDefault(IsReactive)
                    ?? Options.FirstOrDefault(o => !o.IsTemperature)
                    ?? Options.FirstOrDefault();
    }

    /// <summary>Everything on the canvas that could be stepped.</summary>
    public ObservableCollection<SweepOption> Options { get; } = [];

    /// <summary>What to vary between passes.</summary>
    [ObservableProperty]
    public partial SweepOption? Parameter { get; set; }

    [ObservableProperty]
    public partial double Start { get; set; }

    [ObservableProperty]
    public partial double Stop { get; set; } = 1.0;

    /// <summary>
    /// How many passes. Small on purpose: this runs the whole circuit once per value, so it costs
    /// what a transient costs multiplied by this — and more than about six curves on one set of
    /// axes stops being readable anyway.
    /// </summary>
    [ObservableProperty]
    public partial int Count { get; set; } = 4;

    public static IReadOnlyList<int> CountOptions { get; } = [2, 3, 4, 5, 6, 8, 10];

    /// <summary>How long to run each pass, in seconds.</summary>
    [ObservableProperty]
    public partial double Duration { get; set; } = 1e-3;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>The traces from the last run, ready to draw.</summary>
    public ObservableCollection<StepCurve> Curves { get; } = [];

    public bool HasCurves => Curves.Count > 0;

    /// <summary>True when the circuit has nothing that can be stepped at all.</summary>
    public bool HasOptions => Options.Count > 0;

    /// <summary>Raised when new curves are ready, so the view can redraw.</summary>
    public event EventHandler? CurvesChanged;

    partial void OnParameterChanged(SweepOption? value) => ResetRangeFromSelection();

    /// <summary>
    /// A range around wherever the thing is set now, rather than a fixed span: a capacitor lives
    /// in nanofarads and a load resistor in kilohms, and 0-to-1 is wrong for both. Half to double
    /// is the range somebody trying values would try.
    /// </summary>
    private void ResetRangeFromSelection()
    {
        if (Parameter is null) return;

        if (Parameter.IsTemperature)
        {
            Start = -40;
            Stop = 125;
            return;
        }

        var current = Parameter.Current;

        if (Math.Abs(current) < 1e-15)
        {
            Start = 0;
            Stop = 1.0;
            return;
        }

        Start = current / 2.0;
        Stop = current * 2.0;
    }

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

    private void Execute()
    {
        Curves.Clear();

        if (Parameter is null)
        {
            Status = "Nothing on the canvas has a number that can be stepped.";
            return;
        }

        if (_circuit.Probes.Count == 0)
        {
            Status = "Nothing to record — put a probe on the node you want to watch.";
            return;
        }

        if (Duration <= 0)
        {
            Status = "Give each pass a length to run for.";
            return;
        }

        // Its own simulator, as the DC sweep and the frequency response both use: this runs the
        // circuit several times over and must not disturb whatever the canvas is doing.
        var simulator = new CircuitSimulator(_circuit);

        simulator.Reset();
        simulator.ResolveProbes();

        var target = Parameter.IsTemperature
            ? SweepTarget.OverTemperature(Start, Stop, Math.Max(Count, 2))
            : new SweepTarget(
                Parameter.Component, Parameter.PropertyName, Start, Stop, Math.Max(Count, 2));

        var result = new TransientStep(simulator)
            .Run(new TransientStepRequest(target, Duration));

        var name = Parameter.IsTemperature ? "T" : Parameter.Component!.Name;

        foreach (var run in result.Runs)
        foreach (var trace in run.Traces)
        {
            // The value goes in the legend, because on a family of curves it is the only thing
            // telling one from another.
            Curves.Add(new StepCurve(
                $"{trace.Label} @ {name} = {SiPrefix.Format(run.Value, Parameter.Unit)}",
                trace.Kind,
                trace.Unit,
                [.. trace.Samples.Select(s => s.Time)],
                [.. trace.Samples.Select(s => s.Value)],
                run.Value));
        }

        Status = Summarise(result);
    }

    /// <summary>
    /// A line of arithmetic off the family. What is worth saying about a set of transients is how
    /// each one ended and how far it overshot on the way — which is the whole of what stepping a
    /// damping element is for.
    /// </summary>
    private string Summarise(TransientStepResult result)
    {
        if (result.IsEmpty)
            return "No pass could be run. The range may be asking for something the circuit " +
                   "cannot be solved at.";

        List<string> parts = [$"{result.Succeeded} runs of {SiPrefix.Format(Duration, "s")}"];

        // One probe's worth, otherwise the line is unreadable on a circuit with four probes.
        var first = Curves.Where(c => c.Label.StartsWith(Curves[0].Label.Split(" @ ")[0], StringComparison.Ordinal));

        foreach (var curve in first.Take(4))
        {
            if (curve.Values.Count == 0) continue;

            var settled = curve.Values[^1];
            var peak = curve.Values.Aggregate(0.0, (best, v) => Math.Abs(v) > Math.Abs(best) ? v : best);

            var overshoot = Math.Abs(settled) > 1e-12
                ? (Math.Abs(peak) - Math.Abs(settled)) / Math.Abs(settled) * 100.0
                : 0.0;

            var value = SiPrefix.Format(curve.StepValue, Parameter!.Unit);

            parts.Add(overshoot > 1.0
                ? $"{value}: settles at {SiPrefix.Format(settled, curve.Unit)}, {overshoot:0.#} % overshoot"
                : $"{value}: settles at {SiPrefix.Format(settled, curve.Unit)}");
        }

        var failed = result.Runs.Count(r => !r.Succeeded);
        if (failed > 0) parts.Add($"{failed} would not solve");

        return string.Join("   ·   ", parts);
    }

    /// <summary>
    /// A capacitor or an inductor: the things whose value decides the <i>shape</i> of a transient
    /// rather than its size, so the likeliest first thing anybody wants to step.
    /// </summary>
    private static bool IsReactive(SweepOption option) =>
        option.PropertyName is "Capacitance" or "Inductance";

    private static string Describe(Exception ex) => ex switch
    {
        CircuitTopologyException => $"The circuit will not build: {ex.Message}",
        ArgumentException => ex.Message,
        _ => $"The run failed: {ex.Message}",
    };
}
