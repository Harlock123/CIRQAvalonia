using System.Globalization;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Components.Protoboard;

/// <summary>
/// The circuit laid out on a soldered board: stripboard or perfboard.
/// <para>
/// <b>Stripboard</b> has copper strips running the length of the board (here, down it: strips are lettered A, B, C
/// across, holes numbered down). A pin is on whatever net its strip carries, so two pins of different nets on one strip
/// need the copper cut between them, and a net that ends up on several runs of copper is joined by wire links. Chips
/// straddle so each pin has a strip of its own, cut under the body between the two rows; parts lie across strips. The
/// layout places the parts so each pin lands on a strip already carrying its net where it can (no link needed), works
/// out the cuts from where the pins are, and links what's left.
/// </para>
/// <para>
/// <b>Perfboard</b> has a pad at every hole and nothing joined: the same placement, and each net's pins wired together
/// on the back.
/// </para>
/// <para>
/// Either way the connections are rebuilt from the board alone (copper, cuts, links, wires, leads) and held against the
/// schematic's netlist, as the breadboard's are. Parts put somewhere by hand (<see cref="Circuit.Board"/>) go there
/// first, checked against the copper as they go; the rest are laid out round them.
/// </para>
/// </summary>
public static class SolderedLayout
{
    /// <summary>Where a hole's centre is, in pitches: column across, row down.</summary>
    public static (double X, double Y) Position(BoardHole hole) => (2.5 + hole.Column, 1.5 + hole.Row);

    public static double Width(int columns) => columns + 5;

    public static double Height(int rows) => rows + 3;

    /// <summary>A strip's letters, as a stripboard's are printed: A to Z, then AA.</summary>
    public static string Letters(int column)
    {
        var text = "";
        for (var n = column; n > 0; n = (n - 1) / 26) text = (char)('A' + ((n - 1) % 26)) + text;
        return text;
    }

    /// <summary>A hole as strip and number: <c>C12</c>.</summary>
    public static string Name(BoardHole hole) => $"{Letters(hole.Column)}{hole.Row.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>A hole from its name as <see cref="Name"/> prints it (<c>C12</c>: strip C, hole 12), or null.</summary>
    public static BoardHole? Parse(string? text)
    {
        var name = text?.Trim().ToUpperInvariant() ?? "";
        var letters = name.TakeWhile(char.IsAsciiLetterUpper).Count();
        if (letters is 0 or > 2 || !int.TryParse(name[letters..], NumberStyles.None, CultureInfo.InvariantCulture, out var row) || row < 1) return null;
        var column = 0;
        foreach (var c in name[..letters]) column = (column * 26) + (c - 'A' + 1);
        return new BoardHole(1, row, column);
    }

    /// <summary>Lays <paramref name="circuit"/> out on a <paramref name="kind"/>.</summary>
    public static ProtoboardLayout Arrange(Circuit circuit, BoardKind kind) => Arrange(circuit, new BoardArrangement { Kind = kind });

    /// <summary>
    /// Lays <paramref name="circuit"/> out on the board <paramref name="hand"/> names, keeping what was put somewhere by
    /// hand in it (<paramref name="first"/>, the part being moved, checked before the others).
    /// </summary>
    public static ProtoboardLayout Arrange(Circuit circuit, BoardArrangement hand, Guid? first = null)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(hand);
        return new Builder(circuit, hand.Kind, hand, first).Run();
    }

    /// <summary>
    /// The holes a package takes with one pin (<paramref name="index"/>) in <paramref name="at"/>, and the holes its body
    /// covers: a chip with pin 1 bottom left, its pins counted round anticlockwise; an inline part along a row.
    /// </summary>
    public static (List<BoardHole> Holes, List<BoardHole> Covered) Shape(BoardPackage package, int index, BoardHole at)
    {
        ArgumentNullException.ThrowIfNull(package);
        var count = package.Pins.Count;
        if (package.Form == BoardForm.Dip)
        {
            var half = count / 2;
            var n = index + 1;
            var (column, top) = n <= half ? (at.Column - (n - 1), at.Row - package.Span) : (at.Column - (count - n), at.Row);
            return Dip(package, column, top);
        }

        return ([.. Enumerable.Range(0, count).Select(i => at with { Column = at.Column + i - index })], []);
    }

    private static (List<BoardHole> Holes, List<BoardHole> Covered) Dip(BoardPackage package, int column, int top)
    {
        var count = package.Pins.Count;
        var half = count / 2;
        var holes = Enumerable.Range(1, count).Select(n => n <= half
            ? new BoardHole(1, top + package.Span, column + n - 1)
            : new BoardHole(1, top, column + count - n)).ToList();
        var covered = new List<BoardHole>();
        for (var c = column; c < column + half; c++)
            for (var r = top + 1; r < top + package.Span; r++) covered.Add(new BoardHole(1, r, c));
        return (holes, covered);
    }

    private sealed class Builder(Circuit circuit, BoardKind kind, BoardArrangement hand, Guid? first)
    {
        private readonly Dictionary<Guid, string> _refused = [];
        private bool Strips => kind == BoardKind.Stripboard;

        private Netlist _netlist = null!;
        private int _columns, _rows;
        private readonly Dictionary<BoardHole, Net?> _pins = [];
        private readonly HashSet<BoardHole> _blocked = [];
        private readonly Dictionary<int, SortedList<int, Net?>> _byColumn = [];
        private readonly Dictionary<Net, List<BoardHole>> _netHoles = [];
        private readonly List<BoardPart> _parts = [];
        private readonly List<BenchPart> _bench = [];
        private readonly List<string> _unplaced = [];
        private readonly List<(double X, double Y, double R)> _discs = [];
        private readonly List<(double L, double T, double R, double B)> _boxes = [];
        private readonly Dictionary<Net, int> _colours = [];
        private readonly Dictionary<Net, string> _captions = [];
        private Net? _ground, _supply;

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
                    new Dictionary<Guid, string>(), [$"The circuit can't be built: {e.Message}"], []) { Kind = kind, Columns = 20, Rows = 12 };
            }

            ChooseSupplies(built);

            // What was put somewhere by hand goes there first, the part being moved before the rest.
            var placedByHand = new HashSet<CircuitComponent>();
            _columns = 24;
            _rows = 16;
            foreach (var (part, package) in built.OrderBy(p => p.Part.Id == first ? 0 : 1))
            {
                if (package.Form == BoardForm.Bench || !hand.Parts.TryGetValue(part.Id, out var placement) || placement.Pins.Count == 0) continue;
                if (PlaceByHand(part, package, placement) is { } why) _refused[part.Id] = why;
                else placedByHand.Add(part);
            }

            built = [.. built.Where(p => !placedByHand.Contains(p.Part))];
            var dips = built.Where(p => p.Package.Form == BoardForm.Dip).OrderBy(p => p.Part.X).ThenBy(p => p.Part.Y).ToList();
            var small = built.Where(p => p.Package.Form is BoardForm.TwoLead or BoardForm.Inline).ToList();
            var bench = built.Where(p => p.Package.Form == BoardForm.Bench).ToList();

            PlaceDips(dips, small.Count);

            while (small.Count > 0)
            {
                var next = small.OrderByDescending(p => p.Package.Pins.Count(t => t is not null && _netHoles.ContainsKey(NetOf(t))))
                    .ThenBy(p => p.Part.X).ThenBy(p => p.Part.Y).First();
                small.Remove(next);
                if (!Place(next.Part, next.Package)) _unplaced.Add(next.Part.Name);
            }

            PlaceBench(bench);

            // As big as what's on it, and a margin: a 555 doesn't need a sheet the size of a computer's.
            if (_pins.Count > 0)
            {
                _columns = Math.Max(10, _pins.Keys.Max(h => h.Column) + 2);
                _rows = Math.Max(8, _pins.Keys.Concat(_blocked).Max(h => h.Row) + 2);
            }

            var cuts = Strips ? Cut() : [];
            var segments = Strips ? Segment(cuts) : [];
            var wires = Strips ? Link(segments) : kind.IsPcb() ? Traces() : Wire();
            var problems = Check(segments, wires);

            var holeNets = new Dictionary<BoardHole, Net>();
            foreach (var (hole, net) in _pins) if (net is not null) holeNets[hole] = net;
            foreach (var wire in wires)
            {
                holeNets[wire.From] = wire.Net;
                holeNets[wire.To] = wire.Net;
            }

            return new ProtoboardLayout(1, _parts, _bench, wires, new Dictionary<BoardStrip, Net>(), new Dictionary<int, Net>(),
                _refused, problems, _unplaced, _captions)
            {
                Kind = kind,
                Columns = _columns,
                Rows = _rows,
                Cuts = cuts,
                Segments = segments,
                HoleNets = holeNets,
                Vias = _vias,
                Pour = _pour,
            };
        }

        private Net NetOf(Terminal t) => _netlist.NetOf(t);

        // ---- supplies, for the wires' colours and the captions ----

        private void ChooseSupplies(List<(CircuitComponent Part, BoardPackage Package)> built)
        {
            var terminals = built.SelectMany(p => p.Part.Terminals).ToHashSet();
            _ground = _netlist.GroundNet is { } g && g.Terminals.Any(terminals.Contains) ? g : null;
            foreach (var (part, _) in built)
            {
                if (part is not (DcVoltageSource or Battery) || part.Terminals.Count != 2 || _ground is null) continue;
                var (a, b) = (NetOf(part.Terminals[0]), NetOf(part.Terminals[1]));
                var volts = part.ValueLabel.Split(' ')[0];
                if (ReferenceEquals(b, _ground)) { _supply ??= a; _captions.TryAdd(a, "+" + volts); }
                else if (ReferenceEquals(a, _ground)) _captions.TryAdd(b, "−" + volts);
            }
        }

        private WireRole Role(Net net) =>
            ReferenceEquals(net, _ground) ? WireRole.Ground : ReferenceEquals(net, _supply) ? WireRole.Supply : WireRole.Signal;

        private int Colour(Net net)
        {
            if (!_colours.TryGetValue(net, out var colour)) _colours[net] = colour = _colours.Count;
            return colour;
        }

        // ---- the grid ----

        private bool InGrid(BoardHole h) => h.Row >= 1 && h.Row <= _rows && h.Column >= 1 && h.Column <= _columns;

        private bool Free(BoardHole h) => InGrid(h) && !_pins.ContainsKey(h) && !_blocked.Contains(h);

        /// <summary>
        /// Whether a pin of <paramref name="net"/> may go in <paramref name="hole"/>: it's free and, on a stripboard, the
        /// copper either side of it can be cut from any other net's pin on the same strip (a hole between them at least).
        /// </summary>
        private bool Allows(BoardHole hole, Net? net)
        {
            if (!Free(hole)) return false;
            if (!Strips || net is null || !_byColumn.TryGetValue(hole.Column, out var column)) return true;
            var (above, below) = Neighbours(column, hole.Row);
            if (above is { } a && a.Net is { } na && !ReferenceEquals(na, net) && hole.Row - a.Row < 2) return false;
            if (below is { } b && b.Net is { } nb && !ReferenceEquals(nb, net) && b.Row - hole.Row < 2) return false;
            return true;
        }

        private static ((int Row, Net? Net)? Above, (int Row, Net? Net)? Below) Neighbours(SortedList<int, Net?> column, int row)
        {
            (int, Net?)? above = null, below = null;
            foreach (var (r, n) in column)
            {
                if (n is null) continue;
                if (r < row) above = (r, n);
                else if (r > row) { below = (r, n); break; }
            }

            return (above, below);
        }

        /// <summary>
        /// What a pin of <paramref name="net"/> in <paramref name="hole"/> costs: nothing when its strip already carries
        /// the net there (stripboard) or it's the net's first pin; otherwise the link or wire it will need.
        /// </summary>
        private double Cost(BoardHole hole, Net? net)
        {
            if (net is null || !_netHoles.TryGetValue(net, out var holes) || holes.Count == 0) return 0;
            var reach = holes.Min(h => Distance(Position(h), Position(hole)));
            if (!Strips) return 0.4 * reach;
            if (_byColumn.TryGetValue(hole.Column, out var column))
            {
                var (above, below) = Neighbours(column, hole.Row);
                if ((above is { } a && ReferenceEquals(a.Net, net)) || (below is { } b && ReferenceEquals(b.Net, net))) return 0;

                // Between two pins of another net on one strip: it would cut that net in two.
                if (above is { } x && below is { } y && x.Net is not null && ReferenceEquals(x.Net, y.Net)) return 6 + (0.3 * reach);
            }

            return 3 + (0.3 * reach);
        }

        private void Take(BoardHole hole, Net? net)
        {
            _pins[hole] = net;
            if (!_byColumn.TryGetValue(hole.Column, out var column)) _byColumn[hole.Column] = column = [];
            column[hole.Row] = net;
            if (net is null) return;
            if (!_netHoles.TryGetValue(net, out var list)) _netHoles[net] = list = [];
            list.Add(hole);
        }

        private static double Distance((double X, double Y) a, (double X, double Y) b) =>
            Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

        private bool Clashes(IEnumerable<(double X, double Y, double R)> discs) =>
            discs.Any(d => _discs.Any(e => Distance((d.X, d.Y), (e.X, e.Y)) < d.R + e.R + 0.1) || _boxes.Any(b => Touches(d, b)));

        private bool Clashes((double L, double T, double R, double B) box) =>
            _discs.Any(d => Touches(d, box)) || _boxes.Any(b => box.L < b.R + 0.1 && b.L < box.R + 0.1 && box.T < b.B + 0.1 && b.T < box.B + 0.1);

        private static bool Touches((double X, double Y, double R) d, (double L, double T, double R, double B) b)
        {
            var x = Math.Clamp(d.X, b.L, b.R);
            var y = Math.Clamp(d.Y, b.T, b.B);
            return ((d.X - x) * (d.X - x)) + ((d.Y - y) * (d.Y - y)) < (d.R + 0.05) * (d.R + 0.05);
        }

        // ---- chips ----

        /// <summary>
        /// Chips in bands across the board, left to right as they read on the schematic, with room between and around them
        /// for the parts that join their pins. The board is sized to hold them.
        /// </summary>
        private void PlaceDips(List<(CircuitComponent Part, BoardPackage Package)> dips, int smallParts)
        {
            const int gap = 4, margin = 3, maxWidth = 64;
            var bands = new List<List<(CircuitComponent Part, BoardPackage Package)>> { new() };
            var width = margin;
            foreach (var dip in dips)
            {
                var half = dip.Package.Pins.Count / 2;
                if (width + half + gap > maxWidth && bands[^1].Count > 0)
                {
                    bands.Add([]);
                    width = margin;
                }

                bands[^1].Add(dip);
                width += half + gap;
            }

            _columns = Math.Max(_columns, Math.Max(bands.Max(b => margin + b.Sum(d => (d.Package.Pins.Count / 2) + gap)) + margin, 12 + (int)Math.Ceiling(Math.Sqrt(smallParts) * 4)));
            var row = 5;
            foreach (var band in bands)
            {
                var span = band.Count == 0 ? 3 : band.Max(d => d.Package.Span);
                var column = margin + 1;
                foreach (var (part, package) in band)
                {
                    var half = package.Pins.Count / 2;
                    var top = row + span - package.Span;

                    // Along the band past anything put there by hand.
                    _rows = Math.Max(_rows, top + package.Span + 2);
                    _columns = Math.Max(_columns, column + half + margin);
                    var (holes, covered) = Dip(package, column, top);
                    for (var tries = 0; tries < 400 && !DipFits(package, holes, covered); tries++)
                    {
                        column++;
                        if (column + half + margin > _columns) _columns = column + half + margin;
                        (holes, covered) = Dip(package, column, top);
                    }

                    CommitDip(part, package, holes, covered, byHand: false);
                    column += half + gap;
                }

                row += span + 10;
            }

            _rows = Math.Max(_rows, row + 2);
        }

        private bool DipFits(BoardPackage package, List<BoardHole> holes, List<BoardHole> covered)
        {
            for (var i = 0; i < holes.Count; i++)
                if (!Allows(holes[i], package.Pins[i] is { } pin ? NetOf(pin) : null)) return false;
            return covered.All(Free) && !Clashes(ProtoboardLayout.DipBox([.. holes.Select(Position)]));
        }

        private void CommitDip(CircuitComponent part, BoardPackage package, List<BoardHole> holes, List<BoardHole> covered, bool byHand)
        {
            for (var i = 0; i < holes.Count; i++) Take(holes[i], package.Pins[i] is { } pin ? NetOf(pin) : null);
            foreach (var hole in covered) _blocked.Add(hole);
            _parts.Add(new BoardPart(part, package, holes, byHand));
            _boxes.Add(ProtoboardLayout.DipBox([.. holes.Select(Position)]));
        }

        // ---- by hand ----

        /// <summary>Puts a part where it was put by hand, or says why it can't go there.</summary>
        private string? PlaceByHand(CircuitComponent part, BoardPackage package, BoardPlacement placement)
        {
            List<BoardHole> holes;
            List<BoardHole> covered = [];
            if (package.Form == BoardForm.TwoLead)
            {
                holes = [];
                foreach (var pin in package.Pins)
                {
                    if (pin is null || !placement.Pins.TryGetValue(pin.Id, out var hole)) return $"{part.Name}'s place on the board is missing a pin; it was laid out afresh.";
                    holes.Add(hole with { Board = 1 });
                }
            }
            else
            {
                var index = Enumerable.Range(0, package.Pins.Count).FirstOrDefault(i => package.Pins[i] is { } p && placement.Pins.ContainsKey(p.Id), -1);
                if (index < 0) return $"{part.Name}'s place on the board names none of its pins; it was laid out afresh.";
                (holes, covered) = Shape(package, index, placement.Pins[package.Pins[index]!.Id] with { Board = 1 });
            }

            if (holes.Concat(covered).Any(h => h.Row < 1 || h.Column < 1 || h.Row > 400 || h.Column > 400)) return $"{part.Name} would hang off the board.";
            _columns = Math.Max(_columns, holes.Concat(covered).Max(h => h.Column) + 3);
            _rows = Math.Max(_rows, holes.Concat(covered).Max(h => h.Row) + 3);

            var nets = package.Pins.Select(p => p is null ? null : (Net?)NetOf(p)).ToList();
            for (var i = 0; i < holes.Count; i++)
            {
                if (!Free(holes[i])) return $"{Name(holes[i])} is taken.";
                if (!Allows(holes[i], nets[i]))
                    return $"{Name(holes[i])} is too close to a pin of another net on strip {Letters(holes[i].Column)}: there's no room to cut the copper between them.";
                for (var j = 0; j < i; j++)
                    if (Strips && holes[i].Column == holes[j].Column && !ReferenceEquals(nets[i], nets[j]) && Math.Abs(holes[i].Row - holes[j].Row) < 2)
                        return $"{part.Name}'s pins would be on strip {Letters(holes[i].Column)} with no room for a cut between them.";
            }

            if (covered.FirstOrDefault(h => !Free(h)) is { Row: > 0 } under) return $"{part.Name}'s body would sit on {Name(under)}, which is in use.";
            var points = holes.Select(Position).ToList();
            var clash = package.Form switch
            {
                BoardForm.TwoLead => Clashes(ProtoboardLayout.Discs(part, package, points[0], points[1])),
                BoardForm.Dip => Clashes(ProtoboardLayout.DipBox([.. points])),
                _ => Clashes(ProtoboardLayout.InlineBox(package.Look, points, up: true)),
            };
            if (clash) return $"{part.Name} would sit on another part there.";

            if (package.Form == BoardForm.Dip)
            {
                CommitDip(part, package, holes, covered, byHand: true);
                return null;
            }

            for (var i = 0; i < holes.Count; i++) Take(holes[i], nets[i]);
            _parts.Add(new BoardPart(part, package, holes, true));
            if (package.Form == BoardForm.TwoLead) _discs.AddRange(ProtoboardLayout.Discs(part, package, points[0], points[1]));
            else _boxes.Add(ProtoboardLayout.InlineBox(package.Look, points, up: true));
            return null;
        }

        private static (double X, double Y) Position(BoardHole hole) => SolderedLayout.Position(hole);

        // ---- the small parts ----

        private (double X, double Y) Focus(IEnumerable<Net> nets)
        {
            var points = nets.SelectMany(n => _netHoles.GetValueOrDefault(n) ?? []).Select(Position).ToList();
            return points.Count > 0 ? (points.Average(p => p.X), points.Average(p => p.Y)) : Position(new BoardHole(1, _rows / 2, _columns / 2));
        }

        /// <summary>
        /// A part across the strips: a two-lead part lying along a row (or, with the copper cut between its leads, down a
        /// strip), an inline part along a row with a strip for each pin. The cheapest place near its nets wins; the board
        /// grows when nothing fits.
        /// </summary>
        private bool Place(CircuitComponent part, BoardPackage package)
        {
            var nets = package.Pins.Select(p => (Net?)NetOf(p!)).ToList();
            for (var attempt = 0; attempt < 4; attempt++)
            {
                var focus = Focus(nets.OfType<Net>());
                List<BoardHole>? best = null;
                var lowest = double.MaxValue;
                var radius = attempt == 0 ? 14 : 400;
                var (fx, fy) = ((int)Math.Round(focus.X - 2.5), (int)Math.Round(focus.Y - 1.5));

                foreach (var holes in Shapes(package, fx, fy, radius))
                {
                    var ok = true;
                    var cost = 0.0;
                    for (var i = 0; i < holes.Count && ok; i++)
                    {
                        if (!Allows(holes[i], nets[i])) ok = false;
                        else cost += Cost(holes[i], nets[i]);
                    }

                    if (!ok) continue;

                    // Two pins of different nets of the same part on one strip must have room for a cut between.
                    if (Strips)
                        for (var i = 0; i < holes.Count && ok; i++)
                            for (var j = i + 1; j < holes.Count; j++)
                                if (holes[i].Column == holes[j].Column && !ReferenceEquals(nets[i], nets[j]) && Math.Abs(holes[i].Row - holes[j].Row) < 2) ok = false;
                    if (!ok) continue;

                    var points = holes.Select(Position).ToList();
                    if (package.Form == BoardForm.TwoLead ? Clashes(ProtoboardLayout.Discs(part, package, points[0], points[1]))
                        : Clashes(ProtoboardLayout.InlineBox(package.Look, points, up: true))) continue;

                    var middle = (points.Average(p => p.X), points.Average(p => p.Y));
                    cost += 0.05 * Distance(middle, focus);
                    if (package.Form == BoardForm.TwoLead)
                    {
                        var span = Distance(points[0], points[1]);
                        cost += 0.3 * Math.Abs(span - package.Reach.Usual);
                        if (holes[0].Column == holes[1].Column) cost += 0.8;
                    }

                    if (cost < lowest)
                    {
                        lowest = cost;
                        best = holes;
                    }
                }

                if (best is not null)
                {
                    for (var i = 0; i < best.Count; i++) Take(best[i], nets[i]);
                    _parts.Add(new BoardPart(part, package, best, false));
                    var points = best.Select(Position).ToList();
                    if (package.Form == BoardForm.TwoLead) _discs.AddRange(ProtoboardLayout.Discs(part, package, points[0], points[1]));
                    else _boxes.Add(ProtoboardLayout.InlineBox(package.Look, points, up: true));
                    return true;
                }

                // Nothing fits: a bigger board.
                if (attempt >= 1)
                {
                    _rows += 8;
                    _columns += 6;
                }
            }

            return false;
        }

        /// <summary>The ways a part could sit with its first pin near a point: along a row, and for two leads, down a strip.</summary>
        private IEnumerable<List<BoardHole>> Shapes(BoardPackage package, int fx, int fy, int radius)
        {
            var count = package.Pins.Count;
            var (min, _, max) = package.Reach;
            for (var r = Math.Max(1, fy - radius); r <= Math.Min(_rows, fy + radius); r++)
                for (var c = Math.Max(1, fx - radius); c <= Math.Min(_columns, fx + radius); c++)
                {
                    if (package.Form == BoardForm.Inline)
                    {
                        if (c + count - 1 <= _columns) yield return [.. Enumerable.Range(0, count).Select(i => new BoardHole(1, r, c + i))];
                        continue;
                    }

                    for (var span = (int)Math.Ceiling(min); span <= (int)Math.Floor(max); span++)
                    {
                        if (c + span <= _columns) yield return [new BoardHole(1, r, c), new BoardHole(1, r, c + span)];
                        if (r + span <= _rows) yield return [new BoardHole(1, r, c), new BoardHole(1, r + span, c)];
                    }
                }
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
                    var hole = LeadHole(net, start);
                    if (hole is not { } at)
                    {
                        _unplaced.Add($"{part.Name}.{pin.Name}");
                        continue;
                    }

                    var end = Position(at);
                    leads.Add(new BenchLead(pin, net, at, Role(net), Colour(net), [start, (Math.Min(start.Item1 + 3, end.X - 1), start.Item2), end]));
                }

                _bench.Add(new BenchPart(part, package, x, top, width, height, leads, byHand));
            }
        }

        /// <summary>
        /// Where a lead from the bench goes: a free hole on a strip carrying its net (stripboard), or straight onto one of its
        /// pins' pads (perfboard); a hole of its own near the left edge when nothing on the board carries it.
        /// </summary>
        private BoardHole? LeadHole(Net net, (double X, double Y) from)
        {
            if (_netHoles.TryGetValue(net, out var holes) && holes.Count > 0)
            {
                if (!Strips) return holes.MinBy(h => Distance(Position(h), from));
                foreach (var pin in holes.OrderBy(h => h.Column))
                    for (var d = 1; d <= 3; d++)
                        foreach (var r in new[] { pin.Row - d, pin.Row + d })
                        {
                            var h = new BoardHole(1, r, pin.Column);
                            if (Allows(h, net) && Cost(h, net) == 0)
                            {
                                Take(h, net);
                                return h;
                            }
                        }
            }

            // Anywhere the copper allows, near the left edge the leads come in from: a link joins it to the rest if need be.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                BoardHole? best = null;
                var lowest = double.MaxValue;
                for (var c = 1; c <= _columns; c++)
                    for (var r = 1; r <= _rows; r++)
                    {
                        var h = new BoardHole(1, r, c);
                        if (!Allows(h, net)) continue;
                        var cost = Cost(h, net) + (0.05 * c);
                        if (cost < lowest) { lowest = cost; best = h; }
                    }

                if (best is { } found)
                {
                    Take(found, net);
                    return found;
                }

                _rows += 4;
            }

            return null;
        }

        // ---- stripboard: cuts, copper and links ----

        /// <summary>A cut between every two neighbouring pins of different nets on a strip, as near halfway as a free hole allows.</summary>
        private List<BoardHole> Cut()
        {
            var cuts = new List<BoardHole>();
            foreach (var (column, pins) in _byColumn.OrderBy(c => c.Key))
            {
                (int Row, Net Net)? last = null;
                foreach (var (row, net) in pins)
                {
                    if (net is null) continue;
                    if (last is { } before && !ReferenceEquals(before.Net, net))
                    {
                        var between = Enumerable.Range(before.Row + 1, row - before.Row - 1)
                            .OrderBy(r => Math.Abs(r - ((before.Row + row) / 2.0)))
                            .Select(r => new BoardHole(1, r, column))
                            .FirstOrDefault(h => !_pins.ContainsKey(h), new BoardHole(1, 0, 0));
                        if (between.Row > 0) cuts.Add(between);
                    }

                    last = (row, net);
                }
            }

            return cuts;
        }

        private List<StripSegment> Segment(List<BoardHole> cuts)
        {
            var segments = new List<StripSegment>();
            var cutAt = cuts.GroupBy(c => c.Column).ToDictionary(g => g.Key, g => g.Select(c => c.Row).OrderBy(r => r).ToList());
            for (var column = 1; column <= _columns; column++)
            {
                var from = 1;
                foreach (var cut in cutAt.GetValueOrDefault(column) ?? []) { segments.Add(Run(column, from, cut - 1)); from = cut + 1; }
                segments.Add(Run(column, from, _rows));
            }

            return segments;

            StripSegment Run(int column, int from, int to)
            {
                Net? net = null;
                if (_byColumn.TryGetValue(column, out var pins))
                    foreach (var (row, n) in pins)
                        if (row >= from && row <= to && n is not null) { net = n; break; }
                return new StripSegment(column, from, to, net);
            }
        }

        /// <summary>Wire links joining each net's runs of copper: the shortest that reach them all, along a row where both are free.</summary>
        private List<BoardWire> Link(List<StripSegment> segments)
        {
            var wires = new List<BoardWire>();
            foreach (var group in segments.Where(s => s.Net is not null).GroupBy(s => s.Net!).OrderBy(g => g.Key.IsGround ? -1 : g.Key.Index))
            {
                var nodes = group.ToList();
                if (nodes.Count < 2) continue;
                var tree = new List<StripSegment> { nodes[0] };
                var rest = nodes.Skip(1).ToList();
                while (rest.Count > 0)
                {
                    (StripSegment To, BoardHole A, BoardHole B, double Cost)? best = null;
                    foreach (var a in tree)
                        foreach (var b in rest)
                            if (Join(a, b) is { } j && (best is null || j.Cost < best.Value.Cost)) best = (b, j.A, j.B, j.Cost);
                    if (best is not { } edge) break;
                    if (Free(edge.A)) Take(edge.A, group.Key);
                    if (Free(edge.B)) Take(edge.B, group.Key);
                    var (pa, pb) = (Position(edge.A), Position(edge.B));
                    wires.Add(new BoardWire($"link:{Name(edge.A)}-{Name(edge.B)}", group.Key, edge.A, edge.B, Role(group.Key), Colour(group.Key), false, [pa, pb]));
                    tree.Add(edge.To);
                    rest.Remove(edge.To);
                }
            }

            return wires;
        }

        /// <summary>The cheapest link between two runs of copper: straight along a row both span, or else between their nearest free holes.</summary>
        private (BoardHole A, BoardHole B, double Cost)? Join(StripSegment a, StripSegment b)
        {
            (BoardHole, BoardHole, double)? best = null;
            var lo = Math.Max(a.From, b.From);
            var hi = Math.Min(a.To, b.To);
            for (var r = lo; r <= hi; r++)
            {
                var (ha, hb) = (new BoardHole(1, r, a.Column), new BoardHole(1, r, b.Column));
                if (!Free(ha) || !Free(hb)) continue;
                var cost = Math.Abs(a.Column - b.Column) + (0.01 * Math.Abs(r - ((lo + hi) / 2.0)));
                if (best is null || cost < best.Value.Item3) best = (ha, hb, cost);
            }

            if (best is not null) return best;

            // A free hole on each if there is one; on a run of copper with every hole taken, the link is soldered to a pin's
            // pad of the same net, which joins the same copper.
            foreach (var shared in new[] { false, true })
            {
                foreach (var ra in Enumerable.Range(a.From, a.To - a.From + 1))
                    foreach (var rb in Enumerable.Range(b.From, b.To - b.From + 1))
                    {
                        var (ha, hb) = (new BoardHole(1, ra, a.Column), new BoardHole(1, rb, b.Column));
                        if (!Usable(ha, a.Net, shared) || !Usable(hb, b.Net, shared)) continue;
                        var cost = Distance(Position(ha), Position(hb)) + 2 + (shared && (!Free(ha) || !Free(hb)) ? 4 : 0);
                        if (best is null || cost < best.Value.Item3) best = (ha, hb, cost);
                    }

                if (best is not null) return best;
            }

            return null;
        }

        private bool Usable(BoardHole hole, Net? net, bool shared) =>
            Free(hole) || (shared && _pins.TryGetValue(hole, out var on) && on is not null && ReferenceEquals(on, net));

        // ---- perfboard: wiring on the back ----

        /// <summary>Each net's pads wired together on the back: the shortest wires that reach them all, bent square.</summary>
        private List<BoardWire> Wire()
        {
            var wires = new List<BoardWire>();
            foreach (var (net, holes) in _netHoles.OrderBy(n => n.Key.IsGround ? -1 : n.Key.Index))
            {
                var nodes = holes.Distinct().ToList();
                if (nodes.Count < 2) continue;
                var tree = new List<BoardHole> { nodes[0] };
                var rest = nodes.Skip(1).ToList();
                while (rest.Count > 0)
                {
                    var (a, b) = tree.SelectMany(t => rest.Select(r => (t, r))).MinBy(p => Math.Abs(p.t.Column - p.r.Column) + Math.Abs(p.t.Row - p.r.Row));
                    var (pa, pb) = (Position(a), Position(b));
                    List<(double X, double Y)> route = pa.X == pb.X || pa.Y == pb.Y ? [pa, pb] : [pa, (pb.X, pa.Y), pb];
                    wires.Add(new BoardWire($"wire:{Name(a)}-{Name(b)}", net, a, b, Role(net), Colour(net), false, route));
                    tree.Add(b);
                    rest.Remove(b);
                }
            }

            return wires;
        }

        // ---- printed circuit board: routed traces ----

        private List<BoardHole> _vias = [];
        private IReadOnlyList<PourArea> _pour = [];
        private IReadOnlyList<(BoardHole A, BoardHole B)> _pourJoins = [];

        /// <summary>
        /// Each net's pads joined by copper traces, routed round each other, on one side or both; a jumper where one
        /// can't be; and the ground pour, if it's on, round them all.
        /// </summary>
        private List<BoardWire> Traces()
        {
            var nets = _netHoles.OrderBy(n => n.Key.IsGround ? -1 : n.Key.Index).Select(n => new PcbRouter.NetPads(n.Key, n.Value.Distinct().ToList())).ToList();
            var loose = _pins.Where(p => p.Value is null).Select(p => p.Key);

            // Where a jumper's ends can't go: under a part's body on the component side.
            var under = new HashSet<BoardHole>(_blocked);
            for (var row = 1; row <= _rows; row++)
                for (var column = 1; column <= _columns; column++)
                {
                    var hole = new BoardHole(1, row, column);
                    var (x, y) = Position(hole);
                    if (_discs.Any(d => Distance((x, y), (d.X, d.Y)) < d.R + 0.3) || _boxes.Any(b => x > b.L - 0.3 && x < b.R + 0.3 && y > b.T - 0.3 && y < b.B + 0.3))
                        under.Add(hole);
                }

            var pour = hand.GroundPour && _ground is not null && _netHoles.ContainsKey(_ground);
            var routing = PcbRouter.Route(nets, loose, under, _columns, _rows, kind.CopperLayers(), (net, a, b, route, layer) => route is null
                    ? new BoardWire($"jumper:{Name(a)}-{Name(b)}", net, a, b, Role(net), Colour(net), false, [Position(a), Position(b)])
                    : new BoardWire($"trace{(layer == 1 ? "-top" : "")}:{Name(a)}-{Name(b)}", net, a, b, Role(net), Colour(net), false, route) { IsTrace = true, Layer = layer },
                pour ? (wires, vias) => Pour(wires, vias).Joins : null);
            _vias = routing.Vias;
            if (pour)
            {
                var final = Pour(routing.Wires, routing.Vias);
                _pour = final.Areas;
                _pourJoins = final.Joins;
            }

            return routing.Wires;
        }

        /// <summary>The ground pour round the copper so far: every pad, via, trace and jumper's end.</summary>
        private GroundPour.Result Pour(List<BoardWire> wires, List<BoardHole> vias)
        {
            var copper = new List<GroundPour.Copper>();
            foreach (var (hole, net) in _pins) copper.Add(new(net, -1, Position(hole), Position(hole), PcbRouter.PadDiameter));
            var netOf = new Dictionary<BoardHole, Net>();
            foreach (var wire in wires)
            {
                netOf[wire.From] = wire.Net;
                netOf[wire.To] = wire.Net;
                if (wire.IsTrace)
                {
                    var width = wire.Role == WireRole.Signal ? PcbRouter.SignalWidth : PcbRouter.PowerWidth;
                    for (var i = 1; i < wire.Route.Count; i++) copper.Add(new(wire.Net, wire.Layer, wire.Route[i - 1], wire.Route[i], width));
                }
                else
                    foreach (var end in new[] { wire.From, wire.To })
                        if (!_pins.ContainsKey(end)) copper.Add(new(wire.Net, -1, Position(end), Position(end), PcbRouter.PadDiameter));
            }

            foreach (var via in vias)
                if (netOf.TryGetValue(via, out var net)) copper.Add(new(net, -1, Position(via), Position(via), PcbRouter.ViaDiameter));
            return GroundPour.Pour(Width(_columns), Height(_rows), kind.CopperLayers(), copper, _netHoles[_ground!].Distinct().ToList());
        }

        // ---- the check ----

        /// <summary>The connections rebuilt from the copper, cuts, links, wires and leads alone, held against the netlist.</summary>
        private List<string> Check(List<StripSegment> segments, List<BoardWire> wires)
        {
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

            string Node(BoardHole h)
            {
                if (!Strips) return $"{h.Row}:{h.Column}";
                var s = segments.First(seg => seg.Column == h.Column && h.Row >= seg.From && h.Row <= seg.To);
                return $"s{s.Column}:{s.From}";
            }

            foreach (var wire in wires) Union(Node(wire.From), Node(wire.To));
            foreach (var (a, b) in _pourJoins) Union(Node(a), Node(b));
            var at = new Dictionary<Terminal, string>();
            foreach (var part in _parts)
                for (var i = 0; i < part.Package.Pins.Count; i++)
                    if (part.Package.Pins[i] is { } pin) at[pin] = Node(part.Holes[i]);
            foreach (var item in _bench)
                foreach (var lead in item.Leads)
                {
                    var node = $"T:{lead.Terminal.Uid}";
                    Union(node, Node(lead.To));
                    at[lead.Terminal] = node;
                }

            var problems = new List<string>();
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
                if (pieces.Count > 1) problems.Add($"{ProtoboardLayout.NetName(net)} is in {pieces.Count} pieces on the board.");
            foreach (var nets in netsAt.Values)
                if (nets.Count > 1) problems.Add($"{string.Join(" and ", nets.Select(ProtoboardLayout.NetName))} are joined on the board.");
            foreach (var name in _unplaced) problems.Add($"{name} isn't on the board: there was no room for it.");
            return problems;
        }
    }
}
