using Avalonia;
using Avalonia.Media;
using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Core.Topology;

namespace Cirq.UI.Rendering;

/// <summary>
/// The 1.3.0 parts that are boards and displays rather than DIPs: a module body sized to wherever its pins are, each
/// pin named inside it, and for the displays what they're showing.
/// </summary>
public static partial class SymbolRenderer
{
    private static Point At(Terminal terminal) => new(terminal.CanvasOffset.X, terminal.CanvasOffset.Y);

    private static readonly Color OledColour = Color.FromRgb(0x7F, 0xD8, 0xFF);
    private static readonly Color MatrixColour = Color.FromRgb(0xFF, 0x30, 0x20);
    private static readonly Color NeonColour = Color.FromRgb(0xFF, 0x8A, 0x2A);

    /// <summary>The modules the generic DIP or box would draw wrongly, and the least body each one needs.</summary>
    private static Rect? ModuleMinimum(CircuitComponent component) => component switch
    {
        Ssd1306 => new Rect(-70, -68, 140, 84),
        Max7219Matrix => new Rect(-68, -56, 136, 112),
        Tm1637Display => new Rect(-48, -26, 150, 52),
        SdCard => new Rect(-28, -36, 72, 72),
        Ad9833 => new Rect(-28, -40, 56, 92),
        IrReceiver => new Rect(-28, -26, 56, 54),
        DhtSensor => new Rect(-40, -18, 80, 56),
        BuckModule => new Rect(-28, -26, 56, 54),
        BoostModule => new Rect(-28, -26, 56, 54),
        Cirq.Components.Electromechanical.Acs712 => new Rect(-28, -30, 56, 84),
        Hx711 => new Rect(-28, -50, 56, 120),
        Max6675 => new Rect(-28, -40, 56, 80),
        Pca9685 => new Rect(-28, -160, 56, 320),
        Cirq.Components.Electromechanical.Tb6612Module => new Rect(-38, -80, 76, 160),
        Cirq.Components.Electromechanical.RelayModule => new Rect(-38, -32, 76, 64),
        Pcd8544 => new Rect(-80, -48, 160, 96),
        QuadSevenSegment => new Rect(-84, -28, 168, 56),
        L298nModule => null,
        _ => null,
    };

    private static bool IsModule(CircuitComponent component) => ModuleMinimum(component) is not null || component is L298nModule;

    /// <summary>A module's body: its least size, grown to just inside each side's pins.</summary>
    private static Rect ModuleBody(CircuitComponent component)
    {
        var points = component.Terminals.Select(At).ToList();
        var minimum = ModuleMinimum(component) ?? new Rect(-30, -26, 60, 52);
        if (points.Count == 0) return minimum;
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X), minY = points.Min(p => p.Y), maxY = points.Max(p => p.Y);
        double left = minimum.Left, right = minimum.Right, top = minimum.Top, bottom = minimum.Bottom;
        var sides = Sides(points);
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            switch (sides[i])
            {
                case 0: left = Math.Min(left, p.X + 12); top = Math.Min(top, p.Y - 8); bottom = Math.Max(bottom, p.Y + 8); break;
                case 1: right = Math.Max(right, p.X - 12); top = Math.Min(top, p.Y - 8); bottom = Math.Max(bottom, p.Y + 8); break;
                case 2: top = Math.Min(top, p.Y + 12); left = Math.Min(left, p.X - 8); right = Math.Max(right, p.X + 8); break;
                default: bottom = Math.Max(bottom, p.Y - 12); left = Math.Min(left, p.X - 8); right = Math.Max(right, p.X + 8); break;
            }
        }

        // A side with pins on it stops just inside them, whatever the minimum said.
        if (sides.Contains(0)) left = minX + 12;
        if (sides.Contains(1)) right = maxX - 12;
        if (sides.Contains(2)) top = minY + 12;
        if (sides.Contains(3)) bottom = maxY - 12;
        return new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>0 left, 1 right, 2 top, 3 bottom.</summary>
    private static int Side(Point p, double minX, double maxX) =>
        p.X == minX && p.X < -20 ? 0 : p.X == maxX && p.X > 20 ? 1 : p.Y < 0 ? 2 : 3;

    /// <summary>Each pin's side; a module whose pins are all in one row (the OLED's header, the DHT's legs) has them on top or underneath.</summary>
    private static List<int> Sides(List<Point> points)
    {
        if (points.Count == 0) return [];
        if (points.Select(p => p.Y).Distinct().Count() == 1) return [.. points.Select(p => p.Y < 0 ? 2 : 3)];
        double minX = points.Min(p => p.X), maxX = points.Max(p => p.X);
        return [.. points.Select(p => Side(p, minX, maxX))];
    }

    private static double ModuleLabelOffset(CircuitComponent component)
    {
        var body = ModuleBody(component);
        var ys = component.Terminals.Select(t => t.CanvasOffset.Y).DefaultIfEmpty(0).ToList();
        return Math.Max(-Math.Min(body.Top, ys.Min()), Math.Max(body.Bottom, ys.Max())) + 12;
    }

    private static void DrawModule(ISymbolCanvas context, IPen pen, double zoom, CircuitComponent component)
    {
        var body = ModuleBody(component);
        var points = component.Terminals.Select(At).ToList();
        var sides = Sides(points);
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var edge = sides[i] switch
            {
                0 => new Point(body.Left, p.Y),
                1 => new Point(body.Right, p.Y),
                2 => new Point(p.X, body.Top),
                _ => new Point(p.X, body.Bottom),
            };
            context.DrawLine(pen, p, edge);
        }

        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(body, 4));

        if (zoom > 0.45)
        {
            for (var i = 0; i < points.Count; i++)
            {
                var terminal = component.Terminals[i];
                var p = points[i];
                var name = component is DigitalIc ic ? ic.PinLabel(terminal) : terminal.Name;
                switch (sides[i])
                {
                    case 0: DrawAlignedText(context, name, new Point(body.Left + 3, p.Y - 1), 6.5, zoom, CanvasTheme.LabelBrush, alignLeft: true); break;
                    case 1: DrawAlignedText(context, name, new Point(body.Right - 3, p.Y - 1), 6.5, zoom, CanvasTheme.LabelBrush, alignLeft: false); break;
                    case 2: DrawCenteredText(context, name, new Point(p.X, body.Top + 6), 6, zoom, CanvasTheme.LabelBrush); break;
                    default: DrawCenteredText(context, name, new Point(p.X, body.Bottom - 6), 6, zoom, CanvasTheme.LabelBrush); break;
                }
            }
        }

        switch (component)
        {
            case Ssd1306 oled: DrawOledScreen(context, zoom, oled, body); break;
            case Max7219Matrix matrix: DrawMatrix(context, matrix, body); break;
            case Tm1637Display tm: DrawTm1637Digits(context, pen, tm, body); break;
            case Pcd8544 lcd: DrawNokiaScreen(context, zoom, lcd, body); break;
            case QuadSevenSegment quad: DrawQuadDigits(context, pen, quad, body); break;
            default:
                var title = component is DigitalIc chip ? chip.PartNumber : component.ComponentType;
                DrawCenteredText(context, title, TitlePlace(points, sides, body), 8, zoom, CanvasTheme.SymbolBrush);
                break;
        }
    }

    /// <summary>Where a module's name goes: clear of the pin names, below the side pins when there are some on both sides.</summary>
    private static Point TitlePlace(List<Point> points, List<int> sides, Rect body)
    {
        var floor = sides.Contains(3) ? body.Bottom - 12 : body.Bottom;
        var ceiling = sides.Contains(2) ? body.Top + 12 : body.Top;
        var sidePins = points.Where((p, i) => sides[i] is 0 or 1).ToList();
        if (sides.Contains(0) && sides.Contains(1) && body.Width < 80) return new Point(body.Center.X, (sidePins.Max(p => p.Y) + floor) / 2);
        var x = sides.Contains(0) ? body.Center.X + 12 : sides.Contains(1) ? body.Center.X - 12 : body.Center.X;
        return new Point(x, (ceiling + floor) / 2);
    }

    /// <summary>A chip that isn't a <see cref="DigitalIc"/> but sits in a DIP: its pins named from its terminals.</summary>
    private static void DrawNamedDip(ISymbolCanvas context, IPen pen, double zoom, CircuitComponent chip, int pinCount, string partNumber)
    {
        var height = DipPackage.BodyHeight(pinCount);
        var body = new Rect(-DipPackage.HalfWidth + 10, -height / 2, (DipPackage.HalfWidth - 10) * 2, height);
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(body, 3));
        context.DrawGeometry(null, pen, SymbolPath.Polyline([new Point(-6, body.Top), new Point(0, body.Top + 5), new Point(6, body.Top)], false));
        foreach (var terminal in chip.Terminals)
        {
            var at = At(terminal);
            context.DrawLine(pen, at, new Point(at.X < 0 ? body.Left : body.Right, at.Y));
            if (zoom > 0.45)
                DrawAlignedText(context, terminal.Name, new Point(at.X < 0 ? body.Left + 3 : body.Right - 3, at.Y - 1), 6.5, zoom,
                    CanvasTheme.LabelBrush, alignLeft: at.X < 0);
        }

        // With an odd number of rows the middle one runs through the centre: the name goes between it and the next.
        DrawCenteredText(context, partNumber, new Point(0, pinCount / 2 % 2 == 1 ? 10 : 0), 8, zoom, CanvasTheme.SymbolBrush);
    }

    private static (int Pins, string Name)? NamedDip(CircuitComponent component) => component switch
    {
        L293d => (16, "L293D"),
        Ne556 => (14, "NE556"),
        Lm3914 => (18, "LM3914"),
        Ne567 => (8, "NE567"),
        Xr2206 => (16, "XR2206"),
        DualOpAmp dual => (8, dual.Model.Name),
        Lm13700 => (16, "LM13700"),
        Cirq.Components.Digital.Dac0808 => (16, "DAC0808"),
        Icl7107 => (40, "ICL7107"),
        Msgeq7 => (8, "MSGEQ7"),
        Ne602 => (8, "NE602"),
        Mc1496 => (14, "MC1496"),
        LedBarGraph => (20, "BAR GRAPH"),
        Mcp23017 => (28, "MCP23017"),
        _ => null,
    };

    /// <summary>The Nokia's 84 × 48 glass, its pixels dark on grey-green.</summary>
    private static void DrawNokiaScreen(ISymbolCanvas context, double zoom, Pcd8544 lcd, Rect body)
    {
        var glass = new Rect(-42 * 1.5, body.Top + 16, 84 * 1.5, 48 * 1.5);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0xA8, 0xBF, 0x8E)), glass);
        if (lcd.PoweredDown)
        {
            DrawCenteredText(context, "PCD8544", glass.Center, 8, zoom, new SolidColorBrush(Color.FromRgb(0x6B, 0x80, 0x55)));
            return;
        }

        var ink = new SolidColorBrush(Color.FromRgb(0x1C, 0x24, 0x18));
        for (var y = 0; y < 48; y++)
            for (var x = 0; x < 84; x++)
                if (lcd.Pixel(x, y)) context.FillRectangle(ink, new Rect(glass.Left + (x * 1.5), glass.Top + (y * 1.5), 1.5, 1.5));
    }

    /// <summary>The multiplexed display's four digits, each segment as bright as the eye's average says.</summary>
    private static void DrawQuadDigits(ISymbolCanvas context, IPen pen, QuadSevenSegment quad, Rect body)
    {
        var window = new Rect(body.Left + 8, body.Top + 10, body.Width - 16, body.Height - 20);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x08, 0x08)), window);
        var thickness = Math.Max(pen.Thickness * 1.4, 2.2);
        for (var d = 0; d < 4; d++)
        {
            var cx = window.Left + 18 + (d * (window.Width - 24) / 4);
            var (w, h, top) = (10.0, 13.0, window.Center.Y - 13);
            Point A(double x, double y) => new(cx + x, top + y);
            (Point, Point)[] lines =
            [
                (A(0, 0), A(w, 0)), (A(w, 0), A(w, h)), (A(w, h), A(w, 2 * h)), (A(0, 2 * h), A(w, 2 * h)),
                (A(0, h), A(0, 2 * h)), (A(0, 0), A(0, h)), (A(0, h), A(w, h)),
            ];
            for (var s = 0; s < 7; s++)
            {
                var glow = quad.Glow(d, s);
                context.DrawLine(new Pen(Wash(MatrixColour, 0.1 + (0.9 * Math.Clamp(glow * 2, 0, 1))), thickness, lineCap: PenLineCap.Round), lines[s].Item1, lines[s].Item2);
            }

            context.DrawEllipse(Wash(MatrixColour, 0.1 + (0.9 * Math.Clamp(quad.Glow(d, 7) * 2, 0, 1))), null, A(w + 4, 2 * h), 1.6, 1.6);
        }
    }

    /// <summary>A unijunction: the bar between its bases, the emitter's arrow slanting into it.</summary>
    private static void DrawUjt(ISymbolCanvas context, IPen pen, bool programmable)
    {
        if (programmable)
        {
            // A PUT: a diode anode to cathode, the gate off the anode side.
            context.DrawLine(pen, new Point(0, -40), new Point(0, -10));
            context.DrawGeometry(CanvasTheme.SymbolFill, pen, SymbolPath.Polyline([new Point(-11, -10), new Point(11, -10), new Point(0, 9)], true));
            context.DrawLine(pen, new Point(-11, 9), new Point(11, 9));
            context.DrawLine(pen, new Point(0, 9), new Point(0, 40));
            context.DrawLine(pen, new Point(-40, -10), new Point(-18, -10));
            context.DrawLine(pen, new Point(-18, -10), new Point(-6, -2));
            return;
        }

        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(4, 0), 24, 24);
        context.DrawLine(pen, new Point(10, -16), new Point(10, 16));
        context.DrawLine(pen, new Point(10, -14), new Point(10, -40));
        context.DrawLine(pen, new Point(10, 14), new Point(10, 40));
        context.DrawLine(pen, new Point(-40, 10), new Point(10, -2));
        context.DrawGeometry(pen.Brush, pen, SymbolPath.Polyline([new Point(10, -2), new Point(0, -4), new Point(3, 4)], true));
    }

    /// <summary>A pentode: the plate, three grids (the screen and suppressor's grid lines), the cathode, in the envelope.</summary>
    private static void DrawPentode(ISymbolCanvas context, IPen pen)
    {
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 32, 32);
        context.DrawLine(pen, new Point(0, -50), new Point(0, -20));
        context.DrawLine(pen, new Point(-14, -20), new Point(14, -20));
        var dashed = new Pen(pen.Brush, pen.Thickness, new DashStyle([2, 2], 0));
        foreach (var y in new[] { -10.0, 0.0, 10.0 }) context.DrawLine(dashed, new Point(-16, y), new Point(16, y));
        context.DrawLine(pen, new Point(-40, 10), new Point(-16, 10));
        context.DrawLine(pen, new Point(16, 0), new Point(28, 0));
        context.DrawLine(pen, new Point(28, 0), new Point(40, -20));
        context.DrawGeometry(null, pen, SymbolPath.Polyline([new Point(-14, 24), new Point(-14, 20), new Point(14, 20), new Point(14, 24)], false));
        context.DrawLine(pen, new Point(0, 20), new Point(0, 50));
    }

    /// <summary>A rectifier valve: two plates and the filament between them.</summary>
    private static void DrawValveRectifier(ISymbolCanvas context, IPen pen)
    {
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 26, 26);
        foreach (var y in new[] { -20.0, 20.0 })
        {
            context.DrawLine(pen, new Point(-40, y), new Point(-12, y));
            context.DrawLine(pen, new Point(-12, y - 8), new Point(-12, y + 8));
        }

        context.DrawGeometry(null, pen, SymbolPath.Polyline([new Point(10, -12), new Point(4, 0), new Point(10, 12)], false));
        context.DrawLine(pen, new Point(10, 0), new Point(40, 0));
    }

    /// <summary>The OLED's 128 × 64 glass, a unit to a pixel, each row's lit runs drawn as one bar.</summary>
    private static void DrawOledScreen(ISymbolCanvas context, double zoom, Ssd1306 oled, Rect body)
    {
        var glass = new Rect(-64, body.Top + 14, 128, 64);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x08, 0x0C, 0x14)), glass);
        if (!oled.DisplayOn)
        {
            DrawCenteredText(context, "SSD1306", glass.Center, 8, zoom, Wash(OledColour, 0.5));
            return;
        }

        var lit = new SolidColorBrush(OledColour);
        for (var y = 0; y < 64; y++)
        {
            var start = -1;
            for (var x = 0; x <= 128; x++)
            {
                var on = x < 128 && oled.Pixel(x, y);
                if (on && start < 0) start = x;
                else if (!on && start >= 0)
                {
                    context.FillRectangle(lit, new Rect(glass.Left + start, glass.Top + y, x - start, 1));
                    start = -1;
                }
            }
        }
    }

    private static void DrawMatrix(ISymbolCanvas context, Max7219Matrix matrix, Rect body)
    {
        var dark = Wash(MatrixColour, 0.12);
        var glow = Wash(MatrixColour, 0.3);
        var lit = new SolidColorBrush(MatrixColour);
        var centre = body.Center;
        for (var row = 0; row < 8; row++)
            for (var column = 0; column < 8; column++)
            {
                var at = new Point(centre.X + ((column - 3.5) * 12), centre.Y + ((row - 3.5) * 12));
                if (matrix.Lit(row, column))
                {
                    context.DrawEllipse(glow, null, at, 7, 7);
                    context.DrawEllipse(lit, null, at, 4.5, 4.5);
                }
                else context.DrawEllipse(dark, null, at, 4.5, 4.5);
            }
    }

    /// <summary>Four seven-segment digits, a to g as bits 0 to 6, the colon on digit 1's bit 7.</summary>
    private static void DrawTm1637Digits(ISymbolCanvas context, IPen pen, Tm1637Display display, Rect body)
    {
        var window = new Rect(body.Left + 32, body.Top + 6, body.Right - body.Left - 38, body.Height - 12);
        context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x08, 0x08)), window);
        var on = display.On ? Wash(MatrixColour, 0.35 + (0.65 * (display.Brightness + 1) / 8.0)) : null;
        var off = Wash(MatrixColour, 0.1);
        var thickness = Math.Max(pen.Thickness * 1.4, 2.2);
        for (var d = 0; d < 4; d++)
        {
            var segments = display.On ? display.Segments(d) : 0;
            var cx = window.Left + 14 + (d * ((window.Width - 20) / 4)) + (d >= 2 ? 6 : 0);
            var (w, h, top) = (9.0, 13.0, window.Center.Y - 13);
            Point A(double x, double y) => new(cx + x, top + y);
            (Point, Point)[] lines =
            [
                (A(0, 0), A(w, 0)), (A(w, 0), A(w, h)), (A(w, h), A(w, 2 * h)), (A(0, 2 * h), A(w, 2 * h)),
                (A(0, h), A(0, 2 * h)), (A(0, 0), A(0, h)), (A(0, h), A(w, h)),
            ];
            for (var s = 0; s < 7; s++)
            {
                var brush = (segments & (1 << s)) != 0 && on is not null ? on : off;
                context.DrawLine(new Pen(brush, thickness, lineCap: PenLineCap.Round), lines[s].Item1, lines[s].Item2);
            }

            if (d == 1)
            {
                var colon = (segments & 0x80) != 0 && on is not null ? on : off;
                context.DrawEllipse(colon, null, new Point(cx + w + 6, top + (h * 0.6)), 1.6, 1.6);
                context.DrawEllipse(colon, null, new Point(cx + w + 6, top + (h * 1.4)), 1.6, 1.6);
            }
        }
    }

    /// <summary>An RGB or bicolour LED: a round lens lit in the mix of its dice, with the dice's leads.</summary>
    private static void DrawMultiLed(ISymbolCanvas context, IPen pen, double zoom, MultiLed led)
    {
        foreach (var terminal in led.Terminals)
        {
            var p = At(terminal);
            var length = Math.Sqrt((p.X * p.X) + (p.Y * p.Y));
            context.DrawLine(pen, p, length > 14 ? new Point(p.X * 14 / length, p.Y * 14 / length) : p);
        }

        var (r, g, b, brightness) = led.Mix();
        var lit = CanvasTheme.Emission(brightness);
        var top = Math.Max(r, Math.Max(g, b));
        var colour = top > 0 ? Color.FromRgb((byte)(255 * r / top), (byte)(255 * g / top), (byte)(255 * b / top)) : SymbolColour;
        if (lit > 0)
        {
            context.DrawEllipse(Wash(colour, 0.18 * lit), null, new Point(0, 0), 30, 30);
            context.DrawEllipse(Wash(colour, 0.34 * lit), null, new Point(0, 0), 20, 20);
        }

        context.DrawEllipse(new SolidColorBrush(Blend(Color.FromRgb(0xF2, 0xF2, 0xF2), colour, lit)), pen, new Point(0, 0), 14, 14);
        DrawEmissionArrows(context, lit > 0 ? new Pen(new SolidColorBrush(colour), pen.Thickness + 0.5) : pen);
        if (zoom > 0.45)
            foreach (var terminal in led.Terminals)
            {
                var p = At(terminal);
                DrawCenteredText(context, terminal.Name, new Point(p.X * 0.62, (p.Y * 0.62) - 6), 6, zoom, CanvasTheme.LabelBrush);
            }
    }

    /// <summary>A Nixie: the glass, the mesh anode, and the lit digit in neon over the stack of unlit ones.</summary>
    private static void DrawNixie(ISymbolCanvas context, IPen pen, double zoom, NixieTube tube)
    {
        var glass = new Rect(-50, -48, 100, 96);
        context.DrawLine(pen, new Point(0, -60), new Point(0, glass.Top));
        for (var d = 0; d < 10; d++)
        {
            var p = At(tube.Cathode(d));
            context.DrawLine(pen, p, new Point(p.X, glass.Bottom));
        }

        context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1A, 0x12, 0x0E)), pen, new RoundedRect(glass, 22));
        var mesh = new Pen(Wash(SymbolColour, 0.25), 0.6);
        for (var x = -40; x <= 40; x += 8) context.DrawLine(mesh, new Point(x, -40), new Point(x, 30));
        DrawCenteredText(context, "8", new Point(0, -6), 44, 1, Wash(NeonColour, 0.12));
        if (tube.Digit is { } digit)
        {
            var glow = Math.Max(tube.Glow(digit), 0.35);
            context.DrawEllipse(Wash(NeonColour, 0.2 * glow), null, new Point(0, -6), 34, 40);
            DrawCenteredText(context, digit.ToString(System.Globalization.CultureInfo.InvariantCulture), new Point(0, -6), 44, 1,
                Wash(NeonColour, 0.55 + (0.45 * glow)));
        }

        if (zoom > 0.6)
            for (var d = 0; d < 10; d++)
            {
                var p = At(tube.Cathode(d));
                DrawCenteredText(context, d.ToString(System.Globalization.CultureInfo.InvariantCulture), new Point(p.X, glass.Bottom - 7), 5.5, zoom, Wash(NeonColour, 0.7));
            }
    }

    /// <summary>A triode: the envelope, the plate on top, the grid dashed across the middle and the cathode under it.</summary>
    private static void DrawTriode(ISymbolCanvas context, IPen pen, Triode valve)
    {
        _ = valve;
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 26, 26);
        context.DrawLine(pen, new Point(0, -40), new Point(0, -14));
        context.DrawLine(pen, new Point(-12, -14), new Point(12, -14));
        var dashed = new Pen(pen.Brush, pen.Thickness, new DashStyle([2, 2], 0));
        context.DrawLine(pen, new Point(-40, 0), new Point(-14, 0));
        context.DrawLine(dashed, new Point(-14, 0), new Point(14, 0));
        context.DrawGeometry(null, pen, SymbolPath.Polyline([new Point(-12, 14), new Point(-12, 10), new Point(12, 10), new Point(12, 14)], false));
        context.DrawLine(pen, new Point(0, 14), new Point(0, 40));
        context.DrawLine(pen, new Point(-12, 14), new Point(0, 14));
    }
}
