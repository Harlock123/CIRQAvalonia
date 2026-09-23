namespace Cirq.Engine.Simulation;

/// <summary>
/// Thrown when the solver cannot find an answer.
/// <para>
/// It carries a <see cref="Report"/> rather than only a number, because the useful part of this
/// failure is <i>where</i> rather than <i>how far</i>: the node that was still moving, what is
/// attached to it, and which parts said they had not settled. All three name somewhere on the
/// drawing to go and look, which "try a smaller time step" does not.
/// </para>
/// </summary>
public sealed class ConvergenceException : Exception
{
    public ConvergenceException(ConvergenceReport report)
        : base(report?.Describe() ?? throw new ArgumentNullException(nameof(report)))
    {
        Report = report;
    }

    public ConvergenceReport Report { get; }

    public double Time => Report.Time;

    public int Iterations => Report.Iterations;

    public double Residual => Report.Residual;
}
