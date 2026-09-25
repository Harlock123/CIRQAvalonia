using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using Cirq.Engine.Simulation;
using CorePoint = Cirq.Core.Primitives.Point;

namespace Cirq.UI.Services;

/// <summary>What one net is sitting at, and where to write it.</summary>
/// <param name="Name">The net's name — what a label on it calls it, or the generated one.</param>
/// <param name="Volts">Its potential with respect to ground.</param>
/// <param name="At">
/// A point on the net to write the figure beside: the topmost, then leftmost terminal on it. Chosen
/// rather than averaged because the average of a net that spans the sheet is the middle of the
/// sheet, which is usually somewhere the net does not go.
/// </param>
public sealed record NetReading(string Name, double Volts, CorePoint At)
{
    /// <summary>The figure as it should read on the drawing, e.g. <c>4.7V</c>.</summary>
    public string Text => LiveValues.Figure(Volts, "V", LiveValues.VoltFloor);
}

/// <summary>
/// What one part is doing, as distinct from what it is set to.
/// <para>
/// Every field is optional because every one of them is genuinely unavailable somewhere. A
/// three-terminal part has no single current through it; a part with no thermal model has no die
/// temperature; a part the solver stamps as a conductance and that declines to report its own pin
/// currents has none to report.
/// </para>
/// </summary>
/// <param name="Part">The component measured.</param>
/// <param name="Volts">The potential difference across it, for a two-terminal part.</param>
/// <param name="Amps">The current through it, positive into its first pin.</param>
/// <param name="Watts">What it is dissipating.</param>
/// <param name="Celsius">Where its die is, when that is modelled.</param>
public sealed record PartReading(
    CircuitComponent Part,
    double? Volts = null,
    double? Amps = null,
    double? Watts = null,
    double? Celsius = null)
{
    /// <summary>True when there is at least one number here worth showing.</summary>
    public bool HasAnything => Volts is not null || Amps is not null
        || Watts is not null || Celsius is not null;

    /// <summary>
    /// The one line that goes on the drawing beside the symbol, or empty when there is nothing to
    /// put there.
    /// <para>
    /// Current before voltage, because the voltage across a part is very nearly readable off the
    /// two net figures at its ends and the current is not readable off anything. Watts only when
    /// the part is dissipating enough to matter — a milliwatt on every resistor is the kind of
    /// annotation that gets switched off and left off.
    /// </para>
    /// </summary>
    public string Text
    {
        get
        {
            List<string> parts = [];

            if (Amps is { } amps) parts.Add(LiveValues.Figure(amps, "A", LiveValues.AmpFloor));
            else if (Volts is { } volts) parts.Add(LiveValues.Figure(volts, "V", LiveValues.VoltFloor));

            if (Watts is { } watts && watts >= WorthSaying) parts.Add(SiPrefix.Format(watts, "W", 2));

            return string.Join("  ", parts);
        }
    }

    /// <summary>
    /// Below this, a dissipation figure is noise. A tenth of a watt is where a part starts having
    /// to be chosen for it rather than simply bought.
    /// </summary>
    public const double WorthSaying = 0.1;
}

/// <summary>Every reading taken from one solved point.</summary>
public sealed record LiveSnapshot(
    IReadOnlyList<NetReading> Nets,
    IReadOnlyDictionary<CircuitComponent, PartReading> Parts)
{
    public static LiveSnapshot Empty { get; } =
        new([], new Dictionary<CircuitComponent, PartReading>());

    /// <summary>What one part is doing, or null when nothing could be said about it.</summary>
    public PartReading? For(CircuitComponent part) =>
        part is not null && Parts.TryGetValue(part, out var reading) ? reading : null;

    public bool IsEmpty => Nets.Count == 0 && Parts.Count == 0;
}

/// <summary>
/// What every net is sitting at and what every part is doing, read off a solved circuit.
/// <para>
/// The engine knows all of this the instant it has converged, and until now none of it reached the
/// drawing: finding out what a node was at cost placing a probe, and the hover card would tell you
/// a resistor was 4k7 while declining to say it was passing 40 mA and getting hot. Both are
/// questions about the same part, and only one of them needed the circuit to be running.
/// </para>
/// <para>
/// What is deliberately left out is anything ambiguous. Current is reported for two-terminal parts
/// and for parts that keep a branch of their own, and for nothing else — on a package with ten
/// pins there is no such thing as "the" current through it, and a number picked from among them
/// would be a plausible-looking answer to a question nobody asked. The same rule
/// <see cref="WireCurrentsService"/> follows, for the same reason.
/// </para>
/// </summary>
public static class LiveValues
{
    /// <summary>
    /// Reads the current solution. Cheap — a pass over the nets and a pass over the parts, with no
    /// solving — but not free, so callers turn it on only while something is showing it.
    /// </summary>
    public static LiveSnapshot For(Circuit circuit, CircuitSimulator simulator)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(simulator);

        return new LiveSnapshot(NetsOf(simulator), PartsOf(circuit, simulator));
    }

    /// <summary>
    /// Every net but ground, with a point on it to write the figure beside.
    /// <para>
    /// Ground is left out on purpose. It is zero by definition, everywhere, and a schematic with
    /// <c>0V</c> written against every return is a schematic with less room for the figures that
    /// carry information.
    /// </para>
    /// </summary>
    private static List<NetReading> NetsOf(CircuitSimulator simulator)
    {
        List<NetReading> readings = [];

        foreach (var net in simulator.Netlist.Nets)
        {
            if (net.IsGround) continue;
            if (net.Terminals.Count == 0) continue;

            var volts = simulator.System.NodeVoltage(net.Index);
            if (double.IsNaN(volts) || double.IsInfinity(volts)) continue;

            readings.Add(new NetReading(net.Name, volts, Anchor(net)));
        }

        return readings;
    }

    /// <summary>
    /// Where a net's figure goes: its topmost terminal, and the leftmost of those when several are
    /// level. Stable for a given drawing, which matters more than being optimal — a label that
    /// jumps from one end of a net to the other between frames is unreadable.
    /// </summary>
    private static CorePoint Anchor(Net net)
    {
        var best = net.Terminals[0].AbsolutePosition;

        foreach (var terminal in net.Terminals)
        {
            var at = terminal.AbsolutePosition;

            if (at.Y < best.Y || (at.Y == best.Y && at.X < best.X)) best = at;
        }

        return best;
    }

    private static Dictionary<CircuitComponent, PartReading> PartsOf(
        Circuit circuit, CircuitSimulator simulator)
    {
        Dictionary<CircuitComponent, PartReading> readings = [];

        foreach (var part in Flattened(circuit))
        {
            var reading = Read(part, simulator);

            if (reading.HasAnything) readings[part] = reading;
        }

        return readings;
    }

    /// <summary>
    /// The parts the solver actually stamped, which for a circuit containing a block is the block's
    /// contents rather than the block. Hovering the inside of a group has to give the same answer
    /// as hovering the same part drawn loose.
    /// </summary>
    private static IEnumerable<CircuitComponent> Flattened(Circuit circuit) =>
        Flattening.Flatten(circuit.Components);

    private static PartReading Read(CircuitComponent part, CircuitSimulator simulator)
    {
        double? volts = null;
        double? amps = null;

        // Two pins, both of them in this netlist. A part dropped on the sheet and not yet wired to
        // anything has terminals the netlist has never seen, and asking for their nodes throws.
        var pair = part.Terminals.Count == 2
            && simulator.Netlist.Contains(part.Terminals[0])
            && simulator.Netlist.Contains(part.Terminals[1]);

        if (pair)
        {
            volts = Sane(
                simulator.System.NodeVoltage(part.Terminals[0]) -
                simulator.System.NodeVoltage(part.Terminals[1]));

            // Either the part speaks for its own first pin, or it keeps exactly one branch and the
            // solver already has the answer. Both are read through the same call, which prefers the
            // first. The restriction to two pins is what makes the second trustworthy: a branch
            // current is signed by the order the part stamped its nodes in, and for a part with two
            // of them that order is its own two terminals — so "into the first pin" is a statement
            // about the part rather than a guess about its stamping.
            if (part is ICurrentReporting || (part.VoltageSourceCount == 1 && part.InternalNodeCount == 0))
                amps = Sane(simulator.TerminalCurrent(part.Terminals[0]));
        }

        // A part with more pins than two gets no current and no voltage: there is no single answer
        // to either. It may still be dissipating, and that is read below — a transistor's watts are
        // the number worth having about it anyway.

        // A part that models its own heating knows both numbers better than multiplying two of
        // ours would: its dissipation is integrated over the switching it does between our
        // samples, and its die temperature is the state variable that produced the parameters
        // this solve just used.
        double? watts = null;
        double? celsius = null;

        if (part is ISelfHeating thermal)
        {
            watts = Sane(thermal.PowerDissipation);
            if (thermal.IsSelfHeating) celsius = Sane(thermal.JunctionTemperature);
        }
        else if (volts is { } v && amps is { } i)
        {
            watts = Sane(Math.Abs(v * i));
        }

        return new PartReading(part, volts, amps, watts, celsius);
    }

    /// <summary>A number, or null when the solver produced something that is not one.</summary>
    private static double? Sane(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? null : value;

    /// <summary>
    /// A figure as it goes on the drawing, with everything below the floor written as a plain zero.
    /// <para>
    /// Without the floor the SI formatter dresses up the solver's own residue: a node tied to
    /// ground through a closed switch comes out as <c>450pV</c> and one hanging off a capacitor as
    /// <c>1.8nV</c>, both of which read as measurements of something. They are measurements of
    /// nothing, at a resolution no instrument has, and a schematic with half a dozen of them on it
    /// invites somebody to go and find out why that node is at 450 picovolts.
    /// </para>
    /// <para>
    /// The floors are set where the bench stops rather than where the arithmetic does. A microvolt
    /// is below the noise of anything you would measure a node with; a picoamp is below the leakage
    /// of the socket you would measure it through. What survives them is real — the four and a half
    /// nanoamps through an open switch is a genuine statement that the switch is open.
    /// </para>
    /// </summary>
    public static string Figure(double value, string unit, double floor) =>
        Math.Abs(value) < floor ? $"0{unit}" : SiPrefix.Format(value, unit, 3);

    /// <summary>Below a microvolt, a node is at zero as far as any instrument is concerned.</summary>
    public const double VoltFloor = 1e-6;

    /// <summary>And below a picoamp, so is a current.</summary>
    public const double AmpFloor = 1e-12;
}
