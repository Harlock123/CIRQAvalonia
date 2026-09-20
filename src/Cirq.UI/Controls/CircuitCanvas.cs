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

    /// <summary>
    /// Raised when a gesture that will change the document begins, and again when it ends. A drag
    /// moves a component on every pointer movement; without a pair of boundaries round it the
    /// undo history would fill with one entry per pixel travelled.
    /// </summary>
    public event EventHandler<string>? InteractiveEditBegan;

    public event EventHandler? InteractiveEditEnded;

    // ---- interaction state ----------------------------------------------

    private Point _panOffset = new(400, 300);
    private Point _lastPointerPosition;
    private bool _isPanning;
    private bool _isDraggingComponent;
    private bool _isBanding;
    private CorePoint _bandStart;
    private CorePoint _bandEnd;
    private readonly List<(CircuitComponent Component, CorePoint Grab)> _dragGroup = [];
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

    /// <summary>
    /// Steps the zoom about the middle of the viewport, which is where the menu and the keyboard
    /// have to work from — the wheel zooms about the pointer instead, because there is one.
    /// </summary>
    public void ZoomBy(double factor)
    {
        var centre = new Point(Bounds.Width / 2, Bounds.Height / 2);
        var worldBefore = ScreenToWorld(centre);

        _viewAdjustedByUser = true;
        Zoom = Math.Clamp(Zoom * factor, CanvasTheme.MinZoom, CanvasTheme.MaxZoom);

        var worldAfter = ScreenToWorld(centre);
        _panOffset += new Point((worldAfter.X - worldBefore.X) * Zoom, (worldAfter.Y - worldBefore.Y) * Zoom);

        InvalidateVisual();
    }

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

        // The extents of what is actually drawn, not of the component centres. A fixed margin
        // round the centres was enough for a resistor and nowhere near enough for a 40-pin board,
        // whose symbol is some eight hundred units tall: fitting to its centre left most of the
        // part off screen, which is the one case where fitting matters most.
        const double margin = 40.0;

        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;

        void Include(double x, double y)
        {
            minX = Math.Min(minX, x);
            maxX = Math.Max(maxX, x);
            minY = Math.Min(minY, y);
            maxY = Math.Max(maxY, y);
        }

        foreach (var component in circuit.Components)
        {
            var bounds = VisualBoundsOf(component);
            Include(bounds.Left, bounds.Top);
            Include(bounds.Right, bounds.Bottom);
        }

        // Wires route orthogonally through waypoints that can sit well outside the parts they
        // join, so a circuit steered round an obstacle is wider than its components are.
        foreach (var wire in circuit.Wires)
            foreach (var waypoint in wire.Waypoints)
                Include(waypoint.X, waypoint.Y);

        minX -= margin;
        maxX += margin;
        minY -= margin;
        maxY += margin;

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

        var canvas = new AvaloniaSymbolCanvas(context);

        using var _ = canvas.PushTransform(
            Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(_panOffset.X, _panOffset.Y));

        CircuitRenderer.Draw(canvas, circuit,
            new CircuitRenderOptions(Zoom, SelectedComponent, ShowInteractiveMarkers));

        // Editing aids rather than part of the circuit, so they stay here and out of an export.
        DrawTerminals(canvas, circuit);
        DrawWireInProgress(canvas);
        DrawSelectionBand(canvas);
    }

    /// <summary>The band as a rectangle, however it was dragged — up, down, left or right.</summary>
    private Rect BandRect() => new(
        Math.Min(_bandStart.X, _bandEnd.X),
        Math.Min(_bandStart.Y, _bandEnd.Y),
        Math.Abs(_bandEnd.X - _bandStart.X),
        Math.Abs(_bandEnd.Y - _bandStart.Y));

    private void DrawSelectionBand(ISymbolCanvas canvas)
    {
        if (!_isBanding) return;

        var band = BandRect();
        if (band.Width < 1 && band.Height < 1) return;

        // Dashed, because a solid rectangle over the schematic reads as a part rather than as
        // something in the middle of being dragged.
        var pen = CanvasTheme.Pen(CanvasTheme.SelectionBrush, 1.5, Zoom, new DashStyle([4, 3], 0));

        canvas.DrawRectangle(null, pen, band);
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

    private void DrawTerminals(ISymbolCanvas context, Circuit circuit)
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

    private void DrawWireInProgress(ISymbolCanvas context)
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
            foreach (var elbow in CircuitRenderer.OrthogonalElbow(anchors[i - 1], anchors[i]))
                path.Add(new Point(elbow.X, elbow.Y));
            path.Add(new Point(anchors[i].X, anchors[i].Y));
        }

        context.DrawGeometry(null, pen, SymbolPath.Polyline(path, false));
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

    /// <summary>
    /// Everything a component puts on the canvas: its symbol and both captions. The designator and
    /// value sit a component-dependent distance above and below the centre — on a board that is
    /// most of the symbol's height away from it — so they are part of the extents, not decoration
    /// on top of them.
    /// </summary>
    public static Rect VisualBoundsOf(CircuitComponent component)
    {
        var bounds = BoundsOf(component);

        // Captions are centred text of about eleven pixels, drawn at the label offset.
        var caption = SymbolRenderer.LabelOffset(component) + 12;

        var top = Math.Min(bounds.Top, component.Y - caption);
        var bottom = Math.Max(bounds.Bottom, component.Y + caption);

        return new Rect(bounds.Left, top, bounds.Width, bottom - top);
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
            var path = CircuitRenderer.BuildWirePath(wire).ToList();
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

        if (_isBanding)
        {
            _bandEnd = world;
            InvalidateVisual();
            return;
        }

        if (_isDraggingComponent && _dragGroup.Count > 0)
        {
            // Snapped on the part under the pointer and everything else moved by the same amount,
            // so a group keeps its shape instead of each part rounding to the grid separately.
            var anchor = _dragGroup[0];
            var snapped = Snap(new CorePoint(world.X - anchor.Grab.X, world.Y - anchor.Grab.Y));
            var shiftX = snapped.X - (world.X - anchor.Grab.X);
            var shiftY = snapped.Y - (world.Y - anchor.Grab.Y);

            foreach (var (part, grab) in _dragGroup)
            {
                part.X = world.X - grab.X + shiftX;
                part.Y = world.Y - grab.Y + shiftY;
            }

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

        if (_isBanding)
        {
            _isBanding = false;
            e.Pointer.Capture(null);

            var band = BandRect();

            // A click rather than a drag: the selection was already cleared on press, so there is
            // nothing to do but let it be a click on empty space.
            if (band.Width > 2 && band.Height > 2 && Circuit is { } circuit)
            {
                var caught = ComponentsWithin(circuit, band);
                SetSelection(caught);

                StatusChanged?.Invoke(this, caught.Count switch
                {
                    0 => "Nothing in the box.",
                    1 => $"Selected {caught[0].Name}",
                    _ => $"Selected {caught.Count} parts",
                });
            }

            InvalidateVisual();
        }

        if (_isDraggingComponent)
        {
            _isDraggingComponent = false;
            _dragGroup.Clear();
            InteractiveEditEnded?.Invoke(this, EventArgs.Empty);
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
            // Pressing a part that is already in a group keeps the group and moves all of it.
            // Pressing one outside the group selects just that part, which is what every other
            // editor does and the only behaviour that lets you get out of a selection.
            if (!component.IsSelected) SetSelection([component]);

            var moving = Selection;
            if (moving.Count == 0) moving = [component];

            _dragGroup.Clear();
            foreach (var part in moving)
                _dragGroup.Add((part, new CorePoint(world.X - part.X, world.Y - part.Y)));

            SelectedComponent = moving.Count == 1 ? moving[0] : null;
            _isDraggingComponent = true;
            _dragGrabOffset = new CorePoint(world.X - component.X, world.Y - component.Y);

            InteractiveEditBegan?.Invoke(this, moving.Count == 1
                ? $"Move {component.ComponentType}"
                : $"Move {moving.Count} parts");

            e.Pointer.Capture(this);
            return;
        }

        var hitWire = WireAt(world);

        if (hitWire is not null)
        {
            SetSelection([]);
            hitWire.IsSelected = true;
            SelectedComponent = null;
            return;
        }

        // Empty canvas: start a band. A press that turns out not to be a drag clears the
        // selection on release, which is what a plain click on nothing has always done.
        SetSelection([]);

        _isBanding = true;
        _bandStart = world;
        _bandEnd = world;
        e.Pointer.Capture(this);
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
    /// <summary>
    /// Everything currently selected. The flag on the parts is what holds a selection, rather
    /// than <see cref="SelectedComponent"/> — that one names the part the inspector is editing,
    /// which is meaningless once there are five of them.
    /// </summary>
    public IReadOnlyList<CircuitComponent> Selection =>
        Circuit is null ? [] : [.. Circuit.Components.Where(c => c.IsSelected)];

    /// <summary>
    /// Replaces the selection. Wires follow rather than being chosen: one is selected exactly when
    /// <b>both</b> of its ends are on selected parts, which is the same rule that decides whether
    /// it can be copied — a wire with one end outside the group has nothing to attach a copy to.
    /// </summary>
    public void SetSelection(IEnumerable<CircuitComponent> components)
    {
        var circuit = Circuit;
        if (circuit is null) return;

        var chosen = components as IReadOnlySet<CircuitComponent> ?? components.ToHashSet();

        foreach (var component in circuit.Components) component.IsSelected = chosen.Contains(component);

        foreach (var wire in circuit.Wires)
        {
            var from = wire.SourceTerminal?.Owner;
            var to = wire.TargetTerminal?.Owner;

            wire.IsSelected = from is not null && to is not null
                && chosen.Contains(from) && chosen.Contains(to);
        }

        SelectedComponent = chosen.Count == 1 ? chosen.First() : null;
    }

    /// <summary>
    /// Everything a band caught: the parts whose symbol lies <b>wholly</b> inside it.
    /// <para>
    /// The test is against the symbol rather than <see cref="VisualBoundsOf"/>, which includes the
    /// caption printed under a part. Dragging a box that visibly encloses three parts and getting
    /// two, because one of them has a long value label hanging below it, is not a rule anybody can
    /// work with.
    /// </para>
    /// </summary>
    public static IReadOnlyList<CircuitComponent> ComponentsWithin(Circuit circuit, Rect band) =>
        [.. circuit.Components.Where(c => band.Contains(BoundsOf(c)))];

    public void RotateSelection()
    {
        var selection = Selection;

        if (selection.Count == 0)
        {
            if (SelectedComponent is not { } single) return;
            selection = [single];
        }

        foreach (var component in selection)
            component.RotationDegrees = (component.RotationDegrees + 90) % 360;

        TopologyChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Deletes the selected component, or the selected wire when no component is selected.</summary>
    /// <summary>
    /// Shows a component that has just appeared — a pasted copy — and makes it the selection so
    /// it can be dragged straight away.
    /// </summary>
    public void BringIntoView(IReadOnlyList<CircuitComponent> components)
    {
        SelectedComponent = components.Count == 1 ? components[0] : null;
        TopologyChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void DeleteSelection()
    {
        var circuit = Circuit;
        if (circuit is null) return;

        var selection = Selection;

        if (selection.Count > 0)
        {
            // Removing a part takes its wires with it, so the wires in the selection need no
            // separate pass — and a wire selected on its own is not in this branch at all.
            foreach (var component in selection) circuit.Remove(component);
            SelectedComponent = null;
        }
        else if (SelectedComponent is { } component)
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
        // The accelerators go first. V on its own picks the Select tool, so Ctrl+V has to be
        // taken out of the way before the plain letters are looked at.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            base.OnKeyDown(e);
            return;
        }

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
