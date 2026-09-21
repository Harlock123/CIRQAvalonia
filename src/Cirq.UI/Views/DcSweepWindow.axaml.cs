using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.Core.Probing;
using Cirq.UI.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;

namespace Cirq.UI.Views;

/// <summary>
/// The DC sweep plot: whatever was swept across the bottom, and what the probes said up the side.
/// <para>
/// Voltage and current curves get their own stacked plots when both are present, for the reason
/// the Bode plot separates magnitude from phase — millivolts and milliamps on one axis is a plot
/// that has to be decoded rather than read. With only one kind of probe there is only one plot and
/// it uses the whole window.
/// </para>
/// </summary>
public partial class DcSweepWindow : Window
{
    private static readonly uint[] Palette =
        [0xFF4CC9F0, 0xFFF72585, 0xFF80ED99, 0xFFFFB703, 0xFFB388EB, 0xFFFF6B6B];

    private AvaPlot? _plot;

    public DcSweepWindow()
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
        if (DataContext is not DcSweepViewModel model) return;

        model.CurvesChanged -= OnCurvesChanged;
        model.CurvesChanged += OnCurvesChanged;

        // Sweep once on opening, so the window arrives with a curve rather than a blank grid.
        model.RunCommand.Execute(null);
    }

    private void OnCurvesChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        if (_plot is null || DataContext is not DcSweepViewModel model) return;

        var kinds = model.Curves.Select(c => c.Kind).Distinct().ToList();
        var split = kinds.Count > 1;

        _plot.Multiplot.Reset();
        _plot.Multiplot.AddPlots(split ? kinds.Count : 1);
        _plot.Multiplot.Layout = new ScottPlot.MultiplotLayouts.Rows();

        for (var i = 0; i < (split ? kinds.Count : 1); i++)
            Style(_plot.Multiplot.GetPlot(i), model.XLabel,
                split ? AxisLabel(kinds[i]) : (kinds.Count == 1 ? AxisLabel(kinds[0]) : "Value"));

        var colour = 0;

        foreach (var curve in model.Curves)
        {
            var plot = _plot.Multiplot.GetPlot(split ? kinds.IndexOf(curve.Kind) : 0);

            // NaN is how an unconverged point is recorded, and ScottPlot draws a break in the line
            // rather than a spike — which is exactly right, since a gap is what happened.
            var line = plot.Add.ScatterLine(
                curve.X.ToArray(), curve.Y.ToArray(), Color.FromARGB(Palette[colour++ % Palette.Length]));

            line.LineWidth = 1.7f;
            line.MarkerSize = 0;
            line.LegendText = curve.Label;
        }

        for (var i = 0; i < (split ? kinds.Count : 1); i++)
        {
            var plot = _plot.Multiplot.GetPlot(i);

            if (model.Curves.Count > 1) StyleLegend(plot.ShowLegend(Alignment.UpperLeft));
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

        // SI prefixes on both axes: a sweep of a base current is in microamps and a sweep of a
        // mains source is in hundreds of volts, and neither reads well in scientific notation.
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
