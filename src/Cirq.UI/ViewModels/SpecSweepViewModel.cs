using System.Collections.ObjectModel;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Core.Verification;
using Cirq.Engine.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>One requirement's verdict across the range, dressed for the list.</summary>
public sealed class SpecMarginRow
{
    public SpecMarginRow(SpecMargin margin, string unit)
    {
        Margin = margin;
        Name = margin.Spec.Name.Length > 0 ? margin.Spec.Name : margin.Spec.Trace;
        Requirement = margin.Spec.Describe();

        Status = margin.Judged.Count == 0 ? "No verdict" : margin.Fails ? "Fails" : "Holds";

        Detail = margin.Judged.Count == 0
            ? margin.Points.Count == 0
                ? "The circuit could not be solved anywhere in the range."
                : margin.Points[0].Result.Explanation
            : margin.Fails
                ? margin.Window is { } window
                    ? $"Met from {Format(window.From, unit)} to {Format(window.To, unit)}; " +
                      $"worst at {Format(margin.Worst!.Value, unit)}, {Worst(margin)}"
                    : $"Not met anywhere in the range — worst at {Format(margin.Worst!.Value, unit)}, {Worst(margin)}"
                : $"Met everywhere. Closest at {Format(margin.Worst!.Value, unit)}, " +
                  $"with {Room(margin)} to spare.";
    }

    public SpecMargin Margin { get; }

    public string Name { get; }

    public string Requirement { get; }

    /// <summary>The word for the chip at the left of the row.</summary>
    public string Status { get; }

    public string Detail { get; }

    public bool IsFailing => Margin.Fails;

    public bool IsUnjudged => Margin.Judged.Count == 0;

    private static string Worst(SpecMargin margin) =>
        margin.Worst!.Result.Measured is { } measured
            ? $"where it reads {SiPrefix.Format(measured, margin.Spec.Unit, 3)}"
            : "where there was nothing to measure";

    private static string Room(SpecMargin margin) =>
        margin.Worst!.Result.Margin is { } room ? $"{room * 100:0.#} %" : "an unknown margin";

    private static string Format(double value, string unit) =>
        unit == "°C" ? $"{value:0.#} °C" : SiPrefix.Format(value, unit, 3);
}

/// <summary>One requirement's margin, as a curve.</summary>
/// <param name="Label">What the legend says.</param>
/// <param name="Values">The swept value at each point.</param>
/// <param name="Margins">How much room was left there, as a fraction of the limit.</param>
public sealed record MarginCurve(string Label, IReadOnlyList<double> Values, IReadOnlyList<double> Margins);

/// <summary>
/// Does it still meet its requirements across the range?
/// <para>
/// The requirements window holds the circuit to what it is supposed to do at one temperature, with
/// the parts at the values written on them. That is the question nobody is actually signed off
/// against. A design is signed off over a <i>range</i> — −40 °C to +85 °C, or the feedback resistor
/// anywhere in its tolerance band — and a circuit checked at 27 °C and shipped is a circuit nobody
/// has checked at the temperature it will be used at.
/// </para>
/// <para>
/// What is plotted is the <b>margin</b> rather than the measurement, because the measurements are
/// in different units and the margins are not: every requirement, whether it is about millivolts or
/// microseconds, is a fraction of its own limit, and zero is the line it must not cross. Several
/// requirements on one pair of axes, and one line that means failure.
/// </para>
/// </summary>
public sealed partial class SpecSweepViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public SpecSweepViewModel(Circuit circuit)
    {
        _circuit = circuit;

        Options.Add(SweepOption.Temperature(circuit));

        foreach (var option in SweepOption.Discover(circuit)) Options.Add(option);

        // Temperature first, because it is the range almost every design is specified over and the
        // only one that moves every part at once.
        Parameter = Options.FirstOrDefault();

        Status = _circuit.Specs.Count == 0
            ? "No requirements yet. Simulate > Requirements is where they are written down."
            : $"{Enabled} requirement(s) to check. Pick a range and press Run.";
    }

    public ObservableCollection<SweepOption> Options { get; } = [];

    [ObservableProperty]
    public partial SweepOption? Parameter { get; set; }

    /// <summary>The bottom of the range. The commercial and industrial floor, to start with.</summary>
    [ObservableProperty]
    public partial double Start { get; set; } = -40;

    [ObservableProperty]
    public partial double Stop { get; set; } = 85;

    [ObservableProperty]
    public partial int Count { get; set; } = 8;

    public static IReadOnlyList<int> CountOptions { get; } = [3, 4, 5, 6, 8, 10, 12, 16];

    /// <summary>How long to run the circuit at each point, in seconds.</summary>
    [ObservableProperty]
    public partial double Duration { get; set; } = 1e-3;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>One row per requirement.</summary>
    public ObservableCollection<SpecMarginRow> Rows { get; } = [];

    /// <summary>One curve per requirement, for the plot.</summary>
    public ObservableCollection<MarginCurve> Curves { get; } = [];

    public bool HasRows => Rows.Count > 0;

    /// <summary>Raised when there is something new to draw.</summary>
    public event EventHandler? CurvesChanged;

    private int Enabled => _circuit.Specs.Count(s => s.IsEnabled);

    partial void OnParameterChanged(SweepOption? value)
    {
        if (value is null) return;

        // A sensible range for what was chosen: the commercial temperature band, or half to double
        // whatever the part is set to now.
        if (value.IsTemperature)
        {
            Start = -40;
            Stop = 85;
            return;
        }

        var current = value.Current;

        if (current <= 0) return;

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
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException
                                       or CircuitTopologyException or ConvergenceException)
        {
            Rows.Clear();
            Curves.Clear();
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasRows));
            CurvesChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Execute()
    {
        Rows.Clear();
        Curves.Clear();

        if (Parameter is null)
        {
            Status = "Nothing on the canvas has a number that can be swept.";
            return;
        }

        if (Enabled == 0)
        {
            Status = "No requirements to check. Simulate > Requirements is where they are written down.";
            return;
        }

        if (_circuit.Probes.Count == 0)
        {
            Status = "Nothing to measure — put a probe on what the requirements are about.";
            return;
        }

        if (Duration <= 0)
        {
            Status = "Give each point a length to run for.";
            return;
        }

        // Its own simulator, like every other analysis that runs the circuit many times: this must
        // not disturb what the canvas and the scope are showing.
        var simulator = new CircuitSimulator(_circuit);

        simulator.Reset();
        simulator.ResolveProbes();

        var target = Parameter.IsTemperature
            ? SweepTarget.OverTemperature(Start, Stop, Math.Max(Count, 2))
            : new SweepTarget(
                Parameter.Component, Parameter.PropertyName, Start, Stop, Math.Max(Count, 2));

        var result = new SpecSweep(simulator).Run(new SpecSweepRequest(target, Duration));

        foreach (var margin in result.Margins)
        {
            Rows.Add(new SpecMarginRow(margin, result.Unit));

            var judged = margin.Judged.Where(p => p.Result.Margin is not null).ToList();

            if (judged.Count == 0) continue;

            Curves.Add(new MarginCurve(
                margin.Spec.Name.Length > 0 ? margin.Spec.Name : margin.Spec.Trace,
                [.. judged.Select(p => p.Value)],
                [.. judged.Select(p => p.Result.Margin!.Value)]));
        }

        // Failures first: the whole reason for sweeping is to find out whether there are any.
        var ordered = Rows.OrderByDescending(r => r.IsFailing).ThenBy(r => r.IsUnjudged).ToList();

        Rows.Clear();
        foreach (var row in ordered) Rows.Add(row);

        Status = result.Problems.Count == 0
            ? result.Summary()
            : $"{result.Summary()} {result.Problems.Count} point(s) could not be solved: {result.Problems[0]}";
    }

    /// <summary>What the plot's horizontal axis is called.</summary>
    public string AxisLabel => Parameter?.IsTemperature == true
        ? "Temperature (°C)"
        : Parameter is null ? "Value" : $"{Parameter.Component?.Name} {Parameter.PropertyName}";
}
