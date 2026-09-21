using Cirq.Core.Topology;

namespace Cirq.Components.Diagnostics;

/// <summary>How much a finding matters.</summary>
public enum RuleSeverity
{
    /// <summary>The circuit will not solve, or will solve into nonsense.</summary>
    Error,

    /// <summary>It will solve, but it is very probably not what was meant.</summary>
    Warning,
}

/// <summary>One thing the check found.</summary>
/// <param name="Severity">Whether it stops the circuit or merely doubts it.</param>
/// <param name="Rule">A short name for the kind of problem, for grouping.</param>
/// <param name="Message">What is wrong, in a sentence.</param>
/// <param name="Components">Which parts it concerns, so the canvas can point at them.</param>
public sealed record RuleFinding(
    RuleSeverity Severity,
    string Rule,
    string Message,
    IReadOnlyList<CircuitComponent> Components)
{
    /// <summary>The designators involved, e.g. "U1, U2".</summary>
    public string Where => string.Join(", ", Components.Select(c => c.Name));

    public override string ToString() => $"{Severity}: {Message}";
}

/// <summary>
/// Checks a circuit's wiring for the mistakes that are silent.
/// <para>
/// Components in this library already report their own violations — a relay coil without a
/// flyback diode, a transceiver outside its common-mode range, a fuse past its melting integral.
/// Those are all questions a part can answer about itself. What none of them can see is the
/// <i>topology</i>: that the ground pin has been wired to a rail, that two outputs are fighting
/// over one net, that an input is floating, that a supply is shorted. Every one of those is a
/// mistake that produces a number rather than an error, and a number is much harder to disbelieve.
/// </para>
/// <para>
/// The rules here were all chosen because they have actually caught something. A supply pin tied
/// to ground is the ULN2003's Vcc, which is really its ground pin under another name. Two driven
/// outputs on one net is a pair of bus transceivers whose enables were wired together. A label
/// used once is a typo in a net name, which connects to nothing at all and looks exactly like a
/// wire that is simply somewhere else on the page.
/// </para>
/// </summary>
public static class ElectricalRuleCheck
{
    /// <summary>Runs every rule, most serious first.</summary>
    public static IReadOnlyList<RuleFinding> Run(Circuit circuit)
    {
        List<RuleFinding> findings = [];

        Netlist netlist;

        try
        {
            netlist = circuit.BuildNetlist();
        }
        catch (CircuitTopologyException ex)
        {
            return [new RuleFinding(RuleSeverity.Error, "topology", ex.Message, [])];
        }

        if (circuit.Components.Count == 0) return findings;

        CheckGroundExists(circuit, netlist, findings);
        CheckSuppliesNotShorted(circuit, netlist, findings);
        CheckPowerPinsAreNotGround(netlist, findings);
        CheckPowerPinsAreConnected(netlist, findings);
        CheckFloatingTerminals(circuit, netlist, findings);
        CheckDrivenOutputsDoNotShare(netlist, findings);
        CheckLabels(circuit, netlist, findings);

        return [.. findings.OrderBy(f => f.Severity).ThenBy(f => f.Rule)];
    }

    /// <summary>
    /// Nothing is measured against anything until there is a datum. The solver says so too, but it
    /// says it as a singular matrix, and "place a Ground symbol" is a more useful sentence.
    /// </summary>
    private static void CheckGroundExists(
        Circuit circuit, Netlist netlist, List<RuleFinding> findings)
    {
        if (netlist.GroundNet is not null) return;

        findings.Add(new RuleFinding(
            RuleSeverity.Error, "no-ground",
            "The circuit has no ground reference. Every voltage is measured against one, so " +
            "until a Ground symbol is placed and connected there is nothing to solve.",
            []));
    }

    /// <summary>
    /// A source with both terminals on one net is a short across it. The solver reports this as a
    /// singular matrix, which is true and unhelpful.
    /// </summary>
    private static void CheckSuppliesNotShorted(
        Circuit circuit, Netlist netlist, List<RuleFinding> findings)
    {
        foreach (var component in circuit.Components)
        {
            if (component.VoltageSourceCount == 0) continue;
            if (component.Terminals.Count != 2) continue;

            var a = component.Terminals[0];
            var b = component.Terminals[1];

            if (!netlist.Contains(a) || !netlist.Contains(b)) continue;
            if (netlist.IndexOf(a) != netlist.IndexOf(b)) continue;

            findings.Add(new RuleFinding(
                RuleSeverity.Error, "shorted-source",
                $"{component.Name} has both of its terminals on the same net, which is a short " +
                "across it. Nothing limits the current, so there is no solution.",
                [component]));
        }
    }

    /// <summary>
    /// A supply pin and a ground pin on one net. This is the ULN2003 mistake exactly: the part has
    /// no supply pin at all, so the terminal marked Vcc is its ground pin under another name, and
    /// wiring it to a rail shorts the rail to ground.
    /// </summary>
    private static void CheckPowerPinsAreNotGround(Netlist netlist, List<RuleFinding> findings)
    {
        foreach (var net in netlist.Nets)
        {
            var power = net.Terminals.Where(t => t.Type == TerminalType.Power).ToList();
            if (power.Count == 0) continue;

            var grounded = net.IsGround || net.Terminals.Any(t => t.Type == TerminalType.Ground);
            if (!grounded) continue;

            var owners = power.Select(t => t.Owner).Where(o => o is not null).Distinct().ToList();

            findings.Add(new RuleFinding(
                RuleSeverity.Error, "supply-on-ground",
                $"The supply pin{(power.Count > 1 ? "s" : string.Empty)} " +
                $"{string.Join(", ", power.Select(Describe))} " +
                $"{(power.Count > 1 ? "are" : "is")} on the ground net. That shorts the rail to " +
                "ground — check whether the part's Vcc terminal is really a supply pin.",
                [.. owners!]));
        }
    }

    /// <summary>
    /// A package whose supply or ground pin is not wired to anything. It will not work, and
    /// depending on the part it will either release its outputs or quietly solve as though the
    /// pin were at zero.
    /// </summary>
    private static void CheckPowerPinsAreConnected(Netlist netlist, List<RuleFinding> findings)
    {
        foreach (var net in netlist.Nets)
        {
            if (net.Terminals.Count != 1) continue;

            var terminal = net.Terminals[0];
            if (terminal.Type is not (TerminalType.Power or TerminalType.Ground)) continue;
            if (terminal.Owner is null) continue;

            findings.Add(new RuleFinding(
                RuleSeverity.Error, "unpowered",
                $"{Describe(terminal)} is not connected to anything. A package with an unwired " +
                "supply or ground pin does not run.",
                [terminal.Owner]));
        }
    }

    /// <summary>
    /// A terminal alone on its net. Alone means unwired: nothing else is at that point, so it is
    /// either floating or it was meant to go somewhere and does not.
    /// </summary>
    private static void CheckFloatingTerminals(
        Circuit circuit, Netlist netlist, List<RuleFinding> findings)
    {
        foreach (var net in netlist.Nets)
        {
            if (net.Terminals.Count != 1) continue;

            var terminal = net.Terminals[0];
            if (terminal.Owner is null) continue;

            // Already reported, and more precisely, by the supply rule above.
            if (terminal.Type is TerminalType.Power or TerminalType.Ground) continue;

            // An input left floating is the one that bites: on CMOS it picks up whatever is
            // nearby and the part behaves differently from one run to the next.
            var severity = terminal.Type == TerminalType.Input
                ? RuleSeverity.Error
                : RuleSeverity.Warning;

            var why = terminal.Type == TerminalType.Input
                ? "An unconnected input has no defined level. Tie it high or low."
                : "Nothing else is on that net, so it is floating.";

            findings.Add(new RuleFinding(
                severity, "floating", $"{Describe(terminal)} is not connected to anything. {why}",
                [terminal.Owner]));
        }
    }

    /// <summary>
    /// Two driven outputs on one net. One of them wins and the other sinks the difference, which
    /// on real silicon is how a bus transceiver dies — and in a model is simply a number that is
    /// neither of the two levels anybody expected.
    /// <para>
    /// A warning rather than an error, because it is sometimes deliberate: open-collector and
    /// tri-state parts share a net on purpose, and whether they are releasing it at the moment is
    /// a question about the running circuit rather than about its wiring. Parts that are built to
    /// share say so by carrying <c>Bidirectional</c> pins, and those are not counted here.
    /// </para>
    /// </summary>
    private static void CheckDrivenOutputsDoNotShare(Netlist netlist, List<RuleFinding> findings)
    {
        foreach (var net in netlist.Nets)
        {
            var outputs = net.Terminals.Where(t => t.Type == TerminalType.Output).ToList();
            if (outputs.Count < 2) continue;

            var owners = outputs.Select(t => t.Owner).Where(o => o is not null).Distinct().ToList();

            findings.Add(new RuleFinding(
                RuleSeverity.Warning, "output-clash",
                $"{string.Join(" and ", outputs.Select(Describe))} are driven outputs on the " +
                $"same net ({net.Name}). Unless they can release it, whenever they disagree one " +
                "of them wins and the other takes the current.",
                [.. owners!]));
        }
    }

    /// <summary>
    /// A net label that names a net nothing else names. It connects to nothing, and the usual
    /// cause is a typo — which is invisible on the page, because a label that goes nowhere looks
    /// exactly like one whose partner is somewhere else on the drawing.
    /// </summary>
    private static void CheckLabels(Circuit circuit, Netlist netlist, List<RuleFinding> findings)
    {
        var labels = circuit.Components.OfType<INetNaming>().ToList();
        if (labels.Count == 0) return;

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var label in labels)
        {
            var name = label.NetName?.Trim() ?? string.Empty;
            if (name.Length == 0) continue;

            counts[name] = counts.GetValueOrDefault(name) + 1;
        }

        foreach (var label in labels)
        {
            var component = label as CircuitComponent;
            if (component is null) continue;

            var name = label.NetName?.Trim() ?? string.Empty;

            if (name.Length == 0)
            {
                findings.Add(new RuleFinding(
                    RuleSeverity.Warning, "label-blank",
                    $"{component.Name} is a net label with no name, so it joins nothing to anything.",
                    [component]));

                continue;
            }

            if (counts[name] > 1) continue;

            findings.Add(new RuleFinding(
                RuleSeverity.Warning, "label-alone",
                $"{component.Name} names the net '{name}', and no other label uses that name. " +
                "A label on its own connects to nothing — check the spelling against its partner.",
                [component]));
        }

        // Two different names on one net is a contradiction: the netlist has to pick one, and
        // whichever it picks the drawing says something it does not mean.
        foreach (var net in netlist.Nets)
        {
            var names = net.Terminals
                .Select(t => t.Owner as INetNaming)
                .Where(l => l is not null && l!.NetName.Trim().Length > 0)
                .Select(l => l!.NetName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (names.Count < 2) continue;

            var owners = net.Terminals
                .Where(t => t.Owner is INetNaming)
                .Select(t => t.Owner!)
                .Distinct()
                .ToList();

            findings.Add(new RuleFinding(
                RuleSeverity.Warning, "label-conflict",
                $"One net carries more than one name ({string.Join(", ", names)}). They are all " +
                "the same net; only one of the names will be used for it.",
                owners));
        }
    }

    private static string Describe(Terminal terminal)
    {
        var owner = terminal.Owner?.Name ?? "?";

        return terminal.Name.Length > 0 ? $"{owner}.{terminal.Name}" : owner;
    }
}
