using Cirq.Core.Topology;

namespace Cirq.Components.Protoboard;

/// <summary>
/// Finds a path for a jumper wire or a bench lead that keeps off the parts on the board.
/// <para>
/// The parts' bodies, as seen from above (discs for a resistor or an LED, boxes for a chip, a module or a TO-220),
/// are bucketed into a coarse grid so a point can be tested against the few near it. Each wire tries a handful of
/// shapes: straight; bowed to one side or the other, a little or a lot; an L; and runs along the board's clear
/// lanes, which are the centre channel, the gaps between the rails and the terminal rows, and the space
/// between two boards. The one that covers least of any part wins, and the shorter of two that cover nothing.
/// </para>
/// </summary>
internal sealed class WireRouter
{
    private const double Cell = 2.0;
    private const double Margin = 0.2;
    private const double Step = 0.2;

    private readonly List<(double X, double Y, double R)> _discs;
    private readonly List<(double L, double T, double R, double B)> _boxes;
    private readonly Dictionary<(int, int), List<int>> _discCells = [];
    private readonly Dictionary<(int, int), List<int>> _boxCells = [];
    private readonly int _boards;

    public WireRouter(List<(double X, double Y, double R)> discs, List<(double L, double T, double R, double B)> boxes, int boards)
    {
        _discs = discs;
        _boxes = boxes;
        _boards = boards;
        for (var i = 0; i < discs.Count; i++)
        {
            var (x, y, r) = discs[i];
            Bucket(_discCells, i, x - r - Margin, y - r - Margin, x + r + Margin, y + r + Margin);
        }

        for (var i = 0; i < boxes.Count; i++)
        {
            var (l, t, r, b) = boxes[i];
            Bucket(_boxCells, i, l - Margin, t - Margin, r + Margin, b + Margin);
        }
    }

    private static void Bucket(Dictionary<(int, int), List<int>> cells, int index, double l, double t, double r, double b)
    {
        for (var cx = (int)Math.Floor(l / Cell); cx <= (int)Math.Floor(r / Cell); cx++)
            for (var cy = (int)Math.Floor(t / Cell); cy <= (int)Math.Floor(b / Cell); cy++)
            {
                if (!cells.TryGetValue((cx, cy), out var list)) cells[(cx, cy)] = list = [];
                list.Add(index);
            }
    }

    /// <summary>Whether a point is on a part's body (or within a whisker of it).</summary>
    public bool Inside(double x, double y)
    {
        var key = ((int)Math.Floor(x / Cell), (int)Math.Floor(y / Cell));
        if (_discCells.TryGetValue(key, out var discs))
            foreach (var i in discs)
            {
                var (dx, dy, r) = _discs[i];
                var reach = r + Margin;
                if (((x - dx) * (x - dx)) + ((y - dy) * (y - dy)) < reach * reach) return true;
            }

        if (_boxCells.TryGetValue(key, out var boxes))
            foreach (var i in boxes)
            {
                var (l, t, r, b) = _boxes[i];
                if (x > l - Margin && x < r + Margin && y > t - Margin && y < b + Margin) return true;
            }

        return false;
    }

    private const double Fine = 0.25;
    private bool[,]? _fine;
    private double _fineLeft, _fineTop;

    /// <summary>
    /// <see cref="Inside"/> read from a quarter-pitch grid worked out once, since a layout asks it tens of thousands of
    /// times while choosing its wires.
    /// </summary>
    private bool Blocked(double x, double y)
    {
        if (_fine is null)
        {
            _fineLeft = -30;
            _fineTop = -2;
            var columns = (int)Math.Ceiling((Breadboard.Width + 32) / Fine) + 1;
            var rows = (int)Math.Ceiling((Breadboard.Top(_boards) + Breadboard.Height + 4) / Fine) + 1;
            _fine = new bool[columns, rows];
            for (var i = 0; i < columns; i++)
                for (var j = 0; j < rows; j++)
                    _fine[i, j] = Inside(_fineLeft + (i * Fine), _fineTop + (j * Fine));
        }

        var ci = (int)Math.Round((x - _fineLeft) / Fine);
        var cj = (int)Math.Round((y - _fineTop) / Fine);
        return ci >= 0 && cj >= 0 && ci < _fine.GetLength(0) && cj < _fine.GetLength(1) ? _fine[ci, cj] : Inside(x, y);
    }

    /// <summary>How much of a path, in pitches, lies over a part: not counting the last half pitch at each end, which is in its hole.</summary>
    public double Covered(IReadOnlyList<(double X, double Y)> path)
    {
        if (path.Count < 2) return 0;
        var (start, end) = (path[0], path[^1]);
        var covered = 0.0;
        for (var i = 1; i < path.Count; i++)
        {
            var (ax, ay) = path[i - 1];
            var (bx, by) = path[i];
            var length = Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay)));
            var steps = Math.Max(1, (int)Math.Ceiling(length / Step));
            for (var k = 0; k < steps; k++)
            {
                var t = (k + 0.5) / steps;
                var (x, y) = (ax + ((bx - ax) * t), ay + ((by - ay) * t));
                if (Near(x, y, start) || Near(x, y, end)) continue;
                if (Blocked(x, y)) covered += length / steps;
            }
        }

        return covered;
    }

    private static bool Near(double x, double y, (double X, double Y) p) => ((x - p.X) * (x - p.X)) + ((y - p.Y) * (y - p.Y)) < 0.25;

    /// <summary>How much a straight wire between two holes would cover.</summary>
    public double Covered(BoardHole a, BoardHole b) => Covered([Breadboard.Position(a), Breadboard.Position(b)]);

    /// <summary>The clearest path from <paramref name="a"/> to <paramref name="b"/>, in pitches, ends included.</summary>
    public IReadOnlyList<(double X, double Y)> Route((double X, double Y) a, (double X, double Y) b)
    {
        List<(double X, double Y)> straight = [a, b];
        if (Covered(straight) == 0) return straight;

        List<(double X, double Y)> acrossFirst = [a, (b.X, a.Y), b];
        List<(double X, double Y)> downFirst = [a, (a.X, b.Y), b];
        List<List<(double X, double Y)>> candidates = [straight, acrossFirst, downFirst];

        foreach (var bow in new[] { 1.2, 2.5, 4.0, 6.0, 9.0 })
        {
            candidates.Add(Bowed(a, b, bow));
            candidates.Add(Bowed(a, b, -bow));
        }

        foreach (var lane in Lanes(a.Y, b.Y))
            candidates.Add([a, (a.X, lane), (b.X, lane), b]);

        List<(double X, double Y)> best = straight;
        var lowest = double.MaxValue;
        var bestCovered = double.MaxValue;
        foreach (var path in candidates)
        {
            var covered = Covered(path);
            var cost = (covered * 40) + Length(path) + (0.6 * Bends(path));
            if (cost < lowest)
            {
                lowest = cost;
                best = path;
                bestCovered = covered;
            }
        }

        // None of the simple shapes gets past: find a way between the parts, threading the gaps between chips.
        if (bestCovered > 0 && Search(a, b) is { } found && Covered(found) < bestCovered) return found;
        return best;
    }

    // ───────────────────────────── the search ─────────────────────────────

    private const double Pitch = 0.5;
    private bool[,]? _blocked;
    private double _left, _top;
    private int _columns, _rows;

    /// <summary>The board, and the bench beside it, as a grid of half-pitch cells: blocked where a part is.</summary>
    private void Grid()
    {
        if (_blocked is not null) return;
        _left = -30;
        _top = -2;
        var right = Breadboard.Width + 2;
        var bottom = Breadboard.Top(_boards) + Breadboard.Height + 2;
        _columns = (int)Math.Ceiling((right - _left) / Pitch) + 1;
        _rows = (int)Math.Ceiling((bottom - _top) / Pitch) + 1;
        _blocked = new bool[_columns, _rows];
        for (var i = 0; i < _columns; i++)
            for (var j = 0; j < _rows; j++)
                _blocked[i, j] = Blocked(_left + (i * Pitch), _top + (j * Pitch));
    }

    /// <summary>
    /// A* over the grid from one end to the other, then pulled tight: from each point, straight on to the furthest
    /// point it can reach without crossing a part. Null when the ends are walled in.
    /// </summary>
    private List<(double X, double Y)>? Search((double X, double Y) a, (double X, double Y) b)
    {
        Grid();
        var blocked = _blocked!;
        (int I, int J) Cell((double X, double Y) p) =>
            (Math.Clamp((int)Math.Round((p.X - _left) / Pitch), 0, _columns - 1), Math.Clamp((int)Math.Round((p.Y - _top) / Pitch), 0, _rows - 1));
        var (start, goal) = (Cell(a), Cell(b));
        bool Open(int i, int j) =>
            i >= 0 && j >= 0 && i < _columns && j < _rows
            && (!blocked[i, j] || Math.Abs(i - start.I) + Math.Abs(j - start.J) <= 2 || Math.Abs(i - goal.I) + Math.Abs(j - goal.J) <= 2);

        // Costs and back-links in arrays shared by every search, a generation number saying which entries are this one's.
        var size = _columns * _rows;
        if (_cost.Length != size)
        {
            _cost = new double[size];
            _from = new int[size];
            _seen = new int[size];
        }

        var generation = ++_generation;
        var startKey = Key(start);
        var goalKey = Key(goal);
        _cost[startKey] = 0;
        _seen[startKey] = generation;
        var queue = new PriorityQueue<(int I, int J), double>();
        queue.Enqueue(start, 0);
        var found = false;
        while (queue.TryDequeue(out var cell, out var priority))
        {
            var cellKey = Key(cell);
            if (cellKey == goalKey) { found = true; break; }
            var here = _cost[cellKey];
            var heuristic = Math.Sqrt(((cell.I - goal.I) * (cell.I - goal.I)) + ((cell.J - goal.J) * (cell.J - goal.J)));
            if (priority > here + heuristic + 1e-9) continue;
            for (var di = -1; di <= 1; di++)
                for (var dj = -1; dj <= 1; dj++)
                {
                    if (di == 0 && dj == 0) continue;
                    var (ni, nj) = (cell.I + di, cell.J + dj);
                    if (!Open(ni, nj)) continue;
                    // No squeezing diagonally between two blocked cells.
                    if (di != 0 && dj != 0 && (!Open(cell.I + di, cell.J) || !Open(cell.I, cell.J + dj))) continue;
                    var next = here + (di != 0 && dj != 0 ? 1.4142 : 1.0);
                    var key = Key((ni, nj));
                    if (_seen[key] == generation && _cost[key] <= next) continue;
                    _seen[key] = generation;
                    _cost[key] = next;
                    _from[key] = cellKey;
                    var h = Math.Sqrt(((ni - goal.I) * (ni - goal.I)) + ((nj - goal.J) * (nj - goal.J)));
                    queue.Enqueue((ni, nj), next + h);
                }
        }

        if (!found) return null;

        var cells = new List<(double X, double Y)>();
        for (var k = goalKey; ; k = _from[k])
        {
            cells.Add((_left + (k % _columns * Pitch), _top + (k / _columns * Pitch)));
            if (k == startKey) break;
        }

        cells.Reverse();
        cells[0] = a;
        cells[^1] = b;
        return Tighten(cells);
    }

    private int Key((int I, int J) cell) => (cell.J * _columns) + cell.I;

    private double[] _cost = [];
    private int[] _from = [];
    private int[] _seen = [];
    private int _generation;

    /// <summary>The grid path pulled tight, keeping only the corners it needs: each leg runs on for as long as it stays clear.</summary>
    private List<(double X, double Y)> Tighten(List<(double X, double Y)> cells)
    {
        var path = new List<(double X, double Y)> { cells[0] };
        var at = 0;
        while (at < cells.Count - 1)
        {
            var reach = at + 1;
            while (reach + 1 < cells.Count && Covered([cells[at], cells[reach + 1]]) == 0) reach++;
            path.Add(cells[reach]);
            at = reach;
        }

        return path;
    }

    /// <summary>The clear horizontal lanes near a wire's ends: each board's channel and rail gaps, and the gaps between boards.</summary>
    private IEnumerable<double> Lanes(double y1, double y2)
    {
        var (low, high) = (Math.Min(y1, y2) - 4, Math.Max(y1, y2) + 4);
        for (var board = 1; board <= _boards; board++)
        {
            var top = Breadboard.Top(board);
            foreach (var y in new[] { top + 3.4, top + 9.9, top + 16.0, top + Breadboard.Height + (Breadboard.Gap / 2) })
                if (y >= low && y <= high) yield return y;
        }
    }

    private static List<(double X, double Y)> Bowed((double X, double Y) a, (double X, double Y) b, double bow)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        if (length < 1e-9) return [a, b];
        var (nx, ny) = (-dy / length * bow, dx / length * bow);
        var (c1, c2) = ((a.X + (dx * 0.25) + nx, a.Y + (dy * 0.25) + ny), (a.X + (dx * 0.75) + nx, a.Y + (dy * 0.75) + ny));
        var points = new List<(double X, double Y)>();
        const int count = 16;
        for (var i = 0; i <= count; i++)
        {
            var t = (double)i / count;
            var u = 1 - t;
            points.Add((
                (u * u * u * a.X) + (3 * u * u * t * c1.Item1) + (3 * u * t * t * c2.Item1) + (t * t * t * b.X),
                (u * u * u * a.Y) + (3 * u * u * t * c1.Item2) + (3 * u * t * t * c2.Item2) + (t * t * t * b.Y)));
        }

        return points;
    }

    private static double Length(IReadOnlyList<(double X, double Y)> path)
    {
        var total = 0.0;
        for (var i = 1; i < path.Count; i++)
            total += Math.Sqrt(((path[i].X - path[i - 1].X) * (path[i].X - path[i - 1].X)) + ((path[i].Y - path[i - 1].Y) * (path[i].Y - path[i - 1].Y)));
        return total;
    }

    /// <summary>Sharp corners: a curve's many small turns don't count, a dog-leg's do.</summary>
    private static int Bends(IReadOnlyList<(double X, double Y)> path) => path.Count <= 5 ? path.Count - 2 : 0;
}
