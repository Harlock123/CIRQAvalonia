using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.UI.Rendering;
using Cirq.UI.Services;
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

    public static readonly StyledProperty<bool> ShowCurrentFlowProperty =
        AvaloniaProperty.Register<CircuitCanvas, bool>(nameof(ShowCurrentFlow));

    /// <summary>
    /// How much current each wire is carrying, refreshed by whoever is running the simulation.
    /// Null, or an empty map, draws nothing.
    /// </summary>
    public static readonly StyledProperty<IReadOnlyDictionary<WireSegment, double>?> WireCurrentsProperty =
        AvaloniaProperty.Register<CircuitCanvas, IReadOnlyDictionary<WireSegment, double>?>(nameof(WireCurrents));

    /// <summary>
    /// Which page of the drawing is on screen, or null for a document that has only one.
    /// <para>
    /// Everything the canvas enumerates goes through <see cref="Visible"/> rather than through the
    /// circuit directly, and that is not tidiness: a part left out of the drawing but still hit
    /// tested is a part you can select, drag and delete without being able to see it.
    /// </para>
    /// </summary>
    public static readonly StyledProperty<string?> SheetProperty =
        AvaloniaProperty.Register<CircuitCanvas, string?>(nameof(Sheet));

    public static readonly StyledProperty<bool> ShowHoverDetailsProperty =
        AvaloniaProperty.Register<CircuitCanvas, bool>(nameof(ShowHoverDetails), true);

    /// <summary>
    /// Whether the measured figures are written onto the drawing. The hover card shows them
    /// regardless — one card on the part under the pointer costs nothing and hides nothing, which
    /// is not true of a figure against every net at once.
    /// </summary>
    public static readonly StyledProperty<bool> ShowLiveValuesProperty =
        AvaloniaProperty.Register<CircuitCanvas, bool>(nameof(ShowLiveValues));

    /// <summary>
    /// What the circuit was doing at the last solved point, refreshed by whoever is running it.
    /// Null, or an empty snapshot, leaves the readings off the card as well as off the drawing.
    /// </summary>
    public static readonly StyledProperty<LiveSnapshot?> LiveValuesProperty =
        AvaloniaProperty.Register<CircuitCanvas, LiveSnapshot?>(nameof(LiveValues));

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

    /// <summary>Whether the dots showing current moving along the wires are drawn.</summary>
    public bool ShowCurrentFlow
    {
        get => GetValue(ShowCurrentFlowProperty);
        set => SetValue(ShowCurrentFlowProperty, value);
    }

    /// <summary>What each wire is carrying, for those dots.</summary>
    public IReadOnlyDictionary<WireSegment, double>? WireCurrents
    {
        get => GetValue(WireCurrentsProperty);
        set => SetValue(WireCurrentsProperty, value);
    }

    public string? Sheet
    {
        get => GetValue(SheetProperty);
        set => SetValue(SheetProperty, value);
    }

    /// <summary>
    /// Leaves a page. Whatever was selected on it is dropped, because a selection nobody can see is
    /// a Delete key pointed at parts on another page — and the handles, the hover and the pending
    /// wire all belong to the page they were started on.
    /// </summary>
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != SheetProperty) return;

        if (Circuit is { } circuit)
            foreach (var component in circuit.Components)
                component.IsSelected = false;

        if (SelectedComponent is { } chosen && !Visible.Contains(chosen)) SelectedComponent = null;

        // A wire half-drawn on the page just left has nowhere to land, and the hover belongs to
        // whatever the pointer was over there.
        _wireStart = null;
        _wireWaypoints.Clear();
        _hoverTerminal = null;
        _hoverComponent = null;
    }

    /// <summary>What is on the page being shown. Everything, for a document with one page.</summary>
    private IReadOnlyList<CircuitComponent> Visible =>
        Circuit is not { } circuit ? [] : [.. circuit.OnSheet(Sheet)];

    public bool ShowLiveValues
    {
        get => GetValue(ShowLiveValuesProperty);
        set => SetValue(ShowLiveValuesProperty, value);
    }

    public LiveSnapshot? LiveValues
    {
        get => GetValue(LiveValuesProperty);
        set => SetValue(LiveValuesProperty, value);
    }

    /// <summary>Whether components that can be double-clicked are marked as such.</summary>
    public bool ShowInteractiveMarkers
    {
        get => GetValue(ShowInteractiveMarkersProperty);
        set => SetValue(ShowInteractiveMarkersProperty, value);
    }

    /// <summary>Whether resting the pointer on a part describes it without selecting it.</summary>
    public bool ShowHoverDetails
    {
        get => GetValue(ShowHoverDetailsProperty);
        set => SetValue(ShowHoverDetailsProperty, value);
    }

    // ---- events ----------------------------------------------------------

    /// <summary>Raised when components or wires are added or removed, so the engine can recompile.</summary>
    public event EventHandler? TopologyChanged;

    /// <summary>Raised when the probe tool is used on a terminal.</summary>
    public event EventHandler<Terminal>? ProbeRequested;

    /// <summary>
    /// Raised when a terminal is shift-clicked with the probe tool, to become the selected
    /// probe's reference point.
    /// </summary>
    public event EventHandler<Terminal>? ProbeReferenceRequested;

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
    private CircuitComponent? _hoverComponent;
    private Point _hoverPointer;
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

    /// <summary>
    /// The net being picked out, or null when none is. Held here rather than on the view model
    /// because it is a thing the canvas is showing rather than a property of the circuit.
    /// </summary>
    private NetHighlight? _highlightedNet;
    private Terminal? _wireStart;
    private readonly List<CorePoint> _wireWaypoints = [];
    private CorePoint _wireCursor;

    static CircuitCanvas()
    {
        AffectsRender<CircuitCanvas>(
            CircuitProperty, ActiveToolProperty, SelectedComponentProperty,
            ZoomProperty, GridSizeProperty, ShowGridProperty, PendingItemProperty, ShowHoverDetailsProperty,
            ShowCurrentFlowProperty, WireCurrentsProperty,
            ShowLiveValuesProperty, LiveValuesProperty, SheetProperty,
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
    /// Brings one part into the middle of the view, without changing the zoom.
    /// <para>
    /// The zoom is left alone deliberately: somebody looking for R17 on a big sheet wants to be
    /// taken to it at the scale they were working at, not dropped into a close-up of one resistor
    /// with no idea what is around it.
    /// </para>
    /// </summary>
    public void CentreOn(CircuitComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        var box = VisualBoundsOf(component);

        _viewAdjustedByUser = true;
        _panOffset = new Point(
            (Bounds.Width / 2) - (box.Center.X * Zoom),
            (Bounds.Height / 2) - (box.Center.Y * Zoom));

        InvalidateVisual();
    }

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
        if (circuit is null || Visible.Count == 0)
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

        foreach (var component in Visible)
        {
            var bounds = VisualBoundsOf(component);
            Include(bounds.Left, bounds.Top);
            Include(bounds.Right, bounds.Bottom);
        }

        // Wires route orthogonally through waypoints that can sit well outside the parts they
        // join, so a circuit steered round an obstacle is wider than its components are.
        foreach (var wire in circuit.WiresOn(Sheet))
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

        using (canvas.PushTransform(
                   Matrix.CreateScale(Zoom, Zoom) * Matrix.CreateTranslation(_panOffset.X, _panOffset.Y)))
        {
            CircuitRenderer.Draw(canvas, circuit,
                new CircuitRenderOptions(
                    Zoom, SelectedComponent, ShowInteractiveMarkers, HighlightedNet: _highlightedNet,
                    Live: ShowLiveValues ? LiveValues : null, Sheet: Sheet));

            // Editing aids rather than part of the circuit, so they stay here and out of an export.
            DrawCurrentFlow(canvas, circuit);
            DrawTerminals(canvas, circuit);
            DrawWireInProgress(canvas);
            DrawSelectionBand(canvas);
        }

        // Outside the transform, and deliberately: the card is a fixed size on the screen rather
        // than part of the drawing, so it does not shrink as you zoom out of the circuit it is
        // describing. Being here also keeps it out of an export, like the grid.
        DrawHoverCard(context);
    }

    /// <summary>
    /// Dots travelling along each wire at a rate set by what it is carrying.
    /// <para>
    /// Here rather than in <see cref="CircuitRenderer"/> because it is not part of the drawing.
    /// An exported schematic is a still, and a still of moving dots says nothing at all — it
    /// would be a row of marks in whatever positions the clock happened to be at.
    /// </para>
    /// <para>
    /// The colour is fixed rather than themed, for the same reason the colour bands on the hover
    /// card are: it is an overlay that has to read against a dark canvas, a light one and pure
    /// black alike, and the themes' own inks are chosen to blend with their backgrounds.
    /// </para>
    /// </summary>
    private void DrawCurrentFlow(ISymbolCanvas canvas, Circuit circuit)
    {
        if (!ShowCurrentFlow) return;
        if (WireCurrents is not { Count: > 0 } currents) return;

        var seconds = Environment.TickCount64 / 1000.0;
        var radius = Math.Clamp(3.0 / Zoom, 1.5, 6.0);

        foreach (var wire in circuit.WiresOn(Sheet))
        {
            if (!currents.TryGetValue(wire, out var amps)) continue;
            if (wire.SourceTerminal is null || wire.TargetTerminal is null) continue;

            var path = CircuitRenderer.BuildWirePath(wire)
                .Select(p => new Point(p.X, p.Y))
                .ToList();

            foreach (var dot in CurrentFlow.Dots(path, amps, seconds))
                canvas.DrawEllipse(FlowBrush, null, dot, radius, radius);
        }
    }

    /// <summary>Amber, which reads on every theme the application has.</summary>
    private static readonly IBrush FlowBrush =
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.FromRgb(0xFF, 0xB7, 0x03));

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

    /// <summary>
    /// Describes the part under the pointer without selecting it.
    /// <para>
    /// Drawn outside the world transform, and deliberately: the card is a fixed size on the
    /// screen rather than part of the drawing, so it does not shrink as you zoom out of the
    /// circuit it is describing. Being here also keeps it out of an export, like the grid.
    /// </para>
    /// </summary>
    private void DrawHoverCard(DrawingContext context)
    {
        if (!ShowHoverDetails || _hoverComponent is not { } component) return;
        if (_isPanning || _isBanding || _isDraggingComponent) return;

        // The card carries the readings whether or not the drawing is annotated: it describes one
        // part, and the reason the annotation has a switch is that it describes all of them.
        HoverCard.Draw(
            context, ComponentSummary.For(component, LiveValues?.For(component)),
            _hoverPointer, Bounds.Size);
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

        foreach (var component in Visible)
        {
            foreach (var terminal in component.Terminals)
            {
                // Every pin on a highlighted net gets a dot, whether or not a wire reaches it.
                // That is most of the value: a pin joined by a net label rather than by a line has
                // nothing on the drawing to say so, and it is the one you came here to find.
                if (_highlightedNet?.Terminals.Contains(terminal) == true)
                {
                    var spot = terminal.AbsolutePosition;

                    context.DrawEllipse(CanvasTheme.NetHighlightBrush, null,
                        new Point(spot.X, spot.Y), CanvasTheme.TerminalRadius * 1.6,
                        CanvasTheme.TerminalRadius * 1.6);
                }

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

        foreach (var component in Visible)
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

        var visible = Visible;

        for (var i = visible.Count - 1; i >= 0; i--)
        {
            var component = visible[i];
            if (BoundsOf(component).Contains(new Point(world.X, world.Y))) return component;
        }

        return null;
    }

    /// <summary>World-space bounding box of a component, including its pins.</summary>
    public static Rect BoundsOf(CircuitComponent component)
    {
        // A block states its own size, since its pins sit on its edges rather than defining them.
        if (component is Cirq.Components.Hierarchy.Subcircuit block)
        {
            return new Rect(
                component.X - block.HalfWidth, component.Y - block.HalfHeight,
                block.HalfWidth * 2, block.HalfHeight * 2);
        }

        // An annotation has no pins to infer a size from, so it states its own.
        if (component is IAnnotation annotation)
        {
            return new Rect(
                component.X - annotation.HalfWidth, component.Y - annotation.HalfHeight,
                annotation.HalfWidth * 2, annotation.HalfHeight * 2);
        }

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

        // An annotation carries no captions, so its extents are exactly its own.
        if (component is IAnnotation) return bounds;

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

        foreach (var wire in circuit.WiresOn(Sheet))
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

        // The card is for resting the pointer on a part, so it stays out of the way of anything
        // being done with the pointer held down, and out of the way of a pin about to be wired.
        var previousComponent = _hoverComponent;
        var quiet = ActiveTool == EditorTool.Select && _hoverTerminal is null && PendingItem is null;

        _hoverComponent = quiet ? ComponentAt(world) : null;
        _hoverPointer = screen;

        if (!ReferenceEquals(previousHover, _hoverTerminal)
            || !ReferenceEquals(previousComponent, _hoverComponent)
            || (_hoverComponent is not null && ShowHoverDetails)
            || _wireStart is not null)
        {
            InvalidateVisual();
        }

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

    protected override void OnPointerExited(PointerEventArgs e)
    {
        _hoverComponent = null;
        _hoverTerminal = null;

        InvalidateVisual();
        base.OnPointerExited(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();

        _hoverComponent = null;

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
                HandleProbeClick(world, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
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
                var caught = ComponentsWithin(circuit, band, Sheet);
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
            RaiseTopologyChanged();
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

        // Onto the page being looked at, or it would be placed where it cannot be seen.
        if (Sheet is { Length: > 0 } sheet && circuit.Sheets.Contains(sheet)) component.Sheet = sheet;

        circuit.Add(component);

        SelectedComponent = component;
        PendingItem = null;
        ActiveTool = EditorTool.Select;
        RaiseTopologyChanged();
        StatusChanged?.Invoke(this, $"Placed {component.Name}");
    }

    private void HandleSelectClick(CorePoint world, Point screen, PointerPressedEventArgs e)
    {
        // A pin, before anything else. Clicking one is the other way to ask "what is this joined
        // to", and on a schematic drawn with net labels it is the only way that works — there is
        // no wire to click.
        if (TerminalAt(world) is { } pin && e.ClickCount == 1)
        {
            SetSelection([]);
            SelectedComponent = pin.Owner;

            HighlightNet(NetHighlighting.For(Circuit!, pin));
            return;
        }

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

            HighlightNet(NetHighlighting.For(Circuit!, hitWire));
            return;
        }

        // Empty canvas: start a band. A press that turns out not to be a drag clears the
        // selection on release, which is what a plain click on nothing has always done.
        SetSelection([]);
        HighlightNet(null);

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
        RaiseTopologyChanged();
    }

    private void HandleProbeClick(CorePoint world, bool shift)
    {
        var terminal = TerminalAt(world);
        if (terminal is null)
        {
            StatusChanged?.Invoke(this, shift
                ? "Shift-click a terminal to make it the selected probe's reference point."
                : "Click a terminal to attach a probe.");
            return;
        }

        // Shift sets the second point of a differential or power measurement rather than adding
        // another trace — the two-terminal kinds need somewhere to measure against, and picking
        // it on the canvas is the only place the choice makes any sense.
        if (shift) ProbeReferenceRequested?.Invoke(this, terminal);
        else ProbeRequested?.Invoke(this, terminal);
    }

    private void HandleDeleteClick(CorePoint world)
    {
        var circuit = Circuit;
        if (circuit is null) return;

        if (ComponentAt(world) is { } component)
        {
            circuit.Remove(component);
            if (ReferenceEquals(component, SelectedComponent)) SelectedComponent = null;
            RaiseTopologyChanged();
            StatusChanged?.Invoke(this, $"Deleted {component.Name}");
            return;
        }

        if (WireAt(world) is { } wire)
        {
            circuit.Remove(wire);
            RaiseTopologyChanged();
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
    /// <summary>
    /// Picks a net out on the drawing, or clears what was picked out. Says what is on it, because
    /// the answer is usually a list of parts rather than a shape.
    /// </summary>
    private void HighlightNet(NetHighlight? net)
    {
        if (ReferenceEquals(_highlightedNet, net)) return;

        _highlightedNet = net;

        if (net is not null) StatusChanged?.Invoke(this, net.Summary);

        InvalidateVisual();
    }

    /// <summary>Clears any highlighted net, for a caller that changed the circuit under it.</summary>
    public void ClearNetHighlight() => HighlightNet(null);

    /// <summary>
    /// Announces that the circuit's shape changed, and drops any highlighted net first.
    /// <para>
    /// A net is a fact about how things are joined, so the moment a wire is drawn or cut it may
    /// be a different net — and the parts of it still on screen would then be lit up as something
    /// they are no longer. Recomputing on every edit would be the other answer, but clearing is
    /// the honest one: the question was asked about a circuit that no longer exists.
    /// </para>
    /// </summary>
    private void RaiseTopologyChanged()
    {
        HighlightNet(null);

        TopologyChanged?.Invoke(this, EventArgs.Empty);
    }

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
        ComponentsWithin(circuit, band, null);

    /// <summary>The same, limited to one page of the drawing.</summary>
    public static IReadOnlyList<CircuitComponent> ComponentsWithin(
        Circuit circuit, Rect band, string? sheet) =>
        [.. circuit.OnSheet(sheet).Where(c => band.Contains(BoundsOf(c)))];

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

        RaiseTopologyChanged();
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
        RaiseTopologyChanged();
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

        RaiseTopologyChanged();
        InvalidateVisual();
    }

    // ---- keyboard --------------------------------------------------------

    /// <summary>
    /// Moves whatever is selected by one grid square, or by one unit with Shift held.
    /// <para>
    /// The canvas has always needed a mouse to move anything, which is a fair thing to notice
    /// before calling something finished. It is also simply faster: a part that is one square out
    /// is two keystrokes rather than a drag that has to be aimed.
    /// </para>
    /// </summary>
    /// <returns>False when there was nothing selected to move.</returns>
    public bool NudgeSelection(double dx, double dy, bool fine = false)
    {
        var circuit = Circuit;
        if (circuit is null) return false;

        var moving = Selection;

        if (moving.Count == 0)
        {
            if (SelectedComponent is not { } single) return false;
            moving = [single];
        }

        // One unit rather than one grid square with Shift, which is the finer control the modifier
        // usually means — and which is the only way to place something off the grid deliberately.
        var step = fine ? 1.0 : Math.Max(GridSize, 1.0);

        foreach (var component in moving)
        {
            component.X += dx * step;
            component.Y += dy * step;
        }

        RaiseTopologyChanged();
        InvalidateVisual();

        return true;
    }

    /// <summary>
    /// Selects the next part on the sheet, wrapping at the end, and scrolls it into view.
    /// <para>
    /// In the order the parts were added, which is the order the file lists them and the order the
    /// parts list shows them. Any order would do so long as it is the same one every time; this one
    /// has the advantage of already being what the rest of the application means by "the parts".
    /// </para>
    /// </summary>
    /// <returns>False when the sheet is empty.</returns>
    public bool SelectNext(int direction = 1)
    {
        var circuit = Circuit;
        if (circuit is null) return false;

        var parts = Visible.Where(c => c is not IAnnotation).ToList();

        if (parts.Count == 0) return false;

        var current = SelectedComponent is null ? -1 : parts.IndexOf(SelectedComponent);

        // From nothing, forwards starts at the first and backwards at the last.
        var next = current < 0
            ? (direction > 0 ? 0 : parts.Count - 1)
            : ((current + direction) % parts.Count + parts.Count) % parts.Count;

        SetSelection([parts[next]]);
        ScrollIntoView(parts[next]);
        InvalidateVisual();

        return true;
    }

    /// <summary>
    /// Pans so that a part is on screen, leaving the view alone when it already is.
    /// <para>
    /// Leaving it alone matters more than the scrolling does: stepping through six parts that are
    /// all visible should not move the drawing about underneath somebody.
    /// </para>
    /// </summary>
    public void ScrollIntoView(CircuitComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        var box = VisualBoundsOf(component);

        var topLeft = WorldToScreen(new CorePoint(box.X, box.Y));
        var bottomRight = WorldToScreen(new CorePoint(box.Right, box.Bottom));

        const double margin = 40;

        var dx = topLeft.X < margin ? margin - topLeft.X
            : bottomRight.X > Bounds.Width - margin ? Bounds.Width - margin - bottomRight.X
            : 0;

        var dy = topLeft.Y < margin ? margin - topLeft.Y
            : bottomRight.Y > Bounds.Height - margin ? Bounds.Height - margin - bottomRight.Y
            : 0;

        if (dx == 0 && dy == 0) return;

        _panOffset = new Point(_panOffset.X + (dx / Zoom), _panOffset.Y + (dy / Zoom));
        _viewAdjustedByUser = true;
    }

    /// <summary>
    /// Drops the armed part in the middle of the view, for placing one without a pointer.
    /// <para>
    /// The middle rather than beside the selection: it is where the eye already is, and it is the
    /// one place that is certainly on screen.
    /// </para>
    /// </summary>
    /// <returns>False when nothing was armed.</returns>
    public bool PlacePendingAtCentre()
    {
        if (PendingItem is not { } pending) return false;

        PlaceComponent(pending, Snap(ScreenToWorld(new Point(Bounds.Width / 2, Bounds.Height / 2))));

        return true;
    }

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

            case Key.Left or Key.Right or Key.Up or Key.Down:
                var fine = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

                e.Handled = NudgeSelection(
                    e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0,
                    e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0,
                    fine);

                break;

            case Key.Tab:
                // Handled either way, so focus does not leave the canvas for the palette when
                // there is nothing on the sheet to step to.
                SelectNext(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1);
                e.Handled = true;
                break;

            case Key.Enter:
                e.Handled = PlacePendingAtCentre();
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
