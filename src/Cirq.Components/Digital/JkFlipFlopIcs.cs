using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for the dual JK flip-flop packages — the 7473, 7476, 74107, 74109, 74112 and the CMOS 4027.
/// They are one flip-flop in different clothes, and a part is its pin table and four choices:
/// <list type="bullet">
/// <item>which clock edge it acts on (falling for the TTL parts here, rising for the 74109 and 4027);</item>
/// <item>whether it has a preset, a clear, both or neither, and whether they are active low (TTL) or
/// active high (the 4027's set and reset);</item>
/// <item>whether K arrives inverted, as on the 74109's J-K̄ inputs, where J and K̄ tied together make a
/// D flip-flop;</item>
/// <item>its logic family.</item>
/// </list>
/// <para>
/// On the edge: J sets, K resets, both toggle, neither holds. Preset and clear are asynchronous and
/// beat the clock; both asserted at once is the illegal state, and the datasheets leave both
/// outputs high, so that is what happens here.
/// </para>
/// </summary>
public abstract class DualJkFlipFlopIc : DigitalIc
{
    /// <summary>Pin numbers of one flip-flop. A part without a preset or clear leaves it null.</summary>
    protected sealed record JkPinout(int Clock, int J, int K, int Q, int QNot, int? Preset = null, int? Clear = null);

    private sealed class FlipFlop
    {
        public required Terminal Clock;
        public required Terminal J;
        public required Terminal K;
        public required Terminal Q;
        public required Terminal QNot;
        public Terminal? Preset;
        public Terminal? Clear;

        public LogicState LastClock = LogicState.Unknown;
        public LogicState State = LogicState.Low;
    }

    private readonly FlipFlop[] _flipFlops;

    /// <param name="risingEdge">Acts on the rising clock edge rather than the falling one.</param>
    /// <param name="invertedK">K is the K̄ input of the 74109: low means "reset".</param>
    /// <param name="cmos">The 4027: CMOS levels, active-high set and reset, VDD and VSS.</param>
    protected DualJkFlipFlopIc(
        int pinCount, int vccPin, int gndPin, JkPinout first, JkPinout second,
        bool risingEdge, bool invertedK = false, bool cmos = false)
        : base(pinCount)
    {
        IsRisingEdge = risingEdge;
        HasInvertedK = invertedK;
        AsyncActiveHigh = cmos;

        if (cmos)
        {
            Levels = LogicLevels.Cmos5V;
            PropagationDelay = 150e-9;
        }
        else
        {
            PropagationDelay = 16e-9;
        }

        var pins = new Terminal[pinCount + 1];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, pinCount));

        FlipFlop Build(JkPinout p, int n) => new()
        {
            Clock = Pin(p.Clock, $"{n}CLK", TerminalType.Input),
            J = Pin(p.J, $"{n}J", TerminalType.Input),
            K = Pin(p.K, invertedK ? $"{n}K'" : $"{n}K", TerminalType.Input),
            Q = Pin(p.Q, $"{n}Q", TerminalType.Output),
            QNot = Pin(p.QNot, $"{n}Q'", TerminalType.Output),
            Preset = p.Preset is { } pre ? Pin(pre, cmos ? $"{n}SET" : $"{n}PRE", TerminalType.Input) : null,
            Clear = p.Clear is { } clr ? Pin(clr, cmos ? $"{n}RST" : $"{n}CLR", TerminalType.Input) : null,
        };

        _flipFlops = [Build(first, 1), Build(second, 2)];

        Gnd = Pin(gndPin, cmos ? "VSS" : "GND", TerminalType.Ground);
        Vcc = Pin(vccPin, cmos ? "VDD" : "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        // Output order: 1Q, 1Q', 2Q, 2Q'.
        ConfigurePins(
            [.. _flipFlops.SelectMany(f => new[] { f.Clock, f.Preset, f.Clear, f.J, f.K }).OfType<Terminal>()],
            [_flipFlops[0].Q, _flipFlops[0].QNot, _flipFlops[1].Q, _flipFlops[1].QNot]);
    }

    /// <summary>True for the 74109 and the 4027, which act on the rising edge.</summary>
    public bool IsRisingEdge { get; }

    /// <summary>True for the 74109, whose K input is K̄.</summary>
    public bool HasInvertedK { get; }

    /// <summary>True for the 4027, whose set and reset are active high.</summary>
    public bool AsyncActiveHigh { get; }

    /// <summary>Current Q state of one of the two flip-flops, indexed from 0.</summary>
    public LogicState QState(int index) => _flipFlops[index].State;

    /// <summary>The pins of one of the two flip-flops; preset and clear are null on a part without them.</summary>
    public (Terminal Clock, Terminal? Preset, Terminal? Clear, Terminal J, Terminal K, Terminal Q, Terminal QNot)
        Pins(int index)
    {
        var f = _flipFlops[index];
        return (f.Clock, f.Preset, f.Clear, f.J, f.K, f.Q, f.QNot);
    }

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        bool Asserted(Terminal? pin)
        {
            if (pin is null) return false;
            var level = context.ReadInput(pin, Levels);
            return AsyncActiveHigh ? level.IsHigh() : level.IsLow();
        }

        for (var i = 0; i < _flipFlops.Length; i++)
        {
            var f = _flipFlops[i];
            var preset = Asserted(f.Preset);
            var clear = Asserted(f.Clear);
            var clock = context.ReadInput(f.Clock, Levels);

            if (preset && clear)
            {
                // Both asserted is the illegal state; the datasheets leave both outputs high.
                f.State = LogicState.High;
                f.LastClock = clock;
                Drive(context, i, LogicState.High, LogicState.High);
                continue;
            }

            var edge = IsRisingEdge
                ? f.LastClock.IsLow() && clock.IsHigh()
                : f.LastClock.IsHigh() && clock.IsLow();

            if (preset)
            {
                f.State = LogicState.High;
            }
            else if (clear)
            {
                f.State = LogicState.Low;
            }
            else if (edge)
            {
                var j = context.ReadInput(f.J, Levels).IsHigh();
                var k = context.ReadInput(f.K, Levels).IsHigh() ^ HasInvertedK;

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
/// silently papering over. The same is true of the 7473 and 74107 beside it.
/// </para>
/// </summary>
public sealed class Ic7476() : DualJkFlipFlopIc(16, vccPin: 5, gndPin: 12,
    new JkPinout(Clock: 1, J: 4, K: 16, Q: 15, QNot: 14, Preset: 2, Clear: 3),
    new JkPinout(Clock: 6, J: 9, K: 13, Q: 11, QNot: 10, Preset: 7, Clear: 8),
    risingEdge: false)
{
    public override string PartNumber => "7476";

    /// <summary>The pins of one of the two flip-flops, indexed from 0.</summary>
    public (Terminal Clock, Terminal Preset, Terminal Clear, Terminal J, Terminal K, Terminal Q, Terminal QNot)
        Section(int index)
    {
        var (clock, preset, clear, j, k, q, qNot) = Pins(index);
        return (clock, preset!, clear!, j, k, q, qNot);
    }
}

/// <summary>
/// 7473 dual JK flip-flop with clear and no preset, on the falling edge — and on a 14-pin package
/// with its supply in the middle of the sides, VCC on pin 4 and GND on pin 11, as the 7476's is.
/// Put it in a socket wired for a 7400 and it is powered across the wrong pins.
/// <para>
/// Pinout: 1=1CLK, 2=1CLR, 3=1K, 4=VCC, 5=2CLK, 6=2CLR, 7=2J, 8=2Q', 9=2Q, 10=2K, 11=GND,
/// 12=1Q, 13=1Q', 14=1J.
/// </para>
/// </summary>
public sealed class Ic7473() : DualJkFlipFlopIc(14, vccPin: 4, gndPin: 11,
    new JkPinout(Clock: 1, J: 14, K: 3, Q: 12, QNot: 13, Clear: 2),
    new JkPinout(Clock: 5, J: 7, K: 10, Q: 9, QNot: 8, Clear: 6),
    risingEdge: false)
{
    public override string PartNumber => "7473";
}

/// <summary>
/// 74107 dual JK flip-flop with clear, on the falling edge: the 7473's logic with its supply on the
/// corners, VCC 14 and GND 7.
/// <para>
/// Pinout: 1=1J, 2=1Q', 3=1Q, 4=1K, 5=2Q, 6=2Q', 7=GND, 8=2J, 9=2CLK, 10=2CLR, 11=2K, 12=1CLK,
/// 13=1CLR, 14=VCC.
/// </para>
/// </summary>
public sealed class Ic74107() : DualJkFlipFlopIc(14, vccPin: 14, gndPin: 7,
    new JkPinout(Clock: 12, J: 1, K: 4, Q: 3, QNot: 2, Clear: 13),
    new JkPinout(Clock: 9, J: 8, K: 11, Q: 5, QNot: 6, Clear: 10),
    risingEdge: false)
{
    public override string PartNumber => "74107";
}

/// <summary>
/// 74109 dual J-K̄ flip-flop with preset and clear, on the <b>rising</b> edge. The K input is inverted:
/// J high and K̄ low toggles, J low and K̄ high holds. Tie J and K̄ together and it is a D flip-flop,
/// which is why it is the JK part people reach for in synchronous designs.
/// <para>
/// Pinout: 1=1CLR, 2=1J, 3=1K', 4=1CLK, 5=1PRE, 6=1Q, 7=1Q', 8=GND, 9=2Q', 10=2Q, 11=2PRE, 12=2CLK,
/// 13=2K', 14=2J, 15=2CLR, 16=VCC.
/// </para>
/// </summary>
public sealed class Ic74109() : DualJkFlipFlopIc(16, vccPin: 16, gndPin: 8,
    new JkPinout(Clock: 4, J: 2, K: 3, Q: 6, QNot: 7, Preset: 5, Clear: 1),
    new JkPinout(Clock: 12, J: 14, K: 13, Q: 10, QNot: 9, Preset: 11, Clear: 15),
    risingEdge: true, invertedK: true)
{
    public override string PartNumber => "74109";
}

/// <summary>
/// 74112 dual JK flip-flop with preset and clear, on the falling edge — the 7476's logic with the
/// supply on the corners.
/// <para>
/// Pinout: 1=1CLK, 2=1K, 3=1J, 4=1PRE, 5=1Q, 6=1Q', 7=2Q', 8=GND, 9=2Q, 10=2PRE, 11=2J, 12=2K,
/// 13=2CLK, 14=2CLR, 15=1CLR, 16=VCC.
/// </para>
/// </summary>
public sealed class Ic74112() : DualJkFlipFlopIc(16, vccPin: 16, gndPin: 8,
    new JkPinout(Clock: 1, J: 3, K: 2, Q: 5, QNot: 6, Preset: 4, Clear: 15),
    new JkPinout(Clock: 13, J: 11, K: 12, Q: 9, QNot: 7, Preset: 10, Clear: 14),
    risingEdge: false)
{
    public override string PartNumber => "74112";
}

/// <summary>
/// 4027 dual JK flip-flop, CMOS, on the rising edge, with set and reset that are — as on the 4013 —
/// <b>active high</b>. Tie them low or the flip-flop sits wherever the noise puts it.
/// <para>
/// Pinout: 1=1Q, 2=1Q', 3=1CLK, 4=1RST, 5=1K, 6=1J, 7=1SET, 8=VSS, 9=2SET, 10=2J, 11=2K, 12=2RST,
/// 13=2CLK, 14=2Q', 15=2Q, 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4027() : DualJkFlipFlopIc(16, vccPin: 16, gndPin: 8,
    new JkPinout(Clock: 3, J: 6, K: 5, Q: 1, QNot: 2, Preset: 7, Clear: 4),
    new JkPinout(Clock: 13, J: 10, K: 11, Q: 15, QNot: 14, Preset: 9, Clear: 12),
    risingEdge: true, cmos: true)
{
    public override string PartNumber => "4027";
}
