using Cirq.Components.Serialization;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.Services;

/// <summary>
/// Undo and redo, kept as whole-document snapshots rather than as a list of reversible edits.
/// <para>
/// The usual way to do this is a command pattern: every edit knows how to undo itself. That is
/// more efficient and much easier to get subtly wrong — one mutation that forgets to record its
/// inverse, or records it imprecisely, and the history quietly stops matching the document.
/// Snapshots cannot drift, because restoring one is the same code path as opening a file, which
/// is already exercised by the round-trip tests for every component, parameter, wire and probe.
/// </para>
/// <para>
/// The cost is a serialised copy per step, which for schematics of this size is a few tens of
/// kilobytes. Against a bounded depth that is a rounding error, and it buys correctness that does
/// not have to be re-established every time a component is added.
/// </para>
/// </summary>
public sealed partial class UndoHistory : ObservableObject
{
    /// <summary>One point in the document's history, and what produced it.</summary>
    private readonly record struct Snapshot(string Json, string Label);

    private readonly List<Snapshot> _undo = [];
    private readonly List<Snapshot> _redo = [];

    /// <summary>The document as of the last settled point. Never null once seeded.</summary>
    private Snapshot _current;

    private int _suspended;

    /// <summary>How many steps back the history goes.</summary>
    public int Depth { get; init; } = 100;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>What an undo would reverse, for the menu item's text.</summary>
    public string UndoLabel => CanUndo ? _current.Label : string.Empty;

    /// <summary>What a redo would reapply.</summary>
    public string RedoLabel => CanRedo ? _redo[^1].Label : string.Empty;

    /// <summary>
    /// Menu text naming the edit that would be reversed, so the item reads "Undo Add Resistor"
    /// rather than leaving the user to remember what they last did.
    /// </summary>
    public string UndoMenuText => CanUndo && UndoLabel.Length > 0 ? $"_Undo {UndoLabel}" : "_Undo";

    public string RedoMenuText => CanRedo && RedoLabel.Length > 0 ? $"_Redo {RedoLabel}" : "_Redo";

    /// <summary>True while changes are being ignored — during a drag, or during a restore.</summary>
    public bool IsSuspended => _suspended > 0;

    /// <summary>
    /// Starts again from the given circuit with no history, as when a file is opened or a new
    /// document begun. There is nothing to go back to at that point.
    /// </summary>
    public void Reset(Circuit circuit)
    {
        _undo.Clear();
        _redo.Clear();
        _current = new Snapshot(CircuitSerializer.ToJson(circuit), string.Empty);
        NotifyState();
    }

    /// <summary>
    /// Records that the circuit has just changed. The snapshot pushed is the state <b>before</b>
    /// the change, which is the one an undo has to come back to.
    /// </summary>
    public void Capture(Circuit circuit, string label)
    {
        if (IsSuspended) return;

        var json = CircuitSerializer.ToJson(circuit);

        // Nothing actually changed — a property set to the value it already had, or a
        // notification raised for its own sake. Recording it would cost the user a keystroke of
        // undo that appears to do nothing.
        if (json == _current.Json) return;

        _undo.Add(_current);
        if (_undo.Count > Depth) _undo.RemoveAt(0);

        _current = new Snapshot(json, label);

        // A new edit is a new branch: whatever was undone cannot be redone onto it.
        _redo.Clear();
        NotifyState();
    }

    /// <summary>
    /// Stops recording. Used for the span of a drag, where one gesture should cost one undo step
    /// rather than one per mouse movement.
    /// </summary>
    public void Suspend() => _suspended++;

    /// <summary>
    /// Resumes recording and accepts the circuit's present state as the current one, without
    /// pushing anything: the snapshot for this gesture was already taken when it began.
    /// </summary>
    public void Resume(Circuit circuit)
    {
        if (_suspended > 0) _suspended--;
        if (IsSuspended) return;

        _current = _current with { Json = CircuitSerializer.ToJson(circuit) };
        NotifyState();
    }

    /// <summary>The document one step back, or null when there is nothing to go back to.</summary>
    public string? Undo()
    {
        if (!CanUndo) return null;

        _redo.Add(_current);

        _current = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);

        NotifyState();
        return _current.Json;
    }

    /// <summary>The document one step forward, or null when there is nothing to go forward to.</summary>
    public string? Redo()
    {
        if (!CanRedo) return null;

        _undo.Add(_current);

        _current = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);

        NotifyState();
        return _current.Json;
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(UndoLabel));
        OnPropertyChanged(nameof(RedoLabel));
        OnPropertyChanged(nameof(UndoMenuText));
        OnPropertyChanged(nameof(RedoMenuText));
    }
}
