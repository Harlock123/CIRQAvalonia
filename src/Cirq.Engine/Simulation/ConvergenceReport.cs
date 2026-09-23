using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Engine.Simulation;

/// <summary>
/// What the solver was still arguing about when it gave up.
/// <para>
/// "Newton-Raphson failed to converge, try a smaller time step" is unactionable unless you
/// already know what is wrong, which is the one case where you do not need to be told. The solver
/// knows more than that and always has: it tracks how far every unknown moved on the last
/// iteration, so it knows <b>which node</b> was still swinging, and it asks every non-linear part
/// whether it has settled, so it knows <b>which part</b> said no. Both of those name somewhere on
/// the drawing to go and look.
/// </para>
/// </summary>
/// <param name="Time">When it gave up, in seconds.</param>
/// <param name="Iterations">How many iterations it took before doing so.</param>
/// <param name="Residual">The largest movement on the last iteration.</param>
/// <param name="WorstNode">The net that was still moving most, or null when it was a branch.</param>
/// <param name="WorstParts">What is attached to that net.</param>
/// <param name="Unsettled">The non-linear parts that said they had not settled.</param>
public sealed record ConvergenceReport(
    double Time,
    int Iterations,
    double Residual,
    string? WorstNode,
    IReadOnlyList<string> WorstParts,
    IReadOnlyList<string> Unsettled)
{
    /// <summary>The whole thing in words, which is what goes in the exception and the status bar.</summary>
    public string Describe()
    {
        List<string> lines =
        [
            $"The solver could not find an answer at t = {Time:g6} s: after {Iterations} " +
            $"iterations the circuit was still moving by {Residual:g3} a step.",
        ];

        if (WorstNode is not null)
        {
            var attached = WorstParts.Count == 0
                ? string.Empty
                : $", between {Join(WorstParts)}";

            lines.Add($"Most of that movement is on net {WorstNode}{attached}.");
        }

        if (Unsettled.Count > 0)
        {
            lines.Add(Unsettled.Count == 1
                ? $"{Unsettled[0]} says it has not settled."
                : $"{Join(Unsettled)} say they have not settled.");
        }

        // The three things that are actually wrong when this happens, in the order they are worth
        // checking. Generic advice, but generic advice attached to a named node is a place to
        // start rather than a shrug.
        lines.Add(
            "The usual causes, in order: a node with no DC path to ground; a source driving " +
            "straight into a junction with nothing to limit the current; or a genuinely fast " +
            "edge that wants a smaller time step.");

        return string.Join(" ", lines);
    }

    private static string Join(IReadOnlyList<string> names) =>
        names.Count switch
        {
            0 => string.Empty,
            1 => names[0],
            2 => $"{names[0]} and {names[1]}",
            _ => $"{string.Join(", ", names.Take(names.Count - 1))} and {names[^1]}",
        };

    /// <summary>
    /// Works out what to say from the state the solver is already keeping.
    /// </summary>
    /// <param name="deltas">How far each unknown moved on the last iteration.</param>
    internal static ConvergenceReport From(
        double time,
        int iterations,
        double residual,
        IReadOnlyList<double> deltas,
        MnaSystem system,
        SimulationState state,
        IReadOnlyList<CircuitComponent> nonlinear)
    {
        var worst = -1;
        var largest = 0.0;

        for (var i = 0; i < deltas.Count; i++)
        {
            var moved = Math.Abs(deltas[i]);
            if (moved <= largest) continue;

            largest = moved;
            worst = i;
        }

        string? node = null;
        List<string> parts = [];

        // Only a node index names somewhere on the drawing. A branch unknown is a current inside
        // a part rather than a place, and saying "row 14" would be worse than saying nothing.
        if (worst >= 0 && worst < system.NodeCount)
        {
            var net = system.Netlist.Nets.FirstOrDefault(n => n.Index == worst);

            if (net is not null)
            {
                node = net.Name;
                parts = [.. net.Terminals
                    .Select(t => t.Owner?.Name ?? string.Empty)
                    .Where(n => n.Length > 0)
                    .Distinct()
                    .Order()];
            }
        }

        List<string> unsettled = [];

        foreach (var part in nonlinear)
        {
            // Asking costs nothing and the answer is the part's own opinion of itself, which is
            // exactly what is wanted here.
            try
            {
                if (!part.HasConverged(system, state)) unsettled.Add(part.Name);
            }
            catch
            {
                // A part that throws while being asked is not the story; the failure to converge
                // is. Leave it out rather than replacing one confusing error with another.
            }
        }

        return new ConvergenceReport(time, iterations, residual, node, parts, unsettled);
    }
}
