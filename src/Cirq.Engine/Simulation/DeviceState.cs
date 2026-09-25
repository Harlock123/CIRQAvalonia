using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Engine.Simulation;

/// <summary>
/// What one part is doing at the solved point, as opposed to what it is set to.
/// <para>
/// Every field is optional because every one of them is genuinely unavailable somewhere, and a
/// zero in place of an unknown is the failure this type exists to prevent: it reads as a
/// measurement, and "no current through this part" and "no way to know the current through this
/// part" are different sentences that a reader has to be able to tell apart.
/// </para>
/// </summary>
/// <param name="Volts">
/// The potential difference across a two-terminal part, from its first pin to its second. Null for
/// anything with more pins, where there is no such thing as the voltage across it.
/// </param>
/// <param name="Amps">Current through it, positive into its first pin — the clamp meter's convention.</param>
/// <param name="Watts">
/// What it turns into heat. Null unless the part says it dissipates: see
/// <see cref="IPowerRated"/> for why volts times amps is not an answer to this in general.
/// </param>
/// <param name="Delivered">
/// Power it puts into the rest of the circuit, in watts. Positive for anything acting as a source
/// at this instant, which is how a supply is told from a load without asking either what it is.
/// </param>
/// <param name="Celsius">Where its die is, when that is modelled.</param>
public readonly record struct DeviceState(
    double? Volts = null,
    double? Amps = null,
    double? Watts = null,
    double? Delivered = null,
    double? Celsius = null)
{
    /// <summary>True when there is at least one number here.</summary>
    public bool HasAnything =>
        Volts is not null || Amps is not null || Watts is not null || Celsius is not null;
}

/// <summary>
/// Reads the solved matrix back as a statement about one part.
/// <para>
/// One definition, because there were nearly two. The figures written on the schematic and the
/// figures in the power budget are the same figures, and two pieces of code arriving at them
/// separately is two pieces of code that disagree about a resistor eventually.
/// </para>
/// <para>
/// What is deliberately left unanswered is anything ambiguous. Current and voltage are reported
/// for two-terminal parts and for nothing else: a package with ten pins has no single current
/// through it, and a number picked from among them would be a plausible-looking answer to a
/// question nobody asked.
/// </para>
/// </summary>
public static class DeviceStates
{
    public static DeviceState Of(CircuitComponent part, CircuitSimulator simulator)
    {
        ArgumentNullException.ThrowIfNull(part);
        ArgumentNullException.ThrowIfNull(simulator);

        double? volts = null;
        double? amps = null;

        // Both pins have to be in this netlist. A part dropped on the sheet and not yet wired has
        // terminals the netlist has never seen, and asking for their nodes throws — which is the
        // state a circuit is in for as long as it takes to draw the second wire.
        var pair = part.Terminals.Count == 2
            && simulator.Netlist.Contains(part.Terminals[0])
            && simulator.Netlist.Contains(part.Terminals[1]);

        if (pair)
        {
            volts = Sane(
                simulator.System.NodeVoltage(part.Terminals[0]) -
                simulator.System.NodeVoltage(part.Terminals[1]));

            // Either the part speaks for its own first pin, or it keeps exactly one branch and the
            // solver already has the answer. The restriction to two pins is what makes the second
            // trustworthy: a branch current is signed by the order the part stamped its nodes in,
            // and for a part with two of them that order is its own two terminals.
            if (part is ICurrentReporting || (part.VoltageSourceCount == 1 && part.InternalNodeCount == 0))
                amps = Sane(simulator.TerminalCurrent(part.Terminals[0]));
        }

        // Positive when it is pushing current out of its first pin against the potential there,
        // which is what a source does and a load does not.
        var delivered = volts is { } v && amps is { } i ? Sane(-v * i) : null;

        return new DeviceState(volts, amps, Heat(part, volts, amps), delivered, Die(part));
    }

    /// <summary>
    /// What the part turns into heat, or null when it has not said that it turns anything into
    /// heat. See <see cref="IPowerRated"/>: a capacitor and a resistor with the same volts and amps
    /// across them dissipate a watt and nothing respectively, and the topology cannot tell them
    /// apart.
    /// </summary>
    private static double? Heat(CircuitComponent part, double? volts, double? amps)
    {
        // A part with its own thermal model knows better than multiplying two of our numbers
        // would: its figure is integrated across the switching it does between our samples, and it
        // is the figure that produced the parameters this solve just used.
        if (part is ISelfHeating thermal) return Sane(thermal.PowerDissipation);

        if (part is not IPowerRated) return null;

        return volts is { } v && amps is { } i ? Sane(Math.Abs(v * i)) : null;
    }

    private static double? Die(CircuitComponent part) =>
        part is ISelfHeating { IsSelfHeating: true } thermal ? Sane(thermal.JunctionTemperature) : null;

    /// <summary>A number, or null when the solver produced something that is not one.</summary>
    private static double? Sane(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? null : value;
}
