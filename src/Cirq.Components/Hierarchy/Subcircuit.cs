using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Hierarchy;

/// <summary>
/// A block: a group of parts drawn as one thing, with pins where its wires crossed the boundary.
/// <para>
/// Grouping <b>moves</b> the parts inside rather than copying them. That matters more than it
/// sounds: the components keep their identity, so a probe attached to one of them still reads it,
/// whatever state it had carries on, and ungrouping gives back the same parts rather than
/// lookalikes. A block is a line drawn round part of the drawing, not a new circuit built to
/// resemble it.
/// </para>
/// <para>
/// It stamps nothing itself. Before the solve the hierarchy is flattened and its contents join
/// everything else in one matrix, with each port's two terminals treated as one point — so a
/// block is exactly as accurate as the parts inside it, because it is the parts inside it.
/// </para>
/// </summary>
public sealed partial class Subcircuit : CircuitComponent, ISubcircuit
{
    private readonly List<CircuitComponent> _inner = [];
    private readonly List<WireSegment> _innerWires = [];
    private readonly List<(Terminal Outer, Terminal Inner)> _ports = [];

    public Subcircuit()
    {
        Terminals = [];
    }

    /// <summary>What the block is called, drawn across the middle of it.</summary>
    [ObservableProperty]
    public partial string BlockName { get; set; } = "Block";

    public override string ComponentType => "Subcircuit";

    public override string DesignatorPrefix => "X";

    public override string ValueLabel => $"{_inner.Count} part{(_inner.Count == 1 ? string.Empty : "s")}";

    public IReadOnlyList<CircuitComponent> InnerComponents => _inner;

    public IReadOnlyList<WireSegment> InnerWires => _innerWires;

    public IReadOnlyList<(Terminal Outer, Terminal Inner)> Ports => _ports;

    /// <summary>Half the drawn width.</summary>
    public double HalfWidth => 58.0;

    /// <summary>Half the drawn height, sized so the pins down each side are not crowded.</summary>
    public double HalfHeight => Math.Max(38.0, (((_ports.Count + 1) / 2) * 22.0) + 16.0);

    /// <summary>A block contributes nothing of its own; its contents do all the work.</summary>
    public override void StampMatrix(MnaSystem system, SimulationState state) { }

    /// <summary>Takes a part inside. Used while a block is being built or loaded.</summary>
    public void AddInner(CircuitComponent component) => _inner.Add(component);

    /// <summary>Takes a wire inside, one that joins two parts that are both in the block.</summary>
    public void AddInnerWire(WireSegment wire) => _innerWires.Add(wire);

    /// <summary>
    /// Brings a terminal out as a pin. The name is what the pin is called on the block; the inner
    /// terminal is the point it is the same as.
    /// </summary>
    public Terminal AddPort(string name, Terminal inner)
    {
        var index = _ports.Count;

        // Alternating sides, top to bottom, so a block with four pins looks like a part rather
        // than a column of them.
        var left = index % 2 == 0;
        var row = index / 2;

        var terminal = new Terminal(
            $"p{index}", name, TerminalType.Passive,
            new Point(left ? -HalfWidth : HalfWidth, -HalfHeight + 26 + (row * 22)));

        _ports.Add((terminal, inner));
        SetTerminals([.. Terminals, terminal]);

        NotifyValueChanged();
        return terminal;
    }

    /// <summary>Empties it, for when its contents are going back onto the sheet.</summary>
    public void Release()
    {
        _inner.Clear();
        _innerWires.Clear();
        _ports.Clear();

        SetTerminals([]);
        NotifyValueChanged();
    }

    /// <summary>
    /// Every part inside, at every depth — for counting and reporting. The solver builds its own
    /// flattened list rather than using this.
    /// </summary>
    public IEnumerable<CircuitComponent> Descendants()
    {
        foreach (var component in _inner)
        {
            yield return component;

            if (component is Subcircuit nested)
                foreach (var deeper in nested.Descendants())
                    yield return deeper;
        }
    }

    public override void ResetState()
    {
        foreach (var component in _inner) component.ResetState();
    }

    partial void OnBlockNameChanged(string value) => NotifyValueChanged();
}
