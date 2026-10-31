using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Cirq.Core.Units;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Cirq.Mcp;

/// <summary>
/// Saved recipes, served as MCP prompts: short step-by-step plans for circuits agents are often asked for, written with
/// the real tool names, parts, pins and values. Each step that calls a tool shows the call on a line of its own,
/// "→ tool {arguments}", so an agent can follow it as it is (and the tests run every recipe through the server, call by
/// call, so a wrong pin name in one fails the build). Prompt arguments come as text, as MCP clients send them; numbers
/// may be written in engineering notation ("1k", "4.7u").
/// </summary>
[McpServerPromptType]
public static class Recipes
{
    /// <summary>The marker that starts a step's tool call in a recipe's text.</summary>
    public const string CallMarker = "→ ";

    [McpServerPrompt(Name = "rc_low_pass", Title = "RC low-pass filter")]
    [Description("Builds a first-order RC low-pass filter for a corner frequency, driven by a square wave, then shows the capacitor charging in time and the response in frequency.")]
    public static string RcLowPass(
        [Description("The corner (−3 dB) frequency in Hz (default 1k).")] string? cutoff_hz = null)
    {
        var f = Number(cutoff_hz, 1000, "cutoff_hz", 0.1, 1e7);
        const double r = 10e3;
        var c = 1 / (2 * Math.PI * r * f);
        var drive = f / 3;
        var recipe = new Recipe($"Build an RC low-pass filter with its corner at {Si(f, "Hz")}: a 10 kΩ resistor in series and a {Si(c, "F")} capacitor to ground (f = 1 / 2πRC).");
        recipe.Step("Start a new circuit. If it refuses because the open one has unsaved changes, save that first, or ask the person.", "new_circuit", new { title = $"RC low-pass at {Si(f, "Hz")}" });
        recipe.Step($"Place the parts: a function generator making a 0 to 4 V square wave at {Si(drive, "Hz")} (a third of the corner, so the capacitor has time to charge) standing on the left with its OUT pin on top, the resistor across the top, the capacitor standing on the right, and a ground under each.", "add_parts", new
        {
            parts = new object[]
            {
                new { kind = "Function Generator", x = -160, y = 40, rotation = 90, settings = new Dictionary<string, object> { ["Shape"] = "Square", ["Frequency"] = Si(drive), ["AmplitudePeakToPeak"] = 4, ["DcOffset"] = 2 } },
                new { kind = "Resistor", x = -40, y = -20, rotation = 0, settings = new Dictionary<string, object> { ["Resistance"] = "10k" } },
                new { kind = "Capacitor", x = 60, y = 40, rotation = 90, settings = new Dictionary<string, object> { ["Capacitance"] = Si(c) } },
                new { kind = "Ground", x = -160, y = 140, rotation = 0 },
                new { kind = "Ground", x = 60, y = 140, rotation = 0 },
            },
        });
        recipe.Step("Wire the generator through the resistor to the capacitor, and both returns to ground.", "connect", new
        {
            wires = new[] { new[] { "FG1.OUT", "R1.A" }, ["R1.B", "C1.A"], ["C1.B", "GND2.GND"], ["FG1.GND", "GND1.GND"] },
        });
        recipe.Step("Probe the input and the output.", "add_probe", new { pin = "R1.A", label = "Input" });
        recipe.Step(null, "add_probe", new { pin = "C1.A", label = "Output" });
        recipe.Step("Look at the drawing.", "render_schematic", new { });
        recipe.Step("Run three cycles: the output should rise and fall in curves, not follow the square's corners.", "run_transient", new { duration = Si(3 / drive) });
        recipe.Step($"Sweep the frequency: the output's corner_minus_3db should be close to {Si(f, "Hz")}, falling 20 dB a decade above it.", "ac_sweep", new { start_hz = Si(f / 100), stop_hz = Si(f * 100) });
        return recipe.Text("Say what the corner came out at against the target. To move it, change C1's Capacitance (or R1's Resistance) with set_part and sweep again. Save it with save_circuit if the person wants to keep it.");
    }

    [McpServerPrompt(Name = "led_with_resistor", Title = "LED and its resistor")]
    [Description("Lights an LED from a DC supply through the resistor that sets its current, and checks the current with the operating point.")]
    public static string LedWithResistor(
        [Description("The supply in volts (default 5).")] string? supply_volts = null,
        [Description("The LED current in milliamps (default 10).")] string? current_ma = null,
        [Description("red, amber, yellow, green, blue or white (default red).")] string? colour = null)
    {
        var v = Number(supply_volts, 5, "supply_volts", 3, 48);
        var ma = Number(current_ma, 10, "current_ma", 1, 30);
        var shade = Choice(colour, "red", "colour", "red", "amber", "yellow", "green", "blue", "white");
        var drop = shade is "blue" or "white" ? 3.0 : 2.0;
        var r = (v - drop) / (ma / 1000);
        if (r <= 0) throw new McpException($"A {shade} LED drops about {drop} V, more than a {v} V supply gives it.");
        var recipe = new Recipe($"Light a {shade} LED at about {ma:0.#} mA from {v:0.##} V: the resistor takes what the LED doesn't drop, R = ({v:0.##} V − {drop:0.#} V) / {ma:0.#} mA ≈ {Si(r, "Ω")}.");
        recipe.Step("Start a new circuit (save the open one first if it has changes).", "new_circuit", new { title = $"{char.ToUpperInvariant(shade[0])}{shade[1..]} LED from {v:0.##} V" });
        recipe.Step("Place the supply standing on the left with + on top, the resistor across the top, the LED standing on the right with its anode on top, and grounds below.", "add_parts", new
        {
            parts = new object[]
            {
                new { kind = "DC Voltage", x = -160, y = 40, rotation = 90, settings = new Dictionary<string, object> { ["Voltage"] = v } },
                new { kind = "Resistor", x = -40, y = -20, rotation = 0, settings = new Dictionary<string, object> { ["Resistance"] = Si(r) } },
                new { kind = $"LED {char.ToUpperInvariant(shade[0])}{shade[1..]}", x = 60, y = 40, rotation = 90 },
                new { kind = "Ground", x = -160, y = 140, rotation = 0 },
                new { kind = "Ground", x = 60, y = 140, rotation = 0 },
            },
        });
        recipe.Step(null, "connect", new { wires = new[] { new[] { "V1.+", "R1.A" }, ["R1.B", "LED1.A"], ["LED1.K", "GND2.GND"], ["V1.-", "GND1.GND"] } });
        recipe.Step("Probe the LED's current, and the voltage across it.", "add_probe", new { pin = "LED1.A", label = "LED current", kind = "current" });
        recipe.Step(null, "add_probe", new { pin = "LED1.A", label = "LED voltage" });
        recipe.Step($"Solve the bias point: the LED current should read close to {ma:0.#} mA.", "operating_point", new { });
        recipe.Step("Look at the drawing.", "render_schematic", new { });
        return recipe.Text("Report the current and the LED's forward voltage. If the current is off, adjust R1's Resistance with set_part. In the app, transport run lights the LED on the canvas.");
    }

    [McpServerPrompt(Name = "astable_555", Title = "555 astable oscillator")]
    [Description("Builds an NE555 astable multivibrator for a frequency, with an LED on its output, and runs it to measure the frequency and duty cycle.")]
    public static string Astable555(
        [Description("The frequency in Hz (default 1k).")] string? frequency_hz = null)
    {
        var f = Number(frequency_hz, 1000, "frequency_hz", 0.1, 100e3);
        var c = f < 20 ? 10e-6 : f < 2000 ? 100e-9 : 10e-9;
        const double ra = 1e3;
        var rb = (1.44 / (f * c) - ra) / 2;
        if (rb < 100) throw new McpException("That frequency is too high for this recipe's values; try 100 kHz or less.");
        var recipe = new Recipe($"Build a 555 astable at about {Si(f, "Hz")}: f = 1.44 / ((RA + 2·RB)·C), with RA = 1 kΩ, RB = {Si(rb, "Ω")} and C = {Si(c, "F")}. The output is high while C charges through RA and RB, and low while it discharges through RB.");
        recipe.Step("Start a new circuit (save the open one first if it has changes).", "new_circuit", new { title = $"555 astable at {Si(f, "Hz")}" });
        recipe.Step("Place the 555 in the middle, a 9 V supply on the left, RA, RB and the timing capacitor stacked down the right beside DISCH and THRES, the 10 nF control-voltage capacitor under CTRL, the LED and its resistor on the output (left), and grounds.", "add_parts", new
        {
            parts = new object[]
            {
                new { kind = "NE555", x = 0, y = 0, rotation = 0 },
                new { kind = "DC Voltage", x = -280, y = 0, rotation = 90, settings = new Dictionary<string, object> { ["Voltage"] = 9 } },
                new { kind = "Resistor", x = 140, y = -80, rotation = 90, name = "RA", settings = new Dictionary<string, object> { ["Resistance"] = "1k" } },
                new { kind = "Resistor", x = 140, y = 30, rotation = 90, name = "RB", settings = new Dictionary<string, object> { ["Resistance"] = Si(rb) } },
                new { kind = "Capacitor", x = 140, y = 140, rotation = 90, name = "CT", settings = new Dictionary<string, object> { ["Capacitance"] = Si(c) } },
                new { kind = "Capacitor", x = 60, y = 120, rotation = 90, name = "CV", settings = new Dictionary<string, object> { ["Capacitance"] = "10n" } },
                new { kind = "Resistor", x = -140, y = 60, rotation = 90, name = "RL", settings = new Dictionary<string, object> { ["Resistance"] = "680" } },
                new { kind = "LED Red", x = -140, y = 160, rotation = 90 },
                new { kind = "Ground", x = -280, y = 240, rotation = 0 },
                new { kind = "Ground", x = 100, y = 240, rotation = 0 },
            },
        });
        recipe.Step("Wire it: the supply to VCC and RESET (RESET held high keeps it running), VCC through RA to DISCH, DISCH through RB to the capacitor, the capacitor to THRES and TRIG together, CTRL to its capacitor, the output through the resistor to the LED, and every return to ground.", "connect", new
        {
            wires = new[]
            {
                new[] { "V1.+", "U1.VCC" }, ["U1.VCC", "U1.RESET"], ["U1.VCC", "RA.A"], ["RA.B", "U1.DISCH"], ["U1.DISCH", "RB.A"],
                ["RB.B", "CT.A"], ["CT.A", "U1.THRES"], ["U1.THRES", "U1.TRIG"], ["U1.CTRL", "CV.A"], ["U1.OUT", "RL.A"], ["RL.B", "LED1.A"],
                ["V1.-", "GND1.GND"], ["U1.GND", "GND1.GND"], ["LED1.K", "GND1.GND"], ["CT.B", "GND2.GND"], ["CV.B", "GND2.GND"],
            },
        });
        recipe.Step("Probe the timing capacitor and the output.", "add_probe", new { pin = "CT.A", label = "Capacitor" });
        recipe.Step(null, "add_probe", new { pin = "U1.OUT", label = "Output" });
        recipe.Step("Check the wiring.", "check_circuit", new { });
        recipe.Step("Look at the drawing.", "render_schematic", new { });
        recipe.Step($"Run about six cycles (the first is longer: the capacitor starts from 0 V, not a third of the supply). The capacitor should ramp between about 3 V and 6 V, and the output's frequency read near {Si(f, "Hz")}.", "run_transient", new { duration = Si(6 / f), from = Si(1.5 / f) });
        return recipe.Text("Report the measured frequency and duty cycle (above 50 %, since C charges through RA and RB but discharges through RB alone). To trim the frequency, change RB with set_part. In the app, transport run with set_scope (a timebase of about a fifth of a period) lets the person watch it, and the LED blink at low frequencies.");
    }

    [McpServerPrompt(Name = "inverting_amplifier", Title = "Inverting op-amp amplifier")]
    [Description("Builds an LM741 inverting amplifier for a gain on ±15 V rails, drives it with a sine, and checks the gain in time and in frequency.")]
    public static string InvertingAmplifier(
        [Description("The gain, as a positive number (default 10; the output is inverted).")] string? gain = null)
    {
        var g = Number(gain, 10, "gain", 0.1, 1000);
        const double rin = 10e3;
        var rf = g * rin;
        var input = Math.Min(1.0, 20 / g);
        var recipe = new Recipe($"Build an inverting amplifier with a gain of −{g:0.##}: Rin = 10 kΩ, Rf = {Si(rf, "Ω")} (gain = −Rf / Rin), an LM741 on ±15 V, driven by a {Si(input, "V")} peak-to-peak 1 kHz sine.");
        recipe.Step("Start a new circuit (save the open one first if it has changes).", "new_circuit", new { title = $"Inverting amplifier, gain −{g:0.##}" });
        recipe.Step("Place the op-amp in the middle, the signal generator on the left, Rin into IN−, Rf above from IN− round to OUT, the two supplies on the right (one for each rail) and grounds.", "add_parts", new
        {
            parts = new object[]
            {
                new { kind = "LM741", x = 60, y = 0, rotation = 0 },
                new { kind = "Function Generator", x = -300, y = 20, rotation = 90, settings = new Dictionary<string, object> { ["Shape"] = "Sine", ["Frequency"] = "1k", ["AmplitudePeakToPeak"] = input } },
                new { kind = "Resistor", x = -120, y = -20, rotation = 0, name = "RIN", settings = new Dictionary<string, object> { ["Resistance"] = "10k" } },
                new { kind = "Resistor", x = 60, y = -100, rotation = 0, name = "RF", settings = new Dictionary<string, object> { ["Resistance"] = Si(rf) } },
                new { kind = "DC Voltage", x = 260, y = -80, rotation = 90, name = "VP", settings = new Dictionary<string, object> { ["Voltage"] = 15 } },
                new { kind = "DC Voltage", x = 260, y = 80, rotation = 90, name = "VN", settings = new Dictionary<string, object> { ["Voltage"] = 15 } },
                new { kind = "Ground", x = -300, y = 120, rotation = 0 },
                new { kind = "Ground", x = -20, y = 100, rotation = 0 },
                new { kind = "Ground", x = 340, y = 40, rotation = 0 },
            },
        });
        recipe.Step("Wire the signal through RIN to IN−, RF from IN− to OUT, IN+ to ground; VP's + to V+ and its − to ground; VN's + to ground and its − to V− (so V− sits at −15 V).", "connect", new
        {
            wires = new[]
            {
                new[] { "FG1.OUT", "RIN.A" }, ["RIN.B", "U1.IN-"], ["U1.IN-", "RF.A"], ["RF.B", "U1.OUT"], ["U1.IN+", "GND2.GND"],
                ["FG1.GND", "GND1.GND"], ["VP.+", "U1.V+"], ["VP.-", "GND3.GND"], ["VN.+", "GND3.GND"], ["VN.-", "U1.V-"],
            },
        });
        recipe.Step("Probe the input and the output.", "add_probe", new { pin = "RIN.A", label = "Input" });
        recipe.Step(null, "add_probe", new { pin = "U1.OUT", label = "Output" });
        recipe.Step("Look at the drawing.", "render_schematic", new { });
        recipe.Step($"Run three cycles: the output's peak to peak should be about {g:0.##} times the input's, and upside down.", "run_transient", new { duration = "3m" });
        recipe.Step("Sweep the frequency: the output's gain_db_at_start should be about 20·log10(gain), and the corner where the 741's gain-bandwidth runs out.", "ac_sweep", new { start_hz = "10", stop_hz = "1M" });
        return recipe.Text("Report the measured gain against −Rf/Rin and the bandwidth. A bigger gain lowers the bandwidth (the 741 has about 1 MHz of gain-bandwidth), and an output that tries to pass about ±14 V clips at the rails.");
    }

    [McpServerPrompt(Name = "bridge_rectifier", Title = "Bridge rectifier with smoothing")]
    [Description("Builds a mains-style DC supply: an AC source through a transformer into a bridge rectifier, smoothed by a reservoir capacitor into a load, and measures the DC and the ripple.")]
    public static string BridgeRectifier(
        [Description("The AC voltage in volts RMS (default 12).")] string? volts_rms = null,
        [Description("The load in ohms (default 1k).")] string? load_ohms = null)
    {
        var rms = Number(volts_rms, 12, "volts_rms", 3, 240);
        var load = Number(load_ohms, 1000, "load_ohms", 10, 1e6);
        var pp = rms * 2 * Math.Sqrt(2);
        var recipe = new Recipe($"Build a bridge rectifier from {rms:0.#} V RMS at 50 Hz (a {pp:0.#} V peak-to-peak sine) into a 470 µF reservoir and a {Si(load, "Ω")} load. Expect about {rms * Math.Sqrt(2) - 1.4:0.#} V DC (the peak less two diode drops), with ripple at 100 Hz.");
        recipe.Step("Start a new circuit (save the open one first if it has changes).", "new_circuit", new { title = $"Bridge rectifier from {rms:0.#} V AC" });
        recipe.Step("Place the AC source on the left, a 1:1 transformer (it isolates the bridge, so its − can be ground), the bridge, the reservoir capacitor and the load, and grounds.", "add_parts", new
        {
            parts = new object[]
            {
                new { kind = "Function Generator", x = -340, y = 0, rotation = 90, name = "AC", settings = new Dictionary<string, object> { ["Shape"] = "Sine", ["Frequency"] = 50, ["AmplitudePeakToPeak"] = Math.Round(pp, 2) } },
                new { kind = "Transformer", x = -220, y = 0, rotation = 0, settings = new Dictionary<string, object> { ["PrimaryInductance"] = 1, ["SecondaryInductance"] = 1, ["Coupling"] = 0.999 } },
                new { kind = "Bridge Rectifier", x = -80, y = 0, rotation = 0 },
                new { kind = "Electrolytic Cap", x = 60, y = 30, rotation = 90, settings = new Dictionary<string, object> { ["Capacitance"] = "470u", ["VoltageRating"] = 63 } },
                new { kind = "Resistor", x = 160, y = 30, rotation = 90, name = "RLOAD", settings = new Dictionary<string, object> { ["Resistance"] = Si(load) } },
                new { kind = "Ground", x = -340, y = 100, rotation = 0 },
                new { kind = "Ground", x = 60, y = 140, rotation = 0 },
            },
        });
        recipe.Step("Wire the source across the primary, the secondary across the bridge's two ~ pins, the bridge's + to the capacitor (A is its + lead) and the load, and its − to ground.", "connect", new
        {
            wires = new[]
            {
                new[] { "AC.OUT", "T1.P1" }, ["T1.P2", "GND1.GND"], ["AC.GND", "GND1.GND"], ["T1.S1", "BR1.~1"], ["T1.S2", "BR1.~2"],
                ["BR1.+", "C1.A"], ["C1.A", "RLOAD.A"], ["BR1.-", "GND2.GND"], ["C1.B", "GND2.GND"], ["RLOAD.B", "GND2.GND"],
            },
        });
        recipe.Step("Probe the output.", "add_probe", new { pin = "RLOAD.A", label = "DC out" });
        recipe.Step("Look at the drawing.", "render_schematic", new { });
        recipe.Step("Run ten cycles of the mains, reporting after the first three (while the reservoir charges up).", "run_transient", new { duration = "200m", from = "60m" });
        return recipe.Text("Report the mean DC and the ripple (peak to peak) on the output. A bigger capacitor or a lighter load (larger resistance) means less ripple; ripple ≈ I / (2 f C).");
    }

    [McpServerPrompt(Name = "decade_counter", Title = "Decade counter")]
    [Description("Counts clock pulses with a CD4017 decade counter and shows its first four outputs taking turns, as logic traces.")]
    public static string DecadeCounter(
        [Description("The clock frequency in Hz (default 1k).")] string? clock_hz = null)
    {
        var f = Number(clock_hz, 1000, "clock_hz", 1, 1e6);
        var recipe = new Recipe($"Build a CD4017 decade counter clocked at {Si(f, "Hz")}: each rising clock edge moves the one high output on, Q0, Q1, Q2… Q9 and round again.");
        recipe.Step("Start a new circuit (save the open one first if it has changes).", "new_circuit", new { title = "4017 decade counter" });
        recipe.Step("Place the 4017, a 5 V supply on the left, the clock to the right (turned to face it, set to CMOS levels to match the 4017), and grounds.", "add_parts", new
        {
            parts = new object[]
            {
                new { kind = "4017", x = 0, y = 0, rotation = 0 },
                new { kind = "DC Voltage", x = -200, y = 0, rotation = 90, settings = new Dictionary<string, object> { ["Voltage"] = 5 } },
                new { kind = "Clock", x = 160, y = -30, rotation = 180, settings = new Dictionary<string, object> { ["Frequency"] = Si(f), ["Levels"] = "CMOS 5V" } },
                new { kind = "Ground", x = -200, y = 120, rotation = 0 },
                new { kind = "Ground", x = 120, y = 60, rotation = 0 },
            },
        });
        recipe.Step("Wire the supply to VDD and VSS, the clock to CLK, and RST and INH to ground (held low, it counts).", "connect", new
        {
            wires = new[] { new[] { "V1.+", "U1.VDD" }, ["V1.-", "GND1.GND"], ["U1.VSS", "GND1.GND"], ["CLK1.CLK", "U1.CLK"], ["U1.RST", "GND2.GND"], ["U1.INH", "GND2.GND"] },
        });
        recipe.Step("Probe the clock and the first four outputs as logic levels.", "add_probe", new { pin = "U1.CLK", label = "Clock", kind = "logic" });
        recipe.Step(null, "add_probe", new { pin = "U1.Q0", label = "Q0", kind = "logic" });
        recipe.Step(null, "add_probe", new { pin = "U1.Q1", label = "Q1", kind = "logic" });
        recipe.Step(null, "add_probe", new { pin = "U1.Q2", label = "Q2", kind = "logic" });
        recipe.Step(null, "add_probe", new { pin = "U1.Q3", label = "Q3", kind = "logic" });
        recipe.Step("Look at the drawing.", "render_schematic", new { });
        recipe.Step("Run twelve clock periods: Q0 to Q3 should each be high for one period in turn, a tenth of the time (high_fraction about 0.1).", "run_transient", new { duration = Si(12 / f) });
        return recipe.Text("Say what the traces show. Wiring Q5 (or any output) back to RST makes it count to that number and start again, a divide-by-n.");
    }

    // ───────────────────────────── plumbing ─────────────────────────────

    private sealed class Recipe(string intro)
    {
        private readonly StringBuilder _text = new(intro + "\n\nFollow these steps in order. A line starting with \"" + CallMarker.Trim() + "\" is one tool call, with its arguments as JSON. If a step fails, read the error, fix the cause and carry on.\n");
        private int _number;

        public void Step(string? says, string tool, object args)
        {
            _number++;
            _text.Append('\n').Append(_number).Append('.').Append(says != null ? " " : "");
            if (says != null) _text.Append(says);
            _text.Append("\n   ");
            _text.Append(CallMarker).Append(tool).Append(' ').Append(JsonSerializer.Serialize(args, Json.Options));
        }

        public string Text(string closing) => _text.Append("\n\n").Append(closing).ToString();
    }

    /// <summary>A value in engineering notation, as the tools take it: "15.92n", "3.333k".</summary>
    private static string Si(double v, string unit = "") => SiPrefix.Format(v, unit, 4).Replace(" ", "");

    /// <summary>A number argument, as text (MCP prompt arguments are), within limits.</summary>
    internal static double Number(string? given, double fallback, string what, double min, double max)
    {
        if (string.IsNullOrWhiteSpace(given)) return fallback;
        if (!SiPrefix.TryParse(given.Trim(), out var v) || !double.IsFinite(v))
            throw new McpException($"{what} must be a number, such as {fallback.ToString(CultureInfo.InvariantCulture)} (or in engineering notation, as 1k), not \"{given}\".");
        if (v < min || v > max) throw new McpException($"{what} must be from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}.");
        return v;
    }

    private static string Choice(string? given, string fallback, string what, params string[] choices)
    {
        if (string.IsNullOrWhiteSpace(given)) return fallback;
        var v = given.Trim().ToLowerInvariant();
        return choices.Contains(v) ? v : throw new McpException($"{what} must be one of {string.Join(", ", choices)}, not \"{given}\".");
    }
}
