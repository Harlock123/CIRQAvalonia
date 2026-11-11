using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>
/// Two operational amplifiers in an 8-pin DIP sharing a supply, the TL072 or NE5532 (or any model): the same amplifier
/// the single parts use, stamped twice. Pinout: 1=OUT1, 2=IN1−, 3=IN1+, 4=V−, 5=IN2+, 6=IN2−, 7=OUT2, 8=V+.
/// </summary>
public partial class DualOpAmp : CircuitComponent, INoiseSource
{
    private const int Channels = 2;

    private readonly (Terminal NonInverting, Terminal Inverting, Terminal Output, OpAmpStage Stage)[] _amplifiers;

    public DualOpAmp() : this(OpAmpModel.Tl072)
    {
    }

    public DualOpAmp(OpAmpModel model)
    {
        Model = model;
        var pins = new Terminal[9];
        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, Digital.DipPackage.PinOffset(number, 8));

        var out1 = Pin(1, "OUT1", TerminalType.Output);
        var in1Minus = Pin(2, "IN1-", TerminalType.Input);
        var in1Plus = Pin(3, "IN1+", TerminalType.Input);
        NegativeSupply = Pin(4, "V-", TerminalType.Power);
        var in2Plus = Pin(5, "IN2+", TerminalType.Input);
        var in2Minus = Pin(6, "IN2-", TerminalType.Input);
        var out2 = Pin(7, "OUT2", TerminalType.Output);
        PositiveSupply = Pin(8, "V+", TerminalType.Power);
        _amplifiers = [(in1Plus, in1Minus, out1, new OpAmpStage()), (in2Plus, in2Minus, out2, new OpAmpStage())];
        Terminals = [.. pins.Skip(1)];
    }

    public Terminal PositiveSupply { get; }

    public Terminal NegativeSupply { get; }

    [ObservableProperty]
    public partial OpAmpModel Model { get; set; }

    public override string ComponentType => "Dual Op-Amp";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => $"dual {Model.Name}";

    public override bool IsNonlinear => true;

    public override int VoltageSourceCount => Channels;

    public override int InternalNodeCount => Channels;

    /// <summary>The pins of one of the two amplifiers, indexed from 0.</summary>
    public (Terminal NonInverting, Terminal Inverting, Terminal Output) Channel(int index)
    {
        var a = _amplifiers[index];
        return (a.NonInverting, a.Inverting, a.Output);
    }

    public bool IsChannelSaturated(int index) => _amplifiers[index].Stage.IsSaturated;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var (vPos, vNeg) = (system.Node(PositiveSupply), system.Node(NegativeSupply));
        for (var i = 0; i < Channels; i++)
        {
            var a = _amplifiers[i];
            a.Stage.Stamp(system, state, Model, system.Node(a.NonInverting), system.Node(a.Inverting), system.Node(a.Output),
                vPos, vNeg, system.InternalNode(this, i), system.Branch(this, i));
        }
    }

    public override void StampAc(AcSystem system, SimulationState state)
    {
        for (var i = 0; i < Channels; i++) system.StampCapacitance(system.InternalNode(this, i), -1, Model.CompensationCapacitance);
    }

    public IEnumerable<NoiseEmission> NoiseSources(MnaSystem system, SimulationState state, double hertz)
    {
        ArgumentNullException.ThrowIfNull(system);
        for (var i = 0; i < Channels; i++)
        {
            var a = _amplifiers[i];
            foreach (var emission in a.Stage.Noise(Model, $"{Name}{(char)('A' + i)}", hertz, system.Node(a.NonInverting),
                         system.Node(a.Inverting), system.InternalNode(this, i)))
                yield return emission;
        }
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        for (var i = 0; i < Channels; i++)
            _amplifiers[i].Stage.Commit(system, state, Model, system.InternalNode(this, i), PositiveSupply, NegativeSupply);
    }

    public override void ResetState()
    {
        foreach (var a in _amplifiers) a.Stage.Reset();
    }

    partial void OnModelChanged(OpAmpModel value) => NotifyValueChanged();
}
