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

    /// <summary>
    /// The SPICE cards for any imported models the circuit uses, carried in the file so it opens
    /// complete somewhere else.
    /// <para>
    /// A part built in to this library is named and looked up. An imported one has nowhere to be
    /// looked up <i>from</i> on a machine that never imported it, so the card comes along. Null
    /// when the circuit uses nothing but built-in models, which is nearly every circuit — an
    /// ordinary file is byte-for-byte what it always was.
    /// </para>
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ModelRecord>? Models { get; set; }

    /// <summary>
    /// What the circuit is supposed to do. Null in every file written before requirements existed,
    /// and in every circuit that has none — so an ordinary file is byte-for-byte what it was.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SpecRecord>? Specs { get; set; }

    /// <summary>
    /// What the circuit produced when somebody last said "this is right". Null until a baseline is
    /// taken, so an ordinary file is byte-for-byte what it was.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BaselineRecord? Baseline { get; set; }
}

/// <summary>A recorded set of traces, as the file holds them.</summary>
public sealed class BaselineRecord
{
    public DateTimeOffset Taken { get; set; }

    public string Note { get; set; } = string.Empty;

    public List<BaselineTraceRecord> Traces { get; set; } = [];
}

/// <summary>
/// One trace of a baseline: its name, its unit, and a thinned copy of its shape.
/// <para>
/// The times and the values are two flat arrays rather than a list of pairs, and that is worth a
/// line. A pair per point writes <c>{"time":...,"value":...}</c> two hundred and fifty-six times
/// per trace, which is about three times the size for exactly the same numbers — and the file this
/// lives in is one somebody may open in an editor.
/// </para>
/// <para>
/// The measurements are <b>not</b> stored. They are worked out again from the samples when the file
/// is read, which costs nothing and means a release that fixes a measurement fixes it for baselines
/// taken before the fix rather than holding new runs against an old bug.
/// </para>
/// </summary>
public sealed class BaselineTraceRecord
{
    public string Label { get; set; } = string.Empty;

    public string Unit { get; set; } = "V";

    public List<double> Times { get; set; } = [];

    public List<double> Values { get; set; } = [];
}

/// <summary>One written-down requirement, exactly as the panel holds it.</summary>
public sealed class SpecRecord
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>The probe label this requirement measures.</summary>
    public string Trace { get; set; } = string.Empty;

    /// <summary>
    /// The second probe, for the measurements that need one. Null in every file written before
    /// those existed, and in every requirement that does not use one.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Against { get; set; }

    /// <summary>Stored by name so a new quantity can be added without moving the others.</summary>
    public string Quantity { get; set; } = nameof(Cirq.Core.Verification.SpecQuantity.PeakToPeak);

    public string Comparison { get; set; } = nameof(Cirq.Core.Verification.SpecComparison.AtMost);

    public double Limit { get; set; }

    public double Tolerance { get; set; }

    public string Unit { get; set; } = "V";

    public bool IsEnabled { get; set; } = true;
}

/// <summary>One imported device model, as the card that defines it.</summary>
public sealed class ModelRecord
{
    /// <summary>The model's name, as components refer to it.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>The <c>.model</c> card, which is the whole of the definition.</summary>
    public string Card { get; set; } = string.Empty;
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
