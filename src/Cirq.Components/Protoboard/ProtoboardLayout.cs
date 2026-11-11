using System.Globalization;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Components.Protoboard;

/// <summary>What a jumper wire carries, which sets its colour: black for ground, red for the supply.</summary>
public enum WireRole { Ground, Supply, OtherSupply, Signal }

/// <summary>A part plugged into the board.</summary>
/// <param name="Part">The component.</param>
/// <param name="Package">How it is built.</param>
/// <param name="Holes">The hole of each of the package's pins, in the package's pin order (an unconnected DIP pin has one too).</param>
/// <param name="ByHand">Put there by hand rather than by the layout.</param>
public sealed record BoardPart(CircuitComponent Part, BoardPackage Package, IReadOnlyList<BoardHole> Holes, bool ByHand)
{
    /// <summary>The hole a pin is in, or null for a pin that isn't on the package.</summary>
    public BoardHole? HoleOf(Terminal terminal)
    {
        for (var i = 0; i < Package.Pins.Count; i++)
            if (ReferenceEquals(Package.Pins[i], terminal)) return Holes[i];
        return null;
    }
}

/// <summary>A run of a stripboard's copper, from one cut (or the end) to the next, in one column.</summary>
public sealed record StripSegment(int Column, int From, int To, Net? Net);

/// <summary>A jumper wire between two holes, carrying one net.</summary>
/// <param name="Key">Which two strips it joins: the same key while the layout keeps that wire, so a moved end can be remembered.</param>
/// <param name="Route">Where it runs, in pitches, from <paramref name="From"/> to <paramref name="To"/>: kept off the parts where it can be.</param>
public sealed record BoardWire(string Key, Net Net, BoardHole From, BoardHole To, WireRole Role, int Colour, bool ByHand, IReadOnlyList<(double X, double Y)> Route);

/// <summary>A lead from a bench part's terminal to a hole.</summary>
/// <param name="Route">Where it runs, in pitches, from the equipment to its hole: kept off the parts where it can be.</param>
public sealed record BenchLead(Terminal Terminal, Net Net, BoardHole To, WireRole Role, int Colour, IReadOnlyList<(double X, double Y)> Route);

/// <summary>Equipment on the bench beside the boards, in pitches, with its leads.</summary>
public sealed record BenchPart(
    CircuitComponent Part, BoardPackage Package, double X, double Y, double Width, double Height,
    IReadOnlyList<BenchLead> Leads, bool ByHand)
{
    /// <summary>Where a lead leaves the equipment: down its right-hand edge, in the order the leads are listed.</summary>
    public (double X, double Y) LeadStart(int index) => (X + Width, Y + 3.0 + (index * 1.8));

    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

/// <summary>
/// The circuit built on solderless breadboards: which part is in which holes, the jumper wires that join the
/// strips, and the equipment on the bench with its leads.
/// <para>
/// Built from the same netlist the simulator solves, so the board is the circuit and not a second drawing of it:
/// each net is the strips its pins are in, joined by jumpers, and <see cref="Problems"/> says whether that came out
/// right — every net in one piece on the board, and no two nets sharing a strip. Parts put somewhere by hand
/// (<see cref="Circuit.Board"/>) are placed first, exactly where they were put; everything else is laid out
/// automatically around them: chips across the centre channel left to right as they are on the schematic, the
/// small parts beside the pins they join, ground on the − rails and the supply on the + rails.
/// </para>
/// </summary>
public sealed class ProtoboardLayout
{
    internal ProtoboardLayout(
        int boards, IReadOnlyList<BoardPart> parts, IReadOnlyList<BenchPart> bench, IReadOnlyList<BoardWire> wires,
        IReadOnlyDictionary<BoardStrip, Net> strips, IReadOnlyDictionary<int, Net> rails,
        IReadOnlyDictionary<Guid, string> refused, IReadOnlyList<string> problems, IReadOnlyList<string> unplaced,
        IReadOnlyDictionary<Net, string>? captions = null, int crossing = 0, double covered = 0)
    {
        WiresOverParts = crossing;
        CoveredLength = covered;
        _captions = captions ?? new Dictionary<Net, string>();
        Boards = boards;
        Parts = parts;
        Bench = bench;
        Wires = wires;
        Strips = strips;
        Rails = rails;
        Refused = refused;
        Problems = problems;
        Unplaced = unplaced;
    }

    private readonly IReadOnlyDictionary<Net, string> _captions;

    /// <summary>What it is built on: breadboards, or a stripboard or perfboard laid out by <see cref="SolderedLayout"/>.</summary>
    public BoardKind Kind { get; init; } = BoardKind.Breadboard;

    /// <summary>A stripboard's or perfboard's size in holes (columns across, rows down); zero for breadboards.</summary>
    public int Columns { get; init; }

    public int Rows { get; init; }

    /// <summary>Where a stripboard's strips are cut, a hole each: the copper drilled away so two nets part.</summary>
    public IReadOnlyList<BoardHole> Cuts { get; init; } = [];

    /// <summary>A stripboard's strips as the cuts leave them: each run of copper, and the net it carries if any.</summary>
    public IReadOnlyList<StripSegment> Segments { get; init; } = [];

    /// <summary>The net at each hole in use, on a stripboard or perfboard (a breadboard's come from its strips).</summary>
    public IReadOnlyDictionary<BoardHole, Net> HoleNets { get; init; } = new Dictionary<BoardHole, Net>();

    /// <summary>Where a hole's centre is, in pitches, on whatever this is built on.</summary>
    public (double X, double Y) Position(BoardHole hole) =>
        Kind == BoardKind.Breadboard ? Breadboard.Position(hole) : SolderedLayout.Position(hole);

    /// <summary>The hole nearest a point in pitches, within <paramref name="within"/>.</summary>
    public BoardHole? Nearest(double x, double y, double within = 0.75)
    {
        if (Kind == BoardKind.Breadboard) return Breadboard.Nearest(x, y, Boards, within);
        var hole = new BoardHole(1, (int)Math.Round(y - 1.5), (int)Math.Round(x - 2.5));
        if (hole.Row < 1 || hole.Row > Rows || hole.Column < 1 || hole.Column > Columns) return null;
        var (hx, hy) = SolderedLayout.Position(hole);
        return Math.Sqrt(((hx - x) * (hx - x)) + ((hy - y) * (hy - y))) <= within ? hole : null;
    }

    /// <summary>A hole's name: as a breadboard prints it (<c>e12</c>), or a stripboard's strip letter and hole number (<c>C12</c>).</summary>
    public string HoleName(BoardHole hole) => Kind == BoardKind.Breadboard ? Breadboard.Name(hole) : SolderedLayout.Name(hole);

    /// <summary>Whether a part's body stands on the side of its pins towards the top of the board.</summary>
    public bool Up(BoardHole hole) => Kind != BoardKind.Breadboard || Breadboard.IsTopHalf(hole.Row);

    /// <summary>What a rail is labelled with: the supply's voltage when a source sets it (+5V), else the net's name.</summary>
    public string Caption(Net net) => net.Label is { Length: > 0 } label ? label : _captions.TryGetValue(net, out var caption) ? caption : NetName(net);

    /// <summary>How many boards it takes.</summary>
    public int Boards { get; }

    public IReadOnlyList<BoardPart> Parts { get; }

    public IReadOnlyList<BenchPart> Bench { get; }

    public IReadOnlyList<BoardWire> Wires { get; }

    /// <summary>The net each strip in use carries.</summary>
    public IReadOnlyDictionary<BoardStrip, Net> Strips { get; }

    /// <summary>The net each rail row carries, the same on every board.</summary>
    public IReadOnlyDictionary<int, Net> Rails { get; }

    /// <summary>Parts whose hand-made place could not be kept (the circuit changed under them), and why.</summary>
    public IReadOnlyDictionary<Guid, string> Refused { get; }

    /// <summary>Where the board doesn't match the schematic. Empty when it does.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Parts there was no room for.</summary>
    public IReadOnlyList<string> Unplaced { get; }

    /// <summary>How many jumpers and leads still pass over a part somewhere: the router keeps them off where it can.</summary>
    public int WiresOverParts { get; }

    /// <summary>How much wire, in pitches, lies over parts, all the wires and leads together.</summary>
    public double CoveredLength { get; }

    /// <summary>The board is the schematic: every net joined up, no two nets touching.</summary>
    public bool Matches => Problems.Count == 0;

    /// <summary>Lays out <paramref name="circuit"/>, keeping what was put somewhere by hand in it.</summary>
    public static ProtoboardLayout Arrange(Circuit circuit) => Arrange(circuit, circuit?.Board ?? new BoardArrangement());

    /// <summary>Lays out <paramref name="circuit"/> with <paramref name="hand"/> as what was put somewhere by hand.</summary>
    /// <param name="circuit">The circuit.</param>
    /// <param name="hand">What was put where by hand.</param>
    /// <param name="first">A part to place before the others put somewhere by hand: the one being moved, so it is the one checked.</param>
    public static ProtoboardLayout Arrange(Circuit circuit, BoardArrangement hand, Guid? first = null)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(hand);
        return hand.Kind == BoardKind.Breadboard ? new Arranger(circuit, hand, first).Run() : SolderedLayout.Arrange(circuit, hand.Kind);
    }

    /// <summary>The net in a hole, when its strip carries one.</summary>
    public Net? NetAt(BoardHole hole)
    {
        if (Kind == BoardKind.Breadboard) return Strips.GetValueOrDefault(Breadboard.StripOf(hole));
        if (HoleNets.TryGetValue(hole, out var net)) return net;
        return Kind == BoardKind.Stripboard
            ? Segments.FirstOrDefault(s => s.Column == hole.Column && hole.Row >= s.From && hole.Row <= s.To)?.Net
            : null;
    }

    /// <summary>The run of copper a stripboard hole is on, or null on any other board.</summary>
    public StripSegment? SegmentAt(BoardHole hole) =>
        Kind == BoardKind.Stripboard ? Segments.FirstOrDefault(s => s.Column == hole.Column && hole.Row >= s.From && hole.Row <= s.To) : null;

    /// <summary>The part with a pin in a hole.</summary>
    public BoardPart? PartAt(BoardHole hole) => Parts.FirstOrDefault(p => p.Holes.Contains(hole));

    public BoardPart? Find(CircuitComponent part) => Parts.FirstOrDefault(p => ReferenceEquals(p.Part, part));

    public BenchPart? FindOnBench(CircuitComponent part) => Bench.FirstOrDefault(p => ReferenceEquals(p.Part, part));

    /// <summary>
    /// Everything where it is now, as a hand-made arrangement: what moving one part by hand starts from, so the rest
    /// of the board stays where it was rather than being laid out again around it.
    /// </summary>
    public BoardArrangement Freeze(BoardArrangement? wires = null)
    {
        var frozen = new BoardArrangement();
        foreach (var part in Parts)
        {
            var pins = new Dictionary<string, BoardHole>(StringComparer.Ordinal);
            for (var i = 0; i < part.Package.Pins.Count; i++)
                if (part.Package.Pins[i] is { } pin) pins[pin.Id] = part.Holes[i];
            frozen.SetPart(part.Part.Id, new BoardPlacement(pins));
        }

        foreach (var item in Bench)
            frozen.SetPart(item.Part.Id, new BoardPlacement(new Dictionary<string, BoardHole>(), item.X, item.Y));

        foreach (var (key, (from, to)) in wires?.Wires ?? new Dictionary<string, (BoardHole, BoardHole)>())
            frozen.SetWire(key, from, to);

        return frozen;
    }

    /// <summary>What the board extends over, in pitches: the boards and the bench.</summary>
    public (double Left, double Top, double Right, double Bottom) Extent
    {
        get
        {
            var left = 0.0;
            var top = 0.0;
            var right = Kind == BoardKind.Breadboard ? Breadboard.Width : SolderedLayout.Width(Columns);
            var bottom = Kind == BoardKind.Breadboard ? Breadboard.Top(Boards) + Breadboard.Height : SolderedLayout.Height(Rows);
            foreach (var item in Bench)
            {
                left = Math.Min(left, item.X);
                top = Math.Min(top, item.Y);
                right = Math.Max(right, item.X + item.Width);
                bottom = Math.Max(bottom, item.Y + item.Height);
            }

            return (left - 1, top - 1, right + 1, bottom + 1);
        }
    }

    /// <summary>A net's name for a person: its label, GND, or the pins on it when it has no name.</summary>
    public static string NetName(Net net)
    {
        ArgumentNullException.ThrowIfNull(net);
        if (net.Label is { Length: > 0 } label) return label;
        if (net.IsGround) return "GND";
        var pins = net.Terminals.Where(t => t.Owner is { } o && BoardPackages.IsBuilt(o)).Take(2).Select(t => t.ToString()).ToList();
        return pins.Count > 0 ? string.Join(", ", pins) : net.Name;
    }

    /// <summary>The board in words: each part's holes, the wires, and whether it matches the schematic.</summary>
    public string Describe()
    {
        var text = new System.Text.StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{Boards} board{(Boards == 1 ? "" : "s")}, {Parts.Count} part{(Parts.Count == 1 ? "" : "s")} on the board, ");
        text.Append(CultureInfo.InvariantCulture, $"{Bench.Count} on the bench, {Wires.Count} jumper wire{(Wires.Count == 1 ? "" : "s")}.");
        if (Kind == BoardKind.Breadboard)
            foreach (var (row, net) in Rails.OrderBy(r => r.Key))
                text.Append(CultureInfo.InvariantCulture, $" The {Breadboard.RailName(row)} rails carry {NetName(net)}.");
        else
        {
            text.Append(CultureInfo.InvariantCulture, $" A {Kind.ToString().ToLowerInvariant()} {Columns} holes across and {Rows} down");
            text.Append(Kind == BoardKind.Stripboard ? $", strips A to {SolderedLayout.Letters(Columns)} running down, {Cuts.Count} cut{(Cuts.Count == 1 ? "" : "s")}." : ".");
            foreach (var (column, net) in Rails.OrderBy(r => r.Key))
                text.Append(CultureInfo.InvariantCulture, $" Strip {SolderedLayout.Letters(column)} carries {Caption(net)}.");
        }
        text.AppendLine();
        foreach (var part in Parts)
        {
            var pins = part.Package.Pins.Select((t, i) => t is null ? null : $"{t.Name}={HoleName(part.Holes[i])}").Where(s => s is not null);
            text.AppendLine(CultureInfo.InvariantCulture, $"{part.Part.Name} ({part.Package.Label}): {string.Join(" ", pins)}{(part.ByHand ? " (placed by hand)" : "")}");
        }

        foreach (var item in Bench)
            text.AppendLine(CultureInfo.InvariantCulture, $"{item.Part.Name} on the bench: {string.Join(" ", item.Leads.Select(l => $"{l.Terminal.Name}→{HoleName(l.To)}"))}");
        foreach (var name in Unplaced) text.AppendLine(CultureInfo.InvariantCulture, $"No room for {name}.");
        text.Append(Matches ? "The board matches the schematic." : "It doesn't match the schematic: " + string.Join(" ", Problems));
        return text.ToString();
    }

    /// <summary>Where a DIP's pins go, pin 1 at <paramref name="first"/> (bottom left, notch to the left).</summary>
    public static IReadOnlyList<BoardHole> DipHoles(BoardHole first, int pins, int span)
    {
        var (top, bottom) = DipRows(span);
        var half = pins / 2;
        var column = first.Column;
        return [.. Enumerable.Range(1, pins).Select(n => n <= half
            ? new BoardHole(first.Board, bottom, column + n - 1)
            : new BoardHole(first.Board, top, column + pins - n))];
    }

    /// <summary>The rows a DIP of this width straddles the channel in: e and f, d and h, or b and i.</summary>
    public static (int Top, int Bottom) DipRows(int span) => span switch
    {
        >= 9 => (Breadboard.A + 1, Breadboard.F + 3),
        >= 6 => (Breadboard.A + 3, Breadboard.F + 2),
        _ => (Breadboard.E, Breadboard.F),
    };

    /// <summary>The holes under a DIP's body, between its pins and the channel.</summary>
    public static IEnumerable<BoardHole> Covered(BoardHole first, int pins, int span)
    {
        var (top, bottom) = DipRows(span);
        for (var c = 0; c < pins / 2; c++)
        {
            for (var row = top + 1; row <= Breadboard.E; row++) yield return new BoardHole(first.Board, row, first.Column + c);
            for (var row = Breadboard.F; row < bottom; row++) yield return new BoardHole(first.Board, row, first.Column + c);
        }
    }

    /// <summary>
    /// How many rows an inline part's body covers on the side away from the channel: a module's board two, a TO-220's
    /// tab, a trimmer or a slide switch one, a TO-92 none (it stands on its legs).
    /// </summary>
    public static int BodyRows(BoardLook look) => look switch
    {
        BoardLook.Header => 2,
        BoardLook.To220 or BoardLook.Trimmer or BoardLook.SlideSwitch => 1,
        _ => 0,
    };

    /// <summary>The holes under an inline part's body.</summary>
    public static IEnumerable<BoardHole> InlineCovered(BoardPackage package, IReadOnlyList<BoardHole> holes)
    {
        var rows = BodyRows(package.Look);
        foreach (var hole in holes)
        {
            var top = Breadboard.IsTopHalf(hole.Row);
            for (var k = 1; k <= rows; k++)
            {
                var row = top ? hole.Row - k : hole.Row + k;
                if (row < Breadboard.A || row > Breadboard.J) break;
                yield return hole with { Row = row };
            }
        }
    }

    /// <summary>What a two-lead part's body covers seen from above, as discs in pitches: a row of them along a resistor, one for an LED.</summary>
    internal static List<(double X, double Y, double R)> Discs(CircuitComponent part, BoardPackage package, BoardHole a, BoardHole b) =>
        Discs(part, package, Breadboard.Position(a), Breadboard.Position(b));

    internal static List<(double X, double Y, double R)> Discs(CircuitComponent part, BoardPackage package, (double X, double Y) pa, (double X, double Y) pb)
    {
        var (ax, ay) = pa;
        var (bx, by) = pb;
        var span = Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
        var (mx, my) = ((ax + bx) / 2, (ay + by) / 2);
        if (!BoardPackages.IsAxial(package.Look)) return [(mx, my, BoardPackages.BodyRadius(part, package.Look))];
        if (span < 2.6) return [(ax, ay, 0.5)];

        var length = Math.Min(span - 0.9, package.Look == BoardLook.Crystal ? 2.0 : 3.2);
        var (ux, uy) = ((bx - ax) / span, (by - ay) / span);
        var count = Math.Max(2, (int)Math.Ceiling(length / 0.7));
        return [.. Enumerable.Range(0, count).Select(i =>
        {
            var t = (-length / 2) + 0.4 + ((length - 0.8) * i / (count - 1));
            return (mx + (ux * t), my + (uy * t), 0.42);
        })];
    }

    /// <summary>A DIP's body, in pitches.</summary>
    internal static (double L, double T, double R, double B) DipBox(IReadOnlyList<BoardHole> holes) =>
        DipBox([.. holes.Select(Breadboard.Position)]);

    internal static (double L, double T, double R, double B) DipBox(IReadOnlyList<(double X, double Y)> points)
    {
        return (points.Min(p => p.X) - 0.45, points.Min(p => p.Y) + 0.25, points.Max(p => p.X) + 0.45, points.Max(p => p.Y) - 0.25);
    }

    /// <summary>An inline part's body, in pitches: mostly on the side away from the channel.</summary>
    internal static (double L, double T, double R, double B) InlineBox(BoardLook look, IReadOnlyList<BoardHole> holes) =>
        InlineBox(look, [.. holes.Select(Breadboard.Position)], Breadboard.IsTopHalf(holes[0].Row));

    internal static (double L, double T, double R, double B) InlineBox(BoardLook look, IReadOnlyList<(double X, double Y)> points, bool up)
    {
        var y = points[0].Y;
        var outward = look switch
        {
            BoardLook.Header => 2.6,
            BoardLook.To220 => 2.0,
            BoardLook.Trimmer => 2.4,
            BoardLook.SlideSwitch => 1.6,
            _ => 1.3,
        };
        var (left, right) = (points.Min(p => p.X) - 0.6, points.Max(p => p.X) + 0.6);
        return up ? (left, y - outward, right, y + 0.4) : (left, y - 0.4, right, y + outward);
    }

    /// <summary>The key a jumper between two strips is remembered by.</summary>
    internal static string WireKey(BoardStrip a, BoardStrip b)
    {
        static string Of(BoardStrip s) => $"{s.Board}:{s.Row}:{s.Column}";
        var (x, y) = (Of(a), Of(b));
        return string.CompareOrdinal(x, y) <= 0 ? $"{x}-{y}" : $"{y}-{x}";
    }

    // ───────────────────────────── the layout ─────────────────────────────

    private sealed class Arranger(Circuit circuit, BoardArrangement hand, Guid? first)
    {
        private const int MaxBoards = 16;

        /// <summary>Holes a terminal strip keeps free for jumper wires, so every net can always be joined up.</summary>
        private const int Reserve = 2;

        private readonly HashSet<BoardHole> _used = [];
        private readonly Dictionary<BoardStrip, Net> _stripNet = [];
        private readonly HashSet<BoardStrip> _reserved = [];
        private readonly Dictionary<Net, List<BoardStrip>> _netStrips = [];
        private readonly Dictionary<int, Net> _rails = [];
        private readonly List<BoardPart> _parts = [];
        private readonly List<BenchPart> _bench = [];
        private readonly Dictionary<Guid, string> _refused = [];
        private readonly List<string> _unplaced = [];
        private readonly List<string> _notes = [];
        private readonly Dictionary<Net, int> _colours = [];
        private readonly Dictionary<Net, string> _captions = [];
        private WireRouter _router = null!;
        private readonly List<(double X, double Y, double R)> _discs = [];
        private readonly List<(double L, double T, double R, double B)> _boxes = [];
        private Netlist? _netlist;
        private int _boards = 1;
        private Net? _ground, _supply, _supply2;

        public ProtoboardLayout Run()
        {
            List<(CircuitComponent Part, BoardPackage Package)> built;
            try
            {
                _netlist = circuit.BuildNetlist();
                built = [.. Flattening.Flatten(circuit.Components).Select(c => (c, BoardPackages.For(c))).Where(p => p.Item2.Form != BoardForm.None)];
            }
            catch (CircuitTopologyException e)
            {
                return new ProtoboardLayout(1, [], [], [], new Dictionary<BoardStrip, Net>(), new Dictionary<int, Net>(),
                    new Dictionary<Guid, string>(), [$"The circuit can't be built: {e.Message}"], []);
            }

            ChooseRails(built);

            var boardParts = built.Where(p => p.Package.Form != BoardForm.Bench).OrderBy(p => p.Part.Id == first ? 0 : 1).ToList();
            var benchParts = built.Where(p => p.Package.Form == BoardForm.Bench).ToList();

            // What was put somewhere by hand goes there first, before anything automatic can take the holes.
            var auto = new List<(CircuitComponent Part, BoardPackage Package)>();
            foreach (var (part, package) in boardParts)
            {
                if (hand.Parts.TryGetValue(part.Id, out var placement) && placement.Pins.Count > 0)
                {
                    if (PlaceByHand(part, package, placement) is { } why) _refused[part.Id] = why;
                    else continue;
                }

                auto.Add((part, package));
            }

            // Chips across the channel, in the order they read on the schematic.
            foreach (var (part, package) in auto.Where(p => p.Package.Form == BoardForm.Dip).OrderBy(p => p.Part.X).ThenBy(p => p.Part.Y))
                if (!PlaceDip(part, package)) _unplaced.Add(part.Name);

            // Then the small parts, those joined to something already placed first, so each lands beside it.
            var small = auto.Where(p => p.Package.Form is BoardForm.TwoLead or BoardForm.Inline).ToList();
            while (small.Count > 0)
            {
                var next = small.OrderByDescending(p => p.Package.Pins.Count(t => t is not null && _netStrips.ContainsKey(NetOf(t))))
                    .ThenBy(p => p.Part.X).ThenBy(p => p.Part.Y).First();
                small.Remove(next);
                var placed = next.Package.Form == BoardForm.TwoLead ? PlaceTwoLead(next.Part, next.Package) : PlaceInline(next.Part, next.Package);
                if (!placed) _unplaced.Add(next.Part.Name);
            }

            // Every part is placed now, so the wires can be routed round them.
            _router = new WireRouter(_discs, _boxes, _boards);
            PlaceBench(benchParts);
            var wires = Wire();
            var problems = Check(built, wires);

            var routes = wires.Select(w => w.Route).Concat(_bench.SelectMany(b => b.Leads).Select(l => l.Route)).Select(_router.Covered).ToList();
            return new ProtoboardLayout(_boards, _parts, _bench, wires, new Dictionary<BoardStrip, Net>(_stripNet),
                new Dictionary<int, Net>(_rails), _refused, problems, _unplaced, _captions, routes.Count(c => c > 0), routes.Sum());
        }

        private Net NetOf(Terminal terminal) => _netlist!.NetOf(terminal);

        // ---- rails ----

        /// <summary>
        /// Ground on the − rails, and on the + rails the net a supply holds above ground (the one with the most pins,
        /// when there are several); a second supply, a −12 V say, on the bottom + rail.
        /// </summary>
        private void ChooseRails(List<(CircuitComponent Part, BoardPackage Package)> built)
        {
            var builtTerminals = built.SelectMany(p => p.Part.Terminals).ToHashSet();
            _ground = _netlist!.GroundNet is { } g && g.Terminals.Any(builtTerminals.Contains) ? g : null;

            var scores = new Dictionary<Net, double>();
            void Add(Net net, double score)
            {
                if (net.IsGround) return;
                scores[net] = scores.GetValueOrDefault(net) + score;
            }

            foreach (var (part, _) in built)
            {
                if (part is DcVoltageSource or Battery && part.Terminals.Count == 2 && _ground is not null)
                {
                    var (a, b) = (NetOf(part.Terminals[0]), NetOf(part.Terminals[1]));
                    var volts = part.ValueLabel.Split(' ')[0];
                    if (ReferenceEquals(b, _ground))
                    {
                        Add(a, 10);
                        _captions.TryAdd(a, "+" + volts);
                    }
                    else if (ReferenceEquals(a, _ground))
                    {
                        Add(b, 10);
                        _captions.TryAdd(b, "−" + volts);
                    }
                }

                foreach (var t in part.Terminals)
                {
                    var name = t.Name.ToUpperInvariant();
                    if (name is "VCC" or "VDD" or "V+" or "+VS" or "5V" or "VS") Add(NetOf(t), 3);
                    else if (name is "3V3") Add(NetOf(t), 2);
                }
            }

            foreach (var net in scores.Keys.ToList()) scores[net] += 0.1 * net.Terminals.Count(builtTerminals.Contains);
            var ranked = scores.Where(s => s.Value >= 3).OrderByDescending(s => s.Value).ThenBy(s => s.Key.Index).Select(s => s.Key).ToList();
            _supply = ranked.FirstOrDefault();
            _supply2 = ranked.Skip(1).FirstOrDefault();

            if (_ground is not null)
            {
                _rails[Breadboard.TopMinus] = _ground;
                _rails[Breadboard.BottomMinus] = _ground;
            }

            if (_supply is not null)
            {
                _rails[Breadboard.TopPlus] = _supply;
                _rails[Breadboard.BottomPlus] = _supply2 ?? _supply;
            }

            AddBoardRails(1);
        }

        private void AddBoardRails(int board)
        {
            foreach (var (row, net) in _rails) _stripNet[new BoardStrip(board, row, 0)] = net;
        }

        private bool AddBoard()
        {
            if (_boards >= MaxBoards) return false;
            _boards++;
            AddBoardRails(_boards);
            return true;
        }

        private void EnsureBoards(int count)
        {
            while (_boards < Math.Min(count, MaxBoards)) AddBoard();
        }

        // ---- holes and strips ----

        private bool Free(BoardHole hole) => Breadboard.Exists(hole) && hole.Board <= _boards && !_used.Contains(hole);

        private int FreeCount(BoardStrip strip) => Breadboard.HolesOf(strip).Count(Free);

        /// <summary>How many more pins a strip can take and still keep its holes for wires.</summary>
        private int Spare(BoardStrip strip) => strip.IsRail ? FreeCount(strip) : FreeCount(strip) - Reserve;

        /// <summary>Whether <paramref name="net"/> may go in <paramref name="strip"/>: it's empty, or already that net.</summary>
        private bool Allows(BoardStrip strip, Net net) =>
            !_reserved.Contains(strip) && (!_stripNet.TryGetValue(strip, out var on) || ReferenceEquals(on, net));

        private bool IsEmpty(BoardStrip strip) => !strip.IsRail && !_reserved.Contains(strip) && !_stripNet.ContainsKey(strip);

        private void Take(BoardHole hole, Net? net)
        {
            _used.Add(hole);
            var strip = Breadboard.StripOf(hole);
            if (net is null)
            {
                if (!_stripNet.ContainsKey(strip)) _reserved.Add(strip);
                return;
            }

            _stripNet[strip] = net;
            if (!_netStrips.TryGetValue(net, out var list)) _netStrips[net] = list = [];
            if (!list.Contains(strip)) list.Add(strip);
        }

        private static (double X, double Y) Centre(BoardStrip strip)
        {
            if (strip.IsRail) return (Breadboard.Width / 2, Breadboard.Top(strip.Board) + Breadboard.RowPosition(strip.Row));
            var (x, y) = Breadboard.Position(new BoardHole(strip.Board, strip.Row + 2, strip.Column));
            return (x, y);
        }

        private static double Distance((double X, double Y) a, (double X, double Y) b) =>
            Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

        /// <summary>Where a part joining these nets belongs: among the strips they're already on, or the next empty space.</summary>
        private (double X, double Y) Focus(IEnumerable<Net> nets)
        {
            var points = nets.SelectMany(n => _netStrips.GetValueOrDefault(n) ?? []).Where(s => !s.IsRail).Select(Centre).ToList();
            if (points.Count > 0) return (points.Average(p => p.X), points.Average(p => p.Y));

            // A little way in from the left end, which leaves the first columns for the bench's leads.
            for (var board = 1; board <= _boards; board++)
                for (var column = 8; column <= Breadboard.Columns; column++)
                    foreach (var row in new[] { Breadboard.A, Breadboard.F })
                        if (IsEmpty(new BoardStrip(board, row, column))) return Centre(new BoardStrip(board, row, column));

            return Centre(new BoardStrip(_boards, Breadboard.A, 1));
        }

        private readonly record struct Spot(BoardHole Hole, double Cost, bool Fresh);

        /// <summary>
        /// Holes a pin on <paramref name="net"/> could go in near <paramref name="focus"/>: in a strip already carrying it
        /// (free), on its rail (nearly free), or in an empty strip (dearer, because it needs a jumper to the rest).
        /// </summary>
        private List<Spot> Spots(Net net, (double X, double Y) focus)
        {
            var spots = new List<Spot>();
            foreach (var strip in _netStrips.GetValueOrDefault(net) ?? [])
            {
                if (strip.IsRail || Spare(strip) < 1) continue;
                spots.AddRange(Breadboard.HolesOf(strip).Where(Free).Select(h => new Spot(h, 0, false)));
            }

            foreach (var (row, railNet) in _rails)
            {
                if (!ReferenceEquals(railNet, net)) continue;
                for (var board = 1; board <= _boards; board++)
                    spots.AddRange(Breadboard.HolesOf(new BoardStrip(board, row, 0)).Where(Free)
                        .OrderBy(h => Distance(Breadboard.Position(h), focus)).Take(8).Select(h => new Spot(h, 0.2, false)));
            }

            var others = (_netStrips.GetValueOrDefault(net) ?? []).Select(Centre).ToList();
            foreach (var strip in EmptyStrips().OrderBy(s => Distance(Centre(s), focus)).Take(10))
            {
                var reach = others.Count == 0 ? 0 : others.Min(o => Distance(o, Centre(strip)));
                spots.AddRange(Breadboard.HolesOf(strip).Select(h => new Spot(h, 3 + (0.25 * reach), true)));
            }

            return spots;
        }

        private IEnumerable<BoardStrip> EmptyStrips()
        {
            for (var board = 1; board <= _boards; board++)
                for (var column = 1; column <= Breadboard.Columns; column++)
                    foreach (var row in new[] { Breadboard.A, Breadboard.F })
                    {
                        var strip = new BoardStrip(board, row, column);
                        if (IsEmpty(strip)) yield return strip;
                    }
        }

        // ---- by hand ----

        private string? PlaceByHand(CircuitComponent part, BoardPackage package, BoardPlacement placement)
        {
            IReadOnlyList<BoardHole> holes;
            List<BoardHole> covered = [];
            if (package.Form == BoardForm.TwoLead)
            {
                var list = new List<BoardHole>();
                foreach (var pin in package.Pins)
                {
                    if (pin is null || !placement.Pins.TryGetValue(pin.Id, out var hole)) return $"{part.Name}'s place on the board is missing a pin; it was laid out afresh.";
                    list.Add(hole);
                }

                holes = list;
            }
            else
            {
                // The package keeps its shape: found from any one pin, so a part that has gained or lost pins since
                // still lands where it was.
                var index = Enumerable.Range(0, package.Pins.Count).FirstOrDefault(i => package.Pins[i] is { } p && placement.Pins.ContainsKey(p.Id), -1);
                if (index < 0) return $"{part.Name}'s place on the board names none of its pins; it was laid out afresh.";
                var at = placement.Pins[package.Pins[index]!.Id];
                if (package.Form == BoardForm.Dip)
                {
                    var shape = DipHoles(new BoardHole(1, 0, 1), package.Pins.Count, package.Span);
                    var first = new BoardHole(at.Board, shape[0].Row, at.Column - (shape[index].Column - shape[0].Column));
                    holes = DipHoles(first, package.Pins.Count, package.Span);
                    covered.AddRange(Covered(first, package.Pins.Count, package.Span));
                }
                else
                {
                    holes = [.. Enumerable.Range(0, package.Pins.Count).Select(i => at with { Column = at.Column + i - index })];
                    covered.AddRange(InlineCovered(package, holes));
                }
            }

            EnsureBoards(holes.Max(h => h.Board));
            var why = Fits(part, package, holes, covered);
            if (why is not null) return why;
            Commit(part, package, holes, covered, byHand: true);
            return null;
        }

        /// <summary>Why a part can't have these holes, or null when it can.</summary>
        private string? Fits(CircuitComponent part, BoardPackage package, IReadOnlyList<BoardHole> holes, IEnumerable<BoardHole> covered)
        {
            for (var i = 0; i < holes.Count; i++)
            {
                var hole = holes[i];
                if (!Breadboard.Exists(hole) || hole.Board > MaxBoards) return $"{part.Name} would hang off the board.";
                if (!Free(hole)) return $"{Breadboard.Name(hole)} is taken.";
                if (package.Pins[i] is not { } pin) continue;
                var net = NetOf(pin);
                var strip = Breadboard.StripOf(hole);
                if (_reserved.Contains(strip)) return $"{strip.Describe(_boards)} has an unconnected chip pin in it.";
                if (_stripNet.TryGetValue(strip, out var on) && !ReferenceEquals(on, net))
                    return $"{strip.Describe(_boards)} carries {NetName(on)}, and {part.Name}.{pin.Name} is on {NetName(net)}: that would join them.";
                for (var j = 0; j < i; j++)
                    if (package.Pins[j] is { } other && Breadboard.StripOf(holes[j]) == strip && !ReferenceEquals(NetOf(other), net))
                        return $"{part.Name}.{pin.Name} and {part.Name}.{other.Name} would be in the same strip, which would short them.";
            }

            foreach (var hole in covered)
                if (!Free(hole)) return $"{part.Name}'s body would sit on {Breadboard.Name(hole)}, which is in use.";
            return null;
        }

        private void Commit(CircuitComponent part, BoardPackage package, IReadOnlyList<BoardHole> holes, IEnumerable<BoardHole> covered, bool byHand)
        {
            for (var i = 0; i < holes.Count; i++) Take(holes[i], package.Pins[i] is { } pin ? NetOf(pin) : null);
            foreach (var hole in covered) _used.Add(hole);
            _parts.Add(new BoardPart(part, package, holes, byHand));
            switch (package.Form)
            {
                case BoardForm.TwoLead: _discs.AddRange(Discs(part, package, holes[0], holes[1])); break;
                case BoardForm.Dip: _boxes.Add(DipBox(holes)); break;
                case BoardForm.Inline: _boxes.Add(InlineBox(package.Look, holes)); break;
            }
        }

        private bool Clashes(IEnumerable<(double X, double Y, double R)> discs) =>
            discs.Any(d => _discs.Any(e => Math.Sqrt(((d.X - e.X) * (d.X - e.X)) + ((d.Y - e.Y) * (d.Y - e.Y))) < d.R + e.R + 0.15)
                           || _boxes.Any(b => Touches(d, b)));

        private bool Clashes((double L, double T, double R, double B) box) =>
            _discs.Any(d => Touches(d, box)) || _boxes.Any(b => box.L < b.R + 0.15 && b.L < box.R + 0.15 && box.T < b.B + 0.15 && b.T < box.B + 0.15);

        /// <summary>How close a body would be to the others: nothing when there's room, more the tighter it gets.</summary>
        private double Crowding(List<(double X, double Y, double R)> discs)
        {
            var cost = 0.0;
            foreach (var d in discs)
                foreach (var e in _discs)
                {
                    var gap = Math.Sqrt(((d.X - e.X) * (d.X - e.X)) + ((d.Y - e.Y) * (d.Y - e.Y))) - d.R - e.R;
                    if (gap < 1.2) cost += (1.2 - gap) * 0.8;
                }

            return cost / Math.Max(1, discs.Count);
        }

        private static bool Touches((double X, double Y, double R) d, (double L, double T, double R, double B) b)
        {
            var x = Math.Clamp(d.X, b.L, b.R);
            var y = Math.Clamp(d.Y, b.T, b.B);
            return ((d.X - x) * (d.X - x)) + ((d.Y - y) * (d.Y - y)) < (d.R + 0.1) * (d.R + 0.1);
        }

        // ---- automatically ----

        /// <summary>Across the channel at the first columns free on any board, leaving a few after the last chip.</summary>
        private bool PlaceDip(CircuitComponent part, BoardPackage package)
        {
            var pins = package.Pins.Count;
            var half = pins / 2;
            if (half > Breadboard.Columns - 2) return false;

            for (var attempt = 0; attempt < 2; attempt++)
            {
                for (var board = 1; board <= _boards; board++)
                {
                    for (var column = 6; column + half - 1 <= Breadboard.Columns - 1; column++)
                    {
                        var first = new BoardHole(board, DipRows(package.Span).Bottom, column);
                        var holes = DipHoles(first, pins, package.Span);
                        var covered = Covered(first, pins, package.Span).ToList();

                        // A clear column either side, so a chip's neighbours aren't in its pins' strips.
                        var clear = Enumerable.Range(column - 2, half + 4).All(c => c < 1 || c > Breadboard.Columns
                            || (IsEmpty(new BoardStrip(board, Breadboard.A, c)) && IsEmpty(new BoardStrip(board, Breadboard.F, c))));
                        if (!clear || Clashes(DipBox(holes))) continue;
                        if (Fits(part, package, holes, covered) is not null) continue;
                        Commit(part, package, holes, covered, byHand: false);
                        return true;
                    }
                }

                if (!AddBoard()) return false;
            }

            return false;
        }

        private bool PlaceTwoLead(CircuitComponent part, BoardPackage package)
        {
            var (t1, t2) = (package.Pins[0]!, package.Pins[1]!);
            var (n1, n2) = (NetOf(t1), NetOf(t2));
            var (min, usual, max) = package.Reach;

            for (var attempt = 0; attempt < 3; attempt++)
            {
                var focus = Focus([n1, n2]);
                var a = Spots(n1, focus);
                var b = Spots(n2, focus);
                var stretch = attempt == 0 ? max : 14;
                (BoardHole, BoardHole)? best = null;
                var lowest = double.MaxValue;

                foreach (var p in a)
                {
                    foreach (var q in b)
                    {
                        if (p.Hole == q.Hole) continue;
                        var (sp, sq) = (Breadboard.StripOf(p.Hole), Breadboard.StripOf(q.Hole));
                        if (sp == sq && !ReferenceEquals(n1, n2)) continue;
                        if (sp == sq && !sp.IsRail && !p.Fresh && Spare(sp) < 2) continue;
                        var span = Breadboard.Distance(p.Hole, q.Hole);
                        if (span < min - 0.01 || span > stretch + 0.01) continue;

                        var (px, py) = Breadboard.Position(p.Hole);
                        var (qx, qy) = Breadboard.Position(q.Hole);
                        var discs = Discs(part, package, p.Hole, q.Hole);
                        var crowded = Clashes(discs);
                        if (crowded && attempt == 0) continue;
                        var diagonal = Math.Abs(px - qx) > 0.01 && Math.Abs(py - qy) > 0.01;
                        var cost = p.Cost + q.Cost + (0.4 * Math.Abs(span - usual)) + (span > max ? span - max : 0)
                            + (crowded ? 6 : 0) + (diagonal ? 1.2 : 0) + Crowding(discs)
                            + (0.03 * Distance(((px + qx) / 2, (py + qy) / 2), focus))
                            // Across the channel is how a part joins the two halves, not lying in it.
                            + (Breadboard.IsTopHalf(p.Hole.Row) != Breadboard.IsTopHalf(q.Hole.Row) && !sp.IsRail && !sq.IsRail ? 0.5 : 0);
                        if (cost < lowest)
                        {
                            lowest = cost;
                            best = (p.Hole, q.Hole);
                        }
                    }
                }

                if (best is { } found)
                {
                    Commit(part, package, [found.Item1, found.Item2], [], byHand: false);
                    return true;
                }

                if (attempt == 1 && !AddBoard()) return false;
            }

            return false;
        }

        /// <summary>A row of neighbouring holes, each pin's strip either its net's already or an empty one.</summary>
        private bool PlaceInline(CircuitComponent part, BoardPackage package)
        {
            var count = package.Pins.Count;
            var nets = package.Pins.Select(p => NetOf(p!)).ToList();

            for (var attempt = 0; attempt < 2; attempt++)
            {
                var focus = Focus(nets);
                List<BoardHole>? best = null;
                var lowest = double.MaxValue;

                for (var board = 1; board <= _boards; board++)
                {
                    for (var row = Breadboard.A; row <= Breadboard.J; row++)
                    {
                        for (var column = 1; column + count - 1 <= Breadboard.Columns; column++)
                        {
                            var cost = 0.0;
                            var ok = true;
                            for (var i = 0; i < count && ok; i++)
                            {
                                var hole = new BoardHole(board, row, column + i);
                                var strip = Breadboard.StripOf(hole);
                                if (!Free(hole) || !Allows(strip, nets[i])) { ok = false; break; }
                                if (_stripNet.ContainsKey(strip)) { if (Spare(strip) < 1) ok = false; }
                                else
                                {
                                    var others = (_netStrips.GetValueOrDefault(nets[i]) ?? []).Select(Centre).ToList();
                                    cost += 3 + (others.Count == 0 ? 0 : 0.25 * others.Min(o => Distance(o, Centre(strip))));
                                }
                            }

                            if (!ok) continue;
                            var row1 = row;
                            var column1 = column;
                            List<BoardHole> row0 = [.. Enumerable.Range(0, count).Select(i => new BoardHole(board, row1, column1 + i))];
                            if (InlineCovered(package, row0).Any(h => !Free(h)) || Clashes(InlineBox(package.Look, row0))) continue;
                            var (x, y) = Breadboard.Position(new BoardHole(board, row, column));
                            var middle = Breadboard.IsTopHalf(row) ? Breadboard.A + 2 : Breadboard.F + 2;
                            cost += (0.03 * Distance((x + ((count - 1) / 2.0), y), focus)) + (0.1 * Math.Abs(row - middle));
                            if (cost < lowest)
                            {
                                lowest = cost;
                                best = [.. Enumerable.Range(0, count).Select(i => new BoardHole(board, row, column + i))];
                            }
                        }
                    }
                }

                if (best is not null)
                {
                    Commit(part, package, best, [.. InlineCovered(package, best)], byHand: false);
                    return true;
                }

                if (!AddBoard()) return false;
            }

            return false;
        }

        // ---- the bench ----

        private void PlaceBench(List<(CircuitComponent Part, BoardPackage Package)> items)
        {
            var y = 0.0;
            foreach (var (part, package) in items)
            {
                var width = package.Look == BoardLook.DevBoard ? 20.0 : 15.0;
                var height = Math.Max(6.0, 4.0 + (package.Pins.Count * 1.8));
                var byHand = hand.Parts.TryGetValue(part.Id, out var placed) && placed.BenchX is not null && placed.BenchY is not null;
                var (x, top) = byHand ? (placed!.BenchX!.Value, placed.BenchY!.Value) : (-width - 8.0, y);
                if (!byHand) y += height + 2.0;

                var leads = new List<BenchLead>();
                for (var i = 0; i < package.Pins.Count; i++)
                {
                    var pin = package.Pins[i]!;
                    var net = NetOf(pin);
                    var start = (x + width, top + 3.0 + (i * 1.8));

                    // Into the nearest strip that has the net, or its rail; an empty strip near the board's left end
                    // when nothing on the board carries it yet.
                    var focus = _netStrips.ContainsKey(net) || _rails.Values.Contains(net) ? start : Focus([net]);
                    var spots = Spots(net, focus);
                    if (spots.Count == 0 && AddBoard()) spots = Spots(net, focus);
                    var chosen = spots.OrderBy(s => s.Cost + (0.05 * Distance(Breadboard.Position(s.Hole), start))).Cast<Spot?>().FirstOrDefault();
                    if (chosen is not { } spot)
                    {
                        _unplaced.Add($"{part.Name}.{pin.Name}");
                        continue;
                    }

                    Take(spot.Hole, net);
                    leads.Add(new BenchLead(pin, net, spot.Hole, Role(net), Colour(net), _router.Route(start, Breadboard.Position(spot.Hole))));
                }

                _bench.Add(new BenchPart(part, package, x, top, width, height, leads, byHand));
            }
        }

        // ---- the wires ----

        private WireRole Role(Net net) =>
            ReferenceEquals(net, _ground) ? WireRole.Ground
            : ReferenceEquals(net, _supply) ? WireRole.Supply
            : ReferenceEquals(net, _supply2) ? WireRole.OtherSupply
            : WireRole.Signal;

        private int Colour(Net net)
        {
            if (!_colours.TryGetValue(net, out var colour)) _colours[net] = colour = _colours.Count;
            return colour;
        }

        /// <summary>
        /// Joins each net's strips with jumpers: the shortest wires that reach them all (a spanning tree), each end in a
        /// free hole of its strip. A wire whose ends were moved by hand keeps them while it still joins the same strips.
        /// </summary>
        private List<BoardWire> Wire()
        {
            var wires = new List<BoardWire>();
            var kept = new Dictionary<string, (BoardHole From, BoardHole To)>(StringComparer.Ordinal);
            foreach (var (key, (from, to)) in hand.Wires)
            {
                if (!Free(from) || !Free(to)) continue;
                _used.Add(from);
                _used.Add(to);
                kept[key] = (from, to);
            }

            foreach (var (net, strips) in _netStrips.OrderBy(n => n.Key.IsGround ? -1 : n.Key.Index))
            {
                if (strips.Count < 2) continue;
                var nodes = strips.ToList();
                var start = nodes.OrderByDescending(s => s.IsRail).ThenByDescending(FreeCount).First();
                var tree = new List<BoardStrip> { start };
                var rest = nodes.Where(s => s != start).ToList();

                // Prim's tree, each strip outside it remembering its cheapest way in, so that a new strip joining is
                // weighed against the others once rather than every pair being weighed again.
                var bestFor = new Dictionary<BoardStrip, (double Cost, BoardStrip From, BoardHole A, BoardHole B)>();
                void Consider(BoardStrip b, BoardStrip a)
                {
                    if (Pair(a, b, kept) is { } e && (!bestFor.TryGetValue(b, out var now) || e.Cost < now.Cost))
                        bestFor[b] = (e.Cost, a, e.A, e.B);
                }

                void Reconsider(BoardStrip b)
                {
                    bestFor.Remove(b);
                    foreach (var a in tree) Consider(b, a);
                }

                foreach (var b in rest) Consider(b, start);

                while (rest.Count > 0)
                {
                    var next = rest.Where(bestFor.ContainsKey).OrderBy(b => bestFor[b].Cost).Cast<BoardStrip?>().FirstOrDefault();
                    if (next is not { } joining)
                    {
                        _notes.Add($"There was no free hole left to wire {NetName(net)} with.");
                        break;
                    }

                    // A wire moved by hand keeps the holes reserved for it; any other edge's holes must still be free.
                    var edge = bestFor[joining];
                    var own = kept.TryGetValue(WireKey(edge.From, joining), out var hand) && (hand.From == edge.A || hand.To == edge.A);
                    if (!own && (!Free(edge.A) || !Free(edge.B)))
                    {
                        Reconsider(joining);
                        continue;
                    }

                    var wireKey = WireKey(edge.From, joining);
                    var byHand = kept.Remove(wireKey);
                    _used.Add(edge.A);
                    _used.Add(edge.B);
                    wires.Add(new BoardWire(wireKey, net, edge.A, edge.B, Role(net), Colour(net), byHand,
                        _router.Route(Breadboard.Position(edge.A), Breadboard.Position(edge.B))));
                    tree.Add(joining);
                    rest.Remove(joining);
                    bestFor.Remove(joining);

                    foreach (var r in rest)
                    {
                        if (bestFor.TryGetValue(r, out var was) && was.A == edge.A) Reconsider(r);
                        else Consider(r, joining);
                    }
                }
            }

            return wires;
        }

        /// <summary>
        /// The cheapest jumper between two strips of a net: the shortest one that keeps off the parts (a straight wire
        /// over a body costs as much as a detour three times what it covers), or the one moved there by hand.
        /// </summary>
        private (double Cost, BoardHole A, BoardHole B)? Pair(BoardStrip a, BoardStrip b, Dictionary<string, (BoardHole From, BoardHole To)> kept)
        {
            if (kept.TryGetValue(WireKey(a, b), out var own))
            {
                var (f, g) = (Breadboard.StripOf(own.From), Breadboard.StripOf(own.To));
                if (f == a && g == b) return (0, own.From, own.To);
                if (f == b && g == a) return (0, own.To, own.From);
            }

            // Nearest first, so the looking stops as soon as nothing left can be cheaper than the best found.
            // A rail's fifty holes are narrowed to the ones that matter: those near the strip it is joining, or the left
            // end, where two rails are jumpered to each other.
            IEnumerable<BoardHole> Near(BoardStrip strip, BoardStrip other)
            {
                var free = Breadboard.HolesOf(strip).Where(Free);
                if (!strip.IsRail) return free;
                if (other.IsRail) return free.OrderBy(h => h.Column).Take(4);
                var close = free.Where(h => Math.Abs(h.Column - other.Column) <= 4).ToList();
                return close.Count > 0 ? close : free.OrderBy(h => Math.Abs(h.Column - other.Column)).Take(4);
            }

            var holesB = Near(b, a).ToList();
            var pairs = Near(a, b)
                .SelectMany(ha => holesB.Select(hb => (D: Breadboard.Distance(ha, hb), A: ha, B: hb)))
                .OrderBy(p => p.D).ThenBy(p => p.A.Row).ThenBy(p => p.A.Column).ThenBy(p => p.B.Row).ThenBy(p => p.B.Column);
            (double, BoardHole, BoardHole)? best = null;
            var lowest = double.MaxValue;
            foreach (var (d, ha, hb) in pairs)
            {
                if (d >= lowest) break;
                var cost = d + (3 * _router.Covered(ha, hb));
                if (cost < lowest)
                {
                    lowest = cost;
                    best = (cost, ha, hb);
                }
            }

            return best;
        }

        // ---- the check ----

        /// <summary>
        /// Builds the connections back from the board alone (strips, wires, leads) and holds them against the netlist:
        /// a net in more than one piece, or two nets in one, is a board that isn't the schematic.
        /// </summary>
        private List<string> Check(List<(CircuitComponent Part, BoardPackage Package)> built, List<BoardWire> wires)
        {
            var problems = new List<string>(_notes);
            var parent = new Dictionary<string, string>(StringComparer.Ordinal);
            string Find(string x)
            {
                if (!parent.TryGetValue(x, out var p)) { parent[x] = x; return x; }
                if (p == x) return x;
                var root = Find(p);
                parent[x] = root;
                return root;
            }

            void Union(string x, string y)
            {
                var (rx, ry) = (Find(x), Find(y));
                if (rx != ry) parent[rx] = ry;
            }

            static string Node(BoardStrip s) => $"{s.Board}:{s.Row}:{s.Column}";
            foreach (var wire in wires) Union(Node(Breadboard.StripOf(wire.From)), Node(Breadboard.StripOf(wire.To)));

            var at = new Dictionary<Terminal, string>();
            foreach (var part in _parts)
                for (var i = 0; i < part.Package.Pins.Count; i++)
                    if (part.Package.Pins[i] is { } pin) at[pin] = Node(Breadboard.StripOf(part.Holes[i]));
            foreach (var item in _bench)
                foreach (var lead in item.Leads)
                {
                    var node = $"T:{lead.Terminal.Uid}";
                    Union(node, Node(Breadboard.StripOf(lead.To)));
                    at[lead.Terminal] = node;
                }

            var netsAt = new Dictionary<string, HashSet<Net>>(StringComparer.Ordinal);
            var piecesOf = new Dictionary<Net, HashSet<string>>();
            foreach (var (terminal, node) in at)
            {
                var root = Find(node);
                var net = NetOf(terminal);
                if (!netsAt.TryGetValue(root, out var nets)) netsAt[root] = nets = [];
                nets.Add(net);
                if (!piecesOf.TryGetValue(net, out var pieces)) piecesOf[net] = pieces = [];
                pieces.Add(root);
            }

            foreach (var (net, pieces) in piecesOf)
                if (pieces.Count > 1) problems.Add($"{NetName(net)} is in {pieces.Count} pieces on the board.");
            foreach (var nets in netsAt.Values)
                if (nets.Count > 1) problems.Add($"{string.Join(" and ", nets.Select(NetName))} are joined on the board.");
            foreach (var name in _unplaced) problems.Add($"{name} isn't on the board: there was no room for it.");
            return problems;
        }
    }
}
