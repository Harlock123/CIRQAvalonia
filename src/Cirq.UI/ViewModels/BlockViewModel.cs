using Cirq.Components.Hierarchy;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.ViewModels;

/// <summary>
/// What is inside a block, drawn, with the probes reaching in.
/// <para>
/// A block hides a section behind one symbol, which is the point of it — and until now that
/// included hiding it from <i>you</i>. The only thing you could do with one was ungroup it, look,
/// and group it again, and there was no way at all to put a probe on a node inside: the canvas only
/// draws what is on the sheet, so an internal node could never be clicked. A hierarchy you cannot
/// measure inside is half a hierarchy.
/// </para>
/// <para>
/// The contents are the <b>same parts</b>, not copies: this hands the canvas a circuit whose
/// components are the block's own, so what is drawn is the thing itself and changing a value here
/// changes the value in the block. Adding and removing parts is still done by ungrouping — that
/// changes the block's shape rather than its contents, and the honest place for it is the sheet.
/// </para>
/// <para>
/// A probe attached here goes on the <b>parent</b> circuit, pointing at the terminal inside. That
/// is what makes it show on the main scope, and it survives a save: the netlist opens blocks out
/// before it resolves anything, so an internal terminal is a terminal like any other.
/// </para>
/// </summary>
public sealed partial class BlockViewModel : ObservableObject
{
    private readonly Circuit _parent;
    private readonly Subcircuit _block;

    public BlockViewModel(Subcircuit block, Circuit parent)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(parent);

        _block = block;
        _parent = parent;

        // A circuit for the canvas to draw, holding the block's own parts and wires rather than
        // copies of them.
        foreach (var component in block.InnerComponents) Contents.Components.Add(component);
        foreach (var wire in block.InnerWires) Contents.Wires.Add(wire);

        Contents.Title = block.Name;

        // The probes that already point inside, so they are drawn here rather than appearing to
        // have vanished.
        foreach (var probe in parent.Probes.Where(PointsInside)) Contents.Probes.Add(probe);

        Inspector = new InspectorViewModel();

        Summary = block.InnerComponents.Count == 0
            ? "This block has nothing in it."
            : $"{block.InnerComponents.Count} part(s), {block.InnerWires.Count} wire(s), " +
              $"{block.Ports.Count} pin(s). Values edit in place; use the probe tool to measure a " +
              "node inside.";
    }

    /// <summary>The two tools that mean anything in here: look at a part, or probe a node.</summary>
    public static IReadOnlyList<EditorTool> Tools { get; } = [EditorTool.Select, EditorTool.Probe];

    /// <summary>The block's contents, as a circuit the canvas can draw.</summary>
    public Circuit Contents { get; } = new();

    public InspectorViewModel Inspector { get; }

    [ObservableProperty]
    public partial CircuitComponent? Selected { get; set; }

    [ObservableProperty]
    public partial EditorTool ActiveTool { get; set; } = EditorTool.Select;

    [ObservableProperty]
    public partial string Summary { get; set; }

    /// <summary>
    /// What to call the block on screen: its designator and what it is — "X1 — Divider" — because
    /// the designator is what is on the drawing and the name is what it means.
    /// </summary>
    public string Name => _block.BlockName.Trim().Length == 0 || _block.BlockName == _block.Name
        ? _block.Name
        : $"{_block.Name} — {_block.BlockName}";

    /// <summary>Raised when a probe is attached, so the parent can re-resolve and redraw.</summary>
    public event EventHandler? ProbesChanged;

    /// <summary>Raised when something inside changed enough that the engine has to rebuild.</summary>
    public event EventHandler? Changed;

    partial void OnSelectedChanged(CircuitComponent? value) => Inspector.Component = value;

    /// <summary>
    /// Attaches a probe to a terminal inside the block. It goes on the parent circuit, because that
    /// is the circuit being simulated and the scope showing it belongs to the main window.
    /// </summary>
    public SignalProbe Probe(Terminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);

        var existing = _parent.Probes.FirstOrDefault(p => terminal.Equals(p.TargetTerminal));

        if (existing is not null) return existing;

        // Named for where it is — "U1 · R2.B" — because "R2.B" on the scope, in a circuit whose
        // sheet has no R2 on it, is a label nobody can place.
        var probe = new SignalProbe($"{_block.Name} · {terminal}", terminal, NextColour());

        _parent.Probes.Add(probe);
        Contents.Probes.Add(probe);

        ProbesChanged?.Invoke(this, EventArgs.Empty);

        Summary = $"Probing {probe.Label}. It is on the main scope.";

        return probe;
    }

    /// <summary>Announces that a value inside changed, so the circuit is dirty and rebuilds.</summary>
    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private bool PointsInside(SignalProbe probe) =>
        probe.TargetTerminal?.Owner is { } owner && _block.InnerComponents.Contains(owner);

    /// <summary>
    /// The next colour in the probe palette, counted across the parent's probes so a probe added
    /// in here does not come out the same colour as one already on the scope.
    /// </summary>
    private Cirq.Core.Primitives.Color NextColour() =>
        Cirq.Core.Primitives.Color.ProbePalette[
            _parent.Probes.Count % Cirq.Core.Primitives.Color.ProbePalette.Count];
}
