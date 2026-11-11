namespace Cirq.Core.Topology;

/// <summary>
/// One hole of a solderless breadboard: which board of the set, which row and which column.
/// <para>
/// Rows run from the top power rails through the ten terminal rows to the bottom rails (see
/// <c>Breadboard</c> in Cirq.Components, which owns the geometry); columns are numbered from 1, as
/// they are printed on the board. Boards are numbered from 1 too.
/// </para>
/// </summary>
public readonly record struct BoardHole(int Board, int Row, int Column);

/// <summary>What the circuit is built on in the protoboard view.</summary>
public enum BoardKind
{
    /// <summary>Solderless breadboards: five-hole strips either side of a channel, and rails.</summary>
    Breadboard,

    /// <summary>Stripboard: copper strips the whole length, cut where two nets must part, joined by wire links.</summary>
    Stripboard,

    /// <summary>Perfboard: a pad at every hole and nothing joined, wired on the back.</summary>
    Perfboard,

    /// <summary>A single-sided printed circuit board: pads where the pins go, joined by routed copper traces on the back.</summary>
    Pcb,

    /// <summary>A double-sided printed circuit board: traces on both sides, joined through the board by vias.</summary>
    DoubleSidedPcb,
}

/// <summary>What each kind of board is.</summary>
public static class BoardKinds
{
    /// <summary>A printed circuit board, single- or double-sided.</summary>
    public static bool IsPcb(this BoardKind kind) => kind is BoardKind.Pcb or BoardKind.DoubleSidedPcb;

    /// <summary>How many layers of copper it has for traces: two on a double-sided PCB, else one.</summary>
    public static int CopperLayers(this BoardKind kind) => kind == BoardKind.DoubleSidedPcb ? 2 : 1;
}

/// <summary>Where one part was put by hand: a hole for each of its pins, or a spot on the bench beside the boards.</summary>
/// <param name="Pins">The hole each pin is in, by terminal id. Empty for a part that sits on the bench.</param>
/// <param name="BenchX">Where a bench part's top-left corner is, in board pitches, when it was moved there.</param>
/// <param name="BenchY">See <paramref name="BenchX"/>.</param>
public sealed record BoardPlacement(IReadOnlyDictionary<string, BoardHole> Pins, double? BenchX = null, double? BenchY = null);

/// <summary>
/// The protoboard view's hand-made choices: the parts somebody moved, and the jumper wires whose ends they moved.
/// <para>
/// Everything not in here is placed by the automatic layout, so a circuit nobody has arranged by hand has an empty
/// one and its file is what it always was. It is part of the document, not of the window, because a board laid
/// out for a photo should still be laid out that way when the file is opened again. It holds no electrical
/// meaning: the schematic is the circuit, and the board is a way of drawing it.
/// </para>
/// </summary>
public sealed class BoardArrangement
{
    private readonly Dictionary<Guid, BoardPlacement> _parts = [];
    private readonly Dictionary<string, (BoardHole From, BoardHole To)> _wires = new(StringComparer.Ordinal);

    /// <summary>The parts placed by hand, by component id.</summary>
    public IReadOnlyDictionary<Guid, BoardPlacement> Parts => _parts;

    /// <summary>Jumper wires whose ends were moved by hand, by the layout's key for the wire.</summary>
    public IReadOnlyDictionary<string, (BoardHole From, BoardHole To)> Wires => _wires;

    /// <summary>Nothing arranged by hand: the board is laid out automatically.</summary>
    public bool IsEmpty => _parts.Count == 0 && _wires.Count == 0;

    /// <summary>Nothing to save: laid out automatically, on breadboards, with the usual ground pour.</summary>
    public bool IsDefault => IsEmpty && _kind == BoardKind.Breadboard && _groundPour;

    private bool _groundPour = true;

    /// <summary>On a PCB, the free copper filled and joined to ground (on by default).</summary>
    public bool GroundPour
    {
        get => _groundPour;
        set
        {
            if (_groundPour == value) return;
            _groundPour = value;
            Version++;
        }
    }

    /// <summary>Counts every change, so a view can tell its drawing is out of date without comparing contents.</summary>
    public int Version { get; private set; }

    private BoardKind _kind;

    /// <summary>What the circuit is built on. Parts can be put by hand on any of them; the rest are laid out automatically.</summary>
    public BoardKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            Version++;
        }
    }

    public void SetPart(Guid part, BoardPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);
        _parts[part] = placement;
        Version++;
    }

    public bool RemovePart(Guid part)
    {
        if (!_parts.Remove(part)) return false;
        Version++;
        return true;
    }

    public void SetWire(string key, BoardHole from, BoardHole to)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _wires[key] = (from, to);
        Version++;
    }

    public bool RemoveWire(string key)
    {
        if (!_wires.Remove(key)) return false;
        Version++;
        return true;
    }

    /// <summary>Forgets everything placed by hand, so the whole board is laid out afresh.</summary>
    public void Clear()
    {
        if (IsEmpty) return;
        _parts.Clear();
        _wires.Clear();
        Version++;
    }

    /// <summary>Back to how a new circuit starts: nothing placed by hand, on breadboards.</summary>
    public void Reset()
    {
        Clear();
        Kind = BoardKind.Breadboard;
        GroundPour = true;
    }

    /// <summary>Takes another arrangement's contents in place of this one's.</summary>
    public void CopyFrom(BoardArrangement other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (ReferenceEquals(other, this)) return;
        _parts.Clear();
        _wires.Clear();
        foreach (var (id, placement) in other._parts) _parts[id] = placement;
        foreach (var (key, wire) in other._wires) _wires[key] = wire;
        _kind = other._kind;
        _groundPour = other._groundPour;
        Version++;
    }
}
