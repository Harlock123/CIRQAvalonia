using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.Core.Probing;
using Cirq.UI.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;

namespace Cirq.UI.Views;

/// <summary>
/// The stepped-transient plot: time across the bottom, and one curve per value of whatever was
/// stepped laid on top of each other.
/// <para>
/// Laying them on one set of axes is the whole point. Running a circuit four times by hand and
/// looking at four separate pictures tells you much less than the four together do, because what
/// you are looking for is the difference between them.
/// </para>
/// <para>
/// Voltage and current get their own stacked plots when both are present, for the same reason the
/// DC sweep separates them: millivolts and milliamps on one axis is a plot that has to be decoded
/// rather than read.
/// </para>
/// </summary>
public partial class TransientStepWindow : Window
{
    /// <summary>
    /// A ramp rather than a set of unrelated colours. The curves are a <i>sequence</i> — one per
    /// value, in order — so a legend is not the only thing that should say which is which: cool
    /// at the low end, warm at the high end, and the progression is readable at a glance.
    /// </summary>
    private static readonly uint[] Ramp =
        [0xFF4CC9F0, 0xFF4895EF, 0xFF7B68EE, 0xFFB388EB, 0xFFF72585, 0xFFFF6B6B,
         0xFFFFB703, 0xFF80ED99, 0xFF00D4A0, 0xFFD0D0D0];

    private AvaPlot? _plot;

    public TransientStepWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _plot = this.FindControl<AvaPlot>("Plot");

        DataContextChanged += (_, _) => Attach();
        ActualThemeVariantChanged += (_, _) => Render();

        Attach();
    }

    private void Attach()
    {
        if (DataContext is not TransientStepViewModel model) return;

        model.CurvesChanged -= OnCurvesChanged;
        model.CurvesChanged += OnCurvesChanged;

        // Not run on opening, unlike the DC sweep: this runs the whole circuit once per value, and
        // a window that spends several seconds working the moment it appears — on a duration and a
        // range nobody has chosen yet — would be a poor greeting.
        Render();
    }

    private void OnCurvesChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        if (_plot is null || DataContext is not TransientStepViewModel model) return;

        var kinds = model.Curves.Select(c => c.Kind).Distinct().ToList();
        var split = kinds.Count > 1;
        var plots = split ? kinds.Count : 1;

        _plot.Multiplot.Reset();
        _plot.Multiplot.AddPlots(plots);
        _plot.Multiplot.Layout = new ScottPlot.MultiplotLayouts.Rows();

        for (var i = 0; i < plots; i++)
        {
            Style(_plot.Multiplot.GetPlot(i), "Time (s)",
                split ? AxisLabel(kinds[i]) : (kinds.Count == 1 ? AxisLabel(kinds[0]) : "Value"));
        }

        // Coloured by which pass it came from rather than by the order it was added, so on a
        // circuit with two probes both of a pass's traces share a colour and the family reads as
        // a family.
        var values = model.Curves.Select(c => c.StepValue).Distinct().ToList();

        foreach (var curve in model.Curves)
        {
            var plot = _plot.Multiplot.GetPlot(split ? kinds.IndexOf(curve.Kind) : 0);
            var shade = Ramp[Math.Min(values.IndexOf(curve.StepValue), Ramp.Length - 1)];

            var line = plot.Add.ScatterLine(
                curve.Times.ToArray(), curve.Values.ToArray(), Color.FromARGB(shade));

            line.LineWidth = 1.7f;
            line.MarkerSize = 0;
            line.LegendText = curve.Label;
        }

        for (var i = 0; i < plots; i++)
        {
            var plot = _plot.Multiplot.GetPlot(i);

            if (model.Curves.Count > 1) StyleLegend(plot.ShowLegend(Alignment.LowerRight));
            else plot.HideLegend();
        }

        _plot.Refresh();
    }

    private static string AxisLabel(ProbeKind kind) => kind switch
    {
        ProbeKind.Current => "Current (A)",
        ProbeKind.Logic => "Logic",
        _ => "Voltage (V)",
    };

    private void Style(Plot plot, string bottomLabel, string leftLabel)
    {
        plot.Clear();

        plot.FigureBackground.Color = FromTheme("PlotBackground");
        plot.DataBackground.Color = FromTheme("PlotBackground");
        plot.Grid.MajorLineColor = FromTheme("PlotGrid");
        plot.Axes.Color(FromTheme("PlotAxis"));

        plot.Axes.Bottom.Label.Text = bottomLabel;
        plot.Axes.Bottom.Label.FontSize = 11;
        plot.Axes.Left.Label.Text = leftLabel;
        plot.Axes.Left.Label.FontSize = 11;

        // SI prefixes on both axes: a switching edge is in nanoseconds and a thermal settle is in
        // seconds, and neither reads well in scientific notation.
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = value => Cirq.Core.Units.SiPrefix.Format(value, string.Empty),
        };

        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = value => Cirq.Core.Units.SiPrefix.Format(value, string.Empty),
        };
    }

    private void StyleLegend(Legend legend)
    {
        legend.BackgroundColor = FromTheme("PlotBackground").WithAlpha(0.82);
        legend.FontColor = FromTheme("PlotAxis");
        legend.OutlineColor = FromTheme("PlotGrid");
    }

    private Color FromTheme(string key)
    {
        var colour = Cirq.UI.Services.ThemeManager.ColorFor(this, key);

        return Color.FromARGB(unchecked((uint)(
            (colour.A << 24) | (colour.R << 16) | (colour.G << 8) | colour.B)));
    }
}
