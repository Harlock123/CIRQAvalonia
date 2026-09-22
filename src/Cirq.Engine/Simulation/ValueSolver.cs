using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>What a value search was asked for.</summary>
/// <param name="Parameter">What to vary, and the range to look in.</param>
/// <param name="Probe">What to measure.</param>
/// <param name="Target">The reading to aim for.</param>
/// <param name="Tolerance">
/// How close counts as found, as a fraction of the target. A part is bought to three digits at
/// best, so chasing more than that is arithmetic rather than engineering.
/// </param>
public sealed record ValueSearch(
    SweepTarget Parameter, SignalProbe Probe, double Target, double Tolerance = 1e-4);

/// <summary>What a value search found.</summary>
/// <param name="Value">The value that hits the target.</param>
/// <param name="Achieved">What the probe actually reads there.</param>
/// <param name="Nearest">The nearest E-series preferred value, which is what you can buy.</param>
/// <param name="NearestAchieved">What the probe reads with that one fitted instead.</param>
/// <param name="Steps">How many solves it took.</param>
/// <param name="Problem">Why there is no answer, or null when there is.</param>
public sealed record ValueResult(
    double Value,
    double Achieved,
    double? Nearest,
    double? NearestAchieved,
    int Steps,
    string? Problem = null)
{
    public bool IsUsable => Problem is null;

    /// <summary>How far the buyable part lands from the target, as a fraction.</summary>
    public double NearestError => Nearest is null || NearestAchieved is null || Achieved == 0
        ? 0
        : Math.Abs(NearestAchieved.Value - Achieved) / Math.Abs(Achieved);

    public static ValueResult Unusable(string problem) => new(0, 0, null, null, 0, problem);
}

/// <summary>
/// Works backwards: what value of this part gives that reading?
/// <para>
/// Every other analysis here asks the same question in the same direction — given these parts,
/// what does the circuit do. This is the one people actually have in front of them: the output has
/// to be five volts, so <i>what resistor</i>. Until now that was a sweep, a squint at the curve,
/// a guess, and another sweep.
/// </para>
/// <para>
/// It is a bisection rather than anything cleverer, and deliberately. A circuit's response to a
/// component value is monotonic far more often than it is smooth — a diode comes on, a transistor
/// saturates, a comparator flips — and Newton's method on a curve with a corner in it runs away.
/// Bisection cannot: given a bracket, it always converges, and it takes about twenty solves to
/// reach a part count's worth of precision.
/// </para>
/// <para>
/// The answer is also given as the nearest <b>preferred value</b>, because the exact one is
/// usually not a thing you can buy. Being told "68 kΩ, and the E24 part gives you 4.97 V" is a
/// decision; being told "67.3 kΩ" is homework.
/// </para>
/// </summary>
public sealed class ValueSolver
{
    private readonly CircuitSimulator _simulator;

    public ValueSolver(CircuitSimulator simulator)
    {
        _simulator = simulator;
    }

    /// <summary>Searches the range, putting the parameter back where it was afterwards.</summary>
    public ValueResult Run(ValueSearch search, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);

        var property = search.Parameter.IsTemperature ? null : search.Parameter.Resolve();

        if (!search.Parameter.IsTemperature && property is null)
        {
            return ValueResult.Unusable(
                $"'{search.Parameter.Component?.Name}' has no writable number called " +
                $"'{search.Parameter.PropertyName}' to search.");
        }

        var low = Math.Min(search.Parameter.Start, search.Parameter.Stop);
        var high = Math.Max(search.Parameter.Start, search.Parameter.Stop);

        if (high - low <= 0) return ValueResult.Unusable("The range to search in has no width.");

        var was = Read(search.Parameter, property);
        var steps = 0;

        try
        {
            double? At(double value)
            {
                steps++;

                Write(search.Parameter, property, value);

                _simulator.System.ResetSolution();

                try
                {
                    _simulator.SolveOperatingPoint();
                    return _simulator.SampleProbe(search.Probe);
                }
                catch (Exception ex) when (ex is ConvergenceException or SingularMatrixException)
                {
                    return null;
                }
            }

            var atLow = At(low);
            var atHigh = At(high);

            if (atLow is null || atHigh is null)
                return ValueResult.Unusable("The circuit does not solve at one end of the range.");

            // The target has to be inside the bracket, and saying which way it is outside is far
            // more use than "not found" — it tells you which way to widen the range.
            var lowSide = atLow.Value - search.Target;
            var highSide = atHigh.Value - search.Target;

            if (lowSide * highSide > 0)
            {
                var nearer = Math.Abs(lowSide) < Math.Abs(highSide) ? atLow.Value : atHigh.Value;

                return ValueResult.Unusable(
                    $"Nothing in that range gives {search.Target:G6}: the reading runs from " +
                    $"{atLow.Value:G6} to {atHigh.Value:G6}, and the closest it gets is " +
                    $"{nearer:G6}. Widen the range.");
            }

            var rising = highSide > 0;
            var wanted = Math.Max(Math.Abs(search.Target) * search.Tolerance, 1e-12);

            double middle = low, reading = atLow.Value;

            // Fifty halvings takes any range to the limits of double precision; it stops long
            // before that on the tolerance.
            for (var i = 0; i < 50 && high - low > 0; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                middle = (low + high) / 2.0;

                var at = At(middle);

                if (at is null) return ValueResult.Unusable("The circuit stopped solving mid-search.");

                reading = at.Value;

                if (Math.Abs(reading - search.Target) <= wanted) break;

                if ((reading > search.Target) == rising) high = middle;
                else low = middle;
            }

            // And what you could actually buy. Temperature has no preferred values, so it is not
            // offered one.
            double? nearest = search.Parameter.IsTemperature ? null : PreferredValue.Nearest(middle);
            double? nearestReading = null;

            if (nearest is not null) nearestReading = At(nearest.Value);

            return new ValueResult(middle, reading, nearest, nearestReading, steps);
        }
        finally
        {
            Write(search.Parameter, property, was);

            _simulator.System.ResetSolution();

            try
            {
                _simulator.SolveOperatingPoint();
            }
            catch (Exception ex) when (ex is ConvergenceException or SingularMatrixException)
            {
                // Best effort, as everywhere else that puts a circuit back.
            }
        }
    }

    private double Read(SweepTarget target, System.Reflection.PropertyInfo? property) =>
        target.IsTemperature
            ? _simulator.Settings.TemperatureKelvin - 273.15
            : property is null ? 0 : Convert.ToDouble(property.GetValue(target.Component) ?? 0.0);

    private void Write(SweepTarget target, System.Reflection.PropertyInfo? property, double value)
    {
        if (target.IsTemperature)
        {
            _simulator.Settings.TemperatureKelvin = value + 273.15;
            return;
        }

        if (property is null) return;

        if (property.PropertyType == typeof(int))
        {
            property.SetValue(target.Component, (int)Math.Round(value));
            return;
        }

        property.SetValue(target.Component, value);
    }
}

/// <summary>
/// The E-series: the values components are actually made in.
/// <para>
/// Resistors and capacitors are not made in every value. They are made in a dozen or two per
/// decade, spaced so that each one's tolerance band meets the next — which is why E24 has
/// twenty-four values and 5 % parts, and E96 has ninety-six and 1 % ones. An answer that ignores
/// this is an answer you cannot buy.
/// </para>
/// </summary>
public static class PreferredValue
{
    /// <summary>E24: the 5 % series, and what most drawers actually contain.</summary>
    public static readonly double[] E24 =
    [
        1.0, 1.1, 1.2, 1.3, 1.5, 1.6, 1.8, 2.0, 2.2, 2.4, 2.7, 3.0,
        3.3, 3.6, 3.9, 4.3, 4.7, 5.1, 5.6, 6.2, 6.8, 7.5, 8.2, 9.1,
    ];

    /// <summary>
    /// The nearest value in a series, at any decade. Nearest <b>in the logarithm</b>, because the
    /// series is geometric: between 8.2 and 9.1 the midpoint is not 8.65.
    /// </summary>
    public static double Nearest(double value, double[]? series = null)
    {
        series ??= E24;

        if (value <= 0 || double.IsNaN(value) || double.IsInfinity(value)) return value;

        var decade = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var best = value;
        var distance = double.MaxValue;

        // The decade either side as well, so a value just under a decade boundary can round up to
        // the 1.0 above it rather than being stuck at the 9.1 below.
        foreach (var scale in new[] { decade / 10, decade, decade * 10 })
        {
            foreach (var step in series)
            {
                var candidate = step * scale;
                var gap = Math.Abs(Math.Log10(candidate / value));

                if (gap >= distance) continue;

                distance = gap;
                best = candidate;
            }
        }

        return best;
    }
}
