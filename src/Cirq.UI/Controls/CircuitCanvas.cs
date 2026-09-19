using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.UI.Rendering;
using Cirq.UI.ViewModels;
using CorePoint = Cirq.Core.Primitives.Point;

namespace Cirq.UI.Controls;

/// <summary>
/// The schematic editor surface: an infinite, pannable and zoomable grid that draws the circuit
/// and handles placement, selection, wiring and probing.
/// <para>
/// Everything is drawn in a single <see cref="Render"/> pass in world coordinates. Pens are built
/// with their width divided by the zoom, so line weights stay constant on screen no matter how far
/// in the view is scaled.
/// </para>
/// </summary>
public class CircuitCanvas : Control
{
    // ---- styled properties ----------------------------------------------

    public static readonly StyledProperty<Circuit?> CircuitProperty =
        AvaloniaProperty.Register<CircuitCanvas, Circuit?>(nameof(Circuit));

    public static readonly StyledProperty<EditorTool> ActiveToolProperty =
        AvaloniaProperty.Register<CircuitCanvas, EditorTool>(nameof(ActiveTool));

    public static readonly StyledProperty<CircuitComponent?> SelectedComponentProperty =
        AvaloniaProperty.Register<CircuitCanvas, CircuitComponent?>(
            nameof(SelectedComponent), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<PaletteItem?> PendingItemProperty =
        AvaloniaProperty.Register<CircuitCanvas, PaletteItem?>(
            nameof(PendingItem), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public static readonly StyledProperty<double> ZoomProperty =
        AvaloniaProperty.Register<CircuitCanvas, double>(nameof(Zoom), 1.0);

    public static readonly StyledProperty<double> GridSizeProperty =
        AvaloniaProperty.Register<CircuitCanvas, double>(nameof(GridSize), CanvasTheme.DefaultGridSize);

    public static readonly StyledProperty<bool> SnapToGridProperty =
        AvaloniaProperty.Register<CircuitCanvas, bool>(nameof(SnapToGrid), true);

    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<CircuitCanvas, bool>(nameof(ShowGrid), true);

    public static readonly StyledProperty<bool> ShowInteractiveMarkersProperty =
        AvaloniaProperty.Register<CircuitCanvas, bool>(nameof(ShowInteractiveMarkers), true);

    public Circuit? Circuit
    {
        get => GetValue(CircuitProperty);
        set => SetValue(CircuitProperty, value);
    }

    public EditorTool ActiveTool
    {
        get => GetValue(ActiveToolProperty);
        set => SetValue(ActiveToolProperty, value);
    }

    public CircuitComponent? SelectedComponent
    {
        get => GetValue(SelectedComponentProperty);
        set => SetValue(SelectedComponentProperty, value);
    }

    /// <summary>The palette entry waiting to be placed by the next canvas click.</summary>
    public PaletteItem? PendingItem
    {
        get => GetValue(PendingItemProperty);
        set => SetValue(PendingItemProperty, value);
    }

    public double Zoom
    {
        get => GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, Math.Clamp(value, CanvasTheme.MinZoom, CanvasTheme.MaxZoom));
    }

    public double GridSize
    {
        get => GetValue(GridSizeProperty);
        set => SetValue(GridSizeProperty, value);
    }

    public bool SnapToGrid
    {
        get => GetValue(SnapToGridProperty);
        set => SetValue(SnapToGridProperty, value);
    }

    public bool ShowGrid
    {
        get => GetValue(ShowGridProperty);
        set => SetValue(ShowGridProperty, value);
    }

    /// <summary>Whether components that can be double-clicked are marked as such.</summary>
    public bool ShowInteractiveMarkers
    {
        get => GetValue(ShowInteractiveMarkersProperty);
        set => SetValue(ShowInteractiveMarkersProperty, value);
    }

    // ---- events ----------------------------------------------------------

    /// <summary>Raised when components or wires are added or removed, so the engine can recompile.</summary>
    public event EventHandler? TopologyChanged;

    /// <summary>Raised when the probe tool is used on a terminal.</summary>
    public event EventHandler<Terminal>? ProbeRequested;

    /// <summary>Raised with a short description of what the canvas is currently doing.</summary>
    public event EventHandler<string>? StatusChanged;

    // ---- interaction state ----------------------------------------------

    private Point _panOffset = new(400, 300);
    private Point _lastPointerPosition;
    private bool _isPanning;
    private bool _isDraggingComponent;
    private CorePoint _dragGrabOffset;
    private bool _spaceHeld;

    /// <summary>
    /// Set until the control has a real size. The first layout pass then fits the view, because
    /// fitting against a zero-width control would leave the circuit off screen.
    /// </summary>
    private bool _fitPending = true;

    /// <summary>
    /// Cleared until the user pans or zooms. While it is clear the view re-fits whenever the
    /// control is resized, so the circuit stays framed as the window changes size; once the user
    /// has chosen their own view, resizing leaves it alone.
    /// </summary>
    private bool _viewAdjustedByUser;

    private Terminal? _hoverTerminal;
    private Terminal? _wireStart;
    private readonly List<CorePoint> _wireWaypoints = [];
    private CorePoint _wireCursor;

    static CircuitCanvas()
    {
        AffectsRender<CircuitCanvas>(
            CircuitProperty, ActiveToolProperty, SelectedComponentProperty,
            ZoomProperty, GridSizeProperty, ShowGridProperty, PendingItemProperty,
            ShowInteractiveMarkersProperty);
    }

    public CircuitCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>
    /// Backdrop the control paints behind the schematic.
    /// <para>
    /// Resolved on every read rather than captured once. The constructor runs before the desktop
    /// portal has answered what the system theme is, so a latched brush here was the light one
    /// while the symbols — resolved later, on the first render — were the dark ones: pale grey
    /// strokes on a white ground, and unreadable. Reading through the palette keeps the backdrop
    /// and the symbols on the same theme no matter when either is first asked for.
    /// </para>
    /// </summary>
    public IBrush Background => CanvasTheme.BackgroundBrush;

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);

        // Arrange is the first point at which the control knows its real size, so a fit requested
        // before layout (on startup, or right after loading an example) is honoured here. It also
        // re-frames on resize until the user takes over the view themselves.
        if ((_fitPending || !_viewAdjustedByUser) && size.Width > 4 && size.Height > 4)
        {
            _fitPending = false;
            FitTo(size);
        }

        return size;
    }

    /// <summary>
    /// Fits the view now if the control has been laid out, and otherwise defers until it has, so
    /// callers can fit straight after loading a circuit without worrying about layout timing.
    /// </summary>
    public void RequestFit()
    {
        if (Bounds.Width <= 4 || Bounds.Height <= 4)
        {
            _fitPending = true;
            return;
        }

        _fitPending = false;
        _viewAdjustedByUser = false;
        ZoomToFit();
    }

    /// <summary>Centres the view on the circuit's extents, or on the origin when it is empty.</summary>
    public void ZoomToFit() => FitTo(Bounds.Size);

    private void FitTo(Size viewport)
    {
        var circuit = Circuit;
        if (circuit is null || circuit.Components.Count == 0)
        {
            Zoom = 1.0;
            _panOffset = new Point(viewport.Width / 2, viewport.Height / 2);
            InvalidateVisual();
            return;
        }

        const double margin = 80.0;
        var minX = circuit.Components.Min(c => c.X) - margin;
        var maxX = circuit.Components.Max(c => c.X) + margin;
        var minY = circuit.Components.Min(c => c.Y) - margin;
        var maxY = circuit.Components.Max(c => c.Y) + margin;

        var scaleX = viewport.Width / Math.Max(maxX - minX, 1);
        var scaleY = viewport.Height / Math.Max(maxY - minY, 1);
        Zoom = Math.Clamp(Math.Min(scaleX, scaleY), CanvasTheme.MinZoom, 2.0);

        _panOffset = new Point(
            viewport.Width / 2 - (minX + maxX) / 2 * Zoom,
            viewport.Height / 2 - (minY + maxY) / 2 * Zoom);

        InvalidateVisual();
    }

    // ---- coordinate transforms -------------------------------------------

    public Point WorldToScreen(CorePoint world) =>
        new(world.X * Zoom + _panOffset.X, world.Y * Zoom + _panOffset.Y);

    public CorePoint ScreenToWorld(Point screen) =>
        new((screen.X - _panOffset.X) / Zoom, (screen.Y - _panOffset.Y) / Zoom);

    private CorePoint Snap(CorePoint world)
    {
        if (!SnapToGrid || GridSize <= 0) return world;
        return new CorePoint(
            Math.Round(world.X / GridSize) * GridSize,
            Math.Round(world.Y / GridSize) * GridSize);
    }

    // ---- rendering -------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Background, new Rect(Bounds.Size));

        if (ShowGrid) DrawGrid(context);

        var circuit = Circuit;
        if (circuit is null) return;

        using var _ = context.PushTransform(
            Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(_panOffset.X, _panOffset.Y));

        DrawWires(context, circuit);
        DrawComponents(context, circuit);
        DrawTerminals(context, circuit);
        DrawProbes(context, circuit);
        DrawWireInProgress(context);
    }

    private void DrawGrid(DrawingContext context)
    {
        var step = GridSize * Zoom;
        if (step < 4) return;   // Too dense to be useful; skip it rather than grey the canvas out.

        var dotPen = new Pen(new SolidColorBrush(CanvasTheme.GridDot), 1);
        var majorPen = new Pen(new SolidColorBrush(CanvasTheme.GridMajor), 1);

        var startX = _panOffset.X % step;
        var startY = _panOffset.Y % step;
        var majorEvery = 5;

        var indexX = (int)Math.Floor(-_panOffset.X / step);
        for (var x = startX; x < Bounds.Width; x += step, indexX++)
        {
            var isMajor = indexX % majorEvery == 0;
            if (!isMajor && step < 10) continue;

            var indexY = (int)Math.Floor(-_panOffset.Y / step);
            for (var y = startY; y < Bounds.Height; y += step, indexY++)
            {
                var major = isMajor && indexY % majorEvery == 0;
                if (!major && step < 10) continue;
                var pen = major ? majorPen : dotPen;
                var radius = major ? 1.1 : 0.6;
                context.DrawEllipse(pen.Brush, null, new Point(x, y), radius, radius);
            }
        }
    }

    private void DrawWires(DrawingContext context, Circuit circuit)
    {
        foreach (var wire in circuit.Wires)
        {
            if (wire.SourceTerminal is null || wire.TargetTerminal is null) continue;

            var brush = wire.IsSelected ? CanvasTheme.SelectionBrush : CanvasTheme.WireBrush;
            var pen = CanvasTheme.Pen(brush, wire.IsSelected ? 3.0 : 2.0, Zoom);
            var points = BuildWirePath(wire).Select(p => new Point(p.X, p.Y)).ToList();
            if (points.Count < 2) continue;

            context.DrawGeometry(null, pen, new PolylineGeometry(points, false));
        }

        // Junction dots wherever three or more wire ends meet.
        foreach (var junction in FindJunctions(circuit))
            context.DrawEllipse(CanvasTheme.WireBrush, null, new Point(junction.X, junction.Y), 4, 4);
    }

    /// <summary>Full orthogonal path of a wire, inserting elbows between consecutive anchors.</summary>
    public static IEnumerable<CorePoint> BuildWirePath(WireSegment wire)
    {
        var anchors = new List<CorePoint> { wire.SourceTerminal.AbsolutePosition };
        anchors.AddRange(wire.Waypoints);
        anchors.Add(wire.TargetTerminal.AbsolutePosition);

        yield return anchors[0];
        for (var i = 1; i < anchors.Count; i++)
        {
            foreach (var point in OrthogonalElbow(anchors[i - 1], anchors[i]))
                yield return point;
            yield return anchors[i];
        }
    }

    /// <summary>The intermediate corner, if any, needed to join two points with right angles.</summary>
    private static IEnumerable<CorePoint> OrthogonalElbow(CorePoint from, CorePoint to)
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

    private void DrawComponents(DrawingContext context, Circuit circuit)
    {
        foreach (var component in circuit.Components)
        {
            using (context.PushTransform(
                       Matrix.CreateRotation(component.RotationDegrees * Math.PI / 180.0) *
                       Matrix.CreateTranslation(component.X, component.Y)))
            {
                SymbolRenderer.Draw(context, component, Zoom, ReferenceEquals(component, SelectedComponent));
            }

            DrawComponentLabels(context, component);

            if (ShowInteractiveMarkers && component is IInteractiveComponent)
                DrawInteractiveMarker(context, component);
        }
    }

    /// <summary>
    /// A small ringed dot on a part that can be operated by double-clicking it. Drawn outside the
    /// symbol, on the opposite side from the designator, so it neither sits on the device nor
    /// collides with its caption.
    /// </summary>
    private void DrawInteractiveMarker(DrawingContext context, CircuitComponent component)
    {
        if (Zoom < 0.5) return;

        var offset = SymbolRenderer.LabelOffset(component);
        // Just clear of the body rather than beside it: an SPDT's upper throw reaches far enough
        // up and right that a marker tucked against the symbol landed on the contact.
        var centre = new Point(component.X + 26, component.Y - offset - 2);
        var pen = CanvasTheme.Pen(CanvasTheme.ValueBrush, 1.2, Zoom);

        context.DrawEllipse(null, pen, centre, 5, 5);
        context.DrawEllipse(CanvasTheme.ValueBrush, null, centre, 1.8, 1.8);
    }

    private void DrawComponentLabels(DrawingContext context, CircuitComponent component)
    {
        if (Zoom < 0.35) return;

        var offset = SymbolRenderer.LabelOffset(component);
        var above = new Point(component.X, component.Y - offset);
        var below = new Point(component.X, component.Y + offset);

        SymbolRenderer.DrawCenteredText(context, component.Name, above, 11, Zoom, CanvasTheme.LabelBrush);

        var value = component.ValueLabel;
        if (!string.IsNullOrEmpty(value))
            SymbolRenderer.DrawCenteredText(context, value, below, 11, Zoom, CanvasTheme.ValueBrush);
    }

    private void DrawTerminals(DrawingContext context, Circuit circuit)
    {
        var showAll = ActiveTool is EditorTool.Wire or EditorTool.Probe;

        foreach (var component in circuit.Components)
        {
            foreach (var terminal in component.Terminals)
            {
                var isHover = ReferenceEquals(terminal, _hoverTerminal);
                if (!showAll && !isHover && !ReferenceEquals(component, SelectedComponent)) continue;

                var position = terminal.AbsolutePosition;
                var brush = isHover ? CanvasTheme.TerminalHoverBrush : CanvasTheme.TerminalBrush;
                var radius = isHover ? CanvasTheme.TerminalRadius * 1.8 : CanvasTheme.TerminalRadius;

                context.DrawEllipse(brush, null, new Point(position.X, position.Y), radius, radius);

                if (isHover && Zoom > 0.5)
                    SymbolRenderer.DrawCenteredText(context, terminal.Name,
                        new Point(position.X, position.Y - 14), 10, Zoom, CanvasTheme.TerminalHoverBrush);
            }
        }
    }

    private void DrawProbes(DrawingContext context, Circuit circuit)
    {
        foreach (var probe in circuit.Probes)
        {
            if (probe.TargetTerminal is null) continue;
            var position = probe.TargetTerminal.AbsolutePosition;
            var colour = Color.FromArgb(probe.TraceColor.A, probe.TraceColor.R, probe.TraceColor.G, probe.TraceColor.B);
            var brush = new SolidColorBrush(colour);
            var pen = CanvasTheme.Pen(brush, 2.0, Zoom);

            // The probe wears its trace colour so it can be matched to the waveform, but those
            // colours are chosen to read on the scope's black plot: yellow and cyan on a light
            // canvas are all but invisible. Outlining the pennant in the symbol colour — and
            // haloing the mast behind it — keeps the shape legible on any ground without giving
            // up the colour match. The label follows the theme for the same reason; the pennant
            // sitting beside it is what carries the identity.
            var outline = CanvasTheme.Pen(CanvasTheme.SymbolBrush, 0.9, Zoom);
            var halo = CanvasTheme.Pen(CanvasTheme.SymbolBrush, 3.6, Zoom);

            // A small pennant marking the probed node.
            var tip = new Point(position.X, position.Y);
            var mast = new Point(position.X + 6, position.Y - 22);
            context.DrawLine(halo, tip, mast);
            context.DrawLine(pen, tip, mast);
            context.DrawGeometry(brush, outline, new PolylineGeometry(
                [mast, new Point(mast.X + 18, mast.Y + 5), new Point(mast.X, mast.Y + 10)], true));
            context.DrawEllipse(brush, outline, tip, 4, 4);

            if (Zoom > 0.5)
                SymbolRenderer.DrawCenteredText(context, probe.Label,
                    new Point(mast.X + 30, mast.Y + 5), 10, Zoom, CanvasTheme.LabelBrush);
        }
    }

    private void DrawWireInProgress(DrawingContext context)
    {
        if (_wireStart is null) return;

        var pen = CanvasTheme.Pen(CanvasTheme.TerminalHoverBrush, 2.0, Zoom,
            new DashStyle([4, 3], 0));

        var anchors = new List<CorePoint> { _wireStart.AbsolutePosition };
        anchors.AddRange(_wireWaypoints);
        anchors.Add(_hoverTerminal?.AbsolutePosition ?? _wireCursor);

        var path = new List<Point> { new(anchors[0].X, anchors[0].Y) };
        for (var i = 1; i < anchors.Count; i++)
        {
            foreach (var elbow in OrthogonalElbow(anchors[i - 1], anchors[i]))
                path.Add(new Point(elbow.X, elbow.Y));
            path.Add(new Point(anchors[i].X, anchors[i].Y));
        }

        context.DrawGeometry(null, pen, new PolylineGeometry(path, false));
    }

    // ---- hit testing -----------------------------------------------------

    /// <summary>Finds the terminal nearest the pointer, within the screen-space hit radius.</summary>
    public Terminal? TerminalAt(CorePoint world)
    {
        var circuit = Circuit;
        if (circuit is null) return null;

        var radius = CanvasTheme.TerminalHitRadius / Zoom;
        Terminal? best = null;
        var bestDistance = radius;

        foreach (var component in circuit.Components)
        {
            foreach (var terminal in component.Terminals)
            {
                var distance = terminal.AbsolutePosition.DistanceTo(world);
                if (distance > bestDistance) continue;
                bestDistance = distance;
                best = terminal;
            }
        }

        return best;
    }

    /// <summary>Topmost component whose body contains the point, searched in reverse draw order.</summary>
    public CircuitComponent? ComponentAt(CorePoint world)
    {
        var circuit = Circuit;
        if (circuit is null) return null;

        for (var i = circuit.Components.Count - 1; i >= 0; i--)
        {
            var component = circuit.Components[i];
            if (BoundsOf(component).Contains(new Point(world.X, world.Y))) return component;
        }

        return null;
    }

    /// <summary>World-space bounding box of a component, including its pins.</summary>
    public static Rect BoundsOf(CircuitComponent component)
    {
        var halfWidth = 26.0;
        var halfHeight = 22.0;

        foreach (var terminal in component.Terminals)
        {
            var offset = terminal.RotatedOffset;
            halfWidth = Math.Max(halfWidth, Math.Abs(offset.X));
            halfHeight = Math.Max(halfHeight, Math.Abs(offset.Y));
        }

        return new Rect(
            component.X - halfWidth, component.Y - halfHeight,
            halfWidth * 2, halfHeight * 2);
    }

    /// <summary>Wire whose drawn path passes within a few pixels of the point.</summary>
    public WireSegment? WireAt(CorePoint world)
    {
        var circuit = Circuit;
        if (circuit is null) return null;

        var tolerance = 6.0 / Zoom;

        foreach (var wire in circuit.Wires)
        {
            if (wire.SourceTerminal is null || wire.TargetTerminal is null) continue;
            var path = BuildWirePath(wire).ToList();
            for (var i = 1; i < path.Count; i++)
                if (DistanceToSegment(world, path[i - 1], path[i]) <= tolerance)
                    return wire;
        }

        return null;
    }

    private static double DistanceToSegment(CorePoint p, CorePoint a, CorePoint b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var lengthSquared = dx * dx + dy * dy;

        if (lengthSquared < 1e-9) return p.DistanceTo(a);

        var t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lengthSquared, 0, 1);
        return p.DistanceTo(new CorePoint(a.X + t * dx, a.Y + t * dy));
    }

    // ---- pointer input ---------------------------------------------------

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var screen = e.GetPosition(this);
        var world = ScreenToWorld(screen);

        if (_isPanning)
        {
            var delta = screen - _lastPointerPosition;
            _viewAdjustedByUser = true;
            _panOffset += delta;
            _lastPointerPosition = screen;
            InvalidateVisual();
            return;
        }

        if (_isDraggingComponent && SelectedComponent is { } dragged)
        {
            var target = Snap(new CorePoint(world.X - _dragGrabOffset.X, world.Y - _dragGrabOffset.Y));
            dragged.X = target.X;
            dragged.Y = target.Y;
            InvalidateVisual();
            return;
        }

        var previousHover = _hoverTerminal;
        _hoverTerminal = TerminalAt(world);
        _wireCursor = Snap(world);

        if (!ReferenceEquals(previousHover, _hoverTerminal) || _wireStart is not null)
            InvalidateVisual();

        Cursor = new Cursor(ActiveTool switch
        {
            EditorTool.Wire => StandardCursorType.Cross,
            EditorTool.Probe => StandardCursorType.Hand,
            EditorTool.Delete => StandardCursorType.No,
            _ when _hoverTerminal is not null => StandardCursorType.Cross,
            _ when ComponentAt(world) is IInteractiveComponent => StandardCursorType.Hand,
            _ => StandardCursorType.Arrow,
        });

        base.OnPointerMoved(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();

        var screen = e.GetPosition(this);
        var world = ScreenToWorld(screen);
        var properties = e.GetCurrentPoint(this).Properties;
        _lastPointerPosition = screen;

        if (properties.IsMiddleButtonPressed || (_spaceHeld && properties.IsLeftButtonPressed))
        {
            _isPanning = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (properties.IsRightButtonPressed)
        {
            HandleRightClick(world);
            e.Handled = true;
            return;
        }

        if (!properties.IsLeftButtonPressed)
        {
            base.OnPointerPressed(e);
            return;
        }

        // Placing a component from the palette takes priority over every other interaction.
        if (PendingItem is { } pending)
        {
            PlaceComponent(pending, Snap(world));
            e.Handled = true;
            return;
        }

        switch (ActiveTool)
        {
            case EditorTool.Wire:
                HandleWireClick(world);
                break;
            case EditorTool.Probe:
                HandleProbeClick(world);
                break;
            case EditorTool.Delete:
                HandleDeleteClick(world);
                break;
            default:
                HandleSelectClick(world, screen, e);
                break;
        }

        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        if (_isPanning)
        {
            _isPanning = false;
            e.Pointer.Capture(null);
        }

        if (_isDraggingComponent)
        {
            _isDraggingComponent = false;
            e.Pointer.Capture(null);
            TopologyChanged?.Invoke(this, EventArgs.Empty);
        }

        base.OnPointerReleased(e);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        var screen = e.GetPosition(this);
        var worldBefore = ScreenToWorld(screen);

        _viewAdjustedByUser = true;
        var factor = e.Delta.Y > 0 ? 1.12 : 1 / 1.12;
        Zoom = Math.Clamp(Zoom * factor, CanvasTheme.MinZoom, CanvasTheme.MaxZoom);

        // Keep the point under the cursor anchored while zooming.
        var worldAfter = ScreenToWorld(screen);
        _panOffset += new Point((worldAfter.X - worldBefore.X) * Zoom, (worldAfter.Y - worldBefore.Y) * Zoom);

        InvalidateVisual();
        e.Handled = true;
    }

    // ---- interaction handlers --------------------------------------------

    private void PlaceComponent(PaletteItem item, CorePoint position)
    {
        var circuit = Circuit;
        if (circuit is null) return;

        var component = item.Create();
        component.X = position.X;
        component.Y = position.Y;
        circuit.Add(component);

        SelectedComponent = component;
        PendingItem = null;
        ActiveTool = EditorTool.Select;
        TopologyChanged?.Invoke(this, EventArgs.Empty);
        StatusChanged?.Invoke(this, $"Placed {component.Name}");
    }

    private void HandleSelectClick(CorePoint world, Point screen, PointerPressedEventArgs e)
    {
        var component = ComponentAt(world);

        // Double-clicking an operable device flips it, so a switch behaves like a switch instead
        // of something you have to go to the inspector to change.
        if (component is IInteractiveComponent interactive && e.ClickCount >= 2)
        {
            SelectedComponent = component;
            interactive.Interact();
            StatusChanged?.Invoke(this, $"{component.Name}: {component.ValueLabel}");
            InvalidateVisual();
            return;
        }

        if (component is not null)
        {
            SelectedComponent = component;
            foreach (var wire in Circuit!.Wires) wire.IsSelected = false;

            _isDraggingComponent = true;
            _dragGrabOffset = new CorePoint(world.X - component.X, world.Y - component.Y);
            e.Pointer.Capture(this);
            return;
        }

        var hitWire = WireAt(world);
        foreach (var wire in Circuit!.Wires) wire.IsSelected = ReferenceEquals(wire, hitWire);

        SelectedComponent = null;
    }

    private void HandleWireClick(CorePoint world)
    {
        var circuit = Circuit;
        if (circuit is null) return;

        var terminal = TerminalAt(world);

        if (_wireStart is null)
        {
            if (terminal is null)
            {
                StatusChanged?.Invoke(this, "Start a wire on a terminal.");
                return;
            }

            _wireStart = terminal;
            _wireWaypoints.Clear();
            StatusChanged?.Invoke(this, $"Wiring from {terminal}. Click a terminal to finish, Esc to cancel.");
            return;
        }

        if (terminal is null)
        {
            // Empty space adds a routing waypoint.
            _wireWaypoints.Add(Snap(world));
            return;
        }

        if (terminal.Equals(_wireStart))
        {
            CancelWire();
            return;
        }

        var segment = circuit.Connect(_wireStart, terminal);
        foreach (var waypoint in _wireWaypoints) segment.Waypoints.Add(waypoint);

        StatusChanged?.Invoke(this, $"Connected {_wireStart} to {terminal}");
        _wireStart = null;
        _wireWaypoints.Clear();
        TopologyChanged?.Invoke(this, EventArgs.Empty);
    }

    private void HandleProbeClick(CorePoint world)
    {
        var terminal = TerminalAt(world);
        if (terminal is null)
        {
            StatusChanged?.Invoke(this, "Click a terminal to attach a probe.");
            return;
        }

        ProbeRequested?.Invoke(this, terminal);
    }

    private void HandleDeleteClick(CorePoint world)
    {
        var circuit = Circuit;
        if (circuit is null) return;

        if (ComponentAt(world) is { } component)
        {
            circuit.Remove(component);
            if (ReferenceEquals(component, SelectedComponent)) SelectedComponent = null;
            TopologyChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, $"Deleted {component.Name}");
            return;
        }

        if (WireAt(world) is { } wire)
        {
            circuit.Remove(wire);
            TopologyChanged?.Invoke(this, EventArgs.Empty);
            StatusChanged?.Invoke(this, "Deleted wire");
        }
    }

    private void HandleRightClick(CorePoint world)
    {
        if (_wireStart is not null)
        {
            CancelWire();
            return;
        }

        if (ComponentAt(world) is { } component)
        {
            SelectedComponent = component;
            RotateSelection();
        }
    }

    private void CancelWire()
    {
        _wireStart = null;
        _wireWaypoints.Clear();
        StatusChanged?.Invoke(this, "Wire cancelled");
        InvalidateVisual();
    }

    /// <summary>Rotates the selected component by 90 degrees.</summary>
    public void RotateSelection()
    {
        if (SelectedComponent is not { } component) return;
        component.RotationDegrees = (component.RotationDegrees + 90) % 360;
        TopologyChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Deletes the selected component, or the selected wire when no component is selected.</summary>
    public void DeleteSelection()
    {
        var circuit = Circuit;
        if (circuit is null) return;

        if (SelectedComponent is { } component)
        {
            circuit.Remove(component);
            SelectedComponent = null;
        }
        else
        {
            foreach (var wire in circuit.Wires.Where(w => w.IsSelected).ToList())
                circuit.Remove(wire);
        }

        TopologyChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    // ---- keyboard --------------------------------------------------------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                _spaceHeld = true;
                break;
            case Key.Escape:
                if (_wireStart is not null) CancelWire();
                else SelectedComponent = null;
                break;
            case Key.R:
                RotateSelection();
                break;
            case Key.Delete or Key.Back:
                DeleteSelection();
                break;
            case Key.W:
                ActiveTool = EditorTool.Wire;
                break;
            case Key.V:
                ActiveTool = EditorTool.Select;
                break;
            case Key.P:
                ActiveTool = EditorTool.Probe;
                break;
            case Key.F:
                ZoomToFit();
                break;
        }

        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Space) _spaceHeld = false;
        base.OnKeyUp(e);
    }
}
