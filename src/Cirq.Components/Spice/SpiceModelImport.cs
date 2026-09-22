using Cirq.Components.Nonlinear;

namespace Cirq.Components.Spice;

/// <summary>One model brought in from a card, and what was and was not used.</summary>
/// <param name="Name">The model's name.</param>
/// <param name="Kind">Which device it is.</param>
/// <param name="Used">The parameters that reached the simulator's model.</param>
/// <param name="Ignored">
/// The parameters the card carried that this simulator has nowhere to put. Reported rather than
/// dropped, so nobody comes away believing a part is modelled more closely than it is.
/// </param>
public sealed record ImportedModel(
    string Name,
    SpiceDeviceKind Kind,
    IReadOnlyList<string> Used,
    IReadOnlyList<string> Ignored)
{
    public string Summary
    {
        get
        {
            var kind = Kind switch
            {
                SpiceDeviceKind.Diode => "diode",
                SpiceDeviceKind.Npn => "NPN",
                SpiceDeviceKind.Pnp => "PNP",
                SpiceDeviceKind.NChannelMosfet => "N-channel MOSFET",
                _ => "P-channel MOSFET",
            };

            var line = $"{kind}, {Used.Count} parameter{(Used.Count == 1 ? string.Empty : "s")} used";

            return Ignored.Count == 0
                ? line
                : $"{line}, {Ignored.Count} ignored ({string.Join(", ", Ignored)})";
        }
    }
}

/// <summary>
/// Turns parsed <c>.model</c> cards into the models this simulator uses, and registers them.
/// </summary>
public static class SpiceModelImport
{
    /// <summary>Parameters each device understands, so anything else can be named as ignored.</summary>
    private static readonly Dictionary<SpiceDeviceKind, string[]> Understood = new()
    {
        [SpiceDeviceKind.Diode] = ["IS", "N", "RS", "BV", "IBV", "EG", "XTI"],
        [SpiceDeviceKind.Npn] = ["IS", "BF", "BR", "VAF", "VA", "NF"],
        [SpiceDeviceKind.Pnp] = ["IS", "BF", "BR", "VAF", "VA", "NF"],
        [SpiceDeviceKind.NChannelMosfet] = ["VTO", "VT0", "KP", "LAMBDA"],
        [SpiceDeviceKind.PChannelMosfet] = ["VTO", "VT0", "KP", "LAMBDA"],
    };

    /// <summary>
    /// The card behind every imported model, by name.
    /// <para>
    /// Kept because a model is only half of what an imported part is: the other half is the card
    /// it came from, and without that a circuit using the part cannot be saved in a form that
    /// opens anywhere else. This is what a saved file copies into itself.
    /// </para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SpiceModelCard> Cards =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The card an imported model was built from, or null for a built-in.
    /// <para>
    /// Checked against the library rather than trusted: a model can be taken out through its own
    /// library directly, and a card left behind for a name that has since gone back to meaning
    /// the built-in would have a saved circuit carrying a definition of a part it is not using.
    /// A card that no longer describes what the name resolves to is not a card.
    /// </para>
    /// </summary>
    public static SpiceModelCard? CardFor(string? name)
    {
        if (name is null || !Cards.TryGetValue(name, out var card)) return null;

        if (Equals(Existing(name, card.Kind), Build(card))) return card;

        Cards.TryRemove(name, out _);
        return null;
    }

    /// <summary>Every imported model's card, by the order they were brought in.</summary>
    public static IReadOnlyCollection<SpiceModelCard> ImportedCards => [.. Cards.Values];

    /// <summary>
    /// The model a card would build, without registering it — so an incoming card can be compared
    /// against what a library already holds under that name.
    /// </summary>
    public static object Build(SpiceModelCard card) => card.Kind switch
    {
        SpiceDeviceKind.Diode => ToDiode(card),
        SpiceDeviceKind.Npn or SpiceDeviceKind.Pnp => ToBipolar(card),
        _ => ToMosfet(card),
    };

    /// <summary>
    /// The model of that name already in the relevant library, built in or imported, or null when
    /// nothing goes by it.
    /// </summary>
    public static object? Existing(string? name, SpiceDeviceKind kind)
    {
        if (name is null) return null;

        bool Matches(object model) =>
            string.Equals(model.ToString(), name, StringComparison.OrdinalIgnoreCase);

        return kind switch
        {
            SpiceDeviceKind.Diode => DiodeModel.Library.FirstOrDefault(m => Matches(m)),
            SpiceDeviceKind.Npn or SpiceDeviceKind.Pnp => BjtModel.Library.FirstOrDefault(m => Matches(m)),
            _ => MosfetModel.Library.FirstOrDefault(m => Matches(m)),
        };
    }

    /// <summary>
    /// Takes an imported model out of its library and forgets its card. Built-in models are not
    /// removable; an import of the same name was shadowing one, and this brings it back.
    /// </summary>
    public static bool Unregister(string name, SpiceDeviceKind kind)
    {
        Cards.TryRemove(name, out _);

        return kind switch
        {
            SpiceDeviceKind.Diode => DiodeModel.Unregister(name),
            SpiceDeviceKind.Npn or SpiceDeviceKind.Pnp => BjtModel.Unregister(name),
            _ => MosfetModel.Unregister(name),
        };
    }

    /// <summary>
    /// Converts a card and adds the result to the relevant library, replacing anything of the
    /// same name.
    /// </summary>
    public static ImportedModel Register(SpiceModelCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        Cards[card.Name] = card;

        var known = Understood[card.Kind];

        var used = card.Parameters.Keys
            .Where(k => known.Contains(k, StringComparer.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ignored = card.Parameters.Keys
            .Where(k => !known.Contains(k, StringComparer.OrdinalIgnoreCase))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        switch (card.Kind)
        {
            case SpiceDeviceKind.Diode:
                DiodeModel.Register(ToDiode(card));
                break;

            case SpiceDeviceKind.Npn:
            case SpiceDeviceKind.Pnp:
                BjtModel.Register(ToBipolar(card));
                break;

            default:
                MosfetModel.Register(ToMosfet(card));
                break;
        }

        return new ImportedModel(card.Name, card.Kind, used, ignored);
    }

    /// <summary>
    /// A diode. The defaults are SPICE's own where it has one, and this library's where the
    /// parameter is not a SPICE parameter at all — the LED band-gap fit, for instance.
    /// </summary>
    public static DiodeModel ToDiode(SpiceModelCard card) => new(
        Name: card.Name,
        SaturationCurrent: card.Value(1e-14, "IS"),
        EmissionCoefficient: card.Value(1.0, "N"),
        SeriesResistance: card.Value(0.0, "RS"),

        // A card without a breakdown voltage is a diode nobody intends to run backwards. A large
        // number keeps it out of the way rather than putting breakdown at zero.
        BreakdownVoltage: card.Value(1e3, "BV"),
        BreakdownCurrent: card.Value(1e-3, "IBV"),
        EnergyGap: card.Value(1.11, "EG"),
        TemperatureExponent: card.Value(3.0, "XTI"));

    /// <summary>A bipolar. VAF is the Early voltage; a card without one is treated as flat.</summary>
    public static BjtModel ToBipolar(SpiceModelCard card) => new(
        Name: card.Name,
        Polarity: card.Kind == SpiceDeviceKind.Npn ? BjtPolarity.Npn : BjtPolarity.Pnp,
        SaturationCurrent: card.Value(1e-14, "IS"),
        ForwardBeta: card.Value(100, "BF"),
        ReverseBeta: card.Value(1, "BR"),

        // Effectively infinite rather than zero: a missing Early voltage means no Early effect,
        // and zero would mean the output conductance was infinite.
        EarlyVoltage: card.Value(1e6, "VAF", "VA"),
        EmissionCoefficient: card.Value(1.0, "NF"));

    /// <summary>
    /// A MOSFET. SPICE's KP is in amps per volt squared and is the same quantity this library
    /// calls the transconductance parameter, so it carries straight across.
    /// </summary>
    public static MosfetModel ToMosfet(SpiceModelCard card)
    {
        var threshold = card.Value(2.0, "VTO", "VT0");

        return new MosfetModel(
            Name: card.Name,
            Channel: card.Kind == SpiceDeviceKind.NChannelMosfet
                ? MosfetChannel.NChannel
                : MosfetChannel.PChannel,

            // A P-channel card quotes a negative threshold; this library works in the channel's
            // own convention, where the overdrive is positive for both.
            ThresholdVoltage: Math.Abs(threshold),
            TransconductanceParameter: card.Value(0.05, "KP"),
            ChannelLengthModulation: card.Value(0.0, "LAMBDA"));
    }
}
