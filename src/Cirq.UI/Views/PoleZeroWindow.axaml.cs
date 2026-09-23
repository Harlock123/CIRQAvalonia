using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;

namespace Cirq.UI.Views;

/// <summary>
/// The s-plane: poles as crosses, zeros as circles, and the imaginary axis between stable and not.
/// <para>
/// The picture is the whole point. How far left a root sits is how fast it dies away; how far up
/// or down is what frequency it rings at; and anything to the right of the vertical line is a
/// circuit that does not settle. All three are read at a glance from a plot and not at all from a
/// list of complex numbers, which is why the list is beside it rather than instead of it.
/// </para>
/// </summary>
public partial class PoleZeroWindow : Window
{
    private AvaPlot? _plot;

    public PoleZeroWindow()
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
        if (DataContext is not PoleZeroViewModel model) return;

        model.ResultChanged -= OnResultChanged;
        model.ResultChanged += OnResultChanged;

        model.RunCommand.Execute(null);
    }

    private void OnResultChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        if (_plot is null || DataContext is not PoleZeroViewModel model) return;

        var plot = _plot.Plot;

        plot.Clear();

        plot.FigureBackground.Color = FromTheme("PlotBackground");
        plot.DataBackground.Color = FromTheme("PlotBackground");
        plot.Grid.MajorLineColor = FromTheme("PlotGrid");
        plot.Axes.Color(FromTheme("PlotAxis"));

        plot.Axes.Bottom.Label.Text = "Real (rad/s) — how fast it dies away";
        plot.Axes.Left.Label.Text = "Imaginary (rad/s) — what it rings at";
        plot.Axes.Bottom.Label.FontSize = 11;
        plot.Axes.Left.Label.FontSize = 11;

        if (model.HasResult)
        {
            // The line that separates a circuit that settles from one that does not.
            var axis = plot.Add.VerticalLine(0);

            axis.Color = Color.FromARGB(0xFF808A96);
            axis.LineWidth = 1;
            axis.LinePattern = LinePattern.Dashed;

            Scatter(plot, model.Poles, MarkerShape.Cross, Color.FromARGB(0xFF4CC9F0), "Poles");
            Scatter(plot, model.Zeros, MarkerShape.OpenCircle, Color.FromARGB(0xFF80ED99), "Zeros");

            // Anything in the right half plane is why the circuit does not work, so it is drawn
            // in the colour everything else that is wrong is drawn in.
            Scatter(
                plot,
                [.. model.Poles.Where(p => p.IsUnstable)],
                MarkerShape.Cross,
                Color.FromARGB(0xFFFF6B6B),
                null);

            var legend = plot.ShowLegend(Alignment.UpperRight);

            legend.FontColor = FromTheme("PlotAxis");
            legend.OutlineColor = FromTheme("PlotGrid");
            legend.BackgroundColor = FromTheme("PlotBackground");

            plot.Axes.AutoScale();
        }

        _plot.Refresh();
    }

    private static void Scatter(
        Plot plot, IReadOnlyList<Cirq.Engine.Simulation.Root> roots,
        MarkerShape shape, Color colour, string? legend)
    {
        if (roots.Count == 0) return;

        var marks = plot.Add.ScatterPoints(
            roots.Select(r => r.S.Real).ToArray(),
            roots.Select(r => r.S.Imaginary).ToArray(),
            colour);

        marks.MarkerShape = shape;
        marks.MarkerSize = 11;
        marks.MarkerLineWidth = 2;

        if (legend is not null) marks.LegendText = legend;
    }

    private Color FromTheme(string key)
    {
        var colour = Cirq.UI.Services.ThemeManager.ColorFor(this, key);

        return Color.FromARGB(unchecked((uint)(
            (colour.A << 24) | (colour.R << 16) | (colour.G << 8) | colour.B)));
    }
}
