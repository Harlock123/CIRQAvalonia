using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Topology;

namespace Cirq.UI.ViewModels;

/// <summary>
/// The examples for the 0.43 logic families: an ALU, a comparator, a one-shot, a counter with its
/// own display driver, a shift-and-store display and a stereo selector.
/// </summary>
public static partial class Examples
{
    /// <summary>How far apart the rows of a toggle or lamp column are: room for each part's name and value.</summary>
    private const double RowPitch = 80;

    /// <summary>
    /// A column of logic toggles, each output carrying a net label so the part it drives can sit
    /// anywhere — which keeps a chip with two dozen pins readable.
    /// </summary>
    private static LogicToggle[] ToggleColumn(
        Circuit circuit, double x, double y, LogicLevels levels, params (string Name, bool High)[] toggles) =>
        [.. toggles.Select((t, i) =>
        {
            var toggle = Place(circuit, new LogicToggle(t.High) { Levels = levels }, x, y + (i * RowPitch));
            Tag(circuit, toggle.Out, t.Name);
            return toggle;
        })];

    /// <summary>
    /// A column of LEDs, each behind its 330 Ω and fed from a net label a short wire away — the gap
    /// is where a probe's flag goes — with the cathodes run down one line to a ground at the foot.
    /// Returns each row's label, to probe.
    /// </summary>
    private static NetLabel[] LampColumn(Circuit circuit, double x, double y, params (string Name, string Colour)[] lamps)
    {
        var ground = Place(circuit, new Ground(), x + 190, y + (lamps.Length * RowPitch) - 20);

        return [.. lamps.Select((lamp, i) =>
        {
            var row = y + (i * RowPitch);
            var label = LabelAt(circuit, lamp.Name, x - 100, row, 180);
            var resistor = Place(circuit, new Resistor(330), x, row);
            circuit.Connect(label.Pin, resistor.A);

            var led = Place(circuit, Led.OfColour(lamp.Colour), x + 120, row);
            circuit.Connect(resistor.B, led.Anode);

            var back = circuit.Connect(led.Cathode, ground.Pin);
            back.Waypoints.Add(new Point(x + 190, row));
            return label;
        })];
    }

    /// <summary>A compact net label standing on its own, its pin at (x, y), pointing <paramref name="direction"/>.</summary>
    private static NetLabel LabelAt(Circuit circuit, string name, double x, double y, int direction)
    {
        var rotation = direction % 360;
        var label = new NetLabel(name) { Compact = true, RotationDegrees = rotation };
        var offset = label.Pin.CanvasOffset.Rotate(rotation);

        label.X = Math.Round(x - offset.X);
        label.Y = Math.Round(y - offset.Y);
        circuit.Add(label);
        return label;
    }

    /// <summary>A ground with a GND label standing on it, for pins tagged GND to tie to.</summary>
    private static void GroundLabel(Circuit circuit, double x, double y)
    {
        var ground = Place(circuit, new Ground(), x, y);
        Tag(circuit, ground.Pin, "GND", 270);
    }

    /// <summary>A 5 V supply with its negative grounded, its positive labelled VCC.</summary>
    private static DcVoltageSource Supply(Circuit circuit, double x, double y)
    {
        var supply = Place(circuit, new DcVoltageSource(5.0), x, y);
        supply.RotationDegrees = 90;
        var ground = Place(circuit, new Ground(), x, y + 70);
        circuit.Connect(supply.Negative, ground.Pin);
        Tag(circuit, supply.Positive, "VCC", 270);
        return supply;
    }

    /// <summary>Powers a chip from the VCC label and a ground under it.</summary>
    private static void Power(Circuit circuit, DigitalIc ic)
    {
        Tag(circuit, ic.Vcc, "VCC");

        var at = ic.Gnd.AbsolutePosition;
        var ground = Place(circuit, new Ground(), at.X + (ic.Gnd.RotatedOffset.X < 0 ? -40 : 40), at.Y + 40);
        var wire = circuit.Connect(ic.Gnd, ground.Pin);
        wire.Waypoints.Add(new Point(ground.Pin.AbsolutePosition.X, at.Y));
    }

    /// <summary>
    /// A 74181 set to add: A = 9 and B = 5 on toggles, S3-S0 = 1001 and M low (arithmetic), Cn high
    /// (no carry in — active low in this convention). F reads 1110, fourteen; Cn+4 stays high, no carry
    /// out. Set M high and the same S is the logic function A XNOR B; S = 0110 with M low and Cn high
    /// subtracts, and the A=B light comes on when the two are equal.
    /// </summary>
    public static void LoadAlu(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "74181 four-bit ALU";

        Supply(circuit, 600, -330);
        var alu = Place(circuit, new Ic74181(), 0, 0);
        Power(circuit, alu);

        for (var i = 0; i < 4; i++)
        {
            Tag(circuit, alu.A[i], $"A{i}");
            Tag(circuit, alu.B[i], $"B{i}");
            Tag(circuit, alu.Select[i], $"S{i}");
            Tag(circuit, alu.F[i], $"F{i}");
        }

        Tag(circuit, alu.CarryIn, "CN");
        Tag(circuit, alu.Mode, "M");
        Tag(circuit, alu.CarryOut, "CN+4");
        Tag(circuit, alu.Equal, "A=B");

        // A=B is open collector: a pull-up gives it a high to show.
        var pullUp = Place(circuit, new Resistor(1e3), 200, -230);
        pullUp.RotationDegrees = 90;
        Tag(circuit, pullUp.A, "VCC", 270);
        Tag(circuit, pullUp.B, "A=B", 90);

        const int a = 9;
        const int b = 5;

        ToggleColumn(circuit, -600, -300, LogicLevels.Ttl,
            [.. Enumerable.Range(0, 4).Select(i => ($"A{i}", (a >> i & 1) == 1)),
             .. Enumerable.Range(0, 4).Select(i => ($"B{i}", (b >> i & 1) == 1))]);

        ToggleColumn(circuit, -380, -220, LogicLevels.Ttl,
            ("S0", true), ("S1", false), ("S2", false), ("S3", true), ("M", false), ("CN", true));

        var lamps = LampColumn(circuit, 420, -200,
            ("F0", "Green"), ("F1", "Green"), ("F2", "Green"), ("F3", "Green"), ("CN+4", "Red"), ("A=B", "Yellow"));

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(lamps[0].Pin, "F0");
        vm.Scope.AddProbe(lamps[3].Pin, "F3");
        vm.Scope.AddProbe(lamps[4].Pin, "Cn+4");
    }

    /// <summary>
    /// A 7485 comparing A = 9 with B = 5: A&gt;B lights. Its cascade inputs are set as the first of a
    /// chain — A=B in high, the other two low — so equal words light A=B.
    /// </summary>
    public static void LoadComparator(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "7485 magnitude comparator";

        Supply(circuit, 480, -300);
        GroundLabel(circuit, -220, 260);
        var comparator = Place(circuit, new Ic7485(), 0, 0);
        Power(circuit, comparator);

        for (var i = 0; i < 4; i++)
        {
            Tag(circuit, comparator.A[i], $"A{i}");
            Tag(circuit, comparator.B[i], $"B{i}");
        }

        Tag(circuit, comparator.GreaterIn, "GND");
        Tag(circuit, comparator.LessIn, "GND");
        Tag(circuit, comparator.EqualIn, "VCC");
        Tag(circuit, comparator.Greater, "A>B");
        Tag(circuit, comparator.Equal, "A=B");
        Tag(circuit, comparator.Less, "A<B");

        const int a = 9;
        const int b = 5;

        ToggleColumn(circuit, -480, -280, LogicLevels.Ttl,
            [.. Enumerable.Range(0, 4).Select(i => ($"A{i}", (a >> i & 1) == 1)),
             .. Enumerable.Range(0, 4).Select(i => ($"B{i}", (b >> i & 1) == 1))]);

        var lamps = LampColumn(circuit, 360, -70, ("A>B", "Green"), ("A=B", "Yellow"), ("A<B", "Red"));

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(lamps[0].Pin, "A>B");
        vm.Scope.AddProbe(lamps[1].Pin, "A=B");
        vm.Scope.AddProbe(lamps[2].Pin, "A<B");
    }

    /// <summary>
    /// A 4538 stretching short trigger pulses to R·C = 1 ms (10 kΩ, 100 nF). A pattern generator sends
    /// 100 µs pulses every 10 ms: a lone one at 1 ms, a burst of five 300 µs apart from 4 ms, and
    /// another lone one at 8 ms. Each lone pulse becomes exactly a millisecond; the burst holds Q high
    /// throughout, because each trigger restarts the timing, and Q falls a millisecond after the last —
    /// which is how a missing-pulse detector works. The scope shows the trigger, the timing capacitor
    /// discharging and recharging, and Q.
    /// </summary>
    public static void LoadPulseStretcher(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "4538 pulse stretcher";

        Supply(circuit, 360, -300);
        GroundLabel(circuit, 200, 230);

        var trigger = Place(circuit, new PatternGenerator()
        {
            Pattern = new string('0', 10) + "1" + new string('0', 29) + string.Concat(Enumerable.Repeat("100", 5))
                      + new string('0', 25) + "1" + new string('0', 19),
            StepRate = 10e3,
            Levels = LogicLevels.Cmos5V,
        }, -420, 120);

        var oneShot = Place(circuit, new Ic4538(), 0, 0);
        Power(circuit, oneShot);

        var (triggers, reset, q, _, cx, rxcx) = oneShot.Pins(0);
        var triggerLabel = LabelAt(circuit, "TRIG", -280, 120, 360);
        circuit.Connect(trigger.Terminals[0], triggerLabel.Pin);
        Tag(circuit, triggers[0], "TRIG");
        Tag(circuit, triggers[1], "VCC");
        Tag(circuit, reset!, "VCC");
        Tag(circuit, rxcx, "RX");
        Tag(circuit, cx, "CX");

        // The timing parts, as the datasheet draws them: R from VDD to RX/CX, C from RX/CX to CX.
        var r = Place(circuit, new Resistor(10e3), -380, -200);
        r.RotationDegrees = 90;
        Tag(circuit, r.A, "VCC", 270);
        var c = Place(circuit, new Capacitor(100e-9), -380, -90);
        c.RotationDegrees = 90;
        Tag(circuit, c.B, "CX", 90);
        var timing = LabelAt(circuit, "RX", -300, -145, 360);
        var join = circuit.Connect(r.B, timing.Pin);
        join.Waypoints.Add(new Point(-380, -145));
        circuit.Connect(r.B, c.A);

        // The second half unused, its inputs tied off: a floating CMOS input decides for itself.
        var (triggers2, reset2, _, _, _, _) = oneShot.Pins(1);
        Tag(circuit, triggers2[0], "GND");
        Tag(circuit, triggers2[1], "VCC");
        Tag(circuit, reset2!, "GND");

        Tag(circuit, q, "Q");
        var lamp = LampColumn(circuit, -300, 260, ("Q", "Yellow"));

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(triggerLabel.Pin, "+TR");
        vm.Scope.AddProbe(timing.Pin, "RX/CX");
        vm.Scope.AddProbe(lamp[0].Pin, "Q");
    }

    /// <summary>
    /// Two 4026s counting a 1 kHz clock — about one a second at the editor's usual speed — from 00 to
    /// 99 on two common-cathode displays, the units' carry out clocking the tens. Each 4026 is the
    /// counter, the decoder and the segment drivers; its outputs are CMOS ones of a few hundred ohms,
    /// so the segments hang straight off them as the datasheet draws it.
    /// </summary>
    public static void LoadTwoDigitCounter(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "Two 4026s counting to 99";

        Supply(circuit, -520, -300);
        GroundLabel(circuit, -520, 260);

        var clock = Place(circuit, new ClockSource(1e3) { Levels = LogicLevels.Cmos5V }, -520, -100);
        var clockLabel = LabelAt(circuit, "CLK", -420, -100, 360);
        circuit.Connect(clock.Out, clockLabel.Pin);

        var tens = Place(circuit, new Ic4026(), -200, 0);
        var units = Place(circuit, new Ic4026(), 340, 0);
        var tensDisplay = Place(circuit, new SevenSegmentDisplay(commonAnode: false), 40, 0);
        var unitsDisplay = Place(circuit, new SevenSegmentDisplay(commonAnode: false), 580, 0);

        foreach (var (ic, display, digit) in new[] { (tens, tensDisplay, "T"), (units, unitsDisplay, "U") })
        {
            Power(circuit, ic);
            Tag(circuit, ic.ClockInhibit, "GND");
            Tag(circuit, ic.Reset, "GND");
            Tag(circuit, ic.DisplayEnableIn, "VCC");

            // Segment by segment through labels: the chip's pins are on both sides and so are the display's.
            for (var i = 0; i < 7; i++)
            {
                var name = $"{digit}{(char)('a' + i)}";
                Tag(circuit, ic.Segments[i], name);
                Tag(circuit, display.Segments[i], name);
            }

            var common = Place(circuit, new Ground(), display.Common.AbsolutePosition.X, display.Common.AbsolutePosition.Y + 40);
            circuit.Connect(display.Common, common.Pin);
        }

        Tag(circuit, units.Clock, "CLK");
        Tag(circuit, units.CarryOut, "CARRY");
        Tag(circuit, tens.Clock, "CARRY");
        var carryLabel = LabelAt(circuit, "CARRY", -420, 0, 360);

        vm.Scope.TimebasePerDivision = 5e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(clockLabel.Pin, "CLK");
        vm.Scope.AddProbe(carryLabel.Pin, "CARRY");
    }

    /// <summary>
    /// A pattern generator sending bytes into a 4094 over three wires — data, clock and strobe — and
    /// eight LEDs on its outputs. The bits shift in with the strobe low, so the LEDs hold the last byte
    /// while the next one arrives; the strobe pulse at the end of each byte copies it to the outputs at
    /// once, the way a 74595's latch clock does.
    /// </summary>
    public static void LoadSerialDisplay(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "4094 shift-and-store display";

        Supply(circuit, -480, -300);

        byte[] bytes = [0b1000_0001, 0b0100_0010, 0b0010_0100, 0b0001_1000, 0b1111_0000, 0b0000_1111];
        var (data, clock, strobe) = ShiftPattern(bytes);

        var source = Place(circuit, new PatternGenerator()
        {
            Pattern = $"{data}\n{clock}\n{strobe}",
            StepRate = 10e3,
            Levels = LogicLevels.Cmos5V,
        }, -480, -80);

        var register = Place(circuit, new Ic4094(), 0, 0);
        Power(circuit, register);

        // Each generator line out to a label of its own, spaced for a probe's flag.
        string[] lines = ["DATA", "CLK", "STR"];
        var probes = new NetLabel[3];
        for (var i = 0; i < 3; i++)
        {
            var pin = source.Terminals[i].AbsolutePosition;
            probes[i] = LabelAt(circuit, lines[i], -300, -150 + (i * RowPitch), 360);
            var wire = circuit.Connect(source.Terminals[i], probes[i].Pin);
            wire.Waypoints.Add(new Point(-400 + (i * 20), pin.Y));
            wire.Waypoints.Add(new Point(-400 + (i * 20), -150 + (i * RowPitch)));
        }

        Tag(circuit, register.Data, "DATA");
        Tag(circuit, register.Clock, "CLK");
        Tag(circuit, register.Strobe, "STR");
        Tag(circuit, register.OutputEnable, "VCC");

        for (var i = 0; i < 8; i++) Tag(circuit, register.Outputs[i], $"Q{i + 1}");

        var lamps = LampColumn(circuit, 360, -280, [.. Enumerable.Range(1, 8).Select(i => ($"Q{i}", "Red"))]);

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 2.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(probes[0].Pin, "DATA");
        vm.Scope.AddProbe(probes[1].Pin, "CLK");
        vm.Scope.AddProbe(probes[2].Pin, "STR");
        vm.Scope.AddProbe(lamps[0].Pin, "Q1");
    }

    /// <summary>
    /// The three lines of a shift-and-store transfer for some bytes, most significant bit first: each
    /// bit is two steps with the clock rising on the second, then a strobe pulse and a pause.
    /// </summary>
    internal static (string Data, string Clock, string Strobe) ShiftPattern(IEnumerable<byte> bytes)
    {
        var data = new System.Text.StringBuilder();
        var clock = new System.Text.StringBuilder();
        var strobe = new System.Text.StringBuilder();

        foreach (var value in bytes)
        {
            for (var bit = 7; bit >= 0; bit--)
            {
                var d = (value >> bit & 1) == 1 ? '1' : '0';
                data.Append(d).Append(d);
                clock.Append("01");
                strobe.Append("00");
            }

            data.Append("0000000000");
            clock.Append("0000000000");
            strobe.Append("0110000000");
        }

        return (data.ToString(), clock.ToString(), strobe.ToString());
    }

    /// <summary>
    /// A 4052 as a stereo input selector: two sources, each a left and a right channel at different
    /// frequencies, on X0/Y0 and X1/Y1; the two address toggles pick a pair, and X and Y carry the
    /// chosen left and right together. Flip A and both outputs change source at once — the point of a
    /// dual multiplexer with one address over a pair of single ones.
    /// </summary>
    public static void LoadStereoSelector(MainWindowViewModel vm)
    {
        var circuit = vm.Circuit;
        circuit.Title = "4052 stereo selector";

        Supply(circuit, 600, -330);
        GroundLabel(circuit, -260, 260);
        var mux = Place(circuit, new Ic4052(), 0, 0);
        Power(circuit, mux);
        Tag(circuit, mux.Inhibit, "GND");
        Tag(circuit, mux.NegativeSupply, "GND");

        // Source 1 on channel 0, source 2 on channel 1; channels 2 and 3 tied to mid-rail, silence.
        (string Name, double Frequency, int Section, int Channel)[] sources =
        [
            ("1L", 500, 0, 0), ("1R", 750, 1, 0), ("2L", 2e3, 0, 1), ("2R", 3e3, 1, 1),
        ];

        for (var i = 0; i < sources.Length; i++)
        {
            var (name, frequency, section, channel) = sources[i];
            var x = -560 + (i * 170);
            var generator = Place(circuit, new FunctionGenerator(Waveform.Sine, frequency, 2.0) { DcOffset = 2.5 }, x, -260);
            generator.RotationDegrees = 90;
            var ground = Place(circuit, new Ground(), x, -160);
            circuit.Connect(generator.Return, ground.Pin);
            Tag(circuit, generator.Output, name, 270);
            Tag(circuit, mux.ChannelOf(section, channel), name);
        }

        var mid = Place(circuit, new DcVoltageSource(2.5), -420, 140);
        mid.RotationDegrees = 90;
        var midGround = Place(circuit, new Ground(), -420, 210);
        circuit.Connect(mid.Negative, midGround.Pin);
        Tag(circuit, mid.Positive, "MID", 270);
        foreach (var (section, channel) in new[] { (0, 2), (0, 3), (1, 2), (1, 3) })
            Tag(circuit, mux.ChannelOf(section, channel), "MID");

        ToggleColumn(circuit, -600, 0, LogicLevels.Cmos5V, ("A", false), ("B", false));
        Tag(circuit, mux.A, "A");
        Tag(circuit, mux.B, "B");

        // Each output into its own load, as an amplifier's input would be.
        var outputs = new NetLabel[2];
        for (var s = 0; s < 2; s++)
        {
            var name = s == 0 ? "LEFT" : "RIGHT";
            var x = 360 + (s * 170);
            Tag(circuit, mux.CommonOf(s), name);
            var load = Place(circuit, new Resistor(100e3), x, 40);
            load.RotationDegrees = 90;
            outputs[s] = LabelAt(circuit, name, x, -40, 270);
            circuit.Connect(outputs[s].Pin, load.A);
            var ground = Place(circuit, new Ground(), x, 110);
            circuit.Connect(load.B, ground.Pin);
        }

        vm.Scope.TimebasePerDivision = 1e-3;
        vm.Scope.VoltsPerDivision = 1.0;
        vm.Scope.Layout = ScopeLayout.Tiled;
        vm.Scope.AddProbe(outputs[0].Pin, "Left");
        vm.Scope.AddProbe(outputs[1].Pin, "Right");
    }
}
