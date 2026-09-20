using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;

namespace Cirq.UI.Views;

/// <summary>
/// The Bode plot: magnitude above, phase below, frequency logarithmic across both.
/// <para>
/// Two stacked axes rather than one with two scales, because decibels and degrees have nothing to
/// do with each other and overlaying them on a shared axis makes a plot that has to be decoded
/// rather than read.
/// </para>
/// </summary>
public partial class FrequencyResponseWindow : Window
{
    /// <summary>
    /// The trace colours, in the order curves are added. The scope takes its colours from each
    /// probe; here they are assigned in order, because a response curve is a property of the sweep
    /// rather than of the probe that happened to produce it.
    /// </summary>
    private static readonly uint[] Palette =
        [0xFF4CC9F0, 0xFFF72585, 0xFF80ED99, 0xFFFFB703, 0xFFB388EB, 0xFFFF6B6B];

    private AvaPlot? _plot;

    public FrequencyResponseWindow()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);

        _plot = this.FindControl<AvaPlot>("Plot");

        DataContextChanged += (_, _) => Attach();

        // The desktop's theme preference is not always known by the time a window first draws —
        // it arrives once the portal answers — so the plot is redrawn when it settles. The scope
        // never shows this because it repaints twenty times a second and corrects itself; a window
        // that draws once does not.
        ActualThemeVariantChanged += (_, _) => Render();

        Attach();
    }

    private void Attach()
    {
        if (DataContext is not FrequencyResponseViewModel model) return;

        model.CurvesChanged -= OnCurvesChanged;
        model.CurvesChanged += OnCurvesChanged;

        // Sweep once on opening, so the window arrives with an answer rather than a blank grid.
        model.RunCommand.Execute(null);
    }

    private void OnCurvesChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        if (_plot is null || DataContext is not FrequencyResponseViewModel model) return;

        _plot.Multiplot.Reset();
        _plot.Multiplot.AddPlots(2);
        _plot.Multiplot.Layout = new ScottPlot.MultiplotLayouts.Rows();

        var magnitude = _plot.Multiplot.GetPlot(0);
        var phase = _plot.Multiplot.GetPlot(1);

        Style(magnitude, "Gain (dB)");
        Style(phase, "Phase (°)");

        for (var i = 0; i < model.Curves.Count; i++)
        {
            var curve = model.Curves[i];
            var colour = Color.FromARGB(Palette[i % Palette.Length]);

            // Plotted against log10 of the frequency, with the ticks labelled in hertz, because
            // ScottPlot's scatter takes linear data and a decade axis is what a Bode plot is.
            var xs = curve.Frequencies.Select(Math.Log10).ToArray();

            var gain = magnitude.Add.ScatterLine(xs, [.. curve.Decibels], colour);
            gain.LineWidth = 1.7f;
            gain.MarkerSize = 0;
            gain.LegendText = curve.Label;

            var angle = phase.Add.ScatterLine(xs, [.. curve.Degrees], colour);
            angle.LineWidth = 1.7f;
            angle.MarkerSize = 0;
            angle.LegendText = curve.Label;
        }

        if (model.Curves.Count > 1)
        {
            StyleLegend(magnitude.ShowLegend(Alignment.LowerLeft));
            phase.HideLegend();
        }
        else
        {
            magnitude.HideLegend();
            phase.HideLegend();
        }

        _plot.Refresh();
    }

    private void Style(Plot plot, string leftLabel)
    {
        // Reset() leaves the plots it hands back holding whatever was on them, so a second render
        // draws every curve again on top of the first set.
        plot.Clear();

        plot.FigureBackground.Color = FromTheme("PlotBackground");
        plot.DataBackground.Color = FromTheme("PlotBackground");
        plot.Grid.MajorLineColor = FromTheme("PlotGrid");
        plot.Axes.Color(FromTheme("PlotAxis"));

        plot.Axes.Bottom.Label.Text = "Frequency";
        plot.Axes.Bottom.Label.FontSize = 11;
        plot.Axes.Left.Label.Text = leftLabel;
        plot.Axes.Left.Label.FontSize = 11;

        // Decade ticks, written the way anybody reading a datasheet expects them.
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = value => Cirq.Core.Units.SiPrefix.Format(Math.Pow(10, value), "Hz"),
        };
    }

    private void StyleLegend(Legend legend)
    {
        legend.BackgroundColor = FromTheme("PlotBackground").WithAlpha(0.82);
        legend.FontColor = FromTheme("PlotAxis");
        legend.OutlineColor = FromTheme("PlotGrid");
    }

    /// <summary>
    /// Resolved against this window rather than the application, because the two can disagree —
    /// see <see cref="Cirq.UI.Services.ThemeManager.BrushFor"/>.
    /// </summary>
    private Color FromTheme(string key)
    {
        var colour = Cirq.UI.Services.ThemeManager.ColorFor(this, key);

        return Color.FromARGB(unchecked((uint)(
            (colour.A << 24) | (colour.R << 16) | (colour.G << 8) | colour.B)));
    }
}
