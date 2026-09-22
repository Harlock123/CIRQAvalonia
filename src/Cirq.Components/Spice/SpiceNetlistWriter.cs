using System.Globalization;
using System.Text;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;

namespace Cirq.Components.Spice;

/// <summary>What a netlist export produced.</summary>
/// <param name="Netlist">The deck.</param>
/// <param name="Written">Designators that became element lines.</param>
/// <param name="Skipped">
/// Parts with no SPICE equivalent, and why. They appear in the deck as comments rather than being
/// left out silently — a netlist that quietly omits half a circuit is worse than one that says
/// what it could not carry.
/// </param>
public sealed record NetlistResult(
    string Netlist, IReadOnlyList<string> Written, IReadOnlyList<string> Skipped)
{
    public bool IsEmpty => Written.Count == 0;
}

/// <summary>
/// Writes a circuit out as a SPICE deck.
/// <para>
/// The other half of <see cref="SpiceModelReader"/>, and the reason to have both is that they let
/// this be a front end for something else. There are analyses this engine does not do — noise,
/// distortion, pole-zero, a proper Monte Carlo over a transient — and a deck ngspice or LTspice
/// can read means a circuit drawn here is not trapped here.
/// </para>
/// <para>
/// <b>It writes what it can carry and says what it cannot.</b> Resistors, capacitors, inductors,
/// sources, diodes and transistors have direct equivalents. A 7400, an I²C master or an ultrasonic
/// ranger do not: they are modelled here by event-driven code, not by a netlist, and there is no
/// honest translation. Those come out as comments naming the part, so somebody reading the deck
/// can see exactly what is missing rather than wondering why their circuit does not work.
/// </para>
/// </summary>
public static class SpiceNetlistWriter
{
    /// <summary>Writes the deck.</summary>
    /// <param name="circuit">The circuit. Blocks are flattened, as they are for a solve.</param>
    /// <param name="title">The deck's title line, which SPICE always treats as a comment.</param>
    public static NetlistResult Write(Circuit circuit, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(circuit);

        var netlist = circuit.BuildNetlist();

        // Flattened: a block's contents are ordinary parts in the deck, exactly as they are in the
        // matrix. SPICE has subcircuits, but writing one per block would put the hierarchy in the
        // file without making the deck any more accurate, and .subckt has its own pin-ordering
        // rules to get wrong.
        var components = Flattening.Flatten(circuit.Components).ToList();

        var body = new StringBuilder();
        List<string> written = [];
        List<string> skipped = [];

        // Element names have to be unique, and a designator's prefix is not always one letter:
        // LED1 stripped of one character leaves ED1, and prefixed with D gives DED1. Stripping
        // the whole leading run of letters gives D1 instead, which is right until a circuit has
        // both an LED1 and a D1 — so the names are allocated rather than computed.
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Names that belong to a particular part because its designator already begins with the
        // right type letter. Reserved up front so a source called V1 keeps V1 even when an FG1
        // sorts earlier and would otherwise take it.
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in components)
        {
            if (TypeLetter(component) is not { } letter) continue;

            var clean = Sanitise(component.Name);

            if (clean.Length > 1 && char.ToUpperInvariant(clean[0]) == letter) reserved.Add(clean);
        }

        foreach (var component in components.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var line = Element(component, netlist, taken, reserved);

            if (line is null)
            {
                // Annotations and blocks are not parts and not missing; nothing to report.
                if (component is IAnnotation or ISubcircuit or Ground or INetNaming) continue;

                skipped.Add($"{component.Name} ({component.ComponentType})");
                continue;
            }

            body.AppendLine(line);
            written.Add(component.Name);
        }

        var deck = new StringBuilder();

        deck.AppendLine($"* {title ?? circuit.Title}");
        deck.AppendLine($"* Written by CirqAvalonia, {DateTimeOffset.Now:yyyy-MM-dd HH:mm}");
        deck.AppendLine("*");
        deck.AppendLine($"* Temperature {circuit.AmbientTemperatureCelsius:0.##} degC");

        if (skipped.Count > 0)
        {
            deck.AppendLine("*");
            deck.AppendLine("* These parts have no SPICE equivalent and are NOT in this deck. They are");
            deck.AppendLine("* simulated by event-driven code rather than by a netlist, so the circuit");
            deck.AppendLine("* below is incomplete where they were:");

            foreach (var part in skipped) deck.AppendLine($"*   {part}");
        }

        deck.AppendLine();
        deck.Append(body);

        // Models for everything that referenced one, so the deck stands on its own.
        var models = ModelCards(components);

        if (models.Count > 0)
        {
            deck.AppendLine();
            foreach (var card in models) deck.AppendLine(card);
        }

        deck.AppendLine();
        deck.AppendLine($".temp {circuit.AmbientTemperatureCelsius.ToString("0.##", CultureInfo.InvariantCulture)}");
        deck.AppendLine(".op");
        deck.AppendLine(".end");

        return new NetlistResult(deck.ToString(), written, skipped);
    }

    /// <summary>One element line, or null when the part has no SPICE equivalent.</summary>
    private static string? Element(
        CircuitComponent component, Netlist netlist,
        HashSet<string> taken, HashSet<string> reserved)
    {
        string Node(Terminal terminal) =>
            netlist.Contains(terminal) ? NameOf(netlist.NetOf(terminal)) : "0";

        string Name(char type) => Allocate(type, component.Name, taken, reserved);

        return component switch
        {
            Resistor r => $"{Name('R')} {Node(r.A)} {Node(r.B)} {Value(r.Resistance)}",
            Capacitor c => $"{Name('C')} {Node(c.A)} {Node(c.B)} {Value(c.Capacitance)}",
            Inductor l => $"{Name('L')} {Node(l.A)} {Node(l.B)} {Value(l.Inductance)}",

            // SPICE takes the positive node first, which is this library's Positive.
            DcVoltageSource v =>
                $"{Name('V')} {Node(v.Positive)} {Node(v.Negative)} DC {Value(v.Voltage)}",

            // And a current source's current flows from the first node, through the source, to the
            // second — which is the opposite of the convention here, so the nodes are swapped.
            DcCurrentSource i =>
                $"{Name('I')} {Node(i.Negative)} {Node(i.Positive)} DC {Value(i.Current)}",

            // A generator is a voltage source with a waveform on it, which is exactly what SPICE
            // has. Leaving it out would export an analog circuit with nothing driving it.
            FunctionGenerator f =>
                $"{Name('V')} {Node(f.Output)} {Node(f.Return)} {Waveform(f)}",

            Diode d => $"{Name('D')} {Node(d.Anode)} {Node(d.Cathode)} {ModelName(d.Model.Name)}",

            BipolarTransistor q =>
                $"{Name('Q')} {Node(q.Collector)} {Node(q.Base)} {Node(q.Emitter)} " +
                ModelName(q.Model.Name),

            // A MOSFET's bulk is tied to its source here, which is what a discrete part does.
            Mosfet m =>
                $"{Name('M')} {Node(m.Drain)} {Node(m.Gate)} {Node(m.Source)} {Node(m.Source)} " +
                ModelName(m.Model.Name),

            _ => null,
        };
    }

    /// <summary>
    /// A generator's waveform in SPICE's own notation.
    /// <para>
    /// SPICE has no triangle or sawtooth element, but <c>PULSE</c> takes explicit rise and fall
    /// times, and a triangle is a pulse that spends all its time rising and falling. A sawtooth is
    /// the same with the fall collapsed to nothing. That is not an approximation — it is the same
    /// waveform, described the way SPICE describes it.
    /// </para>
    /// </summary>
    private static string Waveform(FunctionGenerator generator)
    {
        var amplitude = generator.AmplitudePeakToPeak / 2.0;
        var offset = generator.DcOffset;
        var frequency = Math.Max(generator.Frequency, 1e-12);
        var period = 1.0 / frequency;

        if (!generator.IsEnabled || generator.Shape == Sources.Waveform.Dc)
            return $"DC {Value(offset)}";

        if (generator.Shape == Sources.Waveform.Sine)
        {
            return $"DC {Value(offset)} AC {Value(amplitude)} " +
                   $"SIN({Value(offset)} {Value(amplitude)} {Value(frequency)} 0 0 " +
                   $"{Value(generator.PhaseDegrees)})";
        }

        var low = offset - amplitude;
        var high = offset + amplitude;
        var edge = Math.Max(generator.EdgeTime, period * 1e-6);

        // PULSE(v1 v2 delay rise fall width period).
        var (rise, fall, width) = generator.Shape switch
        {
            Sources.Waveform.Triangle => (period / 2, period / 2, 0.0),
            Sources.Waveform.Sawtooth => (period * (1 - 1e-3), period * 1e-3, 0.0),
            _ => (edge, edge, Math.Max((period * generator.DutyCycle) - edge, period * 1e-6)),
        };

        return $"DC {Value(offset)} AC {Value(amplitude)} " +
               $"PULSE({Value(low)} {Value(high)} 0 {Value(rise)} {Value(fall)} " +
               $"{Value(width)} {Value(period)})";
    }

    /// <summary>
    /// An element name: the type letter, then the designator with its leading letters removed,
    /// falling back to the whole designator when that would collide.
    /// </summary>
    /// <summary>
    /// The SPICE element letter a part will be written as, or null when it has no equivalent. Kept
    /// beside <see cref="Element"/> and has to agree with it.
    /// </summary>
    private static char? TypeLetter(CircuitComponent component) => component switch
    {
        Resistor => 'R',
        Capacitor => 'C',
        Inductor => 'L',
        DcVoltageSource or FunctionGenerator => 'V',
        DcCurrentSource => 'I',
        Diode => 'D',
        BipolarTransistor => 'Q',
        Mosfet => 'M',
        _ => null,
    };

    private static string Allocate(
        char type, string designator, HashSet<string> taken, HashSet<string> reserved)
    {
        var clean = Sanitise(designator);
        var digits = clean.TrimStart(
            'A', 'B', 'C', 'D', 'E', 'F', 'G', 'H', 'I', 'J', 'K', 'L', 'M',
            'N', 'O', 'P', 'Q', 'R', 'S', 'T', 'U', 'V', 'W', 'X', 'Y', 'Z',
            'a', 'b', 'c', 'd', 'e', 'f', 'g', 'h', 'i', 'j', 'k', 'l', 'm',
            'n', 'o', 'p', 'q', 'r', 's', 't', 'u', 'v', 'w', 'x', 'y', 'z');

        // A part whose designator already begins with the right type letter keeps its own name:
        // a source called V1 should be V1 in the deck, and not lose it to an FG1 that happens to
        // sort earlier.
        List<string> candidates = [];

        if (clean.Length > 1 && char.ToUpperInvariant(clean[0]) == char.ToUpperInvariant(type))
            candidates.Add(clean);

        candidates.Add($"{type}{digits}");
        candidates.Add($"{type}{clean}");

        foreach (var candidate in candidates)
        {
            if (candidate.Length <= 1) continue;

            // Somebody else's reserved name, unless it is this part's own.
            if (!candidate.Equals(clean, StringComparison.OrdinalIgnoreCase) &&
                reserved.Contains(candidate))
            {
                continue;
            }

            if (taken.Add(candidate)) return candidate;
        }

        // Both taken, which needs two parts whose designators differ only in their prefix. Number
        // it rather than writing a duplicate name.
        for (var n = 2; ; n++)
        {
            var candidate = $"{type}{clean}_{n}";

            if (taken.Add(candidate)) return candidate;
        }
    }

    /// <summary>
    /// A <c>.model</c> card for every distinct model the deck referenced, so it stands alone
    /// rather than depending on a library the reader has not got.
    /// </summary>
    private static List<string> ModelCards(IEnumerable<CircuitComponent> components)
    {
        var cards = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var component in components)
        {
            switch (component)
            {
                case Diode d:
                    cards[ModelName(d.Model.Name)] =
                        $".model {ModelName(d.Model.Name)} D(Is={Value(d.Model.SaturationCurrent)} " +
                        $"N={Value(d.Model.EmissionCoefficient)} Rs={Value(d.Model.SeriesResistance)} " +
                        $"Bv={Value(d.Model.BreakdownVoltage)} Ibv={Value(d.Model.BreakdownCurrent)} " +
                        $"Eg={Value(d.Model.EnergyGap)} Xti={Value(d.Model.TemperatureExponent)})";
                    break;

                case BipolarTransistor q:
                    cards[ModelName(q.Model.Name)] =
                        $".model {ModelName(q.Model.Name)} " +
                        $"{(q.Model.Polarity == BjtPolarity.Npn ? "NPN" : "PNP")}(" +
                        $"Is={Value(q.Model.SaturationCurrent)} Bf={Value(q.Model.ForwardBeta)} " +
                        $"Br={Value(q.Model.ReverseBeta)} Vaf={Value(q.Model.EarlyVoltage)} " +
                        $"Nf={Value(q.Model.EmissionCoefficient)})";
                    break;

                case Mosfet m:
                    // The threshold goes back out signed, which is how a card states it.
                    var threshold = m.Model.Channel == MosfetChannel.NChannel
                        ? m.Model.ThresholdVoltage
                        : -m.Model.ThresholdVoltage;

                    cards[ModelName(m.Model.Name)] =
                        $".model {ModelName(m.Model.Name)} " +
                        $"{(m.Model.Channel == MosfetChannel.NChannel ? "NMOS" : "PMOS")}(" +
                        $"Vto={Value(threshold)} Kp={Value(m.Model.TransconductanceParameter)} " +
                        $"Lambda={Value(m.Model.ChannelLengthModulation)})";
                    break;
            }
        }

        return [.. cards.Values];
    }

    /// <summary>
    /// What to call a net. Ground is node 0, which is the one name SPICE insists on; a net a
    /// person has named keeps its name, and anything else takes its index.
    /// </summary>
    private static string NameOf(Net net)
    {
        if (net.IsGround) return "0";

        var label = net.Label?.Trim();

        return string.IsNullOrEmpty(label) ? $"N{net.Index:000}" : Sanitise(label);
    }

    /// <summary>
    /// A designator without its prefix letter, since SPICE puts the element type in the first
    /// character — R1 would otherwise come out as RR1.
    /// </summary>
    private static string Strip(string designator)
    {
        var name = Sanitise(designator);

        return name.Length > 1 && char.IsAsciiLetter(name[0]) ? name[1..] : name;
    }

    /// <summary>A model name SPICE will accept, since this library's have spaces and brackets.</summary>
    private static string ModelName(string name) => Sanitise(name);

    private static string Sanitise(string text)
    {
        var clean = new StringBuilder(text.Length);

        foreach (var c in text)
            clean.Append(char.IsAsciiLetterOrDigit(c) || c is '_' ? c : '_');

        return clean.Length == 0 ? "X" : clean.ToString();
    }

    /// <summary>
    /// A number SPICE will read back the same way. Written in exponential form rather than with a
    /// suffix on purpose: <c>1e-3</c> cannot be misread, where <c>1m</c> is milli to SPICE and mega
    /// to about half the people who look at it.
    /// </summary>
    private static string Value(double value) =>
        value == 0
            ? "0"
            : value.ToString("0.#######e+00", CultureInfo.InvariantCulture);
}
