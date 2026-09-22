using Cirq.Core.Topology;

namespace Cirq.Core.Simulation;

/// <summary>
/// One noise generator inside a part, as the Norton current source it is equivalent to.
/// </summary>
/// <param name="Name">
/// What to call it in a ranking — "R3 thermal", "Q1 shot (collector)". A part with two generators
/// in it produces two of these, because which one dominates is a different answer from which part
/// dominates.
/// </param>
/// <param name="NodeP">Node it flows from, or <see cref="Netlist.GroundIndex"/>.</param>
/// <param name="NodeN">Node it flows to.</param>
/// <param name="SpectralDensity">
/// Mean-square current per hertz, in A²/Hz. Squared because noise powers add and amplitudes do
/// not: two equal generators make √2 times the noise, not twice.
/// </param>
public readonly record struct NoiseEmission(string Name, int NodeP, int NodeN, double SpectralDensity);

/// <summary>
/// A component that generates noise of its own.
/// <para>
/// Everything that dissipates makes thermal noise and everything with a junction in it makes shot
/// noise, so this is not an exotic property of special parts — it is a property of most of them.
/// A component that does not implement this is treated as silent, which is right for an ideal
/// source or a switch and is an approximation everywhere else.
/// </para>
/// </summary>
public interface INoiseSource
{
    /// <summary>
    /// The generators inside this part at the bias point it is sitting at.
    /// </summary>
    /// <param name="system">For resolving the part's own terminals to nodes.</param>
    /// <param name="state">The operating point, including temperature.</param>
    /// <param name="hertz">
    /// The frequency being asked about. White generators ignore it; a flicker term would not.
    /// </param>
    IEnumerable<NoiseEmission> NoiseSources(MnaSystem system, SimulationState state, double hertz);
}

/// <summary>The physical constants noise is made of, in one place so they agree everywhere.</summary>
public static class NoisePhysics
{
    /// <summary>Boltzmann's constant, joules per kelvin.</summary>
    public const double Boltzmann = 1.380649e-23;

    /// <summary>Elementary charge, coulombs.</summary>
    public const double ElementaryCharge = 1.602176634e-19;

    /// <summary>
    /// Thermal noise current of a resistance, in A²/Hz: <c>4kT/R</c>.
    /// <para>
    /// Johnson noise. It is not a property of the material or the make — every resistance of the
    /// same value at the same temperature makes exactly this much, and no more, and there is
    /// nothing to be done about it but use a smaller resistance or a colder one. A 1 kΩ at room
    /// temperature is 4 nV/√Hz, which is the number worth carrying in your head.
    /// </para>
    /// </summary>
    public static double Thermal(double resistance, double kelvin) =>
        resistance <= 0 ? 0.0 : 4.0 * Boltzmann * kelvin / resistance;

    /// <summary>
    /// Shot noise of a current crossing a junction, in A²/Hz: <c>2qI</c>.
    /// <para>
    /// Charge arrives one electron at a time, and the arrivals are independent — so a "steady"
    /// current is steady only on average. Unlike thermal noise this does not depend on
    /// temperature, and unlike thermal noise it is not there at all when no current flows.
    /// </para>
    /// </summary>
    public static double Shot(double current) => 2.0 * ElementaryCharge * Math.Abs(current);

    /// <summary>
    /// Channel thermal noise of a MOSFET in saturation, in A²/Hz: <c>4kT·γ·gm</c>, with γ = 2/3
    /// for a long channel.
    /// <para>
    /// The channel is a resistor that the gate is squeezing, so what comes out is thermal noise —
    /// but of a resistance that is not any of the terminal resistances, which is why it is written
    /// against the transconductance instead.
    /// </para>
    /// </summary>
    public static double Channel(double transconductance, double kelvin, double gamma = 2.0 / 3.0) =>
        4.0 * Boltzmann * kelvin * gamma * Math.Abs(transconductance);
}
