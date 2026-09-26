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

    /// <summary>
    /// How wide the symbol is drawn, in canvas units. Zero means "work it out", which is what every
    /// block does until somebody draws it themselves.
    /// </summary>
    [ObservableProperty]
    public partial double SymbolWidth { get; set; }

    /// <summary>How tall it is drawn. Zero means the height the pins need.</summary>
    [ObservableProperty]
    public partial double SymbolHeight { get; set; }

    /// <summary>Half the drawn width.</summary>
    public double HalfWidth => SymbolWidth > 0 ? SymbolWidth / 2.0 : 58.0;

    /// <summary>Half the drawn height, sized so the pins down each side are not crowded.</summary>
    public double HalfHeight => SymbolHeight > 0
        ? SymbolHeight / 2.0
        : Math.Max(38.0, (((_ports.Count + 1) / 2) * 22.0) + 16.0);

    /// <summary>True once somebody has drawn this symbol rather than letting it arrange itself.</summary>
    public bool HasOwnSymbol => SymbolWidth > 0 || SymbolHeight > 0;

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

        // Every pin is placed again, not just this one. The height of the block depends on how many
        // pins it has, so the ones added earlier were placed against a shorter body — which left a
        // block of five pins with two of them drawn on top of each other.
        if (!HasOwnSymbol) ArrangePorts();

        NotifyValueChanged();
        return terminal;
    }

    /// <summary>
    /// Moves a pin to a point on the symbol's edge. The terminal keeps its identity, so everything
    /// wired to it stays wired to it — a pin that has moved is a drawing change, not a rewiring.
    /// </summary>
    public void PlacePort(Terminal port, Point offset)
    {
        ArgumentNullException.ThrowIfNull(port);

        if (!_ports.Any(p => ReferenceEquals(p.Outer, port))) return;

        port.CanvasOffset = offset;

        NotifyValueChanged();
    }

    /// <summary>
    /// Renames a pin. The name is what the symbol says beside it and what a board netlist calls it;
    /// the terminal itself is untouched, so every wire, probe and net on it stays where it was.
    /// </summary>
    public void RenamePort(Terminal port, string name)
    {
        ArgumentNullException.ThrowIfNull(port);

        if (!_ports.Any(p => ReferenceEquals(p.Outer, port))) return;

        port.Name = name;

        NotifyValueChanged();
    }

    /// <summary>
    /// Puts every pin back where the block would have put it: alternating sides, top to bottom.
    /// Also what a block does before anybody has drawn it.
    /// </summary>
    public void ArrangePorts()
    {
        for (var index = 0; index < _ports.Count; index++)
        {
            var left = index % 2 == 0;
            var row = index / 2;

            _ports[index].Outer.CanvasOffset = new Point(
                left ? -HalfWidth : HalfWidth, -HalfHeight + 26 + (row * 22));
        }

        NotifyValueChanged();
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

    partial void OnSymbolWidthChanged(double value) => NotifyValueChanged();

    partial void OnSymbolHeightChanged(double value) => NotifyValueChanged();
}
