using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;

namespace Cirq.UI.Views;

/// <summary>
/// The spectrum plot: frequency across, magnitude up.
/// <para>
/// One set of axes rather than the Bode plot's two, because there is only one quantity here.
/// Frequency is linear rather than logarithmic, which is the opposite of the response window and
/// deliberately so: a spectrum is read by looking at where the harmonics fall relative to the
/// fundamental, and on a log axis evenly spaced harmonics are not evenly spaced.
/// </para>
/// </summary>
public partial class SpectrumAnalyserWindow : Window
{
    private static readonly uint[] Palette =
        [0xFF4CC9F0, 0xFFF72585, 0xFF80ED99, 0xFFFFB703, 0xFFB388EB, 0xFFFF6B6B];

    private AvaPlot? _plot;

    public SpectrumAnalyserWindow()
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
        if (DataContext is not SpectrumViewModel model) return;

        model.CurvesChanged -= OnCurvesChanged;
        model.CurvesChanged += OnCurvesChanged;

        model.RunCommand.Execute(null);
    }

    private void OnCurvesChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        if (_plot is null || DataContext is not SpectrumViewModel model) return;

        if (_plot.Multiplot.Count() > 1) _plot.Multiplot.Reset();

        var plot = _plot.Plot;

        plot.Clear();
        Style(plot, model.VerticalLabel);

        for (var i = 0; i < model.Curves.Count; i++)
        {
            var curve = model.Curves[i];

            var ys = model.IsLogarithmic ? curve.Decibels : curve.Magnitudes;

            var line = plot.Add.ScatterLine(
                curve.Frequencies.ToArray(), ys.ToArray(),
                Color.FromARGB(Palette[i % Palette.Length]));

            line.LineWidth = 1.5f;
            line.MarkerSize = 0;
            line.LegendText = curve.Label;
        }

        // Decibels want a floor: the empty bins of a clean signal run to minus a hundred and
        // twenty and would squash everything worth seeing into the top inch of the plot.
        if (model.IsLogarithmic && model.Curves.Count > 0)
        {
            var highest = model.Curves.SelectMany(c => c.Decibels).DefaultIfEmpty(0).Max();
            plot.Axes.SetLimitsY(highest - 100, highest + 10);
        }

        if (model.Curves.Count > 1) StyleLegend(plot.ShowLegend(Alignment.UpperRight));
        else plot.HideLegend();

        _plot.Refresh();
    }

    private void Style(Plot plot, string leftLabel)
    {
        plot.FigureBackground.Color = FromTheme("PlotBackground");
        plot.DataBackground.Color = FromTheme("PlotBackground");
        plot.Grid.MajorLineColor = FromTheme("PlotGrid");
        plot.Axes.Color(FromTheme("PlotAxis"));

        plot.Axes.Bottom.Label.Text = "Frequency";
        plot.Axes.Bottom.Label.FontSize = 11;
        plot.Axes.Left.Label.Text = leftLabel;
        plot.Axes.Left.Label.FontSize = 11;

        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = value => Cirq.Core.Units.SiPrefix.Format(value, "Hz"),
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
