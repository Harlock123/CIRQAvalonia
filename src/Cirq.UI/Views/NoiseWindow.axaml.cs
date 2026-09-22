using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;

namespace Cirq.UI.Views;

/// <summary>
/// The noise plot: frequency across the bottom, output noise density up the side.
/// <para>
/// Both axes are logarithmic, and that is not decoration. Noise curves span decades in both
/// directions — a flat thermal floor with a 1/f rise at one end and a filter's roll-off at the
/// other — and on a linear axis the whole of the interesting part is squashed into the corner.
/// </para>
/// </summary>
public partial class NoiseWindow : Window
{
    private AvaPlot? _plot;

    public NoiseWindow()
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
        if (DataContext is not NoiseViewModel model) return;

        model.ResultChanged -= OnResultChanged;
        model.ResultChanged += OnResultChanged;

        // Measured on opening: it is one bias point and a couple of solves per frequency, so the
        // window arrives with an answer rather than a blank grid.
        model.RunCommand.Execute(null);
    }

    private void OnResultChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        if (_plot is null || DataContext is not NoiseViewModel model) return;

        var plot = _plot.Plot;

        plot.Clear();

        plot.FigureBackground.Color = FromTheme("PlotBackground");
        plot.DataBackground.Color = FromTheme("PlotBackground");
        plot.Grid.MajorLineColor = FromTheme("PlotGrid");
        plot.Axes.Color(FromTheme("PlotAxis"));

        plot.Axes.Bottom.Label.Text = "Frequency (Hz)";
        plot.Axes.Bottom.Label.FontSize = 11;
        plot.Axes.Left.Label.Text = "Output noise (V/√Hz)";
        plot.Axes.Left.Label.FontSize = 11;

        if (model.Density.Count > 0)
        {
            // Plotted as logarithms of both, with the ticks written back as the real numbers —
            // the same way the Bode plot handles a decade axis.
            var x = model.Frequencies.Select(f => Math.Log10(Math.Max(f, 1e-12))).ToArray();
            var y = model.Density.Select(d => Math.Log10(Math.Max(d, 1e-21))).ToArray();

            var line = plot.Add.ScatterLine(x, y, Color.FromARGB(0xFF4CC9F0));
            line.LineWidth = 1.8f;
            line.MarkerSize = 0;
        }

        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = value => Cirq.Core.Units.SiPrefix.Format(Math.Pow(10, value), string.Empty),
        };

        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = value => Cirq.Core.Units.SiPrefix.Format(Math.Pow(10, value), string.Empty),
        };

        plot.Axes.AutoScale();
        _plot.Refresh();
    }

    private Color FromTheme(string key)
    {
        var colour = Cirq.UI.Services.ThemeManager.ColorFor(this, key);

        return Color.FromARGB(unchecked((uint)(
            (colour.A << 24) | (colour.R << 16) | (colour.G << 8) | colour.B)));
    }
}
