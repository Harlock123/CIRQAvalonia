using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// Dual D-type positive-edge-triggered flip-flop with asynchronous active-low preset and clear.
/// <para>
/// Pinout: 1=1CLR, 2=1D, 3=1CLK, 4=1PRE, 5=1Q, 6=1Q', 7=GND,
/// 8=2Q', 9=2Q, 10=2PRE, 11=2CLK, 12=2D, 13=2CLR, 14=VCC.
/// </para>
/// </summary>
public sealed class Ic7474 : DigitalIc
{
    private sealed class FlipFlop
    {
        public required Terminal Clear;
        public required Terminal Data;
        public required Terminal Clock;
        public required Terminal Preset;
        public required Terminal Q;
        public required Terminal QNot;

        /// <summary>Clock level seen on the previous evaluation, for rising-edge detection.</summary>
        public LogicState LastClock = LogicState.Unknown;

        public LogicState State = LogicState.Low;
    }

    private readonly FlipFlop[] _flipFlops;

    public Ic7474() : base(14)
    {
        PropagationDelay = 14e-9;

        var pins = new Terminal[15];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 14));

        _flipFlops =
        [
            new FlipFlop
            {
                Clear = Pin(1, "1CLR", TerminalType.Input),
                Data = Pin(2, "1D", TerminalType.Input),
                Clock = Pin(3, "1CLK", TerminalType.Input),
                Preset = Pin(4, "1PRE", TerminalType.Input),
                Q = Pin(5, "1Q", TerminalType.Output),
                QNot = Pin(6, "1Q'", TerminalType.Output),
            },
            new FlipFlop
            {
                QNot = Pin(8, "2Q'", TerminalType.Output),
                Q = Pin(9, "2Q", TerminalType.Output),
                Preset = Pin(10, "2PRE", TerminalType.Input),
                Clock = Pin(11, "2CLK", TerminalType.Input),
                Data = Pin(12, "2D", TerminalType.Input),
                Clear = Pin(13, "2CLR", TerminalType.Input),
            },
        ];

        Gnd = Pin(7, "GND", TerminalType.Ground);
        Vcc = Pin(14, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        // Output order: 1Q, 1Q', 2Q, 2Q'.
        ConfigurePins(
            [.. _flipFlops.SelectMany(f => new[] { f.Clear, f.Data, f.Clock, f.Preset })],
            [_flipFlops[0].Q, _flipFlops[0].QNot, _flipFlops[1].Q, _flipFlops[1].QNot]);
    }

    public override string PartNumber => "7474";

    /// <summary>Current Q state of one of the two flip-flops, indexed from 0.</summary>
    public LogicState QState(int index) => _flipFlops[index].State;

    /// <summary>The pins of one of the two flip-flops, indexed from 0.</summary>
    public (Terminal Clear, Terminal Data, Terminal Clock, Terminal Preset, Terminal Q, Terminal QNot) Section(int index)
    {
        var f = _flipFlops[index];
        return (f.Clear, f.Data, f.Clock, f.Preset, f.Q, f.QNot);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        for (var i = 0; i < _flipFlops.Length; i++)
        {
            var f = _flipFlops[i];
            var clear = context.ReadInput(f.Clear, Levels);
            var preset = context.ReadInput(f.Preset, Levels);
            var clock = context.ReadInput(f.Clock, Levels);

            // Preset and clear are asynchronous and active low, and they beat the clock.
            if (preset.IsLow() && clear.IsLow())
            {
                // Both asserted is the illegal state: the datasheet leaves both outputs high.
                f.State = LogicState.High;
                Drive(context, i, LogicState.High, LogicState.High);
                f.LastClock = clock;
                continue;
            }

            if (preset.IsLow())
            {
                f.State = LogicState.High;
            }
            else if (clear.IsLow())
            {
                f.State = LogicState.Low;
            }
            else if (f.LastClock.IsLow() && clock.IsHigh())
            {
                // Rising edge: latch D.
                f.State = context.ReadInput(f.Data, Levels);
            }

            f.LastClock = clock;
            Drive(context, i, f.State, f.State.Invert());
        }
    }

    private void Drive(IDigitalContext context, int section, LogicState q, LogicState qNot)
    {
        context.Schedule(this, section * 2, q, DelayFor(context));
        context.Schedule(this, section * 2 + 1, qNot, DelayFor(context));
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        foreach (var f in _flipFlops)
        {
            f.State = LogicState.Low;
            f.LastClock = LogicState.Unknown;
        }
    }
}
