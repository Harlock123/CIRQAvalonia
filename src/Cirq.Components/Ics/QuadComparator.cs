using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>
/// LM339 quad comparator: four independent open-collector comparators sharing one supply pair.
/// <para>
/// Pinout: 1=OUT2, 2=OUT1, 3=VCC, 4=IN1-, 5=IN1+, 6=IN2-, 7=IN2+, 8=IN3-,
/// 9=IN3+, 10=IN4-, 11=IN4+, 12=GND, 13=OUT4, 14=OUT3.
/// </para>
/// <para>
/// Because every output is open collector they can be tied together directly, which makes the
/// common node a wired-AND: any one comparator pulling low takes the whole node low. That is the
/// usual way this part is used as a window detector or a multi-input alarm, so the outputs are
/// modelled as genuine pull-downs rather than as drivers.
/// </para>
/// </summary>
public partial class QuadComparator : DigitalIc
{
    private sealed class ComparatorChannel
    {
        public required Terminal NonInverting;
        public required Terminal Inverting;
        public required Terminal Output;
        public double Differential;
    }

    private readonly ComparatorChannel[] _channels;

    public QuadComparator()
        : base(14)
    {
        PropagationDelay = ComparatorModel.Lm339.ResponseTime;

        var pins = new Terminal[15];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 14));

        var out2 = Pin(1, "OUT2", TerminalType.Output);
        var out1 = Pin(2, "OUT1", TerminalType.Output);
        Vcc = Pin(3, "VCC", TerminalType.Power);
        var in1Minus = Pin(4, "IN1-", TerminalType.Input);
        var in1Plus = Pin(5, "IN1+", TerminalType.Input);
        var in2Minus = Pin(6, "IN2-", TerminalType.Input);
        var in2Plus = Pin(7, "IN2+", TerminalType.Input);
        var in3Minus = Pin(8, "IN3-", TerminalType.Input);
        var in3Plus = Pin(9, "IN3+", TerminalType.Input);
        var in4Minus = Pin(10, "IN4-", TerminalType.Input);
        var in4Plus = Pin(11, "IN4+", TerminalType.Input);
        Gnd = Pin(12, "GND", TerminalType.Ground);
        var out4 = Pin(13, "OUT4", TerminalType.Output);
        var out3 = Pin(14, "OUT3", TerminalType.Output);

        _channels =
        [
            new ComparatorChannel { NonInverting = in1Plus, Inverting = in1Minus, Output = out1 },
            new ComparatorChannel { NonInverting = in2Plus, Inverting = in2Minus, Output = out2 },
            new ComparatorChannel { NonInverting = in3Plus, Inverting = in3Minus, Output = out3 },
            new ComparatorChannel { NonInverting = in4Plus, Inverting = in4Minus, Output = out4 },
        ];

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [.. _channels.SelectMany(c => new[] { c.NonInverting, c.Inverting })],
            [.. _channels.Select(c => c.Output)]);
    }

    [ObservableProperty]
    public partial ComparatorModel Model { get; set; } = ComparatorModel.Lm339;

    /// <summary>Built-in hysteresis in volts, applied symmetrically to every channel.</summary>
    [ObservableProperty]
    public partial double HysteresisVoltage { get; set; } = 1e-3;

    public override string PartNumber => "LM339";

    /// <summary>The pins of one of the four comparators, indexed from 0.</summary>
    public (Terminal NonInverting, Terminal Inverting, Terminal Output) Channel(int index)
    {
        var c = _channels[index];
        return (c.NonInverting, c.Inverting, c.Output);
    }

    /// <summary>Differential input of one channel at the last solved point.</summary>
    public double DifferentialInput(int index) => _channels[index].Differential;

    /// <summary>True when a channel has released its output.</summary>
    public bool IsChannelHigh(int index) => GetOutputState(index) is LogicState.HighImpedance or LogicState.High;

    /// <summary>
    /// Open-collector outputs: each pulls down to the GND pin or releases. The base class already
    /// handles a released output as a zero-current branch, which is exactly right here.
    /// </summary>
    protected override double OutputVoltage(LogicState state) => Model.OutputSaturationVoltage;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        Levels = Levels with
        {
            OutputResistance = Model.OutputResistance,
            InputResistance = Model.InputResistance,
        };

        base.StampMatrix(system, state);

        system.StampCurrentSource(system.Node(Vcc), system.Node(Gnd), Model.QuiescentCurrent);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        for (var i = 0; i < _channels.Length; i++)
        {
            var channel = _channels[i];

            channel.Differential =
                context.NodeVoltage(channel.NonInverting) - context.NodeVoltage(channel.Inverting)
                + Model.InputOffsetVoltage;

            var currentlyHigh = IsChannelHigh(i);
            var threshold = currentlyHigh ? -Math.Abs(HysteresisVoltage) : Math.Abs(HysteresisVoltage);

            // Releasing rather than driving is what lets several outputs share a node.
            context.Schedule(this, i,
                channel.Differential > threshold ? LogicState.HighImpedance : LogicState.Low, delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        foreach (var channel in _channels) channel.Differential = 0;
    }

    partial void OnModelChanged(ComparatorModel value)
    {
        PropagationDelay = value.ResponseTime;
        NotifyValueChanged();
    }
}
