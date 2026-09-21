using Cirq.Core.Topology;

namespace Cirq.Components.Hierarchy;

/// <summary>
/// Turning a selection into a block, and a block back into a selection.
/// <para>
/// The whole of the work is deciding where the boundary is. A wire with both ends in the selection
/// belongs inside; a wire with neither belongs outside; a wire with <i>one</i> end in it is
/// crossing the boundary, and that is where a pin goes. Everything else follows from those three
/// cases.
/// </para>
/// </summary>
public static class Grouping
{
    /// <summary>
    /// Groups the given components into a block on the same sheet, moving them inside it.
    /// <para>
    /// Returns null when there is nothing worth grouping — fewer than two parts, or a selection
    /// that would need the block to contain something already inside another one.
    /// </para>
    /// </summary>
    public static Subcircuit? Group(
        Circuit circuit, IReadOnlyCollection<CircuitComponent> selection, string name = "Block")
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(selection);

        var inside = selection.Where(circuit.Components.Contains).ToHashSet();

        if (inside.Count < 2) return null;

        var block = new Subcircuit { BlockName = name };

        // Placed at the middle of what it replaces, which is where the eye expects it.
        block.X = inside.Average(c => c.X);
        block.Y = inside.Average(c => c.Y);

        foreach (var component in inside) block.AddInner(component);

        // Wires, sorted into the three cases.
        var crossing = new List<(WireSegment Wire, Terminal Inner, bool InnerIsSource)>();

        foreach (var wire in circuit.Wires.ToList())
        {
            if (wire.SourceTerminal is null || wire.TargetTerminal is null) continue;

            var sourceIn = wire.SourceTerminal.Owner is { } a && inside.Contains(a);
            var targetIn = wire.TargetTerminal.Owner is { } b && inside.Contains(b);

            if (sourceIn && targetIn)
            {
                block.AddInnerWire(wire);
                circuit.Wires.Remove(wire);
                continue;
            }

            if (sourceIn) crossing.Add((wire, wire.SourceTerminal, true));
            else if (targetIn) crossing.Add((wire, wire.TargetTerminal, false));
        }

        // One pin per distinct inner terminal that something outside is connected to. Two wires
        // landing on the same inner pin share one pin on the block, because they are one net.
        var pins = new Dictionary<Terminal, Terminal>();

        foreach (var (wire, inner, innerIsSource) in crossing)
        {
            if (!pins.TryGetValue(inner, out var pin))
            {
                pin = block.AddPort(PortName(inner, pins.Count), inner);
                pins[inner] = pin;
            }

            // The outside wire now lands on the block instead of reaching through it.
            if (innerIsSource) wire.SourceTerminal = pin;
            else wire.TargetTerminal = pin;
        }

        foreach (var component in inside) circuit.Components.Remove(component);

        circuit.Add(block);
        return block;
    }

    /// <summary>
    /// Puts a block's contents back on the sheet and removes the block, reconnecting whatever was
    /// wired to its pins.
    /// </summary>
    public static IReadOnlyList<CircuitComponent> Ungroup(Circuit circuit, Subcircuit block)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(block);

        if (!circuit.Components.Contains(block)) return [];

        var released = block.InnerComponents.ToList();
        var innerWires = block.InnerWires.ToList();
        var ports = block.Ports.ToList();

        // Anything wired to a pin is re-pointed at the terminal inside that the pin stood for.
        var map = ports.ToDictionary(p => p.Outer, p => p.Inner);

        foreach (var wire in circuit.Wires)
        {
            if (wire.SourceTerminal is { } source && map.TryGetValue(source, out var innerSource))
                wire.SourceTerminal = innerSource;

            if (wire.TargetTerminal is { } target && map.TryGetValue(target, out var innerTarget))
                wire.TargetTerminal = innerTarget;
        }

        block.Release();
        circuit.Components.Remove(block);

        foreach (var component in released) circuit.Components.Add(component);
        foreach (var wire in innerWires) circuit.Wires.Add(wire);

        return released;
    }

    /// <summary>
    /// What to call a pin. The part and terminal it came from, which is far more use than P1
    /// through P6 when you are looking at a block from the outside.
    /// </summary>
    private static string PortName(Terminal inner, int index)
    {
        var owner = inner.Owner?.Name;

        if (string.IsNullOrWhiteSpace(owner)) return $"P{index + 1}";

        return inner.Name.Length > 0 ? $"{owner}.{inner.Name}" : owner;
    }
}
