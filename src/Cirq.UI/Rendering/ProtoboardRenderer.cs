using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Protoboard;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using BuildStep = Cirq.Components.Protoboard.BuildStep;
using BuildStepKind = Cirq.Components.Protoboard.BuildStepKind;
using CoreColor = Cirq.Core.Primitives.Color;

namespace Cirq.UI.Rendering;

/// <summary>What the protoboard drawing picks out, beyond the board itself.</summary>
/// <param name="Selected">The part to ring, as the schematic rings the selected one.</param>
/// <param name="Strip">A strip to light up: the one under the pointer, so you can see what a hole is joined to.</param>
/// <param name="Wire">A jumper to pick out.</param>
/// <param name="Lit">Holes to light up, on a stripboard or perfboard: those joined to the one under the pointer.</param>
/// <param name="Visible">With the build guide open, only what the steps so far have fitted (parts, wires, bench equipment, cuts).</param>
/// <param name="Current">The build guide's step on screen, picked out.</param>
/// <param name="Meter">The multimeter on the bench, when it is out.</param>
public sealed record ProtoboardRenderOptions(
    CircuitComponent? Selected = null, BoardStrip? Strip = null, BoardWire? Wire = null, IReadOnlySet<BoardHole>? Lit = null,
    IReadOnlySet<object>? Visible = null, BuildStep? Current = null, MeterView? Meter = null, IReadOnlyList<(double X, double Y)>? Marks = null,
    IReadOnlyList<InstrumentView>? Instruments = null);

/// <summary>A probe lead of a bench instrument: its name, its colour, where it leaves the instrument and where its tip is, in pitches.</summary>
public sealed record InstrumentLead(string Name, string Colour, (double X, double Y) Jack, (double X, double Y) Tip, bool Attached);

/// <summary>What a channel of a bench instrument shows: its samples (time, value) across the display.</summary>
/// <param name="Start">The time at the display's left edge.</param>
/// <param name="Span">How much time the display shows.</param>
/// <param name="Min">The value at the bottom of the channel's lane (analog), or of the display.</param>
/// <param name="Max">The value at the top.</param>
public sealed record InstrumentTrace(string Colour, IReadOnlyList<(double T, double V)> Points, double Start, double Span, double Min, double Max);

/// <summary>
/// A bench instrument as drawn: the logic analyser or the bench scope, on the bench below the board, its display showing
/// the channels' traces (logic in lanes, one under another; analog overlaid), a line of text under it (a decoder's
/// reading, the scales), and its leads out to their tips.
/// </summary>
public sealed record InstrumentView(string Title, double X, double Y, double Width, double Height, IReadOnlyList<InstrumentLead> Leads,
    IReadOnlyList<InstrumentTrace> Traces, string Note, bool Logic)
{
    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

/// <summary>The multimeter as drawn: where it stands on the bench, where its probe tips are, and what it reads.</summary>
/// <param name="X">Its top-left corner, in pitches.</param>
/// <param name="Y">See <paramref name="X"/>.</param>
/// <param name="Red">Where the red probe's tip is, in pitches.</param>
/// <param name="Black">Where the black probe's tip is.</param>
/// <param name="Reading">The display: "4.982", or dashes when it reads nothing.</param>
/// <param name="Note">Under the display: what it is measuring.</param>
/// <param name="Range">What it measures: 0 volts, 1 ohms, 2 continuity, 3 amps.</param>
/// <param name="Beep">Continuity: the probes are joined, and it is beeping.</param>
public sealed record MeterView(double X, double Y, (double X, double Y) Red, (double X, double Y) Black, string Reading, string Note, int Range = 0, bool Beep = false)
{
    /// <summary>The ranges round the dial, in order: what each reads in, as its display labels it.</summary>
    public static readonly string[] Units = ["V DC", "Ω", "•))) continuity", "mA"];

    public const double Width = 11.0;
    public const double Height = 16.0;

    /// <summary>Where the red and black leads leave the meter.</summary>
    public (double X, double Y) RedJack => (X + 7.6, Y + Height - 1.6);

    public (double X, double Y) BlackJack => (X + 3.4, Y + Height - 1.6);

    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;
}

/// <summary>
/// Draws a <see cref="ProtoboardLayout"/>: white breadboards with their rails and numbered columns, the parts as
/// they look from above (resistors with their colour bands, chips with their notch and number, LEDs lit by the
/// simulation), coloured jumper wires, and the bench equipment with its leads.
/// <para>
/// One unit of the layout (a pitch, 0.1 inch) is <see cref="Unit"/> drawing units. It draws through
/// <see cref="ISymbolCanvas"/>, so the screen and an export are the same drawing. The colours are the parts' own and
/// don't follow the theme: a breadboard is white and a resistor's bands are what they are.
/// </para>
/// </summary>
public static class ProtoboardRenderer
{
    /// <summary>Drawing units to one pitch.</summary>
    public const double Unit = 10.0;

    private static Point P(double x, double y) => new(x * Unit, y * Unit);

    private static Point P((double X, double Y) p) => P(p.X, p.Y);

    private static ImmutableSolidColorBrush Brush(string hex) => new(Color.Parse(hex));

    private static ImmutableSolidColorBrush Brush(Color colour) => new(colour);

    private static ImmutablePen Pen(string hex, double pitches) => new(Brush(hex), pitches * Unit, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    private static ImmutablePen Pen(Color colour, double pitches) => new(Brush(colour), pitches * Unit, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);

    private static readonly ImmutableSolidColorBrush BoardFill = Brush("#F8F7F3");
    private static readonly ImmutablePen BoardEdge = Pen("#CFCBC1", 0.08);
    private static readonly ImmutableSolidColorBrush Shadow = Brush("#2A000000");
    private static readonly ImmutableSolidColorBrush Channel = Brush("#E5E2DA");
    private static readonly ImmutableSolidColorBrush Hole = Brush("#3E3D3A");
    private static readonly ImmutableSolidColorBrush HoleRim = Brush("#E2DFD7");
    private static readonly ImmutableSolidColorBrush Lit = Brush("#5BC46A");
    private static readonly ImmutablePen RedLine = Pen("#D7352C", 0.12);
    private static readonly ImmutablePen BlueLine = Pen("#2F62D0", 0.12);
    private static readonly ImmutableSolidColorBrush Print = Brush("#8D897F");
    private static readonly ImmutablePen Lead = Pen("#A3A8AD", 0.16);
    private static readonly ImmutablePen LeadShade = Pen("#6E7378", 0.22);
    private static readonly ImmutableSolidColorBrush White = Brush("#FFFFFF");
    private static readonly ImmutableSolidColorBrush Ink = Brush("#2B2B2B");
    private static readonly ImmutableSolidColorBrush Silver = Brush("#C9CDD2");
    private static readonly ImmutablePen Selection = Pen("#2F80ED", 0.22);

    /// <summary>The jumper colours for signals, in the order nets are met, after black for ground and red for the supply.</summary>
    private static readonly string[] SignalColours =
        ["#F2C230", "#2E9E4F", "#2F6FD6", "#F4F4F4", "#8E44AD", "#E67E22", "#16A5A5", "#8B5A2B", "#E84393", "#7F8C8D", "#9ACD32", "#5D6DFF"];

    public static Color WireColour(WireRole role, int colour) => role switch
    {
        WireRole.Ground => Color.Parse("#1E1E1E"),
        WireRole.Supply => Color.Parse("#D7261E"),
        WireRole.OtherSupply => Color.Parse("#F07B16"),
        _ => Color.Parse(SignalColours[colour % SignalColours.Length]),
    };

    /// <summary>The layout being drawn, whose geometry places the holes: breadboard, stripboard or perfboard.</summary>
    [ThreadStatic]
    private static ProtoboardLayout? _drawing;

    private static (double X, double Y) Pos(BoardHole hole) => _drawing?.Position(hole) ?? Breadboard.Position(hole);

    private static bool Up(BoardHole hole) => _drawing?.Up(hole) ?? Breadboard.IsTopHalf(hole.Row);

    /// <summary>Draws the whole layout.</summary>
    public static void Draw(ISymbolCanvas canvas, ProtoboardLayout layout, ProtoboardRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(layout);
        options ??= new ProtoboardRenderOptions();
        var previous = _drawing;
        _drawing = layout;
        try
        {
            bool Shown(object item) => options.Visible is not { } visible || visible.Contains(item);

            if (layout.Kind == BoardKind.Breadboard)
                for (var board = 1; board <= layout.Boards; board++) DrawBoard(canvas, layout, board, options);
            else DrawSoldered(canvas, layout, options, Shown);

            // The wires lie on the board and the parts stand on top of them, as they do on a real one: a wire is never
            // drawn over a part, and the layout routes them round the parts where it can, so they stay in sight too.
            // Perfboard's are on the back, seen through the board.
            // A PCB's traces are on the back too, its jumpers on top.
            foreach (var wire in layout.Wires.Where(Shown))
                if (wire.IsTrace) DrawTrace(canvas, wire, ReferenceEquals(wire, options.Wire), layout.Layers);
                else DrawWire(canvas, wire, ReferenceEquals(wire, options.Wire), back: layout.Kind == BoardKind.Perfboard);
            foreach (var via in layout.Vias) DrawVia(canvas, via);
            foreach (var item in layout.Bench.Where(Shown))
                foreach (var lead in item.Leads) DrawLead(canvas, lead);

            foreach (var part in layout.Parts.Where(p => p.Package.Form == BoardForm.Dip && Shown(p))) DrawDip(canvas, part);
            foreach (var part in layout.Parts.Where(p => p.Package.Form == BoardForm.Inline && Shown(p))) DrawInline(canvas, part);
            foreach (var part in layout.Parts.Where(p => p.Package.Form == BoardForm.TwoLead && Shown(p))) DrawTwoLead(canvas, part);
            foreach (var item in layout.Bench.Where(Shown)) DrawBench(canvas, item);

            if (options.Current is { } step) DrawStep(canvas, layout, step);

            // What the design rule check found, ringed in red.
            foreach (var (x, y) in options.Marks ?? [])
                canvas.DrawEllipse(null, Pen("#E0D7261E", 0.18), P(x, y), 0.7 * Unit, 0.7 * Unit);
            if (options.Meter is { } meter) DrawMeter(canvas, meter);
            foreach (var instrument in options.Instruments ?? []) DrawInstrument(canvas, instrument);

            if (options.Selected is { } selected && Bounds(layout, selected) is { } box)
                canvas.DrawRectangle(null, Selection, new RoundedRect(box.Inflate(0.5 * Unit), 0.6 * Unit));
        }
        finally
        {
            _drawing = previous;
        }
    }

    private static readonly ImmutablePen StepRing = Pen("#E0FF9800", 0.3);

    /// <summary>The build guide's step on screen: its part or wire ringed in amber, and the holes it goes in marked.</summary>
    private static void DrawStep(ISymbolCanvas canvas, ProtoboardLayout layout, BuildStep step)
    {
        if (step.Part is { } part && Bounds(layout, part.Part) is { } box)
            canvas.DrawRectangle(null, StepRing, new RoundedRect(box.Inflate(0.8 * Unit), 0.8 * Unit));
        if (step.Bench is { } bench && Bounds(layout, bench.Part) is { } benchBox)
            canvas.DrawRectangle(null, StepRing, new RoundedRect(benchBox.Inflate(0.8 * Unit), 1.0 * Unit));
        if (step.Wire is { } wire) canvas.DrawGeometry(null, Pen("#80FF9800", 0.9), Along(wire.Route));
        foreach (var hole in step.Holes ?? [])
            canvas.DrawEllipse(null, Pen("#FF9800", 0.14), P(Pos(hole)), 0.55 * Unit, 0.55 * Unit);
    }

    /// <summary>What a part covers on the drawing, in drawing units: for picking it with the pointer, and ringing it.</summary>
    public static Rect? Bounds(ProtoboardLayout layout, CircuitComponent part)
    {
        if (layout.FindOnBench(part) is { } bench) return new Rect(P(bench.X, bench.Y), P(bench.X + bench.Width, bench.Y + bench.Height));
        if (layout.Find(part) is not { } placed) return null;

        var previous = _drawing;
        _drawing = layout;
        try
        {
            return BoundsOf(placed);
        }
        finally
        {
            _drawing = previous;
        }
    }

    private static Rect BoundsOf(BoardPart placed)
    {
        var points = placed.Holes.Select(Pos).ToList();
        var (left, top) = (points.Min(p => p.X), points.Min(p => p.Y));
        var (right, bottom) = (points.Max(p => p.X), points.Max(p => p.Y));
        switch (placed.Package.Form)
        {
            case BoardForm.Dip:
                return new Rect(P(left - 0.5, top - 0.3), P(right + 0.5, bottom + 0.3));
            case BoardForm.Inline:
                var rows = ProtoboardLayout.BodyRows(placed.Package.Look) + 0.9;
                var up = Up(placed.Holes[0]);
                return new Rect(P(left - 0.7, up ? top - rows : top - 0.7), P(right + 0.7, up ? bottom + 0.7 : bottom + rows));
            default:
                var thick = Math.Max(0.8, Radius(placed) + 0.2);
                return new Rect(P(left - thick, top - thick), P(right + thick, bottom + thick));
        }
    }

    // ───────────────────────────── the board ─────────────────────────────

    private static void DrawBoard(ISymbolCanvas canvas, ProtoboardLayout layout, int board, ProtoboardRenderOptions options)
    {
        var top = Breadboard.Top(board);
        var body = new Rect(P(0, top), P(Breadboard.Width, top + Breadboard.Height));
        canvas.DrawRectangle(Shadow, null, new RoundedRect(body.Translate(new Vector(0.35 * Unit, 0.45 * Unit)), 0.8 * Unit));
        canvas.DrawRectangle(BoardFill, BoardEdge, new RoundedRect(body, 0.8 * Unit));

        // The channel down the middle, and the rails' printed lines.
        canvas.FillRectangle(Channel, new Rect(P(0.6, top + 9.35), P(Breadboard.Width - 0.6, top + 10.45)));
        foreach (var (y, pen) in new[] { (0.65, RedLine), (3.05, BlueLine), (16.0, BlueLine), (18.25, RedLine) })
            canvas.DrawLine(pen, P(3.6, top + y), P(64.4, top + y));
        foreach (var x in new[] { 2.4, 65.6 })
        {
            canvas.DrawText("+", P(x, top + 1.4), 1.2 * Unit, RedLine.Brush!, SymbolTextAlign.Centre);
            canvas.DrawText("−", P(x, top + 2.4), 1.2 * Unit, BlueLine.Brush!, SymbolTextAlign.Centre);
            canvas.DrawText("−", P(x, top + 16.6), 1.2 * Unit, BlueLine.Brush!, SymbolTextAlign.Centre);
            canvas.DrawText("+", P(x, top + 17.6), 1.2 * Unit, RedLine.Brush!, SymbolTextAlign.Centre);
        }

        // Row letters at both ends, and the column numbers every five.
        for (var row = Breadboard.A; row <= Breadboard.J; row++)
        {
            var letter = ((char)('a' + row - Breadboard.A)).ToString();
            var y = top + Breadboard.RowPosition(row);
            canvas.DrawText(letter, P(1.6, y), 0.75 * Unit, Print, SymbolTextAlign.Centre);
            canvas.DrawText(letter, P(66.4, y), 0.75 * Unit, Print, SymbolTextAlign.Centre);
        }

        foreach (var column in new[] { 1 }.Concat(Enumerable.Range(1, 12).Select(i => i * 5)).Append(63))
        {
            var x = 2.5 + column;
            canvas.DrawText(column.ToString(System.Globalization.CultureInfo.InvariantCulture), P(x, top + 3.65), 0.6 * Unit, Print, SymbolTextAlign.Centre);
            canvas.DrawText(column.ToString(System.Globalization.CultureInfo.InvariantCulture), P(x, top + 9.9), 0.6 * Unit, Print, SymbolTextAlign.Centre);
        }

        if (layout.Boards > 1)
            canvas.DrawText($"Board {board}", P(Breadboard.Width - 1.0, top + Breadboard.Height - 0.55), 0.7 * Unit, Print, SymbolTextAlign.Right);

        // What each rail carries, at the left end.
        foreach (var (row, net) in layout.Rails)
            canvas.DrawText(layout.Caption(net), P(-0.4, top + Breadboard.RowPosition(row)), 0.75 * Unit,
                Breadboard.IsPlusRail(row) ? RedLine.Brush! : BlueLine.Brush!, SymbolTextAlign.Right);

        // The holes, the strip under the pointer lit to show what it's joined to.
        for (var row = 0; row < Breadboard.Rows; row++)
        {
            for (var column = 1; column <= Breadboard.Columns; column++)
            {
                var hole = new BoardHole(board, row, column);
                if (!Breadboard.Exists(hole)) continue;
                var centre = P(Breadboard.Position(hole));
                var lit = options.Strip is { } strip && Breadboard.StripOf(hole) == strip;
                canvas.FillRectangle(lit ? Lit : HoleRim, new Rect(centre - new Point(0.3 * Unit, 0.3 * Unit), new Size(0.6 * Unit, 0.6 * Unit)));
                canvas.FillRectangle(Hole, new Rect(centre - new Point(0.19 * Unit, 0.19 * Unit), new Size(0.38 * Unit, 0.38 * Unit)));
            }
        }
    }

    // ───────────────────────────── stripboard and perfboard ─────────────────────────────

    private static readonly ImmutableSolidColorBrush Copper = Brush("#B0D9894A");
    private static readonly ImmutableSolidColorBrush Pad = Brush("#D9894A");
    private static readonly ImmutableSolidColorBrush Drill = Brush("#2A2420");

    /// <summary>
    /// A stripboard or perfboard seen from the component side, the copper shown through: a stripboard's strips with
    /// their cuts (the copper drilled away, marked with a cross), a perfboard's pad at every hole. Strips or columns are
    /// lettered across the top and holes numbered down the side.
    /// </summary>
    private static void DrawSoldered(ISymbolCanvas canvas, ProtoboardLayout layout, ProtoboardRenderOptions options, Func<object, bool> shown)
    {
        var strips = layout.Kind == BoardKind.Stripboard;
        var pcb = layout.Kind.IsPcb();
        var width = SolderedLayout.Width(layout.Columns);
        var height = SolderedLayout.Height(layout.Rows);
        var body = new Rect(P(0, 0), P(width, height));
        canvas.DrawRectangle(Shadow, null, new RoundedRect(body.Translate(new Vector(0.35 * Unit, 0.45 * Unit)), 0.4 * Unit));
        canvas.DrawRectangle(Brush(strips ? "#C9A36B" : pcb ? "#1E6B3C" : "#3C7A55"), Pen(strips ? "#9C7A48" : pcb ? "#134A29" : "#2A5A3E", 0.08), new RoundedRect(body, pcb ? 0.15 * Unit : 0.4 * Unit));

        // A PCB's ground pour, under the solder mask: the back's seen through the board, the component side's nearer.
        foreach (var layer in new[] { 0, 1 })
        {
            var poured = Brush(layer == 0 ? "#247A47" : "#2C8A52");
            foreach (var area in layout.Pour.Where(a => a.Layer == layer))
                canvas.FillRectangle(poured, new Rect(P(area.Left, area.Top), P(area.Right, area.Bottom)));
        }

        if (strips)
            foreach (var segment in layout.Segments)
            {
                var (x, top) = SolderedLayout.Position(new BoardHole(1, segment.From, segment.Column));
                var (_, bottom) = SolderedLayout.Position(new BoardHole(1, segment.To, segment.Column));
                canvas.FillRectangle(Copper, new Rect(P(x - 0.4, top - 0.45), P(x + 0.4, bottom + 0.45)));
            }

        var lit = options.Lit ?? new HashSet<BoardHole>();
        var vias = layout.Vias.ToHashSet();
        for (var row = 1; row <= layout.Rows; row++)
            for (var column = 1; column <= layout.Columns; column++)
            {
                var hole = new BoardHole(1, row, column);
                var centre = P(SolderedLayout.Position(hole));

                // A PCB is drilled only where something goes: a tinned pad, the rest solder mask.
                if (pcb)
                {
                    if ((!layout.HoleNets.ContainsKey(hole) && layout.PartAt(hole) is null) || vias.Contains(hole))
                    {
                        if (lit.Contains(hole)) canvas.DrawEllipse(Lit, null, centre, 0.25 * Unit, 0.25 * Unit);
                        continue;
                    }

                    var square = layout.PartAt(hole) is { Package.Form: BoardForm.Dip or BoardForm.Inline } owner && owner.Holes[0] == hole;
                    var radius = PcbRouter.PadDiameter / PcbRouter.Pitch / 2;
                    if (square) canvas.FillRectangle(Brush("#D8C9A0"), new Rect(centre - new Point(radius * Unit, radius * Unit), new Size(2 * radius * Unit, 2 * radius * Unit)));
                    else canvas.DrawEllipse(Brush("#D8C9A0"), null, centre, radius * Unit, radius * Unit);
                    if (lit.Contains(hole)) canvas.DrawEllipse(Lit, null, centre, 0.4 * Unit, 0.4 * Unit);
                    canvas.DrawEllipse(Drill, null, centre, PcbRouter.DrillDiameter / PcbRouter.Pitch / 2 * Unit, PcbRouter.DrillDiameter / PcbRouter.Pitch / 2 * Unit);
                    continue;
                }

                if (!strips) canvas.DrawEllipse(Pad, null, centre, 0.34 * Unit, 0.34 * Unit);
                if (lit.Contains(hole)) canvas.DrawEllipse(Lit, null, centre, 0.4 * Unit, 0.4 * Unit);
                canvas.DrawEllipse(Drill, null, centre, 0.15 * Unit, 0.15 * Unit);
            }

        foreach (var cut in layout.Cuts.Where(c => shown(c)))
        {
            var centre = P(SolderedLayout.Position(cut));
            canvas.DrawEllipse(Brush(strips ? "#C9A36B" : "#3C7A55"), null, centre, 0.42 * Unit, 0.42 * Unit);
            var cross = Pen("#C62828", 0.1);
            canvas.DrawLine(cross, new Point(centre.X - (0.3 * Unit), centre.Y - (0.3 * Unit)), new Point(centre.X + (0.3 * Unit), centre.Y + (0.3 * Unit)));
            canvas.DrawLine(cross, new Point(centre.X - (0.3 * Unit), centre.Y + (0.3 * Unit)), new Point(centre.X + (0.3 * Unit), centre.Y - (0.3 * Unit)));
        }

        var print = Brush(strips ? "#6E5530" : pcb ? "#9CC9AB" : "#CFE3D6");
        for (var column = 1; column <= layout.Columns; column++)
            canvas.DrawText(SolderedLayout.Letters(column), P(SolderedLayout.Position(new BoardHole(1, 0, column)).X, 1.2), 0.5 * Unit, print, SymbolTextAlign.Centre);
        for (var row = 1; row <= layout.Rows; row++)
            if (row == 1 || row % 5 == 0)
                canvas.DrawText(row.ToString(System.Globalization.CultureInfo.InvariantCulture), P(1.6, SolderedLayout.Position(new BoardHole(1, row, 0)).Y), 0.5 * Unit, print, SymbolTextAlign.Centre);
    }

    // ───────────────────────────── bench instruments ─────────────────────────────

    /// <summary>A logic analyser or a bench scope: its body, its display with the traces, its note, and its leads.</summary>
    private static void DrawInstrument(ISymbolCanvas canvas, InstrumentView view)
    {
        foreach (var lead in view.Leads)
        {
            var (a, b) = (P(lead.Jack), P(lead.Tip));
            var path = new SymbolPath();
            using (var ctx = path.Open())
            {
                ctx.BeginFigure(a, false);
                ctx.CubicBezierTo(new Point(a.X, a.Y - (3 * Unit)), new Point(b.X, b.Y + (3 * Unit)), b);
                ctx.EndFigure(false);
            }

            canvas.DrawGeometry(null, Pen("#33000000", 0.36), Shifted(path));
            canvas.DrawGeometry(null, Pen(lead.Colour, 0.22), path);

            // A grabber at the tip, its name beside it until it's clipped on.
            canvas.DrawEllipse(Brush(lead.Colour), Pen("#000000", 0.04), new Point(b.X, b.Y + (0.5 * Unit)), 0.25 * Unit, 0.45 * Unit);
            canvas.DrawEllipse(Brush("#E6E8EA"), null, b, 0.1 * Unit, 0.1 * Unit);
            if (!lead.Attached) canvas.DrawText(lead.Name, new Point(b.X, b.Y - (0.6 * Unit)), 0.45 * Unit, Brush("#3A3F45"), SymbolTextAlign.Centre);
        }

        var body = new Rect(P(view.X, view.Y), P(view.X + view.Width, view.Y + view.Height));
        canvas.DrawRectangle(Shadow, null, new RoundedRect(body.Translate(new Vector(0.35 * Unit, 0.45 * Unit)), 0.8 * Unit));
        canvas.DrawRectangle(Brush("#3A4048"), Pen("#22262B", 0.1), new RoundedRect(body, 0.8 * Unit));
        canvas.DrawText(view.Title, new Point(body.Left + (0.8 * Unit), body.Top + (1.0 * Unit)), 0.6 * Unit, Brush("#E8EAED"), SymbolTextAlign.Left);

        var screen = new Rect(body.Left + (0.8 * Unit), body.Top + (1.8 * Unit), body.Width - (1.6 * Unit), body.Height - (4.4 * Unit));
        canvas.DrawRectangle(Brush("#0E1A14"), Pen("#5A6650", 0.05), new RoundedRect(screen, 0.3 * Unit));
        var grid = Pen("#1E3A2A", 0.03);
        for (var i = 1; i < 10; i++) canvas.DrawLine(grid, new Point(screen.Left + (screen.Width * i / 10), screen.Top), new Point(screen.Left + (screen.Width * i / 10), screen.Bottom));

        var lanes = view.Logic ? Math.Max(1, view.Traces.Count) : 1;
        for (var i = 0; i < view.Traces.Count; i++)
        {
            var trace = view.Traces[i];
            if (trace.Points.Count < 2 || trace.Span <= 0) continue;
            var lane = view.Logic ? i : 0;
            var top = screen.Top + (screen.Height * lane / lanes) + (0.15 * Unit);
            var height = (screen.Height / lanes) - (0.3 * Unit);
            double X(double t) => screen.Left + (Math.Clamp((t - trace.Start) / trace.Span, 0, 1) * screen.Width);
            double Y(double v) => top + height - (Math.Clamp((v - trace.Min) / Math.Max(1e-12, trace.Max - trace.Min), 0, 1) * height);
            var path = new SymbolPath();
            using (var ctx = path.Open())
            {
                var first = true;
                var lastY = 0.0;
                foreach (var (t, v) in trace.Points)
                {
                    var (x, y) = (X(t), Y(v));
                    if (first) { ctx.BeginFigure(new Point(x, y), false); first = false; }
                    else
                    {
                        if (view.Logic) ctx.LineTo(new Point(x, lastY));
                        ctx.LineTo(new Point(x, y));
                    }

                    lastY = y;
                }

                ctx.EndFigure(false);
            }

            canvas.DrawGeometry(null, Pen(trace.Colour, 0.08), path);
        }

        canvas.DrawText(view.Note, new Point(body.Left + (0.8 * Unit), body.Bottom - (1.2 * Unit)), 0.5 * Unit, Brush("#C8D0C0"), SymbolTextAlign.Left);
    }

    // ───────────────────────────── the multimeter ─────────────────────────────

    /// <summary>A handheld meter on the bench, its display reading the probes, and its red and black leads to their tips.</summary>
    private static void DrawMeter(ISymbolCanvas canvas, MeterView meter)
    {
        foreach (var (jack, tip, colour) in new[] { (meter.BlackJack, meter.Black, "#1E1E1E"), (meter.RedJack, meter.Red, "#D7261E") })
        {
            var a = P(jack);
            var b = P(tip);
            var path = new SymbolPath();
            using (var ctx = path.Open())
            {
                ctx.BeginFigure(a, false);
                ctx.CubicBezierTo(new Point(a.X, a.Y + (4 * Unit)), new Point(b.X, b.Y + (3 * Unit)), b);
                ctx.EndFigure(false);
            }

            canvas.DrawGeometry(null, Pen("#33000000", 0.55), Shifted(path));
            canvas.DrawGeometry(null, Pen(colour, 0.36), path);

            // The probe: a handle and a steel point at the tip.
            canvas.DrawEllipse(Brush(colour), Pen("#000000", 0.05), new Point(b.X, b.Y + (0.9 * Unit)), 0.35 * Unit, 0.7 * Unit);
            canvas.DrawLine(Pen("#C9CDD2", 0.12), new Point(b.X, b.Y + (0.3 * Unit)), b);
            canvas.DrawEllipse(Brush("#E6E8EA"), null, b, 0.12 * Unit, 0.12 * Unit);
        }

        var body = new Rect(P(meter.X, meter.Y), P(meter.X + MeterView.Width, meter.Y + MeterView.Height));
        canvas.DrawRectangle(Shadow, null, new RoundedRect(body.Translate(new Vector(0.35 * Unit, 0.45 * Unit)), 1.2 * Unit));
        canvas.DrawRectangle(Brush("#F2B705"), Pen("#B88900", 0.1), new RoundedRect(body, 1.2 * Unit));
        var inner = body.Deflate(0.6 * Unit);
        canvas.DrawRectangle(Brush("#2B2B2E"), null, new RoundedRect(inner, 0.8 * Unit));
        var display = new Rect(inner.X + (0.5 * Unit), inner.Y + (0.6 * Unit), inner.Width - (1.0 * Unit), 3.4 * Unit);
        canvas.DrawRectangle(Brush("#B9C7A8"), Pen("#5A6650", 0.06), new RoundedRect(display, 0.3 * Unit));
        canvas.DrawText(meter.Reading, new Point(display.Right - (0.4 * Unit), display.Center.Y), 1.7 * Unit, Brush("#1C2414"), SymbolTextAlign.Right);
        canvas.DrawText(MeterView.Units[meter.Range], new Point(display.Left + (0.4 * Unit), display.Bottom - (0.55 * Unit)), 0.55 * Unit, Brush("#1C2414"), SymbolTextAlign.Left);
        canvas.DrawText(meter.Note, new Point(inner.Center.X, display.Bottom + (0.9 * Unit)), 0.55 * Unit, Brush("#D9D9D9"), SymbolTextAlign.Centre);

        // The dial, pointing at the range: V, Ω, continuity and A round its top.
        var dial = new Point(inner.Center.X, inner.Y + (8.6 * Unit));
        canvas.DrawEllipse(Brush("#3A3A3E"), Pen("#121214", 0.08), dial, 2.2 * Unit, 2.2 * Unit);
        string[] marks = ["V⎓", "Ω", "•)))", "A"];
        for (var i = 0; i < marks.Length; i++)
        {
            var angle = (-135 + (i * 90)) * Math.PI / 180 / 1.5;
            var at = new Point(dial.X + (Math.Sin(angle) * 3.0 * Unit), dial.Y - (Math.Cos(angle) * 3.0 * Unit));
            canvas.DrawText(marks[i], at, 0.55 * Unit, Brush(i == meter.Range ? "#FFFFFF" : "#9A9A9E"), SymbolTextAlign.Centre);
        }

        var pointer = (-135 + (meter.Range * 90)) * Math.PI / 180 / 1.5;
        canvas.DrawLine(Pen("#E6E6E6", 0.22), dial, new Point(dial.X + (Math.Sin(pointer) * 1.6 * Unit), dial.Y - (Math.Cos(pointer) * 1.6 * Unit)));
        if (meter.Beep)
            canvas.DrawText("♪ beep", new Point(display.Left + (0.4 * Unit), display.Top + (0.6 * Unit)), 0.6 * Unit, Brush("#1C2414"), SymbolTextAlign.Left);

        foreach (var (jack, colour, label) in new[] { (meter.BlackJack, "#111111", "COM"), (meter.RedJack, "#C62828", "V") })
        {
            canvas.DrawEllipse(Brush(colour), Pen("#000000", 0.06), P(jack), 0.5 * Unit, 0.5 * Unit);
            canvas.DrawText(label, new Point(P(jack).X, P(jack).Y - (0.95 * Unit)), 0.45 * Unit, Brush("#D9D9D9"), SymbolTextAlign.Centre);
        }
    }

    // ───────────────────────────── wires ─────────────────────────────

    /// <summary>
    /// A PCB's copper trace under the solder mask, as a lighter green: the back's seen through the board, the component
    /// side's brighter. A double-sided board's vias are drawn with the top-side traces.
    /// </summary>
    private static void DrawTrace(ISymbolCanvas canvas, BoardWire wire, bool picked, int layers)
    {
        var path = new SymbolPath();
        using (var ctx = path.Open())
        {
            ctx.BeginFigure(P(wire.Route[0]), false);
            foreach (var point in wire.Route.Skip(1)) ctx.LineTo(P(point));
            ctx.EndFigure(false);
        }

        var width = (wire.Role == WireRole.Signal ? PcbRouter.SignalWidth : PcbRouter.PowerWidth) / PcbRouter.Pitch;
        canvas.DrawGeometry(null, Pen(layers == 2 && wire.Layer == 0 ? "#64B57F" : wire.Layer == 1 ? "#B8F3C8" : "#7FD39A", width), path);
        if (picked) canvas.DrawGeometry(null, Pen("#802F80ED", width + 0.4), path);
    }

    private static void DrawVia(ISymbolCanvas canvas, BoardHole via)
    {
        var centre = P(Pos(via));
        canvas.DrawEllipse(Brush("#D8C9A0"), null, centre, PcbRouter.ViaDiameter / PcbRouter.Pitch / 2 * Unit, PcbRouter.ViaDiameter / PcbRouter.Pitch / 2 * Unit);
        canvas.DrawEllipse(Drill, null, centre, PcbRouter.ViaDrill / PcbRouter.Pitch / 2 * Unit, PcbRouter.ViaDrill / PcbRouter.Pitch / 2 * Unit);
    }

    private static void DrawWire(ISymbolCanvas canvas, BoardWire wire, bool picked, bool back = false)
    {
        var colour = WireColour(wire.Role, wire.Colour);
        var path = Along(wire.Route);
        if (back)
        {
            // On the back of a perfboard, seen through it: thinner and fainter than a wire on top.
            canvas.DrawGeometry(null, Pen(Color.FromArgb(150, colour.R, colour.G, colour.B), 0.2), path);
            foreach (var end in new[] { wire.From, wire.To })
                canvas.DrawEllipse(Brush("#C0C9CDD2"), null, P(Pos(end)), 0.22 * Unit, 0.22 * Unit);
            return;
        }

        canvas.DrawGeometry(null, Pen("#33000000", 0.5), Shifted(path));
        canvas.DrawGeometry(null, Pen(Darker(colour, 0.45), 0.42), path);
        canvas.DrawGeometry(null, Pen(colour, 0.3), path);
        if (picked) canvas.DrawGeometry(null, Pen("#802F80ED", 0.7), path);
        foreach (var end in new[] { wire.From, wire.To })
            canvas.DrawEllipse(Silver, Pen("#7D838A", 0.05), P(Pos(end)), 0.2 * Unit, 0.2 * Unit);
    }

    /// <summary>
    /// A wire's route as a path: a curve's points joined as they are, and a dog-leg's corners rounded off, as a wire
    /// bent by hand is.
    /// </summary>
    private static SymbolPath Along(IReadOnlyList<(double X, double Y)> route)
    {
        var points = route.Select(P).ToList();
        var path = new SymbolPath();
        using var ctx = path.Open();
        ctx.BeginFigure(points[0], false);
        if (points.Count > 2)
        {
            for (var i = 1; i < points.Count - 1; i++)
            {
                var (prev, corner, next) = (points[i - 1], points[i], points[i + 1]);
                var inLength = Math.Sqrt(((corner.X - prev.X) * (corner.X - prev.X)) + ((corner.Y - prev.Y) * (corner.Y - prev.Y)));
                var outLength = Math.Sqrt(((next.X - corner.X) * (next.X - corner.X)) + ((next.Y - corner.Y) * (next.Y - corner.Y)));
                var r = Math.Min(0.8 * Unit, Math.Min(inLength, outLength) / 2);
                if (r < 0.01 || inLength < 1e-6 || outLength < 1e-6)
                {
                    ctx.LineTo(corner);
                    continue;
                }

                var before = new Point(corner.X - ((corner.X - prev.X) / inLength * r), corner.Y - ((corner.Y - prev.Y) / inLength * r));
                var after = new Point(corner.X + ((next.X - corner.X) / outLength * r), corner.Y + ((next.Y - corner.Y) / outLength * r));
                ctx.LineTo(before);
                ctx.CubicBezierTo(new Point((before.X + corner.X) / 2, (before.Y + corner.Y) / 2), new Point((after.X + corner.X) / 2, (after.Y + corner.Y) / 2), after);
            }

            ctx.LineTo(points[^1]);
        }
        else
        {
            for (var i = 1; i < points.Count; i++) ctx.LineTo(points[i]);
        }

        ctx.EndFigure(false);
        return path;
    }

    /// <summary>The same path a little down and to the right, for its shadow.</summary>
    private static SymbolPath Shifted(SymbolPath path)
    {
        var shifted = new SymbolPath();
        using var ctx = shifted.Open();
        path.Replay(new Offset(ctx, 0.25 * Unit, 0.3 * Unit));
        return shifted;
    }

    private sealed class Offset(ISymbolPathSink sink, double dx, double dy) : ISymbolPathSink
    {
        private Point M(Point p) => new(p.X + dx, p.Y + dy);
        public void BeginFigure(Point start, bool isFilled) => sink.BeginFigure(M(start), isFilled);
        public void LineTo(Point point) => sink.LineTo(M(point));
        public void CubicBezierTo(Point c1, Point c2, Point end) => sink.CubicBezierTo(M(c1), M(c2), M(end));
        public void ArcTo(Point point, Size size, double rotationAngle, bool isLargeArc, SweepDirection sweep) => sink.ArcTo(M(point), size, rotationAngle, isLargeArc, sweep);
        public void EndFigure(bool isClosed) => sink.EndFigure(isClosed);
    }

    private static Color Darker(Color c, double by) =>
        Color.FromArgb(c.A, (byte)(c.R * (1 - by)), (byte)(c.G * (1 - by)), (byte)(c.B * (1 - by)));

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)(a.A + ((b.A - a.A) * t)), (byte)(a.R + ((b.R - a.R) * t)), (byte)(a.G + ((b.G - a.G) * t)), (byte)(a.B + ((b.B - a.B) * t)));

    private static Color Of(CoreColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    // ───────────────────────────── chips ─────────────────────────────

    private static void DrawDip(ISymbolCanvas canvas, BoardPart part)
    {
        var holes = part.Holes;
        var half = holes.Count / 2;
        var (left, bottomY) = Pos(holes[0]);
        var (right, topY) = Pos(holes[half - 1] with { Row = holes[^1].Row });
        var body = new Rect(P(left - 0.45, topY + 0.25), P(right + 0.45, bottomY - 0.25));

        // The legs, from the body's edge into each hole.
        foreach (var hole in holes)
        {
            var (x, y) = Pos(hole);
            var toward = y < body.Center.Y / Unit ? body.Top : body.Bottom;
            canvas.FillRectangle(Silver, new Rect(new Point((x - 0.17) * Unit, Math.Min(y * Unit, toward)), new Point((x + 0.17) * Unit, Math.Max(y * Unit, toward))));
        }

        canvas.DrawRectangle(Shadow, null, new RoundedRect(body.Translate(new Vector(0.25 * Unit, 0.3 * Unit)), 0.25 * Unit));

        var look = part.Package.Look;
        var fill = look switch
        {
            BoardLook.DipSwitch => Brush("#C62828"),
            BoardLook.SevenSegment => Brush("#E9E9E6"),
            _ => Brush("#232427"),
        };

        canvas.DrawRectangle(fill, Pen("#111111", 0.05), new RoundedRect(body, 0.25 * Unit));

        if (look == BoardLook.SevenSegment)
        {
            DrawDigit(canvas, part.Part, body);
            Caption(canvas, part.Part.Name, new Point(body.Center.X, body.Bottom - (0.5 * Unit)), Ink);
            return;
        }

        if (look == BoardLook.DipSwitch && part.Part is DipSwitch dip)
        {
            for (var i = 0; i < half; i++)
            {
                var (x, _) = Pos(holes[i]);
                var slot = new Rect(P(x - 0.25, (body.Top / Unit) + 0.35), P(x + 0.25, (body.Bottom / Unit) - 0.35));
                canvas.FillRectangle(Brush("#7F1414"), slot);
                var on = dip.IsClosed(i);
                var knob = on ? new Rect(slot.X, slot.Y, slot.Width, slot.Height / 2) : new Rect(slot.X, slot.Center.Y, slot.Width, slot.Height / 2);
                canvas.FillRectangle(White, knob);
            }

            Caption(canvas, part.Part.Name, new Point(body.Center.X, body.Top - (0.55 * Unit)), Ink);
            return;
        }

        // The notch at pin 1's end, the dot by pin 1, the number and the designator.
        canvas.DrawEllipse(Brush("#3B3C40"), null, new Point(body.Left, body.Center.Y), 0.45 * Unit, 0.45 * Unit);
        canvas.DrawEllipse(Brush("#55575C"), null, new Point(body.Left + (0.55 * Unit), body.Bottom - (0.45 * Unit)), 0.17 * Unit, 0.17 * Unit);
        // The number as big as the body allows, shrunk to fit a short chip rather than cut off.
        var size = Math.Min(1.0, Math.Max(0.55, (body.Height / Unit) * 0.38));
        var room = body.Width - (1.3 * Unit);
        var wide = canvas.MeasureText(part.Package.Label, size * Unit);
        if (wide > room && wide > 0) size = Math.Max(0.4, size * room / wide);
        canvas.DrawText(Fit(canvas, part.Package.Label, room, size * Unit), new Point(body.Center.X, body.Center.Y - (size * 0.22 * Unit)), size * Unit, Brush("#E8E8E8"), SymbolTextAlign.Centre);
        canvas.DrawText(part.Part.Name, new Point(body.Center.X, body.Center.Y + (size * 0.62 * Unit)), size * 0.62 * Unit, Brush("#A9ABB0"), SymbolTextAlign.Centre);
    }

    /// <summary>A seven-segment digit, its segments lit as the simulation has them.</summary>
    private static void DrawDigit(ISymbolCanvas canvas, CircuitComponent part, Rect body)
    {
        var display = part as SevenSegmentDisplay;
        var colour = display is null ? Color.Parse("#FF4136") : Of(display.EmittedColor);
        var w = body.Width * 0.42;
        var h = body.Height * 0.62;
        var (cx, cy) = (body.Center.X - (body.Width * 0.06), body.Center.Y - (body.Height * 0.04));
        var (l, r, t, m, b) = (cx - (w / 2), cx + (w / 2), cy - (h / 2), cy, cy + (h / 2));
        (Point, Point)[] segments =
        [
            (new Point(l, t), new Point(r, t)), (new Point(r, t), new Point(r, m)), (new Point(r, m), new Point(r, b)),
            (new Point(l, b), new Point(r, b)), (new Point(l, m), new Point(l, b)), (new Point(l, t), new Point(l, m)),
            (new Point(l, m), new Point(r, m)),
        ];
        for (var i = 0; i < segments.Length; i++)
        {
            var on = display is not null && i < display.SegmentBrightness.Count ? CanvasTheme.Emission(display.SegmentBrightness[i]) : 0;
            canvas.DrawLine(Pen(Mix(Color.Parse("#D6D3CE"), colour, on), 0.35), segments[i].Item1, segments[i].Item2);
        }

        var dp = display is not null && display.SegmentBrightness.Count > 7 ? CanvasTheme.Emission(display.SegmentBrightness[7]) : 0;
        canvas.DrawEllipse(Brush(Mix(Color.Parse("#D6D3CE"), colour, dp)), null, new Point(r + (0.5 * Unit), b), 0.2 * Unit, 0.2 * Unit);
    }

    private static string Fit(ISymbolCanvas canvas, string text, double width, double size)
    {
        if (canvas.MeasureText(text, size) <= width) return text;
        while (text.Length > 3 && canvas.MeasureText(text + "…", size) > width) text = text[..^1];
        return text + "…";
    }

    private static void Caption(ISymbolCanvas canvas, string text, Point at, IBrush brush) =>
        canvas.DrawText(text, at, 0.7 * Unit, brush, SymbolTextAlign.Centre);

    // ───────────────────────────── inline parts ─────────────────────────────

    private static void DrawInline(ISymbolCanvas canvas, BoardPart part)
    {
        var points = part.Holes.Select(h => P(Pos(h))).ToList();
        var up = Up(part.Holes[0]);
        var sign = up ? -1 : 1;
        var (left, right, y) = (points[0].X, points[^1].X, points[0].Y);
        var centre = (left + right) / 2;

        foreach (var p in points) canvas.DrawEllipse(Silver, null, p, 0.16 * Unit, 0.16 * Unit);

        switch (part.Package.Look)
        {
            case BoardLook.To92:
            {
                // The flat face towards the channel, the rounded back away from it.
                var w = (right - left) + (0.9 * Unit);
                var path = new SymbolPath();
                using (var ctx = path.Open())
                {
                    ctx.BeginFigure(new Point(centre - (w / 2), y + (sign * -0.35 * Unit)), true);
                    ctx.LineTo(new Point(centre + (w / 2), y + (sign * -0.35 * Unit)));
                    ctx.ArcTo(new Point(centre - (w / 2), y + (sign * -0.35 * Unit)), new Size(w / 2, 0.9 * Unit), 0, false,
                        up ? SweepDirection.CounterClockwise : SweepDirection.Clockwise);
                    ctx.EndFigure(true);
                }

                canvas.DrawGeometry(Brush("#1F1F21"), Pen("#000000", 0.05), path);
                canvas.DrawText(Fit(canvas, part.Package.Label, w - (0.3 * Unit), 0.48 * Unit), new Point(centre, y + (sign * 0.25 * Unit)), 0.48 * Unit, Brush("#D9D9D9"), SymbolTextAlign.Centre);
                Caption(canvas, part.Part.Name, new Point(centre, y + (sign * 1.75 * Unit)), Ink);
                break;
            }

            case BoardLook.To220:
            {
                var w = (right - left) + (1.2 * Unit);
                var tab = new Rect(new Point(centre - (w / 2), up ? y - (2.0 * Unit) : y + (0.9 * Unit)), new Size(w, 1.1 * Unit));
                canvas.DrawRectangle(Silver, Pen("#8A9097", 0.05), new RoundedRect(tab, 0.15 * Unit));
                canvas.DrawEllipse(Brush("#6D737A"), null, tab.Center, 0.3 * Unit, 0.3 * Unit);
                var body = new Rect(new Point(centre - (w / 2), up ? y - (1.0 * Unit) : y - (0.1 * Unit)), new Size(w, 1.1 * Unit));
                canvas.DrawRectangle(Brush("#1F1F21"), null, new RoundedRect(body, 0.1 * Unit));
                canvas.DrawText(Fit(canvas, part.Package.Label, w - (0.3 * Unit), 0.5 * Unit), body.Center, 0.5 * Unit, Brush("#D9D9D9"), SymbolTextAlign.Centre);
                Caption(canvas, part.Part.Name, new Point(centre, up ? y + (0.85 * Unit) : y - (0.85 * Unit)), Ink);
                break;
            }

            case BoardLook.Trimmer:
            {
                var size = (right - left) + (0.8 * Unit);
                var box = new Rect(new Point(centre - (size / 2), up ? y - size + (0.4 * Unit) : y - (0.4 * Unit)), new Size(size, size));
                canvas.DrawRectangle(Brush("#2D63B8"), Pen("#1D4380", 0.05), new RoundedRect(box, 0.2 * Unit));
                canvas.DrawEllipse(White, null, box.Center, size * 0.32, size * 0.32);
                var turn = part.Part is Potentiometer pot ? pot.EffectivePosition : 0.5;
                var angle = (-135 + (270 * turn)) * Math.PI / 180;
                var (dx, dy) = (Math.Sin(angle) * size * 0.28, -Math.Cos(angle) * size * 0.28);
                canvas.DrawLine(Pen("#2D63B8", 0.18), new Point(box.Center.X - dx, box.Center.Y - dy), new Point(box.Center.X + dx, box.Center.Y + dy));
                Caption(canvas, part.Part.Name, new Point(centre, up ? box.Top - (0.5 * Unit) : box.Bottom + (0.5 * Unit)), Ink);
                break;
            }

            case BoardLook.SlideSwitch:
            {
                var w = (right - left) + (1.0 * Unit);
                var box = new Rect(new Point(centre - (w / 2), up ? y - (1.6 * Unit) : y + (0.3 * Unit)), new Size(w, 1.3 * Unit));
                canvas.DrawRectangle(Brush("#2B2B2E"), null, new RoundedRect(box, 0.15 * Unit));
                var toA = part.Part is SpdtSwitch spdt && spdt.ValueLabel.EndsWith('A');
                var knob = new Rect(new Point(toA ? box.Left + (0.3 * Unit) : box.Center.X, box.Top + (0.3 * Unit)), new Size((w / 2) - (0.3 * Unit), box.Height - (0.6 * Unit)));
                canvas.FillRectangle(Brush("#D0D0D0"), knob);
                Caption(canvas, part.Part.Name, new Point(centre, up ? box.Top - (0.5 * Unit) : box.Bottom + (0.5 * Unit)), Ink);
                break;
            }

            default:
            {
                // A breakout module: its board standing on a header, the pins labelled.
                var w = (right - left) + (1.0 * Unit);
                var depth = 2.6 * Unit;
                var box = new Rect(new Point(centre - (w / 2), up ? y - depth : y - (0.6 * Unit)), new Size(w, depth + (0.6 * Unit)));
                canvas.DrawRectangle(Shadow, null, new RoundedRect(box.Translate(new Vector(0.25 * Unit, 0.3 * Unit)), 0.3 * Unit));
                canvas.DrawRectangle(Brush("#1E5AA8"), Pen("#143E75", 0.05), new RoundedRect(box, 0.3 * Unit));
                for (var i = 0; i < points.Count; i++)
                {
                    canvas.FillRectangle(Brush("#1A1A1A"), new Rect(points[i] - new Point(0.42 * Unit, 0.42 * Unit), new Size(0.84 * Unit, 0.84 * Unit)));
                    canvas.DrawEllipse(Brush("#D4AF37"), null, points[i], 0.2 * Unit, 0.2 * Unit);
                    if (part.Package.Pins[i] is { } pin)
                        canvas.DrawText(Fit(canvas, pin.Name, 1.0 * Unit, 0.34 * Unit), new Point(points[i].X, points[i].Y + (sign * 0.75 * Unit)), 0.34 * Unit, White, SymbolTextAlign.Centre);
                }

                var labelY = up ? box.Top + (0.65 * Unit) : box.Bottom - (0.65 * Unit);
                canvas.DrawText(Fit(canvas, $"{part.Part.Name} {part.Package.Label}", w - (0.4 * Unit), 0.55 * Unit), new Point(centre, labelY), 0.55 * Unit, White, SymbolTextAlign.Centre);
                break;
            }
        }
    }

    // ───────────────────────────── two-lead parts ─────────────────────────────

    /// <summary>How big a two-lead part's body is, in pitches, across its middle.</summary>
    private static double Radius(BoardPart part) => BoardPackages.BodyRadius(part.Part, part.Package.Look);

    private static void DrawTwoLead(ISymbolCanvas canvas, BoardPart part)
    {
        var (a, b) = (P(Pos(part.Holes[0])), P(Pos(part.Holes[1])));
        var (dx, dy) = (b.X - a.X, b.Y - a.Y);
        var length = Math.Sqrt((dx * dx) + (dy * dy));
        var angle = Math.Atan2(dy, dx);
        var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        var look = part.Package.Look;
        var axial = BoardPackages.IsAxial(look);

        // An axial part too short to lie flat stands on end over its first hole, its other lead bent down beside it.
        var standing = axial && length < 2.6 * Unit;
        var bodyLength = standing ? 0 : Math.Min(length - (0.9 * Unit), look == BoardLook.Crystal ? 2.0 * Unit : 3.2 * Unit);

        canvas.DrawLine(LeadShade, a, b);
        canvas.DrawLine(Lead, a, b);
        canvas.DrawEllipse(Silver, null, a, 0.17 * Unit, 0.17 * Unit);
        canvas.DrawEllipse(Silver, null, b, 0.17 * Unit, 0.17 * Unit);

        var caption = new Point(mid.X, mid.Y + ((Radius(part) + 0.75) * Unit * (Math.Abs(Math.Cos(angle)) > 0.5 ? 1 : 0)));
        if (Math.Abs(Math.Cos(angle)) <= 0.5) caption = new Point(mid.X + ((Radius(part) + 1.1) * Unit), mid.Y);

        if (standing)
        {
            DrawAxialEnd(canvas, part, a);
            Caption(canvas, part.Part.Name, new Point(a.X, a.Y + (1.2 * Unit)), Ink);
            return;
        }

        using (canvas.PushTransform(Matrix.CreateRotation(angle) * Matrix.CreateTranslation(mid.X, mid.Y)))
        {
            switch (look)
            {
                case BoardLook.Resistor: DrawResistor(canvas, part.Part, bodyLength); break;
                case BoardLook.Diode: DrawDiode(canvas, part, bodyLength); break;
                case BoardLook.Inductor: Capsule(canvas, bodyLength, 0.8, "#4E7F4A", ["#C9CDD2", "#C9CDD2"]); break;
                case BoardLook.Ferrite: Capsule(canvas, Math.Min(bodyLength, 1.8 * Unit), 0.85, "#3A3A3C", []); break;
                case BoardLook.Fuse: DrawFuse(canvas, bodyLength); break;
                case BoardLook.Crystal:
                    canvas.DrawRectangle(Silver, Pen("#8A9097", 0.05), new RoundedRect(new Rect(-bodyLength / 2, -0.45 * Unit, bodyLength, 0.9 * Unit), 0.45 * Unit));
                    break;
                default: break;
            }
        }

        switch (look)
        {
            case BoardLook.Ceramic:
                canvas.DrawEllipse(Brush("#D99A2B"), Pen("#9C6A14", 0.05), mid, 0.7 * Unit, 0.7 * Unit);
                canvas.DrawText(CapCode(part.Part), mid, 0.42 * Unit, Ink, SymbolTextAlign.Centre);
                break;
            case BoardLook.Disc:
                canvas.DrawEllipse(Brush(part.Part is ResettableFuse ? "#E6C229" : "#2F5EA8"), null, mid, 0.85 * Unit, 0.85 * Unit);
                break;
            case BoardLook.Electrolytic: DrawElectrolytic(canvas, part, mid, angle); break;
            case BoardLook.Led: DrawLed(canvas, part, mid, angle); break;
            case BoardLook.Button: DrawButton(canvas, part.Part, mid); break;
            case BoardLook.Switch:
                canvas.DrawRectangle(Brush("#2B2B2E"), null, new RoundedRect(new Rect(mid.X - (0.8 * Unit), mid.Y - (0.5 * Unit), 1.6 * Unit, 1.0 * Unit), 0.15 * Unit));
                var closed = part.Part is ToggleSwitch { IsClosed: true };
                canvas.FillRectangle(Brush("#D0D0D0"), new Rect(mid.X + (closed ? 0 : -0.6 * Unit), mid.Y - (0.3 * Unit), 0.6 * Unit, 0.6 * Unit));
                break;
            case BoardLook.Ldr:
                canvas.DrawEllipse(Brush("#F1EEE6"), Pen("#9C9686", 0.05), mid, 0.65 * Unit, 0.65 * Unit);
                canvas.DrawGeometry(null, Pen("#C2571A", 0.1), SymbolPath.Polyline(
                    [.. Enumerable.Range(0, 7).Select(i => new Point(mid.X - (0.4 * Unit) + (i * 0.133 * Unit), mid.Y + ((i % 2 == 0 ? -0.25 : 0.25) * Unit)))], false));
                break;
            case BoardLook.Thermistor:
                canvas.DrawEllipse(Brush("#2D3B2A"), null, mid, 0.5 * Unit, 0.5 * Unit);
                break;
            case BoardLook.Buzzer:
                canvas.DrawEllipse(Brush("#1C1C1E"), Pen("#000000", 0.05), mid, 1.25 * Unit, 1.25 * Unit);
                canvas.DrawEllipse(Brush("#3A3A3D"), null, mid, 0.3 * Unit, 0.3 * Unit);
                canvas.DrawText("+", new Point(mid.X - (0.7 * Unit), mid.Y - (0.6 * Unit)), 0.6 * Unit, White, SymbolTextAlign.Centre);
                break;
            case BoardLook.Microphone:
                canvas.DrawEllipse(Silver, null, mid, 0.95 * Unit, 0.95 * Unit);
                canvas.DrawEllipse(Brush("#232323"), null, mid, 0.7 * Unit, 0.7 * Unit);
                break;
            default: break;
        }

        Caption(canvas, part.Part.Name, caption, Ink);
    }

    private static void Capsule(ISymbolCanvas canvas, double length, double thickness, string fill, IReadOnlyList<string> bands)
    {
        var body = new Rect(-length / 2, -thickness / 2 * Unit, length, thickness * Unit);
        canvas.DrawRectangle(Brush(fill), Pen(Darker(Color.Parse(fill), 0.35), 0.05), new RoundedRect(body, thickness / 2 * Unit));
        for (var i = 0; i < bands.Count; i++)
        {
            var x = (-length / 2) + (length * (i + 1) / (bands.Count + 1));
            canvas.FillRectangle(Brush(bands[i]), new Rect(x - (0.12 * Unit), body.Top + (0.03 * Unit), 0.24 * Unit, body.Height - (0.06 * Unit)));
        }
    }

    private static string BandHex(BandColour band) => band switch
    {
        BandColour.Black => "#1A1A1A",
        BandColour.Brown => "#7B4A1E",
        BandColour.Red => "#D12D26",
        BandColour.Orange => "#EF7D1A",
        BandColour.Yellow => "#F2D21B",
        BandColour.Green => "#2E9E4F",
        BandColour.Blue => "#2F62D0",
        BandColour.Violet => "#8E44AD",
        BandColour.Grey => "#8E9196",
        BandColour.White => "#F6F6F6",
        BandColour.Gold => "#C9A33A",
        BandColour.Silver => "#BFC4CA",
        _ => "#00000000",
    };

    /// <summary>A resistor with its real colour bands: beige for a 5 % carbon film, blue for a 1 % metal film.</summary>
    private static void DrawResistor(ISymbolCanvas canvas, CircuitComponent part, double length)
    {
        var resistance = part is Resistor r ? r.Resistance : 0;
        var tolerance = part is Resistor t ? t.Tolerance : 0.05;
        var code = ColourCodes.For(resistance, tolerance);
        var precise = tolerance is > 0 and <= 0.02;
        var fill = precise ? "#7FB0DD" : "#E3CC9C";
        var body = new Rect(-length / 2, -0.42 * Unit, length, 0.84 * Unit);
        canvas.DrawRectangle(Brush(fill), Pen(Darker(Color.Parse(fill), 0.35), 0.05), new RoundedRect(body, 0.4 * Unit));
        if (code is null) return;

        var bands = code.Bands;
        var step = length * 0.13;
        var x = (-length / 2) + (length * 0.2);
        for (var i = 0; i < bands.Count; i++)
        {
            var last = i == bands.Count - 1 && bands.Count >= 4;
            var at = last ? (length / 2) - (length * 0.18) : x + (i * step);
            canvas.FillRectangle(Brush(BandHex(bands[i].Colour)), new Rect(at - (0.11 * Unit), body.Top + (0.02 * Unit), 0.22 * Unit, body.Height - (0.04 * Unit)));
        }
    }

    private static void DrawDiode(ISymbolCanvas canvas, BoardPart part, double length)
    {
        var label = part.Package.Label;
        var glass = label.Contains("4148", StringComparison.Ordinal) || label.Contains("Zener", StringComparison.OrdinalIgnoreCase)
                    || part.Part.GetType().Name == "Varactor";
        var l = Math.Min(length, 2.6 * Unit);
        var body = new Rect(-l / 2, -0.38 * Unit, l, 0.76 * Unit);
        canvas.DrawRectangle(Brush(glass ? "#E8A04A" : "#1E1E1E"), Pen(glass ? "#A86A1E" : "#000000", 0.05), new RoundedRect(body, 0.3 * Unit));

        // The band marks the cathode, the second lead.
        canvas.FillRectangle(Brush(glass ? "#1E1E1E" : "#C9CDD2"), new Rect((l / 2) - (0.62 * Unit), body.Top + (0.02 * Unit), 0.26 * Unit, body.Height - (0.04 * Unit)));
    }

    /// <summary>An axial part standing on end: its body seen from above, a ring.</summary>
    private static void DrawAxialEnd(ISymbolCanvas canvas, BoardPart part, Point at)
    {
        var fill = part.Package.Look switch
        {
            BoardLook.Resistor => part.Part is Resistor { Tolerance: > 0 and <= 0.02 } ? "#7FB0DD" : "#E3CC9C",
            BoardLook.Diode => "#1E1E1E",
            BoardLook.Inductor => "#4E7F4A",
            BoardLook.Crystal => "#C9CDD2",
            _ => "#B0B0B0",
        };

        canvas.DrawEllipse(Brush(fill), Pen(Darker(Color.Parse(fill), 0.35), 0.05), at, 0.48 * Unit, 0.48 * Unit);
    }

    private static void DrawFuse(ISymbolCanvas canvas, double length)
    {
        var body = new Rect(-length / 2, -0.4 * Unit, length, 0.8 * Unit);
        canvas.DrawRectangle(Brush("#60D6E4F0"), Pen("#8FA7BA", 0.05), new RoundedRect(body, 0.2 * Unit));
        canvas.FillRectangle(Silver, new Rect(body.Left, body.Top, 0.5 * Unit, body.Height));
        canvas.FillRectangle(Silver, new Rect(body.Right - (0.5 * Unit), body.Top, 0.5 * Unit, body.Height));
        canvas.DrawLine(Pen("#7D838A", 0.05), new Point(body.Left + (0.5 * Unit), 0), new Point(body.Right - (0.5 * Unit), 0));
    }

    /// <summary>The usual three-digit code printed on a ceramic: 104 for 100 nF.</summary>
    private static string CapCode(CircuitComponent part)
    {
        if (part is not Capacitor c || c.Capacitance <= 0) return "";
        var picofarads = c.Capacitance * 1e12;
        if (picofarads < 10) return Math.Round(picofarads).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var exponent = (int)Math.Floor(Math.Log10(picofarads)) - 1;
        var digits = (int)Math.Round(picofarads / Math.Pow(10, exponent));
        if (digits >= 100) { digits /= 10; exponent++; }
        return exponent is >= 0 and <= 9 ? $"{digits}{exponent}" : "";
    }

    /// <summary>A radial electrolytic seen from the top: its sleeve, the vent, and the stripe on the − side.</summary>
    private static void DrawElectrolytic(ISymbolCanvas canvas, BoardPart part, Point mid, double angle)
    {
        var r = Radius(part) * Unit;
        canvas.DrawEllipse(Shadow, null, new Point(mid.X + (0.25 * Unit), mid.Y + (0.3 * Unit)), r, r);
        canvas.DrawEllipse(Brush("#1F3D7A"), Pen("#12254B", 0.05), mid, r, r);

        // The stripe faces the second (−) lead.
        var (cx, cy) = (Math.Cos(angle), Math.Sin(angle));
        var stripe = new Point(mid.X + (cx * r * 0.7), mid.Y + (cy * r * 0.7));
        canvas.DrawLine(Pen("#C7D3EA", 0.28), new Point(stripe.X - (cy * r * 0.6), stripe.Y + (cx * r * 0.6)), new Point(stripe.X + (cy * r * 0.6), stripe.Y - (cx * r * 0.6)));
        canvas.DrawEllipse(Silver, null, mid, r * 0.62, r * 0.62);
        canvas.DrawLine(Pen("#8A9097", 0.06), new Point(mid.X - (r * 0.4), mid.Y), new Point(mid.X + (r * 0.4), mid.Y));
        canvas.DrawLine(Pen("#8A9097", 0.06), new Point(mid.X, mid.Y - (r * 0.4)), new Point(mid.X, mid.Y + (r * 0.4)));
    }

    /// <summary>A 5 mm LED's dome: its colour faint when it's off, bright with a glow round it when the simulation lights it.</summary>
    private static void DrawLed(ISymbolCanvas canvas, BoardPart part, Point mid, double angle)
    {
        var colour = part.Part is Led led ? Of(led.EmittedColor) : Color.Parse("#202020");
        var dark = part.Part is not Led;
        var lit = part.Part is Led on ? CanvasTheme.Emission(on.Brightness) : 0;
        if (lit > 0)
        {
            canvas.DrawEllipse(Brush(Color.FromArgb((byte)(70 * lit), colour.R, colour.G, colour.B)), null, mid, 2.8 * Unit, 2.8 * Unit);
            canvas.DrawEllipse(Brush(Color.FromArgb((byte)(120 * lit), colour.R, colour.G, colour.B)), null, mid, 1.7 * Unit, 1.7 * Unit);
        }

        var body = dark ? colour : Mix(Mix(colour, Color.Parse("#F4F1EA"), 0.45), colour, lit);
        canvas.DrawEllipse(Brush(body), Pen(Darker(dark ? Color.Parse("#404040") : colour, 0.3), 0.06), mid, 0.62 * Unit, 0.62 * Unit);
        canvas.DrawEllipse(Brush(lit > 0 ? "#E6FFFFFF" : "#90FFFFFF"), null, new Point(mid.X - (0.2 * Unit), mid.Y - (0.2 * Unit)), 0.17 * Unit, 0.17 * Unit);

        // The flat on the rim, on the cathode's side.
        var (cx, cy) = (Math.Cos(angle), Math.Sin(angle));
        var flat = new Point(mid.X + (cx * 0.6 * Unit), mid.Y + (cy * 0.6 * Unit));
        canvas.DrawLine(Pen(Darker(dark ? Color.Parse("#404040") : colour, 0.4), 0.08), new Point(flat.X - (cy * 0.35 * Unit), flat.Y + (cx * 0.35 * Unit)), new Point(flat.X + (cy * 0.35 * Unit), flat.Y - (cx * 0.35 * Unit)));
    }

    private static void DrawButton(ISymbolCanvas canvas, CircuitComponent part, Point mid)
    {
        var box = new Rect(mid.X - (0.9 * Unit), mid.Y - (0.9 * Unit), 1.8 * Unit, 1.8 * Unit);
        canvas.DrawRectangle(Brush("#2A2A2C"), Pen("#111111", 0.05), new RoundedRect(box, 0.15 * Unit));
        var pressed = part is PushButton { IsPressed: true };
        canvas.DrawEllipse(Brush(pressed ? "#5A5A5E" : "#8B8B90"), Pen("#1C1C1C", 0.05), mid, 0.55 * Unit, 0.55 * Unit);
    }

    // ───────────────────────────── the bench ─────────────────────────────

    private static void DrawLead(ISymbolCanvas canvas, BenchLead lead)
    {
        var colour = WireColour(lead.Role, lead.Colour);
        var path = Along(lead.Route);
        canvas.DrawGeometry(null, Pen("#33000000", 0.5), Shifted(path));
        canvas.DrawGeometry(null, Pen(Darker(colour, 0.45), 0.42), path);
        canvas.DrawGeometry(null, Pen(colour, 0.3), path);
        canvas.DrawEllipse(Silver, Pen("#7D838A", 0.05), P(Pos(lead.To)), 0.2 * Unit, 0.2 * Unit);
    }

    private static void DrawBench(ISymbolCanvas canvas, BenchPart item)
    {
        var box = new Rect(P(item.X, item.Y), P(item.X + item.Width, item.Y + item.Height));
        var (fill, edge, text) = item.Package.Look switch
        {
            BoardLook.Supply => ("#4F565C", "#2E3337", "#FFFFFF"),
            BoardLook.Generator => ("#2E4D6B", "#1C3146", "#FFFFFF"),
            BoardLook.DevBoard => (DevBoardColour(item.Package.Label), "#0E2A2C", "#FFFFFF"),
            BoardLook.Display => ("#1F6E43", "#124229", "#FFFFFF"),
            BoardLook.Keypad => ("#202022", "#000000", "#FFFFFF"),
            BoardLook.Motor or BoardLook.Transformer => ("#9DA3A9", "#6C7278", "#1E1E1E"),
            BoardLook.Speaker => ("#28282B", "#000000", "#FFFFFF"),
            BoardLook.Lamp => ("#EEE6CF", "#B9AE8C", "#2B2B2B"),
            BoardLook.LedStrip => ("#1C1C1C", "#000000", "#FFFFFF"),
            BoardLook.Sensor => ("#1E5AA8", "#143E75", "#FFFFFF"),
            _ => ("#3D3D46", "#25252B", "#FFFFFF"),
        };

        canvas.DrawRectangle(Shadow, null, new RoundedRect(box.Translate(new Vector(0.35 * Unit, 0.45 * Unit)), 0.8 * Unit));
        canvas.DrawRectangle(Brush(fill), Pen(edge, 0.08), new RoundedRect(box, 0.8 * Unit));
        var ink = Brush(text);
        canvas.DrawText(Fit(canvas, item.Part.Name, box.Width - (1.2 * Unit), 0.95 * Unit), new Point(box.Left + (0.6 * Unit), box.Top + (1.05 * Unit)), 0.95 * Unit, ink, SymbolTextAlign.Left);

        // A face for what it is: the supply's reading, the LCD's lines, the strip's pixels, the lamp's glow.
        var face = new Rect(box.Left + (0.6 * Unit), box.Top + (1.9 * Unit), box.Width - (5.2 * Unit), Math.Min(2.2 * Unit, box.Height - (2.6 * Unit)));
        switch (item.Package.Look)
        {
            case BoardLook.Display when item.Part is CharacterLcd lcd:
                var screen = new Rect(face.X, face.Y, face.Width, Math.Max(face.Height, 2.2 * Unit));
                canvas.DrawRectangle(Brush(lcd.DisplayOn ? "#B7D96A" : "#7F9A4A"), null, new RoundedRect(screen, 0.2 * Unit));
                if (lcd.DisplayOn)
                {
                    canvas.DrawText(Fit(canvas, lcd.Line(0).TrimEnd(), screen.Width - (0.4 * Unit), 0.75 * Unit), new Point(screen.Left + (0.2 * Unit), screen.Top + (0.6 * Unit)), 0.75 * Unit, Brush("#1E2B10"), SymbolTextAlign.Left);
                    canvas.DrawText(Fit(canvas, lcd.Line(1).TrimEnd(), screen.Width - (0.4 * Unit), 0.75 * Unit), new Point(screen.Left + (0.2 * Unit), screen.Top + (1.55 * Unit)), 0.75 * Unit, Brush("#1E2B10"), SymbolTextAlign.Left);
                }

                break;
            case BoardLook.LedStrip when item.Part is NeoPixelStrip strip:
                var n = strip.Pixels.Count;
                for (var i = 0; i < n; i++)
                {
                    var c = Of(strip.Pixels[i]);
                    var at = new Point(face.Left + ((i + 0.5) * face.Width / Math.Max(n, 1)), face.Center.Y);
                    canvas.DrawRectangle(Brush("#F0F0F0"), null, new Rect(at.X - (0.45 * Unit), at.Y - (0.45 * Unit), 0.9 * Unit, 0.9 * Unit));
                    canvas.DrawEllipse(Brush(c.R + c.G + c.B > 0 ? c : Color.Parse("#B0B0B0")), null, at, 0.3 * Unit, 0.3 * Unit);
                }

                break;
            case BoardLook.Lamp when item.Part is IncandescentLamp lamp:
                var glow = CanvasTheme.Emission(lamp.Brightness);
                var bulb = new Point(face.Center.X, face.Center.Y + (0.3 * Unit));
                if (glow > 0) canvas.DrawEllipse(Brush(Color.FromArgb((byte)(110 * glow), 255, 200, 90)), null, bulb, 2.2 * Unit, 2.2 * Unit);
                canvas.DrawEllipse(Brush(Mix(Color.Parse("#F7F3E4"), Color.Parse("#FFD35A"), glow)), Pen("#B9AE8C", 0.05), bulb, 1.0 * Unit, 1.0 * Unit);
                break;
            case BoardLook.Keypad:
                for (var row = 0; row < 4; row++)
                    for (var column = 0; column < 4; column++)
                        canvas.DrawRectangle(Brush("#E6E6E6"), null, new RoundedRect(new Rect(face.Left + (column * 1.3 * Unit), box.Top + ((2.0 + (row * 1.3)) * Unit), 1.0 * Unit, 1.0 * Unit), 0.15 * Unit));
                break;
            default:
                var value = item.Package.Label;
                if (value.Length > 0 && value != item.Part.Name)
                {
                    var panel = new Rect(face.X, face.Y, face.Width, Math.Min(face.Height, 1.4 * Unit));
                    if (item.Package.Look is BoardLook.Supply or BoardLook.Generator)
                    {
                        canvas.DrawRectangle(Brush("#10261A"), null, new RoundedRect(panel, 0.15 * Unit));
                        canvas.DrawText(Fit(canvas, value, panel.Width - (0.4 * Unit), 0.75 * Unit), new Point(panel.Left + (0.2 * Unit), panel.Center.Y), 0.75 * Unit, Brush("#62E58A"), SymbolTextAlign.Left);
                    }
                    else
                    {
                        canvas.DrawText(Fit(canvas, value, panel.Width, 0.7 * Unit), new Point(panel.Left, panel.Center.Y), 0.7 * Unit, ink, SymbolTextAlign.Left);
                    }
                }

                break;
        }

        // The terminals down the right edge, each named, where its lead leaves.
        for (var i = 0; i < item.Package.Pins.Count; i++)
        {
            var (x, y) = item.LeadStart(i);
            var post = P(x, y);
            canvas.DrawText(Fit(canvas, item.Package.Pins[i]!.Name, 4.0 * Unit, 0.6 * Unit), new Point(post.X - (0.75 * Unit), post.Y), 0.6 * Unit, ink, SymbolTextAlign.Right);
            canvas.DrawEllipse(Brush("#BFC4CA"), Pen("#5D636A", 0.06), post, 0.36 * Unit, 0.36 * Unit);
        }
    }

    private static string DevBoardColour(string label) =>
        label.Contains("Arduino", StringComparison.OrdinalIgnoreCase) ? "#00838F"
        : label.Contains("ESP32", StringComparison.OrdinalIgnoreCase) ? "#26292E"
        : "#2E7D32";
}
