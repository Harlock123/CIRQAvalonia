using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Cirq.Components.Boards;
using Cirq.Components.Bridges;
using Cirq.Components.Passive;
using Cirq.Components.Buses;
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
    public static void Draw(ISymbolCanvas context, CircuitComponent component, double zoom, bool isSelected)
    {
        var stroke = isSelected ? CanvasTheme.SelectionBrush : CanvasTheme.SymbolBrush;
        var pen = CanvasTheme.Pen(stroke, 1.8, zoom);
        var thin = CanvasTheme.Pen(stroke, 1.2, zoom);

        switch (component)
        {
            case Resistor: DrawResistor(context, pen); break;
            case Speaker speaker: DrawSpeaker(context, pen, zoom, speaker); break;
            case Crystal: DrawCrystal(context, pen); break;
            case CharacterLcd lcd: DrawCharacterLcd(context, pen, zoom, lcd); break;
            case I2cDevice bus: DrawPackage(context, pen, zoom, bus, bus.ComponentType); break;
            case UartPort uart: DrawPackage(context, pen, zoom, uart, uart.ComponentType); break;
            case HallSensor hall: DrawHallSensor(context, pen, zoom, hall); break;
            case ReedSwitch reed: DrawReedSwitch(context, pen, zoom, reed); break;
            case PirSensor pir: DrawPirSensor(context, pen, zoom, pir); break;
            case GateDriver driver: DrawGateDriver(context, pen, zoom, driver); break;
            case FerriteBead: DrawFerriteBead(context, pen); break;
            case TransmissionLine line: DrawTransmissionLine(context, pen, zoom, line); break;
            case Ds18b20 sensor: DrawPackage(context, pen, zoom, sensor, "DS18B20"); break;
            case OneWireMaster master: DrawPackage(context, pen, zoom, master, master.ComponentType); break;
            case HBridge bridge: DrawHBridge(context, pen, zoom, bridge); break;
            case LithiumCharger charger: DrawPackage(context, pen, zoom, charger, "Li-ION"); break;
            case Phototransistor photo: DrawPhototransistor(context, pen, zoom, photo); break;
            case LevelShifter shifter: DrawLevelShifter(context, pen, zoom, shifter); break;
            case SpiMaster spi: DrawPackage(context, pen, zoom, spi, spi.ComponentType); break;
            case DipSwitch dip: DrawDipSwitch(context, pen, zoom, dip); break;
            case SolarCell pv: DrawSolarCell(context, pen, zoom, pv); break;
            case OscillatorModule osc: DrawOscillatorModule(context, pen, zoom, osc); break;
            case Thermocouple tc: DrawThermocouple(context, pen, zoom, tc); break;
            case LoadCell cell: DrawLoadCell(context, pen, zoom, cell); break;
            case RotaryEncoder encoder: DrawRotaryEncoder(context, pen, zoom, encoder); break;
            case ChargePump pump: DrawChargePump(context, pen, zoom, pump); break;
            case SwitchingRegulator reg: DrawSwitchingRegulator(context, pen, zoom, reg); break;
            case Microphone mic: DrawMicrophone(context, pen, zoom, mic); break;
            case Servo servo: DrawServo(context, pen, zoom, servo); break;
            case StepperMotor stepper: DrawStepper(context, pen, zoom, stepper); break;
            case UltrasonicRanger sonar: DrawUltrasonicRanger(context, pen, zoom, sonar); break;
            case NoiseSource noise: DrawNoiseSource(context, pen, zoom, noise); break;
            case Battery battery: DrawBattery(context, pen, zoom, battery); break;
            case TransientSuppressor tvs: DrawTvs(context, pen, zoom, tvs); break;
            case Varistor varistor: DrawVaristor(context, pen, zoom, varistor); break;
            case FixedGainAmplifier amp: DrawFixedGainAmplifier(context, pen, zoom, amp); break;
            case Buzzer buzzer: DrawBuzzer(context, pen, zoom, buzzer); break;
            case ElectrolyticCapacitor e: DrawElectrolytic(context, pen, zoom, e); break;
            case Capacitor: DrawCapacitor(context, pen); break;
            case Inductor: DrawInductor(context, pen); break;
            case Transformer: DrawTransformer(context, pen, thin); break;
            case CentreTappedTransformer: DrawCentreTappedTransformer(context, pen, thin); break;
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

    private static void DrawResistor(ISymbolCanvas context, IPen pen)
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

        context.DrawGeometry(null, pen, SymbolPath.Polyline(points, false));
    }

    /// <summary>
    /// A polarised capacitor: a straight plate on the positive side against a curved one, with a
    /// "+" so the orientation is readable without tracing the wires. Getting an electrolytic the
    /// wrong way round is the mistake the symbol exists to prevent.
    /// </summary>
    private static void DrawElectrolytic(
        ISymbolCanvas context, IPen pen, double zoom, ElectrolyticCapacitor capacitor)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-4, 0));
        context.DrawLine(pen, new Point(6, 0), new Point(30, 0));

        // Positive plate: straight.
        context.DrawLine(pen, new Point(-4, -12), new Point(-4, 12));

        // Negative plate: the curve, bowed towards the positive plate so the pair reads -] (-.
        // Its vertex sits where the lead meets it, at x = 6, and the ends fall away to 10.5.
        var curve = new SymbolPath();
        using (var ctx = curve.Open())
        {
            ctx.BeginFigure(new Point(10.5, -12), false);
            ctx.CubicBezierTo(new Point(4.5, -6), new Point(4.5, 6), new Point(10.5, 12));
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
        ISymbolCanvas context, IPen pen, double zoom, Thermistor thermistor)
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
    private static void DrawBuzzer(ISymbolCanvas context, IPen pen, double zoom, Buzzer buzzer)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-14, 0));
        context.DrawLine(pen, new Point(14, 0), new Point(30, 0));

        // The sounder body: a half-round shell on its side.
        var body = new SymbolPath();
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
                var arc = new SymbolPath();
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
    private static void DrawMotor(ISymbolCanvas context, IPen pen, double zoom, DcMotor motor)
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
    private static void DrawLdr(ISymbolCanvas context, IPen pen, double zoom, LightDependentResistor ldr)
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

            var barb = new SymbolPath();
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
    private static void DrawRelay(ISymbolCanvas context, IPen pen, double zoom, Relay relay)
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
    private static void DrawFuse(ISymbolCanvas context, IPen pen, double zoom, Fuse fuse)
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
    private static void DrawOptocoupler(ISymbolCanvas context, IPen pen, double zoom, Optocoupler opto)
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
        var led = new SymbolPath();
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
        ISymbolCanvas context, IPen pen, double zoom, CircuitComponent component,
        bool latched, bool hasGate)
    {
        context.DrawLine(pen, new Point(-40, 0), new Point(-12, 0));
        context.DrawLine(pen, new Point(12, 0), new Point(40, 0));

        var body = new SymbolPath();
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
    private static void DrawTriac(ISymbolCanvas context, IPen pen, double zoom, Triac triac)
    {
        context.DrawLine(pen, new Point(-40, 0), new Point(-6, 0));
        context.DrawLine(pen, new Point(6, 0), new Point(40, 0));

        var fill = triac.IsLatched ? CanvasTheme.ValueBrush : CanvasTheme.SymbolFill;

        foreach (var sign in new[] { -1.0, 1.0 })
        {
            var arm = new SymbolPath();
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
    private static void DrawDiac(ISymbolCanvas context, IPen pen, double zoom, Diac diac)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-6, 0));
        context.DrawLine(pen, new Point(6, 0), new Point(30, 0));

        var fill = diac.IsLatched ? CanvasTheme.ValueBrush : CanvasTheme.SymbolFill;

        foreach (var sign in new[] { -1.0, 1.0 })
        {
            var arm = new SymbolPath();
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
    /// <summary>
    /// A battery: alternating long and short plates, the oldest symbol in electronics. Two cells
    /// drawn rather than one, which is how a pack is conventionally shown.
    /// </summary>
    /// <summary>
    /// An HC-SR04: the little board with its pair of transducers, one sending and one listening.
    /// The chirp is drawn while the echo pin is up, which is the only time the part is saying
    /// anything.
    /// </summary>
    private static void DrawUltrasonicRanger(
        ISymbolCanvas context, IPen pen, double zoom, UltrasonicRanger sonar)
    {
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-30, -40, 92, 80), 4));

        foreach (var terminal in sonar.Terminals)
        {
            var at = terminal.CanvasOffset;
            context.DrawLine(pen, new Point(at.X, at.Y), new Point(-30, at.Y));

            if (zoom > 0.55)
            {
                DrawCenteredText(context, terminal.Name,
                    new Point(-46, at.Y - 1), 8, zoom, CanvasTheme.LabelBrush);
            }
        }

        // The two cans, side by side as they are on the board. The transmitter lights while it is
        // chirping, which is the only time the part is saying anything.
        var live = sonar.IsEchoing ? CanvasTheme.ValueBrush : null;

        context.DrawEllipse(live, pen, new Point(2, -8), 15, 15);
        context.DrawEllipse(null, pen, new Point(34, -8), 15, 15);

        if (zoom <= 0.4) return;

        // Wavefronts going out, while there is an echo to have come back from.
        if (sonar.IsEchoing)
        {
            var wave = CanvasTheme.Pen(CanvasTheme.ValueBrush, 1.4, zoom);
            for (var i = 1; i <= 3; i++)
                context.DrawEllipse(null, wave, new Point(62, -8), 5 * i, 11 * i);
        }

        DrawCenteredText(context, sonar.IsInRange ? "SR04" : "----",
            new Point(18, 24), 8, zoom, CanvasTheme.LabelBrush);
    }

    private static void DrawBattery(ISymbolCanvas context, IPen pen, double zoom, Battery battery)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-14, 0));
        context.DrawLine(pen, new Point(30, 0), new Point(14, 0));

        // Long plate is positive, short plate negative, and they alternate.
        foreach (var (x, half) in new[] { (-14.0, 14.0), (-6.0, 6.0), (6.0, 14.0), (14.0, 6.0) })
            context.DrawLine(pen, new Point(x, -half), new Point(x, half));

        if (zoom > 0.5)
        {
            // A flat cell is worth seeing without reading the caption.
            var brush = battery.IsFlat ? CanvasTheme.ErrorBrush : CanvasTheme.ValueBrush;
            var filled = Math.Clamp(battery.StateOfCharge, 0.0, 1.0) * 24.0;

            context.DrawRectangle(null, CanvasTheme.Pen(brush, 1.0, zoom),
                new Rect(-12, 20, 24, 6));

            if (filled > 0.5) context.DrawRectangle(brush, null, new Rect(-12, 20, filled, 6));
        }
    }

    /// <summary>
    /// A TVS: two zeners back to back, which is what a bidirectional part is. The bent cathode
    /// bars are the zener marking, and facing them away from each other says it clamps both ways.
    /// </summary>
    private static void DrawTvs(ISymbolCanvas context, IPen pen, double zoom, TransientSuppressor tvs)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-16, 0));
        context.DrawLine(pen, new Point(30, 0), new Point(16, 0));

        var fill = tvs.IsClamping ? CanvasTheme.SymbolBrush : CanvasTheme.SymbolFill;

        foreach (var sign in new[] { -1.0, 1.0 })
        {
            var tip = sign * 2;
            var back = sign * 16;

            var body = new SymbolPath();
            using (var ctx = body.Open())
            {
                ctx.BeginFigure(new Point(back, -12), true);
                ctx.LineTo(new Point(back, 12));
                ctx.LineTo(new Point(tip, 0));
                ctx.EndFigure(true);
            }
            context.DrawGeometry(fill, pen, body);

            // The zener bar, bent at both ends.
            context.DrawLine(pen, new Point(tip, -12), new Point(tip, 12));
            context.DrawLine(pen, new Point(tip, -12), new Point(tip - (sign * 6), -12));
            context.DrawLine(pen, new Point(tip, 12), new Point(tip + (sign * 6), 12));
        }
    }

    /// <summary>A varistor: a resistor body with the diagonal that marks a voltage-dependent part.</summary>
    private static void DrawVaristor(ISymbolCanvas context, IPen pen, double zoom, Varistor varistor)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-18, 0));
        context.DrawLine(pen, new Point(30, 0), new Point(18, 0));

        var brush = varistor.IsWornOut ? CanvasTheme.ErrorBrush : CanvasTheme.SymbolBrush;
        var body = CanvasTheme.Pen(brush, 1.6, zoom);

        context.DrawRectangle(CanvasTheme.SymbolFill, body, new Rect(-18, -11, 36, 22));

        // The diagonal through the body, with the U-shaped kink that says "voltage dependent".
        context.DrawLine(body, new Point(-22, 12), new Point(8, -14));
        context.DrawLine(body, new Point(8, -14), new Point(18, -14));
    }

    /// <summary>A loudspeaker: the coil box and the cone opening out from it.</summary>
    private static void DrawSpeaker(ISymbolCanvas context, IPen pen, double zoom, Speaker speaker)
    {
        context.DrawLine(pen, new Point(-30, -14), new Point(-14, -14));
        context.DrawLine(pen, new Point(-30, 14), new Point(-14, 14));

        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new Rect(-14, -14, 12, 28));

        var cone = new SymbolPath();
        using (var ctx = cone.Open())
        {
            ctx.BeginFigure(new Point(-2, -14), true);
            ctx.LineTo(new Point(16, -26));
            ctx.LineTo(new Point(16, 26));
            ctx.LineTo(new Point(-2, 14));
            ctx.EndFigure(true);
        }
        context.DrawGeometry(CanvasTheme.SymbolFill, pen, cone);

        // Sound coming out of it, drawn only while there is any.
        if (speaker.IsSounding && zoom > 0.4)
        {
            var wave = CanvasTheme.Pen(CanvasTheme.ValueBrush, 1.4, zoom);

            foreach (var r in new[] { 24.0, 32.0 })
            {
                var arc = new SymbolPath();
                using (var ctx = arc.Open())
                {
                    ctx.BeginFigure(new Point(20 + (r * 0.5), -r * 0.7), false);
                    ctx.ArcTo(new Point(20 + (r * 0.5), r * 0.7), new Size(r, r), 0, false,
                        SweepDirection.Clockwise);
                    ctx.EndFigure(false);
                }
                context.DrawGeometry(null, wave, arc);
            }
        }

        if (speaker.Violations.Count > 0 && zoom > 0.4)
        {
            context.DrawEllipse(null, CanvasTheme.Pen(CanvasTheme.ErrorBrush, 2.0, zoom),
                new Point(4, 0), 34, 34);
        }
    }

    /// <summary>
    /// A fixed-gain amplifier: the same triangle an op-amp gets, with its gain written inside
    /// rather than left to a feedback network that is not there.
    /// </summary>
    private static void DrawFixedGainAmplifier(
        ISymbolCanvas context, IPen pen, double zoom, FixedGainAmplifier amp)
    {
        var body = new SymbolPath();
        using (var ctx = body.Open())
        {
            ctx.BeginFigure(new Point(-34, -34), true);
            ctx.LineTo(new Point(-34, 34));
            ctx.LineTo(new Point(38, 0));
            ctx.EndFigure(true);
        }

        var outline = amp.IsClipping ? CanvasTheme.Pen(CanvasTheme.ErrorBrush, 1.6, zoom) : pen;
        context.DrawGeometry(CanvasTheme.SymbolFill, outline, body);

        context.DrawLine(pen, new Point(-50, -20), new Point(-34, -20));
        context.DrawLine(pen, new Point(-50, 20), new Point(-34, 20));
        context.DrawLine(pen, new Point(38, 0), new Point(50, 0));
        context.DrawLine(pen, new Point(0, -45), new Point(0, -22));
        context.DrawLine(pen, new Point(0, 45), new Point(0, 22));

        if (zoom > 0.45)
        {
            DrawCenteredText(context, "+", new Point(-26, -20), 11, zoom, CanvasTheme.LabelBrush);
            DrawCenteredText(context, "\u2212", new Point(-26, 20), 11, zoom, CanvasTheme.LabelBrush);
            // The part number, not the gain: the gain is already the caption underneath, and
            // printing it twice just makes the symbol look like it is stuttering.
            DrawCenteredText(context, amp.ComponentType, new Point(-6, 0), 9, zoom, CanvasTheme.LabelBrush);
        }
    }

    /// <summary>A crystal: the quartz blank between its two electrodes.</summary>
    /// <summary>A thermocouple: two dissimilar wires meeting at a junction bead.</summary>
    private static void DrawThermocouple(
        ISymbolCanvas context, IPen pen, double zoom, Thermocouple tc)
    {
        // The two legs, drawn meeting at a point: the junction is the whole device.
        context.DrawLine(pen, new Point(-30, -12), new Point(10, 0));
        context.DrawLine(pen, new Point(-30, 12), new Point(10, 0));
        context.DrawLine(pen, new Point(10, 0), new Point(30, 0));

        var hot = tc.Temperature > tc.ColdJunctionTemperature + 1.0;
        var bead = hot ? CanvasTheme.ErrorBrush : CanvasTheme.SymbolBrush;

        context.DrawEllipse(bead, pen, new Point(10, 0), 5, 5);

        if (hot && zoom > 0.45)
        {
            // Heat coming off it, so a hot junction is visible without reading the caption.
            var wave = CanvasTheme.Pen(CanvasTheme.ErrorBrush, 1.3, zoom);
            foreach (var dx in new[] { -4.0, 4.0 })
                context.DrawLine(wave, new Point(10 + dx, -12), new Point(10 + dx, -22));
        }
    }

    /// <summary>A load cell: the bridge drawn as the diamond of four gauges it is.</summary>
    private static void DrawLoadCell(ISymbolCanvas context, IPen pen, double zoom, LoadCell cell)
    {
        context.DrawLine(pen, new Point(-40, -30), new Point(-22, -18));
        context.DrawLine(pen, new Point(-40, 30), new Point(-22, 18));
        context.DrawLine(pen, new Point(40, -30), new Point(22, -18));
        context.DrawLine(pen, new Point(40, 30), new Point(22, 18));

        // The diamond: four arms meeting at the four pins.
        var bridge = SymbolPath.Polyline(
            [new Point(0, -26), new Point(26, 0), new Point(0, 26), new Point(-26, 0)], true);

        context.DrawGeometry(CanvasTheme.SymbolFill, pen, bridge);

        if (zoom > 0.45)
        {
            DrawCenteredText(context, "\u2261", new Point(0, 0), 14, zoom, CanvasTheme.LabelBrush);
        }

        if (cell.Violations.Count > 0 && zoom > 0.4)
        {
            context.DrawEllipse(null, CanvasTheme.Pen(CanvasTheme.ErrorBrush, 2.0, zoom),
                new Point(0, 0), 34, 34);
        }
    }

    /// <summary>An encoder: the shaft, with its two contacts shown open or closed.</summary>
    private static void DrawRotaryEncoder(
        ISymbolCanvas context, IPen pen, double zoom, RotaryEncoder encoder)
    {
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 22, 22);
        context.DrawLine(pen, new Point(-40, 0), new Point(-22, 0));

        // The two contact leads, filled while that contact is closed.
        foreach (var (y, closed) in new[] { (-20.0, encoder.IsAClosed), (20.0, encoder.IsBClosed) })
        {
            context.DrawLine(pen, new Point(40, y), new Point(20, y));
            context.DrawEllipse(closed ? CanvasTheme.ValueBrush : null, pen, new Point(18, y), 4, 4);
        }

        // The detent marker, turned to where the shaft has been left.
        if (zoom > 0.4)
        {
            var angle = encoder.Detent * Math.PI / 6.0;
            var mark = encoder.IsTurning
                ? CanvasTheme.Pen(CanvasTheme.ValueBrush, 2.0, zoom)
                : pen;

            context.DrawLine(mark, new Point(0, 0),
                new Point(14 * Math.Sin(angle), -14 * Math.Cos(angle)));
        }
    }

    /// <summary>A charge pump: the package, with the capacitor it shuttles drawn inside it.</summary>
    /// <summary>
    /// A switching controller: the package, with the switch drawn inside it so you can see it
    /// chopping rather than having to infer it from the scope.
    /// </summary>
    private static void DrawSwitchingRegulator(
        ISymbolCanvas context, IPen pen, double zoom, SwitchingRegulator regulator)
    {
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new Rect(-30, -52, 60, 104));

        foreach (var y in new[] { -40.0, -14.0, 14.0, 40.0 })
        {
            context.DrawLine(pen, new Point(-44, y), new Point(-30, y));
            context.DrawLine(pen, new Point(44, y), new Point(30, y));
        }

        if (zoom <= 0.45) return;

        // The switch itself: a contact that lifts when it is off.
        var live = regulator.IsCurrentLimited
            ? CanvasTheme.Pen(CanvasTheme.ErrorBrush, 1.8, zoom)
            : CanvasTheme.Pen(regulator.SwitchIsOn ? CanvasTheme.ValueBrush : CanvasTheme.SymbolBrush, 1.8, zoom);

        context.DrawLine(live, new Point(-16, -18), new Point(-4, -18));
        context.DrawLine(live, new Point(4, -18), new Point(16, -18));
        context.DrawLine(live, new Point(-4, -18),
            regulator.SwitchIsOn ? new Point(4, -18) : new Point(3, -27));

        DrawCenteredText(context, "34063", new Point(0, 8), 9, zoom, CanvasTheme.LabelBrush);
    }

    private static void DrawChargePump(
        ISymbolCanvas context, IPen pen, double zoom, ChargePump pump)
    {
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new Rect(-26, -38, 52, 76));

        foreach (var (x, y) in new[] { (-40.0, -30.0), (-40.0, 30.0) })
            context.DrawLine(pen, new Point(x, y), new Point(-26, y));

        foreach (var y in new[] { -30.0, 0.0, 30.0 })
            context.DrawLine(pen, new Point(40, y), new Point(26, y));

        if (zoom > 0.45)
        {
            // The shuttling capacitor, leaning whichever way the switches are thrown.
            var brush = pump.IsCharging ? CanvasTheme.ValueBrush : CanvasTheme.LabelBrush;
            var plates = CanvasTheme.Pen(brush, 1.6, zoom);
            var x = pump.IsCharging ? -6.0 : 6.0;

            context.DrawLine(plates, new Point(x - 4, -12), new Point(x - 4, 12));
            context.DrawLine(plates, new Point(x + 4, -12), new Point(x + 4, 12));

            DrawCenteredText(context, "7660", new Point(0, 24), 9, zoom, CanvasTheme.LabelBrush);
        }
    }

    /// <summary>A DIP switch: the package with a lever per section, up for on.</summary>
    /// <summary>
    /// A character LCD, showing what has actually been written to it. The point of the part is
    /// that you can read it, so the symbol is the display rather than a box with a part number.
    /// </summary>
    /// <summary>
    /// A package drawn round whatever pins a part happens to have, with a lead to each one.
    /// The generic box is a fixed size, so a part with twelve pins spread over 140 units came out
    /// as a starburst with the leads radiating from a stamp in the middle.
    /// </summary>
    private static Rect DrawPackage(
        ISymbolCanvas context, IPen pen, double zoom, CircuitComponent component, string label)
    {
        // Wide enough that the pin names sit near the edges without running into the part
        // name in the middle, which they did at the first width I tried.
        double halfWidth = 54, halfHeight = 24;

        foreach (var terminal in component.Terminals)
        {
            halfWidth = Math.Max(halfWidth, Math.Abs(terminal.CanvasOffset.X) - 14);
            halfHeight = Math.Max(halfHeight, Math.Abs(terminal.CanvasOffset.Y) + 14);
        }

        var body = new Rect(-halfWidth, -halfHeight, halfWidth * 2, halfHeight * 2);
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(body, 4));

        foreach (var terminal in component.Terminals)
        {
            var at = terminal.CanvasOffset;
            var edge = at.X < 0 ? -halfWidth : halfWidth;

            context.DrawLine(pen, new Point(at.X, at.Y), new Point(edge, at.Y));

            if (zoom > 0.55)
            {
                DrawCenteredText(context, terminal.Name,
                    new Point(edge + (at.X < 0 ? 16 : -16), at.Y - 1), 8, zoom, CanvasTheme.LabelBrush);
            }
        }

        if (zoom > 0.4)
            DrawCenteredText(context, label, new Point(0, 0), 9, zoom, CanvasTheme.LabelBrush);

        return body;
    }

    /// <summary>
    /// The level shifter: a package with a dashed line down the middle, because the whole point of
    /// the part is that the two sides are different voltage domains.
    /// </summary>
    private static void DrawLevelShifter(
        ISymbolCanvas context, IPen pen, double zoom, LevelShifter shifter)
    {
        var body = DrawPackage(context, pen, zoom, shifter, "LEVEL");

        // Broken either side of the caption rather than drawn through it.
        var divider = CanvasTheme.Pen(CanvasTheme.LabelBrush, 1.0, zoom, new DashStyle([3, 3], 0));
        context.DrawLine(divider, new Point(0, body.Top + 8), new Point(0, -16));
        context.DrawLine(divider, new Point(0, 22), new Point(0, body.Bottom - 8));

        if (zoom > 0.45)
            DrawCenteredText(context, "SHIFT", new Point(0, 13), 8, zoom, CanvasTheme.LabelBrush);
    }

    /// <summary>
    /// A Hall switch: the little three-legged package, with field lines coming at its face that
    /// light up once the magnet is close enough to operate it.
    /// </summary>
    private static void DrawHallSensor(
        ISymbolCanvas context, IPen pen, double zoom, HallSensor hall)
    {
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-18, -26, 36, 52), 3));

        context.DrawLine(pen, new Point(-30, -30), new Point(-18, -30));
        context.DrawLine(pen, new Point(-18, -30), new Point(-18, -26));
        context.DrawLine(pen, new Point(-30, 30), new Point(-18, 30));
        context.DrawLine(pen, new Point(-18, 30), new Point(-18, 26));
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));

        if (zoom > 0.45)
            DrawCenteredText(context, "H", new Point(0, 0), 13, zoom, CanvasTheme.LabelBrush);

        if (zoom <= 0.4) return;

        // Field arriving at the face. Lit once it has operated, which is the state that matters.
        var field = CanvasTheme.Pen(
            hall.IsDetecting ? CanvasTheme.ValueBrush : CanvasTheme.LabelBrush, 1.2, zoom);

        for (var i = -1; i <= 1; i++)
            context.DrawLine(field, new Point(-40, i * 13), new Point(-24, i * 13));

        if (hall.Violations.Count > 0)
        {
            context.DrawEllipse(null, CanvasTheme.Pen(CanvasTheme.ErrorBrush, 2.0, zoom),
                new Point(0, 0), 30, 34);
        }
    }

    /// <summary>
    /// A phototransistor: a transistor with no base lead, because light is its base drive, and
    /// arrows coming in to say so. The arrows brighten with the light falling on it.
    /// </summary>
    private static void DrawPhototransistor(
        ISymbolCanvas context, IPen pen, double zoom, Phototransistor photo)
    {
        context.DrawLine(pen, new Point(0, -30), new Point(0, -18));
        context.DrawLine(pen, new Point(0, 30), new Point(0, 18));

        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 20, 20);

        // The bar, and the two slanted leads out to the collector and emitter.
        context.DrawLine(pen, new Point(-8, -12), new Point(-8, 12));
        context.DrawLine(pen, new Point(-8, -6), new Point(0, -18));
        context.DrawLine(pen, new Point(-8, 6), new Point(0, 18));

        // The emitter arrow, which is what says which way round it is.
        var arrow = SymbolPath.Polyline(
            [new Point(-2, 10), new Point(-6, 14), new Point(-7, 7)], true);
        context.DrawGeometry(pen.Brush, pen, arrow);

        if (zoom <= 0.4) return;

        // Light coming in. It brightens as the part is lit, which is the whole input.
        var lit = photo.Illuminance > 50;
        var ray = CanvasTheme.Pen(lit ? CanvasTheme.ValueBrush : CanvasTheme.LabelBrush, 1.2, zoom);

        foreach (var y in new[] { -7.0, 5.0 })
        {
            context.DrawLine(ray, new Point(-38, y - 9), new Point(-24, y + 1));
            context.DrawGeometry(ray.Brush, ray, SymbolPath.Polyline(
                [new Point(-24, y + 1), new Point(-29, y), new Point(-27, y - 5)], true));
        }
    }

    /// <summary>
    /// The bridge as a package, with an arrow between the outputs showing which way it is
    /// driving — the one thing about it you want to see at a glance.
    /// </summary>
    private static void DrawHBridge(
        ISymbolCanvas context, IPen pen, double zoom, HBridge bridge)
    {
        var body = DrawPackage(context, pen, zoom, bridge, "H-BRIDGE");

        if (zoom <= 0.45) return;

        var driving = bridge.State is BridgeState.Forward or BridgeState.Reverse;
        var faulted = bridge.State == BridgeState.ShootThrough;

        var brush = faulted ? CanvasTheme.ErrorBrush
            : driving ? CanvasTheme.ValueBrush
            : CanvasTheme.LabelBrush;

        var mark = CanvasTheme.Pen(brush, 1.6, zoom);

        var y = 22.0;
        var direction = bridge.State == BridgeState.Reverse ? -1 : 1;

        context.DrawLine(mark, new Point(-16, y), new Point(16, y));

        if (driving)
        {
            var tip = new Point(16 * direction, y);
            context.DrawGeometry(brush, mark, SymbolPath.Polyline(
                [tip, new Point(tip.X - (7 * direction), y - 4), new Point(tip.X - (7 * direction), y + 4)],
                true));
        }

        if (faulted)
        {
            context.DrawEllipse(null, CanvasTheme.Pen(CanvasTheme.ErrorBrush, 2.0, zoom),
                new Point(0, 0), body.Width / 2 + 6, body.Height / 2 + 6);
        }
    }

    /// <summary>
    /// A noise source: the usual source circle with a jagged trace in it rather than a waveform,
    /// because that is what it puts out.
    /// </summary>
    private static void DrawNoiseSource(
        ISymbolCanvas context, IPen pen, double zoom, NoiseSource noise)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-18, 0));
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 18, 18);

        if (zoom <= 0.35) return;

        // A fixed jag rather than a random one: a symbol that changed every repaint would be
        // unreadable, and the part is deterministic anyway.
        double[] steps = [0.35, -0.8, 0.55, -0.3, 0.9, -0.65, 0.2];

        List<Point> trace = [new(-11, 0)];

        for (var i = 0; i < steps.Length; i++)
            trace.Add(new Point(-11 + ((i + 1) * 22.0 / steps.Length), steps[i] * 7));

        context.DrawGeometry(null, CanvasTheme.Pen(CanvasTheme.SymbolBrush, 1.3, zoom),
            SymbolPath.Polyline(trace, false));
    }

    private static void DrawCharacterLcd(
        ISymbolCanvas context, IPen pen, double zoom, CharacterLcd lcd)
    {
        var body = new Rect(-110, -46, 220, 92);
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(body, 4));

        // The pin header along the top, as a real module has.
        for (var i = 0; i < 16; i++)
        {
            var x = -100 + (i * 13.3);
            context.DrawLine(pen, new Point(x, -46), new Point(x, -56));
        }

        var glass = new Rect(-92, -30, 184, 60);
        context.DrawRectangle(CanvasTheme.SymbolFill, CanvasTheme.Pen(CanvasTheme.LabelBrush, 1.0, zoom), glass);

        if (zoom <= 0.35) return;

        if (!lcd.DisplayOn)
        {
            DrawCenteredText(context, "HD44780", new Point(0, 0), 10, zoom, CanvasTheme.LabelBrush);
            return;
        }

        // Both lines, always sixteen characters wide, so the text sits where it would on the
        // glass rather than shuffling about as it is written.
        for (var line = 0; line < 2; line++)
        {
            DrawCenteredText(context, lcd.Line(line), new Point(0, -13 + (line * 22)), 13, zoom,
                CanvasTheme.ValueBrush);
        }
    }

    private static void DrawDipSwitch(ISymbolCanvas context, IPen pen, double zoom, DipSwitch dip)
    {
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new Rect(-26, -80, 52, 160));

        for (var i = 0; i < 8; i++)
        {
            var y = -70 + (i * 20);

            context.DrawLine(pen, new Point(-40, y), new Point(-26, y));
            context.DrawLine(pen, new Point(40, y), new Point(26, y));

            if (zoom <= 0.4) continue;

            // The lever: across when the section is closed, tilted away when it is open.
            var closed = dip.IsClosed(i);
            var lever = CanvasTheme.Pen(closed ? CanvasTheme.ValueBrush : CanvasTheme.SymbolBrush, 1.6, zoom);

            context.DrawLine(lever, new Point(-14, y), closed ? new Point(14, y) : new Point(12, y - 6));
        }
    }

    /// <summary>A solar cell: the diode it is, with light arriving at it.</summary>
    private static void DrawSolarCell(ISymbolCanvas context, IPen pen, double zoom, SolarCell pv)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-6, 0));
        context.DrawLine(pen, new Point(30, 0), new Point(8, 0));

        // The junction, drawn as the diode a cell actually is.
        var body = new SymbolPath();
        using (var ctx = body.Open())
        {
            ctx.BeginFigure(new Point(-6, -16), true);
            ctx.LineTo(new Point(-6, 16));
            ctx.LineTo(new Point(8, 0));
            ctx.EndFigure(true);
        }

        context.DrawGeometry(CanvasTheme.SymbolFill, pen, body);
        context.DrawLine(pen, new Point(8, -16), new Point(8, 16));

        if (zoom <= 0.4) return;

        // Light coming in, which is what makes the current.
        var rays = CanvasTheme.Pen(pv.IsLit ? CanvasTheme.ValueBrush : CanvasTheme.LabelBrush, 1.4, zoom);

        foreach (var dx in new[] { -12.0, 2.0 })
        {
            DrawArrowHead(context, rays, new Point(dx - 10, -34), new Point(dx, -20));
            context.DrawLine(rays, new Point(dx - 10, -34), new Point(dx - 4, -26));
        }
    }

    /// <summary>An oscillator can: the package with the wave it produces inside it.</summary>
    private static void DrawOscillatorModule(
        ISymbolCanvas context, IPen pen, double zoom, OscillatorModule osc)
    {
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-28, -30, 56, 60), 4));

        foreach (var (x, y) in new[] { (-40.0, -22.0), (-40.0, 22.0) })
            context.DrawLine(pen, new Point(x, y), new Point(-28, y));

        foreach (var y in new[] { -22.0, 22.0 })
            context.DrawLine(pen, new Point(40, y), new Point(28, y));

        if (zoom <= 0.45) return;

        // A square wave inside, lit while it is actually running.
        var wave = CanvasTheme.Pen(osc.IsRunning ? CanvasTheme.ValueBrush : CanvasTheme.SymbolBrush, 1.6, zoom);

        var path = SymbolPath.Polyline(
        [
            new Point(-16, 6), new Point(-16, -8), new Point(-6, -8), new Point(-6, 6),
            new Point(6, 6), new Point(6, -8), new Point(16, -8), new Point(16, 6),
        ], false);

        context.DrawGeometry(null, wave, path);
    }

    private static void DrawCrystal(ISymbolCanvas context, IPen pen)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-12, 0));
        context.DrawLine(pen, new Point(30, 0), new Point(12, 0));

        // The electrode plates either side of the blank.
        context.DrawLine(pen, new Point(-12, -14), new Point(-12, 14));
        context.DrawLine(pen, new Point(12, -14), new Point(12, 14));

        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new Rect(-7, -16, 14, 32));
    }

    /// <summary>An electret capsule: the can, with the diaphragm drawn across it.</summary>
    private static void DrawMicrophone(
        ISymbolCanvas context, IPen pen, double zoom, Microphone mic)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-16, 0));
        context.DrawLine(pen, new Point(30, 0), new Point(16, 0));

        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 16, 16);
        context.DrawLine(pen, new Point(-16, 0), new Point(16, 0));

        // Sound arriving, drawn only while there is any.
        if (mic.IsHearingSound && zoom > 0.4)
        {
            var wave = CanvasTheme.Pen(CanvasTheme.ValueBrush, 1.4, zoom);

            foreach (var r in new[] { 24.0, 32.0 })
            {
                var arc = new SymbolPath();
                using (var ctx = arc.Open())
                {
                    ctx.BeginFigure(new Point(-(r * 0.5), -r * 0.7), false);
                    ctx.ArcTo(new Point(-(r * 0.5), r * 0.7), new Size(r, r), 0, false,
                        SweepDirection.CounterClockwise);
                    ctx.EndFigure(false);
                }
                context.DrawGeometry(null, wave, arc);
            }
        }

        if (mic.Violations.Count > 0 && zoom > 0.4)
        {
            context.DrawEllipse(null, CanvasTheme.Pen(CanvasTheme.ErrorBrush, 2.0, zoom),
                new Point(0, 0), 24, 24);
        }
    }

    /// <summary>A servo: the case, with the output horn drawn where the shaft actually is.</summary>
    private static void DrawServo(ISymbolCanvas context, IPen pen, double zoom, Servo servo)
    {
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new Rect(-30, -30, 60, 60));

        foreach (var y in new[] { -24.0, 0.0, 24.0 })
            context.DrawLine(pen, new Point(-40, y), new Point(-30, y));

        // The horn, at the angle the shaft has actually reached.
        var hub = new Point(4, 0);
        context.DrawEllipse(null, pen, hub, 13, 13);

        var radians = servo.Angle * Math.PI / 180.0;
        var arm = servo.IsDriven ? CanvasTheme.Pen(CanvasTheme.ValueBrush, 2.0, zoom) : pen;

        context.DrawLine(arm, hub,
            new Point(hub.X + (20 * Math.Sin(radians)), hub.Y - (20 * Math.Cos(radians))));

        if (servo.Violations.Count > 0 && zoom > 0.4)
        {
            context.DrawEllipse(null, CanvasTheme.Pen(CanvasTheme.ErrorBrush, 2.0, zoom),
                new Point(0, 0), 38, 38);
        }
    }

    /// <summary>A stepper: the motor body with its four windings around the rotor.</summary>
    private static void DrawStepper(
        ISymbolCanvas context, IPen pen, double zoom, StepperMotor stepper)
    {
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 30, 30);

        context.DrawLine(pen, new Point(-50, 0), new Point(-30, 0));
        for (var i = 0; i < 4; i++)
            context.DrawLine(pen, new Point(50, -30 + (i * 20)), new Point(26, -30 + (i * 20)));

        // One mark per winding, lit while that winding is carrying current.
        for (var i = 0; i < 4; i++)
        {
            var angle = i * Math.PI / 2.0;
            var at = new Point(20 * Math.Cos(angle), 20 * Math.Sin(angle));
            var live = Math.Max(stepper.CoilCurrent(i), 0.0) >= stepper.HoldingCurrent;

            context.DrawEllipse(live ? CanvasTheme.ValueBrush : null, pen, at, 5, 5);
        }

        // The rotor, pointing where the shaft has actually turned to.
        if (zoom > 0.4)
        {
            var radians = stepper.Angle * Math.PI / 180.0;
            var rotor = stepper.IsSlipping
                ? CanvasTheme.Pen(CanvasTheme.ErrorBrush, 2.0, zoom)
                : CanvasTheme.Pen(CanvasTheme.ValueBrush, 2.0, zoom);

            context.DrawLine(rotor, new Point(0, 0),
                new Point(11 * Math.Cos(radians), 11 * Math.Sin(radians)));
        }
    }

    private static void DrawJfet(ISymbolCanvas context, IPen pen, double zoom, JunctionFet jfet)
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
        ISymbolCanvas context, IPen pen, double zoom, ShuntReference shunt)
    {
        context.DrawLine(pen, new Point(0, -40), new Point(0, -10));
        context.DrawLine(pen, new Point(0, 40), new Point(0, 16));

        // The triangle points from anode up to cathode, as a diode's does.
        var body = new SymbolPath();
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
        var head = new SymbolPath();
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
    private static void DrawBridgeRectifier(ISymbolCanvas context, IPen pen, double zoom)
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

        var body = new SymbolPath();
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
        var arrow = new SymbolPath();
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

    private static void DrawCapacitor(ISymbolCanvas context, IPen pen)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-4, 0));
        context.DrawLine(pen, new Point(4, 0), new Point(30, 0));
        context.DrawLine(pen, new Point(-4, -12), new Point(-4, 12));
        context.DrawLine(pen, new Point(4, -12), new Point(4, 12));
    }

    private static void DrawInductor(ISymbolCanvas context, IPen pen)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-18, 0));
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));

        var geometry = new SymbolPath();
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

    private static void DrawTransformer(ISymbolCanvas context, IPen pen, IPen thin)
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

    private static void DrawWinding(ISymbolCanvas context, IPen pen, double x, double top, double height)
    {
        var geometry = new SymbolPath();
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

    /// <summary>
    /// A centre-tapped transformer: one primary against a secondary drawn as the two windings it
    /// physically is, with the tap coming off the joint between them.
    /// </summary>
    private static void DrawCentreTappedTransformer(ISymbolCanvas context, IPen pen, IPen thin)
    {
        DrawWinding(context, pen, -14, -24, 48);

        // The secondary as two halves, so the tap is visibly the middle of a winding rather than
        // a third terminal hung off an ideal one.
        DrawWinding(context, pen, 14, -32, 32);
        DrawWinding(context, pen, 14, 0, 32);

        // Core.
        context.DrawLine(thin, new Point(-3, -34), new Point(-3, 34));
        context.DrawLine(thin, new Point(3, -34), new Point(3, 34));

        context.DrawLine(pen, new Point(-30, -24), new Point(-14, -24));
        context.DrawLine(pen, new Point(-30, 24), new Point(-14, 24));
        context.DrawLine(pen, new Point(30, -32), new Point(14, -32));
        context.DrawLine(pen, new Point(30, 32), new Point(14, 32));

        // The tap itself, off the joint.
        context.DrawLine(pen, new Point(14, 0), new Point(30, 0));
        context.DrawEllipse(pen.Brush, pen, new Point(14, 0), 2.5, 2.5);
    }

    private static void DrawPotentiometer(ISymbolCanvas context, IPen pen)
    {
        context.DrawLine(pen, new Point(-30, 20), new Point(-18, 20));
        context.DrawLine(pen, new Point(30, 20), new Point(18, 20));
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new Rect(-18, 12, 36, 16));

        // Wiper arrow coming down onto the track.
        context.DrawLine(pen, new Point(0, -30), new Point(0, -6));
        var arrow = SymbolPath.Polyline([new Point(-5, -6), new Point(5, -6), new Point(0, 6)], true);
        context.DrawGeometry(pen.Brush, pen, arrow);
    }

    // ---- sources ---------------------------------------------------------

    private static void DrawGround(ISymbolCanvas context, IPen pen)
    {
        context.DrawLine(pen, new Point(0, -20), new Point(0, -4));
        context.DrawLine(pen, new Point(-14, -4), new Point(14, -4));
        context.DrawLine(pen, new Point(-9, 2), new Point(9, 2));
        context.DrawLine(pen, new Point(-4, 8), new Point(4, 8));
    }

    private static void DrawVoltageSource(ISymbolCanvas context, IPen pen, double zoom)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-18, 0));
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 18, 18);

        // Plus on the left (the + terminal), minus on the right.
        context.DrawLine(pen, new Point(-11, 0), new Point(-5, 0));
        context.DrawLine(pen, new Point(-8, -3), new Point(-8, 3));
        context.DrawLine(pen, new Point(5, 0), new Point(11, 0));
    }

    private static void DrawCurrentSource(ISymbolCanvas context, IPen pen)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-18, 0));
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));
        context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(0, 0), 18, 18);

        context.DrawLine(pen, new Point(-9, 0), new Point(9, 0));
        var head = SymbolPath.Polyline([new Point(9, 0), new Point(2, -5), new Point(2, 5)], true);
        context.DrawGeometry(pen.Brush, pen, head);
    }

    private static void DrawFunctionGenerator(ISymbolCanvas context, IPen pen, FunctionGenerator generator)
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

        context.DrawGeometry(null, pen, SymbolPath.Polyline(points, false));
    }

    // ---- semiconductors --------------------------------------------------

    private static void DrawDiode(ISymbolCanvas context, IPen pen)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-8, 0));
        context.DrawLine(pen, new Point(8, 0), new Point(30, 0));

        var triangle = SymbolPath.Polyline([new Point(-8, -11), new Point(8, 0), new Point(-8, 11)], true);
        context.DrawGeometry(pen.Brush, pen, triangle);
        context.DrawLine(pen, new Point(8, -11), new Point(8, 11));
    }

    private static void DrawLed(ISymbolCanvas context, IPen pen, Led led)
    {
        var emitted = led.EmittedColor;
        var colour = CanvasTheme.Emitted(Color.FromRgb(emitted.R, emitted.G, emitted.B));
        var lit = CanvasTheme.Emission(led.Brightness);

        // The glow, as two overlapping washes rather than one flat disc: light has no edge, and a
        // single circle behind the symbol reads as a coloured sticker stuck to it.
        if (lit > 0)
        {
            context.DrawEllipse(Wash(colour, 0.18 * lit), null, new Point(0, 0), 32, 32);
            context.DrawEllipse(Wash(colour, 0.34 * lit), null, new Point(0, 0), 21, 21);
        }

        context.DrawLine(pen, new Point(-30, 0), new Point(-8, 0));
        context.DrawLine(pen, new Point(8, 0), new Point(30, 0));

        // The body takes the light too, and this is what makes a lit LED obvious at a glance. The
        // triangle is the largest mark in the symbol; leaving it the same dark shape whether the
        // part is conducting or not meant the whole difference between on and off was a faint wash
        // behind it and two arrows a few pixels long.
        //
        // Its outline takes the light as well, in a deeper shade. At the size a symbol is actually
        // drawn on screen the stroke covers a good part of so small a triangle, so leaving it the
        // ordinary symbol colour washed the fill straight back out again.
        var body = new SolidColorBrush(Blend(SymbolColour, colour, lit));
        var edge = lit > 0 ? new Pen(new SolidColorBrush(Deepen(colour)), pen.Thickness) : pen;

        context.DrawGeometry(body, edge,
            SymbolPath.Polyline([new Point(-8, -11), new Point(8, 0), new Point(-8, 11)], true));

        context.DrawLine(pen, new Point(8, -11), new Point(8, 11));

        // Emission arrows. In the symbol colour when the LED is off — they are what says this is an
        // LED rather than a diode, so they cannot simply fade away — and in the emitted colour,
        // slightly thicker, when it is on.
        DrawEmissionArrows(context,
            lit > 0 ? new Pen(new SolidColorBrush(colour), pen.Thickness + 0.5) : pen);
    }

    /// <summary>A deeper shade of an emitted colour, for the edge of something lit by it.</summary>
    private static Color Deepen(Color colour) =>
        Color.FromRgb((byte)(colour.R * 0.55), (byte)(colour.G * 0.55), (byte)(colour.B * 0.55));

    private static void DrawEmissionArrows(ISymbolCanvas context, IPen pen)
    {
        for (var i = 0; i < 2; i++)
        {
            var offset = (i * 8) - 4;
            context.DrawLine(pen, new Point(offset, -14), new Point(offset + 7, -22));
            context.DrawLine(pen, new Point(offset + 7, -22), new Point(offset + 3, -20));
        }
    }

    /// <summary>The emitted colour at a fraction of full opacity, for glows and washes.</summary>
    private static IBrush Wash(Color colour, double opacity) =>
        new SolidColorBrush(Color.FromArgb(
            (byte)Math.Clamp(opacity * 255, 0, 255), colour.R, colour.G, colour.B));

    /// <summary>The theme's symbol stroke as a colour, for blending against.</summary>
    private static Color SymbolColour =>
        CanvasTheme.SymbolBrush is ISolidColorBrush solid ? solid.Color : Colors.Black;

    /// <summary>
    /// A ferrite bead: the inductor symbol with the bead itself drawn over it, because that is
    /// what the part is — a coil of one turn with a lump of ferrite round it. Hatched rather than
    /// filled, which is how a lossy magnetic part is marked on a schematic.
    /// </summary>
    private static void DrawFerriteBead(ISymbolCanvas context, IPen pen)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-16, 0));
        context.DrawLine(pen, new Point(16, 0), new Point(30, 0));

        // The winding underneath, as the inductor's humps.
        var winding = new SymbolPath();
        using (var ctx = winding.Open())
        {
            ctx.BeginFigure(new Point(-16, 0), false);

            for (var i = 0; i < 4; i++)
                ctx.ArcTo(new Point(-16 + ((i + 1) * 8), 0), new Size(4, 4), 0, false, SweepDirection.Clockwise);

            ctx.EndFigure(false);
        }

        context.DrawGeometry(null, pen, winding);

        // The bead: a body over the winding, with the hatching that marks it as lossy.
        var body = new Rect(-16, -9, 32, 18);
        context.DrawRectangle(null, pen, new RoundedRect(body, 2));

        for (var i = -1; i <= 1; i++)
            context.DrawLine(pen, new Point((i * 9) - 4, 9), new Point((i * 9) + 4, -9));
    }

    /// <summary>
    /// A transmission line, drawn as the coax it usually is: an inner conductor running the length
    /// of it inside a screen, rather than as the plain box other tools use. The point of the
    /// component is that the two ends are not the same place, and a symbol you can see a length in
    /// says that better than a rectangle does.
    /// </summary>
    private static void DrawTransmissionLine(
        ISymbolCanvas context, IPen pen, double zoom, TransmissionLine line)
    {
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-38, -16, 76, 32), 16));

        // The inner conductor, end to end.
        context.DrawLine(pen, new Point(-50, -15), new Point(-38, -15));
        context.DrawLine(pen, new Point(-38, -15), new Point(-30, -8));
        context.DrawLine(pen, new Point(-30, -8), new Point(30, -8));
        context.DrawLine(pen, new Point(30, -8), new Point(38, -15));
        context.DrawLine(pen, new Point(38, -15), new Point(50, -15));

        // And the screen, which is the return.
        context.DrawLine(pen, new Point(-50, 15), new Point(-38, 15));
        context.DrawLine(pen, new Point(38, 15), new Point(50, 15));

        if (zoom > 0.5)
        {
            DrawCenteredText(context, $"{line.CharacteristicImpedance:0}Ω",
                new Point(0, 6), 8, zoom, CanvasTheme.LabelBrush);
        }
    }

    /// <summary>
    /// A reed switch: the blades overlapping inside the glass capsule they are sealed in. Drawn
    /// closed or open as it actually is, bounce and all, because watching it chatter is the point
    /// of having one.
    /// </summary>
    private static void DrawReedSwitch(ISymbolCanvas context, IPen pen, double zoom, ReedSwitch reed)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-20, 0));
        context.DrawLine(pen, new Point(20, 0), new Point(30, 0));

        // The capsule.
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-20, -10, 40, 20), 10));

        // The blades. One is fixed and the other springs away from it, so the gap is visible at a
        // glance — which matters here, because watching it open and shut is the point.
        context.DrawLine(pen, new Point(-20, 0), new Point(2, 0));
        context.DrawLine(pen, new Point(20, 0), reed.IsClosed ? new Point(-2, 0) : new Point(-2, -7));

        if (zoom <= 0.4) return;

        // The field arriving, lit once the blades have pulled together.
        var field = CanvasTheme.Pen(
            reed.IsOperated ? CanvasTheme.ValueBrush : CanvasTheme.LabelBrush, 1.2, zoom);

        for (var i = -1; i <= 1; i++)
            context.DrawLine(field, new Point(i * 10, -22), new Point(i * 10, -13));
    }

    /// <summary>
    /// A PIR sensor: the package with the lens dome that is the only part of one you ever see.
    /// The dome fills in while it is triggered.
    /// </summary>
    private static void DrawPirSensor(ISymbolCanvas context, IPen pen, double zoom, PirSensor pir)
    {
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-20, -8, 40, 30), 3));

        context.DrawLine(pen, new Point(-30, -30), new Point(-20, -30));
        context.DrawLine(pen, new Point(-20, -30), new Point(-20, -8));
        context.DrawLine(pen, new Point(-30, 30), new Point(-20, 30));
        context.DrawLine(pen, new Point(-20, 30), new Point(-20, 22));
        context.DrawLine(pen, new Point(20, 0), new Point(30, 0));

        // The lens, which is what a PIR looks like from any distance at all.
        var lens = pir.IsTriggered ? CanvasTheme.ValueBrush : CanvasTheme.SymbolFill;

        var dome = new SymbolPath();
        using (var ctx = dome.Open())
        {
            ctx.BeginFigure(new Point(-16, -8), true);
            ctx.ArcTo(new Point(16, -8), new Size(16, 16), 0, false, SweepDirection.Clockwise);
            ctx.EndFigure(true);
        }

        context.DrawGeometry(lens, pen, dome);

        if (zoom > 0.5)
            DrawCenteredText(context, "PIR", new Point(0, 10), 8, zoom, CanvasTheme.LabelBrush);
    }

    /// <summary>
    /// A gate driver: a buffer triangle, because that is what it is — the same logic function as
    /// a piece of wire, and all of the part is in how hard it does it.
    /// </summary>
    private static void DrawGateDriver(ISymbolCanvas context, IPen pen, double zoom, GateDriver driver)
    {
        context.DrawLine(pen, new Point(-40, 0), new Point(-20, 0));
        context.DrawLine(pen, new Point(20, 0), new Point(40, 0));
        context.DrawLine(pen, new Point(0, -30), new Point(0, -18));
        context.DrawLine(pen, new Point(0, 30), new Point(0, 18));

        var body = SymbolPath.Polyline([new Point(-20, -22), new Point(20, 0), new Point(-20, 22)], true);
        context.DrawGeometry(CanvasTheme.SymbolFill, pen, body);

        if (driver.IsInverting)
            context.DrawEllipse(CanvasTheme.SymbolFill, pen, new Point(24, 0), 4, 4);

        // The rail it swings the gate to, which is the half of the part a triangle cannot say.
        if (zoom > 0.5)
        {
            DrawCenteredText(context, $"{driver.SupplyVoltage:0.#}V",
                new Point(-6, 0), 8, zoom, CanvasTheme.LabelBrush);
        }
    }

    private static void DrawRegulator(ISymbolCanvas context, IPen pen, double zoom, VoltageRegulator regulator)
    {
        context.DrawLine(pen, new Point(-40, 0), new Point(-26, 0));
        context.DrawLine(pen, new Point(26, 0), new Point(40, 0));
        context.DrawLine(pen, new Point(0, 30), new Point(0, 18));
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-26, -18, 52, 36), 3));

        DrawCenteredText(context, regulator.Model.Name, new Point(0, 0), 9, zoom, CanvasTheme.LabelBrush);
    }

    private static void DrawOpAmp(ISymbolCanvas context, IPen pen, double zoom)
    {
        context.DrawLine(pen, new Point(-40, 20), new Point(-26, 20));
        context.DrawLine(pen, new Point(-40, -20), new Point(-26, -20));
        context.DrawLine(pen, new Point(26, 0), new Point(40, 0));
        context.DrawLine(pen, new Point(0, -30), new Point(0, -18));
        context.DrawLine(pen, new Point(0, 30), new Point(0, 18));

        var body = SymbolPath.Polyline([new Point(-26, -30), new Point(26, 0), new Point(-26, 30)], true);
        context.DrawGeometry(CanvasTheme.SymbolFill, pen, body);

        // Input polarity marks: non-inverting is the lower pin.
        context.DrawLine(pen, new Point(-22, 20), new Point(-14, 20));
        context.DrawLine(pen, new Point(-18, 16), new Point(-18, 24));
        context.DrawLine(pen, new Point(-22, -20), new Point(-14, -20));
    }

    // ---- transistors -----------------------------------------------------

    private static void DrawBipolar(ISymbolCanvas context, IPen pen, BipolarTransistor bjt)
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

    private static void DrawMosfet(ISymbolCanvas context, IPen pen, Mosfet mosfet)
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
    private static void DrawArrowHead(ISymbolCanvas context, IPen pen, Point from, Point to)
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

        var head = SymbolPath.Polyline(
        [
            new Point(to.X, to.Y),
            new Point(baseX - uy * halfWidth, baseY + ux * halfWidth),
            new Point(baseX + uy * halfWidth, baseY - ux * halfWidth),
        ], true);

        context.DrawGeometry(pen.Brush, pen, head);
    }

    // ---- switches --------------------------------------------------------

    private static void DrawToggleSwitch(ISymbolCanvas context, IPen pen, ToggleSwitch sw)
    {
        context.DrawLine(pen, new Point(-30, 0), new Point(-14, 0));
        context.DrawLine(pen, new Point(14, 0), new Point(30, 0));

        context.DrawEllipse(pen.Brush, null, new Point(-14, 0), 3.5, 3.5);
        context.DrawEllipse(pen.Brush, null, new Point(14, 0), 3.5, 3.5);

        // The lever lies flat when closed and lifts away from the far contact when open.
        var end = sw.IsClosed ? new Point(14, 0) : new Point(12, -16);
        context.DrawLine(pen, new Point(-14, 0), end);
    }

    private static void DrawPushButton(ISymbolCanvas context, IPen pen, PushButton button)
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

    private static void DrawSpdtSwitch(ISymbolCanvas context, IPen pen, SpdtSwitch sw)
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

    private static void DrawComparator(ISymbolCanvas context, IPen pen, double zoom)
    {
        context.DrawLine(pen, new Point(-40, 20), new Point(-26, 20));
        context.DrawLine(pen, new Point(-40, -20), new Point(-26, -20));
        context.DrawLine(pen, new Point(26, 0), new Point(40, 0));
        context.DrawLine(pen, new Point(0, -30), new Point(0, -18));
        context.DrawLine(pen, new Point(0, 30), new Point(0, 18));

        var body = SymbolPath.Polyline(
            [new Point(-26, -30), new Point(26, 0), new Point(-26, 30)], true);
        context.DrawGeometry(CanvasTheme.SymbolFill, pen, body);

        context.DrawLine(pen, new Point(-22, 20), new Point(-14, 20));
        context.DrawLine(pen, new Point(-18, 16), new Point(-18, 24));
        context.DrawLine(pen, new Point(-22, -20), new Point(-14, -20));

        // A step glyph inside the triangle, which is what separates a comparator symbol from an
        // op-amp at a glance.
        context.DrawGeometry(null, pen, SymbolPath.Polyline(
        [
            new Point(-12, 8), new Point(-2, 8), new Point(-2, -8), new Point(8, -8),
        ], false));
    }

    // ---- seven-segment display -------------------------------------------

    private static void DrawSevenSegment(
        ISymbolCanvas context, IPen pen, double zoom, SevenSegmentDisplay display)
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
        var lit = CanvasTheme.Emitted(Color.FromRgb(colour.R, colour.G, colour.B));

        // From the theme rather than written in here. The hard-coded value this replaces was the
        // dark theme's, so on a light canvas the unlit segments were drawn in a colour meant to
        // sit on a dark one.
        var dark = CanvasTheme.SegmentUnlit;

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
        var dp = CanvasTheme.Emission(display.SegmentBrightness[7]);

        if (dp > 0)
            context.DrawEllipse(Wash(lit, 0.3 * dp), null, new Point(34, 50), 8, 8);

        context.DrawEllipse(new SolidColorBrush(Blend(dark, lit, dp)), null, new Point(34, 50), 5, 5);
    }

    private static void DrawSegment(
        ISymbolCanvas context, SevenSegmentDisplay display, int index,
        Point[] shape, Color lit, Color dark)
    {
        var glowing = CanvasTheme.Emission(display.SegmentBrightness[index]);
        var path = SymbolPath.Polyline(shape, true);

        // A lit element bleeds a little past its own edge, which is what a real display does and
        // what separates a segment that is on from one that is merely a slightly different grey.
        if (glowing > 0)
            context.DrawGeometry(null, new Pen(Wash(lit, 0.3 * glowing), 5.0), path);

        context.DrawGeometry(new SolidColorBrush(Blend(dark, lit, glowing)), null, path);
    }

    /// <summary>
    /// Mixes between the unlit and lit colours. Opacity arrives faster than the hue does: a
    /// half-transparent red on a white sheet washes out to pink, where a dim LED should read as a
    /// darker red, so anything lit at all is drawn nearly solid and the colour carries how hard.
    /// </summary>
    private static Color Blend(Color dark, Color lit, double amount)
    {
        var t = Math.Clamp(amount, 0, 1);
        var opacity = Math.Clamp(t * 2.5, 0, 1);

        return Color.FromArgb(
            (byte)(dark.A + ((255 - dark.A) * opacity)),
            (byte)(dark.R + ((lit.R - dark.R) * t)),
            (byte)(dark.G + ((lit.G - dark.G) * t)),
            (byte)(dark.B + ((lit.B - dark.B) * t)));
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

    private static void DrawDip(ISymbolCanvas context, IPen pen, double zoom, int pinCount, string partNumber)
    {
        var height = DipPackage.BodyHeight(pinCount);
        var body = new Rect(-DipPackage.HalfWidth + 10, -height / 2, (DipPackage.HalfWidth - 10) * 2, height);
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(body, 3));

        // Pin 1 notch on the top edge.
        var notch = new SymbolPath();
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

        // The triangle reaches 34 either side of centre, so the default would sit both captions
        // inside the body.
        FixedGainAmplifier => 46.0,

        // A servo's case and a stepper's body are both 30 either side of centre, which is exactly
        // where the default puts the captions.
        Servo or StepperMotor => 44.0,

        // The ranger's board reaches 40 either side, the centre-tapped secondary 32.
        UltrasonicRanger => 54.0,
        CentreTappedTransformer => 48.0,

        // The bridge diamond reaches 26, the charge pump's package 38, the controller's 52.
        LoadCell => 40.0,
        ChargePump => 52.0,
        SwitchingRegulator => 66.0,
        DipSwitch => 94.0,
        CharacterLcd => 72.0,

        // These are drawn as packages sized to their own pins, so the caption has to clear them.
        Pcf8574 => 98.0,
        Ads1115 => 62.0,
        HBridge => 62.0,
        LithiumCharger => 50.0,
        HallSensor => 42.0,
        PirSensor => 42.0,
        GateDriver => 42.0,

        // The screen reaches 50 either side and the caption sits under the body.
        TransmissionLine => 62.0,
        ReedSwitch => 34.0,
        Ds18b20 or OneWireMaster => 56.0,

        // The leads reach 30, so the default offset puts the caption on top of one.
        Phototransistor => 44.0,
        I2cDevice or SpiMaster or UartPort => 56.0,

        // Eleven pins reaching 52 either side of centre.
        LevelShifter => 84.0,
        OscillatorModule => 44.0,
        Ne555 => DipPackage.BodyHeight(8) / 2 + 16,
        DigitalIc ic => DipPackage.BodyHeight(ic.PinCount) / 2 + 16,
        _ => 30.0,
    };

    /// <summary>
    /// A development board: an outline with every header pin labelled. Pin names are printed
    /// inside the body rather than outside it, because the outside is where the wires go and a
    /// forty-pin header leaves no room for both.
    /// </summary>
    private static void DrawBoard(ISymbolCanvas context, IPen pen, double zoom, DeveloperBoard board)
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
        ISymbolCanvas context, IPen pen, double zoom, BoardProfile profile,
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
        ISymbolCanvas context, string text, Point anchor, double screenSize, double zoom,
        IBrush brush, bool alignLeft)
    {
        using (context.PushTransform(
                   Matrix.CreateScale(1 / zoom, 1 / zoom) * Matrix.CreateTranslation(anchor.X, anchor.Y)))
        {
            context.DrawText(text, default, screenSize, brush,
                alignLeft ? SymbolTextAlign.Left : SymbolTextAlign.Right);
        }
    }

    // ---- logic -----------------------------------------------------------

    private static void DrawLogicGate(ISymbolCanvas context, IPen pen, LogicGate gate)
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
                    SymbolPath.Polyline([new Point(-20, -18), new Point(bodyRight, 0), new Point(-20, 18)], true));
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

    private static void DrawAndBody(ISymbolCanvas context, IPen pen, double right)
    {
        var geometry = new SymbolPath();
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

    private static void DrawOrBody(ISymbolCanvas context, IPen pen, double right, double backX)
    {
        var geometry = new SymbolPath();
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

    private static void DrawOrArc(ISymbolCanvas context, IPen pen, double x)
    {
        var geometry = new SymbolPath();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x, -20), false);
            ctx.CubicBezierTo(new Point(x + 12, -10), new Point(x + 12, 10), new Point(x, 20));
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, pen, geometry);
    }

    private static void DrawToggle(ISymbolCanvas context, IPen pen, double zoom, LogicToggle toggle)
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

    private static void DrawClock(ISymbolCanvas context, IPen pen)
    {
        context.DrawLine(pen, new Point(18, 0), new Point(30, 0));
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-20, -16, 38, 32), 4));

        // A couple of square-wave cycles inside the body.
        var points = new List<Point>
        {
            new(-14, 6), new(-14, -6), new(-6, -6), new(-6, 6),
            new(2, 6), new(2, -6), new(10, -6), new(10, 6), new(13, 6),
        };
        context.DrawGeometry(null, pen, SymbolPath.Polyline(points, false));
    }

    private static void DrawBridge(ISymbolCanvas context, IPen pen, double zoom, string label)
    {
        context.DrawLine(pen, new Point(-40, 0), new Point(-24, 0));
        context.DrawLine(pen, new Point(24, 0), new Point(40, 0));
        context.DrawRectangle(CanvasTheme.SymbolFill, pen, new RoundedRect(new Rect(-24, -18, 48, 36), 3));
        DrawCenteredText(context, label, new Point(0, 0), 11, zoom, CanvasTheme.LabelBrush);
    }

    private static void DrawGenericBox(ISymbolCanvas context, IPen pen, double zoom, CircuitComponent component)
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
        ISymbolCanvas context, string text, Point center, double screenSize, double zoom, IBrush brush)
    {
        using (context.PushTransform(
                   Matrix.CreateScale(1 / zoom, 1 / zoom) * Matrix.CreateTranslation(center.X, center.Y)))
        {
            context.DrawText(text, default, screenSize, brush, SymbolTextAlign.Centre);
        }
    }
}
