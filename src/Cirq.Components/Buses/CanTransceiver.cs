using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// A CAN transceiver, of the MCP2551 / TJA1050 sort: logic on one side, a differential pair on the
/// other.
/// <para>
/// It looks like the RS-485 transceiver beside it and works on a completely different principle,
/// which is the reason to have both. RS-485 is half duplex by arrangement: exactly one driver may
/// be enabled at a time, and enabling two is a fault you have to avoid by agreement. CAN is
/// <b>wired-AND</b>, and every node may transmit whenever it likes.
/// </para>
/// <para>
/// That works because the two bus states are not symmetric. <b>Recessive</b> — a logic one — is
/// nobody driving: the termination pulls both wires to about half a supply and the difference is
/// zero. <b>Dominant</b> — a logic zero — is a driver pulling CANH up and CANL down, about two
/// volts apart. A dominant bit therefore <i>wins</i>: one node driving dominant while thirty
/// others are recessive gives a dominant bus, because the others are not driving anything to
/// overcome.
/// </para>
/// <para>
/// Out of that one asymmetry falls the thing CAN is famous for. Two nodes start transmitting
/// together; each watches the bus while it sends; the moment one sends recessive and reads back
/// dominant it knows somebody else is sending a zero where it sent a one, and it stops. No
/// collision, no retry, no lost time — the higher-priority message simply carries on without
/// noticing. <see cref="LostArbitration"/> is that moment.
/// </para>
/// <para>
/// Note the logic pins are inverted with respect to the bus, and it catches everybody: TXD
/// <b>low</b> sends a dominant bit, and RXD reads low when the bus is dominant. An idle bus, with
/// nothing to say, sits recessive with both pins high.
/// </para>
/// </summary>
public sealed partial class CanTransceiver : DigitalComponent
{
    private const int ReceiverOutput = 0;

    private double _difference;
    private bool _driving;

    public CanTransceiver()
    {
        PropagationDelay = 70e-9;
        Levels = LogicLevels.Cmos5V;

        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-50, -42));
        TransmitIn = new Terminal("txd", "TXD", TerminalType.Input, new Point(-50, -14));
        ReceiveOut = new Terminal("rxd", "RXD", TerminalType.Output, new Point(-50, 14));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-50, 42));

        High = new Terminal("canh", "CANH", TerminalType.Bidirectional, new Point(50, -18));
        Low = new Terminal("canl", "CANL", TerminalType.Bidirectional, new Point(50, 18));

        Terminals = [TransmitIn, ReceiveOut, High, Low, Vcc, Gnd];

        // Only RXD is an ordinary logic output; the pair is driven by this component's own
        // branches, because the bus swings about mid-supply rather than between the logic rails.
        ConfigurePins([TransmitIn], [ReceiveOut]);
    }

    /// <summary>From the controller. Low sends a dominant bit, which is a logic zero on the bus.</summary>
    public Terminal TransmitIn { get; }

    /// <summary>To the controller. Low while the bus is dominant.</summary>
    public Terminal ReceiveOut { get; }

    /// <summary>CAN High — pulled up by a driver sending dominant.</summary>
    public Terminal High { get; }

    /// <summary>CAN Low — pulled down by the same driver.</summary>
    public Terminal Low { get; }

    public Terminal Vcc { get; }

    public Terminal Gnd { get; }

    /// <summary>Where both wires sit when nobody is driving, in volts.</summary>
    [ObservableProperty]
    public partial double RecessiveVoltage { get; set; } = 2.5;

    /// <summary>How far apart a dominant driver pulls the pair, in volts.</summary>
    [ObservableProperty]
    public partial double DominantDrive { get; set; } = 2.0;

    /// <summary>
    /// The difference at which the receiver calls the bus dominant, in volts. The standard puts
    /// the boundary at 0.9 V, with under 0.5 V certainly recessive and over 0.9 V certainly
    /// dominant.
    /// </summary>
    [ObservableProperty]
    public partial double ReceiverThreshold { get; set; } = 0.9;

    /// <summary>Resistance behind each bus pin while driving dominant, in ohms.</summary>
    [ObservableProperty]
    public partial double DriverResistance { get; set; } = 8.0;

    /// <summary>
    /// The resistance this node's recessive bias holds the pair at, in ohms. It is deliberately
    /// weak: it has to be overcome by any dominant driver on the bus, which is the whole
    /// mechanism.
    /// </summary>
    [ObservableProperty]
    public partial double BiasResistance { get; set; } = 10e3;

    /// <summary>The receiver's differential input resistance, in ohms.</summary>
    [ObservableProperty]
    public partial double InputResistance { get; set; } = 25e3;

    public override string ComponentType => "CAN Transceiver";

    public override string ValueLabel => IsBusDominant ? "dominant" : "recessive";

    /// <summary>CANH − CANL at the last solved point.</summary>
    public double Difference => _difference;

    /// <summary>True when the bus as a whole is being held dominant, by anyone.</summary>
    public bool IsBusDominant { get; private set; }

    /// <summary>True while this node is the one driving dominant.</summary>
    public bool IsDriving => _driving;

    /// <summary>
    /// True when this node sent a recessive bit and read back a dominant one — somebody else is
    /// transmitting a zero where this node sent a one. On real hardware the controller stops
    /// talking at this instant and lets the other message through, which is what makes CAN
    /// arbitration cost nothing.
    /// </summary>
    public bool LostArbitration { get; private set; }

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    /// <summary>RXD from the base class, plus one branch for each of the two bus pins.</summary>
    public override int VoltageSourceCount => base.VoltageSourceCount + 2;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        var high = system.Node(High);
        var low = system.Node(Low);
        var gnd = system.Node(Gnd);

        // The receiver sits across the pair whether or not this node is talking.
        system.StampConductance(high, low, 1.0 / Math.Max(InputResistance, 1.0));

        // And each wire is biased weakly to the recessive level. Weakly is the point: this is what
        // any dominant driver anywhere on the bus has to overcome, and it barely resists.
        var bias = 1.0 / Math.Max(BiasResistance, 1.0);

        system.StampConductance(high, gnd, bias);
        system.StampCurrentSource(gnd, high, RecessiveVoltage * bias);
        system.StampConductance(low, gnd, bias);
        system.StampCurrentSource(gnd, low, RecessiveVoltage * bias);

        var half = Math.Max(DominantDrive, 0.0) / 2.0;

        StampBusPin(system, High, 1, _driving ? RecessiveVoltage + half : null);
        StampBusPin(system, Low, 2, _driving ? RecessiveVoltage - half : null);
    }

    /// <summary>
    /// One bus pin: a stiff source while this node sends dominant, and a branch carrying nothing
    /// at all while it sends recessive. Recessive really is <i>not driving</i> rather than driving
    /// to the middle, which is what lets somebody else's dominant bit win without a fight.
    /// </summary>
    private void StampBusPin(MnaSystem system, Terminal pin, int local, double? driveTo)
    {
        var branch = system.Branch(this, local);

        if (driveTo is not { } voltage)
        {
            system.Add(branch, branch, 1.0);
            return;
        }

        system.StampTheveninSource(
            branch, system.Node(pin), system.Node(Gnd), voltage, Math.Max(DriverResistance, 1e-3));
    }

    public override void EvaluateLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        // Inverted, and this is the part that catches people: a low on TXD is a dominant bit.
        var sendingDominant = context.ReadInput(TransmitIn, Levels).IsLow();

        _driving = sendingDominant;
        _difference = context.NodeVoltage(High) - context.NodeVoltage(Low);

        IsBusDominant = _difference >= ReceiverThreshold;

        // Sent a one, heard a zero: somebody with a higher priority message is talking over us.
        LostArbitration = !sendingDominant && IsBusDominant;

        context.Schedule(this, ReceiverOutput,
            IsBusDominant ? LogicState.Low : LogicState.High, delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _driving = false;
        _difference = 0;
        IsBusDominant = false;
        LostArbitration = false;
    }
}
