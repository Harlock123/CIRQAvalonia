using Cirq.Core.Topology;

namespace Cirq.Components.Protoboard;

/// <summary>A change to the board made by hand: the arrangement it leaves, or why it can't be made.</summary>
/// <param name="Arrangement">What <see cref="Circuit.Board"/> becomes, when the change can be made.</param>
/// <param name="Layout">The board laid out with it.</param>
/// <param name="Problem">Why not, in a sentence, when it can't.</param>
public sealed record BoardEdit(BoardArrangement? Arrangement, ProtoboardLayout? Layout, string? Problem)
{
    public bool Succeeded => Problem is null;

    internal static BoardEdit No(string why) => new(null, null, why);
}

/// <summary>
/// Moving things on the board by hand: a part, one lead of a part, the end of a jumper wire, a piece of bench
/// equipment.
/// <para>
/// Every edit starts by freezing the board where it is (<see cref="ProtoboardLayout.Freeze"/>), so moving one part
/// leaves everything else alone rather than laying the board out again around it; the jumpers are worked out afresh
/// for the new places. An edit that would put a pin in a hole that's taken, or in a strip carrying another net (which
/// would join the two), is refused with the reason.
/// </para>
/// </summary>
public static class ProtoboardEdits
{
    /// <summary>
    /// Moves a whole part by the step from <paramref name="grab"/> (one of its holes) to <paramref name="to"/>. A DIP
    /// keeps its rows across the channel and moves along it.
    /// </summary>
    public static BoardEdit MovePart(Circuit circuit, ProtoboardLayout layout, CircuitComponent part, BoardHole grab, BoardHole to)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Find(part) is not { } placed) return BoardEdit.No($"{part?.Name} isn't on the board.");

        var soldered = layout.Kind != BoardKind.Breadboard;
        var rows = placed.Package.Form == BoardForm.Dip && !soldered ? 0 : to.Row - grab.Row;
        var holes = placed.Holes.Select(h => new BoardHole(h.Board + to.Board - grab.Board, h.Row + rows, h.Column + to.Column - grab.Column)).ToList();
        if (holes.SequenceEqual(placed.Holes)) return BoardEdit.No($"{part.Name} is already there.");
        if (soldered)
            return holes.Any(h => h.Row < 1 || h.Column < 1) ? BoardEdit.No($"{part.Name} would hang off the board there.") : Try(circuit, layout, part, Pins(placed, holes));
        if (holes.Any(h => !Breadboard.Exists(h) || (placed.Package.Form != BoardForm.TwoLead && !Breadboard.IsTerminalRow(h.Row))))
            return BoardEdit.No($"{part.Name} would hang off the board there.");
        if (placed.Package.Form == BoardForm.Inline && holes.Select(h => Breadboard.IsTopHalf(h.Row)).Distinct().Count() > 1)
            return BoardEdit.No($"{part.Name} can't straddle the channel.");

        return Try(circuit, layout, part, Pins(placed, holes));
    }

    /// <summary>Moves one lead of a two-lead part, the other staying where it is.</summary>
    public static BoardEdit MoveLead(Circuit circuit, ProtoboardLayout layout, CircuitComponent part, Terminal lead, BoardHole to)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Find(part) is not { } placed) return BoardEdit.No($"{part?.Name} isn't on the board.");
        if (placed.Package.Form != BoardForm.TwoLead) return BoardEdit.No($"{part.Name}'s pins move together: move the part instead.");
        var index = placed.Package.Pins.ToList().IndexOf(lead);
        if (index < 0) return BoardEdit.No($"{lead} isn't one of {part.Name}'s leads.");
        var soldered = layout.Kind != BoardKind.Breadboard;
        if (soldered ? to.Row < 1 || to.Column < 1 : !Breadboard.Exists(to)) return BoardEdit.No("There's no hole there.");
        var holes = placed.Holes.ToList();
        if (holes[index] == to) return BoardEdit.No($"{lead} is already in {layout.HoleName(to)}.");
        holes[index] = to;
        var (ax, ay) = layout.Position(holes[0]);
        var (bx, by) = layout.Position(holes[1]);
        if (Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by))) > 15) return BoardEdit.No($"{part.Name}'s leads won't stretch that far.");
        return Try(circuit, layout, part, Pins(placed, holes));
    }

    /// <summary>Moves one end of a jumper to another free hole of the same strip, which joins exactly what it did.</summary>
    public static BoardEdit MoveWireEnd(Circuit circuit, ProtoboardLayout layout, BoardWire wire, bool fromEnd, BoardHole to)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(wire);
        if (layout.Kind != BoardKind.Breadboard)
            return BoardEdit.No("Links and back wiring on a soldered board follow the parts: move a part and they're worked out again.");
        var end = fromEnd ? wire.From : wire.To;
        if (end == to) return BoardEdit.No("The wire's end is already there.");
        if (!Breadboard.Exists(to) || Breadboard.StripOf(to) != Breadboard.StripOf(end))
            return BoardEdit.No("A wire's end moves along its own strip: anywhere else would change what it joins.");
        if (InUse(layout, to)) return BoardEdit.No($"{Breadboard.Name(to)} is taken.");

        var frozen = layout.Freeze(circuit.Board);
        var (from, other) = fromEnd ? (to, wire.To) : (wire.From, to);
        frozen.SetWire(wire.Key, from, other);
        var result = ProtoboardLayout.Arrange(circuit, frozen);
        return new BoardEdit(frozen, result, null);
    }

    /// <summary>Puts a piece of bench equipment somewhere else on the bench, in pitches.</summary>
    public static BoardEdit MoveBench(Circuit circuit, ProtoboardLayout layout, CircuitComponent part, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.FindOnBench(part) is null) return BoardEdit.No($"{part?.Name} isn't on the bench.");
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1000 || Math.Abs(y) > 1000) return BoardEdit.No("That's too far from the boards.");
        var frozen = layout.Freeze(circuit.Board);
        frozen.SetPart(part.Id, new BoardPlacement(new Dictionary<string, BoardHole>(), Math.Round(x * 2) / 2, Math.Round(y * 2) / 2));
        return new BoardEdit(frozen, ProtoboardLayout.Arrange(circuit, frozen), null);
    }

    /// <summary>Lets the layout place one part again, everything else staying where it is.</summary>
    public static BoardEdit Release(Circuit circuit, ProtoboardLayout layout, CircuitComponent part)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(part);
        var frozen = layout.Freeze(circuit.Board);
        frozen.RemovePart(part.Id);
        return new BoardEdit(frozen, ProtoboardLayout.Arrange(circuit, frozen), null);
    }

    private static bool InUse(ProtoboardLayout layout, BoardHole hole) =>
        layout.Parts.Any(p => p.Holes.Contains(hole))
        || layout.Wires.Any(w => w.From == hole || w.To == hole)
        || layout.Bench.Any(b => b.Leads.Any(l => l.To == hole))
        || layout.Parts.Where(p => p.Package.Form == BoardForm.Dip).Any(p => ProtoboardLayout.Covered(p.Holes[0], p.Holes.Count, p.Package.Span).Contains(hole))
        || layout.Parts.Where(p => p.Package.Form == BoardForm.Inline).Any(p => ProtoboardLayout.InlineCovered(p.Package, p.Holes).Contains(hole));

    private static Dictionary<string, BoardHole> Pins(BoardPart placed, IReadOnlyList<BoardHole> holes)
    {
        var pins = new Dictionary<string, BoardHole>(StringComparer.Ordinal);
        for (var i = 0; i < placed.Package.Pins.Count; i++)
            if (placed.Package.Pins[i] is { } pin) pins[pin.Id] = holes[i];
        return pins;
    }

    /// <summary>Freezes the board, puts the part where it's wanted, and lays it out: refused if anything can't keep its place.</summary>
    private static BoardEdit Try(Circuit circuit, ProtoboardLayout layout, CircuitComponent part, Dictionary<string, BoardHole> pins)
    {
        var frozen = layout.Freeze(circuit.Board);

        // The moved part goes first, so it is the one checked against everything else.
        var others = frozen.Parts.Where(p => p.Key != part.Id).ToList();
        var ordered = new BoardArrangement { Kind = frozen.Kind };
        foreach (var (key, (from, to)) in frozen.Wires) ordered.SetWire(key, from, to);
        ordered.SetPart(part.Id, new BoardPlacement(pins));
        foreach (var (id, placement) in others) ordered.SetPart(id, placement);

        var result = ProtoboardLayout.Arrange(circuit, ordered, first: part.Id);
        if (result.Refused.TryGetValue(part.Id, out var why)) return BoardEdit.No(why);
        if (result.Refused.Count > 0)
        {
            var (id, reason) = result.Refused.First();
            var name = result.Parts.Select(p => p.Part).Concat(result.Bench.Select(b => b.Part)).FirstOrDefault(c => c.Id == id)?.Name ?? "another part";
            return BoardEdit.No($"That's where {name} is: {reason}");
        }

        return new BoardEdit(ordered, result, null);
    }
}
