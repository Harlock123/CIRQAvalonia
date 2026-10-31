using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cirq.Components.Serialization;
using Cirq.Core.Topology;
using Cirq.Core.Units;

namespace Cirq.Mcp;

/// <summary>
/// A part's settings as an agent sees them: the same properties the properties panel and the file format use
/// (<see cref="ComponentReflection.EditableProperties"/>), described with their types, units and choices, and set from
/// JSON values the way the panel sets them from text.
/// </summary>
public static class Settings
{
    /// <summary>
    /// Units by what a property's name ends in, as the properties panel labels them. Only words: nothing reads these
    /// but the agent.
    /// </summary>
    private static readonly (string Suffix, string Unit)[] UnitHints =
    [
        ("Resistance", "Ω"), ("Capacitance", "F"), ("Inductance", "H"), ("Frequency", "Hz"), ("Voltage", "V"),
        ("Current", "A"), ("Delay", "s"), ("Time", "s"), ("Temperature", "°C"), ("SlewRate", "V/s"),
        ("Vcc", "V"), ("Vih", "V"), ("Vil", "V"), ("Voh", "V"), ("Vol", "V"), ("Drop", "V"), ("Threshold", "V"),
        ("High", "V"), ("Low", "V"),
    ];

    public static string UnitFor(string propertyName)
    {
        foreach (var (suffix, unit) in UnitHints)
            if (propertyName.Contains(suffix, StringComparison.OrdinalIgnoreCase)) return unit;
        return "";
    }

    /// <summary>Every setting of a part: name, kind, value now, unit, and the choices for a choice or a model.</summary>
    public static JsonArray Describe(CircuitComponent component)
    {
        var list = new JsonArray();
        foreach (var property in ComponentReflection.EditableProperties(component.GetType()))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var o = new JsonObject { ["name"] = property.Name, ["type"] = KindOf(property) };
            o["value"] = ToJson(property.GetValue(component));
            if ((type == typeof(double) || type == typeof(int)) && UnitFor(property.Name) is { Length: > 0 } unit) o["unit"] = unit;
            if (Nullable.GetUnderlyingType(property.PropertyType) != null) o["optional"] = true;
            if (type.IsEnum) o["choices"] = new JsonArray(Enum.GetNames(type).Select(n => (JsonNode)n).ToArray());
            else if (ComponentReflection.ModelLibrary(type) is { Count: > 0 } library)
                o["choices"] = new JsonArray(library.Select(m => (JsonNode)(m.ToString() ?? "")).ToArray());
            if (component.Expressions.TryGetValue(property.Name, out var expression)) o["expression"] = expression;
            if (property.GetCustomAttribute<OperableAttribute>() is { } operable) o["control"] = operable.Label;
            list.Add(o);
        }
        return list;
    }

    private static string KindOf(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        return type == typeof(double) ? "number"
            : type == typeof(int) ? "whole number"
            : type == typeof(bool) ? "true/false"
            : type == typeof(string) ? "text"
            : type.IsEnum ? "choice"
            : ComponentReflection.ModelLibrary(type) is { Count: > 0 } ? "model"
            : type.Name;
    }

    /// <summary>A setting's value as JSON: numbers, text, true/false, choices and models by name.</summary>
    public static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        double d when double.IsFinite(d) => d,
        double d => d.ToString(CultureInfo.InvariantCulture),
        int i => i,
        bool b => b,
        string s => s,
        Enum e => e.ToString(),
        _ => value.ToString(),
    };

    /// <summary>
    /// Sets settings by name. Numbers may be JSON numbers or text in engineering notation ("4k7", "100n", "2.2M",
    /// "10 kHz"); text beginning with "=" binds the setting to an expression over the circuit's parameters. Returns
    /// whether any change alters the circuit's shape (a model, or a net label's name), which rebuilds the simulation.
    /// </summary>
    public static bool Apply(CircuitComponent component, Circuit circuit, IReadOnlyDictionary<string, JsonElement> values)
    {
        var properties = ComponentReflection.EditableProperties(component.GetType()).ToList();
        var structural = false;
        var bound = false;
        foreach (var (name, value) in values)
        {
            var property = properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(UnknownSetting(component, properties, name));
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            var nullable = Nullable.GetUnderlyingType(property.PropertyType) != null;
            var what = $"{component.Name}'s {property.Name}";

            if (value.ValueKind == JsonValueKind.Null)
            {
                if (!nullable) throw new ArgumentException($"{what} needs a value; it can't be left empty.");
                component.Expressions.Remove(property.Name);
                property.SetValue(component, null);
                continue;
            }

            if (type == typeof(double) || type == typeof(int))
            {
                if (value.ValueKind == JsonValueKind.String && value.GetString()!.Trim() is { Length: > 1 } text && text.StartsWith('='))
                {
                    component.Expressions[property.Name] = text[1..].Trim();
                    bound = true;
                    continue;
                }
                var number = Number(value, what);
                component.Expressions.Remove(property.Name);
                property.SetValue(component, type == typeof(double) ? number : Whole(number, what));
            }
            else if (type == typeof(bool))
            {
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException($"{what} is true or false.");
                property.SetValue(component, value.GetBoolean());
            }
            else if (type == typeof(string))
            {
                if (value.ValueKind != JsonValueKind.String) throw new ArgumentException($"{what} is text.");
                property.SetValue(component, value.GetString());
                if (component is INetNaming) structural = true;
            }
            else if (type.IsEnum)
            {
                var choice = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                if (choice is null || int.TryParse(choice, out _) || !Enum.TryParse(type, choice.Trim(), ignoreCase: true, out var parsed) || !Enum.IsDefined(type, parsed!))
                    throw new ArgumentException($"{what} is one of {string.Join(", ", Enum.GetNames(type))}, not {value.GetRawText()}.");
                property.SetValue(component, parsed);
                structural = true;
            }
            else if (ComponentReflection.ModelLibrary(type) is { Count: > 0 } library)
            {
                var choice = value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
                var model = library.FirstOrDefault(m => string.Equals(m.ToString(), choice, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ArgumentException($"{what} is a model by name, one of {string.Join(", ", library.Select(m => m.ToString()))}; not {value.GetRawText()}.");
                property.SetValue(component, model);
                structural = true;
            }
            else
            {
                throw new ArgumentException($"{what} can't be set by an agent (it is a {type.Name}). Set it in the properties panel.");
            }
        }

        if (bound)
        {
            var applied = CircuitParameters.Apply(circuit.Parameters, circuit.Components);
            var problems = applied.Problems.Where(p => p.Key.StartsWith(component.Name + ".", StringComparison.Ordinal)).ToList();
            if (problems.Count > 0)
                throw new ArgumentException(string.Join(" ", problems.Select(p => $"{p.Key}: {p.Value}")) + " Expressions use the circuit's parameters (set_parameter) and arithmetic, e.g. \"=R * 2\".");
        }
        return structural;
    }

    /// <summary>A number from a JSON number or engineering text ("4k7", "100n", "10 kHz").</summary>
    public static double Number(JsonElement value, string what)
    {
        if (value.ValueKind == JsonValueKind.Number) return value.GetDouble();
        if (value.ValueKind == JsonValueKind.String && SiPrefix.TryParse(value.GetString(), out var parsed)) return parsed;
        throw new ArgumentException($"{what} is a number, as 4700 or in engineering notation as \"4k7\", \"100n\", \"2.2M\" (m is milli, M is mega); not {value.GetRawText()}.");
    }

    private static int Whole(double value, string what)
    {
        if (value != Math.Floor(value) || value < int.MinValue || value > int.MaxValue) throw new ArgumentException($"{what} is a whole number, not {value}.");
        return (int)value;
    }

    private static string UnknownSetting(CircuitComponent component, IReadOnlyList<PropertyInfo> properties, string name)
    {
        var names = properties.Select(p => p.Name).ToList();
        if (name is "Name" or "name" or "X" or "x" or "Y" or "y" or "Rotation" or "rotation")
            return $"{name} isn't a setting: give it as set_part's own new_name, x, y or rotation.";
        var near = names.OrderBy(n => Text.Distance(n.ToLowerInvariant(), name.ToLowerInvariant())).Take(3);
        return names.Count == 0
            ? $"{component.Name} ({component.ComponentType}) has no settings."
            : $"{component.Name} ({component.ComponentType}) has no setting \"{name}\". Did you mean {string.Join(", ", near.Select(n => $"\"{n}\""))}? get_part lists them all.";
    }
}

internal static class Text
{
    /// <summary>The edit distance between two words, for "did you mean".</summary>
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    /// <summary>The nearest of some names to one asked for, quoted.</summary>
    public static string Nearest(IEnumerable<string> names, string asked, int count = 3) =>
        string.Join(", ", names.OrderBy(n => Distance(n.ToLowerInvariant(), (asked ?? "").ToLowerInvariant())).Take(count).Select(n => $"\"{n}\""));
}
