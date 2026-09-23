using Avalonia;

namespace Cirq.UI.Rendering;

/// <summary>
/// Where the moving dots go on a wire that is carrying current.
/// <para>
/// A schematic shows what is connected. A scope shows what one point is doing over time. Neither
/// shows the thing a beginner most needs to see, which is that current is <i>going somewhere</i> —
/// that it leaves the supply, divides at a junction in proportion to nothing you can see by
/// looking, and comes back. Putting it on the wires is the fastest way to make a circuit legible
/// to somebody who cannot yet read a trace, and it costs nothing the solver has not already
/// worked out.
/// </para>
/// <para>
/// Kept as arithmetic rather than as drawing so it can be checked without a window: the direction,
/// the speed, and the fact that a marker never leaves the wire it belongs to.
/// </para>
/// </summary>
public static class CurrentFlow
{
    /// <summary>Below this many amps nothing is drawn, because nothing is happening.</summary>
    public const double Floor = 1e-9;

    /// <summary>The current at which the dots run at full speed. Above it they do not go faster.</summary>
    public const double Ceiling = 10.0;

    /// <summary>How far apart the dots sit along a wire, in schematic units.</summary>
    public const double Spacing = 22.0;

    /// <summary>Schematic units a dot covers in a second at full speed.</summary>
    public const double FullSpeed = 90.0;

    /// <summary>
    /// How fast the dots move for a given current, from zero to one, with the sign of the current.
    /// <para>
    /// Logarithmic, and it has to be. Current in a circuit spans decades — a microamp of base
    /// current beside an amp of collector current is ordinary — and on a linear scale the small
    /// one is indistinguishable from stopped. Six decades mapped onto the speed means both are
    /// visibly moving and visibly different, which is the whole job.
    /// </para>
    /// </summary>
    public static double Speed(double amps)
    {
        var magnitude = Math.Abs(amps);

        if (double.IsNaN(magnitude) || magnitude < Floor) return 0.0;

        var decades = Math.Log10(Ceiling / Floor);
        var scaled = Math.Log10(magnitude / Floor) / decades;

        return Math.Sign(amps) * Math.Clamp(scaled, 0.0, 1.0);
    }

    /// <summary>
    /// The dots along one wire at one instant.
    /// <para>
    /// The path is the wire's own elbowed route, so the dots follow the corners instead of cutting
    /// them. They are spaced evenly along its length and all move together — the offset is what
    /// the clock does, and it is taken modulo the spacing so it never grows without bound however
    /// long a circuit is left running.
    /// </para>
    /// </summary>
    /// <param name="path">The wire's points, in order.</param>
    /// <param name="amps">Current through it. The sign decides which way the dots go.</param>
    /// <param name="seconds">A clock, in seconds. Any monotonic one will do.</param>
    public static IReadOnlyList<Point> Dots(IReadOnlyList<Point> path, double amps, double seconds)
    {
        ArgumentNullException.ThrowIfNull(path);

        var speed = Speed(amps);
        if (speed == 0.0 || path.Count < 2) return [];

        List<double> lengths = [];
        var total = 0.0;

        for (var i = 1; i < path.Count; i++)
        {
            var run = Distance(path[i - 1], path[i]);

            lengths.Add(run);
            total += run;
        }

        if (total < Spacing * 0.5) return [];

        // Modulo the spacing, so a circuit left running for an hour is drawing the same dots in
        // the same places as one just started.
        var travelled = seconds * speed * FullSpeed;
        var offset = travelled % Spacing;

        if (offset < 0) offset += Spacing;

        List<Point> dots = [];

        for (var along = offset; along < total; along += Spacing)
            dots.Add(PointAt(path, lengths, along));

        return dots;
    }

    /// <summary>The point a given distance along a polyline.</summary>
    private static Point PointAt(IReadOnlyList<Point> path, List<double> lengths, double along)
    {
        for (var i = 0; i < lengths.Count; i++)
        {
            if (along > lengths[i])
            {
                along -= lengths[i];
                continue;
            }

            var t = lengths[i] <= 0 ? 0 : along / lengths[i];

            return new Point(
                path[i].X + ((path[i + 1].X - path[i].X) * t),
                path[i].Y + ((path[i + 1].Y - path[i].Y) * t));
        }

        return path[^1];
    }

    private static double Distance(Point a, Point b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;

        return Math.Sqrt((dx * dx) + (dy * dy));
    }
}
