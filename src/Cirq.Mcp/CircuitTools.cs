using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cirq.Components.Diagnostics;
using Cirq.Components.Explaining;
using Cirq.Components.Serialization;
using Cirq.Components.Spice;
using Cirq.Core.Probing;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Cirq.Mcp;

/// <summary>
/// The MCP tools: what an agent can do with CirqAvalonia. Each handler reaches the circuit only through
/// <see cref="ICircuitHost.RunAsync{T}"/>, so the same code serves headless and live. Every change is one undo step, a
/// call that fails leaves the circuit as it was, and failures come back as MCP tool errors
/// (<see cref="McpException"/>) with a message that says what to do instead.
/// </summary>
/// <remarks>The parameter names are the JSON names agents see, hence snake_case.</remarks>
public sealed partial class CircuitTools(ICircuitHost host)
{
    private readonly Workspace _files = new(host.Root);

    // ───────────────────────────── the library ─────────────────────────────

    [McpServerTool(Name = "list_parts", ReadOnly = true, Idempotent = true)]
    [Description("Lists the parts the palette offers, by group: each one's name (what add_parts takes as kind) and what it is. Over 180 parts, so narrow it with search (matched against names, descriptions and groups, e.g. \"555\", \"op-amp\", \"LED\", \"shift register\") or category. describe_part gives a part's pins and settings.")]
    public Task<string> ListParts(
        [Description("Words to look for in a part's name, description or group, e.g. \"timer\" or \"74HC\". Optional.")] string? search = null,
        [Description("Only this group, e.g. \"Passive\", \"Sources\", \"Semiconductors\". Optional.")] string? category = null,
        CancellationToken cancel = default) =>
        Read("list_parts", _ =>
        {
            var parts = host.Library.Parts.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(category))
            {
                var groups = host.Library.Parts.Select(p => p.Category).Distinct().ToList();
                if (!groups.Contains(category.Trim(), StringComparer.OrdinalIgnoreCase))
                    throw new ArgumentException($"There's no group \"{category}\". The groups: {string.Join(", ", groups)}.");
                parts = parts.Where(p => string.Equals(p.Category, category.Trim(), StringComparison.OrdinalIgnoreCase));
            }
            if (!string.IsNullOrWhiteSpace(search))
            {
                var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                parts = parts.Where(p => words.All(w => $"{p.Category} {p.Name} {p.Description}".Contains(w, StringComparison.OrdinalIgnoreCase)));
            }
            var list = parts.ToList();
            var result = new JsonObject
            {
                ["count"] = list.Count,
                ["groups"] = new JsonArray(list.GroupBy(p => p.Category).Select(g => (JsonNode)new JsonObject
                {
                    ["group"] = g.Key,
                    ["parts"] = new JsonArray(g.Select(p => (JsonNode)new JsonObject { ["kind"] = p.Name, ["about"] = p.Description }).ToArray()),
                }).ToArray()),
            };
            if (list.Count == 0) result["note"] = "Nothing matches. Try fewer or other words, or list_parts with no search for every group.";
            return result;
        }, cancel);

    [McpServerTool(Name = "describe_part", ReadOnly = true, Idempotent = true)]
    [Description("Describes one kind of part before you place it: its type, the designator it's named with (R for resistors, so R1, R2…), its pins (name, role, and where each sits relative to the part's position at rotation 0) and every setting with its type, default, unit and choices.")]
    public Task<string> DescribePart(
        [Description("The part's kind as list_parts gives it, e.g. \"Resistor\", \"NE555 Timer\" (any case).")] string kind,
        CancellationToken cancel = default) =>
        Read("describe_part", _ =>
        {
            var part = Kind(kind);
            var c = part.Create();
            return new JsonObject
            {
                ["kind"] = part.Name,
                ["group"] = part.Category,
                ["about"] = part.Description,
                ["type"] = c.ComponentType,
                ["designator"] = c.DesignatorPrefix,
                ["value"] = c.ValueLabel,
                ["pins"] = new JsonArray(c.Terminals.Select(t =>
                {
                    var p = new JsonObject { ["name"] = t.Name, ["kind"] = t.Type.ToString(), ["offset"] = new JsonArray(t.CanvasOffset.X, t.CanvasOffset.Y) };
                    if (!string.Equals(t.Id, t.Name, StringComparison.OrdinalIgnoreCase)) p["id"] = t.Id;
                    return (JsonNode)p;
                }).ToArray()),
                ["settings"] = Settings.Describe(c),
            };
        }, cancel);

    // ───────────────────────────── the document ─────────────────────────────

    [McpServerTool(Name = "get_circuit", ReadOnly = true, Idempotent = true)]
    [Description("The circuit as it stands: its title and file, every part (designator, type, value, position, rotation), every net (the pins wired together on it), pins wired to nothing, the probes, the parameters and the temperature. Look here before changing a circuit you didn't build, and after wiring to check the nets are what you meant.")]
    public Task<string> GetCircuit(CancellationToken cancel = default) =>
        Read("get_circuit", Census.Of, cancel);

    [McpServerTool(Name = "get_part", ReadOnly = true, Idempotent = true)]
    [Description("One part in full: its pins (each one's position on the drawing, net and what else is on that net) and every setting with its value now, unit and choices.")]
    public Task<string> GetPart(
        [Description("The part's designator, e.g. \"R1\".")] string name,
        CancellationToken cancel = default) =>
        Read("get_part", s => Census.Part(s.Circuit, Pins.Part(s.Circuit, name)), cancel);

    [McpServerTool(Name = "new_circuit", Destructive = true)]
    [Description("Starts a new, empty circuit in place of the open one, with a fresh undo history. Refused if the open circuit has unsaved changes, unless discard_changes is true.")]
    public Task<string> NewCircuit(
        [Description("What to call it. Default: \"Untitled circuit\".")] string? title = null,
        [Description("Throw away unsaved changes to the open circuit.")] bool discard_changes = false,
        CancellationToken cancel = default) =>
        Read("started a new circuit", s =>
        {
            Discardable(s, discard_changes);
            s.Replace(new Circuit { Title = string.IsNullOrWhiteSpace(title) ? "Untitled circuit" : title.Trim() }, null);
            return Census.Of(s);
        }, cancel);

    [McpServerTool(Name = "open_circuit", Destructive = true)]
    [Description("Opens a .cirq file from the working folder in place of the open circuit, with a fresh undo history. Refused if the open circuit has unsaved changes, unless discard_changes is true.")]
    public Task<string> OpenCircuit(
        [Description("The file, relative to the working folder, e.g. \"filters/rc.cirq\".")] string path,
        [Description("Throw away unsaved changes to the open circuit.")] bool discard_changes = false,
        CancellationToken cancel = default) =>
        Read($"opened {Path.GetFileName(path)}", s =>
        {
            var full = _files.Resolve(path, "A circuit", CircuitSerializer.FileExtension);
            if (!File.Exists(full)) throw new FileNotFoundException($"There's no file {path} in the working folder ({_files.Root}).");
            Discardable(s, discard_changes);
            var loaded = CircuitSerializer.Load(full);
            s.Replace(loaded.Circuit, full);
            var result = Census.Of(s);
            if (!loaded.IsClean) result["load_warnings"] = new JsonArray(loaded.Warnings.Select(w => (JsonNode)w).ToArray());
            return result;
        }, cancel);

    [McpServerTool(Name = "save_circuit", Destructive = true)]
    [Description("Saves the circuit as a .cirq file in the working folder (parts, wires, probes, parameters and all). With no path it saves over the file it came from. An existing file is only replaced with overwrite true, or if you saved it earlier in this session.")]
    public Task<string> SaveCircuit(
        [Description("Where, relative to the working folder, ending .cirq, e.g. \"rc-filter.cirq\". Optional when the circuit already has a file.")] string? path = null,
        [Description("Replace a file that's already there.")] bool overwrite = false,
        CancellationToken cancel = default) =>
        Read("saved the circuit", s =>
        {
            string full;
            if (string.IsNullOrWhiteSpace(path))
            {
                full = s.FilePath ?? throw new ArgumentException("The circuit has no file yet: give a path, e.g. \"my-circuit.cirq\".");
                _files.Resolve(full, "A circuit", CircuitSerializer.FileExtension);
                overwrite = true;
            }
            else full = _files.Resolve(path, "A circuit", CircuitSerializer.FileExtension);
            _files.CheckWritable(full, overwrite);
            CircuitSerializer.Save(s.Circuit, full);
            _files.Wrote(full);
            s.Saved(full);
            return new JsonObject { ["saved"] = Path.GetRelativePath(_files.Root, full), ["parts"] = s.Circuit.Components.Count, ["wires"] = s.Circuit.Wires.Count };
        }, cancel);

    [McpServerTool(Name = "list_examples", ReadOnly = true, Idempotent = true)]
    [Description("Lists the example circuits the editor comes with (an RC filter, a 555 astable, op-amp amplifiers, rectifiers, logic counters, buses and many more), by group. load_example opens one: a good way to see how a working circuit is laid out and probed.")]
    public Task<string> ListExamples(
        [Description("Words to look for in an example's name, description or group. Optional.")] string? search = null,
        CancellationToken cancel = default) =>
        Read("list_examples", _ =>
        {
            var examples = host.Library.Examples.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(search))
            {
                var words = search.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                examples = examples.Where(e => words.All(w => $"{e.Category} {e.Name} {e.Description}".Contains(w, StringComparison.OrdinalIgnoreCase)));
            }
            var list = examples.ToList();
            return new JsonObject
            {
                ["count"] = list.Count,
                ["groups"] = new JsonArray(list.GroupBy(e => e.Category).Select(g => (JsonNode)new JsonObject
                {
                    ["group"] = g.Key,
                    ["examples"] = new JsonArray(g.Select(e => (JsonNode)new JsonObject { ["name"] = e.Name, ["about"] = e.Description }).ToArray()),
                }).ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "load_example", Destructive = true)]
    [Description("Opens one of the example circuits in place of the open circuit, with its probes, and a fresh undo history. Refused if the open circuit has unsaved changes, unless discard_changes is true.")]
    public Task<string> LoadExample(
        [Description("The example's name as list_examples gives it (any case).")] string name,
        [Description("Throw away unsaved changes to the open circuit.")] bool discard_changes = false,
        CancellationToken cancel = default) =>
        Read($"loaded the example {name}", s =>
        {
            var example = host.Library.Examples.FirstOrDefault(e => string.Equals(e.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"There's no example \"{name}\". Did you mean {Text.Nearest(host.Library.Examples.Select(e => e.Name), name ?? "")}? list_examples lists them.");
            Discardable(s, discard_changes);
            var loaded = host.Library.LoadExample(example.Name);
            s.Replace(loaded.Circuit, null);
            s.Scope(loaded.Timebase, loaded.VoltsPerDivision);
            var result = Census.Of(s);
            result["about"] = example.Description;
            return result;
        }, cancel);

    [McpServerTool(Name = "undo", Destructive = true)]
    [Description("Takes back the last change to the circuit (yours or, in the app, the person's: you share one undo history), as Ctrl+Z does.")]
    public Task<string> Undo(CancellationToken cancel = default) =>
        Read("undid a step", s => new JsonObject { ["undid"] = s.Undo() ?? throw new InvalidOperationException("There's nothing to undo.") }, cancel);

    [McpServerTool(Name = "redo", Destructive = true)]
    [Description("Puts back the change last undone, as Ctrl+Y does.")]
    public Task<string> Redo(CancellationToken cancel = default) =>
        Read("redid a step", s => new JsonObject { ["redid"] = s.Redo() ?? throw new InvalidOperationException("There's nothing to redo.") }, cancel);

    // ───────────────────────────── building ─────────────────────────────

    /// <summary>One part for add_parts to place.</summary>
    public sealed class NewPart
    {
        [Description("The part's kind as list_parts gives it, e.g. \"Resistor\".")]
        public string Kind { get; set; } = "";

        [Description("Where its centre goes on the drawing, in drawing units: x grows to the right, y grows downward. The grid is 10 units; parts are about 60 to 120 across.")]
        public double X { get; set; }

        [Description("See x.")]
        public double Y { get; set; }

        [Description("Clockwise turn in degrees: 0, 90, 180 or 270. A resistor at 0 runs left (pin A) to right (B); at 90, A is on top.")]
        public double Rotation { get; set; }

        [Description("Its designator, e.g. \"R5\". Optional: the next free one is used (R1, R2…).")]
        public string? Name { get; set; }

        [Description("Settings by name, e.g. {\"Resistance\": \"4k7\"} or {\"Capacitance\": 1e-7}. Optional.")]
        public Dictionary<string, JsonElement>? Settings { get; set; }
    }

    [McpServerTool(Name = "add_parts", Destructive = true)]
    [Description("Places parts on the drawing, all in one undo step, and returns each one's designator and pins (with where each pin is, so you can lay out and wire). Numbers in settings may be given in engineering notation as text: \"10k\", \"4k7\", \"100n\", \"2.2u\", \"1M\" (m is milli, M is mega). Lay parts out left to right the way the signal flows, sources on the left, ground below, with room between them for wires. Wire them with connect.")]
    public Task<string> AddParts(
        [Description("The parts, e.g. [{\"kind\": \"Resistor\", \"x\": 0, \"y\": 0, \"settings\": {\"Resistance\": \"10k\"}}, {\"kind\": \"Ground\", \"x\": 0, \"y\": 100}].")] NewPart[] parts,
        CancellationToken cancel = default) =>
        Edit(parts.Length == 1 ? $"Add {parts[0].Kind}" : $"Add {parts.Length} parts", s =>
        {
            if (parts.Length == 0) throw new ArgumentException("Give at least one part.");
            var placed = new JsonArray();
            foreach (var p in parts)
            {
                var kind = Kind(p.Kind);
                var c = kind.Create();
                if (!string.IsNullOrWhiteSpace(p.Name))
                {
                    var name = p.Name.Trim();
                    if (s.Circuit.Components.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                        throw new ArgumentException($"There's a part called {name} already. Leave name out to get the next free designator.");
                    c.Name = name;
                }
                Place(c, p.X, p.Y, p.Rotation);
                s.Circuit.Add(c);
                if (p.Settings is { Count: > 0 }) Settings.Apply(c, s.Circuit, p.Settings);
                placed.Add(Census.Part(s.Circuit, c));
            }
            return new JsonObject { ["added"] = placed };
        }, cancel);

    [McpServerTool(Name = "set_part", Destructive = true)]
    [Description("Changes a part, in one undo step: its settings (by name, as get_part lists them; numbers may be engineering text like \"4k7\"; text starting with \"=\" binds a setting to an expression over the circuit's parameters, e.g. \"=R*2\"), its position, rotation or designator. Wires stay on its pins when it moves. Returns the part as it now is.")]
    public Task<string> SetPart(
        [Description("The part's designator, e.g. \"R1\".")] string name,
        [Description("Settings to change, e.g. {\"Resistance\": \"22k\", \"Tolerance\": 0.01}. Optional.")] Dictionary<string, JsonElement>? settings = null,
        [Description("New x position. Optional.")] double? x = null,
        [Description("New y position. Optional.")] double? y = null,
        [Description("New clockwise rotation in degrees (0, 90, 180, 270). Optional.")] double? rotation = null,
        [Description("A new designator. Optional.")] string? new_name = null,
        CancellationToken cancel = default) =>
        Edit($"Edit {name}", s =>
        {
            var c = Pins.Part(s.Circuit, name);
            if (settings is { Count: > 0 }) Settings.Apply(c, s.Circuit, settings);
            if (x is not null || y is not null || rotation is not null) Place(c, x ?? c.X, y ?? c.Y, rotation ?? c.RotationDegrees);
            if (!string.IsNullOrWhiteSpace(new_name))
            {
                var to = new_name.Trim();
                if (s.Circuit.Components.Any(o => o != c && string.Equals(o.Name, to, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException($"There's a part called {to} already.");
                c.Name = to;
            }
            return Census.Part(s.Circuit, c);
        }, cancel);

    [McpServerTool(Name = "remove_parts", Destructive = true)]
    [Description("Deletes parts, with every wire and probe on them, in one undo step.")]
    public Task<string> RemoveParts(
        [Description("The parts' designators, e.g. [\"R2\", \"C3\"].")] string[] names,
        CancellationToken cancel = default) =>
        Edit(names.Length == 1 ? $"Delete {names[0]}" : $"Delete {names.Length} parts", s =>
        {
            if (names.Length == 0) throw new ArgumentException("Name at least one part.");
            var parts = names.Select(n => Pins.Part(s.Circuit, n)).Distinct().ToList();
            foreach (var c in parts) s.Circuit.Remove(c);
            return new JsonObject { ["removed"] = new JsonArray(parts.Select(c => (JsonNode)c.Name).ToArray()), ["parts"] = s.Circuit.Components.Count };
        }, cancel);

    [McpServerTool(Name = "connect", Destructive = true)]
    [Description("Draws wires between pins, all in one undo step. Each wire joins two pins named Part.Pin (\"R1.B\", \"U1.OUT\"; a pin by its name or id, as get_part or describe_part list them); wiring a pin to several others joins them all into one net. Pins already on the same net need no wire. Returns the nets the wires made. Net labels (the \"Net Label\" part) join pins by name with no wire, which keeps a busy drawing readable.")]
    public Task<string> Connect(
        [Description("The wires as pairs of pins, e.g. [[\"V1.+\", \"R1.A\"], [\"R1.B\", \"C1.A\"]].")] string[][] wires,
        CancellationToken cancel = default) =>
        Edit(wires.Length == 1 ? "Add wire" : $"Add {wires.Length} wires", s =>
        {
            if (wires.Length == 0) throw new ArgumentException("Give at least one wire, as a pair of pins.");
            var made = 0;
            var touched = new List<Terminal>();
            foreach (var w in wires)
            {
                if (w is not { Length: 2 }) throw new ArgumentException("Each wire is a pair of pins, e.g. [\"R1.B\", \"C1.A\"].");
                var a = Pins.Find(s.Circuit, w[0]);
                var b = Pins.Find(s.Circuit, w[1]);
                if (a.Equals(b)) throw new ArgumentException($"A wire can't join {w[0]} to itself.");
                if (a.Owner == b.Owner && a.Owner is not null && a.Owner.Terminals.Count == 2)
                    throw new ArgumentException($"{w[0]} and {w[1]} are the two ends of the same part: a wire between them would short it out.");
                touched.Add(a);
                if (s.Circuit.Wires.Any(x => (x.SourceTerminal.Equals(a) && x.TargetTerminal.Equals(b)) || (x.SourceTerminal.Equals(b) && x.TargetTerminal.Equals(a)))) continue;
                s.Circuit.Connect(a, b);
                made++;
            }
            var netlist = s.Circuit.BuildNetlist();
            var nets = touched.Select(netlist.NetOf).Distinct().Select(n => (JsonNode)new JsonObject
            {
                ["net"] = n.Name,
                ["pins"] = new JsonArray(n.Terminals.Where(t => t.Owner is not null).Select(t => (JsonNode)Pins.Name(t)).ToArray()),
            }).ToArray();
            return new JsonObject { ["wires_added"] = made, ["nets"] = new JsonArray(nets) };
        }, cancel);

    [McpServerTool(Name = "disconnect", Destructive = true)]
    [Description("Removes wires, in one undo step: the wire between two pins (from and to), or with only from, every wire on that pin.")]
    public Task<string> Disconnect(
        [Description("A pin, e.g. \"R1.B\".")] string from,
        [Description("The pin at the wire's other end. Optional: without it every wire on from goes.")] string? to = null,
        CancellationToken cancel = default) =>
        Edit("Delete wire", s =>
        {
            var a = Pins.Find(s.Circuit, from);
            var b = string.IsNullOrWhiteSpace(to) ? null : Pins.Find(s.Circuit, to);
            var gone = s.Circuit.Wires.Where(w => (w.SourceTerminal.Equals(a) || w.TargetTerminal.Equals(a))
                && (b is null || w.SourceTerminal.Equals(b) || w.TargetTerminal.Equals(b))).ToList();
            if (gone.Count == 0)
                throw new ArgumentException(b is null ? $"There are no wires on {from}." : $"There's no wire between {from} and {to}. (Pins on one net may be joined through other pins: get_part {a.Owner?.Name} shows what {from} is wired to.)");
            foreach (var w in gone) s.Circuit.Remove(w);
            return new JsonObject { ["wires_removed"] = gone.Count };
        }, cancel);

    [McpServerTool(Name = "add_probe", Destructive = true)]
    [Description("Puts a probe on a pin, in one undo step: the scope records it as the circuit runs, and run_transient, ac_sweep and dc_sweep report and plot every probe. Kinds: voltage (the pin's net against ground; the usual), current (into the part through that pin), logic (a digital level), differential (against a reference pin) and power (the voltage across to the reference times the current).")]
    public Task<string> AddProbe(
        [Description("The pin, e.g. \"C1.A\" or \"U1.OUT\".")] string pin,
        [Description("What to call the trace, e.g. \"Output\". Default: the pin's name.")] string? label = null,
        [Description("voltage, current, logic, differential or power. Default voltage.")] string kind = "voltage",
        [Description("For differential and power: the second pin, measured against. Default ground.")] string? reference = null,
        CancellationToken cancel = default) =>
        Edit("Add probe", s =>
        {
            var t = Pins.Find(s.Circuit, pin);
            if (!Enum.TryParse<ProbeKind>(kind?.Trim(), true, out var k) || !Enum.IsDefined(k) || int.TryParse(kind, out _))
                throw new ArgumentException($"kind is one of voltage, current, logic, differential or power, not \"{kind}\".");
            var name = string.IsNullOrWhiteSpace(label) ? Pins.Name(t) : label.Trim();
            if (s.Circuit.Probes.Any(p => string.Equals(p.Label, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"There's a probe called {name} already. Give another label.");
            var colour = Core.Primitives.Color.ProbePalette[s.Circuit.Probes.Count % Core.Primitives.Color.ProbePalette.Count];
            var probe = new SignalProbe(name, t, colour) { Kind = k };
            if (!string.IsNullOrWhiteSpace(reference)) probe.ReferenceTerminal = Pins.Find(s.Circuit, reference);
            s.Circuit.Probes.Add(probe);
            return new JsonObject { ["probes"] = Census.Probes(s.Circuit) };
        }, cancel);

    [McpServerTool(Name = "remove_probe", Destructive = true)]
    [Description("Takes a probe off, by its label, in one undo step.")]
    public Task<string> RemoveProbe(
        [Description("The probe's label, as get_circuit lists it.")] string label,
        CancellationToken cancel = default) =>
        Edit("Remove probe", s =>
        {
            s.Circuit.Probes.Remove(Census.Probe(s.Circuit, label));
            return new JsonObject { ["probes"] = Census.Probes(s.Circuit) };
        }, cancel);

    [McpServerTool(Name = "set_parameter", Destructive = true)]
    [Description("Sets one of the circuit's named parameters (a number several parts can be set from: give a part's setting the text \"=name\" or \"=name*2\" with set_part), in one undo step. The expression is a number in engineering notation or arithmetic over other parameters. An empty expression removes the parameter.")]
    public Task<string> SetParameter(
        [Description("The parameter's name, e.g. \"R\" or \"Fc\".")] string name,
        [Description("Its value or expression, e.g. \"10k\" or \"1/(2*pi*R*C)\". Empty to remove it.")] string expression,
        CancellationToken cancel = default) =>
        Edit($"Parameter {name}", s =>
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A parameter needs a name.");
            var existing = s.Circuit.Parameters.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.Ordinal));
            if (string.IsNullOrWhiteSpace(expression))
            {
                if (existing is null) throw new ArgumentException($"There's no parameter {name} to remove.");
                s.Circuit.Parameters.Remove(existing);
            }
            else if (existing is null) s.Circuit.Parameters.Add(new CircuitParameter { Name = name.Trim(), Expression = expression.Trim() });
            else existing.Expression = expression.Trim();
            var applied = CircuitParameters.Apply(s.Circuit.Parameters, s.Circuit.Components);
            if (!applied.IsClean) throw new ArgumentException(string.Join(" ", applied.Problems.Select(p => $"{p.Key}: {p.Value}")));
            return new JsonObject
            {
                ["parameters"] = new JsonArray(s.Circuit.Parameters.Select(p => (JsonNode)new JsonObject
                {
                    ["name"] = p.Name, ["expression"] = p.Expression,
                    ["value"] = applied.Values.TryGetValue(p.Name, out var v) ? v : null,
                }).ToArray()),
            };
        }, cancel);

    [McpServerTool(Name = "set_conditions", Destructive = true)]
    [Description("Sets what the whole circuit is and runs at, in one undo step: its title, the ambient temperature every semiconductor sees (27 °C is room temperature, what models are characterised at), and whether the solver may choose its own time step (adaptive: shorter where waveforms bend, landing on switching edges; off gives the same answer every run).")]
    public Task<string> SetConditions(
        [Description("The circuit's title. Optional.")] string? title = null,
        [Description("Ambient temperature in °C. Optional.")] double? temperature_c = null,
        [Description("Let the solver choose its own time step. Optional.")] bool? adaptive_time_step = null,
        CancellationToken cancel = default) =>
        Edit("Conditions", s =>
        {
            if (temperature_c is { } t && (t < -273.15 || t > 1000 || !double.IsFinite(t))) throw new ArgumentException("temperature_c must be from -273.15 to 1000.");
            if (!string.IsNullOrWhiteSpace(title)) s.Circuit.Title = title.Trim();
            if (temperature_c is { } celsius) s.Circuit.AmbientTemperatureCelsius = celsius;
            if (adaptive_time_step is { } adaptive) s.Circuit.AdaptiveTimeStep = adaptive;
            return new JsonObject { ["title"] = s.Circuit.Title, ["temperature_c"] = s.Circuit.AmbientTemperatureCelsius, ["adaptive_time_step"] = s.Circuit.AdaptiveTimeStep };
        }, cancel);

    // ───────────────────────────── checking ─────────────────────────────

    [McpServerTool(Name = "check_circuit", ReadOnly = true, Idempotent = true)]
    [Description("Checks the circuit for mistakes before running it: the editor's electrical rule check (no ground, outputs driven against each other, floating inputs, a source shorted out…), plus every pin wired to nothing. Errors stop it solving; warnings are very probably not what was meant.")]
    public Task<string> CheckCircuit(CancellationToken cancel = default) =>
        Read("check_circuit", s =>
        {
            var findings = ElectricalRuleCheck.Run(s.Circuit);
            var (_, loose) = Census.Nets(s.Circuit);
            var result = new JsonObject
            {
                ["errors"] = findings.Count(f => f.Severity == RuleSeverity.Error),
                ["warnings"] = findings.Count(f => f.Severity == RuleSeverity.Warning),
                ["findings"] = new JsonArray(findings.Select(f => (JsonNode)new JsonObject
                {
                    ["severity"] = f.Severity.ToString().ToLowerInvariant(), ["rule"] = f.Rule, ["message"] = f.Message, ["parts"] = f.Where,
                }).ToArray()),
            };
            if (loose.Count > 0) result["unconnected_pins"] = loose;
            if (findings.Count == 0 && loose.Count == 0) result["verdict"] = "Nothing found.";
            return result;
        }, cancel);

    [McpServerTool(Name = "explain_circuit", ReadOnly = true, Idempotent = true)]
    [Description("What the editor recognises the circuit as, in words (Simulate › What Is This Circuit?): the stages it finds (a divider, an RC filter, an inverting amplifier, a 555 astable…) with what each does and its working figures, such as a filter's corner frequency or an amplifier's gain.")]
    public Task<string> ExplainCircuit(CancellationToken cancel = default) =>
        Read("explain_circuit", s =>
        {
            var found = CircuitExplainer.Explain(s.Circuit);
            return new JsonObject
            {
                ["stages"] = new JsonArray(found.Select(e => (JsonNode)new JsonObject { ["headline"] = e.Headline, ["detail"] = e.Detail, ["parts"] = e.Designators }).ToArray()),
                ["note"] = found.Count == 0 ? "Nothing it recognises; that doesn't mean the circuit is wrong." : null,
            };
        }, cancel);

    [McpServerTool(Name = "render_schematic", ReadOnly = true, Idempotent = true)]
    [Description("A picture of the schematic as it's drawn (black on white, as it prints): look at it to check the layout reads well: parts not on top of each other, wires not crossing through symbols, the signal flowing left to right.")]
    public async Task<CallToolResult> RenderSchematic(
        [Description("Pixels per drawing unit, from 0.5 to 4. Default 1.5.")] double scale = 1.5,
        [Description("Which sheet, for a drawing split into pages. Default: the first.")] string? sheet = null,
        CancellationToken cancel = default)
    {
        if (scale is < 0.5 or > 4 || !double.IsFinite(scale)) throw new McpException("scale must be from 0.5 to 4.");
        var (png, about) = await Run("drew the schematic", s =>
        {
            if (s.Circuit.Components.Count == 0) throw new InvalidOperationException("The circuit is empty: there's nothing to draw yet.");
            if (sheet is not null && s.Circuit.Sheets.Count > 0 && !s.Circuit.Sheets.Contains(sheet))
                throw new ArgumentException($"There's no sheet \"{sheet}\". The sheets: {string.Join(", ", s.Circuit.Sheets)}.");
            var image = host.Library.Picture(s.Circuit, sheet ?? s.Circuit.Sheets.FirstOrDefault(), scale);
            return (image, $"{s.Circuit.Title}: {s.Circuit.Components.Count} parts, {s.Circuit.Wires.Count} wires.");
        }, cancel).ConfigureAwait(false);
        return new CallToolResult { Content = [ImageContentBlock.FromBytes(png, "image/png"), new TextContentBlock { Text = about }] };
    }

    [McpServerTool(Name = "get_spice_netlist", ReadOnly = true)]
    [Description("The circuit as a SPICE deck (the editor's File › Export › SPICE netlist), to read or to hand to another simulator; with a path it's also written to a .cir file in the working folder. Parts SPICE has no model for are listed as skipped.")]
    public Task<string> GetSpiceNetlist(
        [Description("A .cir, .sp or .net file to write it to, relative to the working folder. Optional.")] string? path = null,
        [Description("Replace a file that's already there.")] bool overwrite = false,
        CancellationToken cancel = default) =>
        Read("get_spice_netlist", s =>
        {
            var deck = SpiceNetlistWriter.Write(s.Circuit);
            var result = new JsonObject { ["netlist"] = deck.Netlist };
            if (deck.Skipped.Count > 0) result["skipped"] = new JsonArray(deck.Skipped.Select(x => (JsonNode)x).ToArray());
            if (!string.IsNullOrWhiteSpace(path))
            {
                var full = _files.Resolve(path, "A SPICE netlist", ".cir", ".sp", ".net");
                _files.CheckWritable(full, overwrite);
                File.WriteAllText(full, deck.Netlist);
                _files.Wrote(full);
                result["saved"] = Path.GetRelativePath(_files.Root, full);
            }
            return result;
        }, cancel);

    // ───────────────────────────── plumbing ─────────────────────────────

    /// <summary>The palette entry called <paramref name="name"/>, or an error naming the nearest.</summary>
    private PartKind Kind(string name)
    {
        var parts = host.Library.Parts;
        var trimmed = name?.Trim() ?? "";
        return parts.FirstOrDefault(p => string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? parts.FirstOrDefault(p => string.Equals(p.Create().ComponentType, trimmed, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"There's no part kind \"{name}\". Did you mean {Text.Nearest(parts.Select(p => p.Name), trimmed)}? list_parts with a search finds them.");
    }

    private static void Place(CircuitComponent c, double x, double y, double rotation)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1e6 || Math.Abs(y) > 1e6) throw new ArgumentException("x and y must be ordinary numbers within a million of the origin.");
        var r = ((rotation % 360) + 360) % 360;
        if (r % 90 != 0) throw new ArgumentException($"rotation is 0, 90, 180 or 270, not {rotation}.");
        c.X = x;
        c.Y = y;
        c.RotationDegrees = r;
    }

    private static void Discardable(ICircuitSession s, bool discard)
    {
        if (s.IsModified && !discard)
            throw new InvalidOperationException("The open circuit has changes that haven't been saved. Save it first (save_circuit), or call again with discard_changes true to throw them away.");
    }

    /// <summary>Runs a call that changes nothing (or replaces the document wholesale) and answers in JSON.</summary>
    private Task<string> Read(string action, Func<ICircuitSession, JsonObject> work, CancellationToken cancel) =>
        Run(action, s => work(s).ToJsonString(Json.Options), cancel);

    /// <summary>
    /// Runs a change as one undo step called <paramref name="label"/>. If it fails, the circuit goes back to how it
    /// was before, and no step is left behind.
    /// </summary>
    private Task<string> Edit(string label, Func<ICircuitSession, JsonObject> work, CancellationToken cancel) =>
        Run(label, s =>
        {
            var before = CircuitSerializer.ToJson(s.Circuit);
            using (s.Step($"AI: {label}"))
            {
                try
                {
                    return work(s).ToJsonString(Json.Options);
                }
                catch
                {
                    s.Restore(before);
                    throw;
                }
            }
        }, cancel);

    private async Task<T> Run<T>(string action, Func<ICircuitSession, T> work, CancellationToken cancel)
    {
        try
        {
            return await host.RunAsync(action, work, cancel).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not McpException and not OperationCanceledException)
        {
            throw new McpException(Explain(e), e);
        }
    }

    /// <summary>A failure's message for the agent: the message alone for the expected kinds, else with its type.</summary>
    internal static string Explain(Exception e) => e switch
    {
        ArgumentException or InvalidOperationException or FormatException or NotSupportedException or IOException
            or UnauthorizedAccessException or CircuitBusyException or JsonException or CircuitFormatException
            or CircuitTopologyException or ConvergenceException or KeyNotFoundException => e.Message,
        _ => $"Something went wrong ({e.GetType().Name}): {e.Message}",
    };
}
