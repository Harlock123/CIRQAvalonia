using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static Cirq.Mcp.Tests.Calls;

namespace Cirq.Mcp.Tests;

/// <summary>The server as an agent meets it: an MCP client in the same process, over in-memory pipes.</summary>
public class EndToEndTests
{
    /// <summary>Every tool the server offers, in order.</summary>
    public static readonly string[] ToolNames =
    [
        "list_parts", "describe_part", "get_circuit", "get_part", "new_circuit", "open_circuit", "save_circuit", "list_examples",
        "load_example", "undo", "redo", "add_parts", "set_part", "remove_parts", "connect", "disconnect", "add_probe",
        "remove_probe", "set_parameter", "set_conditions", "check_circuit", "explain_circuit", "render_schematic",
        "get_spice_netlist", "operating_point", "run_transient", "get_trace", "ac_sweep", "dc_sweep", "transport", "set_scope",
    ];

    [Fact]
    public async Task The_server_introduces_itself_and_lists_its_tools_and_recipes()
    {
        using var b = new Bench();
        await using var s = await InProcess.Start(b.Host);
        Assert.Equal("CirqAvalonia", s.Client.ServerInfo!.Name);
        Assert.Contains("Ground part", s.Client.ServerInstructions!);
        Assert.Contains("Part.Pin", s.Client.ServerInstructions!);

        var tools = await s.Client.ListToolsAsync(cancellationToken: CancellationToken.None);
        Assert.Equal(ToolNames, tools.Select(t => t.Name));
        foreach (var tool in tools)
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), $"{tool.Name} has no description.");
            var schema = tool.JsonSchema;
            Assert.Equal("object", schema.GetProperty("type").GetString());
            // Every argument says what it is, and is named in snake_case as agents expect.
            foreach (var p in schema.TryGetProperty("properties", out var props) ? props.EnumerateObject() : [])
            {
                Assert.Matches("^[a-z][a-z0-9_]*$", p.Name);
                Assert.True(p.Value.TryGetProperty("description", out _), $"{tool.Name}'s {p.Name} has no description.");
            }
        }
        Assert.Equal(ToolNames, CircuitServer.Definitions().Select(t => t.Name));

        var prompts = await s.Client.ListPromptsAsync(cancellationToken: CancellationToken.None);
        Assert.Equal(["rc_low_pass", "led_with_resistor", "astable_555", "inverting_amplifier", "bridge_rectifier", "decade_counter"], prompts.Select(p => p.Name));
    }

    [Fact]
    public async Task Wrong_arguments_are_named_before_anything_runs()
    {
        using var b = new Bench();
        await using var s = await InProcess.Start(b.Host);
        Assert.Contains("set_part has no argument \"resistance\". It takes: name, settings, x, y, rotation, new_name.",
            await s.Client.Fails("set_part", new { name = "R1", resistance = "10k" }));
        Assert.Contains("add_parts: parts must be a list, not the text", await s.Client.Fails("add_parts", new { parts = "a resistor" }));
        Assert.Contains("get_trace: points must be a whole number, not the text \"many\"", await s.Client.Fails("get_trace", new { probe = "x", points = "many" }));
        Assert.Empty(b.Circuit.Components);
    }

    [Fact]
    public async Task An_agent_builds_runs_and_saves_a_circuit()
    {
        using var b = new Bench();
        await using var s = await InProcess.Start(b.Host);
        var added = await s.Client.Json("add_parts", new
        {
            parts = new object[]
            {
                new { kind = "DC Voltage", x = -100, y = 40, rotation = 90, settings = new { Voltage = 10 } },
                new { kind = "Resistor", x = 0, y = 0, settings = new { Resistance = "1k" } },
                new { kind = "Resistor", x = 100, y = 40, rotation = 90, settings = new { Resistance = "3k" } },
                new { kind = "Ground", x = 0, y = 140 },
            },
        });
        Assert.Equal(4, added["added"]!.AsArray().Count);
        await s.Client.Json("connect", new { wires = new[] { new[] { "V1.+", "R1.A" }, ["R1.B", "R2.A"], ["R2.B", "GND1.GND"], ["V1.-", "GND1.GND"] } });
        await s.Client.Json("add_probe", new { pin = "R2.A", label = "Divided" });

        var op = await s.Client.Json("operating_point");
        Assert.Equal("7.5V", (string)op["probes"]![0]!["reading"]!);

        // A run's answer is a picture and the measurements.
        var run = await s.Client.Call("run_transient", new { duration = 0.001 });
        Assert.NotEqual(true, run.IsError);
        Assert.Single(run.Content.OfType<ImageContentBlock>());
        Assert.Equal("7.5V", (string)JsonNode.Parse(Text(run))!["probes"]![0]!["mean"]!);

        await s.Client.Json("save_circuit", new { path = "divider.cirq" });
        Assert.True(File.Exists(b.Folder.File("divider.cirq")));

        // A failure is a tool error saying why, not a protocol error.
        Assert.Contains("There's no part R9", await s.Client.Fails("get_part", new { name = "R9" }));
    }

    [Fact]
    public async Task Every_recipe_runs_as_written_and_does_what_it_says()
    {
        using var b = new Bench();
        await using var s = await InProcess.Start(b.Host);
        var results = new Dictionary<string, List<(string Tool, CallToolResult Result)>>();
        foreach (var prompt in await s.Client.ListPromptsAsync(cancellationToken: CancellationToken.None))
        {
            var text = ((TextContentBlock)(await prompt.GetAsync(cancellationToken: CancellationToken.None)).Messages.Single().Content).Text;
            var calls = Regex.Matches(text, @"^\s*→ (\w+) (\{.*\})$", RegexOptions.Multiline);
            Assert.True(calls.Count >= 5, $"{prompt.Name} has only {calls.Count} calls.");
            var ran = results[prompt.Name] = [];
            foreach (Match call in calls)
            {
                var name = call.Groups[1].Value;
                var args = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(call.Groups[2].Value)!;
                if (name == "new_circuit") args["discard_changes"] = JsonSerializer.SerializeToElement(true);
                var result = await s.Client.CallToolAsync(name, args.ToDictionary(kv => kv.Key, kv => (object?)kv.Value), cancellationToken: CancellationToken.None);
                Assert.True(result.IsError != true, $"{prompt.Name}: {name} failed: {Text(result)}");
                ran.Add((name, result));
            }
        }

        JsonNode Last(string recipe, string tool) => JsonNode.Parse(Text(results[recipe].Last(r => r.Tool == tool).Result))!;
        double Si(JsonNode? n) => Cirq.Core.Units.SiPrefix.Parse((string)n!);
        JsonNode Probe(JsonNode report, string label) => report["probes"]!.AsArray().First(p => (string)p!["probe"]! == label)!;

        Assert.InRange(Si(Probe(Last("rc_low_pass", "ac_sweep"), "Output")["corner_minus_3db"]), 970, 1030);
        Assert.InRange(Si(Probe(Last("led_with_resistor", "operating_point"), "LED current")["reading"]), 9e-3, 11e-3);
        Assert.InRange(Si(Probe(Last("astable_555", "run_transient"), "Output")["frequency"]), 900, 1100);
        var amplifier = Last("inverting_amplifier", "run_transient");
        Assert.InRange(Si(Probe(amplifier, "Output")["peak_to_peak"]) / Si(Probe(amplifier, "Input")["peak_to_peak"]), 9.5, 10.5);
        Assert.InRange(Si(Probe(Last("bridge_rectifier", "run_transient"), "DC out")["mean"]), 14.5, 17.5);
        var counter = Last("decade_counter", "run_transient");
        Assert.InRange((double)Probe(counter, "Q2")["high_fraction"]!, 0.05, 0.2);
        Assert.Equal("10 %", (string)Probe(counter, "Q2")["duty_cycle"]!);

        // A prompt's arguments are read from text, engineering notation and all.
        var faster = await s.Client.GetPromptAsync("astable_555", new Dictionary<string, object?> { ["frequency_hz"] = "2k" }, cancellationToken: CancellationToken.None);
        Assert.Contains("about 2kHz", ((TextContentBlock)faster.Messages[0].Content).Text);
    }
}
