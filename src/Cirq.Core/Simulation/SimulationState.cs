using Cirq.Core.Digital;

namespace Cirq.Core.Simulation;

/// <summary>
/// Per-solve context handed to every component stamper: where we are in time, which integration
/// rule applies, and the digital scheduler used by hybrid devices.
/// </summary>
public sealed class SimulationState
{
    public SimulationState(SimulationSettings settings)
    {
        Settings = settings;
    }

    public SimulationSettings Settings { get; }

    public AnalysisMode Mode { get; internal set; } = AnalysisMode.DcOperatingPoint;

    /// <summary>Simulation time at the point currently being solved, in seconds.</summary>
    public double Time { get; internal set; }

    /// <summary>Step being taken to reach <see cref="Time"/>, in seconds.</summary>
    public double TimeStep { get; internal set; }

    /// <summary>Step used for the previously accepted point (equal to <see cref="TimeStep"/> in steady state).</summary>
    public double PreviousTimeStep { get; internal set; }

    public long StepNumber { get; internal set; }

    public int NewtonIteration { get; internal set; }

    /// <summary>True while the first transient point after a bias solve is being taken.</summary>
    public bool IsFirstTransientStep { get; internal set; } = true;

    /// <summary>The digital scheduler, present once the circuit has been compiled.</summary>
    public IDigitalContext? Digital { get; internal set; }

    /// <summary>
    /// Integration rule for this step. The first transient point always uses Backward Euler to
    /// damp the step discontinuity, which is standard practice in SPICE engines.
    /// </summary>
    public IntegrationMethod EffectiveIntegration =>
        Mode == AnalysisMode.DcOperatingPoint || IsFirstTransientStep
            ? IntegrationMethod.BackwardEuler
            : Settings.Integration;

    /// <summary>Thermal voltage kT/q at the configured temperature.</summary>
    public double ThermalVoltage => PhysicalConstants.Boltzmann * Settings.TemperatureKelvin / PhysicalConstants.ElementaryCharge;

    public bool IsTransient => Mode == AnalysisMode.Transient;

    internal void Reset()
    {
        Time = 0;
        TimeStep = 0;
        PreviousTimeStep = 0;
        StepNumber = 0;
        NewtonIteration = 0;
        IsFirstTransientStep = true;
        Mode = AnalysisMode.DcOperatingPoint;
    }
}

public static class PhysicalConstants
{
    public const double Boltzmann = 1.380649e-23;
    public const double ElementaryCharge = 1.602176634e-19;
}
