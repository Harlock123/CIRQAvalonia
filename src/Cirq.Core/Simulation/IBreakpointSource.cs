namespace Cirq.Core.Simulation;

/// <summary>
/// Implemented by components with scheduled discontinuities (square-wave edges, pulse sources).
/// The transient loop truncates its step so a time point lands on each breakpoint, which keeps a
/// fast edge from being smeared across a step.
/// </summary>
public interface IBreakpointSource
{
    /// <summary>The next discontinuity strictly after <paramref name="time"/>, or null if there is none.</summary>
    double? NextBreakpointAfter(double time);
}
