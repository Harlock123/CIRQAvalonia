namespace Cirq.Core.Simulation;

/// <summary>
/// A part that heats itself up, and behaves differently because it has.
/// <para>
/// Every temperature model in this library takes the circuit's ambient, which is the right answer
/// for a part that dissipates nothing. It is the wrong answer for one that dissipates watts: the
/// die of a MOSFET holding two amps is nowhere near the air around it, and its on-resistance is
/// the parameter that decided how many watts those were. The loop closes, and it is the loop
/// rather than either half of it that decides whether a design works.
/// </para>
/// </summary>
public interface ISelfHeating
{
    /// <summary>
    /// Junction-to-ambient thermal resistance in degrees per watt. Zero means the rise is not
    /// modelled, which is the default: a part is a part until somebody says what it is mounted on.
    /// </summary>
    double ThermalResistance { get; }

    /// <summary>How long the die takes to follow a change in dissipation, in seconds.</summary>
    double ThermalTimeConstant { get; }

    /// <summary>Watts the part was dissipating at the last solved point.</summary>
    double PowerDissipation { get; }

    /// <summary>Where the die actually is, in degrees Celsius.</summary>
    double JunctionTemperature { get; }

    /// <summary>The air around it, in degrees Celsius.</summary>
    double AmbientTemperature { get; }

    /// <summary>The highest the die is rated to reach, in degrees Celsius.</summary>
    double MaximumJunctionTemperature { get; }

    /// <summary>True when it got there, which is a part being destroyed rather than a warning.</summary>
    bool IsOverTemperature { get; }

    /// <summary>True when the rise is being modelled at all.</summary>
    bool IsSelfHeating => ThermalResistance > 0;

    /// <summary>How far above ambient the die is sitting.</summary>
    double TemperatureRise => JunctionTemperature - AmbientTemperature;
}

/// <summary>
/// The die temperature of one part, tracked across a solve.
/// <para>
/// Two regimes, because there are two questions. In a transient the die follows its dissipation
/// with a time constant, which is why a MOSFET survives a short pulse that would destroy it held
/// on — thermal mass is a real component of a design and pretending it is instant loses that.
/// At an operating point there is no time, so the die is wherever the power it is dissipating
/// <i>there</i> puts it, and that has to be solved for rather than evaluated: the power depends
/// on the temperature through the device's own parameters.
/// </para>
/// <para>
/// The operating-point case is walked towards rather than jumped to. The feedback is positive in
/// every device worth modelling this way — hotter means more resistance means more watts means
/// hotter — so a Newton step that took the full distance would overshoot into a temperature at
/// which the device is not on, come back, and sit there swapping between the two. Stepping it
/// bounds how far the linearisation can be wrong, and an iteration that had to be held back is
/// not a converged one.
/// </para>
/// </summary>
public sealed class SelfHeating
{
    /// <summary>The most one outer step may move the die, in degrees.</summary>
    private const double MaximumStep = 40.0;

    /// <summary>
    /// How far towards the target one outer step goes. Under one because the feedback is positive
    /// in every device worth modelling this way — hotter means more dissipation means hotter — so
    /// a full step can overshoot past the fixed point and come back, once per iteration, forever.
    /// </summary>
    private const double Relaxation = 0.7;

    /// <summary>How close is close enough, in degrees, before a solve may be called finished.</summary>
    private const double Settled = 0.05;

    private bool _held;

    /// <summary>Where the die is, in degrees Celsius.</summary>
    public double Celsius { get; private set; } = double.NaN;

    /// <summary>What it is dissipating, in watts.</summary>
    public double Watts { get; private set; }

    /// <summary>The air around it, in degrees Celsius.</summary>
    public double Ambient { get; private set; }

    /// <summary>Kelvin, which is what every model parameter is quoted against.</summary>
    public double Kelvin => Celsius + 273.15;

    /// <summary>True once the die has stopped moving between outer steps.</summary>
    public bool HasConverged => !_held;

    /// <summary>
    /// True when the die reached the maximum the part is rated for.
    /// <para>
    /// Which is also how a runaway is caught. The feedback is positive, and in a badly heatsinked
    /// part its loop gain can exceed one: every degree adds more dissipation than it takes to
    /// produce, so there is no temperature the device settles at and the solve has nothing to
    /// converge to. Stopping at the rated maximum turns "Newton-Raphson failed after a hundred
    /// iterations" into the thing that is actually happening, which is that the part cooks.
    /// </para>
    /// </summary>
    public bool IsOverTemperature { get; private set; }

    /// <summary>
    /// One step of the outer loop at an operating point, where there is no time for the die to
    /// lag and it simply sits wherever its own dissipation puts it.
    /// <para>
    /// Called only from a device's convergence check, which the solver reaches only once the
    /// electrical iterate is already within tolerance. That ordering is the whole of why this is
    /// stable: the power it is handed came from a circuit that had settled, rather than from some
    /// half-way iterate where a MOSFET is momentarily dropping the whole rail and appears to be
    /// dissipating forty watts.
    /// </para>
    /// </summary>
    /// <returns>False while the die is still moving, so the solve keeps going.</returns>
    public bool Relax(
        double watts, SimulationState state, double thermalResistance, double maximumCelsius)
    {
        ArgumentNullException.ThrowIfNull(state);

        Ambient = state.TemperatureKelvin - 273.15;

        if (!Tracked(watts, thermalResistance))
        {
            _held = false;
            IsOverTemperature = false;

            return true;
        }

        // Under initial conditions the operating point is the instant the supply arrives, not a
        // circuit that has been running for ever: the die is still at ambient however much the
        // inrush is momentarily dissipating. The transient that follows warms it up.
        if (state.Settings.UseInitialConditions)
        {
            Celsius = Ambient;
            _held = false;

            return true;
        }

        var steady = Ambient + (Watts * thermalResistance);

        if (double.IsNaN(Celsius)) Celsius = Ambient;

        var distance = steady - Celsius;

        if (Math.Abs(distance) <= Settled)
        {
            Celsius = steady;
            _held = false;

            return true;
        }

        var step = Math.Clamp(distance * Relaxation, -MaximumStep, MaximumStep);

        Celsius += step;

        // A part that has reached its rated maximum has finished moving as far as this solve is
        // concerned: beyond it the model is describing a component that no longer exists.
        if (Celsius >= maximumCelsius)
        {
            Celsius = maximumCelsius;
            IsOverTemperature = true;
            _held = false;

            return true;
        }

        _held = true;

        return false;
    }

    /// <summary>
    /// One accepted time step of a transient, where the die follows its dissipation rather than
    /// jumping to it. Called once per step rather than once per iteration, because a thermal time
    /// constant is enormously longer than an electrical one — the die is a constant for the whole
    /// of a step, and treating it as one is not an approximation worth apologising for.
    /// </summary>
    public void Advance(
        double watts, SimulationState state, double thermalResistance, double timeConstant,
        double maximumCelsius)
    {
        ArgumentNullException.ThrowIfNull(state);

        Ambient = state.TemperatureKelvin - 273.15;

        if (!Tracked(watts, thermalResistance)) return;

        var steady = Ambient + (Watts * thermalResistance);

        // At t = 0 the supply has only just arrived: the die is still at ambient whatever the
        // inrush is momentarily doing. Without this a run starts hot.
        if (double.IsNaN(Celsius))
        {
            Celsius = Ambient;
            return;
        }

        if (state.TimeStep <= 0 || timeConstant <= 0)
        {
            Celsius = steady;
            return;
        }

        Celsius += (steady - Celsius) * (1.0 - Math.Exp(-state.TimeStep / timeConstant));

        if (Celsius < maximumCelsius) return;

        Celsius = maximumCelsius;
        IsOverTemperature = true;
    }

    /// <summary>
    /// Records the dissipation and says whether there is a rise to track at all. A part with no
    /// thermal resistance given sits at ambient, which is what it did before any of this existed.
    /// </summary>
    private bool Tracked(double watts, double thermalResistance)
    {
        Watts = double.IsNaN(watts) || double.IsInfinity(watts) ? 0 : Math.Max(watts, 0);

        if (thermalResistance > 0) return true;

        Celsius = Ambient;

        return false;
    }

    /// <summary>Back to cold and unknown, so the next solve starts from ambient.</summary>
    public void Reset()
    {
        Celsius = double.NaN;
        Watts = 0;
        Ambient = 0;
        _held = false;
        IsOverTemperature = false;
    }
}
