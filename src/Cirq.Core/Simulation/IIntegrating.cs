namespace Cirq.Core.Simulation;

/// <summary>
/// Implemented by parts that integrate something across a time step — a capacitor its charge, an
/// inductor its flux — so the solver can judge how much of the answer the step size is costing.
/// <para>
/// This is the whole of what an adaptive step controller can honestly ask about. The error in a
/// numerical integration shows up in the quantity being integrated and nowhere else: a logic node
/// steps from zero to five volts between two time points <i>by design</i>, and a controller that
/// watched every unknown in the matrix would read that as a catastrophic error and grind the step
/// down to nothing. So only the parts that integrate are asked, and they are asked about the state
/// variable they integrate rather than about the node voltages around them.
/// </para>
/// <para>
/// The estimate is the predictor-corrector difference: extrapolate linearly from the last two
/// accepted points, then compare that with what the step actually solved to. For trapezoidal
/// integration the gap between the two is proportional to the step cubed times the third
/// derivative, which is the local truncation error — so it is small when the waveform is straight,
/// large where it bends, and free, because both numbers are already to hand.
/// </para>
/// </summary>
public interface IIntegrating
{
    /// <summary>
    /// How wrong the step just solved looks, as a multiple of what would be negligible: below 1 the
    /// step is fine, above 1 it is too long. Null when there is no estimate yet — nothing integrates
    /// meaningfully on the first step after a bias point, because there is no history to
    /// extrapolate from.
    /// </summary>
    /// <param name="system">The matrix, solved but not yet committed.</param>
    /// <param name="state">Where the run is, including the step being judged and the one before it.</param>
    double? IntegrationError(MnaSystem system, SimulationState state);
}
