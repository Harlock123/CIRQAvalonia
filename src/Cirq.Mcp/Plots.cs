using Cirq.Core.Probing;
using Cirq.Core.Units;
using Cirq.Engine.Simulation;
using SkiaSharp;
using DataPoint = Cirq.Core.Primitives.DataPoint;

namespace Cirq.Mcp;

/// <summary>
/// The results of a run drawn as pictures for an agent to look at: the traces against time, a Bode plot, a DC sweep.
/// Black on white, with engineering-notation axes.
/// </summary>
/// <remarks>
/// Drawn with SkiaSharp alone rather than ScottPlot. These are drawn on worker threads while the editor's scope is
/// drawing with ScottPlot on the render thread, and nothing ScottPlot keeps between plots should be shared across
/// the two. A few axes and lines need no more than this.
/// </remarks>
public static class Plots
{
    private static readonly SKColor[] Colours =
    [
        new(0x1F, 0x77, 0xB4), new(0xD6, 0x27, 0x28), new(0x2C, 0xA0, 0x2C), new(0xFF, 0x7F, 0x0E),
        new(0x94, 0x67, 0xBD), new(0x8C, 0x56, 0x4B), new(0xE3, 0x77, 0xC2), new(0x17, 0xBE, 0xCF),
    ];

    private static readonly SKColor Ink = new(0x33, 0x3A, 0x44);
    private static readonly SKColor Grid = new(0xE3, 0xE6, 0xEA);

    /// <summary>One trace of a transient run: what it's called, its kind, and its samples.</summary>
    public sealed record Trace(string Label, ProbeKind Kind, IReadOnlyList<DataPoint> Samples)
    {
        public string Unit => Kind switch
        {
            ProbeKind.Voltage or ProbeKind.Differential => "V",
            ProbeKind.Current => "A",
            ProbeKind.Power => "W",
            _ => "",
        };
    }

    /// <summary>One line on a panel: its name, colour and points.</summary>
    private sealed record Line(string Label, SKColor Colour, double[] X, double[] Y);

    /// <summary>
    /// The traces against time, one panel per quantity (volts, amps, watts, logic), stacked, sharing the time axis.
    /// </summary>
    public static byte[] Transient(IReadOnlyList<Trace> traces, int width, int height)
    {
        var groups = traces.Where(t => t.Samples.Count > 0).GroupBy(t => t.Kind == ProbeKind.Logic ? "logic" : t.Unit).ToList();
        if (groups.Count == 0) throw new InvalidOperationException("There's nothing to plot: no probe recorded any samples.");
        var t0 = groups.SelectMany(g => g).Min(t => t.Samples[0].Time);
        var t1 = groups.SelectMany(g => g).Max(t => t.Samples[^1].Time);
        if (!(t1 > t0)) t1 = t0 + 1e-9;
        var colour = 0;
        var timeTicks = Ticks(t0, t1).Select(v => (v, SiPrefix.Format(v, "s", 3))).ToList();
        return Draw(width, height, groups.Count, (canvas, g, area, last) =>
        {
            var unit = groups[g].Key;
            var lines = new List<Line>();
            var row = 0;
            foreach (var trace in groups[g])
            {
                // Logic traces each get a lane of their own, as a logic analyser stacks them.
                var lane = 1.2 * row++;
                lines.Add(new Line(trace.Label, Colours[colour++ % Colours.Length],
                    trace.Samples.Select(s => s.Time).ToArray(),
                    trace.Samples.Select(s => unit == "logic" ? s.Value * 0.8 + lane : s.Value).ToArray()));
            }
            var (y0, y1) = unit == "logic" ? (-0.3, 1.2 * row) : Range(lines.SelectMany(l => l.Y));
            var yTicks = unit == "logic"
                ? groups[g].Select((t, i) => (1.2 * i + 0.4, t.Label)).ToList()
                : Ticks(y0, y1).Select(v => (v, SiPrefix.Format(v, unit, 3))).ToList();
            Panel(canvas, area, (t0, t1), (y0, y1), timeTicks, yTicks, last ? "Time" : null,
                unit switch { "V" => "Volts", "A" => "Amps", "W" => "Watts", "logic" => "Logic", _ => unit }, lines, log: false);
        });
    }

    /// <summary>A Bode plot: gain in dB above, phase in degrees below, against frequency on a log scale.</summary>
    public static byte[] Bode(AcSweepResult result, int width, int height)
    {
        var xs = result.Frequencies.Select(Math.Log10).ToArray();
        var gain = result.Traces.Select((t, i) => new Line(t.Label, Colours[i % Colours.Length], xs, Enumerable.Range(0, xs.Length).Select(t.Decibels).ToArray())).ToList();
        var phase = result.Traces.Select((t, i) => new Line(t.Label, Colours[i % Colours.Length], xs, Enumerable.Range(0, xs.Length).Select(t.Degrees).ToArray())).ToList();
        var first = (int)Math.Ceiling(xs[0] - 1e-9);
        var decades = Enumerable.Range(first, Math.Max(0, (int)Math.Floor(xs[^1] + 1e-9) - first + 1))
            .Select(d => ((double)d, SiPrefix.Format(Math.Pow(10, d), "Hz"))).ToList();
        return Draw(width, height, 2, (canvas, g, area, last) =>
        {
            var lines = g == 0 ? gain : phase;
            var (y0, y1) = Range(lines.SelectMany(l => l.Y));
            Panel(canvas, area, (xs[0], xs[^1]), (y0, y1), decades, Ticks(y0, y1).Select(v => (v, SiPrefix.Format(v, "", 3))).ToList(),
                last ? "Frequency" : null, g == 0 ? "Gain (dB)" : "Phase (°)", lines, log: true, legend: g == 0);
        });
    }

    /// <summary>A DC sweep: each probe against the swept value, one curve per stepped value.</summary>
    public static byte[] DcSweep(DcSweepResult result, int width, int height)
    {
        var xs = result.X.ToArray();
        var lines = new List<Line>();
        foreach (var curve in result.Curves)
            foreach (var trace in curve.Traces)
                lines.Add(new Line(double.IsNaN(curve.StepValue) ? trace.Label : $"{trace.Label} ({result.StepLabel} = {SiPrefix.Format(curve.StepValue)})",
                    Colours[lines.Count % Colours.Length], xs, trace.Values.ToArray()));
        var (x0, x1) = (xs.Min(), xs.Max());
        if (!(x1 > x0)) x1 = x0 + 1;
        var (y0, y1) = Range(lines.SelectMany(l => l.Y));
        return Draw(width, height, 1, (canvas, _, area, _) =>
            Panel(canvas, area, (x0, x1), (y0, y1), Ticks(x0, x1).Select(v => (v, SiPrefix.Format(v, "", 3))).ToList(),
                Ticks(y0, y1).Select(v => (v, SiPrefix.Format(v, "", 3))).ToList(), result.XLabel, "", lines, log: false));
    }

    // ───────────────────────────── drawing ─────────────────────────────

    private delegate void PanelDrawer(SKCanvas canvas, int index, SKRect area, bool last);

    /// <summary>A white picture with <paramref name="panels"/> panels stacked down it, as a PNG.</summary>
    private static byte[] Draw(int width, int height, int panels, PanelDrawer draw)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        const float left = 78, right = 16, top = 14, bottom = 46, gap = 26;
        var each = (height - top - bottom - gap * (panels - 1)) / panels;
        for (var i = 0; i < panels; i++)
        {
            var y = top + i * (each + gap);
            draw(canvas, i, new SKRect(left, y, width - right, y + each), i == panels - 1);
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>One panel: its grid, ticks and labels, its lines clipped to it, and a legend.</summary>
    private static void Panel(
        SKCanvas canvas, SKRect area, (double Min, double Max) x, (double Min, double Max) y,
        IReadOnlyList<(double Value, string Label)> xTicks, IReadOnlyList<(double Value, string Label)> yTicks,
        string? xTitle, string yTitle, IReadOnlyList<Line> lines, bool log, bool legend = true)
    {
        using var font = new SKFont(SKTypeface.Default, 12);
        using var titleFont = new SKFont(SKTypeface.Default, 13) { Embolden = true };
        using var ink = new SKPaint { Color = Ink, IsAntialias = true };
        using var grid = new SKPaint { Color = Grid, StrokeWidth = 1, IsAntialias = true };
        using var frame = new SKPaint { Color = Ink, StrokeWidth = 1, Style = SKPaintStyle.Stroke, IsAntialias = true };
        float X(double v) => area.Left + (float)((v - x.Min) / (x.Max - x.Min) * area.Width);
        float Y(double v) => area.Bottom - (float)((v - y.Min) / (y.Max - y.Min) * area.Height);

        foreach (var (v, label) in xTicks)
        {
            if (v < x.Min - 1e-12 || v > x.Max + 1e-12) continue;
            canvas.DrawLine(X(v), area.Top, X(v), area.Bottom, grid);
            if (xTitle != null) canvas.DrawText(label, X(v), area.Bottom + 16, SKTextAlign.Center, font, ink);
        }
        if (log)
            for (var d = Math.Floor(x.Min); d < x.Max; d++)
                for (var m = 2; m < 10; m++)
                {
                    var v = d + Math.Log10(m);
                    if (v > x.Min && v < x.Max) canvas.DrawLine(X(v), area.Bottom, X(v), area.Bottom - 4, frame);
                }
        foreach (var (v, label) in yTicks)
        {
            if (v < y.Min - 1e-12 || v > y.Max + 1e-12) continue;
            canvas.DrawLine(area.Left, Y(v), area.Right, Y(v), grid);
            canvas.DrawText(label, area.Left - 6, Y(v) + 4, SKTextAlign.Right, font, ink);
        }
        canvas.DrawRect(area, frame);
        if (xTitle != null) canvas.DrawText(xTitle, area.MidX, area.Bottom + 36, SKTextAlign.Center, titleFont, ink);
        if (yTitle.Length > 0)
        {
            canvas.Save();
            canvas.RotateDegrees(-90, 16, area.MidY);
            canvas.DrawText(yTitle, 16, area.MidY + 4, SKTextAlign.Center, titleFont, ink);
            canvas.Restore();
        }

        canvas.Save();
        canvas.ClipRect(area);
        foreach (var line in lines)
        {
            using var pen = new SKPaint { Color = line.Colour, StrokeWidth = 1.6f, Style = SKPaintStyle.Stroke, IsAntialias = true, StrokeJoin = SKStrokeJoin.Round };
            using var path = new SKPath();
            var drawing = false;
            for (var i = 0; i < line.X.Length; i++)
            {
                if (!double.IsFinite(line.Y[i])) { drawing = false; continue; }
                if (drawing) path.LineTo(X(line.X[i]), Y(line.Y[i]));
                else path.MoveTo(X(line.X[i]), Y(line.Y[i]));
                drawing = true;
            }
            canvas.DrawPath(path, pen);
        }
        canvas.Restore();

        if (!legend || lines.Count == 0) return;
        var widest = lines.Max(l => font.MeasureText(l.Label));
        var box = new SKRect(area.Right - widest - 44, area.Top + 6, area.Right - 6, area.Top + 12 + 18 * lines.Count);
        using var paper = new SKPaint { Color = SKColors.White.WithAlpha(235) };
        canvas.DrawRect(box, paper);
        canvas.DrawRect(box, frame);
        for (var i = 0; i < lines.Count; i++)
        {
            var row = box.Top + 15 + 18 * i;
            using var swatch = new SKPaint { Color = lines[i].Colour, StrokeWidth = 2.5f, IsAntialias = true };
            canvas.DrawLine(box.Left + 6, row - 4, box.Left + 26, row - 4, swatch);
            canvas.DrawText(lines[i].Label, box.Left + 32, row, SKTextAlign.Left, font, ink);
        }
    }

    /// <summary>The range of some values with a little room above and below, or around a flat line.</summary>
    private static (double Min, double Max) Range(IEnumerable<double> values)
    {
        var finite = values.Where(double.IsFinite).ToList();
        if (finite.Count == 0) return (-1, 1);
        double lo = finite.Min(), hi = finite.Max();
        if (hi - lo < 1e-12 * Math.Max(1, Math.Abs(hi)))
        {
            var pad = Math.Abs(hi) > 0 ? Math.Abs(hi) * 0.1 : 1;
            return (lo - pad, hi + pad);
        }
        var margin = (hi - lo) * 0.06;
        return (lo - margin, hi + margin);
    }

    /// <summary>Round tick values across a range: steps of 1, 2 or 5 times a power of ten, about six of them.</summary>
    private static List<double> Ticks(double min, double max)
    {
        var span = max - min;
        if (!(span > 0) || !double.IsFinite(span)) return [min];
        var rough = span / 6;
        var power = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        var step = new[] { 1.0, 2, 5, 10 }.Select(m => m * power).First(s => s >= rough);
        var ticks = new List<double>();
        for (var v = Math.Ceiling(min / step) * step; v <= max + step * 1e-9; v += step)
            ticks.Add(Math.Abs(v) < step * 1e-9 ? 0 : v);
        return ticks;
    }
}
