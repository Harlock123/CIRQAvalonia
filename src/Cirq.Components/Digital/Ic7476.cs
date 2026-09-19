using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// Dual JK flip-flop with asynchronous preset and clear, triggered on the falling clock edge.
/// <para>
/// Pinout: 1=1CLK, 2=1PRE, 3=1CLR, 4=1J, 5=VCC, 6=2CLK, 7=2PRE, 8=2CLR,
/// 9=2J, 10=2Q', 11=2Q, 12=GND, 13=2K, 14=1Q', 15=1Q, 16=1K.
/// </para>
/// <para>
/// Modelled as the 74LS76, which is negative-edge triggered. The original bipolar 7476 is a
/// pulse-triggered master-slave part that captures data for the whole time the clock is high — a
/// distinction that matters if you feed it a wide pulse, and one worth knowing about rather than
/// silently papering over.
/// </para>
/// </summary>
public sealed class Ic7476 : DigitalIc
{
    private sealed class FlipFlop
    {
        public required Terminal Clock;
        public required Terminal Preset;
        public required Terminal Clear;
        public required Terminal J;
        public required Terminal K;
        public required Terminal Q;
        public required Terminal QNot;

        public LogicState LastClock = LogicState.Unknown;
        public LogicState State = LogicState.Low;
    }

    private readonly FlipFlop[] _flipFlops;

    public Ic7476() : base(16)
    {
        PropagationDelay = 16e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        var clock1 = Pin(1, "1CLK", TerminalType.Input);
        var preset1 = Pin(2, "1PRE", TerminalType.Input);
        var clear1 = Pin(3, "1CLR", TerminalType.Input);
        var j1 = Pin(4, "1J", TerminalType.Input);
        Vcc = Pin(5, "VCC", TerminalType.Power);
        var clock2 = Pin(6, "2CLK", TerminalType.Input);
        var preset2 = Pin(7, "2PRE", TerminalType.Input);
        var clear2 = Pin(8, "2CLR", TerminalType.Input);
        var j2 = Pin(9, "2J", TerminalType.Input);
        var qNot2 = Pin(10, "2Q'", TerminalType.Output);
        var q2 = Pin(11, "2Q", TerminalType.Output);
        Gnd = Pin(12, "GND", TerminalType.Ground);
        var k2 = Pin(13, "2K", TerminalType.Input);
        var qNot1 = Pin(14, "1Q'", TerminalType.Output);
        var q1 = Pin(15, "1Q", TerminalType.Output);
        var k1 = Pin(16, "1K", TerminalType.Input);

        _flipFlops =
        [
            new FlipFlop { Clock = clock1, Preset = preset1, Clear = clear1, J = j1, K = k1, Q = q1, QNot = qNot1 },
            new FlipFlop { Clock = clock2, Preset = preset2, Clear = clear2, J = j2, K = k2, Q = q2, QNot = qNot2 },
        ];

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [.. _flipFlops.SelectMany(f => new[] { f.Clock, f.Preset, f.Clear, f.J, f.K })],
            [q1, qNot1, q2, qNot2]);
    }

    public override string PartNumber => "7476";

    /// <summary>Current Q state of one of the two flip-flops, indexed from 0.</summary>
    public LogicState QState(int index) => _flipFlops[index].State;

    /// <summary>The pins of one of the two flip-flops, indexed from 0.</summary>
    public (Terminal Clock, Terminal Preset, Terminal Clear, Terminal J, Terminal K, Terminal Q, Terminal QNot)
        Section(int index)
    {
        var f = _flipFlops[index];
        return (f.Clock, f.Preset, f.Clear, f.J, f.K, f.Q, f.QNot);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        for (var i = 0; i < _flipFlops.Length; i++)
        {
            var f = _flipFlops[i];
            var preset = context.ReadInput(f.Preset, Levels);
            var clear = context.ReadInput(f.Clear, Levels);
            var clock = context.ReadInput(f.Clock, Levels);

            if (preset.IsLow() && clear.IsLow())
            {
                // Both asserted is the illegal state; the datasheet leaves both outputs high.
                f.State = LogicState.High;
                f.LastClock = clock;
                Drive(context, i, LogicState.High, LogicState.High);
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
            else if (f.LastClock.IsHigh() && clock.IsLow())
            {
                // Falling edge: apply the JK table.
                var j = context.ReadInput(f.J, Levels).IsHigh();
                var k = context.ReadInput(f.K, Levels).IsHigh();

                f.State = (j, k) switch
                {
                    (false, false) => f.State,                  // hold
                    (false, true) => LogicState.Low,            // reset
                    (true, false) => LogicState.High,           // set
                    (true, true) => f.State.Invert(),           // toggle
                };
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
