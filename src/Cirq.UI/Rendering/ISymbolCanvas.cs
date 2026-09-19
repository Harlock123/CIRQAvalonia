using Avalonia;
using Avalonia.Media;

namespace Cirq.UI.Rendering;

/// <summary>Where a piece of text sits relative to the point it is drawn at.</summary>
public enum SymbolTextAlign
{
    /// <summary>Centred on the point, horizontally and vertically.</summary>
    Centre,

    /// <summary>Starting at the point, so a column of labels lines up on its left edge.</summary>
    Left,

    /// <summary>Ending at the point, for the right-hand column of a package's pins.</summary>
    Right,
}

/// <summary>
/// The drawing operations a schematic symbol needs, and nothing else.
/// <para>
/// <see cref="SymbolRenderer"/> used to draw straight onto Avalonia's <c>DrawingContext</c>, which
/// meant a symbol could only ever appear on screen. Exporting the same schematic as SVG or PDF
/// needs those calls to land somewhere else, and duplicating a hundred and forty symbols into a
/// second renderer would guarantee the two drifted apart. So the renderer draws against this
/// instead, and the two implementations decide where the ink goes.
/// </para>
/// <para>
/// The method names and argument order deliberately match <c>DrawingContext</c>'s, because that
/// kept the change to the symbols themselves down to their parameter type.
/// </para>
/// </summary>
public interface ISymbolCanvas
{
    /// <summary>Applies a transform until the returned scope is disposed.</summary>
    IDisposable PushTransform(Matrix matrix);

    void DrawLine(IPen pen, Point p1, Point p2);

    void DrawEllipse(IBrush? brush, IPen? pen, Point center, double radiusX, double radiusY);

    void DrawRectangle(IBrush? brush, IPen? pen, Rect rect);

    void DrawRectangle(IBrush? brush, IPen? pen, RoundedRect rect);

    void DrawGeometry(IBrush? brush, IPen? pen, SymbolPath path);

    void FillRectangle(IBrush brush, Rect rect);

    /// <summary>
    /// Draws a single line of text at a constant size. The canvas measures the glyphs itself,
    /// because only it knows what it is drawing them with.
    /// </summary>
    void DrawText(string text, Point origin, double size, IBrush brush, SymbolTextAlign align);

    /// <summary>
    /// How wide that text would be, for the callers that have to lay something out around it —
    /// the parts list sizes its columns from this.
    /// </summary>
    double MeasureText(string text, double size);
}

/// <summary>Receives the segments of a <see cref="SymbolPath"/> as it is replayed.</summary>
/// <remarks>
/// The same five calls Avalonia's <c>StreamGeometryContext</c> offers, so that recording a path
/// reads exactly like building a geometry did.
/// </remarks>
public interface ISymbolPathSink
{
    void BeginFigure(Point start, bool isFilled);

    void LineTo(Point point);

    void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint);

    void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweepDirection);

    void EndFigure(bool isClosed);
}

/// <summary>
/// A recorded path, replayable into whichever backend is drawing.
/// <para>
/// Avalonia's <c>StreamGeometry</c> cannot be read back once it is built — you can write into one
/// and draw it, but not ask it what is in it — so a geometry built for the screen cannot be handed
/// to a PDF writer. Recording the segments instead means one description serves both.
/// </para>
/// </summary>
public sealed class SymbolPath
{
    private enum Verb { Begin, Line, Cubic, Arc, End }

    private readonly record struct Step(
        Verb Verb,
        Point A,
        Point B,
        Point C,
        Size Radius,
        double Rotation,
        bool Flag,
        SweepDirection Sweep);

    private readonly List<Step> _steps = [];

    /// <summary>Opens the path for writing, mirroring <c>StreamGeometry.Open</c>.</summary>
    public Recorder Open() => new(this);

    /// <summary>Replays everything recorded into <paramref name="sink"/>, in order.</summary>
    public void Replay(ISymbolPathSink sink)
    {
        foreach (var step in _steps)
        {
            switch (step.Verb)
            {
                case Verb.Begin: sink.BeginFigure(step.A, step.Flag); break;
                case Verb.Line: sink.LineTo(step.A); break;
                case Verb.Cubic: sink.CubicBezierTo(step.A, step.B, step.C); break;
                case Verb.Arc: sink.ArcTo(step.A, step.Radius, step.Rotation, step.Flag, step.Sweep); break;
                case Verb.End: sink.EndFigure(step.Flag); break;
            }
        }
    }

    /// <summary>
    /// A path through a run of points, the replacement for <c>PolylineGeometry</c>. A filled
    /// polyline is closed, which is what makes the arrowheads solid.
    /// </summary>
    public static SymbolPath Polyline(IReadOnlyList<Point> points, bool isFilled)
    {
        var path = new SymbolPath();
        if (points.Count == 0) return path;

        using var ctx = path.Open();
        ctx.BeginFigure(points[0], isFilled);

        for (var i = 1; i < points.Count; i++) ctx.LineTo(points[i]);

        ctx.EndFigure(isFilled);
        return path;
    }

    /// <summary>Writes into a <see cref="SymbolPath"/>; disposing it is what ends the figure block.</summary>
    public sealed class Recorder(SymbolPath path) : ISymbolPathSink, IDisposable
    {
        public void BeginFigure(Point start, bool isFilled) =>
            path._steps.Add(new Step(Verb.Begin, start, default, default, default, 0, isFilled, default));

        public void LineTo(Point point) =>
            path._steps.Add(new Step(Verb.Line, point, default, default, default, 0, false, default));

        public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint) =>
            path._steps.Add(new Step(Verb.Cubic, controlPoint1, controlPoint2, endPoint, default, 0, false, default));

        public void ArcTo(
            Point point, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweepDirection) =>
            path._steps.Add(new Step(Verb.Arc, point, default, default, size, rotationAngle, isLargeArc, sweepDirection));

        public void EndFigure(bool isClosed) =>
            path._steps.Add(new Step(Verb.End, default, default, default, default, 0, isClosed, default));

        public void Dispose()
        {
        }
    }
}
