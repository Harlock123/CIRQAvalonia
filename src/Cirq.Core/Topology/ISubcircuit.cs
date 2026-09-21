namespace Cirq.Core.Topology;

/// <summary>
/// A part that is itself a circuit: a block with pins on the outside and a schematic inside.
/// <para>
/// Past a certain size a drawing stops being readable however neatly it is routed, and the answer
/// everywhere else in engineering is to draw the parts that belong together as one thing and say
/// what it does rather than how. A power supply, an input stage, one channel of something there
/// are four of — each is a page of detail that is not the point when you are looking at the
/// system.
/// </para>
/// <para>
/// It is not a new kind of simulation. Before anything is solved the hierarchy is <b>flattened</b>:
/// the block's contents join the parts outside it in one netlist, with each port's outer pin and
/// inner terminal treated as the same point. So a block is exactly as accurate as the same parts
/// drawn loose, because it <i>is</i> the same parts — the grouping is a fact about the drawing and
/// not about the circuit.
/// </para>
/// </summary>
public interface ISubcircuit
{
    /// <summary>What is inside. These take part in the solve as though they were on the sheet.</summary>
    IReadOnlyList<CircuitComponent> InnerComponents { get; }

    /// <summary>The wires between them.</summary>
    IReadOnlyList<WireSegment> InnerWires { get; }

    /// <summary>
    /// Each pin on the outside and the terminal inside that it is the same point as. This is the
    /// whole of the block's interface, and everything else about it is private.
    /// </summary>
    IReadOnlyList<(Terminal Outer, Terminal Inner)> Ports { get; }
}

/// <summary>
/// Walking a circuit that may contain blocks.
/// </summary>
public static class Flattening
{
    /// <summary>
    /// Every component that takes part in the solve, with blocks opened out — and blocks inside
    /// blocks opened out too. The blocks themselves are included: they stamp nothing, but they are
    /// still components of the document, and leaving them out would make the counts disagree.
    /// </summary>
    public static IEnumerable<CircuitComponent> Flatten(IEnumerable<CircuitComponent> components)
    {
        foreach (var component in components)
        {
            yield return component;

            if (component is not ISubcircuit block) continue;

            foreach (var inner in Flatten(block.InnerComponents)) yield return inner;
        }
    }

    /// <summary>Every wire, inside every block as well as on the sheet.</summary>
    public static IEnumerable<WireSegment> FlattenWires(
        IEnumerable<CircuitComponent> components, IEnumerable<WireSegment> wires)
    {
        foreach (var wire in wires) yield return wire;

        foreach (var component in components)
        {
            if (component is not ISubcircuit block) continue;

            foreach (var inner in FlattenWires(block.InnerComponents, block.InnerWires))
                yield return inner;
        }
    }

    /// <summary>Every port pairing, at every depth.</summary>
    public static IEnumerable<(Terminal Outer, Terminal Inner)> Ports(
        IEnumerable<CircuitComponent> components)
    {
        foreach (var component in components)
        {
            if (component is not ISubcircuit block) continue;

            foreach (var port in block.Ports) yield return port;

            foreach (var nested in Ports(block.InnerComponents)) yield return nested;
        }
    }

    /// <summary>True when anything in the circuit is a block, so the cheap path can be taken.</summary>
    public static bool HasBlocks(IEnumerable<CircuitComponent> components) =>
        components.Any(c => c is ISubcircuit);
}
