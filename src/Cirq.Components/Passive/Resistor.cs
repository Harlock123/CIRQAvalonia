using Cirq.Core.Simulation;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;
using Cirq.Core.Probing;
using Cirq.Core.Topology;

namespace Cirq.Components.Passive;

/// <summary>Ideal linear resistor.</summary>
public partial class Resistor : TwoTerminalComponent, ICurrentReporting, IToleranced, INoiseSource, IPowerRated
{
    public Resistor(double resistance = 1e3)
    {
        Resistance = resistance;
    }

    /// <summary>Resistance in ohms. Must be strictly positive.</summary>
    [ObservableProperty]
    public partial double Resistance { get; set; }

    /// <summary>
    /// How far the real part may be from its marked resistance, as a fraction — 0.05 for a
    /// five percent part. Five percent is the common band for a carbon film part; one percent is a metal film, and the E96 series exists because somebody wanted the extra digit.
    /// <para>
    /// Nothing in an ordinary run uses this: the solver takes the value as given. It is what a
    /// Monte Carlo analysis varies, which is how you find out whether a circuit works with the
    /// parts you can actually buy rather than only with the ones in the drawing.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double Tolerance { get; set; } = 0.05;

    /// <summary>The value a tolerance applies to.</summary>
    public string TolerancedProperty => nameof(Resistance);

    /// <summary>Watts it can dissipate continuously in free air. A quarter watt is the commonest through-hole part, and the one somebody reaching for "a resistor" has in the drawer.</summary>
    [ObservableProperty]
    public partial double PowerRating { get; set; } = 0.25;

    public override string ComponentType => "Resistor";

    public override string DesignatorPrefix => "R";

    public override string ValueLabel => SiPrefix.Format(Resistance, "Ω");

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        // A zero-ohm resistor would be an infinite conductance; clamp to a realistic wire resistance.
        var r = Math.Max(Resistance, 1e-9);
        system.StampConductance(system.Node(A), system.Node(B), 1.0 / r);
    }

    /// <summary>Ohm's law, which is all a resistor's current has ever been.</summary>
    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        CurrentIntoPin(terminal, VoltageAcross(system, A, B) / Math.Max(Resistance, 1e-12));

    partial void OnResistanceChanged(double value) => NotifyValueChanged();

    /// <summary>
    /// Johnson noise: <c>4kT/R</c>, and nothing else.
    /// <para>
    /// Every resistance of the same value at the same temperature makes exactly this much. It is
    /// not a property of the make or the material and there is nothing to be done about it but
    /// use a smaller resistance, or a colder one. A 1 kΩ at room temperature is 4 nV/√Hz.
    /// </para>
    /// <para>
    /// Which is why a low-noise design is mostly an argument about resistor values, and why the
    /// ranking this feeds is usually more useful than the total: the answer is nearly always one
    /// resistor, and nearly always not the one people guess.
    /// </para>
    /// </summary>
    public IEnumerable<NoiseEmission> NoiseSources(MnaSystem system, SimulationState state, double hertz)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(state);

        var density = NoisePhysics.Thermal(Resistance, state.TemperatureKelvin);

        if (density <= 0) yield break;

        yield return new NoiseEmission(
            $"{Name} thermal", system.Node(A), system.Node(B), density);
    }
}
