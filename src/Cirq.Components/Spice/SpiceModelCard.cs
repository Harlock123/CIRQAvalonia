using System.Text.RegularExpressions;

namespace Cirq.Components.Spice;

/// <summary>What kind of device a model card describes.</summary>
public enum SpiceDeviceKind
{
    Diode,
    Npn,
    Pnp,
    NChannelMosfet,
    PChannelMosfet,
}

/// <summary>
/// A parsed <c>.model</c> card: its name, what sort of device it is, and its parameters.
/// </summary>
/// <param name="Name">The model's name, as written.</param>
/// <param name="Kind">Which device the card is for.</param>
/// <param name="Parameters">Its parameters, keyed without regard to case.</param>
public sealed record SpiceModelCard(
    string Name, SpiceDeviceKind Kind, IReadOnlyDictionary<string, double> Parameters)
{
    /// <summary>A parameter by any of the names it is known under, or a fallback.</summary>
    public double Value(double fallback, params string[] names)
    {
        foreach (var name in names)
            if (Parameters.TryGetValue(name, out var value))
                return value;

        return fallback;
    }

    /// <summary>True when the card mentions any of these parameters.</summary>
    public bool Has(params string[] names) => names.Any(Parameters.ContainsKey);

    /// <summary>
    /// The card written back out as SPICE text, which <see cref="SpiceModelReader.Parse"/> reads
    /// to exactly this card again.
    /// <para>
    /// Regenerated rather than kept verbatim, deliberately. A card that came in wrapped over five
    /// continuation lines with a manufacturer's header comment goes back out as one line, and two
    /// cards that say the same thing in different layouts come out identical — which is what makes
    /// an embedded model comparable to the one already in a library, and keeps a saved circuit
    /// diffable.
    /// </para>
    /// <para>
    /// Only what was read is written. A card carrying transit times and capacitances loses them
    /// here, and so it should: they were never used, and writing them back out would imply they
    /// had been.
    /// </para>
    /// </summary>
    public string ToCard()
    {
        var type = Kind switch
        {
            SpiceDeviceKind.Diode => "D",
            SpiceDeviceKind.Npn => "NPN",
            SpiceDeviceKind.Pnp => "PNP",
            SpiceDeviceKind.NChannelMosfet => "NMOS",
            _ => "PMOS",
        };

        var parameters = Parameters
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => $"{p.Key.ToUpperInvariant()}={Number(p.Value)}");

        return $".model {Name} {type}({string.Join(' ', parameters)})";
    }

    /// <summary>
    /// A number SPICE will read back unchanged. Exponential form throughout, because SPICE's
    /// suffixes are a trap — <c>M</c> is milli and <c>MEG</c> is mega — and round-tripping a value
    /// matters more here than looking like a datasheet.
    /// </summary>
    private static string Number(double value) =>
        value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Reads SPICE <c>.model</c> cards.
/// <para>
/// This is what turns the parts library from "what is built in" into "anything with a datasheet".
/// Manufacturers publish SPICE models for almost everything, and the parameters in them —
/// <c>Is</c>, <c>N</c>, <c>Rs</c>, <c>Bf</c>, <c>Vto</c>, <c>Kp</c> — are the same parameters the
/// models in this library already use, because both come from the same forty-year-old formulation.
/// </para>
/// <para>
/// It reads the subset that maps onto what is simulated here, and says what it ignored rather than
/// pretending to have used it. A model card carrying capacitances and transit times is not wrong
/// to have them; this simulator simply does not have anywhere to put them, and quietly dropping
/// them would leave somebody believing their part was modelled more closely than it is.
/// </para>
/// </summary>
public static partial class SpiceModelReader
{
    /// <summary>What came back from a parse.</summary>
    /// <param name="Cards">The cards understood.</param>
    /// <param name="Problems">Lines that looked like model cards and were not usable.</param>
    public sealed record Result(IReadOnlyList<SpiceModelCard> Cards, IReadOnlyList<string> Problems);

    /// <summary>
    /// Parses every <c>.model</c> card in some text. Continuation lines beginning with <c>+</c>
    /// are joined on, comments after <c>;</c> and whole-line <c>*</c> comments are dropped.
    /// </summary>
    public static Result Parse(string? text)
    {
        List<SpiceModelCard> cards = [];
        List<string> problems = [];

        foreach (var statement in Statements(text ?? string.Empty))
        {
            if (!statement.StartsWith(".model", StringComparison.OrdinalIgnoreCase)) continue;

            var match = CardPattern().Match(statement);

            if (!match.Success)
            {
                problems.Add($"Could not read this as a model card: {Shorten(statement)}");
                continue;
            }

            var name = match.Groups["name"].Value;
            var type = match.Groups["type"].Value;

            if (KindOf(type) is not { } kind)
            {
                problems.Add(
                    $"'{name}' is a {type.ToUpperInvariant()} model, which this simulator has no " +
                    "device for. Diodes (D), bipolars (NPN, PNP) and MOSFETs (NMOS, PMOS) are " +
                    "understood.");

                continue;
            }

            var parameters = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (Match parameter in ParameterPattern().Matches(match.Groups["body"].Value))
            {
                var value = SpiceValue.Parse(parameter.Groups["value"].Value);
                if (value is null) continue;

                parameters[parameter.Groups["key"].Value] = value.Value;
            }

            if (parameters.Count == 0)
            {
                problems.Add($"'{name}' has no parameters this simulator could read.");
                continue;
            }

            cards.Add(new SpiceModelCard(name, kind, parameters));
        }

        if (cards.Count == 0 && problems.Count == 0)
            problems.Add("No .model cards found. A card looks like: .model 1N4148 D(Is=2.52n N=1.75)");

        return new Result(cards, problems);
    }

    private static SpiceDeviceKind? KindOf(string type) => type.ToUpperInvariant() switch
    {
        "D" => SpiceDeviceKind.Diode,
        "NPN" => SpiceDeviceKind.Npn,
        "PNP" => SpiceDeviceKind.Pnp,
        "NMOS" => SpiceDeviceKind.NChannelMosfet,
        "PMOS" => SpiceDeviceKind.PChannelMosfet,
        _ => null,
    };

    /// <summary>
    /// Whole statements, with continuation lines joined and comments removed. SPICE wraps long
    /// cards onto lines beginning with a plus, and a model card that is four lines long in the
    /// datasheet is one statement.
    /// </summary>
    private static IEnumerable<string> Statements(string text)
    {
        var current = string.Empty;

        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();

            // Comments: a whole line starting with * or ;, and anything after a ; on a line.
            if (line.StartsWith('*')) continue;

            var semicolon = line.IndexOf(';');
            if (semicolon >= 0) line = line[..semicolon].Trim();

            if (line.Length == 0) continue;

            if (line.StartsWith('+'))
            {
                current += " " + line[1..].Trim();
                continue;
            }

            if (current.Length > 0) yield return current;

            current = line;
        }

        if (current.Length > 0) yield return current;
    }

    private static string Shorten(string text) =>
        text.Length <= 60 ? text : text[..57] + "...";

    [GeneratedRegex(
        @"^\.model\s+(?<name>[^\s(]+)\s+(?<type>[A-Za-z]+)\s*\(?(?<body>.*?)\)?\s*$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CardPattern();

    [GeneratedRegex(@"(?<key>[A-Za-z][A-Za-z0-9_]*)\s*=\s*(?<value>[^\s=]+)")]
    private static partial Regex ParameterPattern();
}
