using Cirq.Core.Topology;

namespace Cirq.Components.Protoboard;

/// <summary>
/// The copper of a single-sided printed circuit board: each net's pads joined by traces on the back, routed by a maze
/// router, and a wire jumper on the component side wherever a trace can't get through.
/// <para>
/// The router works on a grid at half the hole pitch (1.27 mm), so one trace fits between two neighbouring pads, as it
/// does between a DIP's pins. A cell belongs to one net at most: a pad's cell to its pin's net, a trace's cells to the
/// net it carries, so traces of different nets never touch or cross, and keep at least a cell apart from pads. Moves go
/// eight ways; a diagonal step is only taken when neither cell beside it belongs to another net, so a trace never cuts
/// between two of another net's cells. Each net grows as a tree: its pads in order of nearness, each routed by
/// a search to the first copper the net already has (pad or trace), with a little extra for each turn and for
/// running alongside another net, so the traces come out straight and spaced; A* aims each search at the nearest pad
/// already joined, so it stays near the straight line between them.
/// </para>
/// <para>
/// Where a trace is walled in, it may hop: a wire jumper on the component side, two to six holes long and straight,
/// over whatever copper is in the way, between two holes free on both sides (a jumper's end can't go under a part). A
/// hop costs as much as eight steps of trace, so the router takes one only when going round is much longer or
/// impossible; the trace either side of it is ordinary copper. Only when even that fails does a pad get a jumper
/// straight to the net.
/// </para>
/// <para>
/// Nets are routed shortest first. When some can't be, the routing is tried again with those first, and the try with
/// the fewest jumpers is kept.
/// </para>
/// </summary>
public static class PcbRouter
{
    /// <summary>A trace's width in millimetres: a signal's, and a supply's or ground's.</summary>
    public const double SignalWidth = 0.4, PowerWidth = 0.6;

    /// <summary>A pad's diameter and the hole drilled through it, in millimetres.</summary>
    public const double PadDiameter = 1.6, DrillDiameter = 0.9;

    /// <summary>The hole pitch, in millimetres.</summary>
    public const double Pitch = 2.54;

    private static readonly (int X, int Y)[] Steps = [(1, 0), (-1, 0), (0, 1), (0, -1), (1, 1), (1, -1), (-1, 1), (-1, -1)];

    /// <summary>A net's pads to join, in the order they are reached.</summary>
    public sealed record NetPads(Net Net, IReadOnlyList<BoardHole> Pads);

    /// <summary>
    /// Routes <paramref name="nets"/> on a board <paramref name="columns"/> by <paramref name="rows"/> holes, with
    /// <paramref name="others"/> the pads that belong to no net (a chip's unconnected pins) in the way.
    /// </summary>
    /// <param name="wire">Makes the board's wire for a trace (route given, <see cref="BoardWire.IsTrace"/> set) or a jumper (route null) between two pads.</param>
    /// <param name="under">Holes under parts on the component side, where a jumper's end can't go.</param>
    public static List<BoardWire> Route(IReadOnlyList<NetPads> nets, IEnumerable<BoardHole> others, IReadOnlySet<BoardHole> under, int columns, int rows,
        Func<Net, BoardHole, BoardHole, IReadOnlyList<(double X, double Y)>?, BoardWire> wire)
    {
        ArgumentNullException.ThrowIfNull(nets);
        ArgumentNullException.ThrowIfNull(wire);
        var blocked = others?.ToList() ?? [];
        var order = nets.Where(n => n.Pads.Distinct().Count() > 1).OrderBy(n => Span(n.Pads)).ThenBy(n => n.Net.Index).ToList();

        List<BoardWire>? best = null;
        var bestJumpers = int.MaxValue;
        for (var attempt = 0; attempt < 3 && bestJumpers > 0; attempt++)
        {
            var grid = new Grid(columns, rows, nets, blocked, under);
            var wires = new List<BoardWire>();
            var failed = new List<NetPads>();
            foreach (var net in order)
            {
                var jumpers = grid.RouteNet(net, wires, wire);
                if (jumpers > 0) failed.Add(net);
            }

            var count = wires.Count(w => !w.IsTrace);
            if (count < bestJumpers)
            {
                best = wires;
                bestJumpers = count;
            }

            if (failed.Count == 0) break;
            order = [.. failed, .. order.Except(failed)];
        }

        return best ?? [];
    }

    private static int Span(IReadOnlyList<BoardHole> pads) =>
        pads.Max(p => p.Column) - pads.Min(p => p.Column) + pads.Max(p => p.Row) - pads.Min(p => p.Row);

    /// <summary>A grid cell's centre, in pitches on the board's drawing: as <see cref="SolderedLayout.Position"/> puts holes.</summary>
    public static (double X, double Y) Position(int x, int y) => (2.5 + (x / 2.0), 1.5 + (y / 2.0));

    private sealed class Grid
    {
        private readonly int _width, _height;
        private readonly int[] _owner;          // 0 free, -1 a pad of no net, else the net's Id
        private readonly BoardHole?[] _reaches; // for a cell of a net's copper, a pad of that net it is joined to
        private readonly HashSet<int> _pads = [];

        private readonly IReadOnlySet<BoardHole> _under;

        /// <summary>What a hop costs, in steps of trace: as much as going a good way round.</summary>
        private const double HopCost = 8;

        public Grid(int columns, int rows, IReadOnlyList<NetPads> nets, IEnumerable<BoardHole> others, IReadOnlySet<BoardHole> under)
        {
            _under = under;
            _width = (2 * columns) + 3;
            _height = (2 * rows) + 3;
            _owner = new int[_width * _height];
            _reaches = new BoardHole?[_width * _height];
            foreach (var hole in others) if (Index(hole) is { } i) _owner[i] = -1;
            foreach (var net in nets)
                foreach (var pad in net.Pads)
                    if (Index(pad) is { } i)
                    {
                        _owner[i] = Id(net.Net);
                        _pads.Add(i);
                    }
        }

        /// <summary>A net's mark on the cells it owns: never 0 (free) or -1 (a pad of no net), ground's index being -1.</summary>
        private static int Id(Net net) => net.Index + 2;

        private int? Index(BoardHole hole) => Index(2 * hole.Column, 2 * hole.Row);

        private int? Index(int x, int y) => x >= 1 && y >= 1 && x < _width - 1 && y < _height - 1 ? (y * _width) + x : null;

        /// <summary>Joins a net's pads, returning how many needed a jumper.</summary>
        public int RouteNet(NetPads net, List<BoardWire> wires,
            Func<Net, BoardHole, BoardHole, IReadOnlyList<(double X, double Y)>?, BoardWire> wire)
        {
            var me = Id(net.Net);
            var pads = net.Pads.Distinct().ToList();
            var tree = new HashSet<int>();
            var start = pads[0];
            tree.Add(Index(start)!.Value);
            _reaches[Index(start)!.Value] = start;
            var joined = new List<BoardHole> { start };
            var rest = pads.Skip(1).ToList();
            var jumpers = 0;
            while (rest.Count > 0)
            {
                var next = rest.MinBy(p => joined.Min(j => Math.Abs(j.Column - p.Column) + Math.Abs(j.Row - p.Row)))!;
                rest.Remove(next);
                if (tree.Contains(Index(next)!.Value))
                {
                    joined.Add(next);
                    continue;
                }

                var goal = joined.MinBy(j => Math.Abs(j.Column - next.Column) + Math.Abs(j.Row - next.Row))!;
                var path = Search(Index(next)!.Value, tree, me, goal);
                if (path is null)
                {
                    wires.Add(wire(net.Net, next, goal, null));
                    jumpers++;
                    tree.Add(Index(next)!.Value);
                    _reaches[Index(next)!.Value] = next;
                    joined.Add(next);
                    continue;
                }

                var reached = _reaches[path[^1].Cell]!.Value;
                foreach (var (cell, _) in path)
                {
                    _owner[cell] = me;
                    _reaches[cell] = next;
                    tree.Add(cell);
                }

                // The path in pieces: copper, a hop (a jumper between two new pads), copper again.
                var from = next;
                var piece = new List<int> { path[0].Cell };
                for (var i = 1; i < path.Count; i++)
                {
                    if (path[i].Hop)
                    {
                        var (a, b) = (Hole(path[i - 1].Cell), Hole(path[i].Cell));
                        _pads.Add(path[i - 1].Cell);
                        _pads.Add(path[i].Cell);
                        if (piece.Count > 1) wires.Add(wire(net.Net, from, a, Simplify(piece)));
                        wires.Add(wire(net.Net, a, b, null));
                        from = b;
                        piece = [path[i].Cell];
                        continue;
                    }

                    piece.Add(path[i].Cell);
                }

                wires.Add(wire(net.Net, from, reached, Simplify(piece)));
                joined.Add(next);
            }

            return jumpers;
        }

        // The search's working arrays, kept between searches: a state is a cell and the direction it was entered in
        // (8 for the start), and a stamp says which search a state's cost belongs to.
        private double[]? _cost;
        private int[]? _back;
        private int[]? _stamp;
        private bool[]? _hopped;
        private int _search;

        private BoardHole Hole(int cell) => new(1, cell / _width / 2, cell % _width / 2);

        private bool IsHole(int x, int y) => x % 2 == 0 && y % 2 == 0;

        /// <summary>Whether a jumper's end may go at a cell: a hole free on the component side, and free or this net's on the back.</summary>
        private bool JumperEnd(int cell, int me, bool landing) =>
            IsHole(cell % _width, cell / _width) && !_under.Contains(Hole(cell))
            && (landing ? _owner[cell] == 0 : _owner[cell] == 0 || (_owner[cell] == me && !_pads.Contains(cell)));

        /// <summary>
        /// A short path of cells from <paramref name="from"/> to any cell of <paramref name="tree"/>, or null: A* toward
        /// <paramref name="goal"/>, the net's nearest pad already joined, stopping at the first of the net's copper it meets.
        /// </summary>
        private List<(int Cell, bool Hop)>? Search(int from, HashSet<int> tree, int me, BoardHole goal)
        {
            var states = _owner.Length * 9;
            _cost ??= new double[states];
            _back ??= new int[states];
            _stamp ??= new int[states];
            _hopped ??= new bool[states];
            _search++;
            var (gx, gy) = (2 * goal.Column, 2 * goal.Row);
            double Guess(int cell)
            {
                var (dx, dy) = (Math.Abs((cell % _width) - gx), Math.Abs((cell / _width) - gy));
                return Math.Max(dx, dy) + (0.42 * Math.Min(dx, dy));
            }

            var queue = new PriorityQueue<int, double>();
            var first = (from * 9) + 8;
            _stamp[first] = _search;
            _cost[first] = 0;
            _back[first] = -1;
            _hopped[first] = false;
            queue.Enqueue(first, Guess(from));
            var expanded = 0;
            while (queue.TryDequeue(out var state, out var priority))
            {
                var cell = state / 9;
                var spent = _cost[state];
                if (priority - Guess(cell) > spent + 1e-9) continue;
                if (cell != from && tree.Contains(cell))
                {
                    var path = new List<(int, bool)>();
                    for (var s = state; s >= 0; s = _back[s]) path.Add((s / 9, _hopped[s]));
                    path.Reverse();
                    return path;
                }

                if (++expanded > 200_000) return null;
                var dir = state % 9;
                var (x, y) = (cell % _width, cell / _width);
                for (var d = 0; d < Steps.Length; d++)
                {
                    var (dx, dy) = Steps[d];
                    if (Index(x + dx, y + dy) is not { } next) continue;
                    var owner = _owner[next];
                    if (owner != 0 && owner != me) continue;

                    // Not through a pad of its own net that isn't joined yet: it would be joined with no wire to say so.
                    if (owner == me && _pads.Contains(next) && !tree.Contains(next)) continue;
                    var diagonal = dx != 0 && dy != 0;
                    if (diagonal && (Foreign(x + dx, y, me) || Foreign(x, y + dy, me))) continue;

                    var step = diagonal ? 1.42 : 1.0;
                    if (dir != 8 && dir != d) step += 0.35;
                    if (owner == 0) step += 0.25 * Crowding(x + dx, y + dy, me);
                    var total = spent + step;
                    var key = (next * 9) + d;
                    if (_stamp[key] == _search && _cost[key] <= total) continue;
                    _stamp[key] = _search;
                    _cost[key] = total;
                    _back[key] = state;
                    _hopped[key] = false;
                    queue.Enqueue(key, total + Guess(next));
                }

                // A hop over what's in the way, from a hole to a free hole two to six holes on, straight along a row or a column.
                if (!JumperEnd(cell, me, landing: false)) continue;
                for (var d = 0; d < 4; d++)
                {
                    var (dx, dy) = Steps[d];
                    for (var holes = 2; holes <= 6; holes++)
                    {
                        if (Index(x + (2 * holes * dx), y + (2 * holes * dy)) is not { } land) break;
                        if (!JumperEnd(land, me, landing: true)) continue;
                        var total = spent + HopCost + holes;
                        var key = (land * 9) + 8;
                        if (_stamp[key] == _search && _cost[key] <= total) continue;
                        _stamp[key] = _search;
                        _cost[key] = total;
                        _back[key] = state;
                        _hopped[key] = true;
                        queue.Enqueue(key, total + Guess(land));
                    }
                }
            }

            return null;
        }

        private bool Foreign(int x, int y, int me) => Index(x, y) is { } i && _owner[i] != 0 && _owner[i] != me;

        /// <summary>How many of a cell's four neighbours belong to other nets: running alongside them costs a little.</summary>
        private int Crowding(int x, int y, int me) =>
            (Foreign(x + 1, y, me) ? 1 : 0) + (Foreign(x - 1, y, me) ? 1 : 0) + (Foreign(x, y + 1, me) ? 1 : 0) + (Foreign(x, y - 1, me) ? 1 : 0);

        /// <summary>A path's cells as a trace's corners, in pitches: the straight runs merged.</summary>
        private List<(double X, double Y)> Simplify(List<int> path)
        {
            var points = new List<(int X, int Y)>();
            foreach (var cell in path)
            {
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
