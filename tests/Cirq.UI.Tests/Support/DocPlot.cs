using ScottPlot;

namespace Cirq.UI.Tests;

/// <summary>
/// Draws the plots that go in the user guide.
/// <para>
/// Generated rather than screenshotted, for three reasons that all turned out to matter. A
/// screenshot goes stale the moment the thing it shows changes, and one of the guide's already
/// had — it was taken before the palette grew a search box, and nothing said so. A screenshot of
/// a modal window cannot be taken at all on some desktops, this one included, because the
/// compositor shows the desktop through it. And a screenshot has window chrome around the part
/// anybody came to look at.
/// </para>
/// <para>
/// A generated plot has none of those problems: it is drawn from the real analysis of a real
/// circuit, so it cannot show something the code does not do, and it is redrawn every time the
/// tests run.
/// </para>
/// </summary>
public static class DocPlot
{
    /// <summary>Where the guide keeps its pictures.</summary>
    public static string ImageDirectory => Path.Combine(RepoRoot, "docs", "images");

    public static string RepoRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CirqAvalonia.slnx")))
                directory = directory.Parent;

            return directory?.FullName
                   ?? throw new InvalidOperationException("Could not find the repository root.");
        }
    }

    /// <summary>
    /// The dark theme's plot colours, as <c>Themes.axaml</c> has them. Stated here rather than read
    /// from the theme because a headless test has no application to resolve a resource against —
    /// and because the guide should look like one theme rather than whichever was last set.
    /// </summary>
    public static readonly Color Background = Color.FromHex("#0E1116");
    public static readonly Color Grid = Color.FromHex("#212833");
    public static readonly Color Axis = Color.FromHex("#7C8DA0");

    /// <summary>The trace colours the scope uses, so a plot in the guide matches one on screen.</summary>
    public static readonly Color[] Traces =
    [
        Color.FromHex("#4CC9F0"), Color.FromHex("#F72585"), Color.FromHex("#80ED99"),
        Color.FromHex("#FFB703"), Color.FromHex("#B388EB"), Color.FromHex("#FF6B6B"),
    ];

    /// <summary>Muted, for a reference line that is not data.</summary>
    public static readonly Color Marker = Color.FromHex("#808A96");

    /// <summary>A new plot, dressed the way the application dresses one.</summary>
    public static Plot New(string bottom, string left) => Style(new Plot(), bottom, left);

    /// <summary>
    /// Dresses a plot that already exists — which a stacked pair has to be, because a multiplot
    /// makes its own and drawing into ones made separately loses the drawing.
    /// </summary>
    public static Plot Style(Plot plot, string bottom, string left)
    {
        plot.FigureBackground.Color = Background;
        plot.DataBackground.Color = Background;
        plot.Grid.MajorLineColor = Grid;
        plot.Axes.Color(Axis);

        plot.Axes.Bottom.Label.Text = bottom;
        plot.Axes.Left.Label.Text = left;
        plot.Axes.Bottom.Label.FontSize = 13;
        plot.Axes.Left.Label.FontSize = 13;

        return plot;
    }

    /// <summary>
    /// Two plots in one image, sharing a frequency axis and nothing else — the way the
    /// application stacks a Bode pair. Decibels and degrees have no common scale, and a plot that
    /// pretended otherwise would have to be decoded rather than read.
    /// <para>
    /// The plots are handed out rather than taken in, because a multiplot makes its own and
    /// anything drawn into a plot made separately is thrown away. That failure is silent: the
    /// image comes out as two empty grids, which is exactly what it looked like the first time.
    /// </para>
    /// </summary>
    public static void Stack(string name, Action<Plot, Plot> draw, int width = 900, int height = 620)
    {
        ArgumentNullException.ThrowIfNull(draw);

        var multi = new Multiplot();

        multi.AddPlots(2);
        multi.Layout = new ScottPlot.MultiplotLayouts.Rows();

        draw(multi.GetPlot(0), multi.GetPlot(1));

        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(width, height));

        multi.Render(surface.Canvas, new PixelRect(width, height));

        Directory.CreateDirectory(ImageDirectory);

        using var image = surface.Snapshot();
        using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
        using var file = File.Create(Path.Combine(ImageDirectory, name));

        data.SaveTo(file);
    }

    /// <summary>
    /// A decade axis: the values are plotted as their logarithms and the ticks are written back as
    /// the numbers, which is how every Bode plot in the application does it.
    /// </summary>
    public static void DecadeBottom(Plot plot)
    {
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = v => Cirq.Core.Units.SiPrefix.Format(Math.Pow(10, v), string.Empty),
        };
    }

    /// <summary>
    /// A caption across the top saying what the picture shows. A plot in a guide is read before
    /// the paragraph beside it, so the one sentence that makes it make sense belongs on it.
    /// </summary>
    public static void Title(Plot plot, string text)
    {
        plot.Title(text);
        plot.Axes.Title.Label.FontSize = 13;
        plot.Axes.Title.Label.ForeColor = Axis;
        plot.Axes.Title.Label.Bold = false;
    }

    /// <summary>SI prefixes along the bottom, for an axis of seconds or hertz.</summary>
    public static void SiBottom(Plot plot)
    {
        plot.Axes.Bottom.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = v => Cirq.Core.Units.SiPrefix.Format(v, string.Empty),
        };
    }

    /// <summary>
    /// A decade axis up the side, for a quantity plotted as its logarithm. The mirror of
    /// <see cref="DecadeBottom"/>, and needed for the same reason: an impedance that runs from
    /// fifty milliohms to a couple of hundred ohms has its whole interesting part squashed against
    /// the bottom of a linear axis.
    /// </summary>
    public static void DecadeLeft(Plot plot)
    {
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = v => Cirq.Core.Units.SiPrefix.Format(Math.Pow(10, v), string.Empty),
        };
    }

    public static void SiLeft(Plot plot)
    {
        plot.Axes.Left.TickGenerator = new ScottPlot.TickGenerators.NumericAutomatic
        {
            LabelFormatter = v => Cirq.Core.Units.SiPrefix.Format(v, string.Empty),
        };
    }

    /// <summary>A line of the curve, in the scope's colours.</summary>
    public static ScottPlot.Plottables.Scatter Line(
        Plot plot, IEnumerable<double> x, IEnumerable<double> y, int colour, string? legend = null)
    {
        var line = plot.Add.ScatterLine(x.ToArray(), y.ToArray(), Traces[colour % Traces.Length]);

        line.LineWidth = 2f;
        line.MarkerSize = 0;

        if (legend is not null) line.LegendText = legend;

        return line;
    }

    /// <summary>A dashed reference line, for the thresholds a margin is measured from.</summary>
    public static void Reference(Plot plot, double y, string? label)
    {
        var line = plot.Add.HorizontalLine(y);

        line.Color = Marker;
        line.LineWidth = 1;
        line.LinePattern = LinePattern.Dashed;
        line.Text = label ?? string.Empty;
    }

    /// <summary>
    /// A dotted marker at a frequency or a time worth pointing at. The label is optional and
    /// usually better left off: it is drawn on the axis, where it lands on top of the tick labels.
    /// </summary>
    public static void Vertical(Plot plot, double x, string? label)
    {
        var line = plot.Add.VerticalLine(x);

        line.Color = Color.FromHex("#FFB703");
        line.LineWidth = 1;
        line.LinePattern = LinePattern.Dotted;

        if (label is not null) line.Text = label;
    }

    public static void Legend(Plot plot)
    {
        var legend = plot.ShowLegend(Alignment.UpperRight);

        legend.BackgroundColor = Background.WithAlpha(0.85);
        legend.FontColor = Axis;
        legend.OutlineColor = Grid;
    }

    /// <summary>Writes the plot into the guide's images, and says where it went.</summary>
    public static string Save(Plot plot, string name, int width = 900, int height = 480)
    {
        Directory.CreateDirectory(ImageDirectory);

        var path = Path.Combine(ImageDirectory, name);

        plot.SavePng(path, width, height);

        return path;
    }
}
