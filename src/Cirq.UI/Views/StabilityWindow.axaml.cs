using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;

namespace Cirq.UI.Views;

/// <summary>
/// The loop-gain plot: magnitude above, phase below, and the two lines that decide everything —
/// 0 dB on the magnitude and −180° on the phase.
/// <para>
/// Stacked rather than shared, for the reason the Bode plot is: decibels and degrees have nothing
/// in common, and the question being asked is about the <i>vertical distance</i> from each curve
/// to its own line. Drawing those lines matters as much as drawing the curves, because the margins
/// are the gaps between them.
/// </para>
/// </summary>
public partial class StabilityWindow : Window
{
    private AvaPlot? _plot;
    private PlotReadout? _readout;

    /// <summary>
    /// What the loop is doing at the frequency under the pointer — both the gain and the phase,
    /// because on this plot the two are read together or not at all.
    /// </summary>
    private string? Describe(double logHertz)
    {
        if (DataContext is not StabilityViewModel model || !model.HasResult) return null;

        var index = PlotReadout.Nearest(
            [.. model.Frequencies.Select(Math.Log10)], logHertz);

        if (index < 0) return null;

        return $"{Cirq.Core.Units.SiPrefix.Format(model.Frequencies[index], "Hz")}:  " +
               $"{model.Decibels[index]:0.0} dB  ·  {model.Degrees[index]:0.0}°";
    }

    public StabilityWindow()
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
        if (DataContext is not StabilityViewModel model) return;

        model.ResultChanged -= OnResultChanged;
        model.ResultChanged += OnResultChanged;

        _readout ??= new PlotReadout(_plot!, Describe, text => model.Readout = text);

        model.RunCommand.Execute(null);
    }

    private void OnResultChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        if (_plot is null || DataContext is not StabilityViewModel model) return;

        _plot.Multiplot.Reset();
        _plot.Multiplot.AddPlots(2);
        _plot.Multiplot.Layout = new ScottPlot.MultiplotLayouts.Rows();

        var magnitude = _plot.Multiplot.GetPlot(0);
        var phase = _plot.Multiplot.GetPlot(1);

        Style(magnitude, "Loop gain (dB)");
        Style(phase, "Loop phase (°)");

        if (model.HasResult)
        {
            var x = model.Frequencies.Select(f => Math.Log10(Math.Max(f, 1e-12))).ToArray();

            var gain = magnitude.Add.ScatterLine(x, model.Decibels.ToArray(), Color.FromARGB(0xFF4CC9F0));
            gain.LineWidth = 1.8f;
            gain.MarkerSize = 0;

            var angle = phase.Add.ScatterLine(x, model.Degrees.ToArray(), Color.FromARGB(0xFFF72585));
            angle.LineWidth = 1.8f;
            angle.MarkerSize = 0;

            // The two lines the margins are measured from.
            Reference(magnitude, 0, "0 dB");
            Reference(phase, -180, "−180°");

            // And where the loop crosses over, on both, so the two plots can be read together.
            if (model.CrossoverHz is { } crossover)
            {
                var at = Math.Log10(crossover);

                Marker(magnitude, at);
                Marker(phase, at);
            }
        }

        _plot.Refresh();
    }

    private static void Reference(Plot plot, double y, string label)
    {
        var line = plot.Add.HorizontalLine(y);

        line.Color = Color.FromARGB(0xFF808A96);
        line.LineWidth = 1;
        line.LinePattern = LinePattern.Dashed;
        line.Text = label;
    }

    private static void Marker(Plot plot, double x)
    {
        var line = plot.Add.VerticalLine(x);

        line.Color = Color.FromARGB(0x88FFB703);
        line.LineWidth = 1;
        line.LinePattern = LinePattern.Dotted;
    }

    private void Style(Plot plot, string leftLabel)
    {
        plot.Clear();

        plot.FigureBackground.Color = FromTheme("PlotBackground");
        plot.DataBackground.Color = FromTheme("PlotBackground");
        plot.Grid.MajorLineColor = FromTheme("PlotGrid");
        plot.Axes.Color(FromTheme("PlotAxis"));

        plot.Axes.Bottom.Label.Text = "Frequency (Hz)";
        plot.Axes.Bottom.Label.FontSize = 11;
        plot.Axes.Left.Label.Text = leftLabel;
        plot.Axes.Left.Label.FontSize = 11;

        // The X axis is plotted as a logarithm, with the ticks written back as the real numbers.
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = value => Cirq.Core.Units.SiPrefix.Format(Math.Pow(10, value), string.Empty),
        };
    }

    private Color FromTheme(string key)
    {
        var colour = Cirq.UI.Services.ThemeManager.ColorFor(this, key);

        return Color.FromARGB(unchecked((uint)(
            (colour.A << 24) | (colour.R << 16) | (colour.G << 8) | colour.B)));
    }
}
