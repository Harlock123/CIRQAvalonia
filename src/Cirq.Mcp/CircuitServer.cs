using System.Reflection;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cirq.Mcp;

/// <summary>
/// The MCP server: <see cref="CircuitTools"/>' methods as MCP tools (the SDK's attribute model, read by reflection),
/// with instructions that tell an agent CirqAvalonia's conventions. The same server runs over stdio headless, over a
/// pipe for agents attached to the open editor, and in-process for the editor's own AI Chat.
/// </summary>
public static class CircuitServer
{
    public const string Name = "CirqAvalonia";

    /// <summary>What every agent is told on connecting: the conventions the tools' descriptions build on.</summary>
    public const string Instructions = """
        CirqAvalonia is an electronic circuit simulator: a schematic editor over a SPICE-style analog solver (Modified Nodal Analysis, Newton-Raphson for nonlinear parts) and an event-driven digital engine, so analog and digital parts mix in one circuit. You build circuits with the same parts a person places from its palette, wire them, probe them, and run them.

        Conventions (they matter):
        - Parts are known by their designators (R1, C2, U1, Q1, LED1, GND1…), given in order as they're added unless you name them. A part's kind is its palette name (list_parts, describe_part).
        - Pins are named Part.Pin: "R1.A", "R1.B", "C1.A", "U1.OUT", "Q1.B", "GND1.GND". Two-terminal parts have pins A and B (a diode or LED: A the anode, K the cathode; a DC source: + and -; a function generator: OUT and GND); ICs use their datasheet pin names (an NE555: GND, TRIG, OUT, RESET, CTRL, THRES, DISCH, VCC; an op-amp: IN+, IN-, OUT, V+, V-), and a pin can also be named by its id, e.g. "U1.p3" for pin 3. describe_part and get_part list every part's pins.
        - Values are in SI units: ohms, farads, henries, volts, amps, seconds, hertz. Numbers may be written in engineering notation as text: "10k", "4k7", "100n", "2.2u", "1M" (m is milli, M is mega).
        - Every circuit needs a Ground part: the 0 V reference everything is measured against. Return every source to it (a DC supply's - pin, a function generator's GND pin), with a wire or another Ground part (all Ground parts are one net).
        - Wires join pins (connect). Pins wired together form a net; a "Net Label" part names a net, and labels with the same name are one net with no wire between them, which keeps busy drawings readable (and joins sheets).
        - The drawing: x grows to the right and y grows downward, in drawing units on a 10-unit grid. A part's position is its centre and its pins sit around it (a resistor's pins are 30 units either side). Rotation is clockwise in steps of 90 degrees: a resistor at 0 runs left (A) to right (B), at 90 it stands upright with A on top. Lay circuits out as a person would: the signal flowing left to right, supplies at the top or left, ground below, parts on the grid 60 to 120 units apart so wires don't run through symbols. Check with render_schematic.
        - Probes say what to measure: add_probe on a pin records that net's voltage (or a current, a logic level, a difference or power). The scope, run_transient, ac_sweep and dc_sweep all report the probes, so add them before running.
        - Every change is one undo step (undo takes it back; in the app Ctrl+Z does too), and a call that fails changes nothing.

        Running a circuit:
        - operating_point: the DC bias point, every net's voltage. The quickest check that a circuit is wired and biased as meant.
        - run_transient: through time from power-on, with each probe's measurements (min, max, mean, RMS, frequency, duty cycle, rise time) and a plot. Choose a duration that shows several cycles of what you expect.
        - ac_sweep: gain and phase against frequency (a Bode plot), driven by sources' AC magnitude; for filters and amplifiers.
        - dc_sweep: the probes against a swept setting (a supply's Voltage, a resistance, temperature), optionally stepped by a second.
        - check_circuit before running a circuit that won't solve, and explain_circuit to hear what the editor recognises it as.

        A good way to build: list_parts with a search for what you need, describe_part for pins and settings; add_parts with positions laid out left to right (with Ground parts below); connect the pins; get_circuit to check the nets; add_probe on the input and the output; render_schematic to check the drawing reads well; operating_point, then run_transient (or ac_sweep); read the measurements and look at the plot; adjust values with set_part and run again; save_circuit as .cirq.

        The server's prompts are saved recipes (an RC low-pass filter, an LED with its resistor, a 555 astable, an inverting op-amp amplifier, a bridge rectifier with smoothing, a logic counter): step-by-step plans with the exact tool calls, to follow or adapt. load_example opens any of the editor's own example circuits.
        """;

    /// <summary>The MCP tools, bound to a host.</summary>
    public static IEnumerable<McpServerTool> Tools(ICircuitHost host)
    {
        var target = new CircuitTools(host);
        foreach (var method in typeof(CircuitTools).GetMethods(BindingFlags.Public | BindingFlags.Instance))
            if (method.GetCustomAttribute<McpServerToolAttribute>() != null)
                yield return McpServerTool.Create(method, target);
    }

    /// <summary>The saved recipes (<see cref="Recipes"/>) as MCP prompts.</summary>
    public static IEnumerable<McpServerPrompt> Prompts() =>
        typeof(Recipes).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.GetCustomAttribute<McpServerPromptAttribute>() != null)
            .Select(m => McpServerPrompt.Create(m, target: null));

    /// <summary>The tools' MCP definitions (names, descriptions, input schemas), without a host to run them.</summary>
    public static IList<Tool> Definitions() => [.. _definitions.Value];

    private static readonly Lazy<Tool[]> _definitions = new(() => Tools(new NoHost()).Select(t => t.ProtocolTool).ToArray());

    public static Implementation Info => new()
    {
        Name = Name,
        Title = "CirqAvalonia",
        Version = typeof(CircuitServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0",
    };

    public static McpServerOptions Options(ICircuitHost host)
    {
        var options = new McpServerOptions { ServerInfo = Info, ServerInstructions = Instructions, ToolCollection = [] };
        options.Filters.Request.CallToolFilters.Add(next => async (request, cancel) =>
        {
            var tool = request.Params is { } p && options.ToolCollection.TryGetPrimitive(p.Name, out var t) ? t.ProtocolTool : null;
            if (tool != null && Arguments.Check(tool, request.Params!.Arguments) is { } problem) return Arguments.Error(problem);
            try
            {
                return await next(request, cancel).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not McpException and not OperationCanceledException)
            {
                // The SDK couldn't bind the arguments to the handler (a value of the wrong kind): say which, not just that it failed.
                return Arguments.Error(tool != null ? $"{tool.Name}: {e.Message} Check the arguments against the tool's input schema." : e.Message);
            }
        });
        foreach (var tool in Tools(host)) options.ToolCollection.Add(tool);
        options.PromptCollection = [];
        foreach (var prompt in Prompts()) options.PromptCollection.Add(prompt);
        return options;
    }

    /// <summary>Serves one MCP session over a transport until the client goes away or <paramref name="cancel"/> fires.</summary>
    public static async Task RunAsync(ITransport transport, ICircuitHost host, CancellationToken cancel = default)
    {
        await using var server = McpServer.Create(transport, Options(host));
        await server.RunAsync(cancel).ConfigureAwait(false);
    }

    /// <summary>Checks a call's arguments against its tool's input schema before the SDK binds them.</summary>
    internal static class Arguments
    {
        public static CallToolResult Error(string message) => new() { IsError = true, Content = [new TextContentBlock { Text = message }] };

        /// <summary>What's wrong with the arguments (a name the tool doesn't take, a value of the wrong kind), or null.</summary>
        public static string? Check(Tool tool, IDictionary<string, JsonElement>? args)
        {
            if (args == null || !tool.InputSchema.TryGetProperty("properties", out var props)) return null;
            foreach (var (name, value) in args)
            {
                if (!props.TryGetProperty(name, out var schema))
                {
                    var names = props.EnumerateObject().Select(x => x.Name).ToList();
                    return $"{tool.Name} has no argument \"{name}\". It takes: {(names.Count == 0 ? "none" : string.Join(", ", names))}.";
                }
                if (Kinds(schema) is { Count: > 0 } kinds && !kinds.Any(k => Fits(k, value)))
                    return $"{tool.Name}: {name} must be {string.Join(" or ", kinds.Where(k => k != "null").Select(Describe))}, not {Describe(value)}.";
            }
            return null;
        }

        private static List<string> Kinds(JsonElement schema) =>
            schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ? []
            : type.ValueKind == JsonValueKind.String ? [type.GetString()!]
            : type.ValueKind == JsonValueKind.Array ? type.EnumerateArray().Select(x => x.GetString() ?? "").ToList() : [];

        private static bool Fits(string kind, JsonElement v) => kind switch
        {
            "string" => v.ValueKind == JsonValueKind.String,
            "number" => v.ValueKind == JsonValueKind.Number,
            "integer" => v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _),
            "boolean" => v.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "array" => v.ValueKind == JsonValueKind.Array,
            "object" => v.ValueKind == JsonValueKind.Object,
            "null" => v.ValueKind == JsonValueKind.Null,
            _ => true,
        };

        private static string Describe(string kind) => kind switch
        {
            "string" => "text", "number" => "a number", "integer" => "a whole number", "boolean" => "true or false",
            "array" => "a list", "object" => "an object", _ => kind,
        };

        private static string Describe(JsonElement v) => v.ValueKind switch
        {
            JsonValueKind.String => $"the text \"{v.GetString()}\"",
            JsonValueKind.Number => $"the number {v.GetRawText()}",
            JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            JsonValueKind.Array => "a list",
            JsonValueKind.Object => "an object",
            _ => "null",
        };
    }

    /// <summary>A host for reading the tools' definitions only.</summary>
    private sealed class NoHost : ICircuitHost
    {
        public string Root => Path.GetTempPath();
        public ICircuitLibrary Library => throw new InvalidOperationException("Not connected to a circuit.");
        public Task<T> RunAsync<T>(string action, Func<ICircuitSession, T> work, CancellationToken cancel = default) =>
            throw new InvalidOperationException("Not connected to a circuit.");
    }
}
