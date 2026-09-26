using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Components.Eda;

/// <summary>What was written, and what could not be.</summary>
/// <param name="Netlist">The file's text.</param>
/// <param name="Written">The parts that are in it.</param>
/// <param name="WithoutFootprints">
/// Parts with no footprint set. Not an error — the board tool will ask for one — but worth saying,
/// because a netlist of forty parts and no footprints is forty things to assign by hand.
/// </param>
public sealed record KiCadResult(
    string Netlist, IReadOnlyList<string> Written, IReadOnlyList<string> WithoutFootprints)
{
    public bool IsEmpty => Written.Count == 0;
}

/// <summary>
/// Writes the circuit out as a KiCad netlist, which is the step between a simulation that works and
/// a board.
/// <para>
/// A SPICE deck says what the circuit <i>does</i>; this says what it <i>is</i> — every part with its
/// designator, its value and its footprint, and every net with the pins on it. KiCad reads one of
/// these directly, so a circuit drawn and proven here can be laid out without anybody typing the
/// connections again, which is the step where connections get typed wrong.
/// </para>
/// <para>
/// It is the same netlist the solver uses, built by the same code. That matters more than it
/// sounds: a board wired from a second, separately derived list is a board wired from something
/// nobody simulated.
/// </para>
/// </summary>
public static partial class KiCadNetlist
{
    /// <summary>Terminals of a package name their pin number in their id, as <c>p7</c>.</summary>
    [GeneratedRegex(@"^p(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex NumberedPin();

    public static KiCadResult Write(Circuit circuit, string? source = null)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        var netlist = circuit.BuildNetlist();

        // Flattened, as everywhere else: a block is a way of drawing a section, not a thing to
        // solder, and its contents are ordinary parts on the board.
        var parts = Flattening.Flatten(circuit.Components)
            .Where(Buildable)
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        List<string> written = [];
        List<string> bare = [];

        var text = new StringBuilder();

        text.AppendLine("(export (version \"E\")");
        text.AppendLine("  (design");
        text.AppendLine($"    (source {Quote(source ?? circuit.Title)})");
        text.AppendLine($"    (date {Quote(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))})");
        text.AppendLine($"    (tool {Quote("CirqAvalonia")}))");

        text.AppendLine("  (components");

        foreach (var part in parts)
        {
            written.Add(part.Name);

            text.AppendLine($"    (comp (ref {Quote(part.Name)})");
            text.AppendLine($"      (value {Quote(Value(part))})");

            if (part.Footprint.Trim().Length > 0)
                text.AppendLine($"      (footprint {Quote(part.Footprint.Trim())})");
            else
                bare.Add(part.Name);

            // The library a board tool would look the symbol up in. Ours, honestly named: this
            // circuit was not drawn from KiCad's libraries, and saying it was would send somebody
            // looking for a symbol that does not match.
            text.AppendLine($"      (libsource (lib \"cirq\") (part {Quote(part.ComponentType)}))");
            text.AppendLine($"      (tstamps \"/{part.Id:D}\"))");
        }

        text.AppendLine("  )");
        text.AppendLine("  (nets");

        var code = 0;

        foreach (var net in netlist.Nets.OrderBy(n => n.IsGround ? -1 : n.Index))
        {
            var nodes = net.Terminals
                .Where(t => t.Owner is { } owner && Buildable(owner))
                .ToList();

            // A net with one pin on it is not a connection. KiCad reports those as unconnected
            // anyway, and writing them adds noise to a file somebody only reads when an import has
            // gone wrong.
            if (nodes.Count < 2) continue;

            text.AppendLine($"    (net (code \"{++code}\") (name {Quote(Name(net, code))})");

            foreach (var terminal in nodes)
            {
                var owner = terminal.Owner!;
                var pin = PinNumber(owner, terminal);

                text.AppendLine(
                    $"      (node (ref {Quote(owner.Name)}) (pin {Quote(pin)}) " +
                    $"(pinfunction {Quote(terminal.Name.Length > 0 ? terminal.Name : pin)}))");
            }

            text.AppendLine("    )");
        }

        text.AppendLine("  )");
        text.AppendLine(")");

        return new KiCadResult(text.ToString(), written, bare);
    }

    /// <summary>
    /// Whether a part is a thing to build. Annotations are drawing, net labels are notation, a
    /// ground is a symbol for a net rather than a component, and a loop probe is test equipment.
    /// </summary>
    private static bool Buildable(CircuitComponent part) =>
        part is not (IAnnotation or INetNaming or ISubcircuit) and not Ground and not LoopProbe;

    private static string Value(CircuitComponent part)
    {
        var value = part.ValueLabel;

        return value.Length > 0 ? value : part.ComponentType;
    }

    /// <summary>
    /// The net's name. A label somebody wrote beats a generated one, and ground is called GND
    /// because that is what every board library calls it.
    /// </summary>
    private static string Name(Net net, int code) =>
        net.IsGround ? "GND"
        : net.Label is { Length: > 0 } label ? label
        : $"Net-{code}";

    /// <summary>
    /// A pin's number on the package.
    /// <para>
    /// A part built from a DIP package numbers its terminals in their ids — <c>p7</c> is pin 7 —
    /// and those are the numbers on the datasheet, so they are the ones to write. Anything else is
    /// numbered by the order its terminals were declared, which for a two-terminal part is the 1
    /// and 2 that every board library uses for a resistor.
    /// </para>
    /// </summary>
    private static string PinNumber(CircuitComponent part, Terminal terminal)
    {
        var match = NumberedPin().Match(terminal.Id);

        if (match.Success) return match.Groups[1].Value;

        for (var i = 0; i < part.Terminals.Count; i++)
            if (ReferenceEquals(part.Terminals[i], terminal))
                return (i + 1).ToString(CultureInfo.InvariantCulture);

        return "1";
    }

    /// <summary>
    /// A quoted field. KiCad's reader is an s-expression parser, so a stray quote or backslash in a
    /// part's value would end the string early and take the rest of the file with it.
    /// </summary>
    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
                    .Replace("\"", "\\\"", StringComparison.Ordinal)
             + "\"";
}
