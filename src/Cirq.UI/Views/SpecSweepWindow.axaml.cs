using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;

namespace Cirq.UI.Views;

/// <summary>
/// Margin against whatever was swept, with zero drawn across it.
/// <para>
/// The margin rather than the measurement, because the measurements are in different units and
/// cannot share an axis — millivolts of ripple and microseconds of rise time — while the margins
/// can: every requirement is a fraction of its own limit. One pair of axes, every requirement on
/// it, and one line that means failure.
/// </para>
/// </summary>
public partial class SpecSweepWindow : Window
{
    private static readonly uint[] Ramp =
        [0xFF4CC9F0, 0xFFF72585, 0xFF80ED99, 0xFFFFB703, 0xFFB388EB, 0xFFFF6B6B,
         0xFF4895EF, 0xFF00D4A0];

    private AvaPlot? _plot;

    public SpecSweepWindow()
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
        if (DataContext is not SpecSweepViewModel model) return;

        model.CurvesChanged -= OnCurvesChanged;
        model.CurvesChanged += OnCurvesChanged;

        // Not run on opening: this runs the whole circuit once per point, over a range nobody has
        // chosen yet.
        Render();
    }

    private void OnCurvesChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        if (_plot is null || DataContext is not SpecSweepViewModel model) return;

        var plot = _plot.Plot;

        plot.Clear();

        plot.FigureBackground.Color = FromTheme("PlotBackground");
        plot.DataBackground.Color = FromTheme("PlotBackground");
        plot.Grid.MajorLineColor = FromTheme("PlotGrid");
        plot.Axes.Color(FromTheme("PlotAxis"));

        plot.Axes.Bottom.Label.Text = model.AxisLabel;
        plot.Axes.Bottom.Label.FontSize = 11;
        plot.Axes.Left.Label.Text = "Margin (fraction of the limit)";
        plot.Axes.Left.Label.FontSize = 11;

        var index = 0;

        foreach (var curve in model.Curves)
        {
            var line = plot.Add.ScatterLine(
                curve.Values.ToArray(), curve.Margins.ToArray(),
                Color.FromARGB(Ramp[index % Ramp.Length]));

            line.LineWidth = 2f;
            line.MarkerSize = 5;
            line.MarkerShape = MarkerShape.FilledCircle;
            line.LegendText = curve.Label;

            index++;
        }

        if (model.Curves.Count > 0)
        {
            // The line the whole picture is about: above it the requirement is met, below it the
            // circuit does not do what it was written down to do.
            var limit = plot.Add.HorizontalLine(0);

            limit.Color = FromTheme("PlotAxis");
            limit.LineWidth = 1;
            limit.LinePattern = LinePattern.Dashed;
            limit.Text = "limit";

            var legend = plot.ShowLegend(Alignment.LowerLeft);

            legend.BackgroundColor = FromTheme("PlotBackground");
            legend.FontColor = FromTheme("PlotAxis");
            legend.OutlineColor = FromTheme("PlotGrid");
        }
        else
        {
            plot.HideLegend();
        }

        _plot.Refresh();
    }

    private Color FromTheme(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var found)
        && found is Avalonia.Media.Color colour
            ? new Color(colour.R, colour.G, colour.B, colour.A)
            : Colors.Gray;
}
