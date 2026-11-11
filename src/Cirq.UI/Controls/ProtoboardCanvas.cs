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

    public static readonly StyledProperty<bool> MeterShownProperty =
        AvaloniaProperty.Register<ProtoboardCanvas, bool>(nameof(MeterShown));

    public static readonly StyledProperty<int> BuildStepProperty =
        AvaloniaProperty.Register<ProtoboardCanvas, int>(nameof(BuildStep), -1);

    /// <summary>Whether the multimeter is out on the bench.</summary>
    public bool MeterShown
    {
        get => GetValue(MeterShownProperty);
        set => SetValue(MeterShownProperty, value);
    }

    /// <summary>The build guide's step on screen, from 0, or -1 with the guide closed: only what the steps so far fit is shown.</summary>
    public int BuildStep
    {
        get => GetValue(BuildStepProperty);
        set => SetValue(BuildStepProperty, value);
    }

    /// <summary>What a terminal's net is at in the running simulation, or null when nothing is solved: the meter's reading.</summary>
    public Func<Terminal, double?>? VoltageAt { get; set; }

    /// <summary>The current into a part through a pin, from the running simulation: the meter's current range.</summary>
    public Func<Terminal, double?>? CurrentInto { get; set; }

    /// <summary>Whether the circuit is running: resistance is measured with it off, as on a real bench.</summary>
    public Func<bool>? IsRunning { get; set; }

    /// <summary>The meter's range: 0 volts, 1 ohms, 2 continuity, 3 amps. Clicking the meter turns the dial.</summary>
    public int MeterRange { get; private set; }

    /// <summary>Raised when the board is laid out afresh, so the build guide can follow.</summary>
    public event EventHandler? LayoutChanged;

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
    private enum Grab { None, Pan, Part, Lead, WireEnd, Bench, Probe }

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
        AffectsRender<ProtoboardCanvas>(CircuitProperty, SelectedComponentProperty, MeterShownProperty, BuildStepProperty);
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
                var kind = _layout?.Kind;
                _layout = ProtoboardLayout.Arrange(circuit);
                _signature = signature;
                _steps = null;
                if (kind is not null && kind != _layout.Kind) (_redTip, _blackTip) = (null, null);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => LayoutChanged?.Invoke(this, EventArgs.Empty));
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

    private IReadOnlyList<BuildStep>? _steps;

    /// <summary>The board's build guide, step by step.</summary>
    public IReadOnlyList<BuildStep> BuildSteps => _steps ??= Layout is { } layout ? BuildGuide.Steps(layout) : [];

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
        if (MeterShown && Meter(layout) is { } meter)
        {
            left = Math.Min(left, meter.X - 1);
            bottom = Math.Max(bottom, meter.Y + MeterView.Height + 1);
        }
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
        var (x, y) = Layout?.Position(hole) ?? Breadboard.Position(hole);
        return new Point((x * ProtoboardRenderer.Unit * _zoom) + _pan.X, (y * ProtoboardRenderer.Unit * _zoom) + _pan.Y);
    }

    /// <summary>Where a point in pitches is on the control.</summary>
    internal Point ScreenOfPitches(double x, double y) =>
        new((x * ProtoboardRenderer.Unit * _zoom) + _pan.X, (y * ProtoboardRenderer.Unit * _zoom) + _pan.Y);

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
            var steps = BuildStep >= 0 && _candidate is null ? BuildSteps : [];
            var current = BuildStep >= 0 && BuildStep < steps.Count ? steps[BuildStep] : null;
            ProtoboardRenderer.Draw(canvas, shown, new ProtoboardRenderOptions(
                SelectedComponent, _hoverStrip, _grabbedWire, _lit, current is null ? null : Visible(shown, steps, BuildStep), current,
                MeterShown ? Meter(shown) : null));

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
        var hole = layout.Nearest(x, y, 0.45);

        // A probe tip of the meter, before anything on the board under it.
        if (point.Properties.IsLeftButtonPressed && MeterShown && Meter(layout) is { } meter)
        {
            foreach (var (tip, red) in new[] { (meter.Red, true), (meter.Black, false) })
                if (Distance(tip, (x, y)) < 0.9)
                {
                    _grabbedRed = red;
                    Begin(Grab.Probe, e, $"The {(red ? "red" : "black")} probe");
                    return;
                }

            // The meter itself: the dial turns to the next range.
            if (meter.Contains(x, y))
            {
                MeterRange = (MeterRange + 1) % MeterView.Units.Length;
                _ohms = null;
                StatusChanged?.Invoke(this, $"Meter on {MeterView.Units[MeterRange]}. {MeterSays(layout)}");
                InvalidateVisual();
                return;
            }
        }

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

            // A soldered board's links and back wires follow the parts, so only a breadboard's jumpers are picked up.
            if (layout.Kind == BoardKind.Breadboard && layout.Wires.FirstOrDefault(w => w.From == at || w.To == at) is { } wire)
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
                _grabHole = placed.Holes.MinBy(h => Distance(layout.Position(h), (x, y)));
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
            case Grab.Probe:
                _probeAt = (x, y);
                var near = layout.Nearest(x, y, 0.6);
                StatusChanged?.Invoke(this, near is { } h ? $"On {layout.HoleName(h)}" : "Drop it on a hole");
                InvalidateVisual();
                return;
        }

        // Try the move where the pointer is now, and show the board as it would be.
        BoardEdit? edit = null;
        // On a soldered board a part may be dragged past the edge: the board grows to hold it.
        var to = layout.Kind == BoardKind.Breadboard
            ? Breadboard.Nearest(x, y, Math.Max(layout.Boards, _grabHole.Board) + (_grab == Grab.Part ? 1 : 0))
            : new BoardHole(1, (int)Math.Round(y - 1.5), (int)Math.Round(x - 2.5)) is { Row: >= 1, Column: >= 1 } onBoard ? onBoard : null;
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
            else if (to is { } hole) StatusChanged?.Invoke(this, $"To {layout.HoleName(hole)}");
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

        if (grab == Grab.Probe && Layout is { } board)
        {
            var (x, y) = ToPitches(e.GetPosition(this));
            var hole = board.Nearest(x, y, 0.6);
            if (_grabbedRed) _redTip = hole;
            else _blackTip = hole;
            _probeAt = null;
            StatusChanged?.Invoke(this, MeterSays(board));
            InvalidateVisual();
            return;
        }

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
        var hole = layout.Nearest(x, y, 0.45);
        if (layout.Kind != BoardKind.Breadboard)
        {
            HoverSoldered(layout, hole);
            if (hole is null && PartAt(layout, x, y, null) is { } over)
                StatusChanged?.Invoke(this, $"{over.Name} {over.ValueLabel}{(over is IInteractiveComponent ? ": double-click to operate it" : "")}");
            return;
        }

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

    /// <summary>
    /// The pointer over a stripboard's or perfboard's hole: the copper it is on lit (the run of strip between cuts, or every
    /// pad on its net), and what is there said in the status bar.
    /// </summary>
    private void HoverSoldered(ProtoboardLayout layout, BoardHole? hole)
    {
        HashSet<BoardHole>? lit = null;
        if (hole is { } at)
        {
            var net = layout.NetAt(at);
            var segment = layout.SegmentAt(at);
            if (segment is not null)
                lit = [.. Enumerable.Range(segment.From, segment.To - segment.From + 1).Select(r => new BoardHole(1, r, segment.Column))];
            else if (net is not null)
                lit = [.. layout.HoleNets.Where(h => ReferenceEquals(h.Value, net)).Select(h => h.Key)];

            var cut = layout.Cuts.Contains(at) ? ", where the strip is cut" : "";
            var where = segment is null ? layout.HoleName(at) : $"{layout.HoleName(at)}: strip {SolderedLayout.Letters(segment.Column)}, holes {segment.From}–{segment.To}{cut}";
            if (net is null) StatusChanged?.Invoke(this, $"{where}, not joined to anything");
            else
            {
                var pins = net.Terminals.Where(t => t.Owner is { } o && BoardPackages.IsBuilt(o)).Take(6).Select(t => t.ToString());
                StatusChanged?.Invoke(this, $"{where}, on {layout.Caption(net)}: {string.Join(", ", pins)}");
            }
        }

        if ((lit is null) != (_lit is null) || (lit is not null && _lit is not null && !lit.SetEquals(_lit)))
        {
            _lit = lit;
            InvalidateVisual();
        }
    }

    private HashSet<BoardHole>? _lit;

    // ───────────────────────────── the build guide ─────────────────────────────

    /// <summary>What the steps up to and including <paramref name="index"/> have fitted.</summary>
    private static HashSet<object> Visible(ProtoboardLayout layout, IReadOnlyList<BuildStep> steps, int index)
    {
        var visible = new HashSet<object>();
        foreach (var trace in layout.Wires.Where(w => w.IsTrace)) visible.Add(trace);
        foreach (var step in steps.Take(index + 1))
        {
            if (step.Part is { } part) visible.Add(part);
            if (step.Bench is { } bench) visible.Add(bench);
            if (step.Kind == BuildStepKind.Cut) foreach (var hole in step.Holes ?? []) visible.Add(hole);
            if (step.Wire is { } wire)
            {
                // A perfboard step wires a whole net at once; a PCB's traces are there from the start, with the board.
                if (layout.Kind == BoardKind.Perfboard) foreach (var w in layout.Wires.Where(w => ReferenceEquals(w.Net, wire.Net))) visible.Add(w);
                else visible.Add(wire);
            }
        }

        return visible;
    }

    // ───────────────────────────── the multimeter ─────────────────────────────

    private BoardHole? _redTip, _blackTip;
    private bool _grabbedRed;
    private (double X, double Y)? _probeAt;

    /// <summary>
    /// The meter as it stands: on the bench below the boards, its black probe on ground to start with (where anyone puts
    /// it first), the red one waiting beside it, and the reading between them.
    /// </summary>
    private MeterView? Meter(ProtoboardLayout layout)
    {
        var (_, _, _, bottom) = layout.Extent;
        var (x, y) = (2.0, bottom + 2.0);
        _blackTip ??= GroundHole(layout);
        (double, double) Tip(BoardHole? hole, bool red) =>
            _grab == Grab.Probe && _grabbedRed == red && _probeAt is { } at ? at
            : hole is { } h ? layout.Position(h)
            : (x + (red ? 8.0 : 3.0), y - 2.0);
        var (reading, note) = Reading(layout);
        return new MeterView(x, y, Tip(_redTip, true), Tip(_blackTip, false), reading, note, MeterRange, MeterRange == 2 && reading == "0.00" || _beep);
    }

    private static BoardHole? GroundHole(ProtoboardLayout layout)
    {
        if (layout.Kind == BoardKind.Breadboard)
        {
            if (!layout.Rails.ContainsKey(Breadboard.BottomMinus)) return null;
            var used = layout.Wires.SelectMany(w => new[] { w.From, w.To }).Concat(layout.Bench.SelectMany(b => b.Leads.Select(l => l.To))).ToHashSet();
            return Breadboard.RailColumns.Select(c => new BoardHole(layout.Boards, Breadboard.BottomMinus, c)).FirstOrDefault(h => !used.Contains(h), new BoardHole(layout.Boards, Breadboard.BottomMinus, 3));
        }

        return layout.HoleNets.Where(h => h.Value.IsGround).Select(h => h.Key).OrderByDescending(h => h.Row).ThenBy(h => h.Column).Cast<BoardHole?>().FirstOrDefault();
    }

    private (BoardHole Red, BoardHole Black, int Range, ProtoboardLayout Layout, double? Ohms)? _ohms;
    private bool _beep;

    /// <summary>What the meter reads on its range: volts or amps from the running simulation, ohms with it off.</summary>
    private (string Reading, string Note) Reading(ProtoboardLayout layout)
    {
        _beep = false;
        if (MeterRange == 3) return Amps(layout);
        if (_redTip is not { } red || _blackTip is not { } black) return ("- - - -", "Drag the probes onto holes");
        if (MeterRange is 1 or 2) return Ohms(layout, red, black);
        var (vr, vb) = (Volts(layout, red), Volts(layout, black));
        if (vr is null || vb is null)
            return ("- - - -", layout.NetAt(red) is null || layout.NetAt(black) is null ? "A probe is on nothing" : "Run the circuit (F5)");
        var volts = vr.Value - vb.Value;
        var shown = Math.Abs(volts) >= 100 ? volts.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
            : Math.Abs(volts) >= 10 ? volts.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)
            : volts.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        return (shown, $"{layout.HoleName(red)} to {layout.HoleName(black)}");
    }

    /// <summary>Resistance between the probes, on the circuit with its power off: computed once for where they are.</summary>
    private (string Reading, string Note) Ohms(ProtoboardLayout layout, BoardHole red, BoardHole black)
    {
        if (IsRunning?.Invoke() == true) return ("- - - -", "Stop the run (F5) to measure Ω");
        if (layout.NetAt(red) is not { } a || layout.NetAt(black) is not { } b) return ("OL", "A probe is on nothing");
        if (_ohms is not { } cached || cached.Red != red || cached.Black != black || !ReferenceEquals(cached.Layout, layout))
        {
            var ta = Contact(a);
            var tb = Contact(b);
            double? ohms = ta is null || tb is null || Circuit is null ? null : Ohmmeter.Between(Circuit, ta, tb);
            _ohms = cached = (red, black, MeterRange, layout, ohms);
        }

        if (cached.Ohms is not { } value) return ("- - - -", "Can't measure here");
        _beep = MeterRange == 2 && value < Ohmmeter.Continuity;
        return MeterRange == 2
            ? (_beep ? "0.00" : value < Ohmmeter.Open ? Ohmmeter.Display(value) : "OL", _beep ? $"{layout.HoleName(red)} and {layout.HoleName(black)} are joined" : $"{layout.HoleName(red)} to {layout.HoleName(black)}: no continuity")
            : (Ohmmeter.Display(value), $"{layout.HoleName(red)} to {layout.HoleName(black)}, power off");
    }

    /// <summary>
    /// Current through the lead the red probe is on: what the meter reads wired in series with that lead. It wants a
    /// two-lead part's lead, where "the current" means one thing.
    /// </summary>
    private (string Reading, string Note) Amps(ProtoboardLayout layout)
    {
        if (_redTip is not { } red) return ("- - - -", "Put the red probe on a part's lead");
        if (layout.PartAt(red) is not { Package.Form: BoardForm.TwoLead } part || part.HoleOf(part.Package.Pins[0]!) is not { } first)
            return ("- - - -", "On A, the red probe goes on a resistor's, LED's or capacitor's lead");
        var pin = part.Holes[0] == red ? part.Package.Pins[0]! : part.Package.Pins[1]!;
        if (CurrentInto?.Invoke(pin) is not { } amps) return ("- - - -", "Run the circuit (F5)");
        var milli = amps * 1000;
        var shown = Math.Abs(milli) >= 100 ? milli.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : milli.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture);
        return (shown, $"Through {part.Part.Name}, into its {pin.Name} lead");
    }

    /// <summary>A pin on a net that is making contact (not a hidden open fault), to measure the net at.</summary>
    private Terminal? Contact(Net net)
    {
        var open = Circuit?.Faults.Where(f => f.Kind == FaultKind.Open).Select(f => f.First).ToHashSet() ?? [];
        return net.Terminals.FirstOrDefault(t => t.Owner is { } o && BoardPackages.IsBuilt(o) && !open.Contains(t)) ?? net.Terminals.FirstOrDefault();
    }

    /// <summary>
    /// The voltage at a hole: of the copper or strip it is on, measured at a pin there that is making contact (a pin
    /// with a hidden open fault isn't, so the meter reads the strip, as a real one would).
    /// </summary>
    private double? Volts(ProtoboardLayout layout, BoardHole hole)
    {
        if (layout.NetAt(hole) is not { } net || VoltageAt is not { } at) return null;
        var open = Circuit?.Faults.Where(f => f.Kind == FaultKind.Open).Select(f => f.First).ToHashSet() ?? [];
        var pin = net.Terminals.FirstOrDefault(t => t.Owner is { } o && BoardPackages.IsBuilt(o) && !open.Contains(t)) ?? net.Terminals.FirstOrDefault();
        return pin is null ? null : at(pin);
    }

    /// <summary>What the meter reads, for the status bar.</summary>
    public string MeterSays(ProtoboardLayout layout)
    {
        var (reading, note) = Reading(layout);
        var unit = MeterRange switch { 1 => " Ω", 2 => _beep ? " (beep)" : "", 3 => " mA", _ => " V" };
        return reading.StartsWith('-') && reading.Contains(' ', StringComparison.Ordinal) ? $"Meter: {note}" : $"Meter: {reading}{unit}, {note}";
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
