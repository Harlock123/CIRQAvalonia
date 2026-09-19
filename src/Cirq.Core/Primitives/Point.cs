namespace Cirq.Core.Primitives;

/// <summary>Framework-independent 2D point used for schematic geometry.</summary>
public readonly record struct Point(double X, double Y)
{
    public static readonly Point Origin = new(0, 0);

    public Point Translate(double dx, double dy) => new(X + dx, Y + dy);

    /// <summary>Rotates the point about the origin by <paramref name="degrees"/> (clockwise in screen space).</summary>
    public Point Rotate(double degrees)
    {
        var rad = degrees * Math.PI / 180.0;
        var c = Math.Cos(rad);
        var s = Math.Sin(rad);
        return new Point(X * c - Y * s, X * s + Y * c);
    }

    public double DistanceTo(Point other)
    {
        var dx = X - other.X;
        var dy = Y - other.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    public override string ToString() => $"({X:0.###}, {Y:0.###})";
}
