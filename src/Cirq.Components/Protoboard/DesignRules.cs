using System.Globalization;
using Cirq.Core.Topology;

namespace Cirq.Components.Protoboard;

/// <summary>A board house's limits, in millimetres: what it can etch, drill and plate.</summary>
/// <param name="Name">What the rules are, as the menu says it.</param>
/// <param name="MinTrace">The narrowest trace.</param>
/// <param name="MinClearance">The narrowest gap between two nets' copper.</param>
/// <param name="MinDrill">The smallest hole.</param>
/// <param name="MinAnnularRing">The narrowest ring of copper round a hole.</param>
/// <param name="MaxWidth">The widest board, or null for no limit.</param>
/// <param name="MaxHeight">The tallest board, or null for no limit.</param>
public sealed record PcbRules(string Name, double MinTrace, double MinClearance, double MinDrill, double MinAnnularRing, double? MaxWidth = null, double? MaxHeight = null)
{
    /// <summary>What a typical online board house makes as standard: 5 mil traces and gaps, 0.3 mm holes.</summary>
    public static PcbRules BoardHouse { get; } = new("Board house (standard)", 0.127, 0.127, 0.3, 0.13);

    /// <summary>The same, held to the 100 × 100 mm that the cheapest price is for.</summary>
    public static PcbRules Budget { get; } = new("Board house (budget, 100 × 100 mm)", 0.127, 0.127, 0.3, 0.13, 100, 100);

    /// <summary>What toner transfer and a ferric chloride bath can do at home.</summary>
    public static PcbRules HomeEtching { get; } = new("Home etching", 0.3, 0.3, 0.6, 0.3);

    public static IReadOnlyList<PcbRules> All { get; } = [BoardHouse, Budget, HomeEtching];
}

/// <summary>One thing a design rule check found: which rule, in a sentence, and where (in pitches, on the drawing).</summary>
public sealed record DrcIssue(string Rule, string Message, double X, double Y);

/// <summary>
/// The design rule check: a PCB's copper and holes held against a board house's limits before its Gerber files go out.
/// <para>
/// It checks the sizes (every trace's width, every hole's drill, the ring of copper each pad and via leaves round its
/// hole, the board's size) and the gaps: between every two pieces of copper of different nets on the same layer, pads
/// and vias (on both layers, being plated through) and traces, measured edge to edge. The ground pour is kept
/// <see cref="GroundPour.Clearance"/> from other nets by construction, so it is reported once if the rules ask more.
/// </para>
/// </summary>
public static class DesignRules
{
    /// <summary>Checks <paramref name="layout"/> against <paramref name="rules"/>; empty when it passes.</summary>
    public static IReadOnlyList<DrcIssue> Check(ProtoboardLayout layout, PcbRules rules)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(rules);
        if (!layout.Kind.IsPcb()) throw new ArgumentException("A design rule check is for a printed circuit board.", nameof(layout));
        var issues = new List<DrcIssue>();
        var pitch = PcbRouter.Pitch;
        string Mm(double v) => v.ToString("0.###", CultureInfo.InvariantCulture) + " mm";

        // ---- sizes ----
        var (width, height) = (SolderedLayout.Width(layout.Columns) * pitch, SolderedLayout.Height(layout.Rows) * pitch);
        if (rules.MaxWidth is { } mw && rules.MaxHeight is { } mh && !(width <= mw && height <= mh) && !(width <= mh && height <= mw))
            issues.Add(new("Board size", $"The board is {Mm(width)} × {Mm(height)}, bigger than {Mm(mw)} × {Mm(mh)}.", 0, 0));
        foreach (var trace in layout.Wires.Where(w => w.IsTrace))
        {
            var w = TraceWidth(trace);
            if (w < rules.MinTrace - 1e-9) issues.Add(new("Trace width", $"A {Mm(w)} trace of {ProtoboardLayout.NetName(trace.Net)} is narrower than {Mm(rules.MinTrace)}.", trace.Route[0].X, trace.Route[0].Y));
        }

        if (layout.Parts.Count > 0 && PcbRouter.DrillDiameter < rules.MinDrill - 1e-9)
            issues.Add(new("Drill", $"The {Mm(PcbRouter.DrillDiameter)} holes are smaller than {Mm(rules.MinDrill)}.", 0, 0));
        if (layout.Vias.Count > 0 && PcbRouter.ViaDrill < rules.MinDrill - 1e-9)
            issues.Add(new("Drill", $"The {Mm(PcbRouter.ViaDrill)} via holes are smaller than {Mm(rules.MinDrill)}.", 0, 0));
        if ((PcbRouter.PadDiameter - PcbRouter.DrillDiameter) / 2 < rules.MinAnnularRing - 1e-9)
            issues.Add(new("Annular ring", $"The pads leave {Mm((PcbRouter.PadDiameter - PcbRouter.DrillDiameter) / 2)} of copper round their holes, less than {Mm(rules.MinAnnularRing)}.", 0, 0));
        if (layout.Vias.Count > 0 && (PcbRouter.ViaDiameter - PcbRouter.ViaDrill) / 2 < rules.MinAnnularRing - 1e-9)
            issues.Add(new("Annular ring", $"The vias leave {Mm((PcbRouter.ViaDiameter - PcbRouter.ViaDrill) / 2)} of copper round their holes, less than {Mm(rules.MinAnnularRing)}.", 0, 0));
        if (layout.Pour.Count > 0 && GroundPour.Clearance < rules.MinClearance - 1e-9)
            issues.Add(new("Clearance", $"The ground pour keeps {Mm(GroundPour.Clearance)} from other nets, less than {Mm(rules.MinClearance)}: turn the pour off for these rules.", 0, 0));

        // ---- gaps, edge to edge, between different nets' copper on a layer ----
        var copper = Copper(layout);
        var buckets = new Dictionary<(int, int), List<int>>();
        const double cell = 2.0;
        for (var i = 0; i < copper.Count; i++)
        {
            var c = copper[i];
            var reach = c.Radius / pitch;
            for (var bx = (int)Math.Floor((Math.Min(c.A.X, c.B.X) - reach) / cell); bx <= (int)Math.Floor((Math.Max(c.A.X, c.B.X) + reach) / cell); bx++)
                for (var by = (int)Math.Floor((Math.Min(c.A.Y, c.B.Y) - reach) / cell); by <= (int)Math.Floor((Math.Max(c.A.Y, c.B.Y) + reach) / cell); by++)
                {
                    if (!buckets.TryGetValue((bx, by), out var list)) buckets[(bx, by)] = list = [];
                    list.Add(i);
                }
        }

        var seen = new HashSet<(int, int)>();
        var reported = new HashSet<(int, int)>();
        foreach (var list in buckets.Values)
            for (var i = 0; i < list.Count; i++)
                for (var j = i + 1; j < list.Count; j++)
                {
                    var (a, b) = (Math.Min(list[i], list[j]), Math.Max(list[i], list[j]));
                    if (!seen.Add((a, b))) continue;
                    var (p, q) = (copper[a], copper[b]);
                    if (ReferenceEquals(p.Net, q.Net) || (p.Layer >= 0 && q.Layer >= 0 && p.Layer != q.Layer)) continue;
                    var gap = (SegmentDistance(p.A, p.B, q.A, q.B) * pitch) - p.Radius - q.Radius;
                    if (gap >= rules.MinClearance - 1e-6) continue;

                    // One report for each place, not one for each pair of segments meeting there.
                    var (mx, my) = Nearest(p, q);
                    if (!reported.Add(((int)Math.Round(mx * 2), (int)Math.Round(my * 2)))) continue;
                    issues.Add(new("Clearance", gap <= 1e-6
                        ? $"{Name(p)} and {Name(q)} touch."
                        : $"{Name(p)} and {Name(q)} are {Mm(Math.Max(0, gap))} apart, less than {Mm(rules.MinClearance)}.", mx, my));
                }

        return issues;
    }

    /// <summary>A sentence for what the check found, for the status bar.</summary>
    public static string Summary(IReadOnlyList<DrcIssue> issues, PcbRules rules)
    {
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(rules);
        if (issues.Count == 0) return $"Design rules ({rules.Name}): the board passes.";
        var kinds = issues.GroupBy(i => i.Rule).Select(g => $"{g.Count()} {g.Key.ToLowerInvariant()}");
        return $"Design rules ({rules.Name}): {issues.Count} problem{(issues.Count == 1 ? "" : "s")}, {string.Join(", ", kinds)}. They're ringed in red on the board.";
    }

    private static double TraceWidth(BoardWire trace) => trace.Role == WireRole.Signal ? PcbRouter.SignalWidth : PcbRouter.PowerWidth;

    private readonly record struct Piece(Net? Net, int Layer, (double X, double Y) A, (double X, double Y) B, double Radius, string What);

    private static string Name(Piece p) => $"{p.What} of {(p.Net is { } n ? ProtoboardLayout.NetName(n) : "no net")}";

    private static List<Piece> Copper(ProtoboardLayout layout)
    {
        var pieces = new List<Piece>();
        var seen = new HashSet<BoardHole>();
        foreach (var part in layout.Parts)
            for (var i = 0; i < part.Holes.Count; i++)
                if (seen.Add(part.Holes[i]))
                {
                    var at = SolderedLayout.Position(part.Holes[i]);
                    layout.HoleNets.TryGetValue(part.Holes[i], out var net);
                    pieces.Add(new(net, -1, at, at, PcbRouter.PadDiameter / 2, $"{part.Part.Name}'s pad"));
                }

        foreach (var via in layout.Vias)
            if (seen.Add(via))
            {
                var at = SolderedLayout.Position(via);
                pieces.Add(new(layout.HoleNets.GetValueOrDefault(via), -1, at, at, PcbRouter.ViaDiameter / 2, "a via"));
            }

        foreach (var wire in layout.Wires)
        {
            if (wire.IsTrace)
                for (var i = 1; i < wire.Route.Count; i++)
                    pieces.Add(new(wire.Net, wire.Layer, wire.Route[i - 1], wire.Route[i], TraceWidth(wire) / 2, "a trace"));
            else
                foreach (var end in new[] { wire.From, wire.To })
                    if (seen.Add(end))
                    {
                        var at = SolderedLayout.Position(end);
                        pieces.Add(new(wire.Net, -1, at, at, PcbRouter.PadDiameter / 2, "a jumper's pad"));
                    }
        }

        return pieces;
    }

    private static (double X, double Y) Nearest(Piece p, Piece q)
    {
        var best = (p.A, q.A);
        var lowest = double.MaxValue;
        foreach (var (x, a, b) in new[] { (p.A, q.A, q.B), (p.B, q.A, q.B), (q.A, p.A, p.B), (q.B, p.A, p.B) })
        {
            var d = GroundPour.Distance(x, a, b);
            if (d < lowest) { lowest = d; best = (x, Closest(x, a, b)); }
        }

        return ((best.Item1.X + best.Item2.X) / 2, (best.Item1.Y + best.Item2.Y) / 2);
    }

    private static (double X, double Y) Closest((double X, double Y) p, (double X, double Y) a, (double X, double Y) b)
    {
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length = (dx * dx) + (dy * dy);
        var t = length < 1e-12 ? 0 : Math.Clamp((((p.X - a.X) * dx) + ((p.Y - a.Y) * dy)) / length, 0, 1);
        return (a.X + (t * dx), a.Y + (t * dy));
    }

    /// <summary>The distance between two segments (zero where they cross).</summary>
    public static double SegmentDistance((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
    {
        if (Crosses(a, b, c, d)) return 0;
        return Math.Min(Math.Min(GroundPour.Distance(a, c, d), GroundPour.Distance(b, c, d)), Math.Min(GroundPour.Distance(c, a, b), GroundPour.Distance(d, a, b)));
    }

    private static bool Crosses((double X, double Y) a, (double X, double Y) b, (double X, double Y) c, (double X, double Y) d)
    {
        static double Cross((double X, double Y) o, (double X, double Y) p, (double X, double Y) q) => ((p.X - o.X) * (q.Y - o.Y)) - ((p.Y - o.Y) * (q.X - o.X));
        var (d1, d2, d3, d4) = (Cross(c, d, a), Cross(c, d, b), Cross(a, b, c), Cross(a, b, d));
        return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
    }
}
