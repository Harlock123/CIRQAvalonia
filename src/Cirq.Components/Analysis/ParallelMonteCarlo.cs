using Cirq.Components.Serialization;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Analysis;

/// <summary>
/// Runs a tolerance analysis across several threads.
/// <para>
/// A Monte Carlo run is the one embarrassingly parallel thing in this application: a few hundred to
/// a few thousand independent circuits, each solved from scratch, none of them looking at any of the
/// others. It was a serial loop, which on a machine with twelve cores meant eleven of them watching.
/// The difference is between an analysis somebody runs and one they mean to run.
/// </para>
/// <para>
/// What made it serial was not the arithmetic but the circuit: a trial works by writing values into
/// the parts and solving, so two threads sharing one drawing would be writing over each other. So
/// each worker gets <b>its own copy of the circuit</b>, made the way every other copy in this
/// application is made — through the serializer, because a copy that is not the saved form is a
/// second definition of what a circuit is.
/// </para>
/// <para>
/// The answers do not depend on how it was split. Each trial's parts come from that trial's own
/// number, so trial 700 is the same circuit whether it was solved seventh or seven-hundredth, on
/// one thread or on twelve — and a result that changed with the number of cores would not be a
/// result.
/// </para>
/// </summary>
public static class ParallelMonteCarlo
{
    /// <summary>
    /// Below this many trials it is run on one thread. Copying the circuit and starting threads
    /// costs more than a handful of operating-point solves.
    /// </summary>
    public const int SerialBelow = 48;

    /// <summary>Runs the trials, on as many threads as there is work for.</summary>
    /// <param name="circuit">The circuit. It is not modified: the workers use copies.</param>
    /// <param name="request">Trials and seed, as the serial analysis takes them.</param>
    /// <param name="workers">
    /// How many threads, or null for one per processor. Whatever it is, the answer is the same.
    /// </param>
    public static MonteCarloResult Run(
        Circuit circuit,
        MonteCarloRequest request,
        int? workers = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(request);

        var trials = Math.Max(request.Trials, 1);
        var threads = Math.Clamp(workers ?? Environment.ProcessorCount, 1, 64);

        if (trials < SerialBelow || threads == 1)
            return new MonteCarlo(circuit).Run(request, cancellationToken);

        // Chunks of at least a few trials each: a thread that copies a circuit to solve two of them
        // has spent more time copying than solving.
        var chunks = Math.Min(threads, Math.Max(1, trials / 16));

        if (chunks <= 1) return new MonteCarlo(circuit).Run(request, cancellationToken);

        var saved = CircuitSerializer.ToJson(circuit);
        var results = new MonteCarloResult?[chunks];

        var size = trials / chunks;
        var remainder = trials % chunks;

        Parallel.For(0, chunks, new ParallelOptions
        {
            MaxDegreeOfParallelism = threads,
            CancellationToken = cancellationToken,
        },
        chunk =>
        {
            // The first few chunks take one extra trial each, so nothing is left over and the
            // total is exactly what was asked for.
            var count = size + (chunk < remainder ? 1 : 0);
            var start = (chunk * size) + Math.Min(chunk, remainder);

            if (count == 0) return;

            var copy = CircuitSerializer.FromJson(saved).Circuit;

            results[chunk] = new MonteCarlo(copy).Run(
                request with { Trials = count, FirstTrial = request.FirstTrial + start },
                cancellationToken);
        });

        return Merge([.. results.Where(r => r is not null).Select(r => r!)]);
    }

    /// <summary>
    /// Puts the chunks back together in order, so the readings are in trial order as they would
    /// have been from one thread.
    /// </summary>
    private static MonteCarloResult Merge(IReadOnlyList<MonteCarloResult> parts)
    {
        if (parts.Count == 0) return new MonteCarloResult(0, 0, [], []);
        if (parts.Count == 1) return parts[0];

        var first = parts.First(p => !p.IsEmpty || parts.All(q => q.IsEmpty));

        List<MonteCarloTrace> traces = [];

        foreach (var trace in first.Traces)
        {
            List<double> values = [];

            foreach (var part in parts)
            {
                var same = part.Traces.FirstOrDefault(t => t.Label == trace.Label);

                if (same is not null) values.AddRange(same.Values);
            }

            // The nominal is the same in every chunk — every worker solved the same circuit with
            // its parts at their marked values before varying anything.
            traces.Add(trace with { Values = values });
        }

        return new MonteCarloResult(
            parts.Sum(p => p.Trials),
            parts.Sum(p => p.Failed),
            first.Varied,
            traces);
    }
}
