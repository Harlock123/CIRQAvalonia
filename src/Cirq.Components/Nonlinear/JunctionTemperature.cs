using Cirq.Core.Simulation;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// How a semiconductor junction's parameters move with temperature.
/// <para>
/// This is the part of the physics that is easy to leave out and wrong to. The thermal voltage
/// <c>kT/q</c> obviously rises with temperature, and a model that varies only that makes a
/// diode's forward drop <b>rise</b> by about two millivolts a degree. A real one <b>falls</b> by
/// about two millivolts a degree, and the sign is opposite because the saturation current roughly
/// doubles every ten degrees and that term wins comfortably.
/// </para>
/// <para>
/// So the two have to move together or not at all. With both in place the standard result falls
/// out of the arithmetic rather than being asserted:
/// <c>dVf/dT ≈ (Vf − Eg − 3·Vt) / T</c>, which for a silicon junction sitting at 0.6 V is very
/// close to −2 mV/°C — the number every textbook quotes and every temperature-compensated circuit
/// is built around.
/// </para>
/// </summary>
public static class JunctionTemperature
{
    /// <summary>
    /// The temperature the models' parameters are quoted at: 27 °C, which is what SPICE has used
    /// since the seventies and what datasheets mean by "room temperature".
    /// </summary>
    public const double NominalKelvin = 300.15;

    /// <summary>Room temperature in Kelvin, for turning a Celsius figure into one.</summary>
    public static double FromCelsius(double celsius) => celsius + 273.15;

    /// <summary>And back again.</summary>
    public static double ToCelsius(double kelvin) => kelvin - 273.15;

    /// <summary>
    /// Saturation current at a temperature, from its value at the nominal one.
    /// <para>
    /// The standard SPICE expression: a power law in the temperature ratio, and an exponential in
    /// the band gap. The exponential is the part that matters — it is what makes the current
    /// double every ten degrees or so, and it is why leakage in a reverse-biased junction is a
    /// room-temperature measurement that means nothing about a hot one.
    /// </para>
    /// </summary>
    /// <param name="nominal">Saturation current at <see cref="NominalKelvin"/>.</param>
    /// <param name="kelvin">The temperature wanted.</param>
    /// <param name="emission">The junction's emission coefficient, n.</param>
    /// <param name="energyGap">
    /// Band gap in electron volts. 1.11 for silicon. For the parts where the literal gap does not
    /// reproduce the datasheet's temperature coefficient — LEDs especially — this is fitted to the
    /// coefficient instead, which is what a model parameter is for.
    /// </param>
    /// <param name="exponent">
    /// The temperature exponent, SPICE's XTI: 3 for an ordinary junction and 2 for a Schottky,
    /// where conduction is over a barrier rather than across a depletion region.
    /// </param>
    public static double SaturationCurrentAt(
        double nominal, double kelvin, double emission, double energyGap, double exponent)
    {
        if (nominal <= 0) return nominal;

        var temperature = Math.Max(kelvin, 1.0);
        var n = Math.Max(emission, 1e-3);
        var ratio = temperature / NominalKelvin;

        // Eg·q/k has units of Kelvin; the bracket is one over a temperature difference.
        var scale = energyGap * PhysicalConstants.ElementaryCharge / (n * PhysicalConstants.Boltzmann);
        var bracket = (1.0 / NominalKelvin) - (1.0 / temperature);

        // Clamped, because a wide gap at a low temperature overflows the exponential long before
        // it stops being physical — and a saturation current of infinity is not a useful answer.
        var power = Math.Clamp(scale * bracket, -200.0, 200.0);

        return nominal * Math.Pow(ratio, exponent / n) * Math.Exp(power);
    }

    /// <summary>
    /// Forward current gain at a temperature. A bipolar's beta climbs with temperature — roughly
    /// half as much again from room temperature to 125 °C on a typical small-signal part — which
    /// is one of the reasons a bias network built around beta is a bias network that drifts.
    /// </summary>
    /// <param name="nominal">Beta at <see cref="NominalKelvin"/>.</param>
    /// <param name="kelvin">The temperature wanted.</param>
    /// <param name="exponent">SPICE's XTB; 1.5 is ordinary for a small-signal transistor.</param>
    public static double BetaAt(double nominal, double kelvin, double exponent) =>
        nominal * Math.Pow(Math.Max(kelvin, 1.0) / NominalKelvin, exponent);
}
