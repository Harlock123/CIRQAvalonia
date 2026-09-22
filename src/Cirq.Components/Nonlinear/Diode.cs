using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;
using Cirq.Core.Probing;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// Shockley diode, <c>Id = Is·(exp(Vd / (n·Vt)) − 1)</c>, solved by Newton-Raphson.
/// <para>
/// Each iteration linearises the exponential into a Norton companion <c>(Gd, Ieq)</c>. The junction
/// voltage is passed through SPICE's <c>pnjlim</c> limiter first, which is what stops the
/// exponential from overflowing when an early iterate overshoots.
/// </para>
/// </summary>
public partial class Diode : TwoTerminalComponent, ICurrentReporting, INoiseSource
{
    /// <summary>Largest exponent evaluated before the model falls back to a linear extrapolation.</summary>
    private const double MaxExponent = 80.0;

    private double _junctionVoltage;
    private double _previousJunctionVoltage;
    private double _current;
    private bool _limitedThisIteration;

    public Diode(DiodeModel? model = null) : base("A", "K")
    {
        Model = model ?? DiodeModel.D1N4148;
    }

    /// <summary>Anode.</summary>
    public Terminal Anode => A;

    /// <summary>Cathode.</summary>
    public Terminal Cathode => B;

    [ObservableProperty]
    public partial DiodeModel Model { get; set; }

    /// <summary>Models reverse breakdown when true; a plain rectifier can leave it off.</summary>
    [ObservableProperty]
    public partial bool EnableBreakdown { get; set; } = true;

    public override string ComponentType => "Diode";

    public override string DesignatorPrefix => "D";

    public override string ValueLabel => Model.Name;

    public override bool IsNonlinear => true;

    /// <summary>
    /// The ohmic contact node between the junction and the cathode. Modelling the bulk resistance
    /// with a real node (rather than folding it into the companion) is what keeps the exponential
    /// linearised about the true junction voltage instead of the terminal voltage.
    /// </summary>
    public override int InternalNodeCount => 1;

    /// <summary>Junction voltage at the converged solution.</summary>
    public double JunctionVoltage => _junctionVoltage;

    /// <summary>Forward current at the converged solution, in amps.</summary>
    public double Current => _current;

    /// <summary>Positive into the anode, which is the direction the device conducts.</summary>
    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        CurrentIntoPin(terminal, Current);

    /// <summary>True when the diode is carrying meaningful forward current.</summary>
    public bool IsConducting => _current > 1e-6;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var anode = system.Node(A);
        var cathode = system.Node(B);
        var bulk = system.InternalNode(this);

        var vt = state.ThermalVoltage * Model.EmissionCoefficient;
        var raw = system.IterationVoltageAcross(anode, bulk);
        var vd = LimitJunctionVoltage(raw, _previousJunctionVoltage, vt, CriticalVoltage(vt));

        _limitedThisIteration = Math.Abs(vd - raw) > 1e-12;
        _previousJunctionVoltage = vd;

        var (current, conductance) = Evaluate(vd, vt, Model.SaturationCurrentAt(state.TemperatureKelvin));

        // Companion for the junction: i = Gd·v + Ieq, linearised about vd.
        system.StampNorton(anode, bulk, conductance, current - conductance * vd);

        // Bulk resistance from the junction to the cathode. A floor keeps an ideal diode from
        // producing an infinite conductance on this branch.
        system.StampConductance(bulk, cathode, 1.0 / Math.Max(Model.SeriesResistance, 1e-4));
    }

    /// <summary>Evaluates the model, returning the junction current and its small-signal conductance.</summary>
    private (double Current, double Conductance) Evaluate(double vd, double vt, double saturation)
    {
        var gmin = 1e-12;

        if (vd >= 0 || !EnableBreakdown || vd > -Model.BreakdownVoltage)
        {
            // Forward and normal reverse operation.
            var x = vd / vt;
            if (x > MaxExponent)
            {
                // Beyond this the exponential overflows; extrapolate along the tangent instead.
                var e = Math.Exp(MaxExponent);
                var g = saturation * e / vt;
                var i = saturation * (e - 1.0) + g * (vd - MaxExponent * vt);
                return (i + gmin * vd, g + gmin);
            }

            var exp = Math.Exp(x);
            return (saturation * (exp - 1.0) + gmin * vd,
                    saturation * exp / vt + gmin);
        }

        // Reverse breakdown: the current grows exponentially again past -Bv.
        var over = -(vd + Model.BreakdownVoltage) / vt;
        if (over > MaxExponent) over = MaxExponent;
        var expB = Math.Exp(over);
        return (-Model.BreakdownCurrent * expB - saturation + gmin * vd,
                Model.BreakdownCurrent * expB / vt + gmin);
    }

    /// <summary>
    /// The junction voltage above which the exponential grows faster than Newton can follow.
    /// </summary>
    private double CriticalVoltage(double vt) =>
        vt * Math.Log(vt / (Math.Sqrt(2.0) * Math.Max(Model.SaturationCurrent, 1e-30)));

    /// <summary>
    /// SPICE's <c>pnjlim</c>: damps a Newton step that would push the junction far past its
    /// critical voltage, converting a runaway exponential step into a logarithmic one.
    /// </summary>
    internal static double LimitJunctionVoltage(double vNew, double vOld, double vt, double vCritical)
    {
        if (double.IsNaN(vNew)) return vOld;

        if (vNew > vCritical && Math.Abs(vNew - vOld) > 2.0 * vt)
        {
            if (vOld > 0)
            {
                var arg = 1.0 + (vNew - vOld) / vt;
                vNew = arg > 0 ? vOld + vt * Math.Log(arg) : vCritical;
            }
            else
            {
                vNew = vNew > 0 ? vt * Math.Log(Math.Max(vNew / vt, 1e-12)) : vCritical;
            }
        }
        else if (vNew < 0)
        {
            // Clamp deep reverse bias so the model stays numerically well behaved.
            var limit = Math.Min(vOld, 0) - 10.0 * vt - 1.0;
            if (vNew < limit) vNew = limit;
        }

        return vNew;
    }

    public override bool HasConverged(MnaSystem system, SimulationState state)
    {
        // A limited iteration means the solver has not actually reached the junction voltage the
        // matrix asked for, so the point cannot be declared converged yet.
        return !_limitedThisIteration;
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var junction = system.NodeVoltage(system.Node(A)) - system.NodeVoltage(system.InternalNode(this));
        var vt = state.ThermalVoltage * Model.EmissionCoefficient;

        _junctionVoltage = junction;
        (_current, _) = Evaluate(junction, vt, Model.SaturationCurrentAt(state.TemperatureKelvin));
    }

    public override void ResetState()
    {
        _junctionVoltage = 0;
        _previousJunctionVoltage = 0;
        _current = 0;
        _limitedThisIteration = false;
    }

    partial void OnModelChanged(DiodeModel value) => NotifyValueChanged();

    /// <summary>
    /// Shot noise across the junction, and Johnson noise in the bulk resistance.
    /// <para>
    /// Shot noise is <c>2qI</c> and is a different beast from thermal noise: it does not depend on
    /// temperature, and it is not there at all when no current flows. It exists because charge
    /// arrives one electron at a time and the arrivals are independent — a steady current is
    /// steady only on average.
    /// </para>
    /// <para>
    /// The series resistance is an ordinary resistor and makes ordinary thermal noise, between the
    /// anode and the internal node the stamp already carries for it.
    /// </para>
    /// </summary>
    public IEnumerable<NoiseEmission> NoiseSources(MnaSystem system, SimulationState state, double hertz)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(state);

        // The junction is between the anode and the internal node, and the bulk resistance
        // between that node and the cathode — the order the stamp puts them in. Getting the two
        // the wrong way round puts each generator across the other one's impedance, which on a
        // conducting diode is a factor of eighty.
        var bulk = system.InternalNode(this);

        var shot = NoisePhysics.Shot(Current);
        if (shot > 0) yield return new NoiseEmission($"{Name} shot", system.Node(A), bulk, shot);

        var thermal = NoisePhysics.Thermal(Model.SeriesResistance, state.TemperatureKelvin);
        if (thermal > 0) yield return new NoiseEmission($"{Name} bulk", bulk, system.Node(B), thermal);
    }
}
