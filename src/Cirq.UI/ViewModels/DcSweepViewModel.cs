using System.Collections.ObjectModel;
using Cirq.Components.Hierarchy;
using Cirq.Components.Serialization;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Engine.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Cirq.UI.ViewModels;

/// <summary>
/// One thing on the canvas that can be swept: a component and one of its numbers.
/// </summary>
public sealed record SweepOption(CircuitComponent? Component, string PropertyName, string Unit)
{
    /// <summary>
    /// The circuit's temperature, which belongs to no part — every junction reads it at once — and
    /// so cannot be found by looking at components' properties the way the rest are.
    /// </summary>
    public static SweepOption Temperature(Circuit circuit) =>
        new(null, SweepTarget.TemperatureProperty, "°C") { Circuit = circuit };

    /// <summary>Set only on the temperature option, so it can read the circuit's current value.</summary>
    public Circuit? Circuit { get; init; }

    public bool IsTemperature => Component is null;

    /// <summary>What the picker shows, e.g. "V1 · Voltage".</summary>
    public string Display => IsTemperature
        ? "Circuit · Temperature"
        : $"{Component!.Name} · {ParameterNaming.Humanise(PropertyName)}";

    /// <summary>Whatever it is set to now, which is where the range defaults are taken from.</summary>
    public double Current
    {
        get
        {
            if (IsTemperature) return Circuit?.AmbientTemperatureCelsius ?? 27.0;

            var property = Component!.GetType().GetProperty(PropertyName);
            return property is null ? 0.0 : Convert.ToDouble(property.GetValue(Component) ?? 0.0);
        }
    }

    public override string ToString() => Display;

    /// <summary>
    /// Everything varyable in a circuit: the same properties the inspector shows, filtered to the
    /// ones that are writable numbers. Taking the inspector's list rather than every public
    /// property keeps the picker to things a person would recognise as a setting.
    /// </summary>
    public static IEnumerable<SweepOption> Discover(Circuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        foreach (var component in Flattening.Flatten(circuit.Components))
        foreach (var property in ComponentReflection.EditableProperties(component.GetType()))
        {
            if (property.PropertyType != typeof(double) && property.PropertyType != typeof(int))
                continue;

            // Placement is not a circuit parameter, and varying it would just move the part.
            if (property.Name is "X" or "Y" or "RotationDegrees") continue;

            yield return new SweepOption(component, property.Name, ParameterNaming.UnitFor(property.Name));
        }
    }

    /// <summary>A supply or a current source, which is what anybody means by "sweep this".</summary>
    public static bool IsSource(SweepOption option) =>
        option?.Component?.DesignatorPrefix is "V" or "I" &&
        option.PropertyName is "Voltage" or "Current";
}

/// <summary>One curve to draw: a probe, within one value of the stepped parameter.</summary>
/// <param name="Label">What to put in the legend.</param>
/// <param name="Kind">Voltage, current or logic — which decides the axis it belongs on.</param>
/// <param name="X">The swept values.</param>
/// <param name="Y">What the probe said at each one. NaN where the solver could not get there.</param>
public sealed record SweepCurve(
    string Label, ProbeKind Kind, IReadOnlyList<double> X, IReadOnlyList<double> Y);

/// <summary>
/// The DC sweep window: pick something to vary, pick how far, and get the curve.
/// <para>
/// This is the analysis that draws what a part is specified by. A diode's exponential, a
/// transistor's output characteristic, a MOSFET's square law, a panel's maximum power point and a
/// comparator's hysteresis are all curves of one DC quantity against another, and until now the
/// only way to see one was to build a ramp generator and run a transient — which works, slowly,
/// and tells you about the ramp as much as about the part.
/// </para>
/// <para>
/// The second, optional parameter is what turns a line into a curve tracer. Sweeping a
/// transistor's collector voltage gives one curve; stepping its base current as well gives the fan
/// of them off the front of the datasheet.
/// </para>
/// </summary>
public sealed partial class DcSweepViewModel : ObservableObject
{
    private readonly Circuit _circuit;

    public DcSweepViewModel(Circuit circuit)
    {
        _circuit = circuit;

        // Temperature first: it is the one that is not a part, and it is the sweep people come
        // looking for once they know it is there.
        Options.Add(SweepOption.Temperature(circuit));

        foreach (var option in SweepOption.Discover(circuit)) Options.Add(option);

        // A source is what anybody means by "DC sweep", so start on one if there is one.
        Sweep = Options.FirstOrDefault(SweepOption.IsSource) ?? Options.FirstOrDefault(o => !o.IsTemperature)
                ?? Options.FirstOrDefault();

        ResetRangeFromSelection();
    }

    /// <summary>Everything on the canvas that could be swept.</summary>
    public ObservableCollection<SweepOption> Options { get; } = [];

    /// <summary>What goes along the X axis.</summary>
    [ObservableProperty]
    public partial SweepOption? Sweep { get; set; }

    [ObservableProperty]
    public partial double Start { get; set; }

    [ObservableProperty]
    public partial double Stop { get; set; } = 5.0;

    [ObservableProperty]
    public partial int Points { get; set; } = 101;

    /// <summary>True when a second parameter is being stepped to give a family of curves.</summary>
    [ObservableProperty]
    public partial bool IsStepping { get; set; }

    /// <summary>What to step, when stepping.</summary>
    [ObservableProperty]
    public partial SweepOption? Step { get; set; }

    [ObservableProperty]
    public partial double StepStart { get; set; }

    [ObservableProperty]
    public partial double StepStop { get; set; } = 1.0;

    [ObservableProperty]
    public partial int StepCount { get; set; } = 5;

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    /// <summary>The curves from the last sweep, ready to draw.</summary>
    public ObservableCollection<SweepCurve> Curves { get; } = [];

    /// <summary>What the X axis should be called.</summary>
    [ObservableProperty]
    public partial string XLabel { get; set; } = string.Empty;

    /// <summary>True when there is something worth drawing.</summary>
    public bool HasCurves => Curves.Count > 0;

    /// <summary>True when the circuit has nothing that can be swept at all.</summary>
    public bool HasOptions => Options.Count > 0;

    /// <summary>Raised when new curves are ready, so the view can redraw.</summary>
    public event EventHandler? CurvesChanged;

    partial void OnSweepChanged(SweepOption? value) => ResetRangeFromSelection();

    partial void OnStepChanged(SweepOption? value)
    {
        if (value is null) return;

        // A sensible family around wherever it is set: from its own value to four times it, or a
        // small span either side when it is sitting at zero.
        var current = value.Current;

        StepStart = current;
        StepStop = Math.Abs(current) < 1e-12 ? 1.0 : current * 4.0;
    }

    /// <summary>
    /// Ranges default to something the selected thing can actually do: zero to a little past where
    /// it is now. Landing on 0-to-5 whatever was picked is wrong more often than not — a bias
    /// current lives in microamps and a mains source in hundreds of volts.
    /// </summary>
    private void ResetRangeFromSelection()
    {
        if (Sweep is null) return;

        // The range a part is specified over, rather than zero to a bit past where it sits — a
        // temperature sweep from 0 °C would leave out half of what makes it interesting.
        if (Sweep.IsTemperature)
        {
            Start = -40;
            Stop = 125;
            return;
        }

        var current = Sweep.Current;

        Start = 0;
        Stop = Math.Abs(current) < 1e-12 ? 5.0 : current * 1.2;
    }

    /// <summary>
    /// The reading to aim for when solving backwards, in the probe's own units.
    /// </summary>
    [ObservableProperty]
    public partial double TargetValue { get; set; } = 5.0;

    /// <summary>What the search found, or why it found nothing.</summary>
    [ObservableProperty]
    public partial string SolveResult { get; private set; } = string.Empty;

    public bool HasSolveResult => SolveResult.Length > 0;

    partial void OnSolveResultChanged(string value) => OnPropertyChanged(nameof(HasSolveResult));

    /// <summary>
    /// Works the sweep backwards: what value of the swept parameter gives the target reading.
    /// <para>
    /// Every analysis here asks the same question in the same direction — given these parts, what
    /// does the circuit do. This is the one people actually have in front of them: the output has
    /// to be five volts, so <i>what resistor</i>. It searches the range the sweep is already set
    /// to, because that is the range somebody has already decided is sensible.
    /// </para>
    /// </summary>
    [RelayCommand]
    public void SolveForValue()
    {
        SolveResult = string.Empty;

        if (Sweep is null)
        {
            SolveResult = "Nothing on the canvas has a number that can be varied.";
            return;
        }

        var probe = _circuit.Probes.FirstOrDefault();

        if (probe is null)
        {
            SolveResult = "Nothing to measure — put a probe on the node you want the value for.";
            return;
        }

        try
        {
            var simulator = new CircuitSimulator(_circuit);

            simulator.Reset();
            simulator.SolveOperatingPoint();
            simulator.ResolveProbes();

            var target = Sweep.IsTemperature
                ? SweepTarget.OverTemperature(Start, Stop)
                : new SweepTarget(Sweep.Component, Sweep.PropertyName, Start, Stop);

            var result = new ValueSolver(simulator).Run(new ValueSearch(target, probe, TargetValue));

            if (!result.IsUsable)
            {
                SolveResult = result.Problem ?? "No value was found.";
                return;
            }

            var name = Sweep.IsTemperature ? "the temperature" : Sweep.Component!.Name;
            var unit = Sweep.Unit;

            var line = $"{name} = {SiPrefix.Format(result.Value, unit)} gives " +
                       $"{SiPrefix.Format(result.Achieved, probe.Unit)} at {probe.Label}";

            if (result.Nearest is { } nearest && result.NearestAchieved is { } achieved)
            {
                line += $"   ·   nearest E24 part {SiPrefix.Format(nearest, unit)} gives " +
                        $"{SiPrefix.Format(achieved, probe.Unit)} ({result.NearestError:P1} out)";
            }

            SolveResult = line;
        }
        catch (Exception ex)
        {
            SolveResult = Describe(ex);
        }
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

        if (Sweep is null)
        {
            Status = "Nothing on the canvas has a number that can be swept.";
            return;
        }

        if (_circuit.Probes.Count == 0)
        {
            Status = "Nothing to measure — put a probe on the node or part you want the curve of.";
            return;
        }

        // Its own simulator, as the frequency response uses: this must not disturb whatever the
        // canvas is running, and it wants the bias point rather than wherever a transient got to.
        var simulator = new CircuitSimulator(_circuit);

        simulator.Reset();
        simulator.SolveOperatingPoint();
        simulator.ResolveProbes();

        SweepTarget Build(SweepOption option, double start, double stop, int points) =>
            option.IsTemperature
                ? SweepTarget.OverTemperature(start, stop, Math.Max(points, 2))
                : new SweepTarget(option.Component, option.PropertyName, start, stop, Math.Max(points, 2));

        var primary = Build(Sweep, Start, Stop, Points);

        SweepTarget? step = IsStepping && Step is not null
            ? Build(Step, StepStart, StepStop, StepCount)
            : null;

        var result = new DcSweep(simulator).Run(new DcSweepRequest(primary, step));

        XLabel = $"{Sweep.Display}{(Sweep.Unit.Length > 0 ? $" ({Sweep.Unit})" : string.Empty)}";

        foreach (var curve in result.Curves)
        foreach (var trace in curve.Traces)
        {
            // The stepped value goes in the legend, because on a family of curves it is the only
            // thing telling one from another.
            var label = double.IsNaN(curve.StepValue)
                ? trace.Label
                : $"{trace.Label} @ {(Step!.IsTemperature ? "T" : Step.Component!.Name)} = " +
                  $"{SiPrefix.Format(curve.StepValue, Step.Unit)}";

            Curves.Add(new SweepCurve(label, trace.Kind, result.X, trace.Values));
        }

        Status = Summarise(result);
    }

    /// <summary>
    /// A line of arithmetic off the curves. For a single curve the useful facts are its range and
    /// where it peaked — the peak being the answer for a solar panel, a resonant load or anything
    /// else with a maximum in it.
    /// </summary>
    private string Summarise(DcSweepResult result)
    {
        if (result.IsEmpty)
            return "No point in the sweep could be solved. The range may be asking for something " +
                   "the circuit cannot do.";

        List<string> parts = [$"{result.X.Count} points"];

        if (result.Curves.Count > 1) parts.Add($"{result.Curves.Count} curves");

        foreach (var curve in Curves.Take(3))
        {
            var best = -1;
            for (var i = 0; i < curve.Y.Count; i++)
            {
                if (double.IsNaN(curve.Y[i])) continue;
                if (best < 0 || Math.Abs(curve.Y[i]) > Math.Abs(curve.Y[best])) best = i;
            }

            if (best < 0) continue;

            var unit = curve.Kind == ProbeKind.Current ? "A" : "V";
            parts.Add($"{curve.Label} peaks at {SiPrefix.Format(curve.Y[best], unit)} " +
                      $"where {(Sweep!.IsTemperature ? "T" : Sweep.Component!.Name)} = " +
                      $"{SiPrefix.Format(result.X[best], Sweep.Unit)}");
        }

        if (result.FailedPoints > 0)
            parts.Add($"{result.FailedPoints} points would not converge");

        return string.Join("   ·   ", parts);
    }

    private static string Describe(Exception ex) => ex switch
    {
        CircuitTopologyException => $"The circuit will not build: {ex.Message}",
        ArgumentException => ex.Message,
        _ => $"The sweep failed: {ex.Message}",
    };
}
