using System.Text.Json.Nodes;
using Cirq.Core.Probing;
using Cirq.Core.Topology;

namespace Cirq.Mcp;

/// <summary>Finding pins by the names agents use for them: "R1.A", "U1.OUT", "U1.p3".</summary>
public static class Pins
{
    /// <summary>
    /// A pin's name as an agent writes it: the part's designator, a dot and the pin's name, or its id where two pins of
    /// the part share a name (as an IC's two GND pins might).
    /// </summary>
    public static string Name(Terminal t)
    {
        var owner = t.Owner;
        if (owner is null) return t.Name;
        var shared = owner.Terminals.Count(x => string.Equals(x.Name, t.Name, StringComparison.OrdinalIgnoreCase)) > 1;
        return $"{owner.Name}.{(shared || t.Name.Contains('.') || t.Name.Length == 0 ? t.Id : t.Name)}";
    }

    /// <summary>The part called <paramref name="name"/>, or an error naming the nearest.</summary>
    public static CircuitComponent Part(Circuit circuit, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A part's name (its designator, as R1) is needed.");
        return circuit.Components.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException(circuit.Components.Count == 0
                ? $"There's no part {name}: the circuit is empty."
                : $"There's no part {name}. Did you mean {Text.Nearest(circuit.Components.Select(c => c.Name), name)}? get_circuit lists them all.");
    }

    /// <summary>A pin by "Part.Pin" (the pin by name, id or number, any case), or an error saying what the part has.</summary>
    public static Terminal Find(Circuit circuit, string pin)
    {
        var text = pin?.Trim() ?? "";
        var dot = text.IndexOf('.');
        if (dot <= 0 || dot == text.Length - 1)
            throw new ArgumentException($"\"{pin}\" isn't a pin. Name a pin as Part.Pin, e.g. \"R1.A\" or \"U1.OUT\" (describe_part or get_part lists a part's pins).");
        var part = Part(circuit, text[..dot]);
        var name = text[(dot + 1)..];
        var byName = part.Terminals.Where(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byName.Count == 1) return byName[0];
        var byId = part.Terminals.FirstOrDefault(t => string.Equals(t.Id, name, StringComparison.OrdinalIgnoreCase));
        if (byId != null) return byId;
        if (int.TryParse(name, out var number) && part.Terminals.FirstOrDefault(t => t.Id == $"p{number}") is { } numbered) return numbered;
        if (byName.Count > 1)
            throw new ArgumentException($"{part.Name} has {byName.Count} pins called {name}: name one by its id, {string.Join(" or ", byName.Select(t => $"{part.Name}.{t.Id}"))}.");
        throw new ArgumentException($"{part.Name} ({part.ComponentType}) has no pin {name}. Its pins: {string.Join(", ", part.Terminals.Select(t => Name(t)[(part.Name.Length + 1)..]))}.");
    }
}

/// <summary>Circuits, parts and nets described for an agent, as JSON.</summary>
public static class Census
{
    /// <summary>
    /// The whole circuit: its parts (with values and places), its nets (each one's pins), pins wired to nothing, its
    /// probes and parameters, and the conditions it runs at.
    /// </summary>
    public static JsonObject Of(ICircuitSession session)
    {
        var circuit = session.Circuit;
        var o = new JsonObject
        {
            ["title"] = circuit.Title,
            ["file"] = session.FilePath,
            ["modified"] = session.IsModified,
            ["temperature_c"] = circuit.AmbientTemperatureCelsius,
        };
        if (circuit.AdaptiveTimeStep) o["adaptive_time_step"] = true;
        if (circuit.Sheets.Count > 0) o["sheets"] = new JsonArray(circuit.Sheets.Select(s => (JsonNode)s).ToArray());
        o["parts"] = new JsonArray(circuit.Components.Select(c => (JsonNode)Brief(circuit, c)).ToArray());
        var (nets, loose) = Nets(circuit);
        o["nets"] = nets;
        if (loose.Count > 0) o["unconnected_pins"] = loose;
        o["wires"] = circuit.Wires.Count;
        o["probes"] = Probes(circuit);
        if (circuit.Parameters.Count > 0)
            o["parameters"] = new JsonArray(circuit.Parameters.Select(p => (JsonNode)new JsonObject { ["name"] = p.Name, ["expression"] = p.Expression }).ToArray());
        if (circuit.Components.Count > 0 && !circuit.Components.OfType<IGroundReference>().Any())
            o["warning"] = "There's no Ground part: every circuit needs one (add_part \"Ground\") as the 0 V reference its voltages are measured from.";
        return o;
    }

    /// <summary>A part in one line's worth: name, type, value, place.</summary>
    public static JsonObject Brief(Circuit circuit, CircuitComponent c)
    {
        var o = new JsonObject { ["name"] = c.Name, ["type"] = c.ComponentType };
        if (c.ValueLabel.Length > 0) o["value"] = c.ValueLabel;
        o["at"] = new JsonArray(c.X, c.Y);
        if (c.RotationDegrees != 0) o["rotation"] = c.RotationDegrees;
        if (circuit.Sheets.Count > 0) o["sheet"] = circuit.Sheets.FirstOrDefault(s => circuit.Belongs(c, s));
        return o;
    }

    /// <summary>A part in full: its pins (where each is, and what it's wired to) and every setting.</summary>
    public static JsonObject Part(Circuit circuit, CircuitComponent c)
    {
        var o = Brief(circuit, c);
        var netlist = circuit.BuildNetlist();
        var pins = new JsonArray();
        foreach (var t in c.Terminals)
        {
            var p = new JsonObject { ["pin"] = Pins.Name(t), ["kind"] = t.Type.ToString(), ["at"] = new JsonArray(t.AbsolutePosition.X, t.AbsolutePosition.Y) };
            if (!string.Equals(t.Id, t.Name, StringComparison.OrdinalIgnoreCase)) p["id"] = t.Id;
            if (netlist.Contains(t))
            {
                var net = netlist.NetOf(t);
                p["net"] = net.Name;
                var others = net.Terminals.Where(x => !x.Equals(t)).Select(x => (JsonNode)Pins.Name(x)).ToArray();
                if (others.Length > 0) p["connected_to"] = new JsonArray(others);
            }
            pins.Add(p);
        }
        o["pins"] = pins;
        o["settings"] = Settings.Describe(c);
        return o;
    }

    /// <summary>
    /// The nets with more than one pin on them (each one's name and pins), and the pins on no wire at all. A net is
    /// called by its net label, GND for ground, or N and its number.
    /// </summary>
    public static (JsonArray Nets, JsonArray Loose) Nets(Circuit circuit)
    {
        var netlist = circuit.BuildNetlist();
        var nets = new JsonArray();
        var loose = new JsonArray();
        foreach (var net in netlist.Nets.OrderBy(n => n.IsGround ? 0 : 1).ThenBy(n => n.Index))
        {
            var pins = net.Terminals.Where(t => t.Owner is not null).ToList();
            if (pins.Count == 1 && !net.IsGround && net.Label is null)
            {
                loose.Add(Pins.Name(pins[0]));
                continue;
            }
            if (pins.Count == 0) continue;
            nets.Add(new JsonObject { ["name"] = net.Name, ["pins"] = new JsonArray(pins.Select(t => (JsonNode)Pins.Name(t)).ToArray()) });
        }
        // Pins the netlist never saw (parts it leaves out) count as wired to nothing too.
        foreach (var c in circuit.Components)
            foreach (var t in c.Terminals)
                if (!netlist.Contains(t)) loose.Add(Pins.Name(t));
        return (nets, loose);
    }

    public static JsonArray Probes(Circuit circuit) =>
        new(circuit.Probes.Select(p =>
        {
            var o = new JsonObject { ["label"] = p.Label, ["kind"] = p.Kind.ToString().ToLowerInvariant() };
            if (p.TargetTerminal is { } t) o["pin"] = Pins.Name(t);
            if (p.IsDerived) o["reference"] = p.ReferenceTerminal is { } r ? Pins.Name(r) : "ground";
            return (JsonNode)o;
        }).ToArray());

    /// <summary>The probe labelled <paramref name="label"/>, or an error listing them.</summary>
    public static SignalProbe Probe(Circuit circuit, string label) =>
        circuit.Probes.FirstOrDefault(p => string.Equals(p.Label, label?.Trim(), StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException(circuit.Probes.Count == 0
            ? $"There's no probe {label}: the circuit has none. add_probe puts one on a pin."
            : $"There's no probe {label}. The probes: {string.Join(", ", circuit.Probes.Select(p => p.Label))}.");
}
