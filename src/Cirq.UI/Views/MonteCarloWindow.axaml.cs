using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;

namespace Cirq.UI.Views;

/// <summary>
/// The histogram of the selected trace, with the nominal value marked on it.
/// <para>
/// The marker is the point of the plot. A spread on its own is only a number; a spread with the
/// designed value drawn through it shows at a glance whether the circuit is centred on what it
/// was meant to do or merely near it.
/// </para>
/// </summary>
public partial class MonteCarloWindow : Window
{
    private AvaPlot? _plot;

    public MonteCarloWindow()
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
        if (DataContext is not MonteCarloViewModel model) return;

        model.ResultsChanged -= OnResultsChanged;
        model.ResultsChanged += OnResultsChanged;

        model.RunCommand.Execute(null);
    }

    private void OnResultsChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        if (_plot is null || DataContext is not MonteCarloViewModel model) return;

        var plot = _plot.Plot;

        plot.Clear();

        plot.FigureBackground.Color = FromTheme("PlotBackground");
        plot.DataBackground.Color = FromTheme("PlotBackground");
        plot.Grid.MajorLineColor = FromTheme("PlotGrid");
        plot.Axes.Color(FromTheme("PlotAxis"));

        plot.Axes.Left.Label.Text = "Trials";
        plot.Axes.Left.Label.FontSize = 11;
        plot.Axes.Bottom.Label.FontSize = 11;

        if (model.Selected is not { } row)
        {
            plot.Axes.Bottom.Label.Text = string.Empty;
            _plot.Refresh();
            return;
        }

        plot.Axes.Bottom.Label.Text = row.Label;

        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = value => Cirq.Core.Units.SiPrefix.Format(value, string.Empty),
        };

        var histogram = row.Trace.Histogram();
        if (histogram.Length == 0)
        {
            _plot.Refresh();
            return;
        }

        var width = histogram.Length > 1
            ? (histogram[1].Centre - histogram[0].Centre) * 0.9
            : Math.Max(Math.Abs(histogram[0].Centre) * 0.1, 1.0);

        var bars = plot.Add.Bars(histogram.Select(b => new Bar
        {
            Position = b.Centre,
            Value = b.Count,
            Size = width,
        }).ToArray());

        bars.Color = Color.FromARGB(0xFF4CC9F0);

        // Where it was designed to be, which is the thing the spread is spread around — or, when
        // the circuit is not centred, conspicuously is not.
        var nominal = plot.Add.VerticalLine(row.Trace.Nominal);
        nominal.Color = Color.FromARGB(0xFFF72585);
        nominal.LineWidth = 2;
        nominal.Text = "nominal";

        plot.HideLegend();
        _plot.Refresh();
    }

    private Color FromTheme(string key)
    {
        var colour = Cirq.UI.Services.ThemeManager.ColorFor(this, key);

        return Color.FromARGB(unchecked((uint)(
            (colour.A << 24) | (colour.R << 16) | (colour.G << 8) | colour.B)));
    }
}
