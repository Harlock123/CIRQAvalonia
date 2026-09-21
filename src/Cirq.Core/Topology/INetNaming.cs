namespace Cirq.Core.Topology;

/// <summary>
/// A part that gives its net a name, and joins it to every other net carrying the same one.
/// <para>
/// This is how a schematic stops being a drawing of wires. Past a certain size a circuit has
/// signals that go everywhere — a supply rail, a reset line, a clock — and drawing each of them as
/// a wire to every place it is needed produces a diagram nobody can follow. Naming the net instead
/// says the same thing and leaves the page readable.
/// </para>
/// <para>
/// It is in <c>Core</c> rather than in the parts library because the netlist builder has to know
/// about it: joining two nets by name is a topology operation, and the builder is where topology
/// is decided.
/// </para>
/// </summary>
public interface INetNaming
{
    /// <summary>The terminal that carries the name onto whatever it is wired to.</summary>
    Terminal NamedTerminal { get; }

    /// <summary>
    /// The name. Compared case-insensitively and with the ends trimmed, because <c>VCC</c>,
    /// <c>Vcc</c> and <c>vcc </c> are the same rail to everybody except a string comparison.
    /// </summary>
    string NetName { get; }
}
