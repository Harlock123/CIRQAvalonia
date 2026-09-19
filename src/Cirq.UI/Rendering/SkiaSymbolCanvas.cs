using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using SkiaSharp;

namespace Cirq.UI.Rendering;

/// <summary>
/// Draws onto a Skia canvas, which is what makes an export possible.
/// <para>
/// The same canvas type backs a bitmap, an SVG document and a PDF page, so one implementation
/// covers every format the exporter offers. Nothing here knows which of the three it is drawing
/// into — that is the point.
/// </para>
/// </summary>
public sealed class SkiaSymbolCanvas(SKCanvas canvas) : ISymbolCanvas, IDisposable
{
    private readonly List<SKPaint> _paints = [];

    public IDisposable PushTransform(Matrix matrix)
    {
        var depth = canvas.Save();
        canvas.Concat(ToSkia(matrix));
        return new Scope(canvas, depth);
    }

    public void DrawLine(IPen pen, Point p1, Point p2)
    {
        var paint = Stroke(pen);
        if (paint is null) return;

        canvas.DrawLine((float)p1.X, (float)p1.Y, (float)p2.X, (float)p2.Y, paint);
    }

    public void DrawEllipse(IBrush? brush, IPen? pen, Point center, double radiusX, double radiusY)
    {
        var (cx, cy) = ((float)center.X, (float)center.Y);
        var (rx, ry) = ((float)radiusX, (float)radiusY);

        if (Fill(brush) is { } fill) canvas.DrawOval(cx, cy, rx, ry, fill);
        if (Stroke(pen) is { } stroke) canvas.DrawOval(cx, cy, rx, ry, stroke);
    }

    public void DrawRectangle(IBrush? brush, IPen? pen, Rect rect)
    {
        var box = ToSkia(rect);

        if (Fill(brush) is { } fill) canvas.DrawRect(box, fill);
        if (Stroke(pen) is { } stroke) canvas.DrawRect(box, stroke);
    }

    public void DrawRectangle(IBrush? brush, IPen? pen, RoundedRect rect)
    {
        var box = ToSkia(rect.Rect);

        // Every rounded rectangle in the symbol set uses one uniform radius, so the corner radii
        // are read off the top-left corner rather than being carried through individually.
        var radius = (float)rect.RadiiTopLeft.X;

        if (Fill(brush) is { } fill) canvas.DrawRoundRect(box, radius, radius, fill);
        if (Stroke(pen) is { } stroke) canvas.DrawRoundRect(box, radius, radius, stroke);
    }

    public void FillRectangle(IBrush brush, Rect rect)
    {
        if (Fill(brush) is { } fill) canvas.DrawRect(ToSkia(rect), fill);
    }

    public void DrawGeometry(IBrush? brush, IPen? pen, SymbolPath path)
    {
        using var skPath = new SKPath();
        path.Replay(new PathSink(skPath));

        if (Fill(brush) is { } fill) canvas.DrawPath(skPath, fill);
        if (Stroke(pen) is { } stroke) canvas.DrawPath(skPath, stroke);
    }

    public void DrawText(string text, Point origin, double size, IBrush brush, SymbolTextAlign align)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (Fill(brush) is not { } paint) return;

        using var font = new SKFont(Typeface, (float)size);
        var width = font.MeasureText(text);
        var metrics = font.Metrics;

        var x = align switch
        {
            SymbolTextAlign.Left => 0f,
            SymbolTextAlign.Right => -width,
            _ => -width / 2f,
        };

        // Avalonia positions text from the top-left of its line box, and the renderer offsets by
        // half the line height to centre it vertically. Skia draws from the baseline instead, so
        // the ascent has to be added back or every caption sits half a line high.
        var height = metrics.Descent - metrics.Ascent;
        var baseline = -(height / 2f) - metrics.Ascent;

        canvas.DrawText(text, (float)origin.X + x, (float)origin.Y + baseline, font, paint);
    }

    public double MeasureText(string text, double size) => Measure(text, size);

    /// <summary>
    /// The same measurement without needing a canvas, so a page can be sized before there is
    /// anything to draw on.
    /// </summary>
    public static double Measure(string text, double size)
    {
        if (string.IsNullOrEmpty(text)) return 0;

        using var font = new SKFont(Typeface, (float)size);
        return font.MeasureText(text);
    }

    /// <summary>Height of a line at this size, ascent to descent.</summary>
    public static double LineHeight(double size)
    {
        using var font = new SKFont(Typeface, (float)size);
        return font.Metrics.Descent - font.Metrics.Ascent;
    }

    public void Dispose()
    {
        foreach (var paint in _paints) paint.Dispose();
        _paints.Clear();
    }

    // ---- conversions -----------------------------------------------------

    private static SKMatrix ToSkia(Matrix m) => new(
        (float)m.M11, (float)m.M21, (float)m.M31,
        (float)m.M12, (float)m.M22, (float)m.M32,
        0, 0, 1);

    private static SKRect ToSkia(Rect r) =>
        new((float)r.X, (float)r.Y, (float)r.Right, (float)r.Bottom);

    internal static SKColor ToSkia(Color c) => new(c.R, c.G, c.B, c.A);

    private SKPaint? Fill(IBrush? brush)
    {
        if (brush is not ISolidColorBrush solid) return null;

        var colour = ToSkia(solid.Color);
        var alpha = (byte)Math.Clamp(colour.Alpha * solid.Opacity, 0, 255);

        return Track(new SKPaint
        {
            Color = colour.WithAlpha(alpha),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        });
    }

    private SKPaint? Stroke(IPen? pen)
    {
        if (pen?.Brush is not ISolidColorBrush solid) return null;

        var colour = ToSkia(solid.Color);
        var alpha = (byte)Math.Clamp(colour.Alpha * solid.Opacity, 0, 255);
        var thickness = (float)pen.Thickness;

        var paint = new SKPaint
        {
            Color = colour.WithAlpha(alpha),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = thickness,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
        };

        // Avalonia measures dashes in multiples of the stroke width; Skia wants them in the same
        // units as the coordinates, so they are scaled on the way across.
        if (pen.DashStyle?.Dashes is { Count: > 0 } dashes)
        {
            var intervals = dashes.Select(d => (float)(d * thickness)).ToArray();

            // Skia needs an even number of intervals, on then off.
            if (intervals.Length % 2 == 1) intervals = [.. intervals, .. intervals];

            paint.PathEffect = SKPathEffect.CreateDash(intervals, (float)(pen.DashStyle.Offset * thickness));
        }

        return Track(paint);
    }

    private SKPaint Track(SKPaint paint)
    {
        _paints.Add(paint);
        return paint;
    }

    // ---- text ------------------------------------------------------------

    private static SKTypeface? _typeface;

    /// <summary>
    /// Inter, the same face the canvas draws with, loaded out of the font package rather than
    /// looked up by name — the export has to come out the same on a machine that has never heard
    /// of Inter, which is most of them.
    /// </summary>
    private static SKTypeface Typeface => _typeface ??= LoadInter();

    private static SKTypeface LoadInter()
    {
        try
        {
            using var stream = AssetLoader.Open(
                new Uri("avares://Avalonia.Fonts.Inter/Assets/Inter-Regular.ttf"));

            if (SKTypeface.FromStream(stream) is { } loaded) return loaded;
        }
        catch (Exception)
        {
            // Fall through to whatever the platform will give us rather than failing an export
            // over a font.
        }

        return SKTypeface.FromFamilyName("sans-serif") ?? SKTypeface.Default;
    }

    // ---- helpers ---------------------------------------------------------

    /// <summary>Pours a recorded path into a Skia path.</summary>
    private sealed class PathSink(SKPath path) : ISymbolPathSink
    {
        private SKPoint _start;

        public void BeginFigure(Point start, bool isFilled)
        {
            _start = new SKPoint((float)start.X, (float)start.Y);
            path.MoveTo(_start);
        }

        public void LineTo(Point point) => path.LineTo((float)point.X, (float)point.Y);

        public void CubicBezierTo(Point controlPoint1, Point controlPoint2, Point endPoint) =>
            path.CubicTo(
                (float)controlPoint1.X, (float)controlPoint1.Y,
                (float)controlPoint2.X, (float)controlPoint2.Y,
                (float)endPoint.X, (float)endPoint.Y);

        public void ArcTo(
            Point point, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweepDirection) =>
            path.ArcTo(
                new SKPoint((float)size.Width, (float)size.Height),
                (float)rotationAngle,
                isLargeArc ? SKPathArcSize.Large : SKPathArcSize.Small,
                sweepDirection == SweepDirection.Clockwise
                    ? SKPathDirection.Clockwise
                    : SKPathDirection.CounterClockwise,
                new SKPoint((float)point.X, (float)point.Y));

        public void EndFigure(bool isClosed)
        {
            if (isClosed) path.Close();
        }
    }

    private sealed class Scope(SKCanvas canvas, int depth) : IDisposable
    {
        public void Dispose() => canvas.RestoreToCount(depth);
    }
}
