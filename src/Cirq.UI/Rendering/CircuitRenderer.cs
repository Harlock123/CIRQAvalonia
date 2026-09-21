using Avalonia;
using Avalonia.Media;
using Cirq.Core.Topology;
using CorePoint = Cirq.Core.Primitives.Point;

namespace Cirq.UI.Rendering;

/// <summary>What to include when a circuit is drawn, and at what scale.</summary>
/// <param name="Zoom">
/// Scale the circuit is being drawn at. Line weights and text are divided by it so they stay a
/// constant size on screen — and, in an export, so a schematic drawn at 3× does not come out with
/// hairline strokes.
/// </param>
/// <param name="SelectedComponent">
/// Drawn in the selection colour, if it is on this canvas — along with anything else carrying
/// <see cref="CircuitComponent.IsSelected"/>, which is how a band selection of several parts is
/// held. Exports pass null and the flags are not set on a loaded document, so neither shows
/// selection highlighting.
/// </param>
/// <param name="ShowInteractiveMarkers">Rings the parts that can be operated by double-clicking.</param>
/// <param name="ShowProbes">Whether probe pennants are drawn.</param>
public sealed record CircuitRenderOptions(
    double Zoom,
    CircuitComponent? SelectedComponent = null,
    bool ShowInteractiveMarkers = true,
    bool ShowProbes = true,
    bool ShowSelection = true);

/// <summary>
/// Draws a whole circuit — wires, symbols, captions and probes — onto any <see cref="ISymbolCanvas"/>.
/// <para>
/// This was part of the canvas control until the schematic needed to come out as a file as well as
/// appear on screen. An export that re-implemented the drawing would have started identical and
/// then quietly fallen behind, so both paths run this instead: what you export is what you see,
/// because it is the same code.
/// </para>
/// <para>
/// What is deliberately <i>not</i> here is anything that belongs to editing rather than to the
/// circuit: the dot grid, terminal dots, hover highlighting and the wire being dragged. Those are
/// the editor talking to you, not part of the drawing, so they stay with the control.
/// </para>
/// </summary>
public static class CircuitRenderer
{
    public static void Draw(ISymbolCanvas canvas, Circuit circuit, CircuitRenderOptions options)
    {
        DrawWires(canvas, circuit, options);
        DrawComponents(canvas, circuit, options);

        if (options.ShowProbes) DrawProbes(canvas, circuit, options);
    }

    // ---- wires -----------------------------------------------------------

    private static void DrawWires(ISymbolCanvas canvas, Circuit circuit, CircuitRenderOptions options)
    {
        foreach (var wire in circuit.Wires)
        {
            if (wire.SourceTerminal is null || wire.TargetTerminal is null) continue;

            var highlight = options.ShowSelection && wire.IsSelected;
            var brush = highlight ? CanvasTheme.SelectionBrush : CanvasTheme.WireBrush;
            var pen = CanvasTheme.Pen(brush, highlight ? 3.0 : 2.0, options.Zoom);
            var points = BuildWirePath(wire).Select(p => new Point(p.X, p.Y)).ToList();
            if (points.Count < 2) continue;

            canvas.DrawGeometry(null, pen, SymbolPath.Polyline(points, false));
        }

        // Junction dots wherever three or more wire ends meet.
        foreach (var junction in FindJunctions(circuit))
            canvas.DrawEllipse(CanvasTheme.WireBrush, null, new Point(junction.X, junction.Y), 4, 4);
    }

    /// <summary>Full orthogonal path of a wire, inserting elbows between consecutive anchors.</summary>
    public static IEnumerable<CorePoint> BuildWirePath(WireSegment wire)
    {
        var anchors = new List<CorePoint> { wire.SourceTerminal!.AbsolutePosition };
        anchors.AddRange(wire.Waypoints);
        anchors.Add(wire.TargetTerminal!.AbsolutePosition);

        yield return anchors[0];
        for (var i = 1; i < anchors.Count; i++)
        {
            foreach (var point in OrthogonalElbow(anchors[i - 1], anchors[i]))
                yield return point;
            yield return anchors[i];
        }
    }

    /// <summary>The intermediate corner, if any, needed to join two points with right angles.</summary>
    public static IEnumerable<CorePoint> OrthogonalElbow(CorePoint from, CorePoint to)
    {
        const double epsilon = 0.01;
        if (Math.Abs(from.X - to.X) < epsilon || Math.Abs(from.Y - to.Y) < epsilon)
            yield break;

        // Travel horizontally first, then vertically.
        yield return new CorePoint(to.X, from.Y);
    }

    private static IEnumerable<CorePoint> FindJunctions(Circuit circuit)
    {
        var counts = new Dictionary<(double, double), int>();
        foreach (var wire in circuit.Wires)
        {
            if (wire.SourceTerminal is null || wire.TargetTerminal is null) continue;
            foreach (var terminal in new[] { wire.SourceTerminal, wire.TargetTerminal })
            {
                var p = terminal.AbsolutePosition;
                var key = (Math.Round(p.X, 2), Math.Round(p.Y, 2));
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        foreach (var ((x, y), count) in counts)
            if (count >= 3)
                yield return new CorePoint(x, y);
    }

    // ---- components ------------------------------------------------------

    private static void DrawComponents(ISymbolCanvas canvas, Circuit circuit, CircuitRenderOptions options)
    {
        // Annotations first, so a box drawn round a section ends up behind the parts in it rather
        // than over the top of them. Ordering them here rather than sorting the circuit keeps the
        // document's order — which is what undo and the parts list use — unchanged.
        var ordered = circuit.Components
            .Where(c => c is IAnnotation)
            .Concat(circuit.Components.Where(c => c is not IAnnotation));

        foreach (var component in ordered)
        {
            using (canvas.PushTransform(
                       Matrix.CreateRotation(component.RotationDegrees * Math.PI / 180.0) *
                       Matrix.CreateTranslation(component.X, component.Y)))
            {
                // Either way of being selected counts: the flag, which a band sets on everything
                // it caught, or being the one part the inspector is editing.
                var selected = options.ShowSelection
                    && (component.IsSelected || ReferenceEquals(component, options.SelectedComponent));

                SymbolRenderer.Draw(canvas, component, options.Zoom, selected);
            }

            // An annotation says what it says; a designator and a value caption under it would be
            // repeating its own text back at it under a name nobody chose.
            if (component is not IAnnotation) DrawComponentLabels(canvas, component, options);

            if (options.ShowInteractiveMarkers && component is IInteractiveComponent)
                DrawInteractiveMarker(canvas, component, options);
        }
    }

    /// <summary>
    /// A small ringed dot on a part that can be operated by double-clicking it. Drawn outside the
    /// symbol, on the opposite side from the designator, so it neither sits on the device nor
    /// collides with its caption.
    /// </summary>
    private static void DrawInteractiveMarker(
        ISymbolCanvas canvas, CircuitComponent component, CircuitRenderOptions options)
    {
        if (options.Zoom < 0.5) return;

        var offset = SymbolRenderer.LabelOffset(component);
        // Just clear of the body rather than beside it: an SPDT's upper throw reaches far enough
        // up and right that a marker tucked against the symbol landed on the contact.
        var centre = new Point(component.X + 26, component.Y - offset - 2);
        var pen = CanvasTheme.Pen(CanvasTheme.ValueBrush, 1.2, options.Zoom);

        canvas.DrawEllipse(null, pen, centre, 5, 5);
        canvas.DrawEllipse(CanvasTheme.ValueBrush, null, centre, 1.8, 1.8);
    }

    private static void DrawComponentLabels(
        ISymbolCanvas canvas, CircuitComponent component, CircuitRenderOptions options)
    {
        if (options.Zoom < 0.35) return;

        var offset = SymbolRenderer.LabelOffset(component);
        var above = new Point(component.X, component.Y - offset);
        var below = new Point(component.X, component.Y + offset);

        SymbolRenderer.DrawCenteredText(canvas, component.Name, above, 11, options.Zoom, CanvasTheme.LabelBrush);

        var value = component.ValueLabel;
        if (!string.IsNullOrEmpty(value))
            SymbolRenderer.DrawCenteredText(canvas, value, below, 11, options.Zoom, CanvasTheme.ValueBrush);
    }

    // ---- probes ----------------------------------------------------------

    private static void DrawProbes(ISymbolCanvas canvas, Circuit circuit, CircuitRenderOptions options)
    {
        foreach (var probe in circuit.Probes)
        {
            if (probe.TargetTerminal is null) continue;

            var position = probe.TargetTerminal.AbsolutePosition;
            var colour = Color.FromArgb(
                probe.TraceColor.A, probe.TraceColor.R, probe.TraceColor.G, probe.TraceColor.B);
            var brush = new SolidColorBrush(colour);
            var pen = CanvasTheme.Pen(brush, 2.0, options.Zoom);

            // The probe wears its trace colour so it can be matched to the waveform, but those
            // colours are chosen to read on the scope's black plot: yellow and cyan on a light
            // canvas are all but invisible. Outlining the pennant in the symbol colour — and
            // haloing the mast behind it — keeps the shape legible on any ground without giving
            // up the colour match. The label follows the theme for the same reason; the pennant
            // sitting beside it is what carries the identity.
            var outline = CanvasTheme.Pen(CanvasTheme.SymbolBrush, 0.9, options.Zoom);
            var halo = CanvasTheme.Pen(CanvasTheme.SymbolBrush, 3.6, options.Zoom);

            // A small pennant marking the probed node.
            var tip = new Point(position.X, position.Y);
            var mast = new Point(position.X + 6, position.Y - 22);
            canvas.DrawLine(halo, tip, mast);
            canvas.DrawLine(pen, tip, mast);
            canvas.DrawGeometry(brush, outline, SymbolPath.Polyline(
                [mast, new Point(mast.X + 18, mast.Y + 5), new Point(mast.X, mast.Y + 10)], true));
            canvas.DrawEllipse(brush, outline, tip, 4, 4);

            if (options.Zoom > 0.5)
                SymbolRenderer.DrawCenteredText(canvas, probe.Label,
                    new Point(mast.X + 30, mast.Y + 5), 10, options.Zoom, CanvasTheme.LabelBrush);
        }
    }

    // ---- bounds ----------------------------------------------------------

    /// <summary>
    /// The rectangle a whole circuit occupies, captions and wire waypoints included.
    /// <para>
    /// Empty for an empty circuit, which the caller has to handle — there is no sensible box to
    /// return for nothing at all.
    /// </para>
    /// </summary>
    public static Rect BoundsOf(Circuit circuit)
    {
        var bounds = default(Rect);
        var any = false;

        foreach (var component in circuit.Components)
        {
            var box = VisualBounds(component);
            bounds = any ? bounds.Union(box) : box;
            any = true;
        }

        foreach (var wire in circuit.Wires)
        {
            if (wire.SourceTerminal is null || wire.TargetTerminal is null) continue;

            foreach (var point in BuildWirePath(wire))
            {
                var box = new Rect(point.X - 4, point.Y - 4, 8, 8);
                bounds = any ? bounds.Union(box) : box;
                any = true;
            }
        }

        // Probes hang up and to the right of the terminal they are clipped to, and their labels
        // reach further still. Left out, a probe on the rightmost node had its name sliced off by
        // the edge of the exported page.
        foreach (var probe in circuit.Probes)
        {
            if (probe.TargetTerminal is null) continue;

            var at = probe.TargetTerminal.AbsolutePosition;
            var box = new Rect(at.X - 6, at.Y - ProbeRise, ProbeReach, ProbeRise + 8);

            bounds = any ? bounds.Union(box) : box;
            any = true;
        }

        return any ? bounds : default;
    }

    /// <summary>How far a probe's pennant and label reach to the right of the probed terminal.</summary>
    /// <remarks>
    /// The mast leans 6 right, the label is centred 30 beyond that, and the widest label anyone
    /// is likely to type takes the rest. Measuring the text properly would mean asking a font
    /// system that an export running headless may not have, for a few units of margin.
    /// </remarks>
    private const double ProbeReach = 130.0;

    /// <summary>How far the pennant stands above the terminal.</summary>
    private const double ProbeRise = 30.0;

    /// <summary>A single component's footprint, symbol and captions together.</summary>
    public static Rect VisualBounds(CircuitComponent component) =>
        Controls.CircuitCanvas.VisualBoundsOf(component);
}
