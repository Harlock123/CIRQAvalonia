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
    double AdjustPinCurrent = 0,
    double ReferenceTempcoPerKelvin = -1e-3)
{
    /// <summary>
    /// The reference at a temperature, in volts.
    /// <para>
    /// A regulator's output is only as steady as the reference inside it, and that reference
    /// moves. A 78xx drifts about a millivolt a degree, which over a commercial range is some
    /// tens of millivolts — small against five volts, and not small against the tolerance
    /// somebody quoted for the thing it is powering. It is quoted on the datasheet as an output
    /// voltage drift, and it is the reason a supply measured on the bench is not the supply the
    /// board sees in a warm enclosure.
    /// </para>
    /// <para>
    /// Applied about the reference's own sign, so a negative regulator's output drifts towards
    /// zero as it warms just as a positive one does rather than away from it.
    /// </para>
    /// </summary>
    public double ReferenceAt(double celsius)
    {
        var drift = ReferenceTempcoPerKelvin * (celsius - 25.0);

        return ReferenceVoltage + (Math.Sign(ReferenceVoltage) * drift);
    }

    public static readonly RegulatorModel Lm7805 = new("LM7805", 5.0, 2.0, 1.5, 5e-3, 0.02, 50, false);
    public static readonly RegulatorModel Lm7809 = new("LM7809", 9.0, 2.0, 1.5, 5e-3, 0.02, 50, false);
    public static readonly RegulatorModel Lm7812 = new("LM7812", 12.0, 2.0, 1.5, 5e-3, 0.02, 50, false);
    public static readonly RegulatorModel Lm7905 = new("LM7905", -5.0, 2.0, 1.5, 5e-3, 0.02, 50, false);

    /// <summary>Adjustable regulator: holds 1.25 V between OUT and ADJ.</summary>
    public static readonly RegulatorModel Lm317 = new(
        "LM317", 1.25, 2.0, 1.5, 5e-3, 0.02, 50, true, 50e-6,
        // Specified as a fraction of the reference rather than in millivolts, which on 1.25 V is
        // a far tighter figure in absolute terms than a 78xx's.
        ReferenceTempcoPerKelvin: -1.25 * 1e-4);

    /// <summary>Low-dropout 3.3 V regulator.</summary>
    public static readonly RegulatorModel Ld1117 = new(
        "LD1117-3.3", 3.3, 1.1, 0.8, 5e-3, 0.02, 60, false,
        ReferenceTempcoPerKelvin: -0.4e-3);

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

    /// <summary>
    /// Ambient temperature in degrees Celsius, as the circuit's own setting had it at the last
    /// solve. Read-only here: it is a property of the run rather than of this part, and having
    /// two ambient temperatures in one circuit is a way to be wrong twice.
    /// <para>
    /// Set it under <b>Simulate &gt; Conditions</b>, where every other junction in the circuit
    /// reads it from as well.
    /// </para>
    /// </summary>
    public double AmbientTemperature { get; private set; } = 27.0;

    /// <summary>Junction temperature at which the regulator shuts down, in degrees Celsius.</summary>
    [ObservableProperty]
    public partial double ThermalShutdownTemperature { get; set; } = 150.0;

    /// <summary>
    /// How long the junction takes to approach its steady-state temperature, in seconds.
    /// <para>
    /// The die has thermal mass, so power has to be sustained before it heats up. Without this the
    /// temperature tracked instantaneous power, and the microsecond inrush that charges an output
    /// capacitor at switch-on — tens of watts for a few microseconds — read as a junction at
    /// hundreds of degrees and latched thermal shutdown. A real 7805 starts into a capacitor every
    /// day. Ten milliseconds still trips a genuine short within a few milliseconds, which is the
    /// behaviour the protection exists for.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double ThermalTimeConstant { get; set; } = 10e-3;

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
        // At the junction's temperature rather than the room's: the reference is on the die, and
        // on a regulator dropping several watts the die is the part that is hot.
        var nominal = Model.ReferenceAt(JunctionTemperature);
        var passThrough = headroom - Math.Sign(nominal) * Model.DropoutVoltage;

        if (nominal < 0)
        {
            // Negative regulators clamp from the other side: mirror, solve, mirror back.
            var (v, s) = Available(-nominal, -passThrough);
            return (-v, s);
        }

        return Available(nominal, passThrough);
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
    /// <summary>
    /// What the regulator can actually hold: the nominal output, or whatever the input leaves
    /// after the dropout, but never the wrong side of its own common pin.
    /// <para>
    /// The pass element is a follower — it can source current but not sink it — so a positive
    /// regulator cannot pull its output below common however little headroom it has. Without the
    /// floor, a part whose input was still rising drove its output to minus the dropout voltage,
    /// which is not something a 78xx does, and made the output capacitor of a supply look
    /// reverse-biased while the reservoir was charging.
    /// </para>
    /// </summary>
    private static (double Value, double Slope) Available(double nominal, double passThrough)
    {
        var (floored, flooredSlope) = SmoothMaximum(0.0, passThrough);
        var (value, slope) = SmoothMinimum(nominal, floored);
        return (value, slope * flooredSlope);
    }

    /// <summary>The mirror of <see cref="SmoothMinimum"/>, with the slope taken in the same sense.</summary>
    private static (double Value, double Slope) SmoothMaximum(double a, double b)
    {
        var (value, slope) = SmoothMinimum(-a, -b);
        return (-value, slope);
    }

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

        // The circuit's ambient, not a figure of this part's own: a regulator in an enclosure at
        // 60 degrees starts 60 degrees up before it has dissipated anything, which is most of
        // what decides whether it reaches shutdown.
        AmbientTemperature = state.TemperatureKelvin - 273.15;

        // Where the junction would end up if this power were held indefinitely.
        var steady = AmbientTemperature + PowerDissipation * Model.ThermalResistance;

        var step = state.TimeStep;
        if (step > 0 && ThermalTimeConstant > 0)
        {
            JunctionTemperature += (steady - JunctionTemperature) * (1.0 - Math.Exp(-step / ThermalTimeConstant));
        }
        else if (state.Settings.UseInitialConditions && !state.IsTransient)
        {
            // Under initial conditions t=0 is the instant power is applied, not a circuit that
            // has been running for ever: the die is still at ambient however much power the
            // inrush is momentarily dissipating.
            JunctionTemperature = AmbientTemperature;
        }
        else
        {
            // A plain bias point has been settled for ever, so the junction is at its
            // steady-state temperature for that dissipation.
            JunctionTemperature = steady;
        }

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
