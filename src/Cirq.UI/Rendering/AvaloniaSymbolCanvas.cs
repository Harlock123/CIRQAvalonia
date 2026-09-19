using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Cirq.UI.Rendering;

/// <summary>
/// Draws onto the screen, by forwarding to Avalonia's own drawing context.
/// <para>
/// Every call is a straight pass-through except the geometry, which is replayed into a
/// <see cref="StreamGeometry"/> at the moment it is drawn, and the text, which is measured here
/// because Avalonia is what will be laying out the glyphs.
/// </para>
/// </summary>
public sealed class AvaloniaSymbolCanvas(DrawingContext context) : ISymbolCanvas
{
    public IDisposable PushTransform(Matrix matrix) => context.PushTransform(matrix);

    public void DrawLine(IPen pen, Point p1, Point p2) => context.DrawLine(pen, p1, p2);

    public void DrawEllipse(IBrush? brush, IPen? pen, Point center, double radiusX, double radiusY) =>
        context.DrawEllipse(brush, pen, center, radiusX, radiusY);

    public void DrawRectangle(IBrush? brush, IPen? pen, Rect rect) =>
        context.DrawRectangle(brush, pen, rect);

    public void DrawRectangle(IBrush? brush, IPen? pen, RoundedRect rect) =>
        context.DrawRectangle(brush, pen, rect);

    public void FillRectangle(IBrush brush, Rect rect) => context.FillRectangle(brush, rect);

    public void DrawGeometry(IBrush? brush, IPen? pen, SymbolPath path)
    {
        var geometry = new StreamGeometry();

        using (var sink = geometry.Open())
        {
            path.Replay(new StreamSink(sink));
        }

        context.DrawGeometry(brush, pen, geometry);
    }

    public void DrawText(string text, Point origin, double size, IBrush brush, SymbolTextAlign align)
    {
        if (string.IsNullOrEmpty(text)) return;

        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            CanvasTheme.LabelTypeface, size, brush);

        var x = align switch
        {
            SymbolTextAlign.Left => 0,
            SymbolTextAlign.Right => -formatted.Width,
            _ => -formatted.Width / 2,
        };

        context.DrawText(formatted, new Point(origin.X + x, origin.Y - (formatted.Height / 2)));
    }

    public double MeasureText(string text, double size) =>
        string.IsNullOrEmpty(text)
            ? 0
            : new FormattedText(
                text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                CanvasTheme.LabelTypeface, size, Brushes.Black).Width;

    /// <summary>Pours a recorded path into Avalonia's geometry sink.</summary>
    private sealed class StreamSink(StreamGeometryContext sink) : ISymbolPathSink
    {
        public void BeginFigure(Point start, bool isFilled) => sink.BeginFigure(start, isFilled);

        public void LineTo(Point point) => sink.LineTo(point);

        public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint) =>
            sink.CubicBezierTo(controlPoint1, controlPoint2, endPoint);

        public void ArcTo(
            Point point, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweepDirection) =>
            sink.ArcTo(point, size, rotationAngle, isLargeArc, sweepDirection);

        public void EndFigure(bool isClosed) => sink.EndFigure(isClosed);
    }
}
