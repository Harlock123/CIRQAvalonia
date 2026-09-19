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
public partial class ScopePanel : UserControl
{
    /// <summary>
    /// Plot colours come from the active theme rather than being fixed, so the scope follows a
    /// light/dark switch along with the rest of the window.
    /// </summary>
    private static Color PlotBackground => FromTheme("PlotBackground");

    private static Color GridColour => FromTheme("PlotGrid");

    private static Color AxisColour => FromTheme("PlotAxis");

    private static Color FromTheme(string key)
    {
        var colour = Cirq.UI.Services.ThemeManager.Color(key);
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
        var start = scope.AutoScroll ? Math.Max(0, latest - window) : 0;
        var scale = ChooseTimeScale(window);

        if (scope.Layout == ScopeLayout.Tiled && visible.Count > 1)
            RenderTiled(scope, visible, start, window, scale);
        else
            RenderSingle(scope, visible, start, window, scale);

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

        for (var i = 0; i < probes.Count; i++)
        {
            var offset = stacked ? (probes.Count - 1 - i) * slot - (probes.Count - 1) * slot / 2.0 : 0.0;
            AddTrace(plot, probes[i], start, window, scale, offset);
        }

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
    private static void StyleLegend(Legend legend)
    {
        legend.BackgroundColor = PlotBackground.WithAlpha(0.82);
        legend.FontColor = FromTheme("TextValue");
        legend.OutlineColor = GridColour;
        legend.ShadowColor = Colors.Transparent;
        legend.FontSize = 11;
    }

    private static void StylePlot(Plot plot, TimeScale scale)
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
