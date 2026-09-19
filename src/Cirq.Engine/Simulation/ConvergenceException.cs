namespace Cirq.Engine.Simulation;

public sealed class ConvergenceException : Exception
{
    public ConvergenceException(double time, int iterations, double residual)
        : base($"Newton-Raphson failed to converge at t={time:g6}s after {iterations} iterations " +
               $"(largest update {residual:g3}). Try a smaller time step or check for a circuit with no DC path to ground.")
    {
        Time = time;
        Iterations = iterations;
        Residual = residual;
    }

    public double Time { get; }

    public int Iterations { get; }

    public double Residual { get; }
}
