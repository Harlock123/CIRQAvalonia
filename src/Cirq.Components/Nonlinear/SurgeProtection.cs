using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// Shared behaviour for the parts whose job is to survive something the rest of the circuit
/// cannot: they sit there doing nothing until the voltage across them goes somewhere it should
/// not, and then they conduct hard enough to hold it down.
/// <para>
/// Both are symmetric and both are rated in energy rather than steady current, because what they
/// are asked to do lasts microseconds. The energy is counted here so exceeding the rating is
/// something the part says rather than something that silently keeps working.
/// </para>
/// </summary>
public abstract partial class SurgeSuppressor : TwoTerminalComponent, ICurrentReporting
{
    /// <summary>Leakage each way, which also keeps the node from floating while it is blocking.</summary>
    protected const double LeakageConductance = 1e-9;

    /// <summary>Current through it at the last solved point, positive from A to B.</summary>
    public double Current { get; private set; }

    /// <summary>Voltage across it at the last solved point.</summary>
    public double Voltage { get; private set; }

    /// <summary>Energy it has absorbed since the last reset, in joules.</summary>
    public double AbsorbedJoules { get; private set; }

    /// <summary>Highest instantaneous power it has been asked to dissipate, in watts.</summary>
    public double PeakPower { get; private set; }

    /// <summary>Current through the device for a given voltage, and the slope of that.</summary>
    protected abstract (double Current, double Conductance) Conduct(double voltage);

    public override bool IsNonlinear => true;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var a = system.Node(A);
        var b = system.Node(B);

        var v = system.IterationVoltageAcross(a, b);
        var (current, conductance) = Conduct(v);

        system.StampNorton(a, b, conductance, current - (conductance * v));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        Voltage = system.NodeVoltage(A) - system.NodeVoltage(B);
        Current = Conduct(Voltage).Current;

        var power = Math.Abs(Voltage * Current);
        PeakPower = Math.Max(PeakPower, power);

        if (state.IsTransient) AbsorbedJoules += power * state.TimeStep;
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        CurrentIntoPin(terminal, Current);

    public override void ResetState()
    {
        Current = 0;
        Voltage = 0;
        AbsorbedJoules = 0;
        PeakPower = 0;
    }
}

/// <summary>
/// A bidirectional TVS diode — the part you put across a supply rail or a signal line to catch
/// whatever the outside world sends up it.
/// <para>
/// It is a zener bred for speed and current rather than for a precise voltage. Below its standoff
/// voltage it does nothing at all, which is the point: it has to be invisible to the circuit it is
/// protecting. Past that it turns on hard, and it is specified by what it clamps to at its rated
/// surge current rather than by where it starts conducting.
/// </para>
/// <para>
/// Choosing one with a standoff below the working voltage is the classic mistake — it then
/// conducts continuously, gets hot, and takes out the thing it was defending. The part says so.
/// </para>
/// </summary>
public partial class TransientSuppressor : SurgeSuppressor
{
    public TransientSuppressor(double standoff = 24.0)
    {
        StandoffVoltage = standoff;
    }

    /// <summary>Working voltage it must not conduct below, in volts.</summary>
    [ObservableProperty]
    public partial double StandoffVoltage { get; set; }

    /// <summary>Voltage it clamps to at the rated surge current, in volts.</summary>
    [ObservableProperty]
    public partial double ClampingVoltage { get; set; } = 38.9;

    /// <summary>Surge current the clamping voltage is quoted at, in amps.</summary>
    [ObservableProperty]
    public partial double RatedSurgeCurrent { get; set; } = 38.0;

    /// <summary>Peak pulse power it is rated to survive, in watts.</summary>
    [ObservableProperty]
    public partial double PeakPulsePower { get; set; } = 1500.0;

    public override string ComponentType => "TVS Diode";

    public override string DesignatorPrefix => "D";

    public override string ValueLabel => $"{SiPrefix.Format(StandoffVoltage, "V")} standoff";

    /// <summary>True while it is clamping rather than sitting out of the way.</summary>
    public bool IsClamping => Math.Abs(Current) > 1e-3;

    public IReadOnlyList<string> Violations
    {
        get
        {
            var faults = new List<string>();

            if (PeakPower > PeakPulsePower)
            {
                faults.Add($"asked for {SiPrefix.Format(PeakPower, "W")} against a " +
                           $"{SiPrefix.Format(PeakPulsePower, "W")} rating — a real one fails " +
                           "short, which is safe but permanent");
            }

            // Conducting steadily means the standoff voltage is below the working voltage. A TVS
            // is rated for microseconds, not for holding a rail down indefinitely.
            if (AbsorbedJoules > PeakPulsePower * 1e-3 && IsClamping)
            {
                faults.Add($"conducting continuously — {SiPrefix.Format(StandoffVoltage, "V")} " +
                           "standoff is below the working voltage, so it is clamping the circuit " +
                           "rather than protecting it");
            }

            return faults;
        }
    }

    /// <summary>
    /// A sharp symmetric knee. The exponent is fitted so that the rated surge current falls at the
    /// quoted clamping voltage, which is how the datasheet specifies the part — the two numbers
    /// people actually choose between.
    /// </summary>
    protected override (double Current, double Conductance) Conduct(double voltage)
    {
        var standoff = Math.Max(StandoffVoltage, 1e-3);
        var clamp = Math.Max(ClampingVoltage, standoff * 1.05);
        var rated = Math.Max(RatedSurgeCurrent, 1e-6);

        // Volts per e-fold of current between the two datasheet points.
        var slope = (clamp - standoff) / Math.Log(rated / 1e-6);

        var magnitude = Math.Abs(voltage);

        // Never linearised past the clamping point by more than a few slopes: an exponential
        // evaluated a hundred volts up asks for currents no solver can walk back from.
        var limited = Math.Min(magnitude, clamp + (6.0 * slope));

        var e = Math.Exp((limited - standoff) / slope);
        var current = 1e-6 * e;
        var conductance = current / slope;

        return ((Math.Sign(voltage) * current) + (voltage * LeakageConductance),
                conductance + LeakageConductance);
    }
}

/// <summary>
/// A metal-oxide varistor: the part across the mains input of almost everything.
/// <para>
/// Where a TVS is a silicon junction with a sharp knee, an MOV is a block of zinc oxide grains
/// that behaves like a very high-order power law — <c>I ∝ V^α</c> with α around thirty. That makes
/// it soft rather than sharp: it starts conducting well before its rated voltage and never quite
/// stops, which is why an MOV alone is not a regulator and why it belongs behind a fuse.
/// </para>
/// <para>
/// It also wears out. Every surge absorbed takes a little of it permanently, and a part at the end
/// of its life conducts at normal working voltage and cooks. That is modelled here: the absorbed
/// energy is counted against the rating, and it says when it has had enough.
/// </para>
/// </summary>
public partial class Varistor : SurgeSuppressor
{
    public Varistor(double varistorVoltage = 275.0)
    {
        VaristorVoltage = varistorVoltage;
    }

    /// <summary>Voltage at which it passes one milliamp — how an MOV is named, in volts.</summary>
    [ObservableProperty]
    public partial double VaristorVoltage { get; set; }

    /// <summary>The power-law exponent. Around thirty for a real part; higher is a sharper knee.</summary>
    [ObservableProperty]
    public partial double Nonlinearity { get; set; } = 30.0;

    /// <summary>Energy it can absorb over its life before it is worn out, in joules.</summary>
    [ObservableProperty]
    public partial double EnergyRating { get; set; } = 70.0;

    public override string ComponentType => "Varistor (MOV)";

    public override string DesignatorPrefix => "RV";

    public override string ValueLabel => IsWornOut
        ? "worn out"
        : $"{SiPrefix.Format(VaristorVoltage, "V")}";

    /// <summary>True once it has absorbed its rated energy.</summary>
    public bool IsWornOut => AbsorbedJoules >= EnergyRating;

    public IReadOnlyList<string> Violations => IsWornOut
        ? [$"worn out — it has absorbed {SiPrefix.Format(AbsorbedJoules, "J")} against a " +
           $"{SiPrefix.Format(EnergyRating, "J")} rating. A real one now conducts at working " +
           "voltage and overheats, which is why an MOV belongs behind a fuse"]
        : [];

    /// <summary>
    /// The power law, symmetric about zero. Written through logarithms because
    /// <c>V^30</c> overflows long before the voltages involved become unreasonable.
    /// </summary>
    protected override (double Current, double Conductance) Conduct(double voltage)
    {
        var reference = Math.Max(VaristorVoltage, 1e-3);
        var alpha = Math.Clamp(Nonlinearity, 2.0, 60.0);
        var magnitude = Math.Abs(voltage);

        if (magnitude < reference * 1e-3) return (voltage * LeakageConductance, LeakageConductance);

        // i = 1 mA * (v / Vn)^alpha, capped where the exponential would run away from Newton.
        var ratio = Math.Min(magnitude / reference, 4.0);
        var current = 1e-3 * Math.Pow(ratio, alpha);

        // di/dv = alpha * i / v, which is the slope of the power law.
        var conductance = alpha * current / Math.Max(magnitude, 1e-6);

        return ((Math.Sign(voltage) * current) + (voltage * LeakageConductance),
                conductance + LeakageConductance);
    }
}
