namespace Cirq.Core.Topology;

/// <summary>
/// One net picked out on the drawing: everything that is electrically the same point.
/// </summary>
/// <param name="Name">What the net is called — its label if it has one, otherwise N3 or GND.</param>
/// <param name="Wires">The wires on it that are drawn at the top level.</param>
/// <param name="Terminals">Every pin on it, blocks opened out.</param>
/// <param name="Components">The parts it touches, by name, in order.</param>
/// <param name="IsGround">True for the ground net, which is usually the biggest and least useful.</param>
public sealed record NetHighlight(
    string Name,
    IReadOnlySet<WireSegment> Wires,
    IReadOnlySet<Terminal> Terminals,
    IReadOnlyList<string> Components,
    bool IsGround)
{
    /// <summary>
    /// A line saying what is on it, which is the point of asking. "N4: R2, C1, U1 pin 3" answers
    /// the question in one go where tracing the wires by eye does not.
    /// </summary>
    public string Summary
    {
        get
        {
            var parts = Components.Count switch
            {
                0 => "nothing",
                <= 6 => string.Join(", ", Components),
                _ => $"{string.Join(", ", Components.Take(6))} and {Components.Count - 6} more",
            };

            return $"{Name}: {parts}";
        }
    }
}

/// <summary>
/// Works out everything joined to a point on the drawing.
/// <para>
/// Past a certain size a schematic stops being readable by eye. Net labels make that worse in
/// exchange for making it possible at all: a rail named <c>VCC</c> in six places is six pieces of
/// text with no line between them, and the only way to be sure they are the same net is to trust
/// that you typed them identically. A block's pins are the same problem from the other direction.
/// </para>
/// <para>
/// So this asks the netlist — the same one the solver uses, with blocks opened out and labels
/// resolved — rather than following the wires as drawn. What lights up is what the simulator
/// thinks is one node, which is the only definition that matters.
/// </para>
/// </summary>
public static class NetHighlighting
{
    /// <summary>
    /// The net a terminal is on, or null when the circuit will not resolve or the terminal is not
    /// in it.
    /// </summary>
    public static NetHighlight? For(Circuit circuit, Terminal? terminal)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        if (terminal is null) return null;

        Netlist netlist;

        try
        {
            netlist = circuit.BuildNetlist();
        }
        catch (CircuitTopologyException)
        {
            // A circuit that will not resolve has no nets to point at. Saying nothing is better
            // than guessing from the wires as drawn, which is exactly what this exists to avoid.
            return null;
        }

        return netlist.Contains(terminal) ? From(circuit, netlist.NetOf(terminal)) : null;
    }

    /// <summary>The net a wire is part of, taken from whichever end it can resolve.</summary>
    public static NetHighlight? For(Circuit circuit, WireSegment? wire) =>
        wire is null ? null : For(circuit, wire.SourceTerminal) ?? For(circuit, wire.TargetTerminal);

    private static NetHighlight From(Circuit circuit, Net net)
    {
        var terminals = new HashSet<Terminal>(net.Terminals);

        // Only wires drawn on this sheet. A block's internal wiring is on the net too, but it is
        // not on the drawing, so lighting it up would highlight nothing a person can see.
        var wires = new HashSet<WireSegment>(circuit.Wires.Where(w =>
            (w.SourceTerminal is not null && terminals.Contains(w.SourceTerminal))
            || (w.TargetTerminal is not null && terminals.Contains(w.TargetTerminal))));

        // Named parts rather than terminals, because "R2, C1, U1" is what somebody wants to know
        // and "R2 pin A, R2 pin B" is noise — a part joined to a net twice is still one part.
        List<string> components = [];

        foreach (var pin in net.Terminals)
        {
            if (pin.Owner is not { } owner) continue;

            var name = owner.Name.Length > 0 ? owner.Name : owner.ComponentType;

            if (!components.Contains(name)) components.Add(name);
        }

        components.Sort(StringComparer.OrdinalIgnoreCase);

        return new NetHighlight(net.Name, wires, terminals, components, net.IsGround);
    }
}
