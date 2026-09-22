using Cirq.Core.Topology;

namespace Cirq.Core.Simulation;

/// <summary>
/// A place a feedback loop can be opened for a small-signal measurement without being opened for
/// anything else.
/// <para>
/// A part implementing this is a piece of wire in every other analysis — zero volts across it, at
/// DC and in a transient alike — and becomes an injection point only while a stability sweep is
/// running. That is the whole trick: the operating point is the one the working circuit has, so
/// nothing saturates and what is linearised is the real thing, while the loop is still open enough
/// for the gain round it to be measured.
/// </para>
/// </summary>
public interface ILoopBreak
{
    /// <summary>The driven side: where the loop's output arrives from.</summary>
    Terminal From { get; }

    /// <summary>The driving side: what that output goes on to feed.</summary>
    Terminal To { get; }

    /// <summary>
    /// What to inject across the break, in volts. Zero at every other time, which is what makes
    /// the part a short circuit rather than a component. A loop gain is a ratio, so the value
    /// itself never reaches the answer.
    /// </summary>
    double Injection { get; set; }
}
