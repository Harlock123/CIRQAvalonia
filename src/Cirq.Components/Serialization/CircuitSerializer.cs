using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Cirq.Components.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Verification;
using Cirq.Components.Hierarchy;
using Cirq.Components.Spice;
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
    /// <summary>
    /// The file format's version, and the number a build refuses to read above.
    /// <para>
    /// <b>Adding something never moves this.</b> A new optional field, a new component type, a new
    /// parameter on an existing one, a new kind of record — all of those are already survivable by
    /// a build that has never heard of them: unknown JSON members are ignored on load, an unknown
    /// component type is a warning with the rest of the circuit still opening, and an unknown value
    /// in a known field is kept and reported rather than quietly read as something else. A circuit
    /// saved by a newer build opens in an older one, minus the parts it cannot represent.
    /// </para>
    /// <para>
    /// It moves only when the <b>meaning of existing data changes</b> — a field that used to be
    /// ohms becoming siemens, a sign convention flipping, a list that used to be ordered no longer
    /// being. That is the one case where an old build would read the file successfully and be
    /// confidently wrong, and refusing it outright is better than that.
    /// </para>
    /// <para>
    /// Bumping it for an addition costs everything and buys nothing: every build already in the
    /// world would refuse every new file, for a change those builds could have ignored. The
    /// forward-compatibility tests exist to keep that distinction honest.
    /// </para>
    /// </summary>
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

    /// <summary>
    /// An independent copy of one component: same type, same parameters, same place and rotation,
    /// and nothing shared with the original.
    /// <para>
    /// It goes out through the save path and back in through the load path rather than copying
    /// fields, and that is deliberate. Those two already know which properties define a component,
    /// which ones need a value at construction time to shape the pins, and how a model reference
    /// is written down — and they are exercised by every example in the test suite. A second
    /// implementation of "what a component is" would be a second thing to keep up to date, and the
    /// copy would quietly start losing whichever property the newer one forgot.
    /// </para>
    /// <para>
    /// The copy comes back with <b>no name</b> and a new identity, so adding it to a circuit gives
    /// it the next free designator instead of a second R4.
    /// </para>
    /// </summary>
    /// <param name="component">What to copy.</param>
    /// <param name="warnings">Collects anything that did not survive the round trip, if given.</param>
    public static CircuitComponent Clone(CircuitComponent component, ICollection<string>? warnings = null)
    {
        ArgumentNullException.ThrowIfNull(component);

        var collected = warnings ?? [];
        var record = ToRecord(component);

        var copy = TryCreate(record, collected)
            ?? throw new CircuitFormatException($"Cannot copy '{component.Name}' ({record.Type}).");

        copy.X = record.X;
        copy.Y = record.Y;
        copy.RotationDegrees = record.Rotation;
        copy.Sheet = record.Sheet ?? string.Empty;

        ApplyParameters(copy, record, collected);

        // Left blank on purpose: Circuit.Add hands out the next designator, which is what makes a
        // pasted part R5 rather than a duplicate of the R4 it came from.
        copy.Name = string.Empty;

        return copy;
    }

    // ---- writing ---------------------------------------------------------

    public static CircuitDocument ToDocument(Circuit circuit)
    {
        var document = new CircuitDocument
        {
            Title = circuit.Title,
            SavedUtc = DateTimeOffset.UtcNow,
        };

        // Flattened: a block's contents are written out alongside everything else, and the block
        // itself carries a small record saying which of them are its.
        foreach (var component in Flattening.Flatten(circuit.Components))
        {
            var record = ToRecord(component);

            if (component is ISubcircuit block)
            {
                record.Block = new BlockRecord
                {
                    Components = [.. block.InnerComponents.Select(c => c.Id)],
                    Wires = [.. block.InnerWires.Select(w => w.Id)],
                    Ports =
                    [
                        .. block.Ports.Select(port => new PortRecord
                        {
                            Name = port.Outer.Name,
                            Inner = Reference(port.Inner),
                        }),
                    ],
                };
            }

            document.Components.Add(record);
        }

        foreach (var wire in Flattening.FlattenWires(circuit.Components, circuit.Wires))
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

        // Only when it is not the default, so an ordinary circuit's file does not grow a line
        // saying it is at room temperature.
        if (Math.Abs(circuit.AmbientTemperatureCelsius - 27.0) > 1e-9)
            document.AmbientTemperatureCelsius = circuit.AmbientTemperatureCelsius;

        if (circuit.AdaptiveTimeStep) document.AdaptiveTimeStep = true;

        if (circuit.Revision.Trim().Length > 0) document.Revision = circuit.Revision.Trim();
        if (circuit.Author.Trim().Length > 0) document.Author = circuit.Author.Trim();

        foreach (var probe in circuit.Probes)
        {
            if (probe.TargetTerminal?.Owner is null) continue;

            document.Probes.Add(new ProbeRecord
            {
                Label = probe.Label,
                Target = Reference(probe.TargetTerminal),
                Color = probe.TraceColor.ToHex(),
                Kind = probe.Kind.ToString(),
                Reference = probe.ReferenceTerminal is { Owner: not null }
                    ? Reference(probe.ReferenceTerminal)
                    : null,
                IsVisible = probe.IsVisible,
                AcCoupled = probe.AcCoupled,
            });
        }

        document.Models = EmbeddedModels(circuit);

        if (circuit.Specs.Count > 0)
        {
            document.Specs = [.. circuit.Specs.Select(spec => new SpecRecord
            {
                Id = spec.Id,
                Name = spec.Name,
                Trace = spec.Trace,
                Against = spec.Against.Length == 0 ? null : spec.Against,
                Quantity = spec.Quantity.ToString(),
                Comparison = spec.Comparison.ToString(),
                Limit = spec.Limit,
                Tolerance = spec.Tolerance,
                Unit = spec.Unit,
                IsEnabled = spec.IsEnabled,
            })];
        }

        if (circuit.Sheets.Count > 0) document.Sheets = [.. circuit.Sheets];

        if (circuit.Parameters.Count > 0)
        {
            document.Parameters = [.. circuit.Parameters.Select(p => new ParameterRecord
            {
                Id = p.Id,
                Name = p.Name,
                Expression = p.Expression,
                Note = p.Note,
            })];
        }

        if (!circuit.Baseline.IsEmpty)
        {
            document.Baseline = new BaselineRecord
            {
                Taken = circuit.Baseline.Taken,
                Note = circuit.Baseline.Note,
                Traces = [.. circuit.Baseline.Traces.Select(trace => new BaselineTraceRecord
                {
                    Label = trace.Label,
                    Unit = trace.Unit,
                    Times = [.. trace.Samples.Select(p => p.Time)],
                    Values = [.. trace.Samples.Select(p => p.Value)],
                })],
            };
        }

        return document;
    }

    /// <summary>
    /// The cards for every imported model the circuit uses, so the file carries its own parts.
    /// <para>
    /// Only what is <i>used</i>, and only what was <i>imported</i>. A file does not copy somebody's
    /// whole library into itself — that would make every saved circuit an unasked-for export of
    /// everything on the machine that wrote it — and it does not copy built-ins, which are the
    /// same everywhere and already travel.
    /// </para>
    /// </summary>
    private static List<ModelRecord>? EmbeddedModels(Circuit circuit)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in Flattening.Flatten(circuit.Components))
        {
            foreach (var property in ComponentReflection.EditableProperties(component.GetType()))
            {
                if (!ComponentReflection.IsModelProperty(property)) continue;

                if (property.GetValue(component)?.ToString() is { Length: > 0 } name) names.Add(name);
            }
        }

        List<ModelRecord> records = [];

        foreach (var name in names)
        {
            if (SpiceModelImport.CardFor(name) is not { } card) continue;

            records.Add(new ModelRecord { Name = card.Name, Card = card.ToCard() });
        }

        return records.Count == 0 ? null : records;
    }

    public static string ToJson(Circuit circuit) =>
        JsonSerializer.Serialize(ToDocument(circuit), WriteOptions);

    /// <summary>
    /// The same JSON with the save time left out, for comparing two states of a document.
    /// <para>
    /// A saved file wants to say when it was saved. A <i>snapshot</i> does not, and the timestamp
    /// actively gets in the way: two serialisations of an unchanged circuit differ in it, so
    /// anything comparing them to ask "did this actually change" always gets yes. The undo history
    /// asks exactly that, to avoid charging somebody a keystroke for an edit that set a value to
    /// what it already was.
    /// </para>
    /// </summary>
    public static string ToComparableJson(Circuit circuit)
    {
        var document = ToDocument(circuit);

        document.SavedUtc = default;

        return JsonSerializer.Serialize(document, WriteOptions);
    }

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

        if (component.Expressions.Count > 0)
            record.Expressions = new Dictionary<string, string>(component.Expressions);

        if (component.Sheet.Length > 0) record.Sheet = component.Sheet;

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

    /// <summary>
    /// Registers the models a file brought with it, so the components in it resolve.
    /// <para>
    /// A name already in a library <b>wins</b>. Opening a file must not quietly rewrite somebody's
    /// parts: if their 2N3904 and the file's disagree, theirs is the one they trimmed against
    /// every other circuit on the machine, and silently swapping it would change answers in
    /// circuits they were not even looking at. The disagreement is reported instead, which is the
    /// one thing that lets it be sorted out.
    /// </para>
    /// <para>
    /// What is registered stays registered for the session, and shows up in the parts library
    /// alongside everything else — but it is not written to the machine's own imported models. The
    /// file is where it lives; closing the file is where it goes.
    /// </para>
    /// </summary>
    private static void RegisterEmbeddedModels(CircuitDocument document, ICollection<string> warnings)
    {
        if (document.Models is not { Count: > 0 } models) return;

        foreach (var record in models)
        {
            var parsed = SpiceModelReader.Parse(record.Card);

            foreach (var problem in parsed.Problems)
                warnings.Add($"model '{record.Name}' saved in this file: {problem}");

            foreach (var card in parsed.Cards)
            {
                var existing = SpiceModelImport.Existing(card.Name, card.Kind);

                if (existing is null)
                {
                    SpiceModelImport.Register(card);
                    continue;
                }

                if (!Equals(existing, SpiceModelImport.Build(card)))
                {
                    warnings.Add(
                        $"'{card.Name}' is saved in this file but a different model of that name " +
                        "is already here; the one already here was used.");
                }
            }
        }
    }

    private static CircuitLoadResult Rebuild(CircuitDocument document)
    {
        var circuit = new Circuit { Title = document.Title };
        var result = new CircuitLoadResult { Circuit = circuit, Document = document };

        // Before any component is built: a component's model is resolved by name as it is created.
        RegisterEmbeddedModels(document, result.Warnings);

        var byId = new Dictionary<Guid, CircuitComponent>();

        foreach (var record in document.Components)
        {
            var component = TryCreate(record, result.Warnings);
            if (component is null) continue;

            component.Name = record.Name;
            component.X = record.X;
            component.Y = record.Y;
            component.Sheet = record.Sheet ?? string.Empty;
            component.RotationDegrees = record.Rotation;

            ApplyParameters(component, record, result.Warnings);

            // Preserve the saved identity so wires and probes can find it again.
            var placed = CloneWithId(component, record.Id);
            circuit.Components.Add(placed);
            byId[record.Id] = placed;
        }

        // Blocks get their pins back before any wire is resolved, because the wires outside a
        // block were saved against those pins. Rebuilding them in the saved order is what makes
        // the pin ids line up again.
        foreach (var record in document.Components)
        {
            if (record.Block is not { } blockRecord) continue;
            if (!byId.TryGetValue(record.Id, out var placed)) continue;
            if (placed is not Subcircuit block) continue;

            foreach (var port in blockRecord.Ports)
            {
                var inner = Resolve(port.Inner, byId, result.Warnings);
                if (inner is null) continue;

                block.AddPort(port.Name, inner);
            }
        }

        var wiresById = new Dictionary<Guid, WireSegment>();

        foreach (var record in document.Wires)
        {
            var from = Resolve(record.From, byId, result.Warnings);
            var to = Resolve(record.To, byId, result.Warnings);
            if (from is null || to is null) continue;

            var wire = new WireSegment { Id = record.Id, SourceTerminal = from, TargetTerminal = to };
            foreach (var point in record.Waypoints ?? []) wire.Waypoints.Add(new Point(point.X, point.Y));

            circuit.Wires.Add(wire);
            wiresById[record.Id] = wire;
        }

        // And now the contents move inside, off the sheet. Deepest blocks first, so a block that
        // is itself inside another has already taken its own contents before it is moved.
        foreach (var record in document.Components.AsEnumerable().Reverse())
        {
            if (record.Block is not { } blockRecord) continue;
            if (!byId.TryGetValue(record.Id, out var placed)) continue;
            if (placed is not Subcircuit block) continue;

            foreach (var id in blockRecord.Components)
            {
                if (!byId.TryGetValue(id, out var inner)) continue;

                block.AddInner(inner);
                circuit.Components.Remove(inner);
            }

            foreach (var id in blockRecord.Wires)
            {
                if (!wiresById.TryGetValue(id, out var inner)) continue;

                block.AddInnerWire(inner);
                circuit.Wires.Remove(inner);
            }
        }

        if (document.AmbientTemperatureCelsius is { } ambient)
            circuit.AmbientTemperatureCelsius = ambient;

        circuit.AdaptiveTimeStep = document.AdaptiveTimeStep ?? false;
        circuit.Revision = document.Revision ?? string.Empty;
        circuit.Author = document.Author ?? string.Empty;

        foreach (var record in document.Probes)
        {
            var terminal = Resolve(record.Target, byId, result.Warnings);
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

            // After the kind, because setting a kind clears the history and the reference alike.
            if (record.Reference is not null)
                probe.ReferenceTerminal = Resolve(record.Reference, byId, result.Warnings);

            circuit.Probes.Add(probe);
        }

        foreach (var record in document.Specs ?? [])
        {
            var spec = new DesignSpec
            {
                Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id,
                Name = record.Name,
                Trace = record.Trace,
                Against = record.Against ?? string.Empty,
                Limit = record.Limit,
                Tolerance = record.Tolerance,
                Unit = record.Unit,
                IsEnabled = record.IsEnabled,
            };

            // Stored by name, so a file written by a newer version that has grown a quantity this
            // one does not know about keeps the requirement rather than silently measuring the
            // wrong thing. It is reported instead.
            if (Enum.TryParse<SpecQuantity>(record.Quantity, ignoreCase: true, out var quantity))
            {
                spec.Quantity = quantity;
            }
            else
            {
                result.Warnings.Add(
                    $"Requirement \"{record.Name}\" measures \"{record.Quantity}\", which this " +
                    "version does not know how to measure. It was kept but turned off.");

                spec.IsEnabled = false;
            }

            if (Enum.TryParse<SpecComparison>(record.Comparison, ignoreCase: true, out var comparison))
                spec.Comparison = comparison;

            circuit.Specs.Add(spec);
        }

        foreach (var sheet in document.Sheets ?? []) circuit.Sheets.Add(sheet);

        foreach (var record in document.Parameters ?? [])
        {
            circuit.Parameters.Add(new CircuitParameter
            {
                Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id,
                Name = record.Name,
                Expression = record.Expression,
                Note = record.Note,
            });
        }

        // Worked out and written into the parts now, so that everything downstream — the solver,
        // the parts list, the canvas — reads plain numbers and knows nothing about expressions.
        // The values in the file are already right; this is what keeps them right when a parameter
        // is edited, and what reports a binding that has stopped making sense.
        if (circuit.Parameters.Count > 0 || circuit.Components.Any(c => c.Expressions.Count > 0))
        {
            foreach (var (_, problem) in CircuitParameters.Apply(circuit.Parameters, circuit.Components).Problems)
                result.Warnings.Add(problem);
        }

        if (document.Baseline is { } baseline) circuit.Baseline = Restore(baseline, result.Warnings);

        return result;
    }

    /// <summary>
    /// A recorded baseline, back off the file.
    /// <para>
    /// A trace whose two arrays are different lengths is dropped rather than truncated: the pairing
    /// between a time and a value is the whole of what the record means, and half a pairing is not
    /// a shorter waveform, it is an unknown one.
    /// </para>
    /// </summary>
    private static TraceBaseline Restore(BaselineRecord record, ICollection<string> warnings)
    {
        List<BaselineTrace> traces = [];

        foreach (var trace in record.Traces)
        {
            if (trace.Times.Count != trace.Values.Count)
            {
                warnings.Add(
                    $"The baseline for \"{trace.Label}\" has {trace.Times.Count} times against " +
                    $"{trace.Values.Count} values, so it was left out.");

                continue;
            }

            if (trace.Times.Count < 2) continue;

            List<DataPoint> samples = new(trace.Times.Count);

            for (var i = 0; i < trace.Times.Count; i++)
                samples.Add(new DataPoint(trace.Times[i], trace.Values[i]));

            traces.Add(new BaselineTrace(trace.Label, trace.Unit, samples));
        }

        return new TraceBaseline(record.Taken, record.Note, traces);
    }

    private static CircuitComponent? TryCreate(ComponentRecord record, ICollection<string> warnings)
    {
        if (CustomFactories.TryGetValue(record.Type, out var factory))
        {
            try
            {
                return factory(record);
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not rebuild '{record.Name}' ({record.Type}): {ex.Message}");
                return null;
            }
        }

        var type = KnownComponentTypes.Value.GetValueOrDefault(record.Type);
        if (type is null)
        {
            warnings.Add(
                $"Skipped '{record.Name}': this build has no component type called '{record.Type}'.");
            return null;
        }

        try
        {
            return ComponentReflection.Instantiate(type);
        }
        catch (Exception ex)
        {
            warnings.Add($"Could not create '{record.Name}' ({record.Type}): {ex.Message}");
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
        CircuitComponent component, ComponentRecord record, ICollection<string> warnings)
    {
        var properties = ComponentReflection.EditableProperties(component.GetType())
            .ToDictionary(p => p.Name, StringComparer.Ordinal);

        foreach (var (property, expression) in record.Expressions ?? [])
        {
            // Kept whether or not the property is still there: a part that has lost a setting since
            // the file was written is worth reporting when the parameters are next applied, rather
            // than quietly dropping the binding here and leaving nothing to report.
            component.Expressions[property] = expression;
        }

        foreach (var (name, element) in record.Parameters)
        {
            if (!properties.TryGetValue(name, out var property)) continue;

            try
            {
                var value = DeserializeValue(property, element, component, warnings, record.Name);
                if (value is not null || Nullable.GetUnderlyingType(property.PropertyType) is not null)
                    property.SetValue(component, value);
            }
            catch (Exception ex)
            {
                warnings.Add($"'{record.Name}': could not restore {name} ({ex.Message}).");
            }
        }
    }

    private static object? DeserializeValue(
        PropertyInfo property, JsonElement element, CircuitComponent component,
        ICollection<string> warnings, string componentName)
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

            warnings.Add(
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
        TerminalReference reference, IReadOnlyDictionary<Guid, CircuitComponent> byId,
        ICollection<string> warnings)
    {
        if (!byId.TryGetValue(reference.Component, out var component))
        {
            warnings.Add($"Dropped a connection to a component that is not in the file.");
            return null;
        }

        var terminal = component.Terminals.FirstOrDefault(t => t.Id == reference.Terminal);
        if (terminal is null)
        {
            warnings.Add(
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
