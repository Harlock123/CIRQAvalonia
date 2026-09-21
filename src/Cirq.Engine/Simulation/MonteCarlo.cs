using System.Reflection;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>What a Monte Carlo run was asked for.</summary>
/// <param name="Trials">How many circuits to build and solve.</param>
/// <param name="Seed">Where the sequence starts, so a run can be repeated exactly.</param>
public sealed record MonteCarloRequest(int Trials = 200, int Seed = 1);

/// <summary>
/// What one probe read across every trial, and what that spread amounts to.
/// </summary>
/// <param name="Label">The probe's name.</param>
/// <param name="Kind">Voltage, current, power — whatever the probe measures.</param>
/// <param name="Values">One reading per trial, in the order they were run.</param>
/// <param name="Nominal">What it read with every part at its marked value.</param>
public sealed record MonteCarloTrace(
    string Label, ProbeKind Kind, IReadOnlyList<double> Values, double Nominal)
{
    public double Minimum => Values.Count == 0 ? 0 : Values.Min();

    public double Maximum => Values.Count == 0 ? 0 : Values.Max();

    public double Mean => Values.Count == 0 ? 0 : Values.Average();

    /// <summary>Population standard deviation of the readings.</summary>
    public double StandardDeviation
    {
        get
        {
            if (Values.Count < 2) return 0;

            var mean = Mean;
            return Math.Sqrt(Values.Sum(v => (v - mean) * (v - mean)) / Values.Count);
        }
    }

    /// <summary>
    /// The widest departure from nominal, as a fraction of nominal. This is the number the whole
    /// analysis exists to produce: "the output is within this much of where it was designed to be,
    /// whatever parts come out of the bag".
    /// </summary>
    public double WorstFractionalError => Math.Abs(Nominal) < 1e-15
        ? 0
        : Math.Max(Math.Abs(Maximum - Nominal), Math.Abs(Minimum - Nominal)) / Math.Abs(Nominal);

    /// <summary>
    /// How many trials landed outside a tolerance band around nominal — the yield question, put
    /// the other way up.
    /// </summary>
    public int OutsideBand(double fraction)
    {
        if (Math.Abs(Nominal) < 1e-15) return 0;

        var allowed = Math.Abs(Nominal) * fraction;

        return Values.Count(v => Math.Abs(v - Nominal) > allowed);
    }

    /// <summary>The readings gathered into equal buckets, for a histogram.</summary>
    public (double Centre, int Count)[] Histogram(int buckets = 21)
    {
        if (Values.Count == 0 || buckets < 1) return [];

        var low = Minimum;
        var high = Maximum;

        // A spread of nothing is one bar, not a division by zero.
        if (high - low < 1e-15) return [(low, Values.Count)];

        var width = (high - low) / buckets;
        var counts = new int[buckets];

        foreach (var value in Values)
            counts[Math.Clamp((int)((value - low) / width), 0, buckets - 1)]++;

        return [.. counts.Select((c, i) => (low + ((i + 0.5) * width), c))];
    }
}

/// <summary>The whole answer.</summary>
/// <param name="Trials">How many were actually solved.</param>
/// <param name="Failed">How many would not converge and were discarded.</param>
/// <param name="Varied">Which parts were varied, for the report.</param>
/// <param name="Traces">One per probe.</param>
public sealed record MonteCarloResult(
    int Trials, int Failed, IReadOnlyList<string> Varied, IReadOnlyList<MonteCarloTrace> Traces)
{
    public bool IsEmpty => Trials == 0 || Traces.Count == 0;
}

/// <summary>
/// Builds the same circuit many times with its parts drawn from their tolerance bands, and reports
/// what the answer did.
/// <para>
/// This asks a question nothing else here can: <b>will it work with the parts I can actually
/// buy?</b> Every other analysis in this library uses the value written on the schematic, and no
/// resistor has ever had the value written on it. A divider of two 5 % resistors is not a divider
/// by two; a 555 built round a 20 % ceramic is not a precision timer. Whether that matters depends
/// on the circuit, and this is how you find out which kind you have.
/// </para>
/// <para>
/// Values are drawn <b>uniformly</b> across the band rather than from a bell curve, and that is a
/// deliberate choice worth knowing about. People assume a normal distribution because manufacturing
/// usually produces one — but resistors are made to a loose tolerance and then <i>sorted</i>, and
/// the ones near the middle are pulled out and sold as one percent parts. What is left in the five
/// percent bag is the skirts, which is if anything worse than uniform. Uniform is the honest
/// middle: it does not flatter the circuit the way a bell curve would.
/// </para>
/// <para>
/// The sequence comes from a seed, so a run repeats exactly. An analysis whose answer changes every
/// time you look at it is not much use for deciding anything.
/// </para>
/// </summary>
public sealed class MonteCarlo
{
    private readonly Circuit _circuit;

    public MonteCarlo(Circuit circuit)
    {
        _circuit = circuit;
    }

    /// <summary>
    /// Runs the trials and puts every varied part back at its marked value afterwards.
    /// </summary>
    public MonteCarloResult Run(MonteCarloRequest request, CancellationToken cancellationToken = default)
    {
        var targets = Targets(_circuit).ToList();

        if (targets.Count == 0)
            return new MonteCarloResult(0, 0, [], []);

        var simulator = new CircuitSimulator(_circuit);

        simulator.Reset();
        simulator.SolveOperatingPoint();
        simulator.ResolveProbes();

        var probes = _circuit.Probes.ToList();

        if (probes.Count == 0) return new MonteCarloResult(0, 0, [], []);

        // Nominal first, with everything at its marked value — the answer everything else is
        // measured against.
        var nominal = probes.Select(simulator.SampleProbe).ToArray();

        List<double>[] readings = [.. probes.Select(_ => new List<double>())];

        var random = new Xorshift(request.Seed);
        var failed = 0;
        var trials = Math.Max(request.Trials, 1);

        try
        {
            for (var trial = 0; trial < trials; trial++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var target in targets)
                {
                    // Uniform across the band, either side of the marked value.
                    var spread = 1.0 + (((random.Next() * 2.0) - 1.0) * target.Tolerance);
                    target.Property.SetValue(target.Component, target.Nominal * spread);
                }

                // Each trial is a different circuit, so it starts from scratch rather than from
                // the previous one's solution.
                simulator.System.ResetSolution();

                try
                {
                    simulator.SolveOperatingPoint();
                }
                catch (Exception ex) when (ex is ConvergenceException or SingularMatrixException)
                {
                    failed++;
                    continue;
                }

                for (var i = 0; i < probes.Count; i++)
                    readings[i].Add(simulator.SampleProbe(probes[i]));
            }
        }
        finally
        {
            foreach (var target in targets) target.Property.SetValue(target.Component, target.Nominal);
        }

        List<MonteCarloTrace> traces = [];

        for (var i = 0; i < probes.Count; i++)
            traces.Add(new MonteCarloTrace(probes[i].Label, probes[i].Kind, readings[i], nominal[i]));

        var varied = targets
            .Select(t => $"{t.Component.Name} ±{t.Tolerance * 100:0.#} %")
            .ToList();

        return new MonteCarloResult(trials - failed, failed, varied, traces);
    }

    /// <summary>Everything in the circuit with a tolerance worth varying.</summary>
    public static IEnumerable<ToleranceTarget> Targets(Circuit circuit)
    {
        foreach (var component in circuit.Components)
        {
            if (component is not IToleranced toleranced) continue;
            if (toleranced.Tolerance <= 0) continue;

            var property = component.GetType().GetProperty(
                toleranced.TolerancedProperty, BindingFlags.Public | BindingFlags.Instance);

            if (property is null || !property.CanWrite) continue;
            if (property.PropertyType != typeof(double)) continue;

            yield return new ToleranceTarget(
                component, property, Convert.ToDouble(property.GetValue(component) ?? 0.0),
                toleranced.Tolerance);
        }
    }

    /// <summary>One part that will be varied, and what it is nominally.</summary>
    public sealed record ToleranceTarget(
        CircuitComponent Component, PropertyInfo Property, double Nominal, double Tolerance);

    /// <summary>
    /// A small deterministic generator rather than <c>System.Random</c>, so a seed gives the same
    /// sequence on every platform and every run — the same reasoning the noise source uses.
    /// </summary>
    private sealed class Xorshift(int seed)
    {
        private uint _state = seed == 0 ? 1u : (uint)seed;

        /// <summary>A value in [0,1).</summary>
        public double Next()
        {
            _state ^= _state << 13;
            _state ^= _state >> 17;
            _state ^= _state << 5;

            return (_state >> 8) / 16777216.0;
        }
    }
}
