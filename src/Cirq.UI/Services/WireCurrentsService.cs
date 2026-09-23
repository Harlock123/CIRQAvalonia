using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.UI.Services;

/// <summary>
/// How much current each wire is carrying, and which way.
/// <para>
/// A wire is not a component, so the solver has no unknown for it. Two things stand in for one.
/// </para>
/// <para>
/// <b>What a pin says.</b> A component that can report the current through one of its own pins is
/// asked, and if that pin has exactly one wire on it then that wire is carrying all of it —
/// there is nowhere else for it to go.
/// </para>
/// <para>
/// <b>What must follow.</b> Current into a two-terminal part is current out of it, exactly, so
/// knowing one of its wires gives the other. Repeating that walks a series loop all the way round
/// — including the return through ground, where nothing can report anything and the wire would
/// otherwise stay dark. Which rather spoils the point, since "it goes round and comes back" is
/// the thing the drawing exists to show.
/// </para>
/// <para>
/// Where a pin has several wires on it, the answer is genuinely unknown and the wire is left out.
/// A junction divides current in a ratio that depends on the whole rest of the circuit, and
/// guessing it — splitting it evenly, say — would draw something confidently wrong on the one
/// part of a schematic somebody would trust it for.
/// </para>
/// </summary>
public static class WireCurrentsService
{
    /// <summary>
    /// The current in each wire it can be worked out for, positive from the wire's source end
    /// towards its target end.
    /// </summary>
    public static IReadOnlyDictionary<WireSegment, double> For(
        Circuit circuit, CircuitSimulator simulator)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(simulator);

        // How many wires land on each terminal, which is what decides whether one of them carries
        // the whole of that pin's current.
        Dictionary<Terminal, int> fanout = [];

        // And which wires those are, for walking through a part from one to the other.
        Dictionary<Terminal, WireSegment> only = [];

        foreach (var wire in circuit.Wires)
        {
            Count(wire.SourceTerminal, wire);
            Count(wire.TargetTerminal, wire);
        }

        Dictionary<WireSegment, double> currents = [];

        foreach (var wire in circuit.Wires)
        {
            if (wire.SourceTerminal is null || wire.TargetTerminal is null) continue;

            // Reading the target end needs no sign change: the current into that component
            // through that pin arrived down this wire, so it is flowing source to target.
            if (Reported(wire.TargetTerminal) is { } into)
            {
                currents[wire] = into;
                continue;
            }

            // The source end says the same thing backwards.
            if (Reported(wire.SourceTerminal) is { } outOf) currents[wire] = -outOf;
        }

        Propagate(circuit, currents, only);

        return currents;

        void Count(Terminal? terminal, WireSegment wire)
        {
            if (terminal is null) return;

            var seen = fanout.GetValueOrDefault(terminal) + 1;

            fanout[terminal] = seen;

            if (seen == 1) only[terminal] = wire;
            else only.Remove(terminal);
        }

        double? Reported(Terminal terminal)
        {
            // Only a part that can speak for one pin in particular. The generic branch current is
            // deliberately not used: it cannot tell one pin of a package from another, and its
            // sign depends on which way round the part stamped itself — so a wire drawn from it
            // could run backwards, which is worse than a wire that shows nothing.
            if (fanout.GetValueOrDefault(terminal) != 1) return null;
            if (terminal.Owner is not ICurrentReporting) return null;

            var current = simulator.TerminalCurrent(terminal);

            return double.IsNaN(current) || double.IsInfinity(current) ? null : current;
        }
    }

    /// <summary>
    /// Carries what is known through every two-terminal part, until nothing new is learned.
    /// <para>
    /// Each pass can only add wires, and there are finitely many, so it stops. The bound is there
    /// for the reader rather than the loop.
    /// </para>
    /// </summary>
    private static void Propagate(
        Circuit circuit,
        Dictionary<WireSegment, double> currents,
        Dictionary<Terminal, WireSegment> only)
    {
        var parts = circuit.Components.Where(c => c.Terminals.Count == 2).ToList();

        for (var pass = 0; pass < circuit.Wires.Count + 1; pass++)
        {
            var learned = false;

            foreach (var part in parts)
            {
                var first = part.Terminals[0];
                var second = part.Terminals[1];

                if (!only.TryGetValue(first, out var a) || !only.TryGetValue(second, out var b))
                    continue;

                var knownA = currents.ContainsKey(a);
                var knownB = currents.ContainsKey(b);

                if (knownA == knownB) continue;

                var (known, from, wanted, to) = knownA ? (a, first, b, second) : (b, second, a, first);

                // What goes in at one pin comes out at the other, which is the whole of it.
                var into = ReferenceEquals(known.TargetTerminal, from)
                    ? currents[known]
                    : -currents[known];

                currents[wanted] = ReferenceEquals(wanted.TargetTerminal, to) ? -into : into;

                learned = true;
            }

            if (!learned) return;
        }
    }
}
