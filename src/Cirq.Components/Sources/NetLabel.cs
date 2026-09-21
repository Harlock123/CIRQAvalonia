using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Sources;

/// <summary>
/// A named net: wire one of these to a point, put another with the same name somewhere else, and
/// the two points are connected without a wire between them.
/// <para>
/// It is not a component in the electrical sense — it stamps nothing and carries no current — but
/// it changes the circuit, because the netlist joins every terminal whose label matches. Two
/// labels reading <c>VCC</c> on opposite corners of the page are one net, exactly as though a wire
/// ran between them.
/// </para>
/// <para>
/// The reason to want this is that a schematic is a document as much as a description. A supply
/// rail, a reset line and a clock go almost everywhere, and drawing each of them as a wire to
/// every place it is needed produces a page of crossings that hides the circuit it is meant to
/// show. Naming them says the same thing and leaves the drawing readable — which is why every
/// commercial schematic tool has this, and why every real schematic uses it.
/// </para>
/// <para>
/// The name is matched with its ends trimmed and without regard to case, since <c>VCC</c>,
/// <c>Vcc</c> and <c>vcc </c> are the same rail to everybody except a string comparison. A label
/// left blank connects to nothing, which is what an unnamed one should do.
/// </para>
/// </summary>
public sealed partial class NetLabel : CircuitComponent, INetNaming
{
    public NetLabel()
    {
        Pin = new Terminal("net", string.Empty, TerminalType.Passive, new Point(-40, 0));
        Terminals = [Pin];
    }

    public NetLabel(string name) : this()
    {
        NetName = name;
    }

    /// <summary>The single terminal, which goes to the point being named.</summary>
    public Terminal Pin { get; }

    public Terminal NamedTerminal => Pin;

    /// <summary>
    /// What this net is called. Every label carrying the same name is the same net.
    /// </summary>
    [ObservableProperty]
    public partial string NetName { get; set; } = "NET";

    public override string ComponentType => "Net Label";

    public override string DesignatorPrefix => "L";

    public override string ValueLabel => NetName;

    /// <summary>
    /// A label contributes nothing to the matrix. Its whole effect happened when the netlist was
    /// built, which is why changing the name is a change of topology rather than of a value.
    /// </summary>
    public override void StampMatrix(MnaSystem system, SimulationState state) { }

    partial void OnNetNameChanged(string value) => NotifyValueChanged();
}
