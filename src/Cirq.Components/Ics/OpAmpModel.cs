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
    double QuiescentCurrent)
{
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
        QuiescentCurrent: 1.7e-3);

    /// <summary>JFET input, fast and high impedance.</summary>
    public static readonly OpAmpModel Tl081 = new(
        "TL081", 200_000, 3e6, 13e6, 1e12, 100, 1.5, 3e-3, 30e-12, 1.4e-3);

    /// <summary>Single-supply capable, output swings close to the negative rail.</summary>
    public static readonly OpAmpModel Lm358 = new(
        "LM358", 100_000, 1e6, 0.3e6, 2e6, 100, 0.7, 2e-3, 45e-9, 0.7e-3);

    public static readonly IReadOnlyList<OpAmpModel> Library = [Lm741, Tl081, Lm358];

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
