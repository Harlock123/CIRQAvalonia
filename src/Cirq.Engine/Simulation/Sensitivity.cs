using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>What one part contributes to one measurement's spread.</summary>
/// <param name="Part">The component's designator.</param>
/// <param name="Property">Which of its values was varied.</param>
/// <param name="Tolerance">The band it was varied over, as a fraction.</param>
/// <param name="Low">The measurement with that part at the bottom of its band.</param>
/// <param name="High">And at the top.</param>
/// <param name="Share">
/// This part's share of the total variance, from nought to one. Contributions add in quadrature
/// because the parts are independent, so a part responsible for half the spread is responsible for
/// a quarter of the variance — which is why the shares are worked out that way rather than by
/// simply dividing the ranges.
/// </param>
public sealed record SensitivityEntry(
    string Part, string Property, double Tolerance, double Low, double High, double Share)
{
    /// <summary>How far the measurement moves across this part's band, either side of nominal.</summary>
    public double HalfRange => Math.Abs(High - Low) / 2.0;

    /// <summary>
    /// The measurement's fractional change per fractional change in the part — the elasticity, and
    /// the figure that says whether a tighter part would actually help.
    /// </summary>
    public double Elasticity(double nominal) =>
        Tolerance <= 0 || Math.Abs(nominal) < 1e-15
            ? 0
            : HalfRange / Math.Abs(nominal) / Tolerance;
}

/// <summary>One probe's ranking.</summary>
/// <param name="Label">The probe's name.</param>
/// <param name="Nominal">What it reads with every part at its marked value.</param>
/// <param name="Entries">The parts, worst first.</param>
public sealed record SensitivityResult(
    string Label, double Nominal, IReadOnlyList<SensitivityEntry> Entries);

/// <summary>
/// Which part is responsible for the spread.
/// <para>
/// A tolerance analysis says how far the answer moves. It cannot say <i>why</i>, and the two
/// questions have different uses: the first tells you whether the design works, the second tells
/// you where to spend money. Buying one percent resistors for a whole board is expensive and
/// mostly pointless; buying one for the part that causes eighty percent of the spread is neither.
/// </para>
/// <para>
/// Worked out by taking each part to each end of its own band with everything else at nominal,
/// which is exact rather than sampled. Monte Carlo has to be random because it is asking what
/// happens when everything moves at once; this is asking a question about one part at a time, and
/// two solves answer it exactly.
/// </para>
/// </summary>
public sealed class Sensitivity
{
    private readonly Circuit _circuit;

    public Sensitivity(Circuit circuit)
    {
        _circuit = circuit;
    }

    /// <summary>
    /// Ranks every toleranced part by what it contributes, for every probe. Puts each part back
    /// at its marked value afterwards and leaves the circuit at its own bias point.
    /// </summary>
    public IReadOnlyList<SensitivityResult> Run(CancellationToken cancellationToken = default)
    {
        var targets = MonteCarlo.Targets(_circuit).ToList();
        var probes = _circuit.Probes.ToList();

        if (targets.Count == 0 || probes.Count == 0) return [];

        var simulator = new CircuitSimulator(_circuit);

        simulator.Reset();
        simulator.SolveOperatingPoint();
        simulator.ResolveProbes();

        var nominal = probes.Select(simulator.SampleProbe).ToArray();

        // [target][probe] at each end of that target's band.
        var low = new double[targets.Count][];
        var high = new double[targets.Count][];

        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var target = targets[i];

                low[i] = Measure(simulator, probes, target, 1.0 - target.Tolerance, nominal);
                high[i] = Measure(simulator, probes, target, 1.0 + target.Tolerance, nominal);

                target.Property.SetValue(target.Component, target.Nominal);
            }
        }
        finally
        {
            foreach (var target in targets)
                target.Property.SetValue(target.Component, target.Nominal);

            simulator.System.ResetSolution();
            TrySolve(simulator);
        }

        List<SensitivityResult> results = [];

        for (var p = 0; p < probes.Count; p++)
        {
            // In quadrature: independent contributions add as squares, so the share of the
            // variance is the square of the range over the sum of squares.
            var squares = new double[targets.Count];
            var total = 0.0;

            for (var i = 0; i < targets.Count; i++)
            {
                var half = Math.Abs(high[i][p] - low[i][p]) / 2.0;

                squares[i] = half * half;
                total += squares[i];
            }

            List<SensitivityEntry> entries = [];

            for (var i = 0; i < targets.Count; i++)
            {
                entries.Add(new SensitivityEntry(
                    targets[i].Component.Name,
                    targets[i].Property.Name,
                    targets[i].Tolerance,
                    low[i][p],
                    high[i][p],
                    total > 0 ? squares[i] / total : 0));
            }

            results.Add(new SensitivityResult(
                probes[p].Label, nominal[p],
                [.. entries.OrderByDescending(e => e.Share).ThenBy(e => e.Part, StringComparer.Ordinal)]));
        }

        return results;
    }

    /// <summary>
    /// One end of one part's band. A point that will not converge is recorded as the nominal
    /// reading, which contributes nothing — better than a gap that would rank the part first.
    /// </summary>
    private static double[] Measure(
        CircuitSimulator simulator,
        IReadOnlyList<SignalProbe> probes,
        MonteCarlo.ToleranceTarget target,
        double scale,
        double[] nominal)
    {
        target.Property.SetValue(target.Component, target.Nominal * scale);

        simulator.System.ResetSolution();

        return TrySolve(simulator)
            ? [.. probes.Select(simulator.SampleProbe)]
            : [.. nominal];
    }

    private static bool TrySolve(CircuitSimulator simulator)
    {
        try
        {
            simulator.SolveOperatingPoint();
            return true;
        }
        catch (Exception ex) when (ex is ConvergenceException or SingularMatrixException)
        {
            simulator.System.ResetSolution();
            return false;
        }
    }
}
