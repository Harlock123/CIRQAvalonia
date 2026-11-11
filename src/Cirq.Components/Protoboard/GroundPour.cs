using Cirq.Core.Topology;

namespace Cirq.Components.Protoboard;

/// <summary>A rectangle of a ground pour on one copper layer (0 the back, 1 the component side), in pitches.</summary>
public sealed record PourArea(int Layer, double Left, double Top, double Right, double Bottom);

/// <summary>
/// A ground pour: the free copper of a printed circuit board's layer filled and joined to ground, kept clear of every
/// other net by <see cref="Clearance"/>.
/// <para>
/// It is worked out on a raster an eighth of the hole pitch across (0.32 mm). Every other net's pads, traces and vias are
/// painted out with their own half-width and the clearance, and so is a margin round the board's edge; ground's own
/// copper is painted in as the seed. The pour is then flooded out from the seed through what's left, so only copper
/// that ground reaches is poured: no floating islands. Ground's pads join the pour solidly (no thermal reliefs). Which of
/// ground's pads share a piece of pour is worked out too, layer by layer, a pad being on both layers, so the router can
/// leave those joins to the pour.
/// </para>
/// </summary>
public static class GroundPour
{
    /// <summary>The gap kept between the pour and any other net's copper, in millimetres.</summary>
    public const double Clearance = 0.3;

    /// <summary>The margin kept clear inside the board's edge, in millimetres.</summary>
    public const double EdgeMargin = 0.5;

    private const int PerPitch = 8;

    /// <summary>A piece of copper: a pad (a dot) or a trace's segment, of a net, on a layer (-1: every layer, as a pad or via is).</summary>
    public readonly record struct Copper(Net? Net, int Layer, (double X, double Y) From, (double X, double Y) To, double Width);

    /// <summary>What the pour came to: its rectangles, and which of ground's pads it joins.</summary>
    public sealed record Result(IReadOnlyList<PourArea> Areas, IReadOnlyList<(BoardHole A, BoardHole B)> Joins);

    /// <summary>
    /// Pours ground over a board <paramref name="width"/> by <paramref name="height"/> pitches with
    /// <paramref name="layers"/> copper layers, round <paramref name="copper"/>; <paramref name="groundPads"/> are where
    /// ground's pads are, for the joins.
    /// </summary>
    public static Result Pour(double width, double height, int layers, IReadOnlyList<Copper> copper, IReadOnlyList<BoardHole> groundPads)
    {
        ArgumentNullException.ThrowIfNull(copper);
        ArgumentNullException.ThrowIfNull(groundPads);
        var nx = (int)Math.Ceiling(width * PerPitch);
        var ny = (int)Math.Ceiling(height * PerPitch);
        var cell = 1.0 / PerPitch;
        var pitch = PcbRouter.Pitch;
        var slack = cell * 0.71;   // a cell's corner beyond its centre, so the clearance holds at the corners too
        var areas = new List<PourArea>();
        var parent = groundPads.Distinct().ToDictionary(p => p, p => p);
        BoardHole Find(BoardHole p) => parent[p] == p ? p : parent[p] = Find(parent[p]);

        for (var layer = 0; layer < layers; layer++)
        {
            // 0 free, 1 kept clear, 2 ground's own copper.
            var grid = new byte[nx * ny];
            var margin = (EdgeMargin / pitch) + slack;
            for (var y = 0; y < ny; y++)
                for (var x = 0; x < nx; x++)
                {
                    var (cx, cy) = ((x + 0.5) * cell, (y + 0.5) * cell);
                    if (cx < margin || cy < margin || cx > width - margin || cy > height - margin) grid[(y * nx) + x] = 1;
                }

            foreach (var piece in copper.Where(c => c.Layer < 0 || c.Layer == layer))
            {
                var ground = piece.Net is { IsGround: true };
                var reach = (piece.Width / 2 / pitch) + (ground ? 0 : (Clearance / pitch) + slack);
                Paint(grid, nx, ny, cell, piece.From, piece.To, reach, ground ? (byte)2 : (byte)1);
            }

            // Flood from ground's copper through the free cells.
            var poured = new bool[nx * ny];
            for (var i = 0; i < grid.Length; i++)
                if (grid[i] == 2) poured[i] = true;
            var component = new int[nx * ny];
            Array.Fill(component, -1);
            var label = 0;
            for (var i = 0; i < grid.Length; i++)
            {
                if (grid[i] != 2 || component[i] >= 0) continue;
                var stack = new Stack<int>();
                stack.Push(i);
                component[i] = label;
                while (stack.Count > 0)
                {
                    var c = stack.Pop();
                    var (x, y) = (c % nx, c / nx);
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        var (px, py) = (x + dx, y + dy);
                        if (px < 0 || py < 0 || px >= nx || py >= ny) continue;
                        var n = (py * nx) + px;
                        if (component[n] >= 0 || grid[n] == 1) continue;
                        component[n] = label;
                        poured[n] = true;
                        stack.Push(n);
                    }
                }

                label++;
            }

            // Ground's pads that land in the same piece are joined by it.
            var byPiece = new Dictionary<int, BoardHole>();
            foreach (var pad in parent.Keys.ToList())
            {
                var (px, py) = SolderedLayout.Position(pad);
                var (gx, gy) = ((int)(px * PerPitch), (int)(py * PerPitch));
                if (gx < 0 || gy < 0 || gx >= nx || gy >= ny || component[(gy * nx) + gx] is var piece && piece < 0) continue;
                if (byPiece.TryGetValue(piece, out var first)) parent[Find(pad)] = Find(first);
                else byPiece[piece] = pad;
            }

            // The poured cells as rectangles: each row's runs, merged down while the run below is the same.
            var open = new Dictionary<(int From, int To), int>();
            for (var y = 0; y <= ny; y++)
            {
                var runs = new HashSet<(int, int)>();
                if (y < ny)
                    for (var x = 0; x < nx;)
                    {
                        if (!poured[(y * nx) + x]) { x++; continue; }
                        var start = x;
                        while (x < nx && poured[(y * nx) + x]) x++;
                        runs.Add((start, x));
                    }

                foreach (var (run, top) in open.ToList())
                    if (!runs.Contains(run))
                    {
                        areas.Add(new PourArea(layer, run.From * cell, top * cell, run.To * cell, y * cell));
                        open.Remove(run);
                    }

                foreach (var run in runs) open.TryAdd(run, y);
            }
        }

        var joins = new List<(BoardHole, BoardHole)>();
        foreach (var group in parent.Keys.GroupBy(Find))
        {
            var pads = group.ToList();
            for (var i = 1; i < pads.Count; i++) joins.Add((pads[0], pads[i]));
        }

        return new Result(areas, joins);
    }

    private static void Paint(byte[] grid, int nx, int ny, double cell, (double X, double Y) a, (double X, double Y) b, double reach, byte value)
    {
        var (left, right) = (Math.Min(a.X, b.X) - reach, Math.Max(a.X, b.X) + reach);
        var (top, bottom) = (Math.Min(a.Y, b.Y) - reach, Math.Max(a.Y, b.Y) + reach);
        for (var y = Math.Max(0, (int)(top / cell)); y <= Math.Min(ny - 1, (int)(bottom / cell)); y++)
            for (var x = Math.Max(0, (int)(left / cell)); x <= Math.Min(nx - 1, (int)(right / cell)); x++)
            {
                var i = (y * nx) + x;
                if (grid[i] == 1 && value == 2) continue;   // never pour ground over another net's keep-out
                if (Distance(((x + 0.5) * cell, (y + 0.5) * cell), a, b) <= reach) grid[i] = value;
            }
    }

    /// <summary>The distance from a point to a segment.</summary>
    public static double Distance((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length = (dx * dx) + (dy * dy);
        var t = length < 1e-12 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / length, 0, 1);
        var (qx, qy) = (a.X + (t * dx), a.Y + (t * dy));
        return Math.Sqrt(((p.X - qx) * (p.X - qx)) + ((p.Y - qy) * (p.Y - qy)));
    }
}
