using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>
/// An LM324: four operational amplifiers in one package, sharing a supply.
/// <para>
/// It is a package rather than a model — the amplifier inside is the same one the single op-amps
/// use, stamped four times. What it changes is how a schematic looks. A two-stage filter, a
/// summing node and a buffer is four amplifiers, and as four separate parts that is four sets of
/// supply wires cluttering the drawing over the circuit you were trying to read. It is also how
/// they are actually bought: nobody puts four singles on a board when a quad costs less than two
/// of them.
/// </para>
/// <para>
/// The LM324's own character matters as much as the count. It is a <b>single-supply</b> part, like
/// the LM358 that is literally half of one, so its output goes down to within a few tens of
/// millivolts of the negative rail and stops about a volt and a half below the positive one. On
/// five volts that is roughly 0.02 V to 3.5 V of usable output: plenty of room at the bottom, none
/// at the top. Build a follower with one and drive it past three and a half volts to see it.
/// </para>
/// <para>
/// The <b>unused amplifiers still have to be wired</b>, and it is the commonest mistake with any
/// quad. An op-amp with floating inputs is not idle — it slams to a rail, draws current and
/// couples into the others through the shared supply. Tie the spare ones as followers with their
/// inputs at a sensible voltage.
/// </para>
/// <para>
/// Pinout: 1=OUT1, 2=IN1−, 3=IN1+, 4=V+, 5=IN2+, 6=IN2−, 7=OUT2, 8=OUT3, 9=IN3−, 10=IN3+,
/// 11=V−, 12=IN4+, 13=IN4−, 14=OUT4.
/// </para>
/// </summary>
public partial class QuadOpAmp : CircuitComponent
{
    private const int Channels = 4;

    private sealed class Amplifier
    {
        public required Terminal NonInverting;
        public required Terminal Inverting;
        public required Terminal Output;
        public OpAmpStage Stage { get; } = new();
    }

    private readonly Amplifier[] _amplifiers;

    public QuadOpAmp(OpAmpModel? model = null)
    {
        Model = model ?? OpAmpModel.Lm324;

        var pins = new Terminal[15];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, Digital.DipPackage.PinOffset(number, 14));

        var out1 = Pin(1, "OUT1", TerminalType.Output);
        var in1Minus = Pin(2, "IN1-", TerminalType.Input);
        var in1Plus = Pin(3, "IN1+", TerminalType.Input);
        PositiveSupply = Pin(4, "V+", TerminalType.Power);
        var in2Plus = Pin(5, "IN2+", TerminalType.Input);
        var in2Minus = Pin(6, "IN2-", TerminalType.Input);
        var out2 = Pin(7, "OUT2", TerminalType.Output);
        var out3 = Pin(8, "OUT3", TerminalType.Output);
        var in3Minus = Pin(9, "IN3-", TerminalType.Input);
        var in3Plus = Pin(10, "IN3+", TerminalType.Input);
        NegativeSupply = Pin(11, "V-", TerminalType.Power);
        var in4Plus = Pin(12, "IN4+", TerminalType.Input);
        var in4Minus = Pin(13, "IN4-", TerminalType.Input);
        var out4 = Pin(14, "OUT4", TerminalType.Output);

        _amplifiers =
        [
            new Amplifier { NonInverting = in1Plus, Inverting = in1Minus, Output = out1 },
            new Amplifier { NonInverting = in2Plus, Inverting = in2Minus, Output = out2 },
            new Amplifier { NonInverting = in3Plus, Inverting = in3Minus, Output = out3 },
            new Amplifier { NonInverting = in4Plus, Inverting = in4Minus, Output = out4 },
        ];

        Terminals = [.. pins.Skip(1)];
    }

    public Terminal PositiveSupply { get; }

    public Terminal NegativeSupply { get; }

    /// <summary>
    /// The amplifier inside, and it is the same one the single parts use. Changing it changes all
    /// four, because they are on one piece of silicon.
    /// </summary>
    [ObservableProperty]
    public partial OpAmpModel Model { get; set; }

    public override string ComponentType => "LM324";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => $"quad {Model.Name}";

    public override bool IsNonlinear => true;

    /// <summary>One output buffer branch per amplifier.</summary>
    public override int VoltageSourceCount => Channels;

    /// <summary>One compensated gain node per amplifier.</summary>
    public override int InternalNodeCount => Channels;

    /// <summary>The pins of one of the four amplifiers, indexed from 0.</summary>
    public (Terminal NonInverting, Terminal Inverting, Terminal Output) Channel(int index)
    {
        var amplifier = _amplifiers[index];
        return (amplifier.NonInverting, amplifier.Inverting, amplifier.Output);
    }

    /// <summary>True when one amplifier's output has run into a supply rail.</summary>
    public bool IsChannelSaturated(int index) => _amplifiers[index].Stage.IsSaturated;

    /// <summary>True when one amplifier is moving at its slew-rate limit.</summary>
    public bool IsChannelSlewing(int index) => _amplifiers[index].Stage.IsSlewing;

    /// <summary>Any of the four having run into a rail, for the label and the hover card.</summary>
    public bool IsSaturated => _amplifiers.Any(a => a.Stage.IsSaturated);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var vPos = system.Node(PositiveSupply);
        var vNeg = system.Node(NegativeSupply);

        for (var i = 0; i < Channels; i++)
        {
            var amplifier = _amplifiers[i];

            // Each amplifier gets its own internal node and its own branch, which is the whole of
            // what makes four of them fit in one component.
            amplifier.Stage.Stamp(
                system, state, Model,
                system.Node(amplifier.NonInverting), system.Node(amplifier.Inverting),
                system.Node(amplifier.Output), vPos, vNeg,
                system.InternalNode(this, i), system.Branch(this, i));
        }
    }

    public override void StampAc(AcSystem system, SimulationState state)
    {
        for (var i = 0; i < Channels; i++)
            system.StampCapacitance(system.InternalNode(this, i), -1, Model.CompensationCapacitance);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        for (var i = 0; i < Channels; i++)
        {
            _amplifiers[i].Stage.Commit(
                system, state, Model, system.InternalNode(this, i), PositiveSupply, NegativeSupply);
        }
    }

    public override void ResetState()
    {
        foreach (var amplifier in _amplifiers) amplifier.Stage.Reset();
    }

    partial void OnModelChanged(OpAmpModel value) => NotifyValueChanged();
}
