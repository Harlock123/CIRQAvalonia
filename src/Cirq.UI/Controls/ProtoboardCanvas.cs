using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Cirq.Components.Protoboard;
using Cirq.Core.Topology;
using Cirq.UI.Rendering;

namespace Cirq.UI.Controls;

/// <summary>
/// The circuit built on breadboards: the protoboard view, beside the schematic and switched with it (F11).
/// <para>
/// It draws <see cref="ProtoboardLayout"/>, the same netlist the simulator solves laid out on boards, and redraws
/// as the simulation runs, so LEDs light where they sit. Parts are moved by dragging them; a resistor's or an LED's
/// lead can be dragged on its own, and a jumper's end along its strip. Each move freezes the rest of the board where
/// it is and is one undo step. A move that would put a pin in a strip carrying another net is refused, and the status
/// bar says why. Hovering over a hole lights its strip and names what is on it.
/// </para>
/// </summary>
public sealed class ProtoboardCanvas : Control
{
    public static readonly StyledProperty<Circuit?> CircuitProperty =
        AvaloniaProperty.Register<ProtoboardCanvas, Circuit?>(nameof(Circuit));

    public static readonly StyledProperty<CircuitComponent?> SelectedComponentProperty =
        AvaloniaProperty.Register<ProtoboardCanvas, CircuitComponent?>(nameof(SelectedComponent), defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    public Circuit? Circuit
    {
        get => GetValue(CircuitProperty);
        set => SetValue(CircuitProperty, value);
    }

    public CircuitComponent? SelectedComponent
    {
        get => GetValue(SelectedComponentProperty);
        set => SetValue(SelectedComponentProperty, value);
    }

    /// <summary>Says what's under the pointer, or what a move did.</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>A move by hand is starting: one undo step, named.</summary>
    public event EventHandler<string>? InteractiveEditBegan;

    public event EventHandler? InteractiveEditEnded;

    /// <summary>The board was changed by hand, so the document has changed.</summary>
    public event EventHandler? BoardEdited;

    private const double MinZoom = 0.15;
    private const double MaxZoom = 6.0;

    private double _zoom = 1.0;
    private Point _pan = new(40, 40);
    private bool _fitPending = true;
    private bool _viewAdjustedByUser;

    private ProtoboardLayout? _layout;
    private long _signature;

    // A move in progress: what was grabbed, where, and the board as it would be if dropped now.
    private enum Grab { None, Pan, Part, Lead, WireEnd, Bench }

    private Grab _grab;
    private Point _lastPointer;
    private CircuitComponent? _grabbedPart;
    private Terminal? _grabbedLead;
    private BoardWire? _grabbedWire;
    private bool _grabbedFromEnd;
    private BoardHole _grabHole;
    private (double X, double Y) _grabOffset;
    private BoardEdit? _candidate;
    private string? _refusal;

    private BoardStrip? _hoverStrip;
    private bool _spaceHeld;

    static ProtoboardCanvas()
    {
        AffectsRender<ProtoboardCanvas>(CircuitProperty, SelectedComponentProperty);
    }

    public ProtoboardCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    /// <summary>The board as it is drawn now, laid out again whenever the circuit's parts, wires or hand-made places have changed.</summary>
    public ProtoboardLayout? Layout
    {
        get
        {
            if (Circuit is not { } circuit) return null;
            var signature = Signature(circuit);
            if (_layout is null || signature != _signature)
            {
                _layout = ProtoboardLayout.Arrange(circuit);
                _signature = signature;
            }

            return _layout;
        }
    }

    /// <summary>A cheap fingerprint of everything the layout depends on: which parts, their pins, the wires, the hand-made places.</summary>
    private static long Signature(Circuit circuit)
    {
        var hash = new HashCode();
        hash.Add(circuit.Board.Version);
        hash.Add(circuit.Components.Count);
        foreach (var c in circuit.Components)
        {
            hash.Add(c.Id);
            hash.Add(c.Terminals.Count);
            hash.Add(c.Name);
            hash.Add(c.ValueLabel.Length > 0 ? c.ValueLabel[0] : ' ');
        }

        foreach (var w in circuit.Wires)
        {
            hash.Add(w.SourceTerminal?.Uid);
            hash.Add(w.TargetTerminal?.Uid);
        }

        return ((long)circuit.Wires.Count << 32) ^ (uint)hash.ToHashCode();
    }

    /// <summary>Makes the next drawing lay the board out afresh.</summary>
    public void Refresh()
    {
        _layout = null;
        InvalidateVisual();
    }

    // ───────────────────────────── view ─────────────────────────────

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        if ((_fitPending || !_viewAdjustedByUser) && size.Width > 4 && size.Height > 4)
        {
            _fitPending = false;
            Fit(size);
        }

        return size;
    }

    /// <summary>Frames the whole board and bench, now or once laid out.</summary>
    public void RequestFit()
    {
        _viewAdjustedByUser = false;
        if (Bounds.Width <= 4 || Bounds.Height <= 4)
        {
            _fitPending = true;
            return;
        }

        Fit(Bounds.Size);
    }

    private void Fit(Size viewport)
    {
        if (Layout is not { } layout) return;
        var (left, top, right, bottom) = layout.Extent;
        var unit = ProtoboardRenderer.Unit;
        var width = (right - left) * unit;
        var height = (bottom - top) * unit;
        _zoom = Math.Clamp(Math.Min(viewport.Width / width, viewport.Height / height) * 0.96, MinZoom, MaxZoom);
        _pan = new Point(
            (viewport.Width / 2) - (((left + right) / 2) * unit * _zoom),
            (viewport.Height / 2) - (((top + bottom) / 2) * unit * _zoom));
        InvalidateVisual();
    }

    public void ZoomBy(double factor) => ZoomAbout(new Point(Bounds.Width / 2, Bounds.Height / 2), factor);

    private void ZoomAbout(Point screen, double factor)
    {
        var before = ToWorld(screen);
        _zoom = Math.Clamp(_zoom * factor, MinZoom, MaxZoom);
        _pan = new Point(screen.X - (before.X * _zoom), screen.Y - (before.Y * _zoom));
        _viewAdjustedByUser = true;
        InvalidateVisual();
    }

    /// <summary>Where a hole is on the control, for a test to put the pointer on it.</summary>
    internal Point ScreenOf(BoardHole hole)
    {
        var (x, y) = Breadboard.Position(hole);
        return new Point((x * ProtoboardRenderer.Unit * _zoom) + _pan.X, (y * ProtoboardRenderer.Unit * _zoom) + _pan.Y);
    }

    private Point ToWorld(Point screen) => new((screen.X - _pan.X) / _zoom, (screen.Y - _pan.Y) / _zoom);

    /// <summary>A screen point in pitches, the layout's units.</summary>
    private (double X, double Y) ToPitches(Point screen)
    {
        var world = ToWorld(screen);
        return (world.X / ProtoboardRenderer.Unit, world.Y / ProtoboardRenderer.Unit);
    }

    // ───────────────────────────── drawing ─────────────────────────────

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse(Application.Current?.ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? "#23272C" : "#DEE3E7")), new Rect(Bounds.Size));
        var shown = _candidate?.Layout ?? Layout;
        if (shown is null) return;

        var canvas = new AvaloniaSymbolCanvas(context);
        using (canvas.PushTransform(Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_pan.X, _pan.Y)))
        {
            ProtoboardRenderer.Draw(canvas, shown, new ProtoboardRenderOptions(SelectedComponent, _hoverStrip, _grabbedWire));

            // A move that can't be made: the part ringed in red where it was, until it's let go.
            if (_refusal is not null && _grabbedPart is { } part && ProtoboardRenderer.Bounds(shown, part) is { } box)
                canvas.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#D7261E")), 2.2), new RoundedRect(box.Inflate(5), 6));
        }

        if (shown.Parts.Count == 0 && shown.Bench.Count == 0)
        {
            var text = new FormattedText("Nothing on the schematic to build yet: place some parts and they appear here on the board.",
                System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, CanvasTheme.LabelTypeface, 14, Brushes.Gray);
            context.DrawText(text, new Point(24, 24));
        }
    }

    // ───────────────────────────── the pointer ─────────────────────────────

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAbout(e.GetPosition(this), e.Delta.Y > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        _lastPointer = point.Position;
        if (Layout is not { } layout || Circuit is not { } circuit) return;

        if (point.Properties.IsMiddleButtonPressed || (point.Properties.IsLeftButtonPressed && _spaceHeld))
        {
            Begin(Grab.Pan, e);
            return;
        }

        var (x, y) = ToPitches(point.Position);
        var hole = Breadboard.Nearest(x, y, layout.Boards, 0.45);

        if (point.Properties.IsRightButtonPressed)
        {
            ShowMenu(layout, PartAt(layout, x, y, hole));
            return;
        }

        if (!point.Properties.IsLeftButtonPressed) return;

        var part = PartAt(layout, x, y, hole);

        // Double-click a switch or a button and it works, as on the schematic.
        if (e.ClickCount >= 2 && part is IInteractiveComponent interactive)
        {
            SelectedComponent = part;
            interactive.Interact();
            StatusChanged?.Invoke(this, $"{part.Name}: {part.ValueLabel}");
            InvalidateVisual();
            return;
        }

        // A lead's end or a jumper's end, before the part they belong to.
        if (hole is { } at)
        {
            foreach (var placed in layout.Parts.Where(p => p.Package.Form == BoardForm.TwoLead))
            {
                var index = placed.Holes.ToList().IndexOf(at);
                if (index < 0) continue;
                SelectedComponent = placed.Part;
                (_grabbedPart, _grabbedLead, _grabHole) = (placed.Part, placed.Package.Pins[index], at);
                Begin(Grab.Lead, e, $"Move {placed.Part.Name}'s lead");
                return;
            }

            if (layout.Wires.FirstOrDefault(w => w.From == at || w.To == at) is { } wire)
            {
                (_grabbedWire, _grabbedFromEnd, _grabHole) = (wire, wire.From == at, at);
                Begin(Grab.WireEnd, e, "Move a wire's end");
                return;
            }
        }

        if (part is not null)
        {
            SelectedComponent = part;
            _grabbedPart = part;
            if (layout.FindOnBench(part) is { } bench)
            {
                _grabOffset = (x - bench.X, y - bench.Y);
                Begin(Grab.Bench, e, $"Move {part.Name}");
                return;
            }

            if (layout.Find(part) is { } placed)
            {
                _grabHole = placed.Holes.MinBy(h => Distance(Breadboard.Position(h), (x, y)));
                Begin(Grab.Part, e, $"Move {part.Name} on the board");
                return;
            }
        }

        SelectedComponent = null;
        Begin(Grab.Pan, e);
    }

    private void Begin(Grab grab, PointerPressedEventArgs e, string? label = null)
    {
        _grab = grab;
        _candidate = null;
        _refusal = null;
        e.Pointer.Capture(this);
        if (label is not null) StatusChanged?.Invoke(this, label + ": drag it where you want it");
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var position = e.GetPosition(this);
        var layout = Layout;
        if (layout is null || Circuit is not { } circuit) return;
        var (x, y) = ToPitches(position);

        switch (_grab)
        {
            case Grab.Pan:
                _pan += position - _lastPointer;
                _lastPointer = position;
                _viewAdjustedByUser = true;
                InvalidateVisual();
                return;
            case Grab.None:
                Hover(layout, x, y);
                return;
        }

        // Try the move where the pointer is now, and show the board as it would be.
        BoardEdit? edit = null;
        var to = Breadboard.Nearest(x, y, Math.Max(layout.Boards, _grabHole.Board) + (_grab == Grab.Part ? 1 : 0));
        switch (_grab)
        {
            case Grab.Part when to is { } hole && hole != _grabHole && _grabbedPart is { } part:
                edit = ProtoboardEdits.MovePart(circuit, layout, part, _grabHole, hole);
                break;
            case Grab.Lead when to is { } hole && hole != _grabHole && _grabbedPart is { } part && _grabbedLead is { } lead:
                edit = ProtoboardEdits.MoveLead(circuit, layout, part, lead, hole);
                break;
            case Grab.WireEnd when to is { } hole && hole != _grabHole && _grabbedWire is { } wire:
                edit = ProtoboardEdits.MoveWireEnd(circuit, layout, wire, _grabbedFromEnd, hole);
                break;
            case Grab.Bench when _grabbedPart is { } part:
                edit = ProtoboardEdits.MoveBench(circuit, layout, part, x - _grabOffset.X, y - _grabOffset.Y);
                break;
        }

        if (edit is null) return;
        if (edit.Succeeded)
        {
            _candidate = edit;
            _refusal = null;
            if (edit.Layout is { Matches: false } bad) StatusChanged?.Invoke(this, string.Join(" ", bad.Problems));
            else if (to is { } hole) StatusChanged?.Invoke(this, $"To {Breadboard.Name(hole)}");
        }
        else
        {
            _candidate = null;
            _refusal = edit.Problem;
            StatusChanged?.Invoke(this, edit.Problem!);
        }

        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var grab = _grab;
        _grab = Grab.None;
        e.Pointer.Capture(null);

        if (grab is not (Grab.None or Grab.Pan) && _candidate is { Arrangement: { } arrangement } && Circuit is { } circuit)
        {
            InteractiveEditBegan?.Invoke(this, grab switch
            {
                Grab.Lead => $"Move {_grabbedPart?.Name}'s lead",
                Grab.WireEnd => "Move a wire on the board",
                _ => $"Move {_grabbedPart?.Name} on the board",
            });
            circuit.Board.CopyFrom(arrangement);
            InteractiveEditEnded?.Invoke(this, EventArgs.Empty);
            BoardEdited?.Invoke(this, EventArgs.Empty);
            if (Layout is { } now) StatusChanged?.Invoke(this, Summary(now));
        }
        else if (_refusal is not null)
        {
            StatusChanged?.Invoke(this, $"Not moved: {_refusal}");
        }

        (_grabbedPart, _grabbedLead, _grabbedWire, _candidate, _refusal) = (null, null, null, null, null);
        InvalidateVisual();
    }

    private void Hover(ProtoboardLayout layout, double x, double y)
    {
        var hole = Breadboard.Nearest(x, y, layout.Boards, 0.45);
        var strip = hole is { } h ? Breadboard.StripOf(h) : (BoardStrip?)null;
        if (strip != _hoverStrip)
        {
            _hoverStrip = strip;
            InvalidateVisual();
        }

        if (hole is { } at)
        {
            var net = layout.NetAt(at);
            var where = $"{Breadboard.Name(at)}: {Breadboard.StripOf(at).Describe(layout.Boards)}";
            if (net is null)
            {
                StatusChanged?.Invoke(this, $"{where}, empty");
                return;
            }

            var pins = net.Terminals.Where(t => t.Owner is { } o && BoardPackages.IsBuilt(o)).Take(6).Select(t => t.ToString());
            StatusChanged?.Invoke(this, $"{where}, on {layout.Caption(net)}: {string.Join(", ", pins)}");
            return;
        }

        if (PartAt(layout, x, y, null) is { } part)
            StatusChanged?.Invoke(this, $"{part.Name} {part.ValueLabel}: drag to move it{(part is IInteractiveComponent ? ", double-click to operate it" : "")}; right-click for more");
    }

    /// <summary>The part under a point: on the bench, a chip's body, a part's pins, a two-lead part's body.</summary>
    private static CircuitComponent? PartAt(ProtoboardLayout layout, double x, double y, BoardHole? hole)
    {
        if (layout.Bench.LastOrDefault(b => b.Contains(x, y)) is { } bench) return bench.Part;
        if (hole is { } at && layout.PartAt(at) is { } pinned) return pinned.Part;
        var world = new Point(x * ProtoboardRenderer.Unit, y * ProtoboardRenderer.Unit);
        foreach (var part in layout.Parts.Reverse().OrderBy(p => p.Package.Form == BoardForm.Dip ? 1 : 0))
            if (ProtoboardRenderer.Bounds(layout, part.Part) is { } box && box.Contains(world)) return part.Part;
        return null;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt(((a.X - b.X) * (a.X - b.X)) + ((a.Y - b.Y) * (a.Y - b.Y)));

    /// <summary>How the board stands, for the status bar.</summary>
    public static string Summary(ProtoboardLayout layout)
    {
        var text = $"{layout.Boards} board{(layout.Boards == 1 ? "" : "s")}, {layout.Parts.Count} part{(layout.Parts.Count == 1 ? "" : "s")}, {layout.Wires.Count} jumper{(layout.Wires.Count == 1 ? "" : "s")}";
        return layout.Matches ? $"{text}: the board matches the schematic" : $"{text}. {string.Join(" ", layout.Problems)}";
    }

    // ───────────────────────────── the menu ─────────────────────────────

    private void ShowMenu(ProtoboardLayout layout, CircuitComponent? part)
    {
        var items = new List<MenuItem>();
        if (part is not null && Circuit is { } circuit && circuit.Board.Parts.ContainsKey(part.Id))
        {
            var release = new MenuItem { Header = $"Let the Layout Place {part.Name}" };
            release.Click += (_, _) => Edit($"Let the layout place {part.Name}", ProtoboardEdits.Release(circuit, layout, part));
            items.Add(release);
        }

        var arrange = new MenuItem { Header = "Arrange the Board Automatically", IsEnabled = Circuit is { Board.IsEmpty: false } };
        arrange.Click += (_, _) => ArrangeAutomatically();
        items.Add(arrange);

        var fit = new MenuItem { Header = "Zoom to Fit" };
        fit.Click += (_, _) => RequestFit();
        items.Add(fit);

        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(this);
    }

    private void Edit(string label, BoardEdit edit)
    {
        if (Circuit is not { } circuit || edit.Arrangement is not { } arrangement) return;
        InteractiveEditBegan?.Invoke(this, label);
        circuit.Board.CopyFrom(arrangement);
        InteractiveEditEnded?.Invoke(this, EventArgs.Empty);
        BoardEdited?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    /// <summary>Forgets every place made by hand, so the whole board is laid out again.</summary>
    public void ArrangeAutomatically()
    {
        if (Circuit is not { } circuit || circuit.Board.IsEmpty) return;
        InteractiveEditBegan?.Invoke(this, "Arrange the board");
        circuit.Board.Clear();
        InteractiveEditEnded?.Invoke(this, EventArgs.Empty);
        BoardEdited?.Invoke(this, EventArgs.Empty);
        if (Layout is { } layout) StatusChanged?.Invoke(this, "Laid out afresh. " + Summary(layout));
        RequestFit();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Space) _spaceHeld = true;
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Space) _spaceHeld = false;
        base.OnKeyUp(e);
    }
}
