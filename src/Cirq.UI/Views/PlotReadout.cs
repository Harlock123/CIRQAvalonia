using ScottPlot;
using Avalonia.Input;
using ScottPlot.Avalonia;

namespace Cirq.UI.Views;

/// <summary>
/// Says what the curves read wherever the pointer is.
/// <para>
/// The scope has draggable cursors, and they are the right tool there: what you want from a
/// waveform is the interval between two instants. What you want from a Bode plot, a noise curve
/// or a loop gain is different — "what is it at ten kilohertz" — and that is one reading rather
/// than two, needed the moment the pointer is over the place rather than after dragging something
/// to it.
/// </para>
/// <para>
/// So these windows get a readout instead. It costs a line under the plot, works without any
/// clicking at all, and reads the value off the same arrays the curve was drawn from rather than
/// off the picture.
/// </para>
/// </summary>
public sealed class PlotReadout
{
    private readonly AvaPlot _plot;
    private readonly Func<double, string?> _describe;
    private readonly Action<string> _report;

    /// <param name="plot">The plot to watch.</param>
    /// <param name="describe">
    /// Given the X coordinate the pointer is over, what to say — or null when there is nothing to
    /// say there, which clears the line rather than leaving a stale reading under the pointer.
    /// </param>
    /// <param name="report">Where the line goes.</param>
    public PlotReadout(AvaPlot plot, Func<double, string?> describe, Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(plot);

        _plot = plot;
        _describe = describe;
        _report = report;

        _plot.PointerMoved += OnMoved;
        _plot.PointerExited += (_, _) => _report(string.Empty);
    }

    /// <summary>
    /// Which subplot to read from when the window is stacked. A Bode pair shares its frequency
    /// axis, so either gives the same answer and the top one is the one the pointer is usually
    /// over.
    /// </summary>
    public int Subplot { get; set; }

    private void OnMoved(object? sender, PointerEventArgs e)
    {
        var plot = _plot.Multiplot.Count() > 1
            ? _plot.Multiplot.GetPlot(Math.Min(Subplot, _plot.Multiplot.Count() - 1))
            : _plot.Plot;

        var area = plot.RenderManager.LastRender.DataRect;

        if (area.Width <= 0)
        {
            _report(string.Empty);
            return;
        }

        var position = e.GetPosition(_plot);
        var x = plot.Axes.Bottom.GetCoordinate((float)(position.X * _plot.DisplayScale), area);

        if (double.IsNaN(x))
        {
            _report(string.Empty);
            return;
        }

        _report(_describe(x) ?? string.Empty);
    }

    /// <summary>
    /// The index of the sample nearest a position, for a list that is in order. Returns −1 when
    /// there is nothing to point at, or when the pointer is outside the data rather than merely
    /// between two of its points — a readout for a frequency the sweep never visited would be an
    /// invention.
    /// </summary>
    public static int Nearest(IReadOnlyList<double> values, double at)
    {
        if (values.Count == 0) return -1;

        var low = Math.Min(values[0], values[^1]);
        var high = Math.Max(values[0], values[^1]);

        // A little slack either end, so the very first and last points can still be read.
        var slack = (high - low) * 0.02;

        if (at < low - slack || at > high + slack) return -1;

        var best = 0;

        for (var i = 1; i < values.Count; i++)
            if (Math.Abs(values[i] - at) < Math.Abs(values[best] - at)) best = i;

        return best;
    }
}
