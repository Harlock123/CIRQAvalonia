using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Cirq.Components.Boards;
using Cirq.Components.Bridges;
using Cirq.Components.Passive;
using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.UI.Rendering;

/// <summary>
/// Draws each component's schematic symbol in its own local coordinate space, with the origin at
/// the component's placement point. The caller supplies the world transform, so symbols never
/// need to know about pan, zoom or rotation.
/// </summary>
public static class SymbolRenderer
{
    public static void Draw(DrawingContext context, CircuitComponent component, double zoom, bool isSelected)
    {
        var stroke = isSelected ? CanvasTheme.SelectionBrush : CanvasTheme.SymbolBrush;
        var pen = CanvasTheme.Pen(stroke, 1.8, zoom);
        var thin = CanvasTheme.Pen(stroke, 1.2, zoom);

        switch (component)
        {
            case Resistor: DrawResistor(context, pen); break;
            case Buzzer buzzer: DrawBuzzer(context, pen, zoom, buzzer); break;
            case ElectrolyticCapacitor e: DrawElectrolytic(context, pen, zoom, e); break;
            case Capacitor: DrawCapacitor(context, pen); break;
            case Inductor: DrawInductor(context, pen); break;
            case Transformer: DrawTransformer(context, pen, thin); break;
            case Potentiometer: DrawPotentiometer(context, pen); break;
            case Ground: DrawGround(context, pen); break;
            case DcVoltageSource: DrawVoltageSource(context, pen, zoom); break;
            case DcCurrentSource: DrawCurrentSource(context, pen); break;
            case FunctionGenerator fg: DrawFunctionGenerator(context, pen, fg); break;
            case Led led: DrawLed(context, pen, led); break;
            case DcMotor motor: DrawMotor(context, pen, zoom, motor); break;
            case LightDependentResistor ldr: DrawLdr(context, pen, zoom, ldr); break;
            case Thermistor thermistor: DrawThermistor(context, pen, zoom, thermistor); break;
            case Relay relay: DrawRelay(context, pen, zoom, relay); break;
            case Fuse fuse: DrawFuse(context, pen, zoom, fuse); break;
            case Optocoupler opto: DrawOptocoupler(context, pen, zoom, opto); break;
            case SiliconControlledRectifier scr: DrawThyristor(context, pen, zoom, scr, scr.IsLatched, true); break;
            case Triac triac: DrawTriac(context, pen, zoom, triac); break;
            case Diac diac: DrawDiac(context, pen, zoom, diac); break;
            case JunctionFet jfet: DrawJfet(context, pen, zoom, jfet); break;
            case ShuntReference shunt: DrawShuntReference(context, pen, zoom, shunt); break;
            case BridgeRectifier: DrawBridgeRectifier(context, pen, zoom); break;
            case Diode: DrawDiode(context, pen); break;
            case SevenSegmentDisplay display: DrawSevenSegment(context, pen, zoom, display); break;
            case BipolarTransistor bjt: DrawBipolar(context, pen, bjt); break;
            case Mosfet mosfet: DrawMosfet(context, pen, mosfet); break;
            case ToggleSwitch toggleSwitch: DrawToggleSwitch(context, pen, toggleSwitch); break;
            case PushButton button: DrawPushButton(context, pen, button); break;
            case SpdtSwitch spdt: DrawSpdtSwitch(context, pen, spdt); break;
            case Comparator: DrawComparator(context, pen, zoom); break;
            case VoltageRegulator reg: DrawRegulator(context, pen, zoom, reg); break;
            case OperationalAmplifier: DrawOpAmp(context, pen, zoom); break;
            case Ne555: DrawDip(context, pen, zoom, 8, "NE555"); break;
            case DigitalIc ic: DrawDip(context, pen, zoom, ic.PinCount, ic.PartNumber); break;
            case LogicGate gate: DrawLogicGate(context, pen, gate); break;
            case LogicToggle toggle: DrawToggle(context, pen, zoom, toggle); break;
            case ClockSource: DrawClock(context, pen); break;
            case DeveloperBoard board: DrawBoard(context, pen, zoom, board); break;
            case AdcBridge: DrawBridge(context, pen, zoom, "A/D"); break;
            case DacBridge: DrawBridge(context, pen, zoom, "D/A"); break;
            default: DrawGenericBox(context, pen, zoom, component); break;
        }
    }

    // ---- passives --------------------------------------------------------

    private static void DrawResistor(DrawingContext context, IPen pen)
    {
        // Leads in to the zigzag body.
        context.DrawLine(pen, new Point(-30, 0), new Point(-18, 0));
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));

        var points = new List<Point> { new(-18, 0) };
        for (var i = 0; i < 6; i++)
        {
            var x = -18 + 3 + i * 6;
            points.Add(new Point(x, i % 2 == 0 ? -8 : 8));
        }
        points.Add(new Point(18, 0));

        context.DrawGeometry(null, pen, new PolylineGeometry(points, false));
    }

    /// <summary>
    /// A polarised capacitor: a straight plate on the positive side against a curved one, with a
    /// "+" so the orientation is readable without tracing the wires. Getting an electrolytic the
    /// wrong way round is the mistake the symbol exists to prevent.
    /// </summary>
    private static void DrawElectrolytic(
        DrawingContext context, IPen pen, double zoom, ElectrolyticCapacitor capacitor)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-4, 0));
        context.DrawLine(pen, new Point(6, 0), new Point(30, 0));

        // Positive plate: straight.
        context.DrawLine(pen, new Point(-4, -12), new Point(-4, 12));

        // Negative plate: the curve, drawn hollow-side towards the positive plate.
        var curve = new StreamGeometry();
        using (var ctx = curve.Open())
        {
            ctx.BeginFigure(new Point(6, -12), false);
            ctx.CubicBezierTo(new Point(12, -6), new Point(12, 6), new Point(6, 12));
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, curve);

        if (zoom > 0.5)
            DrawCenteredText(context, "+", new Point(-13, -13), 10, zoom, CanvasTheme.SymbolBrush);

        // A part that has been reverse-biased or over-volted is called out on the symbol itself,
        // because the schematic is where you would fix it.
        if (capacitor.Violations.Count > 0 && zoom > 0.4)
        {
            var warn = CanvasTheme.Pen(CanvasTheme.ErrorBrush, 2.0, zoom);
            context.DrawEllipse(null, warn, new Point(0, 0), 20, 18);
        }
    }

    /// <summary>
    /// A thermistor: the resistor body with the diagonal stroke and a t, the standard marking for
    /// a temperature-dependent part. The stroke warms in colour as the bead does.
    /// </summary>
    private static void DrawThermistor(
        DrawingContext context, IPen pen, double zoom, Thermistor thermistor)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-16, 0));
        context.DrawLine(pen, new Point(16, 0), new Point(30, 0));
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new Rect(-16, -8, 32, 16));

        var warm = CanvasTheme.Pen(
            thermistor.IsWarm ? CanvasTheme.SelectionBrush : CanvasTheme.LabelBrush, 1.6, zoom);

        // The diagonal through the body, with the foot that marks it as a thermistor.
        context.DrawLine(warm, new Point(-20, 13), new Point(14, -13));
        context.DrawLine(warm, new Point(-20, 13), new Point(-20, 6));

        if (zoom > 0.5)
        {
            var sign = thermistor.Kind is ThermistorKind.Ntc ? "t°-" : "t°+";
            DrawCenteredText(context, sign, new Point(0, -18), 8, zoom, CanvasTheme.LabelBrush);
        }
    }

    /// <summary>
    /// A buzzer: the conventional bell-shaped sounder, with arcs radiating from it while it is
    /// actually making a noise and a warning ring when it is being driven with something that
    /// will never make it sound.
    /// </summary>
    private static void DrawBuzzer(DrawingContext context, IPen pen, double zoom, Buzzer buzzer)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-14, 0));
        context.DrawLine(pen, new Point(14, 0), new Point(30, 0));

        // The sounder body: a half-round shell on its side.
        var body = new StreamGeometry();
        using (var ctx = body.Open())
        {
            ctx.BeginFigure(new Point(-14, -14), true);
            ctx.LineTo(new Point(-2, -14));
            ctx.ArcTo(new Point(-2, 14), new Size(14, 14), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(-14, 14));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(CanvasTheme.SymbolFill, pen, body);

        if (buzzer.IsSounding && zoom > 0.45)
        {
            var wave = CanvasTheme.Pen(CanvasTheme.ValueBrush, 1.3, zoom);
            foreach (var radius in new[] { 8.0, 14.0 })
            {
                var arc = new StreamGeometry();
                using (var ctx = arc.Open())
                {
                    ctx.BeginFigure(new Point(16, -radius), false);
                    ctx.ArcTo(new Point(16, radius), new Size(radius, radius), 0, false,
                        SweepDirection.Clockwise);
                    ctx.EndFigure(false);
                }
                context.DrawGeometry(null, wave, arc);
            }
        }

        if (buzzer.Violations.Count > 0 && zoom > 0.4)
        {
            var warn = CanvasTheme.Pen(CanvasTheme.ErrorBrush, 2.0, zoom);
            context.DrawEllipse(null, warn, new Point(0, 0), 20, 19);
        }
    }

    /// <summary>
    /// A motor: the conventional circle and M, ringed when it is being stalled.
    /// </summary>
    private static void DrawMotor(DrawingContext context, IPen pen, double zoom, DcMotor motor)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-18, 0));
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 18, 18);

        DrawCenteredText(context, "M", new Point(0, 0), 13, zoom, CanvasTheme.SymbolBrush);

        if (motor.Violations.Count > 0 && zoom > 0.4)
        {
            var warn = CanvasTheme.Pen(CanvasTheme.ErrorBrush, 2.0, zoom);
            context.DrawEllipse(null, warn, new Point(0, 0), 23, 23);
        }
    }

    /// <summary>
    /// A light-dependent resistor: the resistor body inside a circle, with arrows for the light
    /// falling on it. They point inwards — a cell absorbs light, unlike an LED which emits it.
    /// </summary>
    private static void DrawLdr(DrawingContext context, IPen pen, double zoom, LightDependentResistor ldr)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-20, 0));
        context.DrawLine(pen, new Point(20, 0), new Point(30, 0));
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 20, 20);
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new Rect(-12, -6, 24, 12));

        // Two arrows in, brighter when the cell is lit.
        var brush = ldr.IsLit ? CanvasTheme.ValueBrush : CanvasTheme.LabelBrush;
        var ray = CanvasTheme.Pen(brush, 1.4, zoom);

        foreach (var offset in new[] { -7.0, 3.0 })
        {
            var tail = new Point(-26 + offset, -24);
            var head = new Point(-14 + offset, -12);
            context.DrawLine(ray, tail, head);

            var barb = new StreamGeometry();
            using (var ctx = barb.Open())
            {
                ctx.BeginFigure(head, true);
                ctx.LineTo(new Point(head.X - 5, head.Y - 1));
                ctx.LineTo(new Point(head.X - 1, head.Y - 5));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(brush, null, barb);
        }
    }

    /// <summary>
    /// A relay: the coil as a boxed inductor on the left, the changeover contact on the right,
    /// with the armature drawn against whichever throw is currently made.
    /// </summary>
    private static void DrawRelay(DrawingContext context, IPen pen, double zoom, Relay relay)
    {
        // Coil.
        context.DrawLine(pen, new Point(-40, -20), new Point(-26, -20));
        context.DrawLine(pen, new Point(-40, 20), new Point(-26, 20));
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new Rect(-26, -20, 18, 40));

        // The dashed mechanical link from coil to contact.
        var link = CanvasTheme.Pen(CanvasTheme.SymbolBrush, 1.0, zoom, new DashStyle([2, 3], 0));
        context.DrawLine(link, new Point(-8, 0), new Point(14, 0));

        // Contact: common on the right, throwing between NO above and NC below.
        context.DrawLine(pen, new Point(40, -30), new Point(26, -30));
        context.DrawLine(pen, new Point(40, 30), new Point(26, 30));
        context.DrawLine(pen, new Point(40, 0), new Point(26, 0));

        var throwTo = relay.IsEnergised ? new Point(26, -30) : new Point(26, 30);
        context.DrawLine(pen, new Point(26, 0), throwTo);

        context.DrawEllipse(CanvasTheme.SymbolBrush, null, new Point(26, -30), 2.5, 2.5);
        context.DrawEllipse(CanvasTheme.SymbolBrush, null, new Point(26, 30), 2.5, 2.5);

        if (relay.Violations.Count > 0 && zoom > 0.4)
        {
            var warn = CanvasTheme.Pen(CanvasTheme.ErrorBrush, 2.0, zoom);
            context.DrawEllipse(null, warn, new Point(-17, 0), 18, 26);
        }
    }

    /// <summary>A fuse: the element in its holder, drawn broken once it has blown.</summary>
    private static void DrawFuse(DrawingContext context, IPen pen, double zoom, Fuse fuse)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-18, 0));
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-18, -9, 36, 18), 3));

        if (fuse.HasBlown)
        {
            // Broken element, with the gap that stopped the current.
            var broken = CanvasTheme.Pen(CanvasTheme.ErrorBrush, 1.6, zoom);
            context.DrawLine(broken, new Point(-18, 0), new Point(-6, 0));
            context.DrawLine(broken, new Point(-6, 0), new Point(-3, -5));
            context.DrawLine(broken, new Point(3, 5), new Point(6, 0));
            context.DrawLine(broken, new Point(6, 0), new Point(18, 0));
        }
        else
        {
            context.DrawLine(pen, new Point(-18, 0), new Point(18, 0));
        }
    }

    /// <summary>
    /// An optocoupler: the emitter and the detector in one package, with the barrier between them
    /// drawn as the dashed line it electrically is.
    /// </summary>
    private static void DrawOptocoupler(DrawingContext context, IPen pen, double zoom, Optocoupler opto)
    {
        var body = new Rect(-26, -30, 52, 60);
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(body, 3));

        foreach (var (from, to) in new[]
                 {
                     (new Point(-40, -20), new Point(-26, -20)),
                     (new Point(-40, 20), new Point(-26, 20)),
                     (new Point(40, -20), new Point(26, -20)),
                     (new Point(40, 20), new Point(26, 20)),
                 })
        {
            context.DrawLine(pen, from, to);
        }

        // Emitter: a diode pointing down the input side.
        context.DrawLine(pen, new Point(-18, -20), new Point(-18, 20));
        var led = new StreamGeometry();
        using (var ctx = led.Open())
        {
            ctx.BeginFigure(new Point(-24, -8), true);
            ctx.LineTo(new Point(-12, -8));
            ctx.LineTo(new Point(-18, 4));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(opto.IsConducting ? CanvasTheme.ValueBrush : CanvasTheme.SymbolFill, pen, led);
        context.DrawLine(pen, new Point(-24, 4), new Point(-12, 4));

        // The barrier.
        var barrier = CanvasTheme.Pen(CanvasTheme.SymbolBrush, 1.0, zoom, new DashStyle([2, 2], 0));
        context.DrawLine(barrier, new Point(-4, -26), new Point(-4, 26));

        // Detector: a transistor with no base lead, because light is its base drive.
        context.DrawLine(pen, new Point(12, -14), new Point(12, 14));
        context.DrawLine(pen, new Point(12, -7), new Point(26, -20));
        context.DrawLine(pen, new Point(12, 7), new Point(26, 20));

        // Light crossing the barrier.
        if (zoom > 0.5)
        {
            var ray = CanvasTheme.Pen(
                opto.IsConducting ? CanvasTheme.ValueBrush : CanvasTheme.LabelBrush, 1.0, zoom);
            context.DrawLine(ray, new Point(-9, -4), new Point(8, -4));
            context.DrawLine(ray, new Point(-9, 4), new Point(8, 4));
        }
    }

    /// <summary>
    /// An SCR: a diode with a gate lead. Filled while it is latched, so whether it is conducting
    /// is visible without reading the caption — which matters for a part whose whole character is
    /// that it stays on after the thing that triggered it has gone.
    /// </summary>
    private static void DrawThyristor(
        DrawingContext context, IPen pen, double zoom, CircuitComponent component,
        bool latched, bool hasGate)
    {
        context.DrawLine(pen, new Point(-40, 0), new Point(-12, 0));
        context.DrawLine(pen, new Point(12, 0), new Point(40, 0));

        var body = new StreamGeometry();
        using (var ctx = body.Open())
        {
            ctx.BeginFigure(new Point(-12, -14), true);
            ctx.LineTo(new Point(-12, 14));
            ctx.LineTo(new Point(12, 0));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(latched ? CanvasTheme.ValueBrush : CanvasTheme.SymbolFill, pen, body);
        context.DrawLine(pen, new Point(12, -14), new Point(12, 14));

        if (hasGate)
        {
            context.DrawLine(pen, new Point(0, 40), new Point(8, 14));
            context.DrawLine(pen, new Point(8, 14), new Point(12, 5));
        }
    }

    /// <summary>A triac: two thyristors back to back, so it is drawn symmetrically.</summary>
    private static void DrawTriac(DrawingContext context, IPen pen, double zoom, Triac triac)
    {
        context.DrawLine(pen, new Point(-40, 0), new Point(-6, 0));
        context.DrawLine(pen, new Point(6, 0), new Point(40, 0));

        var fill = triac.IsLatched ? CanvasTheme.ValueBrush : CanvasTheme.SymbolFill;

        foreach (var sign in new[] { -1.0, 1.0 })
        {
            var arm = new StreamGeometry();
            using (var ctx = arm.Open())
            {
                ctx.BeginFigure(new Point(sign * 6, -14), true);
                ctx.LineTo(new Point(sign * 6, 14));
                ctx.LineTo(new Point(sign * -6, 0));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(fill, pen, arm);
        }

        context.DrawLine(pen, new Point(0, 40), new Point(-6, 14));
    }

    /// <summary>A diac: the triac's symbol without a gate, because it has none.</summary>
    private static void DrawDiac(DrawingContext context, IPen pen, double zoom, Diac diac)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-6, 0));
        context.DrawLine(pen, new Point(6, 0), new Point(30, 0));

        var fill = diac.IsLatched ? CanvasTheme.ValueBrush : CanvasTheme.SymbolFill;

        foreach (var sign in new[] { -1.0, 1.0 })
        {
            var arm = new StreamGeometry();
            using (var ctx = arm.Open())
            {
                ctx.BeginFigure(new Point(sign * 6, -13), true);
                ctx.LineTo(new Point(sign * 6, 13));
                ctx.LineTo(new Point(sign * -6, 0));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(fill, pen, arm);
        }
    }

    /// <summary>
    /// A JFET: the channel bar with the gate arrow, pointing in on an N-channel part and out on a
    /// P-channel one. No gap in the channel, because a JFET conducts with no gate drive at all.
    /// </summary>
    private static void DrawJfet(DrawingContext context, IPen pen, double zoom, JunctionFet jfet)
    {
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 28, 28);

        // Gate lead straight onto the channel. There is no gap here, unlike the MOSFET's
        // insulated gate: a JFET's gate is a junction, in contact with the channel it pinches.
        context.DrawLine(pen, new Point(-40, 0), new Point(-8, 0));

        // The channel: one unbroken bar, against the MOSFET's three segments. That is the
        // difference the symbol is carrying — this device conducts with no gate drive at all.
        context.DrawLine(pen, new Point(-8, -18), new Point(-8, 18));

        context.DrawLine(pen, new Point(-8, -14), new Point(16, -14));
        context.DrawLine(pen, new Point(16, -14), new Point(16, -26));
        context.DrawLine(pen, new Point(16, -26), new Point(20, -40));

        context.DrawLine(pen, new Point(-8, 14), new Point(16, 14));
        context.DrawLine(pen, new Point(16, 14), new Point(16, 26));
        context.DrawLine(pen, new Point(16, 26), new Point(20, 40));

        // The gate arrow points in on an N-channel part and out on a P-channel one, the same
        // way a bipolar's emitter arrow reads: along the way the junction would conduct.
        DrawArrowHead(context, pen,
            from: jfet.Model.IsNChannel ? new Point(-30, 0) : new Point(-8, 0),
            to: jfet.Model.IsNChannel ? new Point(-8, 0) : new Point(-30, 0));

        if (jfet.Violations.Count > 0 && zoom > 0.4)
        {
            var warn = CanvasTheme.Pen(CanvasTheme.ErrorBrush, 2.0, zoom);
            context.DrawEllipse(null, warn, new Point(0, 0), 33, 33);
        }
    }

    /// <summary>
    /// A TL431: drawn as the adjustable zener it behaves like — cathode bar at the top, anode
    /// below, and the reference coming in from the side to the point where it is compared.
    /// </summary>
    private static void DrawShuntReference(
        DrawingContext context, IPen pen, double zoom, ShuntReference shunt)
    {
        context.DrawLine(pen, new Point(0, -40), new Point(0, -10));
        context.DrawLine(pen, new Point(0, 40), new Point(0, 16));

        // The triangle points from anode up to cathode, as a diode's does.
        var body = new StreamGeometry();
        using (var ctx = body.Open())
        {
            ctx.BeginFigure(new Point(-13, 16), true);
            ctx.LineTo(new Point(13, 16));
            ctx.LineTo(new Point(0, -10));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(CanvasTheme.SymbolFill, pen, body);

        // Cathode bar with the bent ends that mark it as a zener rather than a plain diode.
        context.DrawLine(pen, new Point(-13, -10), new Point(13, -10));
        context.DrawLine(pen, new Point(-13, -10), new Point(-18, -4));
        context.DrawLine(pen, new Point(13, -10), new Point(18, -16));

        // Reference lead in from the right, arrow pointing at the body.
        context.DrawLine(pen, new Point(40, 0), new Point(12, 4));
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(new Point(12, 4), true);
            ctx.LineTo(new Point(20, 1));
            ctx.LineTo(new Point(19, 8));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(CanvasTheme.SymbolBrush, null, head);

        if (shunt.Violations.Count > 0 && zoom > 0.4)
        {
            var warn = CanvasTheme.Pen(CanvasTheme.ErrorBrush, 2.0, zoom);
            context.DrawEllipse(null, warn, new Point(0, 2), 24, 24);
        }
    }

    /// <summary>
    /// A packaged bridge: the diamond with the AC pair on the flanks and the DC pair top and
    /// bottom, and one diode drawn inside pointing at the positive corner so the direction is
    /// obvious.
    /// </summary>
    private static void DrawBridgeRectifier(DrawingContext context, IPen pen, double zoom)
    {
        const double r = 26;

        foreach (var (from, to) in new[]
                 {
                     (new Point(-40, 0), new Point(-r, 0)),
                     (new Point(40, 0), new Point(r, 0)),
                     (new Point(0, -40), new Point(0, -r)),
                     (new Point(0, 40), new Point(0, r)),
                 })
        {
            context.DrawLine(pen, from, to);
        }

        var body = new StreamGeometry();
        using (var ctx = body.Open())
        {
            ctx.BeginFigure(new Point(0, -r), true);
            ctx.LineTo(new Point(r, 0));
            ctx.LineTo(new Point(0, r));
            ctx.LineTo(new Point(-r, 0));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(CanvasTheme.SymbolFill, pen, body);

        // One arm shown: a triangle and bar pointing at the + corner.
        var arrow = new StreamGeometry();
        using (var ctx = arrow.Open())
        {
            ctx.BeginFigure(new Point(-8, 5), true);
            ctx.LineTo(new Point(8, 5));
            ctx.LineTo(new Point(0, -7));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(CanvasTheme.SymbolBrush, pen, arrow);
        context.DrawLine(pen, new Point(-8, -7), new Point(8, -7));

        if (zoom > 0.55)
        {
            DrawCenteredText(context, "+", new Point(13, -15), 9, zoom, CanvasTheme.SymbolBrush);
            DrawCenteredText(context, "-", new Point(13, 15), 9, zoom, CanvasTheme.SymbolBrush);
        }
    }

    private static void DrawCapacitor(DrawingContext context, IPen pen)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-4, 0));
        context.DrawLine(pen, new Point(4, 0), new Point(30, 0));
        context.DrawLine(pen, new Point(-4, -12), new Point(-4, 12));
        context.DrawLine(pen, new Point(4, -12), new Point(4, 12));
    }

    private static void DrawInductor(DrawingContext context, IPen pen)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-18, 0));
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(-18, 0), false);
            for (var i = 0; i < 4; i++)
            {
                var start = -18 + i * 9;
                ctx.ArcTo(new Point(start + 9, 0), new Size(4.5, 4.5), 0, false, SweepDirection.Clockwise);
            }
            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawTransformer(DrawingContext context, IPen pen, IPen thin)
    {
        DrawWinding(context, pen, -14, -20, 20);
        DrawWinding(context, pen, 14, -20, 20);

        // Core.
        context.DrawLine(thin, new Point(-3, -24), new Point(-3, 24));
        context.DrawLine(thin, new Point(3, -24), new Point(3, 24));

        context.DrawLine(pen, new Point(-30, -20), new Point(-14, -20));
        context.DrawLine(pen, new Point(-30, 20), new Point(-14, 20));
        context.DrawLine(pen, new Point(30, -20), new Point(14, -20));
        context.DrawLine(pen, new Point(30, 20), new Point(14, 20));
    }

    private static void DrawWinding(DrawingContext context, IPen pen, double x, double top, double height)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x, top), false);
            var direction = x < 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise;
            for (var i = 0; i < 4; i++)
            {
                var y = top + i * (height / 4.0);
                ctx.ArcTo(new Point(x, y + height / 4.0), new Size(5, height / 8.0), 0, false, direction);
            }
            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawPotentiometer(DrawingContext context, IPen pen)
    {
        context.DrawLine(pen, new Point(-30, 20), new Point(-18, 20));
        context.DrawLine(pen, new Point(30, 20), new Point(18, 20));
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new Rect(-18, 12, 36, 16));

        // Wiper arrow coming down onto the track.
        context.DrawLine(pen, new Point(0, -30), new Point(0, -6));
        var arrow = new PolylineGeometry([new Point(-5, -6), new Point(5, -6), new Point(0, 6)], true);
        context.DrawGeometry(pen.Brush, pen, arrow);
    }

    // ---- sources ---------------------------------------------------------

    private static void DrawGround(DrawingContext context, IPen pen)
    {
        context.DrawLine(pen, new Point(0, -20), new Point(0, -4));
        context.DrawLine(pen, new Point(-14, -4), new Point(14, -4));
        context.DrawLine(pen, new Point(-9, 2), new Point(9, 2));
        context.DrawLine(pen, new Point(-4, 8), new Point(4, 8));
    }

    private static void DrawVoltageSource(DrawingContext context, IPen pen, double zoom)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-18, 0));
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 18, 18);

        // Plus on the left (the + terminal), minus on the right.
        context.DrawLine(pen, new Point(-11, 0), new Point(-5, 0));
        context.DrawLine(pen, new Point(-8, -3), new Point(-8, 3));
        context.DrawLine(pen, new Point(5, 0), new Point(11, 0));
    }

    private static void DrawCurrentSource(DrawingContext context, IPen pen)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-18, 0));
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 18, 18);

        context.DrawLine(pen, new Point(-9, 0), new Point(9, 0));
        var head = new PolylineGeometry([new Point(9, 0), new Point(2, -5), new Point(2, 5)], true);
        context.DrawGeometry(pen.Brush, pen, head);
    }

    private static void DrawFunctionGenerator(DrawingContext context, IPen pen, FunctionGenerator generator)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-18, 0));
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 18, 18);

        var points = new List<Point>();
        for (var i = 0; i <= 24; i++)
        {
            var phase = i / 24.0;
            var x = -11 + phase * 22;
            var y = generator.Shape switch
            {
                Waveform.Square => phase < 0.5 ? -7 : 7,
                Waveform.Triangle => phase < 0.5 ? -7 + phase * 28 : 7 - (phase - 0.5) * 28,
                Waveform.Sawtooth => -7 + phase * 14,
                Waveform.Dc => 0,
                _ => -7 * Math.Sin(phase * 2 * Math.PI),
            };
            points.Add(new Point(x, y));
        }

        context.DrawGeometry(null, pen, new PolylineGeometry(points, false));
    }

    // ---- semiconductors --------------------------------------------------

    private static void DrawDiode(DrawingContext context, IPen pen)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-8, 0));
        context.DrawLine(pen, new Point(8, 0), new Point(30, 0));

        var triangle = new PolylineGeometry([new Point(-8, -11), new Point(8, 0), new Point(-8, 11)], true);
        context.DrawGeometry(pen.Brush, pen, triangle);
        context.DrawLine(pen, new Point(8, -11), new Point(8, 11));
    }

    private static void DrawLed(DrawingContext context, IPen pen, Led led)
    {
        DrawDiode(context, pen);

        // Emission arrows, brightened by how hard the LED is being driven.
        var colour = led.EmittedColor;
        var alpha = (byte)Math.Clamp(60 + led.Brightness * 195, 0, 255);
        var glow = new SolidColorBrush(Color.FromArgb(alpha, colour.R, colour.G, colour.B));
        var glowPen = new Pen(glow, pen.Thickness);

        if (led.Brightness > 0.02)
            context.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(alpha / 3), colour.R, colour.G, colour.B)),
                null, new Point(0, 0), 20, 20);

        for (var i = 0; i < 2; i++)
        {
            var offset = i * 8 - 4;
            context.DrawLine(glowPen, new Point(offset, -14), new Point(offset + 7, -22));
            context.DrawLine(glowPen, new Point(offset + 7, -22), new Point(offset + 3, -20));
        }
    }

    private static void DrawRegulator(DrawingContext context, IPen pen, double zoom, VoltageRegulator regulator)
    {
        context.DrawLine(pen, new Point(-40, 0), new Point(-26, 0));
        context.DrawLine(pen, new Point(26, 0), new Point(40, 0));
        context.DrawLine(pen, new Point(0, 30), new Point(0, 18));
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-26, -18, 52, 36), 3));

        DrawCenteredText(context, regulator.Model.Name, new Point(0, 0), 9, zoom, CanvasTheme.LabelBrush);
    }

    private static void DrawOpAmp(DrawingContext context, IPen pen, double zoom)
    {
        context.DrawLine(pen, new Point(-40, 20), new Point(-26, 20));
        context.DrawLine(pen, new Point(-40, -20), new Point(-26, -20));
        context.DrawLine(pen, new Point(26, 0), new Point(40, 0));
        context.DrawLine(pen, new Point(0, -30), new Point(0, -18));
        context.DrawLine(pen, new Point(0, 30), new Point(0, 18));

        var body = new PolylineGeometry([new Point(-26, -30), new Point(26, 0), new Point(-26, 30)], true);
        context.DrawGeometry(CanvasTheme.SymbolFill, pen, body);

        // Input polarity marks: non-inverting is the lower pin.
        context.DrawLine(pen, new Point(-22, 20), new Point(-14, 20));
        context.DrawLine(pen, new Point(-18, 16), new Point(-18, 24));
        context.DrawLine(pen, new Point(-22, -20), new Point(-14, -20));
    }

    // ---- transistors -----------------------------------------------------

    private static void DrawBipolar(DrawingContext context, IPen pen, BipolarTransistor bjt)
    {
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 28, 28);

        // Base plate and its lead.
        context.DrawLine(pen, new Point(-40, 0), new Point(-8, 0));
        context.DrawLine(pen, new Point(-8, -18), new Point(-8, 18));

        // Collector and emitter, angled off the plate to the package pins.
        context.DrawLine(pen, new Point(-8, -10), new Point(16, -26));
        context.DrawLine(pen, new Point(16, -26), new Point(20, -40));
        context.DrawLine(pen, new Point(-8, 10), new Point(16, 26));
        context.DrawLine(pen, new Point(16, 26), new Point(20, 40));

        // The emitter arrow is the whole point of the symbol: it points out of the device on an
        // NPN and into it on a PNP, which is also the direction conventional current flows.
        DrawArrowHead(context, pen,
            from: bjt.IsNpn ? new Point(-8, 10) : new Point(16, 26),
            to: bjt.IsNpn ? new Point(16, 26) : new Point(-8, 10));
    }

    private static void DrawMosfet(DrawingContext context, IPen pen, Mosfet mosfet)
    {
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 28, 28);

        // Insulated gate: a plate with a visible gap to the channel.
        context.DrawLine(pen, new Point(-40, 0), new Point(-22, 0));
        context.DrawLine(pen, new Point(-22, -16), new Point(-22, 16));

        // Channel drawn as three segments, the enhancement-mode convention.
        context.DrawLine(pen, new Point(-12, -18), new Point(-12, -7));
        context.DrawLine(pen, new Point(-12, -4), new Point(-12, 4));
        context.DrawLine(pen, new Point(-12, 7), new Point(-12, 18));

        context.DrawLine(pen, new Point(-12, -14), new Point(16, -14));
        context.DrawLine(pen, new Point(16, -14), new Point(16, -26));
        context.DrawLine(pen, new Point(16, -26), new Point(20, -40));

        context.DrawLine(pen, new Point(-12, 14), new Point(16, 14));
        context.DrawLine(pen, new Point(16, 14), new Point(16, 26));
        context.DrawLine(pen, new Point(16, 26), new Point(20, 40));

        // Bulk tie with the substrate arrow: inward for N-channel, outward for P.
        context.DrawLine(pen, new Point(-12, 0), new Point(16, 0));
        context.DrawLine(pen, new Point(16, 0), new Point(16, 14));
        DrawArrowHead(context, pen,
            from: mosfet.IsNChannel ? new Point(4, 0) : new Point(-12, 0),
            to: mosfet.IsNChannel ? new Point(-12, 0) : new Point(4, 0));
    }

    /// <summary>Filled triangular arrow head at the <paramref name="to"/> end of a segment.</summary>
    private static void DrawArrowHead(DrawingContext context, IPen pen, Point from, Point to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 1e-6) return;

        var ux = dx / length;
        var uy = dy / length;
        const double size = 9.0;
        const double halfWidth = 4.0;

        var baseX = to.X - ux * size;
        var baseY = to.Y - uy * size;

        var head = new PolylineGeometry(
        [
            new Point(to.X, to.Y),
            new Point(baseX - uy * halfWidth, baseY + ux * halfWidth),
            new Point(baseX + uy * halfWidth, baseY - ux * halfWidth),
        ], true);

        context.DrawGeometry(pen.Brush, pen, head);
    }

    // ---- switches --------------------------------------------------------

    private static void DrawToggleSwitch(DrawingContext context, IPen pen, ToggleSwitch sw)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-14, 0));
        context.DrawLine(pen, new Point(14, 0), new Point(30, 0));

        context.DrawEllipse(pen.Brush, null, new Point(-14, 0), 3.5, 3.5);
        context.DrawEllipse(pen.Brush, null, new Point(14, 0), 3.5, 3.5);

        // The lever lies flat when closed and lifts away from the far contact when open.
        var end = sw.IsClosed ? new Point(14, 0) : new Point(12, -16);
        context.DrawLine(pen, new Point(-14, 0), end);
    }

    private static void DrawPushButton(DrawingContext context, IPen pen, PushButton button)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-14, 0));
        context.DrawLine(pen, new Point(14, 0), new Point(30, 0));

        context.DrawEllipse(pen.Brush, null, new Point(-14, 0), 3.5, 3.5);
        context.DrawEllipse(pen.Brush, null, new Point(14, 0), 3.5, 3.5);

        // Contact bar drops onto the terminals when the button is pressed.
        var barY = button.IsPressed ? -4.0 : -12.0;
        context.DrawLine(pen, new Point(-18, barY), new Point(18, barY));

        // Plunger and cap.
        context.DrawLine(pen, new Point(0, barY), new Point(0, barY - 10));
        context.DrawLine(pen, new Point(-10, barY - 10), new Point(10, barY - 10));

        if (!button.IsNormallyOpen)
        {
            // Normally-closed parts are drawn with the contact bridging from below.
            context.DrawLine(pen, new Point(-18, 10), new Point(18, 10));
        }
    }

    private static void DrawSpdtSwitch(DrawingContext context, IPen pen, SpdtSwitch sw)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-14, 0));
        context.DrawLine(pen, new Point(30, -20), new Point(16, -20));
        context.DrawLine(pen, new Point(30, 20), new Point(16, 20));

        context.DrawEllipse(pen.Brush, null, new Point(-14, 0), 3.5, 3.5);
        context.DrawEllipse(pen.Brush, null, new Point(16, -20), 3.5, 3.5);
        context.DrawEllipse(pen.Brush, null, new Point(16, 20), 3.5, 3.5);

        var throwPoint = sw.IsThrownToB ? new Point(16, 20) : new Point(16, -20);
        context.DrawLine(pen, new Point(-14, 0), throwPoint);
    }

    // ---- comparator ------------------------------------------------------

    private static void DrawComparator(DrawingContext context, IPen pen, double zoom)
    {
        context.DrawLine(pen, new Point(-40, 20), new Point(-26, 20));
        context.DrawLine(pen, new Point(-40, -20), new Point(-26, -20));
        context.DrawLine(pen, new Point(26, 0), new Point(40, 0));
        context.DrawLine(pen, new Point(0, -30), new Point(0, -18));
        context.DrawLine(pen, new Point(0, 30), new Point(0, 18));

        var body = new PolylineGeometry(
            [new Point(-26, -30), new Point(26, 0), new Point(-26, 30)], true);
        context.DrawGeometry(CanvasTheme.SymbolFill, pen, body);

        context.DrawLine(pen, new Point(-22, 20), new Point(-14, 20));
        context.DrawLine(pen, new Point(-18, 16), new Point(-18, 24));
        context.DrawLine(pen, new Point(-22, -20), new Point(-14, -20));

        // A step glyph inside the triangle, which is what separates a comparator symbol from an
        // op-amp at a glance.
        context.DrawGeometry(null, pen, new PolylineGeometry(
        [
            new Point(-12, 8), new Point(-2, 8), new Point(-2, -8), new Point(8, -8),
        ], false));
    }

    // ---- seven-segment display -------------------------------------------

    private static void DrawSevenSegment(
        DrawingContext context, IPen pen, double zoom, SevenSegmentDisplay display)
    {
        // Package body and pin legs.
        var body = new Rect(-50, -72, 100, 144);
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(body, 4));

        foreach (var terminal in display.Terminals)
        {
            var p = new Point(terminal.CanvasOffset.X, terminal.CanvasOffset.Y);
            var inner = new Point(
                Math.Clamp(p.X, body.Left, body.Right),
                Math.Clamp(p.Y, body.Top, body.Bottom));
            context.DrawLine(pen, p, inner);
        }

        var colour = display.EmittedColor;
        var lit = Color.FromRgb(colour.R, colour.G, colour.B);
        var dark = Color.FromArgb(70, 40, 46, 54);

        // Classic segment placement: a across the top, g through the middle, d along the bottom.
        const double halfWidth = 22.0;
        const double quarterHeight = 24.0;

        DrawSegment(context, display, 0, Horizontal(0, -48, 44), lit, dark);
        DrawSegment(context, display, 1, Vertical(halfWidth, -quarterHeight, 44), lit, dark);
        DrawSegment(context, display, 2, Vertical(halfWidth, quarterHeight, 44), lit, dark);
        DrawSegment(context, display, 3, Horizontal(0, 48, 44), lit, dark);
        DrawSegment(context, display, 4, Vertical(-halfWidth, quarterHeight, 44), lit, dark);
        DrawSegment(context, display, 5, Vertical(-halfWidth, -quarterHeight, 44), lit, dark);
        DrawSegment(context, display, 6, Horizontal(0, 0, 44), lit, dark);

        // Decimal point.
        var dpBrightness = display.SegmentBrightness[7];
        var dpBrush = new SolidColorBrush(Blend(dark, lit, dpBrightness));
        context.DrawEllipse(dpBrush, null, new Point(34, 50), 5, 5);
    }

    private static void DrawSegment(
        DrawingContext context, SevenSegmentDisplay display, int index,
        Point[] shape, Color lit, Color dark)
    {
        var brightness = display.SegmentBrightness[index];
        var brush = new SolidColorBrush(Blend(dark, lit, brightness));
        context.DrawGeometry(brush, null, new PolylineGeometry(shape, true));
    }

    /// <summary>Mixes between the unlit and lit colours, with a floor so segments stay visible.</summary>
    private static Color Blend(Color dark, Color lit, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        return Color.FromArgb(
            (byte)(dark.A + (255 - dark.A) * t),
            (byte)(dark.R + (lit.R - dark.R) * t),
            (byte)(dark.G + (lit.G - dark.G) * t),
            (byte)(dark.B + (lit.B - dark.B) * t));
    }

    /// <summary>A horizontal segment as a stretched hexagon, the shape a real digit uses.</summary>
    private static Point[] Horizontal(double cx, double cy, double length)
    {
        const double t = 5.0;
        var half = length / 2;
        return
        [
            new Point(cx - half + t, cy - t),
            new Point(cx + half - t, cy - t),
            new Point(cx + half, cy),
            new Point(cx + half - t, cy + t),
            new Point(cx - half + t, cy + t),
            new Point(cx - half, cy),
        ];
    }

    private static Point[] Vertical(double cx, double cy, double length)
    {
        const double t = 5.0;
        var half = length / 2;
        return
        [
            new Point(cx - t, cy - half + t),
            new Point(cx - t, cy + half - t),
            new Point(cx, cy + half),
            new Point(cx + t, cy + half - t),
            new Point(cx + t, cy - half + t),
            new Point(cx, cy - half),
        ];
    }

    // ---- packages --------------------------------------------------------

    private static void DrawDip(DrawingContext context, IPen pen, double zoom, int pinCount, string partNumber)
    {
        var height = DipPackage.BodyHeight(pinCount);
        var body = new Rect(-DipPackage.HalfWidth + 10, -height / 2, (DipPackage.HalfWidth - 10) * 2, height);
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(body, 3));

        // Pin 1 notch on the top edge.
        var notch = new StreamGeometry();
        using (var ctx = notch.Open())
        {
            ctx.BeginFigure(new Point(-6, body.Top), false);
            ctx.ArcTo(new Point(6, body.Top), new Size(6, 6), 0, false, SweepDirection.CounterClockwise);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, notch);

        // Pin legs.
        for (var pin = 1; pin <= pinCount; pin++)
        {
            var offset = DipPackage.PinOffset(pin, pinCount);
            var inner = offset.X < 0 ? body.Left : body.Right;
            context.DrawLine(pen, new Point(offset.X, offset.Y), new Point(inner, offset.Y));
        }

        DrawCenteredText(context, partNumber, new Point(0, 0), 10, zoom, CanvasTheme.LabelBrush);
    }

    /// <summary>
    /// How far from a symbol's centre its designator and value captions belong.
    /// <para>
    /// A fixed offset puts the caption inside the body of anything taller than a resistor — a
    /// 16-pin DIP already overlapped, and a 40-pin board buries the caption in its pin rows.
    /// </para>
    /// </summary>
    public static double LabelOffset(CircuitComponent component) => component switch
    {
        DeveloperBoard board => BoardPackage.BodyHeight(board.Profile) / 2 + 16,
        Ne555 => DipPackage.BodyHeight(8) / 2 + 16,
        DigitalIc ic => DipPackage.BodyHeight(ic.PinCount) / 2 + 16,
        _ => 30.0,
    };

    /// <summary>
    /// A development board: an outline with every header pin labelled. Pin names are printed
    /// inside the body rather than outside it, because the outside is where the wires go and a
    /// forty-pin header leaves no room for both.
    /// </summary>
    private static void DrawBoard(DrawingContext context, IPen pen, double zoom, DeveloperBoard board)
    {
        var profile = board.Profile;
        var height = BoardPackage.BodyHeight(profile);
        var half = BoardPackage.HalfWidth - 18;
        var body = new Rect(-half, -height / 2, half * 2, height);

        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(body, 5));

        // Header strips, so the two pin columns read as connectors rather than loose legs.
        var strip = 9.0;
        foreach (var left in new[] { true, false })
        {
            var x = left ? body.Left : body.Right - strip;
            context.DrawRectangle(null, thinPenFor(pen), new Rect(x, body.Top + 12, strip, height - 24));
        }

        DrawPinColumn(context, pen, zoom, profile, profile.LeftPins, body, left: true);
        DrawPinColumn(context, pen, zoom, profile, profile.RightPins, body, left: false);

        DrawCenteredText(context, $"{profile.LogicVoltage:0.0}V logic", new Point(0, height / 2 - 12), 8, zoom,
            CanvasTheme.ValueBrush);

        static IPen thinPenFor(IPen pen) => new Pen(pen.Brush, pen.Thickness * 0.6);
    }

    private static void DrawPinColumn(
        DrawingContext context, IPen pen, double zoom, BoardProfile profile,
        IReadOnlyList<BoardPin> pins, Rect body, bool left)
    {
        for (var i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            var offset = BoardPackage.PinOffset(profile, i, left);
            var edge = left ? body.Left : body.Right;

            context.DrawLine(pen, new Point(offset.X, offset.Y), new Point(edge, offset.Y));

            // Power and ground pins are worth picking out: they are what a beginner mis-wires.
            var brush = pin.Function switch
            {
                PinFunction.Power => CanvasTheme.ValueBrush,
                PinFunction.Ground => CanvasTheme.LabelBrush,
                PinFunction.Reserved => CanvasTheme.LabelBrush,
                _ => CanvasTheme.SymbolBrush,
            };

            if (zoom > 0.55)
            {
                var textX = left ? body.Left + 14 : body.Right - 14;
                DrawAlignedText(context, pin.Label, new Point(textX, offset.Y), 7, zoom, brush, alignLeft: left);
            }
        }
    }

    /// <summary>Draws text butted against a point rather than centred on it, for pin columns.</summary>
    private static void DrawAlignedText(
        DrawingContext context, string text, Point anchor, double screenSize, double zoom,
        IBrush brush, bool alignLeft)
    {
        if (string.IsNullOrEmpty(text)) return;

        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            CanvasTheme.LabelTypeface, screenSize, brush);

        using (context.PushTransform(
                   Matrix.CreateScale(1 / zoom, 1 / zoom) * Matrix.CreateTranslation(anchor.X, anchor.Y)))
        {
            context.DrawText(formatted, new Point(alignLeft ? 0 : -formatted.Width, -formatted.Height / 2));
        }
    }

    // ---- logic -----------------------------------------------------------

    private static void DrawLogicGate(DrawingContext context, IPen pen, LogicGate gate)
    {
        foreach (var terminal in gate.InputTerminals)
            context.DrawLine(pen, new Point(terminal.CanvasOffset.X, terminal.CanvasOffset.Y),
                new Point(-20, terminal.CanvasOffset.Y));

        var hasBubble = gate.Function is GateFunction.Nand or GateFunction.Nor
            or GateFunction.Not or GateFunction.Xnor;
        var bodyRight = hasBubble ? 22.0 : 28.0;

        context.DrawLine(pen, new Point(hasBubble ? bodyRight + 8 : bodyRight, 0), new Point(40, 0));

        switch (gate.Function)
        {
            case GateFunction.And:
            case GateFunction.Nand:
                DrawAndBody(context, pen, bodyRight);
                break;
            case GateFunction.Not:
            case GateFunction.Buffer:
                context.DrawGeometry(CanvasTheme.SymbolFill, pen,
                    new PolylineGeometry([new Point(-20, -18), new Point(bodyRight, 0), new Point(-20, 18)], true));
                break;
            case GateFunction.Xor:
            case GateFunction.Xnor:
                DrawOrBody(context, pen, bodyRight, -26);
                DrawOrArc(context, pen, -30);
                break;
            default:
                DrawOrBody(context, pen, bodyRight, -22);
                break;
        }

        if (hasBubble)
            context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(bodyRight + 4, 0), 4, 4);
    }

    private static void DrawAndBody(DrawingContext context, IPen pen, double right)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(-20, -20), true);
            ctx.LineTo(new Point(right - 20, -20));
            ctx.ArcTo(new Point(right - 20, 20), new Size(20, 20), 0, false, SweepDirection.Clockwise);
            ctx.LineTo(new Point(-20, 20));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(CanvasTheme.SymbolFill, pen, geometry);
    }

    private static void DrawOrBody(DrawingContext context, IPen pen, double right, double backX)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(backX, -20), true);
            ctx.CubicBezierTo(new Point(right - 14, -20), new Point(right - 4, -12), new Point(right, 0));
            ctx.CubicBezierTo(new Point(right - 4, 12), new Point(right - 14, 20), new Point(backX, 20));
            ctx.CubicBezierTo(new Point(backX + 12, 10), new Point(backX + 12, -10), new Point(backX, -20));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(CanvasTheme.SymbolFill, pen, geometry);
    }

    private static void DrawOrArc(DrawingContext context, IPen pen, double x)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x, -20), false);
            ctx.CubicBezierTo(new Point(x + 12, -10), new Point(x + 12, 10), new Point(x, 20));
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawToggle(DrawingContext context, IPen pen, double zoom, LogicToggle toggle)
    {
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-20, -16, 38, 32), 4));

        // Semantic keys rather than fixed hexes: the "on" knob is the same green a live wire is
        // drawn in, and the "off" knob the neutral used for terminals, so both follow the theme.
        var fill = toggle.State ? CanvasTheme.WireBrush : CanvasTheme.TerminalBrush;
        context.DrawEllipse(fill, null, new Point(toggle.State ? 7 : -9, 0), 9, 9);
        DrawCenteredText(context, toggle.State ? "1" : "0", new Point(toggle.State ? -9 : 7, 0), 10, zoom,
            CanvasTheme.LabelBrush);
    }

    private static void DrawClock(DrawingContext context, IPen pen)
    {
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-20, -16, 38, 32), 4));

        // A couple of square-wave cycles inside the body.
        var points = new List<Point>
        {
            new(-14, 6), new(-14, -6), new(-6, -6), new(-6, 6),
            new(2, 6), new(2, -6), new(10, -6), new(10, 6), new(13, 6),
        };
        context.DrawGeometry(null, pen, new PolylineGeometry(points, false));
    }

    private static void DrawBridge(DrawingContext context, IPen pen, double zoom, string label)
    {
        context.DrawLine(pen, new Point(-40, 0), new Point(-24, 0));
        context.DrawLine(pen, new Point(24, 0), new Point(40, 0));
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-24, -18, 48, 36), 3));
        DrawCenteredText(context, label, new Point(0, 0), 11, zoom, CanvasTheme.LabelBrush);
    }

    private static void DrawGenericBox(DrawingContext context, IPen pen, double zoom, CircuitComponent component)
    {
        foreach (var terminal in component.Terminals)
        {
            var p = new Point(terminal.CanvasOffset.X, terminal.CanvasOffset.Y);
            var inner = new Point(Math.Clamp(p.X, -24, 24), Math.Clamp(p.Y, -18, 18));
            context.DrawLine(pen, p, inner);
        }

        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-24, -18, 48, 36), 3));
        DrawCenteredText(context, component.ComponentType, new Point(0, 0), 9, zoom, CanvasTheme.LabelBrush);
    }

    // ---- text ------------------------------------------------------------

    /// <summary>
    /// Draws text centred on a point at a constant on-screen size, by undoing the canvas zoom for
    /// the glyphs only.
    /// </summary>
    public static void DrawCenteredText(
        DrawingContext context, string text, Point center, double screenSize, double zoom, IBrush brush)
    {
        if (string.IsNullOrEmpty(text)) return;

        var formatted = new FormattedText(
            text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            CanvasTheme.LabelTypeface, screenSize, brush);

        using (context.PushTransform(
                   Matrix.CreateScale(1 / zoom, 1 / zoom) * Matrix.CreateTranslation(center.X, center.Y)))
        {
            context.DrawText(formatted, new Point(-formatted.Width / 2, -formatted.Height / 2));
        }
    }
}
