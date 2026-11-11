using System.Text.RegularExpressions;
using Cirq.Components.Boards;
using Cirq.Components.Digital;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Components.Protoboard;

/// <summary>How a part goes onto the board.</summary>
public enum BoardForm
{
    /// <summary>Not a thing on the bench at all: a ground symbol, a net label, a note, a block's outline.</summary>
    None,

    /// <summary>Equipment beside the boards (a supply, a generator, a dev board, a motor), joined by leads.</summary>
    Bench,

    /// <summary>Two leads into two holes, anywhere they reach: resistors, capacitors, diodes, LEDs.</summary>
    TwoLead,

    /// <summary>A row of pins in one row of neighbouring columns: a transistor, a regulator, a trimmer, a module's header.</summary>
    Inline,

    /// <summary>A dual in-line package straddling the centre channel.</summary>
    Dip,
}

/// <summary>What a part looks like on the board, for the drawing.</summary>
public enum BoardLook
{
    Generic,
    Resistor,
    Ceramic,
    Disc,
    Electrolytic,
    Inductor,
    Ferrite,
    Diode,
    Led,
    Crystal,
    Fuse,
    Button,
    Switch,
    Ldr,
    Thermistor,
    Buzzer,
    Microphone,
    To92,
    To220,
    Trimmer,
    SlideSwitch,
    Header,
    Dip,
    SevenSegment,
    DipSwitch,
    Supply,
    Generator,
    Instrument,
    DevBoard,
    Display,
    Keypad,
    Motor,
    Speaker,
    Lamp,
    Transformer,
    LedStrip,
    Sensor,
}

/// <summary>
/// How one part is built on a breadboard: its form, its look, and which pin goes where.
/// </summary>
/// <param name="Form">How it goes onto the board.</param>
/// <param name="Look">What it is drawn as.</param>
/// <param name="Pins">
/// The pins in order. For a DIP, pin 1 first, with null for a pin that isn't connected inside; for an inline part,
/// left to right as you look at its face; for a two-lead part, its two leads (anode first for a diode); for bench
/// equipment, the order its leads are drawn in.
/// </param>
/// <param name="Span">A DIP's width in pitches: 3 for the usual 0.3 inch, 6 for 0.6, 9 for a 64-pin 0.9.</param>
/// <param name="Label">What's printed on it.</param>
public sealed record BoardPackage(BoardForm Form, BoardLook Look, IReadOnlyList<Terminal?> Pins, int Span, string Label)
{
    /// <summary>How far apart a two-lead part's leads go, in pitches: the shortest, the usual and the longest.</summary>
    public (double Min, double Usual, double Max) Reach => Look switch
    {
        BoardLook.Resistor or BoardLook.Diode or BoardLook.Inductor or BoardLook.Fuse or BoardLook.Ferrite => (3, 4, 8),
        BoardLook.Led => (1, 1, 3),
        BoardLook.Electrolytic => (1, 2, 3),
        BoardLook.Ceramic or BoardLook.Disc or BoardLook.Ldr or BoardLook.Thermistor => (1, 2, 4),
        BoardLook.Crystal or BoardLook.Switch or BoardLook.Microphone => (1, 2, 3),
        BoardLook.Button => (2, 2, 4),
        BoardLook.Buzzer => (2, 3, 4),
        _ => (1, 3, 8),
    };
}

/// <summary>
/// Which package each part in the library is built from on a breadboard, with the pin order from its usual
/// datasheet.
/// <para>
/// A chip whose pins are numbered (<c>p1</c>, <c>p2</c>, …) is a DIP by those numbers. Parts drawn with named pins
/// (an op-amp's IN+, a transistor's C, B and E) get the package they're normally bought in from the tables here:
/// a 741 is an 8-pin DIP with IN− on pin 2, a 2N3904 a TO-92 reading E, B, C. Anything the tables don't know is
/// built as a small module on a row of header pins, in the order its pins are drawn. Sources, instruments,
/// motors and dev boards sit on the bench beside the board, as they do on a real one.
/// </para>
/// </summary>
public static partial class BoardPackages
{
    [GeneratedRegex(@"^p(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedPin();

    /// <summary>Equipment that is wired to the board rather than plugged into it, by type name.</summary>
    private static readonly Dictionary<string, BoardLook> BenchTypes = new(StringComparer.Ordinal)
    {
        ["DcVoltageSource"] = BoardLook.Supply,
        ["DcCurrentSource"] = BoardLook.Supply,
        ["Battery"] = BoardLook.Supply,
        ["SolarCell"] = BoardLook.Supply,
        ["FunctionGenerator"] = BoardLook.Generator,
        ["WaveformSource"] = BoardLook.Generator,
        ["NoiseSource"] = BoardLook.Generator,
        ["ClockSource"] = BoardLook.Generator,
        ["PatternGenerator"] = BoardLook.Generator,
        ["LogicToggle"] = BoardLook.Generator,
        ["VoltageControlledVoltageSource"] = BoardLook.Instrument,
        ["VoltageControlledCurrentSource"] = BoardLook.Instrument,
        ["CurrentControlledVoltageSource"] = BoardLook.Instrument,
        ["CurrentControlledCurrentSource"] = BoardLook.Instrument,
        ["BehaviouralSource"] = BoardLook.Instrument,
        ["I2cMaster"] = BoardLook.Instrument,
        ["SpiMaster"] = BoardLook.Instrument,
        ["SerialTerminal"] = BoardLook.Instrument,
        ["SerialDevice"] = BoardLook.Instrument,
        ["OneWireMaster"] = BoardLook.Instrument,
        ["AdcBridge"] = BoardLook.Instrument,
        ["DacBridge"] = BoardLook.Instrument,
        ["TransmissionLine"] = BoardLook.Instrument,
        ["Transformer"] = BoardLook.Transformer,
        ["CentreTappedTransformer"] = BoardLook.Transformer,
        ["DcMotor"] = BoardLook.Motor,
        ["Servo"] = BoardLook.Motor,
        ["StepperMotor"] = BoardLook.Motor,
        ["Speaker"] = BoardLook.Speaker,
        ["IncandescentLamp"] = BoardLook.Lamp,
        ["NeoPixelStrip"] = BoardLook.LedStrip,
        ["LoadCell"] = BoardLook.Sensor,
        ["Thermocouple"] = BoardLook.Sensor,
        ["CharacterLcd"] = BoardLook.Display,
        ["MatrixKeypad"] = BoardLook.Keypad,
    };

    /// <summary>Two-lead parts and what they look like, by type name.</summary>
    private static readonly Dictionary<string, BoardLook> TwoLeadTypes = new(StringComparer.Ordinal)
    {
        ["Resistor"] = BoardLook.Resistor,
        ["Capacitor"] = BoardLook.Ceramic,
        ["ElectrolyticCapacitor"] = BoardLook.Electrolytic,
        ["Inductor"] = BoardLook.Inductor,
        ["FerriteBead"] = BoardLook.Ferrite,
        ["Crystal"] = BoardLook.Crystal,
        ["Diode"] = BoardLook.Diode,
        ["Varactor"] = BoardLook.Diode,
        ["TransientSuppressor"] = BoardLook.Diode,
        ["Diac"] = BoardLook.Diode,
        ["Varistor"] = BoardLook.Disc,
        ["Led"] = BoardLook.Led,
        ["Photodiode"] = BoardLook.Led,
        ["Phototransistor"] = BoardLook.Led,
        ["Fuse"] = BoardLook.Fuse,
        ["ResettableFuse"] = BoardLook.Disc,
        ["PushButton"] = BoardLook.Button,
        ["ToggleSwitch"] = BoardLook.Switch,
        ["ReedSwitch"] = BoardLook.Switch,
        ["LightDependentResistor"] = BoardLook.Ldr,
        ["Thermistor"] = BoardLook.Thermistor,
        ["Buzzer"] = BoardLook.Buzzer,
        ["Microphone"] = BoardLook.Microphone,
    };

    /// <summary>DIP pinouts for parts drawn with named pins: pin count, then terminal id by pin number.</summary>
    private static readonly Dictionary<string, (int Pins, Dictionary<int, string> Map)> NamedDips = new(StringComparer.OrdinalIgnoreCase)
    {
        // Single op-amps and comparators in the 741's pinout.
        ["LM741"] = (8, new() { [2] = "in-", [3] = "in+", [4] = "v-", [6] = "out", [7] = "v+" }),
        ["TL081"] = (8, new() { [2] = "in-", [3] = "in+", [4] = "v-", [6] = "out", [7] = "v+" }),
        ["TLV3501"] = (8, new() { [2] = "in-", [3] = "in+", [4] = "v-", [6] = "out", [7] = "v+" }),
        // Duals, the first half used.
        ["LM358"] = (8, new() { [1] = "out", [2] = "in-", [3] = "in+", [4] = "v-", [8] = "v+" }),
        ["MCP6002"] = (8, new() { [1] = "out", [2] = "in-", [3] = "in+", [4] = "v-", [8] = "v+" }),
        ["LM393"] = (8, new() { [1] = "out", [2] = "in-", [3] = "in+", [4] = "v-", [8] = "v+" }),
        ["LM311"] = (8, new() { [2] = "in+", [3] = "in-", [4] = "v-", [7] = "out", [8] = "v+" }),
        ["LM386"] = (8, new() { [2] = "in-", [3] = "in+", [4] = "v-", [5] = "out", [6] = "v+" }),
        ["INA126"] = (8, new() { [2] = "in-", [3] = "in+", [4] = "v-", [5] = "ref", [6] = "out", [7] = "v+" }),
        ["AD633"] = (8, new() { [1] = "x1", [2] = "x2", [3] = "y1", [4] = "y2", [5] = "v-", [6] = "z", [7] = "w", [8] = "v+" }),
        ["MC34063"] = (8, new() { [1] = "swc", [2] = "swe", [3] = "ct", [4] = "gnd", [5] = "fb", [6] = "vcc", [7] = "ipk", [8] = "drc" }),
        ["ICL7660"] = (8, new() { [2] = "cap+", [3] = "gnd", [4] = "cap-", [5] = "out", [8] = "v+" }),
        ["MCP2551"] = (8, new() { [1] = "txd", [2] = "gnd", [3] = "vcc", [4] = "rxd", [6] = "canl", [7] = "canh" }),
        ["MAX485"] = (8, new() { [1] = "ro", [2] = "re", [3] = "de", [4] = "di", [5] = "gnd", [6] = "a", [7] = "b", [8] = "vcc" }),
        ["TC4420"] = (8, new() { [1] = "vcc", [2] = "in", [4] = "gnd", [6] = "out" }),
        ["PC817"] = (4, new() { [1] = "a", [2] = "k", [3] = "e", [4] = "c" }),
        ["4N35"] = (6, new() { [1] = "a", [2] = "k", [4] = "e", [5] = "c" }),
        ["24LC256"] = (8, new() { [4] = "gnd", [5] = "sda", [6] = "scl", [8] = "vcc" }),
        ["DS1307"] = (8, new() { [4] = "gnd", [5] = "sda", [6] = "scl", [8] = "vcc" }),
        ["PCF8574"] = (16, new() { [4] = "p0", [5] = "p1", [6] = "p2", [7] = "p3", [8] = "gnd", [9] = "p4", [10] = "p5", [11] = "p6", [12] = "p7", [14] = "scl", [15] = "sda", [16] = "vcc" }),
        ["L293D"] = (16, new() { [1] = "en", [2] = "in1", [3] = "out1", [4] = "gnd", [6] = "out2", [7] = "in2", [8] = "vm", [16] = "vcc" }),
        ["OSC"] = (8, new() { [1] = "en", [4] = "gnd", [5] = "out", [8] = "vcc" }),
        ["7SEG"] = (10, new() { [1] = "s4", [2] = "s3", [3] = "com", [4] = "s2", [5] = "s7", [6] = "s1", [7] = "s0", [9] = "s5", [10] = "s6" }),
    };

    /// <summary>Three-legged parts: which package, and their legs left to right, by model.</summary>
    private static readonly Dictionary<string, (BoardLook Look, string[] Legs)> Legged = new(StringComparer.OrdinalIgnoreCase)
    {
        ["2N3904"] = (BoardLook.To92, ["e", "b", "c"]),
        ["2N3906"] = (BoardLook.To92, ["e", "b", "c"]),
        ["2N2222"] = (BoardLook.To92, ["e", "b", "c"]),
        ["BC547"] = (BoardLook.To92, ["c", "b", "e"]),
        ["TIP31C"] = (BoardLook.To220, ["b", "c", "e"]),
        ["TIP120"] = (BoardLook.To220, ["b", "c", "e"]),
        ["2N7000"] = (BoardLook.To92, ["s", "g", "d"]),
        ["IRLZ44N"] = (BoardLook.To220, ["g", "d", "s"]),
        ["IRF9540"] = (BoardLook.To220, ["g", "d", "s"]),
        ["IRG4BC30"] = (BoardLook.To220, ["g", "c", "e"]),
    };

    /// <summary>The board package for <paramref name="part"/>.</summary>
    public static BoardPackage For(CircuitComponent part)
    {
        ArgumentNullException.ThrowIfNull(part);

        if (part is IAnnotation or INetNaming or ISubcircuit or Ground or LoopProbe || part.Terminals.Count == 0)
            return new BoardPackage(BoardForm.None, BoardLook.Generic, [], 0, "");

        var type = part.GetType().Name;
        var label = Label(part);

        if (part is DeveloperBoard)
            return Bench(part, BoardLook.DevBoard, label);

        if (BenchTypes.TryGetValue(type, out var bench))
            return Bench(part, bench, label);

        if (TwoLeadTypes.TryGetValue(type, out var look) && part.Terminals.Count == 2)
            return new BoardPackage(BoardForm.TwoLead, look, [part.Terminals[0], part.Terminals[1]], 0, label);

        // Numbered pins: a DIP by its numbers.
        if (part.Terminals.All(t => NumberedPin().IsMatch(t.Id)))
        {
            var numbers = part.Terminals.ToDictionary(t => int.Parse(NumberedPin().Match(t.Id).Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), t => (Terminal?)t);
            var count = part is DigitalIc ic ? ic.PinCount : numbers.Keys.Max();
            count = Math.Max(count, numbers.Keys.Max());
            if (count % 2 == 1) count++;
            return Dip(part, count, n => numbers.GetValueOrDefault(n), label);
        }

        if (NamedDip(part) is { } named)
            return Dip(part, named.Pins, n => named.Map.TryGetValue(n, out var id) ? Find(part, id) : null, label);

        if (type == "DipSwitch")
        {
            var sections = part.Terminals.Count / 2;
            return Dip(part, sections * 2, n => n <= sections ? Find(part, $"a{n}") : Find(part, $"b{(2 * sections) + 1 - n}"), label, BoardLook.DipSwitch);
        }

        if (part.Terminals.Count == 2)
            return new BoardPackage(BoardForm.TwoLead, BoardLook.Generic, [part.Terminals[0], part.Terminals[1]], 0, label);

        if (Legs(part) is { } legs)
            return new BoardPackage(BoardForm.Inline, legs.Look, legs.Pins, 0, label);

        // Something with named pins the tables don't know: a breakout module on a row of header pins, or a chip on
        // a carrier for the ones with too many pins for one row.
        if (part.Terminals.Count <= 12)
            return new BoardPackage(BoardForm.Inline, BoardLook.Header, [.. part.Terminals], 0, label);

        var pins = part.Terminals.Count + (part.Terminals.Count % 2);
        return Dip(part, pins, n => n <= part.Terminals.Count ? part.Terminals[n - 1] : null, label);
    }

    /// <summary>Whether a two-lead part lies along its leads (a resistor, a diode) rather than standing up on them.</summary>
    public static bool IsAxial(BoardLook look) =>
        look is BoardLook.Resistor or BoardLook.Diode or BoardLook.Inductor or BoardLook.Fuse or BoardLook.Ferrite or BoardLook.Generic or BoardLook.Crystal;

    /// <summary>How far a two-lead part's body reaches from its middle, in pitches, seen from above.</summary>
    public static double BodyRadius(CircuitComponent part, BoardLook look) => look switch
    {
        BoardLook.Electrolytic => part is Passive.Capacitor c ? Math.Clamp(0.75 + (0.22 * Math.Log10(Math.Max(c.Capacitance, 1e-7) / 1e-6)), 0.7, 1.7) : 1.0,
        BoardLook.Buzzer => 1.25,
        BoardLook.Microphone => 0.95,
        BoardLook.Disc => 0.85,
        BoardLook.Button => 0.9,
        _ => 0.65,
    };

    /// <summary>Whether a part goes on the board or the bench at all.</summary>
    public static bool IsBuilt(CircuitComponent part) => For(part).Form != BoardForm.None;

    private static BoardPackage Bench(CircuitComponent part, BoardLook look, string label) =>
        new(BoardForm.Bench, look, [.. part.Terminals], 0, label);

    private static BoardPackage Dip(CircuitComponent part, int pins, Func<int, Terminal?> pin, string label, BoardLook look = BoardLook.Dip)
    {
        var order = Enumerable.Range(1, pins).Select(pin).ToList();

        // A 0.6 inch package from 24 pins (memories, processors, the 74154), 0.9 for the 64-pin 68000; the 7-segment
        // display's rows are 0.6 inch apart too.
        // The ATmega328P-PU is a "skinny" 28-pin DIP, 0.3 inch like the small chips.
        var span = pins >= 64 ? 9 : part.GetType().Name == "Atmega328p" ? 3 : pins >= 24 || part.GetType().Name == "SevenSegmentDisplay" ? 6 : 3;
        var drawn = part.GetType().Name == "SevenSegmentDisplay" ? BoardLook.SevenSegment : look;
        return new BoardPackage(BoardForm.Dip, drawn, order, span, label);
    }

    private static (int Pins, Dictionary<int, string> Map)? NamedDip(CircuitComponent part)
    {
        var type = part.GetType().Name;
        var key = type switch
        {
            "SevenSegmentDisplay" => "7SEG",
            "OscillatorModule" => "OSC",
            "HBridge" => "L293D",
            "Optocoupler" => part.ValueLabel.Contains("4N35", StringComparison.OrdinalIgnoreCase) ? "4N35" : "PC817",
            "CanTransceiver" => "MCP2551",
            "Rs485Transceiver" => "MAX485",
            "GateDriver" => "TC4420",
            "AnalogMultiplier" => "AD633",
            "SwitchingRegulator" => "MC34063",
            "ChargePump" => "ICL7660",
            "I2cEeprom" => "24LC256",
            "Ds1307" => "DS1307",
            "Pcf8574" => "PCF8574",
            "Lm386" => "LM386",
            "Ina126" => "INA126",
            _ => Model(part),
        };

        if (key is null || !NamedDips.TryGetValue(key, out var found)) return null;

        // Only when every pin the part has is on the package: a table entry is no use to a part that has grown a pin.
        var ids = found.Map.Values.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return part.Terminals.All(t => ids.Contains(t.Id)) ? found : null;
    }

    private static (BoardLook Look, IReadOnlyList<Terminal?> Pins)? Legs(CircuitComponent part)
    {
        if (part.Terminals.Count != 3) return null;
        var type = part.GetType().Name;
        var model = Model(part) ?? "";

        (BoardLook Look, string[] Legs)? known = Legged.TryGetValue(model, out var found) ? found : type switch
        {
            "BipolarTransistor" or "DarlingtonTransistor" => (BoardLook.To92, ["e", "b", "c"]),
            "Mosfet" => (BoardLook.To92, ["s", "g", "d"]),
            "JunctionFet" => (BoardLook.To92, ["d", "s", "g"]),
            "Igbt" => (BoardLook.To220, ["g", "c", "e"]),
            "VoltageRegulator" => (BoardLook.To220, Regulator(part.ValueLabel)),
            "ShuntReference" => (BoardLook.To92, ["r", "a", "k"]),
            "TemperatureSensorIc" => (BoardLook.To92, ["vs", "vout", "gnd"]),
            "SiliconControlledRectifier" => (BoardLook.To92, ["k", "g", "a"]),
            "Triac" => (BoardLook.To220, ["mt1", "mt2", "g"]),
            "Potentiometer" => (BoardLook.Trimmer, ["a", "w", "b"]),
            "SpdtSwitch" => (BoardLook.SlideSwitch, ["a", "com", "b"]),
            "RotaryEncoder" => (BoardLook.Header, ["a", "com", "b"]),
            "HallSensor" => (BoardLook.To92, ["vcc", "gnd", "out"]),
            "Ds18b20" => (BoardLook.To92, ["gnd", "dq", "vdd"]),
            _ => null,
        };

        if (known is not { } legs) return null;
        var pins = legs.Legs.Select(id => Find(part, id)).ToList();
        return pins.All(p => p is not null) && pins.Distinct().Count() == 3 ? (legs.Look, pins) : null;
    }

    /// <summary>A 78xx reads IN, GND, OUT; a 79xx GND, IN, OUT; an LM317 ADJ, OUT, IN; an LD1117 GND, OUT, IN.</summary>
    private static string[] Regulator(string label) =>
        label.Contains("79", StringComparison.Ordinal) ? ["com", "in", "out"]
        : label.Contains("317", StringComparison.Ordinal) || label.Contains("1117", StringComparison.Ordinal) ? ["com", "out", "in"]
        : ["in", "com", "out"];

    private static Terminal? Find(CircuitComponent part, string id) =>
        part.Terminals.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The model name a part shows (2N3904, LM741), the first word of its value.</summary>
    private static string? Model(CircuitComponent part)
    {
        var value = part.ValueLabel.Trim();
        if (value.Length == 0) return null;
        return value.Split(' ', '(')[0];
    }

    /// <summary>What a part has printed on it: a chip's number, a transistor's model, a resistor's value.</summary>
    private static string Label(CircuitComponent part) =>
        part is DigitalIc ic ? ic.PartNumber
        : part.ValueLabel.Length > 0 ? part.ValueLabel
        : part.ComponentType;
}
