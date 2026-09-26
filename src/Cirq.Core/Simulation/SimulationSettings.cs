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

    /// <summary>
    /// Let the solver choose its own step: shorter where the waveform bends, longer where it does
    /// not, and landing on the instant a part switches rather than somewhere past it.
    /// <para>
    /// Off by default, and deliberately. A fixed step is predictable, and predictability is worth a
    /// great deal in something whose answers people check by eye: every run of the same circuit
    /// takes the same steps and gives the same numbers. Turning this on trades that for accuracy
    /// where it is needed and speed where it is not — <see cref="TimeStep"/> becomes where the
    /// controller starts rather than what it uses, and <see cref="MaxTimeStep"/> the ceiling it may
    /// grow to.
    /// </para>
    /// </summary>
    public bool AdaptiveTimeStep { get; set; }

    /// <summary>
    /// How much local truncation error the adaptive controller will accept, relative to the size of
    /// the quantity being integrated. A thousandth is about a hundred and forty points across a
    /// sine; a hundredth is about forty-five.
    /// </summary>
    public double StepErrorTolerance { get; set; } = 1e-3;

    /// <summary>
    /// The most the adaptive controller will lengthen one step by, as a multiple of the last.
    /// <para>
    /// Kept modest on purpose. A controller that may double at will walks straight past a
    /// discontinuity it had no way to predict, discovers the trouble on the far side of it, and
    /// spends the steps it saved getting back — and a run whose step size oscillates is a run whose
    /// answers depend on where the oscillation happened to be.
    /// </para>
    /// </summary>
    public double MaxStepGrowth { get; set; } = 1.6;

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
