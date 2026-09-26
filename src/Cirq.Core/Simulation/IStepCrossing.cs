namespace Cirq.Core.Simulation;

/// <summary>
/// Implemented by parts that change state when something inside them crosses a threshold, so the
/// solver can land on the instant it happened instead of somewhere past it.
/// <para>
/// This is the discontinuity an error estimate cannot see. A switching regulator's oscillator is a
/// ramp between two thresholds: the ramp is a straight line, so the truncation error across a long
/// step is nil and a step controller has no reason to shorten it — and then the ramp is found to
/// be well past the threshold, the overshoot is time the oscillator did not really spend charging,
/// and the frequency comes out low. Nothing about the waveform says a step was too long. Only the
/// part knows, and only after the step has been solved.
/// </para>
/// <para>
/// So it is asked afterwards, and it may say the step overshot and by how much. The step is then
/// thrown away — nothing has been committed yet, which is what makes this cheap — and retaken to
/// land on the crossing. A part declaring a breakpoint through
/// <see cref="IBreakpointSource"/> is the same idea for a discontinuity it can see coming; this is
/// for the ones that depend on the solution and therefore cannot be known in advance.
/// </para>
/// </summary>
public interface IStepCrossing
{
    /// <summary>
    /// Where inside the step just solved this part crossed a threshold of its own, as a fraction of
    /// the step between 0 and 1 — or null when it crossed nothing, or crossed it near enough the end
    /// of the step that landing on it again would be landing on the same point.
    /// </summary>
    /// <param name="system">The matrix, solved but not yet committed.</param>
    /// <param name="state">Where the run is, including the length of the step being judged.</param>
    double? CrossingFraction(MnaSystem system, SimulationState state);
}
