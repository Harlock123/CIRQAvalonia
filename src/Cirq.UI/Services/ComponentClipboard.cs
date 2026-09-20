using Cirq.Components.Serialization;
using Cirq.Core.Topology;

namespace Cirq.UI.Services;

/// <summary>
/// Holds a copied component and hands out independent duplicates of it.
/// <para>
/// It keeps a detached <see cref="CircuitComponent"/> rather than the one that was copied, so the
/// clipboard survives the original being edited, moved or deleted — copy a resistor, change it to
/// 47 k, paste, and you get the 10 k you copied. That is what copying means everywhere else.
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

    private CircuitComponent? _held;
    private int _pasteCount;

    /// <summary>True when there is something to paste.</summary>
    public bool HasContent => _held is not null;

    /// <summary>What kind of part is on the clipboard, for the menu to say so.</summary>
    public string? HeldDescription => _held?.ComponentType;

    /// <summary>
    /// Takes a copy of a component. The clipboard holds its own duplicate from this moment, so
    /// what gets pasted is what was copied rather than whatever the original has become since.
    /// </summary>
    public void Copy(CircuitComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        _held = CircuitSerializer.Clone(component);
        _pasteCount = 0;
    }

    /// <summary>
    /// Puts a copy into the circuit and returns it, or null if there is nothing held.
    /// <para>
    /// Each paste lands a little further down and to the right than the last, so pressing
    /// <c>Ctrl+V</c> four times gives four parts you can see rather than four stacked on one
    /// another with only the top one reachable.
    /// </para>
    /// </summary>
    public CircuitComponent? PasteInto(Circuit circuit, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        if (_held is null) return null;

        var copy = CircuitSerializer.Clone(_held, warnings);

        _pasteCount++;
        copy.X = _held.X + (PasteOffset * _pasteCount);
        copy.Y = _held.Y + (PasteOffset * _pasteCount);

        // Blank name in, next free designator out.
        return circuit.Add(copy);
    }

    /// <summary>Forgets what it is holding.</summary>
    public void Clear()
    {
        _held = null;
        _pasteCount = 0;
    }
}
