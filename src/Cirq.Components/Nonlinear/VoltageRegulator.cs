using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>Datasheet parameters for a three-terminal linear regulator.</summary>
public sealed record RegulatorModel(
    string Name,
    double ReferenceVoltage,
    double DropoutVoltage,
    double CurrentLimit,
    double QuiescentCurrent,
    double OutputResistance,
    double ThermalResistance,
    bool IsAdjustable,
    double AdjustPinCurrent = 0)
{
    public static readonly RegulatorModel Lm7805 = new("LM7805", 5.0, 2.0, 1.5, 5e-3, 0.02, 50, false);
    public static readonly RegulatorModel Lm7809 = new("LM7809", 9.0, 2.0, 1.5, 5e-3, 0.02, 50, false);
    public static readonly RegulatorModel Lm7812 = new("LM7812", 12.0, 2.0, 1.5, 5e-3, 0.02, 50, false);
    public static readonly RegulatorModel Lm7905 = new("LM7905", -5.0, 2.0, 1.5, 5e-3, 0.02, 50, false);

    /// <summary>Adjustable regulator: holds 1.25 V between OUT and ADJ.</summary>
    public static readonly RegulatorModel Lm317 = new("LM317", 1.25, 2.0, 1.5, 5e-3, 0.02, 50, true, 50e-6);

    /// <summary>Low-dropout 3.3 V regulator.</summary>
    public static readonly RegulatorModel Ld1117 = new("LD1117-3.3", 3.3, 1.1, 0.8, 5e-3, 0.02, 60, false);

    public static readonly IReadOnlyList<RegulatorModel> Library =
        [Lm7805, Lm7809, Lm7812, Lm7905, Lm317, Ld1117];

    public override string ToString() => Name;
}

/// <summary>
/// Three-terminal linear voltage regulator (78xx / 79xx / LM317 family).
/// <para>
/// The output is a stiff source referenced to the COMMON (or ADJ) pin, but it can only hold its
/// setpoint while the input stays at least the dropout voltage above it. Below that the pass
/// element saturates and the output simply follows the input down, which is modelled with a smooth
/// minimum so Newton-Raphson sees a differentiable corner rather than a hard kink.
/// </para>
/// <para>
/// Load current is drawn from the IN pin rather than from the reference node, so the input supply
/// sees the real load, and both current limiting and thermal shutdown fold the output back.
/// </para>
/// </summary>
public partial class VoltageRegulator : CircuitComponent
{
    /// <summary>Width of the smoothed corner between regulation and dropout, in volts.</summary>
    private const double CornerSharpness = 0.05;

    /// <summary>
    /// Slope of the current limiter once it engages, in ohms. Steep enough to hold the output
    /// current at the limit, shallow enough that Newton can still walk down it.
    /// </summary>
    private const double LimiterResistance = 1e4;

    private double _limiterDrop;

    public VoltageRegulator(RegulatorModel? model = null)
    {
        Model = model ?? RegulatorModel.Lm7805;

        Input = new Terminal("in", "IN", TerminalType.Input, new Point(-40, 0));
        Output = new Terminal("out", "OUT", TerminalType.Output, new Point(40, 0));
        Common = new Terminal("com", Model.IsAdjustable ? "ADJ" : "GND", TerminalType.Passive, new Point(0, 30));

        Terminals = [Input, Output, Common];
    }

    public Terminal Input { get; }
    public Terminal Output { get; }

    /// <summary>The GND pin on a fixed regulator, or the ADJ pin on an adjustable one.</summary>
    public Terminal Common { get; }

    [ObservableProperty]
    public partial RegulatorModel Model { get; set; }

    /// <summary>Ambient temperature in degrees Celsius, used for the thermal model.</summary>
    [ObservableProperty]
    public partial double AmbientTemperature { get; set; } = 25.0;

    /// <summary>Junction temperature at which the regulator shuts down, in degrees Celsius.</summary>
    [ObservableProperty]
    public partial double ThermalShutdownTemperature { get; set; } = 150.0;

    public override string ComponentType => "Voltage Regulator";

    public override string DesignatorPrefix => "VR";

    public override string ValueLabel => Model.IsAdjustable
        ? Model.Name
        : $"{Model.Name} ({SiPrefix.Format(Model.ReferenceVoltage, "V")})";

    public override bool IsNonlinear => true;

    public override int VoltageSourceCount => 1;

    /// <summary>Load current delivered at the last accepted point, in amps.</summary>
    public double OutputCurrent { get; private set; }

    /// <summary>
    /// Total power dissipated at the last accepted point, in watts: the drop across the pass
    /// element times the load current, plus the quiescent current times the input voltage.
    /// </summary>
    public double PowerDissipation { get; private set; }

    /// <summary>Estimated junction temperature in degrees Celsius.</summary>
    public double JunctionTemperature { get; private set; }

    /// <summary>True while the input is too close to the output for the regulator to regulate.</summary>
    public bool IsInDropout { get; private set; }

    /// <summary>True while the output is being folded back by the current limit.</summary>
    public bool IsCurrentLimited { get; private set; }

    /// <summary>True once the junction has exceeded the thermal shutdown temperature.</summary>
    public bool IsThermallyShutDown { get; private set; }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var inNode = system.Node(Input);
        var outNode = system.Node(Output);
        var common = system.Node(Common);
        var branch = system.Branch(this);

        var headroom = system.IterationVoltage(inNode) - system.IterationVoltage(common);
        var (target, slope) = RegulatedVoltage(headroom);

        if (IsThermallyShutDown)
        {
            target = 0;
            slope = 0;
        }

        // Current limiting is part of the device equation rather than a step-to-step correction,
        // so Newton resolves it within the time point. The limiter is a series drop that stays
        // near zero below the limit and then climbs steeply, which keeps dv/di negative
        // throughout: a fold-back characteristic would introduce negative resistance and with it
        // multiple operating points.
        var branchCurrent = system.IterationValue(branch);
        var (limiterDrop, limiterSlope) = CurrentLimiterDrop(branchCurrent);

        // v_out - v_common - Rout·i - limiter(i) - slope·(v_in - v_common) = target
        system.Add(outNode, branch, 1.0);
        system.Add(common, branch, -1.0);
        system.Add(branch, outNode, 1.0);
        system.Add(branch, common, -1.0);
        system.Add(branch, branch, -(Model.OutputResistance + limiterSlope));
        system.Add(branch, inNode, -slope);
        system.Add(branch, common, slope);
        system.AddRhs(branch, target - slope * headroom + limiterDrop - limiterSlope * branchCurrent);

        // Load current is drawn from IN, not from the reference node: cancel the source's return
        // path at COMMON and redirect it to the input pin.
        system.Add(inNode, branch, -1.0);
        system.Add(common, branch, 1.0);

        if (Model.IsAdjustable)
        {
            // A floating regulator returns its quiescent current through OUT, not through ADJ.
            // The ADJ pin carries only its own small bias current, which is why the divider
            // resistors can be chosen almost freely.
            system.StampCurrentSource(inNode, outNode, Model.QuiescentCurrent);
            if (Model.AdjustPinCurrent > 0)
                system.StampCurrentSource(common, outNode, Model.AdjustPinCurrent);
        }
        else
        {
            // A fixed regulator's quiescent current leaves through the GND pin.
            system.StampCurrentSource(inNode, common, Model.QuiescentCurrent);
        }
    }

    /// <summary>
    /// The voltage the regulator will hold, and its sensitivity to the input voltage. In
    /// regulation the slope is zero; in dropout it approaches one as the pass element saturates.
    /// </summary>
    private (double Voltage, double Slope) RegulatedVoltage(double headroom)
    {
        var nominal = Model.ReferenceVoltage;
        var passThrough = headroom - Math.Sign(nominal) * Model.DropoutVoltage;

        if (nominal < 0)
        {
            // Negative regulators clamp from the other side: mirror, solve, mirror back.
            var (v, s) = SmoothMinimum(-nominal, -passThrough);
            return (-v, s);
        }

        return SmoothMinimum(nominal, passThrough);
    }

    /// <summary>
    /// Series voltage drop imposed by the current limiter, and its derivative with respect to the
    /// branch current. Below the limit the drop is negligible; above it the drop rises at
    /// <see cref="LimiterResistance"/> ohms per amp, pinning the current at the limit.
    /// </summary>
    private (double Drop, double Slope) CurrentLimiterDrop(double current)
    {
        var magnitude = Math.Abs(current);
        var width = Math.Max(Model.CurrentLimit * 0.02, 1e-4);
        var x = (magnitude - Model.CurrentLimit) / width;

        // Softplus, evaluated in the numerically stable form for large arguments.
        var softplus = x > 30 ? x : Math.Log(1.0 + Math.Exp(x));
        var sigmoid = 1.0 / (1.0 + Math.Exp(-Math.Clamp(x, -60, 60)));

        var drop = LimiterResistance * width * softplus * Math.Sign(current);
        var slope = LimiterResistance * sigmoid;
        return (drop, slope);
    }

    /// <summary>
    /// Differentiable <c>min(a, b)</c> where only <paramref name="b"/> varies, returning the value
    /// and d/db. The hyperbola rounds the corner over <see cref="CornerSharpness"/> volts.
    /// </summary>
    private static (double Value, double Slope) SmoothMinimum(double a, double b)
    {
        var diff = a - b;
        var root = Math.Sqrt(diff * diff + CornerSharpness * CornerSharpness);
        var value = 0.5 * (a + b) - 0.5 * root;
        var slope = 0.5 + 0.5 * diff / root;
        return (value, slope);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        // The branch current is defined flowing from OUT into COMMON inside the source, so a
        // regulator sourcing current into a load shows a negative branch current.
        OutputCurrent = -system.BranchCurrent(this);

        var vin = system.NodeVoltage(Input);
        var vout = system.NodeVoltage(Output);
        var common = system.NodeVoltage(Common);

        PowerDissipation = Math.Abs(vin - vout) * Math.Abs(OutputCurrent)
                           + Math.Abs(vin - common) * Model.QuiescentCurrent;
        JunctionTemperature = AmbientTemperature + PowerDissipation * Model.ThermalResistance;
        IsThermallyShutDown = JunctionTemperature > ThermalShutdownTemperature;

        var headroom = vin - common;
        var (unlimited, _) = RegulatedVoltage(headroom);
        IsInDropout = Math.Abs(unlimited) < Math.Abs(Model.ReferenceVoltage) - 0.1;

        (_limiterDrop, _) = CurrentLimiterDrop(system.BranchCurrent(this));
        // The limiter counts as engaged once it is dropping a meaningful share of a volt.
        IsCurrentLimited = Math.Abs(_limiterDrop) > 0.05;
    }

    public override void ResetState()
    {
        _limiterDrop = 0;
        OutputCurrent = 0;
        PowerDissipation = 0;
        JunctionTemperature = AmbientTemperature;
        IsInDropout = false;
        IsCurrentLimited = false;
        IsThermallyShutDown = false;
    }

    partial void OnModelChanged(RegulatorModel value) => NotifyValueChanged();
}
