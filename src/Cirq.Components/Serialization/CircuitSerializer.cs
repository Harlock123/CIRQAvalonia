using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Cirq.Components.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Topology;

namespace Cirq.Components.Serialization;

/// <summary>Anything that stopped a saved circuit from being loaded exactly as written.</summary>
public sealed class CircuitLoadResult
{
    public required Circuit Circuit { get; init; }

    public required CircuitDocument Document { get; init; }

    /// <summary>
    /// Non-fatal problems: an unknown component type, a wire whose endpoint went missing, a device
    /// model that is no longer in the library. The circuit still loads; these say what was lost.
    /// </summary>
    public List<string> Warnings { get; } = [];

    public bool IsClean => Warnings.Count == 0;
}

public sealed class CircuitFormatException : Exception
{
    public CircuitFormatException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Reads and writes circuits as JSON.
/// <para>
/// Components are persisted by type name plus the parameters
/// <see cref="ComponentReflection.EditableProperties"/> reports, so a new component type is
/// saveable the moment it exists — there is no registry to forget to update. Wires and probes
/// refer to terminals as (component id, pin id), which survives a round trip without relying on
/// object identity.
/// </para>
/// </summary>
public static class CircuitSerializer
{
    public const int CurrentVersion = 1;

    public const string FileExtension = ".cirq";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Component types that need a value at construction time to shape their pins.</summary>
    private static readonly Dictionary<string, Func<ComponentRecord, CircuitComponent>> CustomFactories = new()
    {
        [nameof(LogicGate)] = record => new LogicGate(
            ReadConstruction(record, "Function", GateFunction.And),
            ReadConstruction(record, "InputCount", 2)),
    };

    // ---- writing ---------------------------------------------------------

    public static CircuitDocument ToDocument(Circuit circuit)
    {
        var document = new CircuitDocument
        {
            Title = circuit.Title,
            SavedUtc = DateTimeOffset.UtcNow,
        };

        foreach (var component in circuit.Components)
            document.Components.Add(ToRecord(component));

        foreach (var wire in circuit.Wires)
        {
            if (wire.SourceTerminal?.Owner is null || wire.TargetTerminal?.Owner is null) continue;

            document.Wires.Add(new WireRecord
            {
                Id = wire.Id,
                From = Reference(wire.SourceTerminal),
                To = Reference(wire.TargetTerminal),
                Waypoints = wire.Waypoints.Count == 0
                    ? null
                    : [.. wire.Waypoints.Select(p => new PointRecord(p.X, p.Y))],
            });
        }

        foreach (var probe in circuit.Probes)
        {
            if (probe.TargetTerminal?.Owner is null) continue;

            document.Probes.Add(new ProbeRecord
            {
                Label = probe.Label,
                Target = Reference(probe.TargetTerminal),
                Color = probe.TraceColor.ToHex(),
                Kind = probe.Kind.ToString(),
                IsVisible = probe.IsVisible,
                AcCoupled = probe.AcCoupled,
            });
        }

        return document;
    }

    public static string ToJson(Circuit circuit) =>
        JsonSerializer.Serialize(ToDocument(circuit), WriteOptions);

    public static void Save(Circuit circuit, string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // Write to a temporary file and move it into place, so an interrupted save cannot leave a
        // half-written circuit where the original used to be.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, ToJson(circuit));
        File.Move(temporary, path, overwrite: true);
    }

    private static ComponentRecord ToRecord(CircuitComponent component)
    {
        var record = new ComponentRecord
        {
            Id = component.Id,
            Type = component.GetType().Name,
            Name = component.Name,
            X = component.X,
            Y = component.Y,
            Rotation = component.RotationDegrees,
        };

        if (component is LogicGate gate)
        {
            record.Construction = new Dictionary<string, JsonElement>
            {
                ["Function"] = JsonSerializer.SerializeToElement(gate.Function.ToString()),
                ["InputCount"] = JsonSerializer.SerializeToElement(gate.InputTerminals.Count),
            };
        }

        foreach (var property in ComponentReflection.EditableProperties(component.GetType()))
        {
            var value = property.GetValue(component);
            if (value is null) continue;

            record.Parameters[property.Name] = ComponentReflection.IsModelProperty(property)
                ? JsonSerializer.SerializeToElement(value.ToString())
                : SerializeValue(value);
        }

        return record;
    }

    private static JsonElement SerializeValue(object value) => value switch
    {
        Color colour => JsonSerializer.SerializeToElement(colour.ToHex()),
        Enum e => JsonSerializer.SerializeToElement(e.ToString()),
        _ => JsonSerializer.SerializeToElement(value),
    };

    private static TerminalReference Reference(Terminal terminal) => new()
    {
        Component = terminal.Owner!.Id,
        Terminal = terminal.Id,
    };

    // ---- reading ---------------------------------------------------------

    public static CircuitLoadResult FromJson(string json)
    {
        CircuitDocument? document;

        try
        {
            document = JsonSerializer.Deserialize<CircuitDocument>(json, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new CircuitFormatException("The file is not valid circuit JSON.", ex);
        }

        if (document is null) throw new CircuitFormatException("The file is empty.");

        if (document.Version > CurrentVersion)
            throw new CircuitFormatException(
                $"This circuit was saved by a newer version of the application (format {document.Version}, " +
                $"this build understands {CurrentVersion}).");

        return Rebuild(document);
    }

    public static CircuitLoadResult Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Circuit file not found.", path);
        return FromJson(File.ReadAllText(path));
    }

    private static CircuitLoadResult Rebuild(CircuitDocument document)
    {
        var circuit = new Circuit { Title = document.Title };
        var result = new CircuitLoadResult { Circuit = circuit, Document = document };
        var byId = new Dictionary<Guid, CircuitComponent>();

        foreach (var record in document.Components)
        {
            var component = TryCreate(record, result);
            if (component is null) continue;

            component.Name = record.Name;
            component.X = record.X;
            component.Y = record.Y;
            component.RotationDegrees = record.Rotation;

            ApplyParameters(component, record, result);

            // Preserve the saved identity so wires and probes can find it again.
            var placed = CloneWithId(component, record.Id);
            circuit.Components.Add(placed);
            byId[record.Id] = placed;
        }

        foreach (var record in document.Wires)
        {
            var from = Resolve(record.From, byId, result);
            var to = Resolve(record.To, byId, result);
            if (from is null || to is null) continue;

            var wire = new WireSegment { SourceTerminal = from, TargetTerminal = to };
            foreach (var point in record.Waypoints ?? []) wire.Waypoints.Add(new Point(point.X, point.Y));
            circuit.Wires.Add(wire);
        }

        foreach (var record in document.Probes)
        {
            var terminal = Resolve(record.Target, byId, result);
            if (terminal is null) continue;

            var probe = new SignalProbe
            {
                Label = record.Label,
                TargetTerminal = terminal,
                TraceColor = ParseColour(record.Color),
                IsVisible = record.IsVisible,
                AcCoupled = record.AcCoupled,
            };

            if (Enum.TryParse<ProbeKind>(record.Kind, ignoreCase: true, out var kind)) probe.Kind = kind;
            circuit.Probes.Add(probe);
        }

        return result;
    }

    private static CircuitComponent? TryCreate(ComponentRecord record, CircuitLoadResult result)
    {
        if (CustomFactories.TryGetValue(record.Type, out var factory))
        {
            try
            {
                return factory(record);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Could not rebuild '{record.Name}' ({record.Type}): {ex.Message}");
                return null;
            }
        }

        var type = KnownComponentTypes.Value.GetValueOrDefault(record.Type);
        if (type is null)
        {
            result.Warnings.Add(
                $"Skipped '{record.Name}': this build has no component type called '{record.Type}'.");
            return null;
        }

        try
        {
            return ComponentReflection.Instantiate(type);
        }
        catch (Exception ex)
        {
            result.Warnings.Add($"Could not create '{record.Name}' ({record.Type}): {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Components generate their own id, so a freshly built one is copied onto an instance
    /// carrying the saved id. Terminals are rebuilt with it, which keeps their owner correct.
    /// </summary>
    private static CircuitComponent CloneWithId(CircuitComponent component, Guid id)
    {
        if (component.Id == id) return component;

        var field = typeof(CircuitComponent)
            .GetProperty(nameof(CircuitComponent.Id))!
            .GetBackingField();

        field?.SetValue(component, id);
        return component;
    }

    private static void ApplyParameters(
        CircuitComponent component, ComponentRecord record, CircuitLoadResult result)
    {
        var properties = ComponentReflection.EditableProperties(component.GetType())
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (var (name, element) in record.Parameters)
        {
            if (!properties.TryGetValue(name, out var property)) continue;

            try
            {
                var value = DeserializeValue(property, element, component, result, record.Name);
                if (value is not null || Nullable.GetUnderlyingType(property.PropertyType) is not null)
                    property.SetValue(component, value);
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"'{record.Name}': could not restore {name} ({ex.Message}).");
            }
        }
    }

    private static object? DeserializeValue(
        PropertyInfo property, JsonElement element, CircuitComponent component,
        CircuitLoadResult result, string componentName)
    {
        var declared = property.PropertyType;
        var target = Nullable.GetUnderlyingType(declared) ?? declared;

        if (element.ValueKind is JsonValueKind.Null) return null;

        // Device models are stored by name and resolved against their library.
        if (ComponentReflection.ModelLibrary(target) is { Count: > 0 } library)
        {
            var wanted = element.GetString();
            var match = library.FirstOrDefault(m =>
                string.Equals(m.ToString(), wanted, StringComparison.OrdinalIgnoreCase));

            if (match is not null) return match;

            result.Warnings.Add(
                $"'{componentName}': model '{wanted}' is not in this build's library, keeping the default.");
            return property.GetValue(component);
        }

        if (target == typeof(Color)) return ParseColour(element.GetString());

        if (target.IsEnum)
        {
            var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
            return Enum.Parse(target, text!, ignoreCase: true);
        }

        if (target == typeof(string)) return element.GetString();
        if (target == typeof(bool)) return element.GetBoolean();

        if (target == typeof(double) || target == typeof(float) ||
            target == typeof(int) || target == typeof(long) || target == typeof(decimal))
        {
            var number = element.ValueKind == JsonValueKind.String
                ? double.Parse(element.GetString()!, CultureInfo.InvariantCulture)
                : element.GetDouble();

            return Convert.ChangeType(number, target, CultureInfo.InvariantCulture);
        }

        return JsonSerializer.Deserialize(element.GetRawText(), target, ReadOptions);
    }

    private static Terminal? Resolve(
        TerminalReference reference, IReadOnlyDictionary<Guid, CircuitComponent> byId, CircuitLoadResult result)
    {
        if (!byId.TryGetValue(reference.Component, out var component))
        {
            result.Warnings.Add($"Dropped a connection to a component that is not in the file.");
            return null;
        }

        var terminal = component.Terminals.FirstOrDefault(t => t.Id == reference.Terminal);
        if (terminal is null)
        {
            result.Warnings.Add(
                $"Dropped a connection: '{component.Name}' has no pin '{reference.Terminal}'.");
        }

        return terminal;
    }

    private static Color ParseColour(string? hex)
    {
        try
        {
            return string.IsNullOrWhiteSpace(hex) ? Color.Yellow : Color.FromHex(hex);
        }
        catch (FormatException)
        {
            return Color.Yellow;
        }
    }

    private static T ReadConstruction<T>(ComponentRecord record, string key, T fallback)
    {
        if (record.Construction is null || !record.Construction.TryGetValue(key, out var element))
            return fallback;

        if (typeof(T).IsEnum)
        {
            var text = element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
            return Enum.TryParse(typeof(T), text, ignoreCase: true, out var parsed) ? (T)parsed! : fallback;
        }

        if (typeof(T) == typeof(int))
            return element.TryGetInt32(out var number) ? (T)(object)number : fallback;

        return fallback;
    }

    /// <summary>Every concrete component type in the library, keyed by class name.</summary>
    private static readonly Lazy<Dictionary<string, Type>> KnownComponentTypes = new(() =>
        typeof(CircuitSerializer).Assembly.GetTypes()
            .Where(t => t is { IsAbstract: false, IsClass: true } && typeof(CircuitComponent).IsAssignableFrom(t))
            .ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase));
}

internal static class ReflectionExtensions
{
    /// <summary>The compiler-generated backing field of an auto-property.</summary>
    public static FieldInfo? GetBackingField(this PropertyInfo property) =>
        property.DeclaringType?.GetField(
            $"<{property.Name}>k__BackingField",
            BindingFlags.Instance | BindingFlags.NonPublic);
}
