using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Core.Topology;

namespace Cirq.Components.Protoboard;

/// <summary>
/// Fault finding: a fault hidden in a working circuit for somebody to track down with the scope, the multimeter and
/// their reasoning, the way a breadboard that doesn't work is fixed on the bench.
/// <para>
/// The faults are the ones breadboards actually have: a lead not making contact, two neighbouring pins bridged, a
/// polarised part in backwards. Each is chosen from what the circuit really has (a pin that is wired to something, two
/// chip pins side by side on different nets, an LED, a diode or an electrolytic), and hidden in
/// <see cref="Circuit.Faults"/>, where only the simulation sees it.
/// </para>
/// </summary>
public static class FaultFinding
{
    /// <summary>Every fault this circuit could plausibly have.</summary>
    public static IReadOnlyList<CircuitFault> Candidates(Circuit circuit)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        Netlist netlist;
        try { netlist = circuit.BuildNetlist(); }
        catch (CircuitTopologyException) { return []; }

        var faults = new List<CircuitFault>();
        foreach (var part in Flattening.Flatten(circuit.Components))
        {
            var package = BoardPackages.For(part);
            if (package.Form is BoardForm.None or BoardForm.Bench) continue;

            // A pin that is joined to something, not making contact.
            foreach (var pin in part.Terminals)
                if (netlist.Contains(pin) && netlist.NetOf(pin).Terminals.Count > 1) faults.Add(new CircuitFault(FaultKind.Open, pin));

            // Neighbouring pins of a chip, bridged: only where they are on different nets, or it would change nothing.
            if (package.Form == BoardForm.Dip)
            {
                var half = package.Pins.Count / 2;
                for (var i = 0; i + 1 < package.Pins.Count; i++)
                {
                    if (i + 1 == half) continue;
                    if (package.Pins[i] is { } a && package.Pins[i + 1] is { } b && netlist.Contains(a) && netlist.Contains(b)
                        && !ReferenceEquals(netlist.NetOf(a), netlist.NetOf(b)))
                        faults.Add(new CircuitFault(FaultKind.Bridged, a, b));
                }
            }

            // A polarised two-lead part, backwards.
            if (package.Form == BoardForm.TwoLead && part is Diode or ElectrolyticCapacitor
                && netlist.Contains(part.Terminals[0]) && netlist.Contains(part.Terminals[1])
                && !ReferenceEquals(netlist.NetOf(part.Terminals[0]), netlist.NetOf(part.Terminals[1])))
                faults.Add(new CircuitFault(FaultKind.Reversed, part.Terminals[0], part.Terminals[1]));
        }

        return faults;
    }

    /// <summary>
    /// Hides one fault, chosen at random, in place of any already hidden. Opens are the commonest fault on a real board
    /// and most of the candidates, so the other kinds are given more weight than their number to keep it interesting.
    /// </summary>
    /// <returns>The fault, or null when the circuit has nothing a fault could be hidden in.</returns>
    public static CircuitFault? Hide(Circuit circuit, Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        var candidates = Candidates(circuit);
        if (candidates.Count == 0) return null;
        random ??= Random.Shared;
        var pool = candidates.ToList();
        for (var tries = 0; tries < 12 && pool.Count > 0; tries++)
        {
            var kinds = pool.Select(c => c.Kind).Distinct().ToList();
            var kind = kinds[random.Next(kinds.Count)];
            var ofKind = pool.Where(c => c.Kind == kind).ToList();
            var fault = ofKind[random.Next(ofKind.Count)];
            pool.Remove(fault);
            if (!Survives(circuit, fault)) continue;
            circuit.Faults.Clear();
            circuit.Faults.Add(fault);
            return fault;
        }

        return null;
    }

    /// <summary>
    /// Whether the circuit still simulates with the fault in it. A bridge across two supply pins is a short no solver
    /// can answer, and a fault that stops the simulation with an error gives itself away; those aren't puzzles.
    /// </summary>
    public static bool Survives(Circuit circuit, CircuitFault fault)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(fault);

        // On a copy, so the circuit on screen (and a run of it that may be going) is never touched.
        var copy = Serialization.CircuitSerializer.FromJson(Serialization.CircuitSerializer.ToJson(circuit)).Circuit;
        Terminal? Find(Terminal? t) =>
            t?.Owner is not { } owner ? null
            : Flattening.Flatten(copy.Components).FirstOrDefault(c => c.Id == owner.Id)?.Terminals.FirstOrDefault(x => x.Id == t.Id);
        if (Find(fault.First) is not { } first) return false;
        copy.Faults.Add(fault with { First = first, Second = Find(fault.Second) });
        try
        {
            var simulator = new Cirq.Engine.Simulation.CircuitSimulator(copy, new Cirq.Core.Simulation.SimulationSettings { TimeStep = 1e-6 });
            simulator.Reset();
            simulator.SolveOperatingPoint();
            for (var i = 0; i < 20; i++) simulator.Step();
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Hides a particular fault, for a puzzle somebody chose: refused if it isn't one the circuit could have.</summary>
    public static CircuitFault Hide(Circuit circuit, FaultKind kind, Terminal first, Terminal? second = null)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(first);
        var fault = Candidates(circuit).FirstOrDefault(c => c.Kind == kind && ReferenceEquals(c.First, first)
                                                            && (second is null || ReferenceEquals(c.Second, second)))
            ?? Candidates(circuit).FirstOrDefault(c => c.Kind == kind && ReferenceEquals(c.Second, first) && (second is null || ReferenceEquals(c.First, second)))
            ?? throw new ArgumentException(kind switch
            {
                FaultKind.Open => $"{first} can't be the open pin: it isn't joined to anything, so leaving it out would change nothing.",
                FaultKind.Bridged => $"A bridge goes between two neighbouring pins of a chip that are on different nets; {first}{(second is null ? "" : $" and {second}")} aren't.",
                _ => $"Only an LED, a diode or an electrolytic can be in backwards, and only when its two leads are on different nets; {first.Owner?.Name} isn't one.",
            });
        if (!Survives(circuit, fault))
            throw new ArgumentException($"With that fault the circuit doesn't simulate at all, which would give it away: {fault.Describe()} Choose another.");
        circuit.Faults.Clear();
        circuit.Faults.Add(fault);
        return fault;
    }

    /// <summary>
    /// A hint, each more telling than the last: what kind of fault it is, then the net to look at, then the part.
    /// </summary>
    /// <param name="level">1, 2 or 3.</param>
    public static string Hint(Circuit circuit, int level)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        if (circuit.Faults.Count == 0) return "There's no fault hidden in this circuit.";
        var fault = circuit.Faults[0];
        switch (Math.Clamp(level, 1, 3))
        {
            case 1:
                return fault.Kind switch
                {
                    FaultKind.Open => "Hint: somewhere a pin isn't making contact. Look for a node that's floating, or a part with no current through it.",
                    FaultKind.Bridged => "Hint: two chip pins that should be apart are joined. Look for two signals that move together when they shouldn't.",
                    _ => "Hint: a polarised part is in backwards. Look for a diode drop where there shouldn't be one, or none where there should.",
                };
            case 2:
                var net = circuit.BuildNetlist().NetOf(fault.First);
                var neighbours = net.Terminals.Where(t => t.Owner is { } o && BoardPackages.IsBuilt(o) && !ReferenceEquals(t, fault.First)).Take(4).Select(t => t.ToString());
                return $"Hint: look at the net with {string.Join(", ", neighbours)} on it.";
            default:
                return $"Hint: it's on {string.Join(" and ", fault.Parts.Select(p => p.Name))}.";
        }
    }

    /// <summary>Whether a guess is right: the part, or one of the two parts, the fault is at.</summary>
    public static bool IsFaultAt(Circuit circuit, CircuitComponent part) =>
        circuit is not null && part is not null && circuit.Faults.Any(f => f.Parts.Contains(part));
}
