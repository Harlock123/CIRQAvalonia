using System.Text.Json.Nodes;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using static Cirq.Mcp.Tests.Calls;

namespace Cirq.Mcp.Tests;

/// <summary>The tool handlers on a headless circuit, called directly.</summary>
public class ToolTests
{
    [Fact]
    public async Task Building_an_RC_filter_gives_the_parts_and_nets_a_person_would_draw()
    {
        using var b = new Bench();
        await b.BuildRc();

        Assert.Equal(["FG1", "R1", "C1", "GND1", "GND2"], b.Circuit.Components.Select(c => c.Name));
        Assert.Equal(10e3, Assert.IsType<Resistor>(b.Circuit.Components[1]).Resistance);
        Assert.Equal(100e-9, Assert.IsType<Capacitor>(b.Circuit.Components[2]).Capacitance, 15);
        Assert.Equal(90, b.Circuit.Components[2].RotationDegrees);

        var circuit = await Parse(b.Tools.GetCircuit());
        var nets = circuit["nets"]!.AsArray().ToDictionary(n => (string)n!["name"]!, n => n!["pins"]!.AsArray().Select(p => (string)p!).ToList());
        Assert.Equal(["FG1.GND", "GND1.GND", "C1.B", "GND2.GND"], nets["GND"].OrderBy(p => p == "FG1.GND" ? 0 : p == "GND1.GND" ? 1 : p == "C1.B" ? 2 : 3));
        Assert.Contains(nets.Values, pins => pins.SequenceEqual(["R1.B", "C1.A"]));
        Assert.Null(circuit["unconnected_pins"]);
        Assert.Null(circuit["warning"]);
        Assert.Equal(2, circuit["probes"]!.AsArray().Count);
        Assert.True((bool)circuit["modified"]!);

        // get_part says where each pin is and what it's wired to.
        var part = await Parse(b.Tools.GetPart("r1"));
        var a = part["pins"]!.AsArray().First(p => (string)p!["pin"]! == "R1.A")!;
        Assert.Equal([-70.0, -20.0], a["at"]!.AsArray().Select(x => (double)x!));
        Assert.Equal(["FG1.OUT"], a["connected_to"]!.AsArray().Select(x => (string)x!));
        Assert.Contains(part["settings"]!.AsArray(), s => (string)s!["name"]! == "Resistance" && (double)s["value"]! == 10e3 && (string)s["unit"]! == "Ω");
    }

    [Fact]
    public async Task Each_change_is_one_undo_step_and_undo_and_redo_step_through_them()
    {
        using var b = new Bench();
        await b.BuildRc();
        // Parts, wires and two probes: four steps.
        Assert.Equal(4, b.Session.UndoCount);

        var undid = await Parse(b.Tools.Undo());
        Assert.Equal("AI: Add probe", (string)undid["undid"]!);
        Assert.Single(b.Circuit.Probes);
        await b.Tools.Undo();
        await b.Tools.Undo();
        Assert.Empty(b.Circuit.Wires);
        Assert.Equal(5, b.Circuit.Components.Count);

        await b.Tools.Redo();
        Assert.Equal(4, b.Circuit.Wires.Count);
        await b.Tools.Undo();
        await b.Tools.Undo();
        Assert.Empty(b.Circuit.Components);
        Assert.Contains("nothing to undo", await Error(b.Tools.Undo()));
    }

    [Fact]
    public async Task A_change_that_fails_changes_nothing_and_leaves_no_step()
    {
        using var b = new Bench();
        // The second part is wrong: the first isn't placed either.
        var message = await Error(b.Tools.AddParts([new() { Kind = "Resistor" }, new() { Kind = "Flux Capacitor" }]));
        Assert.Contains("no part kind \"Flux Capacitor\"", message);
        Assert.Empty(b.Circuit.Components);
        Assert.Equal(0, b.Session.UndoCount);

        await b.BuildRc();
        var steps = b.Session.UndoCount;
        // A good setting then a bad one: neither sticks.
        Assert.Contains("is a number", await Error(b.Tools.SetPart("R1", Args(new { Resistance = "22k", Tolerance = "lots" }))));
        Assert.Equal(10e3, ((Resistor)b.Circuit.Components[1]).Resistance);
        // One good wire and one to a pin that isn't there.
        Assert.Contains("has no pin Q", await Error(b.Tools.Connect([["R1.A", "C1.B"], ["R1.Q", "C1.A"]])));
        Assert.Equal(4, b.Circuit.Wires.Count);
        Assert.Equal(steps, b.Session.UndoCount);
    }

    [Fact]
    public async Task Settings_take_engineering_text_choices_models_and_expressions()
    {
        using var b = new Bench();
        await b.BuildRc();
        await b.Tools.AddParts([new() { Kind = "Diode 1N4148", X = 200, Y = 0 }]);

        await b.Tools.SetPart("R1", Args(new { Resistance = "4k7" }));
        Assert.Equal(4700, ((Resistor)b.Circuit.Components[1]).Resistance, 9);
        await b.Tools.SetPart("FG1", Args(new { Shape = "triangle", Frequency = "2.5 kHz" }));
        var generator = (FunctionGenerator)b.Circuit.Components[0];
        Assert.Equal(Waveform.Triangle, generator.Shape);
        Assert.Equal(2500, generator.Frequency, 9);
        await b.Tools.SetPart("D1", Args(new { Model = "1n4001" }));
        Assert.Equal("1N4001", ((Diode)b.Circuit.Components[^1]).Model.Name);

        Assert.Contains("Sine, Square, Triangle", await Error(b.Tools.SetPart("FG1", Args(new { Shape = "wobbly" }))));
        Assert.Contains("1N4148", await Error(b.Tools.SetPart("D1", Args(new { Model = "BC547" }))));
        Assert.Contains("Did you mean \"Resistance\"", await Error(b.Tools.SetPart("R1", Args(new { Resistnce = 1 }))));
        Assert.Contains("set_part's own", await Error(b.Tools.SetPart("R1", Args(new { X = 1 }))));

        // A parameter, and a setting bound to it: changing the parameter moves the part.
        await b.Tools.SetParameter("R", "22k");
        await b.Tools.SetPart("R1", Args(new { Resistance = "=R*2" }));
        Assert.Equal(44e3, ((Resistor)b.Circuit.Components[1]).Resistance, 6);
        var set = await Parse(b.Tools.SetParameter("R", "1k"));
        Assert.Equal(1000, (double)set["parameters"]![0]!["value"]!);
        Assert.Equal(2000, ((Resistor)b.Circuit.Components[1]).Resistance, 6);
        Assert.Contains("\"expression\":\"R*2\"", (await Parse(b.Tools.GetPart("R1")))["settings"]!.ToJsonString());

        // Place and rename.
        await b.Tools.SetPart("R1", x: 100, rotation: 450, new_name: "RLOAD");
        var moved = b.Circuit.Components[1];
        Assert.Equal(("RLOAD", 100.0, -20.0, 90.0), (moved.Name, moved.X, moved.Y, moved.RotationDegrees));
        Assert.Contains("0, 90, 180 or 270", await Error(b.Tools.SetPart("RLOAD", rotation: 45)));
        Assert.Contains("already", await Error(b.Tools.SetPart("RLOAD", new_name: "C1")));
    }

    [Fact]
    public async Task Pins_are_found_by_name_id_or_number_and_a_wrong_one_says_what_there_is()
    {
        using var b = new Bench();
        await b.Tools.AddParts([new() { Kind = "NE555" }, new() { Kind = "Resistor", X = 200 }, new() { Kind = "Net Label", X = -200, Settings = Args(new { NetName = "VCC" }) }]);
        var made = await Parse(b.Tools.Connect([["U1.out", "R1.A"], ["U1.p8", "R1.B"], ["U1.4", "L1.net"]]));
        Assert.Equal(3, (int)made["wires_added"]!);
        var nets = made["nets"]!.AsArray().Select(n => n!["pins"]!.AsArray().Select(p => (string)p!).ToList()).ToList();
        Assert.Contains(nets, n => n.SequenceEqual(["U1.OUT", "R1.A"]));
        Assert.Contains(nets, n => n.SequenceEqual(["U1.VCC", "R1.B"]));
        Assert.Contains(nets, n => n.Contains("U1.RESET") && n.Contains("L1.net"));

        Assert.Contains("Its pins: GND, TRIG, OUT, RESET, CTRL, THRES, DISCH, VCC", await Error(b.Tools.Connect([["U1.ZAP", "R1.A"]])));
        Assert.Contains("Did you mean \"R1\"", await Error(b.Tools.Connect([["R2.A", "R1.A"]])));
        Assert.Contains("Part.Pin", await Error(b.Tools.Connect([["R1", "U1.OUT"]])));
        Assert.Contains("same part", await Error(b.Tools.Connect([["R1.A", "R1.B"]])));

        // A wire that's already there isn't drawn twice; disconnect takes it away.
        Assert.Equal(0, (int)(await Parse(b.Tools.Connect([["R1.A", "U1.OUT"]])))["wires_added"]!);
        Assert.Equal(1, (int)(await Parse(b.Tools.Disconnect("R1.A", "U1.OUT")))["wires_removed"]!);
        Assert.Contains("no wire between", await Error(b.Tools.Disconnect("R1.A", "U1.OUT")));
        Assert.Equal(1, (int)(await Parse(b.Tools.Disconnect("U1.VCC")))["wires_removed"]!);
    }

    [Fact]
    public async Task The_operating_point_leaves_capacitors_open_and_reads_every_net()
    {
        using var b = new Bench();
        await b.Tools.AddParts(
        [
            new() { Kind = "DC Voltage", X = -100, Y = 40, Rotation = 90, Settings = Args(new { Voltage = 9 }) },
            new() { Kind = "Resistor", Settings = Args(new { Resistance = "1k" }) },
            new() { Kind = "Capacitor", X = 100, Y = 40, Rotation = 90 },
            new() { Kind = "LED Green", X = 200, Y = 40, Rotation = 90 },
            new() { Kind = "Resistor", X = 260, Settings = Args(new { Resistance = "470" }) },
            new() { Kind = "Ground", X = 0, Y = 140 },
        ]);
        await b.Tools.Connect([["V1.+", "R1.A"], ["R1.B", "C1.A"], ["C1.B", "GND1.GND"], ["V1.-", "GND1.GND"], ["V1.+", "R2.A"], ["R2.B", "LED1.A"], ["LED1.K", "GND1.GND"]]);
        await b.Tools.AddProbe("C1.A", "Cap");
        await b.Tools.AddProbe("LED1.A", "LED", kind: "current");

        var op = await Parse(b.Tools.OperatingPoint());
        // At DC the capacitor is open: no current in R1, so the capacitor sits at the full 9 V.
        var cap = op["nets"]!.AsArray().First(n => ((string)n!["pins"]!).Contains("C1.A"))!;
        Assert.Equal(9, (double)cap["volts"]!, 3);
        var led = (string)op["probes"]!.AsArray().First(p => (string)p!["probe"]! == "LED")!["reading"]!;
        Assert.EndsWith("mA", led);
        Assert.InRange(double.Parse(led[..^2], System.Globalization.CultureInfo.InvariantCulture), 12, 16);
    }

    [Fact]
    public async Task A_transient_run_measures_the_probes_plots_them_and_fills_the_scope()
    {
        using var b = new Bench();
        await b.BuildRc();
        var result = await b.Tools.RunTransient(J("6m"));
        var report = (JsonObject)JsonNode.Parse(Text(result))!;
        Assert.Equal("6ms", (string)report["duration"]!);
        Assert.Equal("600ns", (string)report["time_step"]!);
        var input = report["probes"]![0]!;
        var output = report["probes"]![1]!;
        Assert.Equal("Input", (string)input["probe"]!);
        Assert.StartsWith("500", (string)input["frequency"]!);
        Assert.Equal("4V", (string)input["peak_to_peak"]!);
        // 500 Hz is three times the corner: the output can't follow the edges all the way.
        Assert.StartsWith("2.", (string)output["peak_to_peak"]!);
        Png(result);

        // The samples went into the circuit's own probes, for the scope.
        Assert.All(b.Circuit.Probes, p => Assert.True(p.HistoryBuffer.Count > 1000));
        Assert.InRange(b.Circuit.Probes[1].HistoryBuffer[^1].Time, 5.9e-3, 6.1e-3);

        var trace = await Parse(b.Tools.GetTrace("output", from: J("1m"), to: J("2m"), points: 5));
        var pairs = trace["pairs"]!.AsArray();
        Assert.Equal(5, pairs.Count);
        Assert.InRange((double)pairs[0]![0]!, 1e-3, 1.01e-3);
        Assert.Equal("V", (string)trace["unit"]!);

        // A later reading window skips the start.
        var later = (JsonObject)JsonNode.Parse(Text(await b.Tools.RunTransient(J(0.006), from: J("3m"), plot: false)))!;
        Assert.Equal("3ms", (string)later["from"]!);
        // Past the first cycle the capacitor never discharges below about a volt.
        Assert.InRange(Cirq.Core.Units.SiPrefix.Parse((string)later["probes"]![1]!["min"]!), 0.8, 1.2);
    }

    [Fact]
    public async Task An_ac_sweep_finds_the_corner_and_a_dc_sweep_walks_a_setting()
    {
        using var b = new Bench();
        await b.BuildRc();
        var ac = await b.Tools.AcSweep(stop_hz: J("100k"));
        var report = (JsonObject)JsonNode.Parse(Text(ac))!;
        var output = report["probes"]![1]!;
        var corner = Cirq.Core.Units.SiPrefix.Parse((string)output["corner_minus_3db"]!);
        Assert.InRange(corner, 155, 163);
        Assert.InRange((double)output["gain_db_at_stop"]!, -57, -55);
        Png(ac);

        // DC: the capacitor is open, so the output follows the generator's offset.
        var dc = await b.Tools.DcSweep("FG1.DcOffset", J(0), J(4), points: 5);
        var rows = ((JsonObject)JsonNode.Parse(Text(dc))!)["table"]!.AsArray();
        Assert.Equal(5, rows.Count);
        Assert.Equal((double)rows[4]!["Input"]!, (double)rows[4]!["Output"]!, 3);
        Png(dc);

        // Stepping a second setting gives a curve per value.
        var family = (JsonObject)JsonNode.Parse(Text(await b.Tools.DcSweep("temperature", J(0), J(50), points: 3, step: "R1.Resistance", step_start: J("1k"), step_stop: J("2k"), step_points: 2, plot: false)))!;
        Assert.Equal("R1 Resistance", (string)family["stepped"]!);
        Assert.Equal(4, family["table"]![0]!.AsObject().Count(p => p.Key != "x"));

        Assert.Contains("no number setting Wattage", await Error(b.Tools.DcSweep("R1.Wattage", J(0), J(1))));
        Assert.Contains("Part.Setting", await Error(b.Tools.DcSweep("nothing", J(0), J(1))));
    }

    [Fact]
    public async Task Running_needs_a_circuit_and_probes_and_sensible_times()
    {
        using var b = new Bench();
        Assert.Contains("empty", await Error(b.Tools.RunTransient(J("1m"))));
        await b.Tools.AddParts([new() { Kind = "Resistor" }, new() { Kind = "Ground", Y = 100 }]);
        Assert.Contains("no probes", await Error(b.Tools.RunTransient(J("1m"))));
        Assert.Contains("no probes", await Error(b.Tools.AcSweep()));
        Assert.Contains("engineering notation", await Error(b.Tools.RunTransient(J("soon"))));
        Assert.Contains("from 1 ns to 100 s", await Error(b.Tools.RunTransient(J(500))));
        Assert.Contains("more than the", await Error(b.Tools.RunTransient(J(10), time_step: J("1n"))));
    }

    [Fact]
    public async Task Files_stay_in_the_working_folder_and_nothing_is_written_over_unasked()
    {
        using var b = new Bench();
        await b.BuildRc();

        var saved = await Parse(b.Tools.SaveCircuit("filters/rc.cirq"));
        Assert.Equal(Path.Combine("filters", "rc.cirq"), (string)saved["saved"]!);
        Assert.True(File.Exists(b.Folder.File("filters/rc.cirq")));
        Assert.False(b.Session.IsModified);
        Assert.Equal(b.Folder.File("filters/rc.cirq"), b.Session.FilePath);
        // Its own file again (and with no path, the file it has) is fine.
        await b.Tools.SaveCircuit("filters/rc.cirq");
        await b.Tools.SaveCircuit();

        File.WriteAllText(b.Folder.File("theirs.cirq"), "{}");
        Assert.Contains("already exists", await Error(b.Tools.SaveCircuit("theirs.cirq")));
        await b.Tools.SaveCircuit("theirs.cirq", overwrite: true);
        Assert.Contains("outside the working folder", await Error(b.Tools.SaveCircuit("../escape.cirq")));
        Assert.Contains(".cirq", await Error(b.Tools.SaveCircuit("rc.txt")));

        // A circuit with changes isn't thrown away unless asked.
        await b.Tools.SetPart("R1", Args(new { Resistance = "1k" }));
        Assert.Contains("discard_changes", await Error(b.Tools.NewCircuit()));
        Assert.Contains("discard_changes", await Error(b.Tools.OpenCircuit("filters/rc.cirq")));
        var opened = await Parse(b.Tools.OpenCircuit("filters/rc.cirq", discard_changes: true));
        Assert.Equal(5, opened["parts"]!.AsArray().Count);
        Assert.Equal(10e3, ((Resistor)b.Circuit.Components[1]).Resistance);
        Assert.Equal(0, b.Session.UndoCount);

        Assert.Contains("no file", await Error(b.Tools.OpenCircuit("missing.cirq")));
        var spice = await Parse(b.Tools.GetSpiceNetlist("rc.cir"));
        Assert.Contains("R1 ", (string)spice["netlist"]!);
        Assert.Equal((string)spice["netlist"]!, File.ReadAllText(b.Folder.File("rc.cir")));

        var fresh = await Parse(b.Tools.NewCircuit("Blank"));
        Assert.Equal("Blank", (string)fresh["title"]!);
        Assert.Empty(b.Circuit.Components);
    }

    [Fact]
    public async Task An_example_loads_with_its_probes_and_a_fresh_history()
    {
        using var b = new Bench();
        var listed = await Parse(b.Tools.ListExamples("rectifier"));
        Assert.Contains("Full-Wave Rectifier", listed.ToJsonString());
        var loaded = await Parse(b.Tools.LoadExample("full-wave rectifier"));
        Assert.Equal("Full-wave rectifier", (string)loaded["title"]!);
        Assert.Equal(3, loaded["probes"]!.AsArray().Count);
        Assert.Contains("centre-tapped", (string)loaded["about"]!);
        Assert.Equal(0, b.Session.UndoCount);
        Assert.False(b.Session.IsModified);
        Assert.Contains("Did you mean", await Error(b.Tools.LoadExample("Ful Wave Rectifer")));
    }

    [Fact]
    public async Task The_library_lists_and_describes_the_palette()
    {
        using var b = new Bench();
        var all = await Parse(b.Tools.ListParts());
        Assert.True((int)all["count"]! > 180);
        var timers = await Parse(b.Tools.ListParts("555"));
        Assert.Contains("NE555", timers.ToJsonString());
        Assert.Contains("\"group\":\"Passive\"", (await Parse(b.Tools.ListParts(category: "passive"))).ToJsonString());
        Assert.Contains("The groups:", await Error(b.Tools.ListParts(category: "Kitchen")));

        var led = await Parse(b.Tools.DescribePart("led red"));
        Assert.Equal("LED", (string)led["designator"]!);
        Assert.Equal(["A", "K"], led["pins"]!.AsArray().Select(p => (string)p!["name"]!));
        Assert.Contains(led["settings"]!.AsArray(), s => (string)s!["name"]! == "Model" && s["choices"]!.AsArray().Count > 5);
        Assert.Contains("Did you mean", await Error(b.Tools.DescribePart("Resistr")));
    }

    [Fact]
    public async Task Checks_find_a_missing_ground_and_the_explainer_names_the_filter()
    {
        using var b = new Bench();
        await b.Tools.AddParts([new() { Kind = "DC Voltage" }, new() { Kind = "Resistor", X = 100 }]);
        await b.Tools.Connect([["V1.+", "R1.A"]]);
        Assert.Contains("no Ground part", (string)(await Parse(b.Tools.GetCircuit()))["warning"]!);
        var check = await Parse(b.Tools.CheckCircuit());
        Assert.True((int)check["errors"]! + (int)check["warnings"]! > 0);
        Assert.Contains("R1.B", check["unconnected_pins"]!.ToJsonString());

        using var rc = new Bench();
        await rc.BuildRc();
        var explained = await Parse(rc.Tools.ExplainCircuit());
        Assert.Contains("low-pass", explained["stages"]![0]!["headline"]!.ToString());
        Assert.Equal("Nothing found.", (string)(await Parse(rc.Tools.CheckCircuit()))["verdict"]!);
    }

    [Fact]
    public async Task The_schematic_is_drawn_black_on_white()
    {
        using var b = new Bench();
        Assert.Contains("empty", await Error(b.Tools.RenderSchematic()));
        await b.BuildRc();
        var png = Png(await b.Tools.RenderSchematic(scale: 1));
        using var bitmap = SkiaSharp.SKBitmap.Decode(png);
        Assert.InRange(bitmap.Width, 200, 600);
        // White paper at the corner, and dark ink somewhere on it.
        Assert.Equal(SkiaSharp.SKColors.White, bitmap.GetPixel(2, 2));
        Assert.Contains(Enumerable.Range(0, bitmap.Width * bitmap.Height).Select(i => bitmap.GetPixel(i % bitmap.Width, i / bitmap.Width)), p => p.Red < 60 && p.Green < 60 && p.Blue < 60);
        Assert.Contains("scale", await Error(b.Tools.RenderSchematic(scale: 9)));
    }

    [Fact]
    public async Task Probes_and_conditions_are_edits_too()
    {
        using var b = new Bench();
        await b.BuildRc();
        Assert.Contains("already", await Error(b.Tools.AddProbe("C1.A", "output")));
        Assert.Contains("voltage, current", await Error(b.Tools.AddProbe("C1.A", "x", kind: "magnetism")));
        var diff = await Parse(b.Tools.AddProbe("R1.A", "Across R1", kind: "differential", reference: "R1.B"));
        Assert.Contains("\"reference\":\"R1.B\"", diff.ToJsonString());
        await b.Tools.RemoveProbe("Input");
        Assert.Equal(["Output", "Across R1"], b.Circuit.Probes.Select(p => p.Label));

        await b.Tools.SetConditions("Hot filter", 85, true);
        Assert.Equal(("Hot filter", 85.0, true), (b.Circuit.Title, b.Circuit.AmbientTemperatureCelsius, b.Circuit.AdaptiveTimeStep));
        Assert.Contains("-273.15", await Error(b.Tools.SetConditions(temperature_c: -400)));

        // Its wires and its probe go with it.
        await b.Tools.RemoveParts(["C1"]);
        Assert.Equal(["Across R1"], b.Circuit.Probes.Select(p => p.Label));
        Assert.Equal(2, b.Circuit.Wires.Count);
    }

    [Fact]
    public async Task The_headless_server_has_no_transport_or_scope()
    {
        using var b = new Bench();
        Assert.Contains("run_transient", await Error(b.Tools.Transport("run")));
        Assert.Contains("run, pause or reset", await Error(b.Tools.Transport("faster")));
        Assert.Contains("no scope", await Error(b.Tools.SetScope(timebase: J("1m"))));
    }
}
