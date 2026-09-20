using Cirq.Components.Serialization;
using Cirq.Core.Primitives;
using Cirq.Core.Topology;

namespace Cirq.UI.Services;

/// <summary>
/// Holds a copied selection — one part or a whole group of them, with the wires that ran between
/// them — and hands out independent duplicates.
/// <para>
/// It keeps detached duplicates rather than the parts that were copied, so the clipboard survives
/// the originals being edited, moved or deleted: copy a resistor, change it to 47 k, paste, and you
/// get the 10 k you copied. That is what copying means everywhere else.
/// </para>
/// <para>
/// The clipboard is the application's own rather than the desktop's. Within one window the two are
/// indistinguishable, and this one is deterministic: no async round trip, no permission prompt, and
/// nothing to go wrong on a machine with no clipboard manager running.
/// </para>
/// </summary>
public sealed class ComponentClipboard
{
    /// <summary>How far a pasted copy lands from what it was pasted from, in schematic units.</summary>
    public const double PasteOffset = 40.0;

    /// <summary>
    /// A wire between two held parts, recorded by their <i>positions in the held list</i> rather
    /// than by any identity. The parts on the clipboard are duplicates and the ones a paste
    /// produces are duplicates again, so the only thing that survives all of that is which member
    /// of the group a wire went to and which of its pins.
    /// </summary>
    private sealed record HeldWire(
        int FromIndex, string FromTerminal, int ToIndex, string ToTerminal, IReadOnlyList<Point> Waypoints);

    private readonly List<CircuitComponent> _held = [];
    private readonly List<HeldWire> _wires = [];

    private int _pasteCount;

    /// <summary>True when there is something to paste.</summary>
    public bool HasContent => _held.Count > 0;

    /// <summary>How many parts are on the clipboard.</summary>
    public int Count => _held.Count;

    /// <summary>How many wires came with them.</summary>
    public int WireCount => _wires.Count;

    /// <summary>What is waiting, for the menu to say so: a part by type, a group by size.</summary>
    public string? HeldDescription => _held.Count switch
    {
        0 => null,
        1 => _held[0].ComponentType,
        _ => $"{_held.Count} parts",
    };

    /// <summary>
    /// Takes a copy of one part, with no wires — a single part has no connections of its own, only
    /// connections to things that are not being copied.
    /// </summary>
    public void Copy(CircuitComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        Copy([component], []);
    }

    /// <summary>
    /// Takes a copy of a group and the wires among them.
    /// <para>
    /// A wire is only worth copying when <b>both</b> of its ends are on parts in the group. One
    /// with an end outside it is a connection to something that is not being duplicated, and there
    /// is nothing sensible for the copy to attach to — so those are dropped rather than guessed at.
    /// </para>
    /// </summary>
    public void Copy(IReadOnlyCollection<CircuitComponent> components, IEnumerable<WireSegment> wires)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(wires);

        Clear();

        if (components.Count == 0) return;

        // Position in this list is the identity everything downstream uses.
        var index = new Dictionary<CircuitComponent, int>();

        foreach (var component in components)
        {
            index[component] = _held.Count;
            _held.Add(CircuitSerializer.Clone(component));
        }

        foreach (var wire in wires)
        {
            var from = wire.SourceTerminal?.Owner;
            var to = wire.TargetTerminal?.Owner;

            if (from is null || to is null) continue;
            if (!index.TryGetValue(from, out var fromIndex)) continue;
            if (!index.TryGetValue(to, out var toIndex)) continue;

            _wires.Add(new HeldWire(
                fromIndex, wire.SourceTerminal!.Id,
                toIndex, wire.TargetTerminal!.Id,
                [.. wire.Waypoints]));
        }
    }

    /// <summary>
    /// Puts a copy of everything held into the circuit and returns the new parts, or an empty list
    /// if there is nothing on the clipboard.
    /// <para>
    /// The group keeps its shape: every part lands the same distance and direction from the others
    /// as it was copied at, and the whole thing is offset together. Each paste lands a little
    /// further along than the last, so pressing <c>Ctrl+V</c> four times gives four groups you can
    /// see rather than four stacked on one another.
    /// </para>
    /// </summary>
    public IReadOnlyList<CircuitComponent> PasteInto(Circuit circuit, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        if (_held.Count == 0) return [];

        _pasteCount++;
        var shift = PasteOffset * _pasteCount;

        var pasted = new List<CircuitComponent>(_held.Count);

        foreach (var held in _held)
        {
            var copy = CircuitSerializer.Clone(held, warnings);

            copy.X = held.X + shift;
            copy.Y = held.Y + shift;

            // Blank name in, next free designator out.
            pasted.Add(circuit.Add(copy));
        }

        foreach (var wire in _wires)
        {
            var from = TerminalOn(pasted[wire.FromIndex], wire.FromTerminal);
            var to = TerminalOn(pasted[wire.ToIndex], wire.ToTerminal);

            if (from is null || to is null)
            {
                warnings?.Add("a wire could not be reconnected on the copy");
                continue;
            }

            var copy = circuit.Connect(from, to);

            foreach (var point in wire.Waypoints)
                copy.Waypoints.Add(new Point(point.X + shift, point.Y + shift));
        }

        return pasted;
    }

    /// <summary>Forgets what it is holding.</summary>
    public void Clear()
    {
        _held.Clear();
        _wires.Clear();
        _pasteCount = 0;
    }

    private static Terminal? TerminalOn(CircuitComponent component, string id) =>
        component.Terminals.FirstOrDefault(t => t.Id == id);
}
