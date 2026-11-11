using Cirq.Core.Topology;

namespace Cirq.Components.Protoboard;

/// <summary>What the router made of a board: its traces and jumpers, its vias, and the ground pads the pour joins.</summary>
public sealed record PcbRouting(List<BoardWire> Wires, List<BoardHole> Vias, IReadOnlyList<(BoardHole A, BoardHole B)> PourJoins);

/// <summary>
/// The copper of a printed circuit board, single- or double-sided: each net's pads joined by traces, routed by a maze
/// router, with vias between the layers of a double-sided board and wire jumpers on a single-sided one where a trace
/// can't get through.
/// <para>
/// The router works on a grid at half the hole pitch (1.27 mm), so one trace fits between two neighbouring pads, as it
/// does between a DIP's pins. On each layer a cell belongs to one net at most: a pad's cell (on every layer: pads are
/// plated through) to its pin's net, a trace's cells to the net it carries, so traces of different nets never touch or
/// cross, and keep at least a cell apart from pads. Moves go eight ways; a diagonal step is only taken when neither cell
/// beside it belongs to another net, so a trace never cuts between two of another net's cells. Each net grows as a tree:
/// its pads in order of nearness, each routed by a search to the first copper the net already has (pad, trace or via),
/// with a little extra for each turn and for running alongside another net, so the traces come out straight and
/// spaced; A* aims each search at the nearest pad already joined, so it stays near the straight line between them.
/// </para>
/// <para>
/// On a double-sided board a trace may change layer at a via: at a hole position free on both layers, costing as much
/// as five steps of trace, so the router takes one where the other side is clear and this one isn't. Buses that have to
/// cross do it this way, one on each side.
/// </para>
/// <para>
/// On a single-sided board a walled-in trace may hop instead: a wire jumper on the component side, two to six holes long
/// and straight, over whatever copper is in the way, between two holes free on both sides (a jumper's end can't go under
/// a part). A hop costs as much as eight steps of trace. Only when even that fails does a pad get a jumper straight to
/// the net.
/// </para>
/// <para>
/// Nets are routed shortest first. With a ground pour, ground goes last: the pour is worked out round everything else,
/// and ground's pads that it already joins count as joined, so ground's traces only bridge what the pour can't reach.
/// When some nets can't be routed, it is tried again with those first, and the try with the fewest jumpers is kept.
/// </para>
/// </summary>
public static class PcbRouter
{
    /// <summary>A trace's width in millimetres: a signal's, and a supply's or ground's.</summary>
    public const double SignalWidth = 0.4, PowerWidth = 0.6;

    /// <summary>A pad's diameter and the hole drilled through it, in millimetres.</summary>
    public const double PadDiameter = 1.6, DrillDiameter = 0.9;

    /// <summary>A via's pad and its drill, in millimetres.</summary>
    public const double ViaDiameter = 1.0, ViaDrill = 0.5;

    /// <summary>The hole pitch, in millimetres.</summary>
    public const double Pitch = 2.54;

    private static readonly (int X, int Y)[] Steps = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    /// <summary>A net's pads to join, in the order they are reached.</summary>
    public sealed record NetPads(Net Net, IReadOnlyList<BoardHole> Pads);

    /// <summary>Makes the board's wire for a trace (route given, on a layer: 0 the back, 1 the component side) or a jumper (route null).</summary>
    public delegate BoardWire WireMaker(Net net, BoardHole from, BoardHole to, IReadOnlyList<(double X, double Y)>? route, int layer);

    /// <summary>
    /// Routes <paramref name="nets"/> on a board <paramref name="columns"/> by <paramref name="rows"/> holes with
    /// <paramref name="layers"/> layers of copper, <paramref name="others"/> the pads that belong to no net (a chip's
    /// unconnected pins) in the way.
    /// </summary>
    /// <param name="under">Holes under parts on the component side, where a jumper's end can't go.</param>
    /// <param name="pour">
    /// With a ground pour, which of ground's pads the pour joins, given the copper routed so far (every net but ground);
    /// null for none.
    /// </param>
    public static PcbRouting Route(IReadOnlyList<NetPads> nets, IEnumerable<BoardHole> others, IReadOnlySet<BoardHole> under, int columns, int rows,
        int layers, WireMaker wire, Func<List<BoardWire>, List<BoardHole>, IReadOnlyList<(BoardHole A, BoardHole B)>>? pour = null)
    {
        ArgumentNullException.ThrowIfNull(nets);
        ArgumentNullException.ThrowIfNull(wire);
        layers = Math.Clamp(layers, 1, 2);
        var blocked = others?.ToList() ?? [];
        var routed = nets.Where(n => n.Pads.Distinct().Count() > 1).ToList();
        var ground = pour is null ? null : routed.FirstOrDefault(n => n.Net.IsGround);
        var order = routed.Where(n => n != ground).OrderBy(n => Span(n.Pads)).ThenBy(n => n.Net.Index).ToList();

        PcbRouting? best = null;
        var bestJumpers = int.MaxValue;
        for (var attempt = 0; attempt < 3 && bestJumpers > 0; attempt++)
        {
            var grid = new Grid(columns, rows, layers, nets, blocked, under);
            var wires = new List<BoardWire>();
            var vias = new List<BoardHole>();
            var failed = new List<NetPads>();
            foreach (var net in order)
                if (grid.RouteNet(net, wires, vias, wire, []) > 0) failed.Add(net);

            IReadOnlyList<(BoardHole, BoardHole)> joins = [];
            if (pour is not null)
            {
                joins = pour(wires, vias);
                if (ground is not null && grid.RouteNet(ground, wires, vias, wire, joins) > 0) failed.Add(ground);
            }

            var count = wires.Count(w => !w.IsTrace);
            if (count < bestJumpers)
            {
                best = new PcbRouting(wires, vias, joins);
                bestJumpers = count;
            }

            if (failed.Count == 0) break;
            order = [.. failed.Where(f => f != ground), .. order.Except(failed)];
        }

        return best ?? new PcbRouting([], [], []);
    }

    private static int Span(IReadOnlyList<BoardHole> pads) =>
        pads.Max(p => p.Column) - pads.Min(p => p.Column) + pads.Max(p => p.Row) - pads.Min(p => p.Row);

    /// <summary>A grid cell's centre, in pitches on the board's drawing: as <see cref="SolderedLayout.Position"/> puts holes.</summary>
    public static (double X, double Y) Position(int x, int y) => (2.5 + (x / 2.0), 1.5 + (y / 2.0));

    private sealed class Grid
    {
        private readonly int _width, _height, _layers, _cells;
        private readonly int[] _owner;          // per layer and cell: 0 free, -1 a pad of no net, else the net's Id
        private readonly BoardHole?[] _reaches; // for a cell of a net's copper, a pad of that net it is joined to
        private readonly HashSet<int> _pads = []; // cells (layer-free) with a pad in them

        private readonly IReadOnlySet<BoardHole> _under;

        /// <summary>What a hop costs, in steps of trace: as much as going a good way round.</summary>
        private const double HopCost = 8;

        /// <summary>What a via costs, in steps of trace.</summary>
        private const double ViaCost = 5;

        public Grid(int columns, int rows, int layers, IReadOnlyList<NetPads> nets, IEnumerable<BoardHole> others, IReadOnlySet<BoardHole> under)
        {
            _under = under;
            _layers = layers;
            _width = (2 * columns) + 3;
            _height = (2 * rows) + 3;
            _cells = _width * _height;
            _owner = new int[_cells * layers];
            _reaches = new BoardHole?[_cells * layers];
            foreach (var hole in others)
                if (Index(hole) is { } i)
                    for (var l = 0; l < layers; l++) _owner[L(l, i)] = -1;
            foreach (var net in nets)
                foreach (var pad in net.Pads)
                    if (Index(pad) is { } i)
                    {
                        for (var l = 0; l < layers; l++) _owner[L(l, i)] = Id(net.Net);
                        _pads.Add(i);
                    }
        }

        /// <summary>A net's mark on the cells it owns: never 0 (free) or -1 (a pad of no net), ground's index being -1.</summary>
        private static int Id(Net net) => net.Index + 2;

        private int L(int layer, int cell) => (layer * _cells) + cell;

        private int? Index(BoardHole hole) => Index(2 * hole.Column, 2 * hole.Row);

        private int? Index(int x, int y) => x >= 1 && y >= 1 && x < _width - 1 && y < _height - 1 ? (y * _width) + x : null;

        private void Join(HashSet<int> tree, BoardHole pad)
        {
            var cell = Index(pad)!.Value;
            for (var l = 0; l < _layers; l++)
            {
                tree.Add(L(l, cell));
                _reaches[L(l, cell)] = pad;
            }
        }

        /// <summary>Joins a net's pads, returning how many needed a jumper; <paramref name="joins"/> are pairs already joined another way.</summary>
        public int RouteNet(NetPads net, List<BoardWire> wires, List<BoardHole> vias, WireMaker wire, IReadOnlyList<(BoardHole A, BoardHole B)> joins)
        {
            var me = Id(net.Net);
            var pads = net.Pads.Distinct().ToList();

            // Pads joined another way (the pour) go into the tree together.
            var group = pads.ToDictionary(p => p, p => new List<BoardHole> { p });
            foreach (var (a, b) in joins)
            {
                if (!group.TryGetValue(a, out var ga) || !group.TryGetValue(b, out var gb) || ReferenceEquals(ga, gb)) continue;
                ga.AddRange(gb);
                foreach (var p in gb) group[p] = ga;
            }

            var tree = new HashSet<int>();
            var joined = new List<BoardHole>();
            var done = new HashSet<BoardHole>();
            void Add(BoardHole pad)
            {
                foreach (var p in group[pad])
                    if (done.Add(p))
                    {
                        Join(tree, p);
                        joined.Add(p);
                    }
            }

            Add(pads.OrderByDescending(p => group[p].Count).First());
            var jumpers = 0;
            while (true)
            {
                var rest = pads.Where(p => !done.Contains(p)).ToList();
                if (rest.Count == 0) break;
                var next = rest.MinBy(p => joined.Min(j => Math.Abs(j.Column - p.Column) + Math.Abs(j.Row - p.Row)))!;
                var goal = joined.MinBy(j => Math.Abs(j.Column - next.Column) + Math.Abs(j.Row - next.Row))!;
                var path = Search(Index(next)!.Value, tree, me, goal);
                if (path is null)
                {
                    wires.Add(wire(net.Net, next, goal, null, 1));
                    jumpers++;
                    Add(next);
                    continue;
                }

                var reached = _reaches[path[^1].State]!.Value;
                foreach (var (state, how) in path)
                {
                    _owner[state] = me;
                    _reaches[state] = next;
                    tree.Add(state);
                    if (how == Move.Via)
                    {
                        var cell = state % _cells;
                        for (var l = 0; l < _layers; l++)
                        {
                            _owner[L(l, cell)] = me;
                            _reaches[L(l, cell)] = next;
                            tree.Add(L(l, cell));
                        }
                    }
                }

                // The path in pieces: copper on one layer, a via or a hop, copper again.
                var from = next;
                var piece = new List<int> { path[0].State };
                for (var i = 1; i < path.Count; i++)
                {
                    var (state, how) = path[i];
                    if (how == Move.Via)
                    {
                        var at = Hole(state % _cells);
                        if (!_pads.Contains(state % _cells))
                        {
                            vias.Add(at);
                            _pads.Add(state % _cells);
                        }

                        if (piece.Count > 1) wires.Add(wire(net.Net, from, at, Simplify(piece), piece[0] / _cells));
                        from = at;
                        piece = [state];
                        continue;
                    }

                    if (how == Move.Hop)
                    {
                        var (a, b) = (Hole(path[i - 1].State % _cells), Hole(state % _cells));
                        _pads.Add(path[i - 1].State % _cells);
                        _pads.Add(state % _cells);
                        if (piece.Count > 1) wires.Add(wire(net.Net, from, a, Simplify(piece), piece[0] / _cells));
                        wires.Add(wire(net.Net, a, b, null, 1));
                        from = b;
                        piece = [state];
                        continue;
                    }

                    piece.Add(state);
                }

                wires.Add(wire(net.Net, from, reached, Simplify(piece), piece[0] / _cells));
                Add(next);
            }

            return jumpers;
        }

        private enum Move : byte { Step, Hop, Via }

        // The search's working arrays, kept between searches: a state is a layer's cell and the direction it was
        // entered in (8 for the start, a via or a hop), and a stamp says which search a state's cost belongs to.
        private double[]? _cost;
        private int[]? _back;
        private int[]? _stamp;
        private Move[]? _how;
        private int _search;

        private BoardHole Hole(int cell) => new(1, cell / _width / 2, cell % _width / 2);

        private bool IsHole(int x, int y) => x % 2 == 0 && y % 2 == 0;

        /// <summary>Whether a jumper's end may go at a cell: a hole free on the component side, and free or this net's on the back.</summary>
        private bool JumperEnd(int cell, int me, bool landing) =>
            IsHole(cell % _width, cell / _width) && !_under.Contains(Hole(cell))
            && (landing ? _owner[cell] == 0 : _owner[cell] == 0 || (_owner[cell] == me && !_pads.Contains(cell)));

        /// <summary>
        /// A short path of layer cells from the pad at <paramref name="from"/> to any of <paramref name="tree"/>, or null:
        /// A* toward <paramref name="goal"/>, the net's nearest pad already joined, stopping at the first of the net's
        /// copper it meets.
        /// </summary>
        private List<(int State, Move How)>? Search(int from, HashSet<int> tree, int me, BoardHole goal)
        {
            var states = _owner.Length * 9;
            _cost ??= new double[states];
            _back ??= new int[states];
            _stamp ??= new int[states];
            _how ??= new Move[states];
            _search++;
            var (gx, gy) = (2 * goal.Column, 2 * goal.Row);
            double Guess(int lc)
            {
                var cell = lc % _cells;
                var (dx, dy) = (Math.Abs((cell % _width) - gx), Math.Abs((cell / _width) - gy));
                return Math.Max(dx, dy) + (0.42 * Math.Min(dx, dy));
            }

            var queue = new PriorityQueue<int, double>();
            for (var l = 0; l < _layers; l++)
            {
                var first = (L(l, from) * 9) + 8;
                _stamp[first] = _search;
                _cost[first] = 0;
                _back[first] = -1;
                _how[first] = Move.Step;
                queue.Enqueue(first, Guess(L(l, from)));
            }

            var expanded = 0;
            while (queue.TryDequeue(out var state, out var priority))
            {
                var lc = state / 9;
                var spent = _cost[state];
                if (priority - Guess(lc) > spent + 1e-9) continue;
                var layer = lc / _cells;
                var cell = lc % _cells;
                if (cell != from && tree.Contains(lc))
                {
                    var path = new List<(int, Move)>();
                    for (var s = state; s >= 0; s = _back[s]) path.Add((s / 9, _how[s]));
                    path.Reverse();
                    return path;
                }

                if (++expanded > 250_000) return null;
                var dir = state % 9;
                var (x, y) = (cell % _width, cell / _width);
                for (var d = 0; d < Steps.Length; d++)
                {
                    var (dx, dy) = Steps[d];
                    if (Index(x + dx, y + dy) is not { } nextCell) continue;
                    var next = L(layer, nextCell);
                    var owner = _owner[next];
                    if (owner != 0 && owner != me) continue;

                    // Not through a pad of its own net that isn't joined yet: it would be joined with no wire to say so.
                    if (owner == me && _pads.Contains(nextCell) && !tree.Contains(next)) continue;
                    var diagonal = dx != 0 && dy != 0;
                    if (diagonal && (Foreign(layer, x + dx, y, me) || Foreign(layer, x, y + dy, me))) continue;

                    var step = diagonal ? 1.42 : 1.0;
                    if (dir != 8 && dir != d) step += 0.35;
                    if (owner == 0) step += 0.25 * Crowding(layer, x + dx, y + dy, me);
                    Offer(queue, state, (next * 9) + d, spent + step, Move.Step, Guess(next));
                }

                // A via to the other side, at a hole free on both.
                if (_layers == 2 && cell != from && IsHole(x, y) && !_pads.Contains(cell))
                {
                    var other = L(1 - layer, cell);
                    if (_owner[other] == 0 || _owner[other] == me)
                        Offer(queue, state, (other * 9) + 8, spent + ViaCost, Move.Via, Guess(other));
                }

                // On one layer, a hop over what's in the way, from a hole to a free hole two to six holes on, straight.
                if (_layers != 1 || !JumperEnd(cell, me, landing: false)) continue;
                for (var d = 0; d < 4; d++)
                {
                    var (dx, dy) = Steps[d];
                    for (var holes = 2; holes <= 6; holes++)
                    {
                        if (Index(x + (2 * holes * dx), y + (2 * holes * dy)) is not { } land) break;
                        if (!JumperEnd(land, me, landing: true)) continue;
                        Offer(queue, state, (land * 9) + 8, spent + HopCost + holes, Move.Hop, Guess(land));
                    }
                }
            }

            return null;
        }

        private void Offer(PriorityQueue<int, double> queue, int from, int key, double total, Move how, double guess)
        {
            if (_stamp![key] == _search && _cost![key] <= total) return;
            _stamp[key] = _search;
            _cost![key] = total;
            _back![key] = from;
            _how![key] = how;
            queue.Enqueue(key, total + guess);
        }

        private bool Foreign(int layer, int x, int y, int me) => Index(x, y) is { } i && _owner[L(layer, i)] is var o && o != 0 && o != me;

        /// <summary>How many of a cell's four neighbours belong to other nets: running alongside them costs a little.</summary>
        private int Crowding(int layer, int x, int y, int me) =>
            (Foreign(layer, x + 1, y, me) ? 1 : 0) + (Foreign(layer, x - 1, y, me) ? 1 : 0) + (Foreign(layer, x, y + 1, me) ? 1 : 0) + (Foreign(layer, x, y - 1, me) ? 1 : 0);

        /// <summary>A path's cells as a trace's corners, in pitches: the straight runs merged.</summary>
        private List<(double X, double Y)> Simplify(List<int> path)
        {
            var points = new List<(int X, int Y)>();
            foreach (var lc in path)
            {
                var cell = lc % _cells;
                var p = (cell % _width, cell / _width);
                if (points.Count >= 2)
                {
                    var (a, b) = (points[^2], points[^1]);
                    if (Math.Sign(b.Item1 - a.Item1) == Math.Sign(p.Item1 - b.Item1) && Math.Sign(b.Item2 - a.Item2) == Math.Sign(p.Item2 - b.Item2))
                        points.RemoveAt(points.Count - 1);
                }

                points.Add(p);
            }

            return [.. points.Select(p => Position(p.X, p.Y))];
        }
    }
}
