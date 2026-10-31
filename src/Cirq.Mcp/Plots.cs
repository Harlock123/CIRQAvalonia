using DataPoint = Cirq.Core.Primitives.DataPoint;
using Cirq.Core.Probing;
using Cirq.Core.Units;
using Cirq.Engine.Simulation;
using ScottPlot;
using SkiaSharp;

namespace Cirq.Mcp;

/// <summary>
/// The results of a run drawn as pictures for an agent to look at: the traces against time, a Bode plot, a DC sweep.
/// Black on white with the scope's trace colours and engineering-notation axes, so they read the way the editor's own
/// plots do.
/// </summary>
public static class Plots
{
    /// <summary>Trace colours, as the scope uses them (darkened where the scope's yellow would vanish on white).</summary>
    private static readonly ScottPlot.Color[] Colours =
    [
        ScottPlot.Color.FromHex("#1F77B4"), ScottPlot.Color.FromHex("#D62728"), ScottPlot.Color.FromHex("#2CA02C"),
        ScottPlot.Color.FromHex("#FF7F0E"), ScottPlot.Color.FromHex("#9467BD"), ScottPlot.Color.FromHex("#8C564B"),
        ScottPlot.Color.FromHex("#E377C2"), ScottPlot.Color.FromHex("#17BECF"),
    ];

    /// <summary>One trace of a transient run: what it's called, its unit, and its samples.</summary>
    public sealed record Trace(string Label, ProbeKind Kind, IReadOnlyList<DataPoint> Samples)
    {
        public string Unit => Kind switch
        {
            ProbeKind.Voltage or ProbeKind.Differential => "V",
            ProbeKind.Current => "A",
            ProbeKind.Power => "W",
            _ => "",
        };
    }

    /// <summary>
    /// The traces against time, one panel per quantity (volts, amps, watts, logic), stacked, sharing the time axis.
    /// </summary>
    public static byte[] Transient(IReadOnlyList<Trace> traces, int width, int height)
    {
        var groups = traces.Where(t => t.Samples.Count > 0).GroupBy(t => t.Kind == ProbeKind.Logic ? "logic" : t.Unit).ToList();
        if (groups.Count == 0) throw new InvalidOperationException("There's nothing to plot: no probe recorded any samples.");
        var multiplot = new Multiplot();
        multiplot.AddPlots(groups.Count);
        multiplot.Layout = new ScottPlot.MultiplotLayouts.Rows();
        var colour = 0;
        for (var g = 0; g < groups.Count; g++)
        {
            var plot = multiplot.GetPlot(g);
            var unit = groups[g].Key;
            Style(plot, g == groups.Count - 1 ? "Time" : "", unit switch { "V" => "Volts", "A" => "Amps", "W" => "Watts", "logic" => "Logic", _ => unit });
            Engineering(plot.Axes.Bottom, "s");
            if (unit != "logic") Engineering(plot.Axes.Left, unit);
            var row = 0;
            foreach (var trace in groups[g])
            {
                // Logic traces each get a lane of their own, as a logic analyser stacks them.
                var lane = 1.2 * row++;
                var xs = trace.Samples.Select(s => s.Time).ToArray();
                var ys = unit == "logic"
                    ? trace.Samples.Select(s => s.Value * 0.8 + lane).ToArray()
                    : trace.Samples.Select(s => s.Value).ToArray();
                var line = plot.Add.ScatterLine(xs, ys, Colours[colour++ % Colours.Length]);
                line.LineWidth = 1.6f;
                line.LegendText = trace.Label;
            }
            if (unit == "logic")
            {
                var names = groups[g].ToList();
                plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericManual(
                    names.Select((t, i) => new Tick(1.2 * i + 0.4, t.Label)).ToArray());
                plot.Axes.SetLimitsY(-0.3, 1.2 * names.Count);
            }
            plot.ShowLegend(Alignment.UpperRight);
        }
        return Png(multiplot, width, height);
    }

    /// <summary>A Bode plot: gain in dB above, phase in degrees below, against frequency on a log scale.</summary>
    public static byte[] Bode(AcSweepResult result, int width, int height)
    {
        var multiplot = new Multiplot();
        multiplot.AddPlots(2);
        multiplot.Layout = new ScottPlot.MultiplotLayouts.Rows();
        var gain = multiplot.GetPlot(0);
        var phase = multiplot.GetPlot(1);
        Style(gain, "", "Gain (dB)");
        Style(phase, "Frequency", "Phase (°)");
        foreach (var plot in new[] { gain, phase }) LogFrequency(plot);
        var xs = result.Frequencies.Select(Math.Log10).ToArray();
        for (var i = 0; i < result.Traces.Count; i++)
        {
            var trace = result.Traces[i];
            var colour = Colours[i % Colours.Length];
            var db = gain.Add.ScatterLine(xs, Enumerable.Range(0, xs.Length).Select(trace.Decibels).ToArray(), colour);
            db.LineWidth = 1.6f;
            db.LegendText = trace.Label;
            var degrees = phase.Add.ScatterLine(xs, Enumerable.Range(0, xs.Length).Select(trace.Degrees).ToArray(), colour);
            degrees.LineWidth = 1.6f;
        }
        gain.ShowLegend(Alignment.UpperRight);
        return Png(multiplot, width, height);
    }

    /// <summary>A DC sweep: each probe against the swept value, one curve per stepped value.</summary>
    public static byte[] DcSweep(DcSweepResult result, int width, int height)
    {
        var plot = new Plot();
        Style(plot, result.XLabel, "");
        Engineering(plot.Axes.Bottom, "");
        Engineering(plot.Axes.Left, "");
        var xs = result.X.ToArray();
        var colour = 0;
        foreach (var curve in result.Curves)
        {
            foreach (var trace in curve.Traces)
            {
                var line = plot.Add.ScatterLine(xs, trace.Values.ToArray(), Colours[colour++ % Colours.Length]);
                line.LineWidth = 1.6f;
                line.LegendText = double.IsNaN(curve.StepValue) ? trace.Label : $"{trace.Label} ({result.StepLabel} = {SiPrefix.Format(curve.StepValue)})";
            }
        }
        plot.ShowLegend(Alignment.UpperLeft);
        return plot.GetImageBytes(width, height, ImageFormat.Png);
    }

    private static void Style(Plot plot, string bottom, string left)
    {
        plot.FigureBackground.Color = Colors.White;
        plot.DataBackground.Color = Colors.White;
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#E3E6EA");
        plot.Axes.Color(ScottPlot.Color.FromHex("#333A44"));
        plot.Axes.Bottom.Label.Text = bottom;
        plot.Axes.Left.Label.Text = left;
        plot.Axes.Bottom.Label.FontSize = 13;
        plot.Axes.Left.Label.FontSize = 13;
    }

    private static void Engineering(IAxis axis, string unit) =>
        axis.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic { LabelFormatter = v => SiPrefix.Format(v, unit, 3) };

    private static void LogFrequency(Plot plot) =>
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            MinorTickGenerator = new ScottPlot.TickGenerators.LogMinorTickGenerator(),
            IntegerTicksOnly = true,
            LabelFormatter = v => SiPrefix.Format(Math.Pow(10, v), "Hz"),
        };

    private static byte[] Png(Multiplot multiplot, int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        multiplot.Render(surface);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }
}
