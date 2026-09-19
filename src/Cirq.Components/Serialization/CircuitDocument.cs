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

    public bool IsVisible { get; set; } = true;

    public bool AcCoupled { get; set; }
}
