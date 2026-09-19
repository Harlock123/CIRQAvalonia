namespace Cirq.Core.Simulation;

public enum AnalysisMode
{
    /// <summary>Bias point solve: capacitors open, inductors shorted, sources at their t=0 value.</summary>
    DcOperatingPoint,
    /// <summary>Time-domain solve using companion models for reactive elements.</summary>
    Transient,
}

public enum IntegrationMethod
{
    /// <summary>First-order, unconditionally stable, numerically damped.</summary>
    BackwardEuler,
    /// <summary>Second-order and more accurate, but can ring on stiff circuits.</summary>
    Trapezoidal,
}
