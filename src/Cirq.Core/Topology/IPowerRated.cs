namespace Cirq.Core.Topology;

/// <summary>
/// A part with a continuous dissipation it is rated for.
/// <para>
/// Declaring a rating is also declaring that what passes through the part becomes heat, and that
/// is the more useful half of this interface. A resistor turns every watt into heat; a capacitor
/// with the same volts and amps across it turns none of them into heat at all, because it is
/// storing and returning energy rather than spending it. Volts times amps is therefore a
/// dissipation for one of them and a meaningless number for the other, and nothing in the topology
/// can tell the two apart.
/// </para>
/// <para>
/// So the parts that dissipate say so. A semiconductor says it through
/// <see cref="Simulation.ISelfHeating"/>, which reports what its own model worked out; a passive
/// says it here, and its dissipation is the product the solver already has.
/// </para>
/// </summary>
public interface IPowerRated
{
    /// <summary>
    /// Watts it can dissipate continuously in free air at room temperature, or zero when nothing
    /// has been said about it.
    /// <para>
    /// Free air and room temperature because that is how the part is sold. A quarter-watt resistor
    /// in still air inside a closed box is not a quarter-watt resistor, and no number stored on a
    /// component can know which of those it is in — which is why this is the figure to compare
    /// against rather than the figure to trust.
    /// </para>
    /// </summary>
    double PowerRating { get; set; }
}


/// <summary>
/// A part with a voltage it must not be taken above.
/// <para>
/// A different kind of limit from a dissipation, and it has to be checked differently. Watts are
/// thermal: what matters is the average over long enough for the part to have warmed up, which is
/// why a MOSFET survives a pulse that would destroy it held on. Volts are not — a dielectric that
/// breaks down does so at the instant the peak arrives, and averaging is exactly the wrong thing to
/// do to it. So a rating like this is held against the <b>peak</b>.
/// </para>
/// </summary>
public interface IVoltageRated
{
    /// <summary>The most it may have across it, or zero when nothing has been said about it.</summary>
    double VoltageRating { get; set; }
}

/// <summary>
/// A part with a current it must not be taken above, held against the peak for the reason
/// <see cref="IVoltageRated"/> is.
/// <para>
/// The property is called <c>RatedCurrent</c> rather than <c>CurrentRating</c> because that is what
/// the parts which already had one call it. An interface that made three existing parts rename
/// their property would be an interface arranging the library around itself.
/// </para>
/// </summary>
public interface ICurrentRated
{
    double RatedCurrent { get; set; }
}
