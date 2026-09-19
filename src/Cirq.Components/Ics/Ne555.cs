using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Components.Digital;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>
/// NE555 timer, modelled as a genuine mixed-signal package rather than as a behavioural block.
/// <list type="bullet">
/// <item>The internal 5k-5k-5k divider is stamped as three real resistors, so loading the CTRL pin
/// shifts both comparator thresholds exactly as it does on the bench.</item>
/// <item>Two comparators watch THRES against 2/3 Vcc and TRIG against 1/3 Vcc, driving an SR
/// latch through the event scheduler.</item>
/// <item>OUT is a push-pull driver referenced to the GND pin; DISCH is an open-collector NPN
/// stamped as a switched conductance to GND.</item>
/// </list>
/// <para>
/// Pinout: 1=GND, 2=TRIG, 3=OUT, 4=RESET, 5=CTRL, 6=THRES, 7=DISCH, 8=VCC.
/// </para>
/// </summary>
public partial class Ne555 : DigitalComponent
{
    /// <summary>Each leg of the internal divider, in ohms.</summary>
    public const double DividerResistance = 5e3;

    /// <summary>Saturation resistance of the discharge transistor when it is on.</summary>
    private const double DischargeOnResistance = 10.0;

    private const double DischargeOffResistance = 1e9;

    /// <summary>Latch state: true means the output is high and the discharge transistor is off.</summary>
    private bool _latched;

    private bool _dischargeOn;

    public Ne555()
    {
        PropagationDelay = 100e-9;
        Levels = LogicLevels.Ttl with { Vih = 2.0, Vil = 0.8, Voh = 3.3, Vol = 0.1, OutputResistance = 10.0 };

        var pins = new Terminal[9];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 8));

        Gnd = Pin(1, "GND", TerminalType.Ground);
        Trigger = Pin(2, "TRIG", TerminalType.Input);
        Out = Pin(3, "OUT", TerminalType.Output);
        Reset = Pin(4, "RESET", TerminalType.Input);
        Control = Pin(5, "CTRL", TerminalType.Passive);
        Threshold = Pin(6, "THRES", TerminalType.Input);
        Discharge = Pin(7, "DISCH", TerminalType.Bidirectional);
        Vcc = Pin(8, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        // Only OUT is a driven logic pin. DISCH is a switch, stamped as a conductance instead.
        ConfigurePins([Trigger, Threshold, Reset], [Out]);
    }

    public Terminal Gnd { get; }
    public Terminal Trigger { get; }
    public Terminal Out { get; }
    public Terminal Reset { get; }
    public Terminal Control { get; }
    public Terminal Threshold { get; }
    public Terminal Discharge { get; }
    public Terminal Vcc { get; }

    public override string ComponentType => "NE555";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => "NE555";

    /// <summary>Voltage drop of the bipolar output stage below Vcc when driving high.</summary>
    [ObservableProperty]
    public partial double OutputSaturationDrop { get; set; } = 1.7;

    /// <summary>True while the output is high.</summary>
    public bool IsOutputHigh => _latched;

    /// <summary>True while the discharge transistor is conducting.</summary>
    public bool IsDischarging => _dischargeOn;

    /// <summary>The internal 1/3 Vcc node shared by the divider and the trigger comparator.</summary>
    public override int InternalNodeCount => 1;

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var vcc = system.Node(Vcc);
        var gnd = system.Node(Gnd);
        var ctrl = system.Node(Control);
        var lower = system.InternalNode(this);   // 1/3 Vcc tap

        // Internal divider: VCC -- 5k -- CTRL(2/3) -- 5k -- lower(1/3) -- 5k -- GND.
        system.StampConductance(vcc, ctrl, 1.0 / DividerResistance);
        system.StampConductance(ctrl, lower, 1.0 / DividerResistance);
        system.StampConductance(lower, gnd, 1.0 / DividerResistance);

        // Comparator inputs present a high impedance to the pins they watch.
        system.StampConductance(system.Node(Threshold), gnd, 1.0 / Levels.InputResistance);
        system.StampConductance(system.Node(Trigger), gnd, 1.0 / Levels.InputResistance);
        system.StampConductance(system.Node(Reset), gnd, 1.0 / Levels.InputResistance);

        // Open-collector discharge transistor to the GND pin.
        var dischargeResistance = _dischargeOn ? DischargeOnResistance : DischargeOffResistance;
        system.StampConductance(system.Node(Discharge), gnd, 1.0 / dischargeResistance);

        // Push-pull output stage, referenced to the GND pin.
        var branch = system.Branch(this, 0);
        var supply = system.IterationVoltage(vcc) - system.IterationVoltage(gnd);
        var high = Math.Max(supply - OutputSaturationDrop, 0);
        var driven = GetOutputState(0) switch
        {
            LogicState.High => high,
            LogicState.Low => Levels.Vol,
            _ => high * 0.5,
        };

        system.StampTheveninSource(branch, system.Node(Out), gnd, driven, Levels.OutputResistance);
    }

    public override void EvaluateLogic(IDigitalContext context)
    {
        var supply = context.NodeVoltage(Vcc) - context.NodeVoltage(Gnd);
        if (supply < 2.0)
        {
            // Unpowered: release the output and leave the discharge transistor off.
            _latched = false;
            _dischargeOn = false;
            context.Schedule(this, 0, LogicState.HighImpedance, DelayFor(context));
            return;
        }

        var ground = context.NodeVoltage(Gnd);
        var upperThreshold = context.NodeVoltage(Control) - ground;
        var lowerThreshold = upperThreshold * 0.5;

        var thresholdVoltage = context.NodeVoltage(Threshold) - ground;
        var triggerVoltage = context.NodeVoltage(Trigger) - ground;
        var resetVoltage = context.NodeVoltage(Reset) - ground;

        // RESET is active low and dominates both comparators.
        if (resetVoltage < 0.7)
        {
            _latched = false;
        }
        else
        {
            // Trigger below 1/3 Vcc sets the latch; threshold above 2/3 Vcc resets it. When both
            // are asserted, the datasheet says trigger wins.
            if (thresholdVoltage > upperThreshold) _latched = false;
            if (triggerVoltage < lowerThreshold) _latched = true;
        }

        _dischargeOn = !_latched;

        context.Schedule(this, 0, _latched ? LogicState.High : LogicState.Low, DelayFor(context));
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _latched = false;
        _dischargeOn = true;
    }

    /// <summary>
    /// Astable frequency for the classic two-resistor configuration,
    /// <c>f = 1.44 / ((R1 + 2·R2)·C)</c>.
    /// </summary>
    public static double AstableFrequency(double r1, double r2, double c) =>
        1.0 / (Math.Log(2.0) * (r1 + 2.0 * r2) * c);

    /// <summary>Duty cycle of the classic astable configuration, <c>(R1 + R2) / (R1 + 2·R2)</c>.</summary>
    public static double AstableDutyCycle(double r1, double r2) => (r1 + r2) / (r1 + 2.0 * r2);

    /// <summary>Monostable pulse width, <c>t = 1.1·R·C</c>.</summary>
    public static double MonostablePulseWidth(double r, double c) => Math.Log(3.0) * r * c;
}
