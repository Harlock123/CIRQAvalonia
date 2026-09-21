using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// A MAX232: two RS-232 drivers and two receivers, and a charge pump that makes the line voltages
/// out of nothing but the five volt supply.
/// <para>
/// RS-232 predates logic levels as anybody now thinks of them. A mark is <b>minus</b> five to
/// fifteen volts and a space is <b>plus</b> five to fifteen — bipolar, and inverted with respect
/// to the TTL it is carrying, so an idle line sits at about minus ten volts and a start bit swings
/// it positive. A microcontroller pin cannot produce that and would be destroyed by receiving it,
/// which is the entire reason this part exists.
/// </para>
/// <para>
/// <b>The charge pump is the famous half.</b> Before this chip a serial port meant a board with
/// plus and minus twelve volt rails on it just for the line drivers. The MAX232 makes both from
/// the five volts already there, using four capacitors and a switch matrix: one pair is charged to
/// the supply and then stacked on top of it to give roughly twice the supply, and the other pair
/// is charged and then flipped upside down to give roughly minus that. Probe <c>V+</c> and
/// <c>V-</c> in a running circuit and they really are there — about ±8.5 V from a 5 V rail once
/// the drivers are loaded, which is inside the standard and nowhere near the ±12 V people expect.
/// </para>
/// <para>
/// The pump is modelled by what it produces rather than by its switching: the two rails appear as
/// sources with a real output resistance, so loading a driver pulls them in and overloading one
/// collapses the other as well — which is exactly the failure people meet when they try to drive
/// four lines from one chip. The four capacitor pins are brought out because a real one does not
/// work without them, but nothing here is stamped through them.
/// </para>
/// <para>
/// <b>The receivers invert back</b>, and they have hysteresis — about half a volt of it — because
/// a line long enough to need RS-232 is long enough to pick up noise, and a receiver without
/// hysteresis would chatter on every edge. They also survive ±30 V on the input, which the model
/// reflects by simply not caring how far outside the supply the line goes.
/// </para>
/// </summary>
public sealed partial class Max232 : DigitalIc
{
    private const int Channels = 2;

    private readonly Terminal[] _driverIn = new Terminal[Channels];
    private readonly Terminal[] _driverOut = new Terminal[Channels];
    private readonly Terminal[] _receiverIn = new Terminal[Channels];
    private readonly Terminal[] _receiverOut = new Terminal[Channels];

    private readonly bool[] _driverSpace = new bool[Channels];
    private readonly bool[] _receiverMark = [true, true];
    private readonly double[] _line = new double[Channels];

    public Max232() : base(16)
    {
        PropagationDelay = 500e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        CapacitorOnePositive = Pin(1, "C1+", TerminalType.Passive);
        PumpPositive = Pin(2, "V+", TerminalType.Power);
        CapacitorOneNegative = Pin(3, "C1-", TerminalType.Passive);
        CapacitorTwoPositive = Pin(4, "C2+", TerminalType.Passive);
        CapacitorTwoNegative = Pin(5, "C2-", TerminalType.Passive);
        PumpNegative = Pin(6, "V-", TerminalType.Power);

        _driverOut[1] = Pin(7, "T2OUT", TerminalType.Passive);
        _receiverIn[1] = Pin(8, "R2IN", TerminalType.Passive);
        _receiverOut[1] = Pin(9, "R2OUT", TerminalType.Output);
        _driverIn[1] = Pin(10, "T2IN", TerminalType.Input);
        _driverIn[0] = Pin(11, "T1IN", TerminalType.Input);
        _receiverOut[0] = Pin(12, "R1OUT", TerminalType.Output);
        _receiverIn[0] = Pin(13, "R1IN", TerminalType.Passive);
        _driverOut[0] = Pin(14, "T1OUT", TerminalType.Passive);

        Gnd = Pin(15, "GND", TerminalType.Ground);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        // The receiver inputs are analog and are read as node voltages rather than as logic, so
        // they are deliberately not configured as digital pins: RS-232 is not a logic level.
        ConfigurePins(_driverIn, _receiverOut);
    }

    /// <summary>Charge-pump capacitor pins. Real ones need 100 nF here; nothing is stamped through them.</summary>
    public Terminal CapacitorOnePositive { get; }

    public Terminal CapacitorOneNegative { get; }

    public Terminal CapacitorTwoPositive { get; }

    public Terminal CapacitorTwoNegative { get; }

    /// <summary>The doubled rail, roughly twice the supply. Probe it — it is the point of the part.</summary>
    public Terminal PumpPositive { get; }

    /// <summary>The inverted rail, roughly minus twice the supply.</summary>
    public Terminal PumpNegative { get; }

    /// <summary>The two TTL inputs, T1IN and T2IN.</summary>
    public IReadOnlyList<Terminal> DriverInputs => _driverIn;

    /// <summary>The two RS-232 outputs, T1OUT and T2OUT.</summary>
    public IReadOnlyList<Terminal> DriverOutputs => _driverOut;

    /// <summary>The two RS-232 inputs, R1IN and R2IN.</summary>
    public IReadOnlyList<Terminal> ReceiverInputs => _receiverIn;

    /// <summary>The two TTL outputs, R1OUT and R2OUT.</summary>
    public IReadOnlyList<Terminal> ReceiverOutputs => _receiverOut;

    /// <summary>
    /// Output resistance of each charge-pump rail, in ohms. It is what makes the rails sag under
    /// load, and a real pump running at a few hundred kilohertz into a tenth of a microfarad has
    /// something very like it.
    /// </summary>
    [ObservableProperty]
    public partial double PumpResistance { get; set; } = 150.0;

    /// <summary>Output resistance of each line driver, in ohms. The standard wants 300 Ω of it.</summary>
    [ObservableProperty]
    public partial double DriverResistance { get; set; } = 300.0;

    /// <summary>
    /// Where a receiver decides, in volts. The standard leaves a wide undefined band between the
    /// levels and this sits in the middle of it, which is why an RS-232 receiver happily reads a
    /// line driven by a 0-to-5 V logic gate even though nothing about that is RS-232.
    /// </summary>
    [ObservableProperty]
    public partial double ReceiverThreshold { get; set; } = 1.5;

    /// <summary>How far the input must come back before a receiver changes its mind, in volts.</summary>
    [ObservableProperty]
    public partial double ReceiverHysteresis { get; set; } = 0.5;

    /// <summary>Input resistance of each receiver, in ohms. The standard calls for 3 kΩ to 7 kΩ.</summary>
    [ObservableProperty]
    public partial double ReceiverResistance { get; set; } = 5e3;

    public override string PartNumber => "MAX232";

    /// <summary>
    /// The rails it is making, once it is making them. Before the circuit has run there is no
    /// honest number to show — the pump's output depends on the supply it is given — so it says
    /// what it is instead of claiming ±0 V.
    /// </summary>
    public override string ValueLabel => PositiveRail > 0.1
        ? $"±{SiPrefix.Format(PositiveRail, "V")}"
        : "RS-232";

    /// <summary>Two line drivers and the two pump rails, on top of the logic outputs.</summary>
    public override int VoltageSourceCount => base.VoltageSourceCount + Channels + 2;

    /// <summary>The doubled rail at the last solved point, in volts.</summary>
    public double PositiveRail { get; private set; }

    /// <summary>The inverted rail at the last solved point, in volts.</summary>
    public double NegativeRail { get; private set; }

    /// <summary>True while a driver is sending a space, which is the positive line level.</summary>
    public bool IsSendingSpace(int channel) => _driverSpace[channel];

    /// <summary>True while a receiver reads a mark, which is the negative line level.</summary>
    public bool IsReceivingMark(int channel) => _receiverMark[channel];

    /// <summary>What one driver is actually delivering to its line, in volts.</summary>
    public double LineVoltage(int channel) => _line[channel];

    public IReadOnlyList<string> Violations =>
        PositiveRail > 0.1 && _line.Any(v => Math.Abs(v) < 5.0)
            ? ["a driver is delivering less than the ±5 V RS-232 requires — the load is heavier " +
               "than the 3 kΩ the standard allows, and the charge pump has been pulled in with it"]
            : [];

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        var gnd = system.Node(Gnd);
        var supply = system.IterationVoltage(system.Node(Vcc)) - system.IterationVoltage(gnd);

        // Two capacitors stacked on the supply, less the switch drops that stacking costs. Below
        // the minimum supply there is nothing to pump and both rails collapse to ground, which is
        // what an unpowered part does and why its outputs stop being a line level.
        var open = supply < MinimumSupplyVoltage;
        var ideal = open ? 0.0 : Math.Max((2.0 * supply) - 1.5, 0.0);

        var pump = Math.Max(PumpResistance, 1e-3);

        system.StampTheveninSource(system.Branch(this, PumpBranch), system.Node(PumpPositive), gnd, ideal, pump);
        system.StampTheveninSource(system.Branch(this, PumpBranch + 1), system.Node(PumpNegative), gnd, -ideal, pump);

        for (var i = 0; i < Channels; i++)
        {
            // A receiver is a resistance to ground and nothing else. It is specified rather than
            // incidental: the standard fixes it so a driver knows what it is pushing against.
            system.StampConductance(system.Node(_receiverIn[i]), gnd, 1.0 / Math.Max(ReceiverResistance, 1.0));

            // A driver is a switch to one rail or the other through its own resistance, and it is
            // stamped against the rail's node rather than against ground on purpose: the current
            // a line draws then comes out of the pump, so loading a driver really does pull the
            // rail in, and overloading one really does collapse the other — which is the failure
            // people meet when they try to run four lines off one chip.
            var rail = _driverSpace[i] ? PumpPositive : PumpNegative;

            system.StampTheveninSource(
                system.Branch(this, DriverBranch + i), system.Node(_driverOut[i]), system.Node(rail),
                0.0, Math.Max(DriverResistance, 1e-3));
        }
    }

    /// <summary>First branch of the two line drivers, past the ones the logic outputs reserved.</summary>
    private int DriverBranch => base.VoltageSourceCount;

    private int PumpBranch => DriverBranch + Channels;

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        base.CommitTimeStep(system, state);

        var gnd = system.NodeVoltage(system.Node(Gnd));

        PositiveRail = system.NodeVoltage(system.Node(PumpPositive)) - gnd;
        NegativeRail = system.NodeVoltage(system.Node(PumpNegative)) - gnd;

        for (var i = 0; i < Channels; i++)
            _line[i] = system.NodeVoltage(system.Node(_driverOut[i])) - gnd;
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var ground = context.NodeVoltage(Gnd);

        for (var i = 0; i < Channels; i++)
        {
            // Inverted. A TTL low on the input becomes a space, which is the positive line level,
            // and this is the single thing people get wrong when they scope an RS-232 line and
            // find the idle state is the one that looks like a fault.
            _driverSpace[i] = context.ReadInput(_driverIn[i], Levels).IsLow();

            var line = context.NodeVoltage(_receiverIn[i]) - ground;

            // Hysteresis: the threshold moves away from wherever the receiver currently sits, so
            // noise riding on an edge cannot walk it back and forth.
            var edge = _receiverMark[i]
                ? ReceiverThreshold + (ReceiverHysteresis / 2.0)
                : ReceiverThreshold - (ReceiverHysteresis / 2.0);

            _receiverMark[i] = line < edge;

            // And inverted again on the way back: a mark is a logic one.
            context.Schedule(this, i, _receiverMark[i] ? LogicState.High : LogicState.Low, delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        Array.Clear(_driverSpace);
        Array.Fill(_receiverMark, true);

        PositiveRail = 0;
        NegativeRail = 0;

        Array.Clear(_line);
    }
}
