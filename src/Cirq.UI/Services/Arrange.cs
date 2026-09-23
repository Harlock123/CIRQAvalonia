using Avalonia;
using Cirq.Core.Topology;
using Cirq.UI.Controls;

namespace Cirq.UI.Services;

/// <summary>Which edge or line a selection is lined up on.</summary>
public enum AlignTo
{
    Left,
    HorizontalCentre,
    Right,
    Top,
    VerticalMiddle,
    Bottom,
}

/// <summary>Which way a selection is spread out evenly.</summary>
public enum SpreadAlong
{
    Horizontal,
    Vertical,
}

/// <summary>
/// Lining parts up, and spreading them out evenly.
/// <para>
/// A schematic is read as much as it is solved, and a drawing where the parts are a few pixels
/// out is harder to read than one where they are not — the eye spends effort on the wobble that
/// it should be spending on the circuit. Nudging six parts into a column by hand is also the
/// least interesting work there is.
/// </para>
/// <para>
/// Worked out as new positions rather than applied here, so the arithmetic can be checked without
/// a canvas and so the caller can put the whole move inside one undo step. Six parts jumping into
/// line is one thing that happened, not six.
/// </para>
/// </summary>
public static class Arrange
{
    /// <summary>
    /// Where each part ends up when the selection is lined up.
    /// <para>
    /// Left and right go by the parts' <b>edges</b>, because that is what those words mean; the
    /// centre lines go by their middles. Anything already in the right place is left out of the
    /// result rather than moved to where it already is, so an align that changes nothing produces
    /// no undo step.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<CircuitComponent, (double X, double Y)> Align(
        IReadOnlyList<CircuitComponent> parts, AlignTo to)
    {
        ArgumentNullException.ThrowIfNull(parts);

        // One part is already aligned with itself, and two is the fewest that can disagree.
        if (parts.Count < 2) return Nothing;

        var boxes = parts.ToDictionary(p => p, CircuitCanvas.BoundsOf);

        var target = to switch
        {
            AlignTo.Left => boxes.Values.Min(b => b.Left),
            AlignTo.Right => boxes.Values.Max(b => b.Right),
            AlignTo.HorizontalCentre => boxes.Values.Average(b => b.Center.X),
            AlignTo.Top => boxes.Values.Min(b => b.Top),
            AlignTo.Bottom => boxes.Values.Max(b => b.Bottom),
            _ => boxes.Values.Average(b => b.Center.Y),
        };

        Dictionary<CircuitComponent, (double X, double Y)> moved = [];

        foreach (var part in parts)
        {
            var box = boxes[part];

            // The offset from the part's origin to whichever edge is being lined up, so the part
            // lands with that edge on the target rather than its origin.
            var (x, y) = to switch
            {
                AlignTo.Left => (part.X + (target - box.Left), part.Y),
                AlignTo.Right => (part.X + (target - box.Right), part.Y),
                AlignTo.HorizontalCentre => (part.X + (target - box.Center.X), part.Y),
                AlignTo.Top => (part.X, part.Y + (target - box.Top)),
                AlignTo.Bottom => (part.X, part.Y + (target - box.Bottom)),
                _ => (part.X, part.Y + (target - box.Center.Y)),
            };

            if (Same(x, part.X) && Same(y, part.Y)) continue;

            moved[part] = (x, y);
        }

        return moved;
    }

    /// <summary>
    /// Where each part ends up when the selection is spread out evenly.
    /// <para>
    /// The two outermost parts stay exactly where they are and everything between them is spaced
    /// at equal centres. That is what makes this an operation somebody can reach for without
    /// thinking: it cannot run the drawing away from where it was, because the extremes are
    /// fixed points.
    /// </para>
    /// </summary>
    public static IReadOnlyDictionary<CircuitComponent, (double X, double Y)> Spread(
        IReadOnlyList<CircuitComponent> parts, SpreadAlong along)
    {
        ArgumentNullException.ThrowIfNull(parts);

        // Fewer than three and there is nothing in the middle to space out.
        if (parts.Count < 3) return Nothing;

        var horizontal = along == SpreadAlong.Horizontal;

        var boxes = parts.ToDictionary(p => p, CircuitCanvas.BoundsOf);

        var ordered = parts
            .OrderBy(p => horizontal ? boxes[p].Center.X : boxes[p].Center.Y)
            .ToList();

        var first = Centre(boxes[ordered[0]], horizontal);
        var last = Centre(boxes[ordered[^1]], horizontal);

        var step = (last - first) / (ordered.Count - 1);

        Dictionary<CircuitComponent, (double X, double Y)> moved = [];

        for (var i = 1; i < ordered.Count - 1; i++)
        {
            var part = ordered[i];
            var wanted = first + (step * i);
            var shift = wanted - Centre(boxes[part], horizontal);

            if (Same(shift, 0)) continue;

            moved[part] = horizontal ? (part.X + shift, part.Y) : (part.X, part.Y + shift);
        }

        return moved;
    }

    /// <summary>Rounds a set of moves onto the grid, so lining up does not knock parts off it.</summary>
    public static IReadOnlyDictionary<CircuitComponent, (double X, double Y)> OnGrid(
        IReadOnlyDictionary<CircuitComponent, (double X, double Y)> moves, double grid)
    {
        ArgumentNullException.ThrowIfNull(moves);

        if (grid <= 0) return moves;

        Dictionary<CircuitComponent, (double X, double Y)> snapped = [];

        foreach (var (part, (x, y)) in moves)
        {
            var gx = Math.Round(x / grid) * grid;
            var gy = Math.Round(y / grid) * grid;

            if (Same(gx, part.X) && Same(gy, part.Y)) continue;

            snapped[part] = (gx, gy);
        }

        return snapped;
    }

    private static double Centre(Rect box, bool horizontal) =>
        horizontal ? box.Center.X : box.Center.Y;

    /// <summary>Within a hundredth of a pixel, which no drawing can tell apart.</summary>
    private static bool Same(double a, double b) => Math.Abs(a - b) < 0.01;

    private static readonly Dictionary<CircuitComponent, (double X, double Y)> Nothing = [];
}
