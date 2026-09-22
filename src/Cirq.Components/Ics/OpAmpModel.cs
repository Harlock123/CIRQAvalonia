namespace Cirq.Components.Ics;

/// <summary>
/// Datasheet parameters for an operational amplifier macromodel. The internal gain-stage values
/// (transconductance, compensation capacitor) are derived from these rather than stored, so the
/// model stays consistent when a parameter is edited from the inspector.
/// </summary>
public sealed record OpAmpModel(
    string Name,
    double OpenLoopGain,
    double GainBandwidthProduct,
    double SlewRate,
    double InputResistance,
    double OutputResistance,
    double OutputSwingHeadroom,
    double InputOffsetVoltage,
    double InputBiasCurrent,
    double QuiescentCurrent,
    double OffsetDriftPerKelvin = 15e-6,
    double VoltageNoiseDensity = 20e-9,
    double CurrentNoiseDensity = 0.5e-12,
    double VoltageNoiseCornerHz = 200.0,
    double CurrentNoiseCornerHz = 200.0)
{
    /// <summary>
    /// Input offset voltage at a temperature.
    /// <para>
    /// Offset <i>drift</i> is the specification that separates a precision part from a jellybean,
    /// and the reason is arithmetic: the offset itself can be trimmed out once, at whatever
    /// temperature you trimmed it at, and the drift cannot. A part with a millivolt of offset and
    /// no drift is a better part than one with a hundred microvolts that moves by ten every
    /// degree, and on a gain of a thousand across an equipment's operating range the difference
    /// is volts.
    /// </para>
    /// </summary>
    /// <summary>
    /// Input-referred voltage noise at a frequency, in volts per root hertz.
    /// <para>
    /// Two terms. The <b>white</b> floor is what the datasheet quotes at 1 kHz and is flat from
    /// there upwards. Below the <b>corner</b> it rises as 1/√f — flicker noise, which is what makes
    /// a DC-coupled precision circuit hard and is the reason a chopper part exists at all. The
    /// corner is where the two are equal, so the density there is √2 times the floor.
    /// </para>
    /// <para>
    /// Both are properties of the part rather than of anything derivable, so they are quoted per
    /// model from its datasheet. A JFET or CMOS input is quiet in current and noisy in voltage; a
    /// bipolar is the other way round, and which matters depends entirely on the impedance the
    /// amplifier is looking at.
    /// </para>
    /// </summary>
    public double VoltageNoiseAt(double hertz) =>
        VoltageNoiseDensity * Math.Sqrt(1.0 + (VoltageNoiseCornerHz / Math.Max(hertz, 1e-9)));

    /// <summary>Input-referred current noise at a frequency, in amps per root hertz.</summary>
    public double CurrentNoiseAt(double hertz) =>
        CurrentNoiseDensity * Math.Sqrt(1.0 + (CurrentNoiseCornerHz / Math.Max(hertz, 1e-9)));

    public double OffsetAt(double kelvin) =>
        InputOffsetVoltage +
        (OffsetDriftPerKelvin * (kelvin - Cirq.Components.Nonlinear.JunctionTemperature.NominalKelvin));

    private readonly double? _negativeSwingHeadroom;

    /// <summary>
    /// How close the output gets to the <i>negative</i> rail, in volts, where
    /// <c>OutputSwingHeadroom</c> says how close it gets to the positive one. Left alone it is the
    /// same figure, which is right for a part built round a symmetric output stage.
    /// <para>
    /// It is separate because most single-supply parts are not symmetric, and the asymmetry is the
    /// whole reason they exist. An LM358 pulls its output down to within twenty millivolts of the
    /// negative rail and stops a volt and a half below the positive one, so on a single five volt
    /// supply it can use the bottom of the range and not the top. Modelling one headroom for both
    /// rails gets the part wrong in the direction people actually use it.
    /// </para>
    /// </summary>
    public double NegativeSwingHeadroom
    {
        get => _negativeSwingHeadroom ?? OutputSwingHeadroom;
        init => _negativeSwingHeadroom = value;
    }

    /// <summary>The classic bipolar general-purpose op-amp: Aol 200k, 1 MHz GBW, 0.5 V/us.</summary>
    public static readonly OpAmpModel Lm741 = new(
        Name: "LM741",
        OpenLoopGain: 200_000,
        GainBandwidthProduct: 1e6,
        SlewRate: 0.5e6,
        InputResistance: 2e6,
        OutputResistance: 75,
        OutputSwingHeadroom: 1.5,
        InputOffsetVoltage: 1e-3,
        InputBiasCurrent: 80e-9,
        QuiescentCurrent: 1.7e-3,
        OffsetDriftPerKelvin: 15e-6,

        // A bipolar input: middling voltage noise, and current noise large enough to matter the
        // moment it is asked to look at anything above a few kilohms.
        VoltageNoiseDensity: 20e-9,
        CurrentNoiseDensity: 0.5e-12,
        VoltageNoiseCornerHz: 200,
        CurrentNoiseCornerHz: 2e3);

    /// <summary>JFET input, fast and high impedance.</summary>
    public static readonly OpAmpModel Tl081 = new(
        "TL081", 200_000, 3e6, 13e6, 1e12, 100, 1.5, 3e-3, 30e-12, 1.4e-3,
        OffsetDriftPerKelvin: 18e-6,

        // A JFET input, which is the trade in one line: a thousand times less current noise than
        // the LM741's, at the price of a higher flicker corner. Looking at a megohm it is far
        // quieter; looking at fifty ohms it is not.
        VoltageNoiseDensity: 18e-9,
        CurrentNoiseDensity: 0.01e-12,
        VoltageNoiseCornerHz: 2e3,
        CurrentNoiseCornerHz: 1e3);

    /// <summary>
    /// The single-supply workhorse. Its output gets closer to the rails than an LM741's, which is
    /// what lets it work on one battery, but it still stops well short of them.
    /// <para>
    /// The part is <b>asymmetric</b>, and that is the point of it: the output pulls down to within
    /// twenty millivolts of the negative rail but stops a volt and a half below the positive one.
    /// On a single five volt supply that means roughly 0.02 V to 3.5 V of usable output — plenty
    /// of room at the bottom, none at the top. Compare it with the <see cref="Mcp6002"/>, which
    /// reaches both.
    /// </para>
    /// </summary>
    public static readonly OpAmpModel Lm358 = new(
        "LM358", 100_000, 1e6, 0.3e6, 2e6, 100, 1.5, 2e-3, 45e-9, 0.7e-3,
        OffsetDriftPerKelvin: 7e-6,
        VoltageNoiseDensity: 40e-9,
        CurrentNoiseDensity: 0.3e-12,
        VoltageNoiseCornerHz: 500,
        CurrentNoiseCornerHz: 1e3)
    {
        NegativeSwingHeadroom = 0.02,
    };

    /// <summary>
    /// A CMOS rail-to-rail part, and the reason to have it here is the comparison.
    /// <para>
    /// "Rail to rail" means what it says: the output reaches within a few tens of millivolts of
    /// the supplies, where the LM358 above stops the better part of a volt short and the LM741
    /// a volt and a half. Build the same follower three times and watch how much of a five volt
    /// supply each one can actually use — that is the single most common surprise in
    /// single-supply analog work, and much easier to believe once seen.
    /// </para>
    /// </summary>
    public static readonly OpAmpModel Mcp6002 = new(
        "MCP6002", 112_000, 1e6, 0.6e6, 1e13, 100, 0.025, 2e-3, 1e-12, 100e-6,
        OffsetDriftPerKelvin: 2e-6,

        // CMOS: almost no current noise at all, and a high flicker corner — which is why a
        // rail-to-rail CMOS part is a poor choice for a DC-coupled precision measurement and an
        // excellent one for a high-impedance sensor above a kilohertz.
        VoltageNoiseDensity: 28e-9,
        CurrentNoiseDensity: 0.6e-15,
        VoltageNoiseCornerHz: 3e3,
        CurrentNoiseCornerHz: 1e3);

    /// <summary>
    /// Four of the <see cref="Lm358"/> in one package, and electrically that is all it is — same
    /// gain, same bandwidth, same stubborn volt and a half of headroom at the top. It is here as
    /// its own entry because a channel of an LM324 is what a <see cref="QuadOpAmp"/> contains, and
    /// a saved circuit has to be able to name it.
    /// </summary>
    public static readonly OpAmpModel Lm324 = Lm358 with { Name = "LM324" };

    public static readonly IReadOnlyList<OpAmpModel> Library = [Lm741, Tl081, Lm358, Lm324, Mcp6002];

    /// <summary>Input-stage transconductance. Fixed; the other values scale around it.</summary>
    public double Transconductance => 1e-4;

    /// <summary>Dominant-pole compensation capacitance, set by the gain-bandwidth product.</summary>
    public double CompensationCapacitance => Transconductance / (2.0 * Math.PI * GainBandwidthProduct);

    /// <summary>Gain-stage load resistance, set by the DC open-loop gain.</summary>
    public double GainResistance => OpenLoopGain / Transconductance;

    /// <summary>Tail current the input stage can deliver, which is what sets the slew rate.</summary>
    public double SlewCurrent => SlewRate * CompensationCapacitance;

    /// <summary>Frequency of the dominant pole, in hertz.</summary>
    public double DominantPoleHz => GainBandwidthProduct / OpenLoopGain;

    public override string ToString() => Name;
}
