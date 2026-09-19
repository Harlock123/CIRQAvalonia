namespace Cirq.Core.Simulation;

/// <summary>Tunable numerical parameters for the mixed-signal engine.</summary>
public sealed class SimulationSettings
{
    /// <summary>Nominal transient time step in seconds.</summary>
    public double TimeStep { get; set; } = 1e-6;

    /// <summary>Upper bound used when the adaptive controller grows the step.</summary>
    public double MaxTimeStep { get; set; } = 1e-4;

    /// <summary>Lower bound before the solver gives up on a non-converging step.</summary>
    public double MinTimeStep { get; set; } = 1e-12;

    public IntegrationMethod Integration { get; set; } = IntegrationMethod.Trapezoidal;

    /// <summary>Maximum Newton-Raphson iterations per time point.</summary>
    public int MaxNewtonIterations { get; set; } = 100;

    /// <summary>Absolute voltage tolerance for Newton convergence (volts).</summary>
    public double VoltageTolerance { get; set; } = 1e-6;

    /// <summary>Absolute current tolerance for Newton convergence (amps).</summary>
    public double CurrentTolerance { get; set; } = 1e-9;

    /// <summary>Relative tolerance applied on top of the absolute tolerances.</summary>
    public double RelativeTolerance { get; set; } = 1e-3;

    /// <summary>Conductance added from every node to ground to keep the matrix non-singular.</summary>
    public double Gmin { get; set; } = 1e-12;

    /// <summary>Ambient temperature in Kelvin (27 degC by default).</summary>
    public double TemperatureKelvin { get; set; } = 300.15;

    /// <summary>Skip the bias-point solve and start the transient from zero initial conditions.</summary>
    public bool UseInitialConditions { get; set; }

    /// <summary>Halve the step and retry when Newton fails, instead of throwing immediately.</summary>
    public bool EnableStepRejection { get; set; } = true;

    /// <summary>
    /// Minimum simulated time between probe samples, in seconds. Zero records every accepted time
    /// point; a scope sets this from its timebase so a fast circuit does not fill the trace
    /// buffers in a few microseconds of simulated time.
    /// </summary>
    public double ProbeSampleInterval { get; set; }

    /// <summary>Maximum zero-delay delta cycles evaluated at a single time point.</summary>
    public int MaxDeltaCycles { get; set; } = 100;

    public SimulationSettings Clone() => (SimulationSettings)MemberwiseClone();
}
