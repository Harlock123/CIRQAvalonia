using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Cirq.Core.Probing;
using Cirq.UI.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;
using Sample = Cirq.Core.Primitives.DataPoint;

namespace Cirq.UI.Views;

/// <summary>
/// The oscilloscope drawer. A timer pulls snapshots out of the probes' ring buffers and redraws
/// the ScottPlot surface, so the render rate is decoupled from the solver: the engine can be
/// taking millions of time points per second while this repaints at a steady 25 FPS.
/// </summary>
public partial class ScopePanel : UserControl, Cirq.UI.Services.IScopeSource
{
    /// <summary>
    /// Nothing to export until a probe is attached and visible. Checked rather than assumed,
    /// because exporting an empty plot is a worse answer than saying there is nothing there.
    /// </summary>
    public bool HasTraces =>
        _plot is not null && DataContext is ScopeViewModel scope && scope.Probes.Any(p => p.IsVisible);

    /// <summary>
    /// Draws the scope exactly as it stands into whatever canvas the exporter hands over — a
    /// bitmap, an SVG document or a PDF page. ScottPlot draws on Skia too, so the traces come out
    /// as curves in the vector formats rather than as a picture of curves.
    /// </summary>
    public void Render(SkiaSharp.SKCanvas canvas, SkiaSharp.SKRect area)
    {
        if (_plot is null) return;

        var depth = canvas.Save();

        canvas.Translate(area.Left, area.Top);
        _plot.Multiplot.Render(canvas, new PixelRect(area.Width, area.Height));

        canvas.RestoreToCount(depth);
    }

    /// <summary>
    /// Plot colours come from the active theme rather than being fixed, so the scope follows a
    /// light/dark switch along with the rest of the window.
    /// </summary>
    private Color PlotBackground => FromTheme("PlotBackground");

    private Color GridColour => FromTheme("PlotGrid");

    private Color AxisColour => FromTheme("PlotAxis");

    /// <summary>
    /// Resolved against this panel rather than the application. The two are not always the same
    /// answer — see <see cref="Cirq.UI.Services.ThemeManager.BrushFor"/> — and taking the
    /// application's put a white plot inside a dark window.
    /// </summary>
    private Color FromTheme(string key)
    {
        var colour = Cirq.UI.Services.ThemeManager.ColorFor(this, key);
        return Color.FromARGB(unchecked((uint)(
            (colour.A << 24) | (colour.R << 16) | (colour.G << 8) | colour.B)));
    }

    private readonly DispatcherTimer _timer;
    private AvaPlot? _plot;
    private TextBlock? _emptyHint;

    public ScopePanel()
    {
        InitializeComponent();

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(40),
        };
        _timer.Tick += (_, _) => Redraw();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _plot = this.FindControl<AvaPlot>("Plot");
        _emptyHint = this.FindControl<TextBlock>("EmptyHint");
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void Redraw()
    {
        if (_plot is null || DataContext is not ScopeViewModel scope) return;

        var visible = scope.Probes.Where(p => p.IsVisible).ToList();
        if (_emptyHint is not null) _emptyHint.IsVisible = scope.Probes.Count == 0;

        var window = scope.WindowSeconds;
        var latest = scope.Probes.Count == 0 ? 0 : scope.Probes.Max(LastSampleTime);
        // Where the capture starts: the newest samples, or an edge lined up in the same place every
        // repaint so a repeating waveform stands still. See ScopeViewModel.WindowStart.
        var start = scope.WindowStart(latest);
        var scale = ChooseTimeScale(window);

        // Measured before the plot is built, so the cursor lines below are drawn where the
        // readout says they are rather than a frame behind it.
        scope.Measure(start, window);

        if (scope.Layout == ScopeLayout.Xy)
            RenderXy(scope, start, window);
        else if (scope.Layout == ScopeLayout.Tiled && visible.Count > 1)
            RenderTiled(scope, visible, start, window, scale);
        else
            RenderSingle(scope, visible, start, window, scale);

        // Cursors mark instants, and in XY mode the horizontal axis is not time.
        if (scope.ShowCursors && scope.Layout != ScopeLayout.Xy) AddCursors(scope, scale);

        if (scope.IsTriggering && scope.Layout != ScopeLayout.Xy) AddTriggerMarks(scope, scale);

        _lastScale = scale;
        _plot.Refresh();
    }

    private static double LastSampleTime(SignalProbe probe)
    {
        var count = probe.HistoryBuffer.Count;
        return count == 0 ? 0 : probe.HistoryBuffer[count - 1].Time;
    }

    // ---- layouts ---------------------------------------------------------

    private void RenderSingle(
        ScopeViewModel scope, List<SignalProbe> probes, double start, double window, TimeScale scale)
    {
        // Collapse any previous tiled arrangement back to a single set of axes.
        if (_plot!.Multiplot.Count() > 1) _plot.Multiplot.Reset();

        var plot = _plot.Plot;
        plot.Clear();
        StylePlot(plot, scale);
        plot.Axes.Left.Label.Text = SharedAxisLabel(scope.Probes);

        var stacked = scope.Layout == ScopeLayout.Stacked;

        // Fit the range to the data before plotting, so a changed source amplitude is followed
        // rather than running off the top of the screen. Stacked mode is excluded: there the
        // vertical axis is a set of slots, and refitting it would move the traces around.
        if (!stacked) FitVerticalRange(scope, probes, start, window);

        var slot = scope.VoltsPerDivision * 2.0;

        // References first, so a live trace is drawn over its own reference rather than under it.
        // The comparison is between the two, and the one you are working on should be on top.
        foreach (var reference in scope.References)
        {
            var index = probes.FindIndex(p => p.Label == reference.Label);
            if (index < 0) continue;

            var offset = stacked ? (probes.Count - 1 - index) * slot - (probes.Count - 1) * slot / 2.0 : 0.0;

            AddReference(plot, reference, start, window, scale, offset);
        }

        for (var i = 0; i < probes.Count; i++)
        {
            var offset = stacked ? (probes.Count - 1 - i) * slot - (probes.Count - 1) * slot / 2.0 : 0.0;
            AddTrace(plot, probes[i], start, window, scale, offset);
        }

        // Computed traces last and unstacked: they are a different quantity from the ones they
        // were worked out from — a ratio has no volts in it — so a slot on a voltage axis would
        // be a promise the number cannot keep.
        foreach (var computed in scope.Computed)
            AddComputed(plot, scope, computed, start, window, scale);

        plot.Axes.SetLimitsX(start * scale.Factor, (start + window) * scale.Factor);

        // Headroom on both layouts, so a trace at full scale clears the axis frame instead of
        // being drawn over by it.
        if (stacked)
        {
            var span = Math.Max(probes.Count, 1) * slot / 2.0;
            plot.Axes.SetLimitsY(-span * (1 + ScopeViewModel.TraceHeadroom), span * (1 + ScopeViewModel.TraceHeadroom));
        }
        else
        {
            plot.Axes.SetLimitsY(scope.DisplayMinimum, scope.DisplayMaximum);
        }

        if (probes.Count > 1) StyleLegend(plot.ShowLegend(Alignment.UpperRight));
        else plot.HideLegend();
    }

    /// <summary>
    /// Scans the samples that are actually on screen and asks the scope to refit around them.
    /// AC coupling is applied first, so the range follows what is drawn rather than the raw
    /// voltages.
    /// </summary>
    private static void FitVerticalRange(
        ScopeViewModel scope, List<SignalProbe> probes, double start, double window)
    {
        if (!scope.AutoScaleVertical || probes.Count == 0) return;

        var minimum = double.MaxValue;
        var maximum = double.MinValue;
        var end = start + window;

        foreach (var probe in probes)
        {
            var samples = probe.HistoryBuffer.ToArray();
            if (samples.Length < 2) continue;

            var from = FindIndexBefore(samples, start);
            var mean = probe.AcCoupled ? Average(samples, from, samples.Length) : 0.0;

            for (var i = from; i < samples.Length; i++)
            {
                if (samples[i].Time > end) break;
                var value = samples[i].Value - mean;
                minimum = Math.Min(minimum, value);
                maximum = Math.Max(maximum, value);
            }
        }

        if (minimum > maximum) return;

        // A perfectly flat trace has no range of its own; give it a little so it is not a hairline
        // pinned to the centre of the screen.
        if (maximum - minimum < 1e-9)
        {
            var padding = Math.Max(Math.Abs(maximum) * 0.2, 0.5);
            minimum -= padding;
            maximum += padding;
        }

        scope.AutoScaleTo(minimum, maximum);
    }

    private void RenderTiled(
        ScopeViewModel scope, List<SignalProbe> probes, double start, double window, TimeScale scale)
    {
        FitVerticalRange(scope, probes, start, window);

        _plot!.Multiplot.Reset();
        _plot.Multiplot.AddPlots(probes.Count);
        _plot.Multiplot.Layout = new ScottPlot.MultiplotLayouts.Rows();

        for (var i = 0; i < probes.Count; i++)
        {
            var plot = _plot.Multiplot.GetPlot(i);
            plot.Clear();
            StylePlot(plot, scale);
            plot.Axes.Left.Label.Text = probes[i].Unit.Length > 0
                ? $"{probes[i].Label} ({probes[i].Unit})"
                : probes[i].Label;

            // Only the bottom tile carries the time axis label.
            plot.Axes.Bottom.Label.Text = i == probes.Count - 1 ? $"Time ({scale.Unit})" : string.Empty;

            AddTrace(plot, probes[i], start, window, scale, 0);
            plot.Axes.SetLimitsX(start * scale.Factor, (start + window) * scale.Factor);

            plot.Axes.SetLimitsY(scope.DisplayMinimum, scope.DisplayMaximum);
            plot.HideLegend();
        }
    }

    /// <summary>
    /// One trace against another. The horizontal axis is a signal rather than time, so the whole
    /// of the time styling — the decade ticks, the unit in the label, the fitted window — is
    /// replaced rather than adjusted.
    /// </summary>
    private void RenderXy(ScopeViewModel scope, double start, double window)
    {
        if (_plot!.Multiplot.Count() > 1) _plot.Multiplot.Reset();

        var plot = _plot.Plot;
        plot.Clear();

        plot.FigureBackground.Color = PlotBackground;
        plot.DataBackground.Color = PlotBackground;
        plot.Grid.MajorLineColor = GridColour;
        plot.Axes.Color(AxisColour);

        var (horizontal, vertical) = scope.XyPairs();

        plot.Axes.Bottom.Label.Text = Describe(horizontal);
        plot.Axes.Bottom.Label.FontSize = 11;
        plot.Axes.Left.Label.FontSize = 11;
        plot.Axes.Left.Label.Text = vertical.Count == 1 ? Describe(vertical[0]) : "Value";

        // Both axes carry a signal here, so both want engineering notation rather than the
        // time formatting the other layouts use along the bottom.
        static ScottPlot.TickGenerators.NumericAutomatic Engineering() => new()
        {
            LabelFormatter = value => Cirq.Core.Units.SiPrefix.Format(value, string.Empty),
        };

        plot.Axes.Bottom.TickGenerator = Engineering();
        plot.Axes.Left.TickGenerator = Engineering();

        var drew = false;

        foreach (var probe in vertical)
        {
            var (xs, ys) = ScopeViewModel.XySeries(horizontal, probe, start, start + window);
            if (xs.Length == 0) continue;

            var colour = Color.FromARGB(unchecked((uint)(
                (probe.TraceColor.A << 24) | (probe.TraceColor.R << 16) |
                (probe.TraceColor.G << 8) | probe.TraceColor.B)));

            var trace = plot.Add.ScatterLine(xs, ys, colour);
            trace.LineWidth = 1.4f;
            trace.MarkerSize = 0;
            trace.LegendText = probe.Label;

            drew = true;
        }

        if (drew) plot.Axes.AutoScale();

        if (vertical.Count > 1) StyleLegend(plot.ShowLegend(Alignment.UpperRight));
        else plot.HideLegend();
    }

    private static string Describe(SignalProbe probe) =>
        probe.Unit.Length > 0 ? $"{probe.Label} ({probe.Unit})" : probe.Label;

    // ---- cursors ---------------------------------------------------------

    /// <summary>
    /// Draws the two time cursors onto every plot in the current layout.
    /// <para>
    /// Onto every plot, because in tiled mode the tiles share a time axis and a cursor on only
    /// one of them would be useless for the thing cursors are for — lining an event on one trace
    /// up against an event on another.
    /// </para>
    /// </summary>
    private void AddCursors(ScopeViewModel scope, TimeScale scale)
    {
        var count = Math.Max(_plot!.Multiplot.Count(), 1);

        for (var i = 0; i < count; i++)
        {
            var plot = count == 1 ? _plot.Plot : _plot.Multiplot.GetPlot(i);

            AddCursor(plot, scope.CursorA * scale.Factor, "A");
            AddCursor(plot, scope.CursorB * scale.Factor, "B");
        }
    }

    /// <summary>
    /// Where the trigger is looking, drawn on the face: the level across, and the instant it fired
    /// down. Without them a trigger that never fires is indistinguishable from one set to a level
    /// the signal does not reach, which is nearly always what has happened.
    /// </summary>
    private void AddTriggerMarks(ScopeViewModel scope, TimeScale scale)
    {
        var count = Math.Max(_plot!.Multiplot.Count(), 1);

        for (var i = 0; i < count; i++)
        {
            var plot = count == 1 ? _plot.Plot : _plot.Multiplot.GetPlot(i);

            var level = plot.Add.HorizontalLine(scope.TriggerLevel);
            level.Color = FromTheme("PlotAxis");
            level.LineWidth = 1;
            level.LinePattern = LinePattern.Dotted;
            level.Text = "T";

            if (scope.TriggeredAt is not { } at) continue;

            var edge = plot.Add.VerticalLine(at * scale.Factor);
            edge.Color = FromTheme("PlotAxis");
            edge.LineWidth = 1;
            edge.LinePattern = LinePattern.Dotted;
        }
    }

    private void AddCursor(Plot plot, double x, string label)
    {
        var line = plot.Add.VerticalLine(x);

        line.Color = FromTheme("PlotAxis");
        line.LineWidth = 1;
        line.LinePattern = LinePattern.Dashed;
        line.Text = label;
    }

    /// <summary>
    /// Which cursor a pointer press has taken hold of, or null when it has not taken hold of one.
    /// Grabbing is by proximity in pixels rather than in seconds, because a cursor is something
    /// aimed at on screen and the timebase changes what a second is worth there by decades.
    /// </summary>
    private char? _dragging;

    private TimeScale _lastScale = new(1.0, "s");

    private const double GrabPixels = 8.0;

    protected override void OnPointerPressed(Avalonia.Input.PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (_plot is null || DataContext is not ScopeViewModel scope || !scope.ShowCursors) return;

        var time = TimeAt(e, out var pixelsPerSecond);
        if (double.IsNaN(time)) return;

        var toA = Math.Abs(time - scope.CursorA) * pixelsPerSecond;
        var toB = Math.Abs(time - scope.CursorB) * pixelsPerSecond;

        if (Math.Min(toA, toB) > GrabPixels) return;

        _dragging = toA <= toB ? 'A' : 'B';

        // Taken by the cursor, so the plot does not also pan.
        e.Handled = true;
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_dragging is null || DataContext is not ScopeViewModel scope) return;

        var time = TimeAt(e, out _);
        if (double.IsNaN(time)) return;

        if (_dragging == 'A') scope.CursorA = time;
        else scope.CursorB = time;

        e.Handled = true;
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragging = null;
    }

    /// <summary>
    /// Where the pointer is, in seconds, and how many pixels a second is worth there — which is
    /// what the grab test needs. NaN when the pointer is not over the plot.
    /// </summary>
    private double TimeAt(Avalonia.Input.PointerEventArgs e, out double pixelsPerSecond)
    {
        pixelsPerSecond = 0;

        if (_plot is null) return double.NaN;

        var position = e.GetPosition(_plot);
        var plot = _plot.Multiplot.Count() > 1 ? _plot.Multiplot.GetPlot(0) : _plot.Plot;

        var axis = plot.Axes.Bottom;
        var area = plot.RenderManager.LastRender.DataRect;

        if (area.Width <= 0) return double.NaN;

        // The plot's X axis is in whatever unit the timebase chose — milliseconds, microseconds —
        // so the reading has to be divided back out to seconds before it means anything to the
        // view model, which works in seconds throughout.
        var scaled = axis.GetCoordinate((float)(position.X * _plot.DisplayScale), area);

        pixelsPerSecond = area.Width / Math.Max(axis.Range.Span, 1e-30) * _lastScale.Factor;

        return scaled / _lastScale.Factor;
    }

    // ---- trace construction ----------------------------------------------

    private static void AddTrace(
        Plot plot, SignalProbe probe, double start, double window, TimeScale scale, double offset)
    {
        var samples = probe.HistoryBuffer.ToArray();
        if (samples.Length < 2) return;

        // Keep one sample either side of the window so the trace runs to both edges.
        var from = FindIndexBefore(samples, start);
        var to = samples.Length;

        var count = to - from;
        if (count < 2) return;

        var xs = new double[count];
        var ys = new double[count];

        var mean = probe.AcCoupled ? Average(samples, from, to) : 0.0;

        for (var i = 0; i < count; i++)
        {
            var sample = samples[from + i];
            xs[i] = sample.Time * scale.Factor;
            ys[i] = sample.Value - mean + offset;
        }

        var colour = Color.FromARGB(unchecked((uint)(
            (probe.TraceColor.A << 24) | (probe.TraceColor.R << 16) |
            (probe.TraceColor.G << 8) | probe.TraceColor.B)));

        var trace = plot.Add.ScatterLine(xs, ys, colour);
        trace.LineWidth = 1.6f;
        trace.MarkerSize = 0;
        trace.LegendText = probe.Label;
    }

    /// <summary>
    /// Draws a captured trace behind the live ones: the probe's own colour, so it is recognisably
    /// the same signal, but dashed and faded so there is never a question about which is which.
    /// <para>
    /// A reference is not AC coupled even when the probe it came from now is. It is a record of
    /// what was on the screen, and re-processing it with settings that were not in force when it
    /// was taken would make it a different measurement.
    /// </para>
    /// </summary>
    private static void AddReference(
        Plot plot, ReferenceTrace reference, double start, double window, TimeScale scale,
        double offset)
    {
        var samples = reference.Samples;
        if (samples.Count < 2) return;

        var end = start + window;

        List<double> xs = [];
        List<double> ys = [];

        foreach (var sample in samples)
        {
            if (sample.Time < start) continue;
            if (sample.Time > end) break;

            xs.Add(sample.Time * scale.Factor);
            ys.Add(sample.Value + offset);
        }

        if (xs.Count < 2) return;

        var colour = Color.FromARGB(unchecked((uint)(
            (reference.Color.A << 24) | (reference.Color.R << 16) |
            (reference.Color.G << 8) | reference.Color.B)));

        var trace = plot.Add.ScatterLine(xs.ToArray(), ys.ToArray(), colour.WithAlpha(0.45));

        trace.LineWidth = 1.3f;
        trace.MarkerSize = 0;
        trace.LinePattern = LinePattern.Dashed;
        trace.LegendText = reference.LegendText;
    }

    /// <summary>
    /// Draws a trace worked out from the others. Solid like a recorded one, because it is a
    /// measurement rather than a memory, but in a colour no probe uses.
    /// </summary>
    private static void AddComputed(
        Plot plot, ScopeViewModel scope, ComputedTrace computed, double start, double window,
        TimeScale scale)
    {
        var samples = scope.Samples(computed);
        if (samples.Count < 2) return;

        var end = start + window;

        List<double> xs = [];
        List<double> ys = [];

        foreach (var sample in samples)
        {
            if (sample.Time < start) continue;
            if (sample.Time > end) break;

            xs.Add(sample.Time * scale.Factor);

            // NaN is how a division by zero is recorded, and ScottPlot draws a break in the line
            // rather than a spike — which is right, because a gap is what happened.
            ys.Add(sample.Value);
        }

        if (xs.Count < 2) return;

        var colour = Color.FromARGB(unchecked((uint)(
            (computed.Color.A << 24) | (computed.Color.R << 16) |
            (computed.Color.G << 8) | computed.Color.B)));

        var trace = plot.Add.ScatterLine(xs.ToArray(), ys.ToArray(), colour);

        trace.LineWidth = 1.4f;
        trace.MarkerSize = 0;
        trace.LegendText = computed.LegendText;
    }

    /// <summary>Index of the last sample at or before <paramref name="time"/>, via binary search.</summary>
    private static int FindIndexBefore(Sample[] samples, double time)
    {
        var low = 0;
        var high = samples.Length - 1;

        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            if (samples[mid].Time <= time) low = mid;
            else high = mid - 1;
        }

        return Math.Max(0, low);
    }

    private static double Average(Sample[] samples, int from, int to)
    {
        var sum = 0.0;
        for (var i = from; i < to; i++) sum += samples[i].Value;
        return to > from ? sum / (to - from) : 0.0;
    }

    // ---- styling ---------------------------------------------------------

    /// <summary>
    /// The default legend is an opaque light panel, which on a dark scope sits on top of the very
    /// traces it is labelling. Dark and slightly transparent keeps it readable without hiding the
    /// waveform underneath.
    /// </summary>
    private void StyleLegend(Legend legend)
    {
        legend.BackgroundColor = PlotBackground.WithAlpha(0.82);
        legend.FontColor = FromTheme("TextValue");
        legend.OutlineColor = GridColour;
        legend.ShadowColor = Colors.Transparent;
        legend.FontSize = 11;
    }

    private void StylePlot(Plot plot, TimeScale scale)
    {
        plot.FigureBackground.Color = PlotBackground;
        plot.DataBackground.Color = PlotBackground;
        plot.Grid.MajorLineColor = GridColour;
        plot.Axes.Color(AxisColour);
        plot.Axes.Bottom.Label.Text = $"Time ({scale.Unit})";
        plot.Axes.Bottom.Label.FontSize = 11;
        plot.Axes.Left.Label.FontSize = 11;
    }

    /// <summary>
    /// What to call the vertical axis when every trace shares it. Traces of different kinds can
    /// be shown together — it is often the point, a current against the voltage driving it — so
    /// the label names what is actually up there rather than claiming they are all volts.
    /// </summary>
    private static string SharedAxisLabel(IEnumerable<SignalProbe> probes)
    {
        var units = probes.Where(p => p.IsVisible && p.Unit.Length > 0)
                          .Select(p => p.Unit)
                          .Distinct()
                          .OrderBy(u => u)
                          .ToList();

        return units.Count switch
        {
            0 => "Logic",
            1 => units[0] == "A" ? "Amps" : "Volts",
            _ => "Volts / Amps",
        };
    }

    /// <summary>
    /// Picks a display unit for the time axis so ticks read "250 µs" rather than "0.00025".
    /// </summary>
    private static TimeScale ChooseTimeScale(double window) => window switch
    {
        < 1e-6 => new TimeScale(1e9, "ns"),
        < 1e-3 => new TimeScale(1e6, "µs"),
        < 1.0 => new TimeScale(1e3, "ms"),
        _ => new TimeScale(1.0, "s"),
    };

    private readonly record struct TimeScale(double Factor, string Unit);
}
