using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cirq.UI.ViewModels;
using ScottPlot;
using ScottPlot.Avalonia;

namespace Cirq.UI.Views;

/// <summary>
/// Impedance against frequency: magnitude above, phase below.
/// <para>
/// Both axes are logarithms, and the magnitude's has to be. An impedance that starts at fifty
/// milliohms and peaks at ten kilohms spans six decades, and on a linear axis the whole of the
/// interesting part — the dip where a decoupling capacitor stops working — is a flat line against
/// the bottom.
/// </para>
/// <para>
/// The phase is what says <i>which</i> kind of resonance you are looking at: falling through zero
/// is parallel and the impedance peaks, rising through it is series and the impedance dips. The
/// two plots are read together, which is why they are stacked rather than separate.
/// </para>
/// </summary>
public partial class ImpedanceWindow : Window
{
    private AvaPlot? _plot;
    private PlotReadout? _readout;

    private string? Describe(double logHertz)
    {
        if (DataContext is not ImpedanceViewModel model || !model.HasResult) return null;

        var index = PlotReadout.Nearest([.. model.Frequencies.Select(Math.Log10)], logHertz);

        if (index < 0) return null;

        return $"{Cirq.Core.Units.SiPrefix.Format(model.Frequencies[index], "Hz")}:  " +
               $"{Cirq.Core.Units.SiPrefix.Format(model.Ohms[index], "Ω")}  ·  " +
               $"{model.Degrees[index]:0.0}°";
    }

    public ImpedanceWindow()
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
        if (DataContext is not ImpedanceViewModel model) return;

        model.ResultChanged -= OnResultChanged;
        model.ResultChanged += OnResultChanged;

        _readout ??= new PlotReadout(_plot!, Describe, text => model.Readout = text);

        model.RunCommand.Execute(null);
    }

    private void OnResultChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        if (_plot is null || DataContext is not ImpedanceViewModel model) return;

        _plot.Multiplot.Reset();
        _plot.Multiplot.AddPlots(2);
        _plot.Multiplot.Layout = new ScottPlot.MultiplotLayouts.Rows();

        var magnitude = _plot.Multiplot.GetPlot(0);
        var phase = _plot.Multiplot.GetPlot(1);

        Style(magnitude, "|Z| (Ω)", logarithmicLeft: true);
        Style(phase, "Phase (°)", logarithmicLeft: false);

        if (model.HasResult)
        {
            var x = model.Frequencies.Select(f => Math.Log10(Math.Max(f, 1e-12))).ToArray();

            var ohms = magnitude.Add.ScatterLine(
                x,
                [.. model.Ohms.Select(o => Math.Log10(Math.Max(o, 1e-12)))],
                Color.FromARGB(0xFF4CC9F0));

            ohms.LineWidth = 1.8f;
            ohms.MarkerSize = 0;

            var angle = phase.Add.ScatterLine(x, [.. model.Degrees], Color.FromARGB(0xFFF72585));
            angle.LineWidth = 1.8f;
            angle.MarkerSize = 0;

            // Zero reactance is where a resonance is, which is what the phase plot is here for.
            Reference(phase, 0, "0° — resistive");

            foreach (var (hertz, _) in model.Resonances)
            {
                var at = Math.Log10(hertz);

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

    private void Style(Plot plot, string leftLabel, bool logarithmicLeft)
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

        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = value => Cirq.Core.Units.SiPrefix.Format(Math.Pow(10, value), string.Empty),
        };

        if (!logarithmicLeft) return;

        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
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
