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
    /// Converts a card and adds the result to the relevant library, replacing anything of the
    /// same name.
    /// </summary>
    public static ImportedModel Register(SpiceModelCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

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
