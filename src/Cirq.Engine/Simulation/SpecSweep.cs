using Cirq.Core.Primitives;
using Cirq.Core.Units;
using Cirq.Core.Verification;

namespace Cirq.Engine.Simulation;

/// <summary>What a requirement sweep was asked for.</summary>
/// <param name="Over">What to vary — the temperature, or any component value. The sweep a DC sweep uses.</param>
/// <param name="Duration">How long to run the circuit at each value, in seconds.</param>
/// <param name="SampleInterval">How often to record, or null to work one out from the duration.</param>
public sealed record SpecSweepRequest(SweepTarget Over, double Duration, double? SampleInterval = null);

/// <summary>One requirement, held against the circuit at one point of the sweep.</summary>
/// <param name="Value">What the swept quantity was — degrees, ohms, farads.</param>
/// <param name="Result">What the requirement made of the circuit there.</param>
public sealed record SpecPoint(double Value, SpecResult Result);

/// <summary>
/// One requirement across the whole sweep.
/// </summary>
/// <param name="Spec">The requirement.</param>
/// <param name="Points">Every value it was checked at, in order.</param>
public sealed record SpecMargin(DesignSpec Spec, IReadOnlyList<SpecPoint> Points)
{
    /// <summary>The points where there was a verdict at all.</summary>
    public IReadOnlyList<SpecPoint> Judged => [.. Points.Where(p => p.Result.Passed is not null)];

    /// <summary>True when it was met everywhere it could be judged, and judged somewhere.</summary>
    public bool Holds => Judged.Count > 0 && Judged.All(p => p.Result.Passed == true);

    /// <summary>True when it failed somewhere, which is the answer somebody swept to find.</summary>
    public bool Fails => Judged.Any(p => p.Result.Passed == false);

    /// <summary>
    /// The point with the least room left — the worst case, whether or not it is a failure. Null
    /// when nothing could be judged.
    /// </summary>
    public SpecPoint? Worst
    {
        get
        {
            SpecPoint? worst = null;
            var least = double.PositiveInfinity;

            foreach (var point in Judged)
            {
                // A margin that cannot be worked out — a limit of zero — still has a verdict, and a
                // failure with no margin is worse than a pass with one.
                var room = point.Result.Margin ?? (point.Result.Passed == true ? 0.0 : double.NegativeInfinity);

                if (room >= least) continue;

                least = room;
                worst = point;
            }

            return worst;
        }
    }

    /// <summary>The range over which it was met, or null when it was never met.</summary>
    public (double From, double To)? Window
    {
        get
        {
            var met = Judged.Where(p => p.Result.Passed == true).Select(p => p.Value).ToList();

            return met.Count == 0 ? null : (met.Min(), met.Max());
        }
    }
}

/// <summary>What a requirement sweep found.</summary>
/// <param name="Label">What was swept, for an axis.</param>
/// <param name="Unit">The unit it was swept in.</param>
/// <param name="Values">Every value, in order.</param>
/// <param name="Margins">One per enabled requirement.</param>
/// <param name="Problems">Values the circuit could not be solved at, and why.</param>
public sealed record SpecSweepResult(
    string Label,
    string Unit,
    IReadOnlyList<double> Values,
    IReadOnlyList<SpecMargin> Margins,
    IReadOnlyList<string> Problems)
{
    public static SpecSweepResult Empty => new(string.Empty, string.Empty, [], [], []);

    public bool IsEmpty => Margins.Count == 0 || Values.Count == 0;

    /// <summary>True when every requirement was met at every value.</summary>
    public bool Holds => Margins.Count > 0 && Margins.All(m => m.Holds);

    /// <summary>The answer in a sentence, which is what somebody swept to get.</summary>
    public string Summary()
    {
        if (IsEmpty) return "Nothing to check: add a requirement, and probe what it is about.";

        var failing = Margins.Where(m => m.Fails).ToList();
        var range = $"{Format(Values.Min())} to {Format(Values.Max())}";

        if (failing.Count == 0)
        {
            var tightest = Margins
                .Where(m => m.Worst?.Result.Margin is not null)
                .OrderBy(m => m.Worst!.Result.Margin)
                .FirstOrDefault();

            var closest = tightest?.Worst is { } worst
                ? $" The closest is {tightest!.Spec.Trace} at {Format(worst.Value)}, with " +
                  $"{worst.Result.Margin * 100:0.#} % of its limit to spare."
                : string.Empty;

            return $"Every requirement is met from {range}.{closest}";
        }

        var first = failing[0];
        var where = first.Window is { } window
            ? $"it holds from {Format(window.From)} to {Format(window.To)}"
            : "it is not met anywhere in the range";

        return failing.Count == 1
            ? $"{first.Spec.Describe()} — {where}."
            : $"{failing.Count} requirements are not met across {range}. " +
              $"{first.Spec.Describe()} — {where}.";
    }

    // Degrees are not an SI-prefixed quantity: "-4.29°C" reads as a unit somebody has mangled,
    // where "-4.3 °C" is a temperature.
    private string Format(double value) =>
        Unit == "°C" ? $"{value:0.#} °C" : SiPrefix.Format(value, Unit, 3);
}

/// <summary>
/// Holds the circuit's requirements against it across a range, rather than at one point.
/// <para>
/// The requirements answer "does it work". This answers the question a design is actually signed
/// off against, which is "does it work <i>over the range</i>" — from −40 °C to +85 °C, or with the
/// feedback resistor anywhere in its tolerance band. A circuit checked at 27 °C and shipped is a
/// circuit nobody has checked at the temperature it will be used at.
/// </para>
/// <para>
/// It is built on <see cref="TransientStep"/>, which already knows how to run the same transient
/// several times with one thing changed and put everything back afterwards. The only thing added
/// here is holding the requirements against each pass instead of drawing it — which is the whole
/// point: the verdicts come from exactly the code that produces them in the requirements window, so
/// the two can never disagree about whether a circuit passes.
/// </para>
/// </summary>
public sealed class SpecSweep
{
    private readonly CircuitSimulator _simulator;

    public SpecSweep(CircuitSimulator simulator)
    {
        _simulator = simulator;
    }

    public SpecSweepResult Run(SpecSweepRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var specs = _simulator.Circuit.Specs.Where(s => s.IsEnabled).ToList();

        if (specs.Count == 0 || _simulator.Circuit.Probes.Count == 0) return SpecSweepResult.Empty;

        var family = new TransientStep(_simulator).Run(
            new TransientStepRequest(request.Over, request.Duration, request.SampleInterval),
            cancellationToken);

        List<double> values = [];
        List<string> problems = [];

        // One list of points per requirement, filled as the passes come back.
        var points = specs.ToDictionary(s => s, _ => new List<SpecPoint>());

        foreach (var run in family.Runs)
        {
            values.Add(run.Value);

            if (!run.Succeeded)
            {
                problems.Add($"{Format(request, run.Value)}: {run.Problem}");
                continue;
            }

            var traces = run.Traces.ToDictionary(t => t.Label, t => (IReadOnlyList<DataPoint>?)t.Samples);

            foreach (var spec in specs)
            {
                var result = SpecCheck.EvaluateFrom(
                    spec, label => traces.TryGetValue(label, out var samples) ? samples : null);

                points[spec].Add(new SpecPoint(run.Value, result));
            }
        }

        return new SpecSweepResult(
            request.Over.Label,
            request.Over.IsTemperature ? "°C" : string.Empty,
            values,
            [.. specs.Select(s => new SpecMargin(s, points[s]))],
            problems);
    }

    private static string Format(SpecSweepRequest request, double value) =>
        request.Over.IsTemperature ? $"{value:0.#} °C" : SiPrefix.Format(value, string.Empty, 3);
}
