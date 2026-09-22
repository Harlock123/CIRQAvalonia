using System.Numerics;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>What a noise analysis was asked for.</summary>
/// <param name="Sweep">The band to look across, spaced as a Bode plot is.</param>
/// <param name="Output">The probe whose noise is being measured.</param>
public sealed record NoiseRequest(AcSweepRequest Sweep, SignalProbe Output);

/// <summary>One generator's share of the output noise, integrated across the band.</summary>
/// <param name="Name">The generator, e.g. "R3 thermal".</param>
/// <param name="Rms">What it alone would put on the output, in volts RMS.</param>
/// <param name="Share">Its fraction of the total noise power, from 0 to 1.</param>
public sealed record NoiseContributor(string Name, double Rms, double Share)
{
    public double Percent => Share * 100.0;

    public override string ToString() => $"{Name}: {Rms:0.###e+00} V rms ({Percent:0.#} %)";
}

/// <summary>What a noise analysis found.</summary>
/// <param name="Frequencies">Where it looked, in hertz.</param>
/// <param name="Density">
/// Output noise spectral density at each frequency, in volts per root hertz — which is the unit
/// every op-amp datasheet quotes and the only one that can be compared between circuits.
/// </param>
/// <param name="Rms">Total output noise across the whole band, in volts RMS.</param>
/// <param name="Contributors">Where it came from, largest first.</param>
/// <param name="Problem">Why the answer should not be believed, or null when it can be.</param>
public sealed record NoiseResult(
    IReadOnlyList<double> Frequencies,
    IReadOnlyList<double> Density,
    double Rms,
    IReadOnlyList<NoiseContributor> Contributors,
    string? Problem = null)
{
    public bool IsUsable => Problem is null && Frequencies.Count > 0;

    /// <summary>The worst density in the band, and where it is.</summary>
    public (double Frequency, double Density)? Worst()
    {
        if (Density.Count == 0) return null;

        var best = 0;
        for (var i = 1; i < Density.Count; i++)
            if (Density[i] > Density[best]) best = i;

        return (Frequencies[best], Density[best]);
    }

    public static NoiseResult Unusable(string problem) => new([], [], 0, [], problem);
}

/// <summary>
/// How much noise a circuit makes, where it comes from, and which part to change.
/// <para>
/// Noise is the floor under everything: it is what decides the smallest signal a circuit can be
/// asked to handle, and it is the one property that cannot be improved by being careful. It is
/// also invisible in every other analysis here — a transient shows a clean line and an AC sweep
/// shows a clean curve, because neither of them has any noise in it.
/// </para>
/// <para>
/// The ranking is the useful half. "This circuit makes 12 µV" is a number; "and 80 % of it is R3"
/// is an instruction. Almost always the answer is one resistor, and almost always it is not the
/// one people guess.
/// </para>
/// <para>
/// <b>How it is done.</b> Every generator's contribution needs the transfer function from where it
/// sits to the output, which sounds like one solve per generator per frequency. It is not: solving
/// the <i>transposed</i> system once, against a unit vector at the output, gives a vector whose
/// entries are exactly those transfer functions — all of them, for every node at once. So a
/// circuit with forty noise generators costs two solves per frequency rather than forty-one.
/// </para>
/// </summary>
public sealed class NoiseAnalysis
{
    private readonly CircuitSimulator _simulator;

    public NoiseAnalysis(CircuitSimulator simulator)
    {
        _simulator = simulator;
    }

    /// <summary>
    /// Runs the analysis about the circuit's bias point, leaving the simulator where it found it.
    /// </summary>
    public NoiseResult Run(NoiseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var output = request.Output.NodeIndex;

        if (output < 0)
            return NoiseResult.Unusable(
                $"'{request.Output.Label}' is on ground, which has no noise on it by definition. " +
                "Probe the node you actually care about.");

        var emitters = _simulator.Circuit.Components.OfType<INoiseSource>().ToList();

        if (emitters.Count == 0)
            return NoiseResult.Unusable(
                "Nothing in this circuit makes noise. Resistors, diodes, transistors and MOSFETs " +
                "do; ideal sources and switches do not.");

        var frequencies = request.Sweep.Frequencies();
        var state = _simulator.State;
        var system = _simulator.System;

        var ac = new AcSystem(system);
        var solver = new ComplexLuSolver(ac.Size);

        var density = new double[frequencies.Count];

        // Mean-square per generator at each frequency, so the ranking can be integrated the same
        // way the total is rather than taken at one arbitrary point in the band.
        Dictionary<string, double[]> perSource = [];

        var previousMode = state.Mode;
        var previousOmega = state.AngularFrequency;

        var transposed = new Complex[ac.Size][];
        for (var i = 0; i < ac.Size; i++) transposed[i] = new Complex[ac.Size];

        var unit = new Complex[ac.Size];
        var transfer = new Complex[ac.Size];

        try
        {
            state.Mode = AnalysisMode.SmallSignal;

            for (var f = 0; f < frequencies.Count; f++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Build(ac, state, system, frequencies[f]);

                // The adjoint: solve Aᵀ y = e_out, and y holds the transfer from a current
                // injected at every node to the voltage at the output. One solve, every generator.
                for (var i = 0; i < ac.Size; i++)
                for (var j = 0; j < ac.Size; j++)
                    transposed[i][j] = ac.Matrix[j][i];

                Array.Clear(unit);
                unit[output] = Complex.One;

                try
                {
                    solver.Factor(transposed);
                    solver.Solve(unit, transfer);
                }
                catch (SingularMatrixException)
                {
                    // A frequency the circuit cannot be solved at contributes nothing rather than
                    // throwing the whole band away.
                    continue;
                }

                foreach (var emitter in emitters)
                {
                    foreach (var emission in emitter.NoiseSources(system, state, frequencies[f]))
                    {
                        if (emission.SpectralDensity <= 0) continue;

                        var gain = At(transfer, emission.NodeP) - At(transfer, emission.NodeN);
                        var contribution = gain.Magnitude * gain.Magnitude * emission.SpectralDensity;

                        if (double.IsNaN(contribution) || double.IsInfinity(contribution)) continue;

                        density[f] += contribution;

                        if (!perSource.TryGetValue(emission.Name, out var bins))
                            perSource[emission.Name] = bins = new double[frequencies.Count];

                        bins[f] += contribution;
                    }
                }
            }
        }
        finally
        {
            state.Mode = previousMode;
            state.AngularFrequency = previousOmega;
        }

        var total = Integrate(frequencies, density);

        List<NoiseContributor> contributors = [];

        foreach (var (name, bins) in perSource)
        {
            var mean = Integrate(frequencies, bins);
            if (mean <= 0) continue;

            contributors.Add(new NoiseContributor(
                name, Math.Sqrt(mean), total > 0 ? mean / total : 0));
        }

        contributors.Sort((a, b) => b.Share.CompareTo(a.Share));

        // Reported as volts per root hertz, not as the mean-square it was accumulated in: that is
        // the unit every datasheet quotes and the only one comparable between circuits.
        return new NoiseResult(
            frequencies, [.. density.Select(Math.Sqrt)], Math.Sqrt(total), contributors);
    }

    /// <summary>Ground has no entry in the solution vector and contributes no transfer.</summary>
    private static Complex At(Complex[] vector, int node) =>
        node < 0 || node >= vector.Length ? Complex.Zero : vector[node];

    /// <summary>
    /// Builds the complex matrix at one frequency, exactly as the AC sweep does. No right-hand
    /// side: a noise analysis never drives the circuit, it only asks what the circuit would do to
    /// something injected.
    /// </summary>
    private void Build(AcSystem ac, SimulationState state, MnaSystem system, double hertz)
    {
        var omega = 2.0 * Math.PI * hertz;

        state.AngularFrequency = omega;
        ac.AngularFrequency = omega;

        system.Clear();
        foreach (var component in _simulator.Circuit.Components) component.StampMatrix(system, state);

        ac.Clear();
        ac.FoldInConductances();

        foreach (var component in _simulator.Circuit.Components) component.StampAc(ac, state);
    }

    /// <summary>
    /// Total mean-square across the band: the area under the density curve, by trapezoids.
    /// <para>
    /// In <i>linear</i> frequency, whatever spacing the points are on. Noise power is per hertz,
    /// so a decade from 10 kHz to 100 kHz carries ninety thousand hertz of it and a decade from
    /// 1 Hz to 10 Hz carries nine — which is why a wide-band circuit's noise is almost entirely
    /// decided by its top octave, and why integrating in the logarithm would be wrong by orders
    /// of magnitude.
    /// </para>
    /// </summary>
    private static double Integrate(IReadOnlyList<double> frequencies, IReadOnlyList<double> density)
    {
        var total = 0.0;

        for (var i = 1; i < frequencies.Count; i++)
        {
            var width = frequencies[i] - frequencies[i - 1];
            if (width <= 0) continue;

            total += 0.5 * (density[i] + density[i - 1]) * width;
        }

        return total;
    }
}
