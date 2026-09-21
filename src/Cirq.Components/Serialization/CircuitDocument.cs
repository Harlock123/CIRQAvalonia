using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cirq.Components.Serialization;

/// <summary>A saved circuit file. Plain JSON, so a schematic stays readable and diffable.</summary>
public sealed class CircuitDocument
{
    /// <summary>Schema version, bumped when the format changes incompatibly.</summary>
    public int Version { get; set; } = CircuitSerializer.CurrentVersion;

    public string Application { get; set; } = "CirqAvalonia";

    public string Title { get; set; } = "Untitled circuit";

    public DateTimeOffset SavedUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<ComponentRecord> Components { get; set; } = [];

    public List<WireRecord> Wires { get; set; } = [];

    public List<ProbeRecord> Probes { get; set; } = [];

    /// <summary>
    /// The circuit's ambient temperature in degrees Celsius. Null in every file written before
    /// temperature was a circuit property, which is why it is optional rather than defaulted to a
    /// sentinel — an old file means "room temperature", not "zero".
    /// </summary>
    public double? AmbientTemperatureCelsius { get; set; }
}

/// <summary>One placed component: what it is, where it sits, and its parameters.</summary>
public sealed class ComponentRecord
{
    public Guid Id { get; set; }

    /// <summary>Type key, matching the component class name.</summary>
    public string Type { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public double X { get; set; }

    public double Y { get; set; }

    public double Rotation { get; set; }

    /// <summary>
    /// Values a component needs at construction time because they shape its pins — a gate's input
    /// count, for instance — and so cannot simply be assigned afterwards.
    /// </summary>
    public Dictionary<string, JsonElement>? Construction { get; set; }

    public Dictionary<string, JsonElement> Parameters { get; set; } = [];

    /// <summary>
    /// Set only on a block, and only describing its <i>structure</i>. What is inside a block is
    /// written out at the top level along with everything else — a block's contents are ordinary
    /// components and ordinary wires — and this says which of them belong to it and where its pins
    /// go. Saving it that way means the nesting costs one small record rather than a second copy
    /// of the whole file format.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BlockRecord? Block { get; set; }
}

/// <summary>The structure of a subcircuit: what is inside it, and what its pins stand for.</summary>
public sealed class BlockRecord
{
    /// <summary>Ids of the components inside, which appear at the top level of the document.</summary>
    public List<Guid> Components { get; set; } = [];

    /// <summary>Ids of the wires inside.</summary>
    public List<Guid> Wires { get; set; } = [];

    /// <summary>
    /// The pins, in the order they were made. Order matters: a pin's id is its position, and the
    /// wires outside the block were saved against those ids.
    /// </summary>
    public List<PortRecord> Ports { get; set; } = [];
}

/// <summary>One pin of a block and the terminal inside that it stands for.</summary>
public sealed class PortRecord
{
    public string Name { get; set; } = string.Empty;

    public TerminalReference Inner { get; set; } = new();
}

/// <summary>
/// A terminal, identified by its owning component and its pin id. Pin ids are unique within a
/// component, which is what lets a wire survive a round trip without any object identity.
/// </summary>
public sealed class TerminalReference
{
    public Guid Component { get; set; }

    public string Terminal { get; set; } = string.Empty;
}

public sealed class WireRecord
{
    public Guid Id { get; set; }

    public TerminalReference From { get; set; } = new();

    public TerminalReference To { get; set; } = new();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<PointRecord>? Waypoints { get; set; }
}

public sealed record PointRecord(double X, double Y);

public sealed class ProbeRecord
{
    public string Label { get; set; } = "Probe";

    public TerminalReference Target { get; set; } = new();

    /// <summary>Trace colour as #AARRGGBB.</summary>
    public string Color { get; set; } = "#FFFFD733";

    public string Kind { get; set; } = "Voltage";

    /// <summary>
    /// The second point of a differential or power probe. Null for the kinds measured against
    /// ground, and null in every file written before those kinds existed — which is why it is
    /// optional rather than a required field with a sentinel.
    /// </summary>
    public TerminalReference? Reference { get; set; }

    public bool IsVisible { get; set; } = true;

    public bool AcCoupled { get; set; }
}
