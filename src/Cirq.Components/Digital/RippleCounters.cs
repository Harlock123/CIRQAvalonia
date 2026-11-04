using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// The two stages inside a TTL ripple counter: a divide-by-two flip-flop (CKA to QA) and a second
/// stage that divides by five (the 7490 and 74390) or by eight (the 7493 and 74393), QB to QD. Both
/// count on the <b>falling</b> edge of their clock. Wire QA to CKB and the pair count in BCD or binary;
/// on the 74393 that wire is inside the package.
/// <para>
/// This is the state and the edge-detection only, shared by the four packages; each decides which
/// pins clock it and which clear it.
/// </para>
/// </summary>
internal sealed class RippleStages(int secondModulus)
{
    private LogicState _lastA = LogicState.Unknown;
    private LogicState _lastB = LogicState.Unknown;

    /// <summary>QA, the divide-by-two stage.</summary>
    public int First { get; set; }

    /// <summary>The second stage, 0 to its modulus less one, on QB to QD in binary.</summary>
    public int Second { get; set; }

    /// <summary>QD..QA as a number: QA in bit 0, the second stage above it.</summary>
    public int Value => First | (Second << 1);

    /// <summary>Advances whichever stage saw a falling edge on its own clock.</summary>
    public void Clock(LogicState a, LogicState b)
    {
        if (_lastA.IsHigh() && a.IsLow()) First ^= 1;
        if (_lastB.IsHigh() && b.IsLow()) Second = (Second + 1) % secondModulus;
        Track(a, b);
    }

    /// <summary>
    /// The 74393's arrangement, QA clocking the second stage inside the package: a falling edge on
    /// the one clock toggles QA, and QA falling carries into the second stage.
    /// </summary>
    public void ClockChained(LogicState a)
    {
        if (_lastA.IsHigh() && a.IsLow())
        {
            First ^= 1;
            if (First == 0) Second = (Second + 1) % secondModulus;
        }

        _lastA = a;
    }

    /// <summary>Remembers the clock levels without counting, while a reset holds the stages.</summary>
    public void Track(LogicState a, LogicState b)
    {
        _lastA = a;
        _lastB = b;
    }

    public void Clear()
    {
        First = 0;
        Second = 0;
    }

    public void Reset()
    {
        Clear();
        _lastA = LogicState.Unknown;
        _lastB = LogicState.Unknown;
    }

    /// <summary>Schedules QA to QD on four consecutive outputs from <paramref name="first"/>.</summary>
    public void Drive(DigitalComponent device, IDigitalContext context, int first, double delay)
    {
        var value = Value;
        for (var i = 0; i < 4; i++)
            context.Schedule(device, first + i, LogicStateExtensions.FromBool((value & (1 << i)) != 0), delay);
    }
}

/// <summary>
/// Decade counter: an independent divide-by-two stage (CKA to QA) and a divide-by-five stage
/// (CKB to QB/QC/QD). Wiring QA into CKB gives the usual BCD 0-9 sequence. Both clocks are
/// negative-edge triggered.
/// <para>
/// Pinout: 1=CKB, 2=R0(1), 3=R0(2), 4=NC, 5=VCC, 6=R9(1), 7=R9(2),
/// 8=QC, 9=QB, 10=GND, 11=QD, 12=QA, 13=NC, 14=CKA.
/// </para>
/// </summary>
public sealed class Ic7490 : DigitalIc
{
    private readonly RippleStages _stages = new(5);

    public Ic7490() : base(14)
    {
        PropagationDelay = 16e-9;

        var pins = new Terminal[15];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 14));

        ClockB = Pin(1, "CKB", TerminalType.Input);
        Reset0A = Pin(2, "R0(1)", TerminalType.Input);
        Reset0B = Pin(3, "R0(2)", TerminalType.Input);
        Pin(4, "NC", TerminalType.Passive);
        Vcc = Pin(5, "VCC", TerminalType.Power);
        Reset9A = Pin(6, "R9(1)", TerminalType.Input);
        Reset9B = Pin(7, "R9(2)", TerminalType.Input);
        Qc = Pin(8, "QC", TerminalType.Output);
        Qb = Pin(9, "QB", TerminalType.Output);
        Gnd = Pin(10, "GND", TerminalType.Ground);
        Qd = Pin(11, "QD", TerminalType.Output);
        Qa = Pin(12, "QA", TerminalType.Output);
        Pin(13, "NC", TerminalType.Passive);
        ClockA = Pin(14, "CKA", TerminalType.Input);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [ClockA, ClockB, Reset0A, Reset0B, Reset9A, Reset9B],
            [Qa, Qb, Qc, Qd]);
    }

    public Terminal ClockA { get; }
    public Terminal ClockB { get; }
    public Terminal Reset0A { get; }
    public Terminal Reset0B { get; }
    public Terminal Reset9A { get; }
    public Terminal Reset9B { get; }
    public Terminal Qa { get; }
    public Terminal Qb { get; }
    public Terminal Qc { get; }
    public Terminal Qd { get; }

    public override string PartNumber => "7490";

    /// <summary>
    /// The BCD value on QD..QA. Only meaningful in the usual configuration where QA drives CKB.
    /// </summary>
    public int Count => _stages.Value;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var r0 = context.ReadInput(Reset0A, Levels).IsHigh() && context.ReadInput(Reset0B, Levels).IsHigh();
        var r9 = context.ReadInput(Reset9A, Levels).IsHigh() && context.ReadInput(Reset9B, Levels).IsHigh();

        var clockA = context.ReadInput(ClockA, Levels);
        var clockB = context.ReadInput(ClockB, Levels);

        // Set-to-nine overrides reset-to-zero, and both override counting.
        if (r9)
        {
            _stages.First = 1;
            _stages.Second = 4;
            _stages.Track(clockA, clockB);
        }
        else if (r0)
        {
            _stages.Clear();
            _stages.Track(clockA, clockB);
        }
        else
        {
            _stages.Clock(clockA, clockB);
        }

        _stages.Drive(this, context, 0, DelayFor(context));
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _stages.Reset();
    }
}

/// <summary>
/// 7493 four-bit binary ripple counter: the 7490's arrangement with a divide-by-eight second stage, so
/// QA wired to CKB counts 0 to 15. Two reset pins, both high to clear; there is no set-to-nine.
/// <para>
/// Its supply is on pins 5 and 10, not the corners, and pins 4, 6, 7 and 13 are not connected — wired
/// as if it were a 14-pin part with VCC on 14, it is not powered at all, and pin 14 is CKA.
/// </para>
/// <para>
/// Pinout: 1=CKB, 2=R0(1), 3=R0(2), 4=NC, 5=VCC, 6=NC, 7=NC, 8=QC, 9=QB, 10=GND, 11=QD, 12=QA,
/// 13=NC, 14=CKA.
/// </para>
/// </summary>
public sealed class Ic7493 : DigitalIc
{
    private readonly RippleStages _stages = new(8);

    public Ic7493() : base(14)
    {
        PropagationDelay = 16e-9;

        var pins = new Terminal[15];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 14));

        ClockB = Pin(1, "CKB", TerminalType.Input);
        Reset0A = Pin(2, "R0(1)", TerminalType.Input);
        Reset0B = Pin(3, "R0(2)", TerminalType.Input);
        foreach (var nc in new[] { 4, 6, 7, 13 }) Pin(nc, "NC", TerminalType.Passive);
        Vcc = Pin(5, "VCC", TerminalType.Power);
        Qc = Pin(8, "QC", TerminalType.Output);
        Qb = Pin(9, "QB", TerminalType.Output);
        Gnd = Pin(10, "GND", TerminalType.Ground);
        Qd = Pin(11, "QD", TerminalType.Output);
        Qa = Pin(12, "QA", TerminalType.Output);
        ClockA = Pin(14, "CKA", TerminalType.Input);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([ClockA, ClockB, Reset0A, Reset0B], [Qa, Qb, Qc, Qd]);
    }

    public Terminal ClockA { get; }
    public Terminal ClockB { get; }
    public Terminal Reset0A { get; }
    public Terminal Reset0B { get; }
    public Terminal Qa { get; }
    public Terminal Qb { get; }
    public Terminal Qc { get; }
    public Terminal Qd { get; }

    /// <summary>QA to QD.</summary>
    public IReadOnlyList<Terminal> Outputs => [Qa, Qb, Qc, Qd];

    public override string PartNumber => "7493";

    /// <summary>The value on QD..QA, 0 to 15 with QA driving CKB.</summary>
    public int Count => _stages.Value;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var clockA = context.ReadInput(ClockA, Levels);
        var clockB = context.ReadInput(ClockB, Levels);

        if (context.ReadInput(Reset0A, Levels).IsHigh() && context.ReadInput(Reset0B, Levels).IsHigh())
        {
            _stages.Clear();
            _stages.Track(clockA, clockB);
        }
        else
        {
            _stages.Clock(clockA, clockB);
        }

        _stages.Drive(this, context, 0, DelayFor(context));
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _stages.Reset();
    }
}

/// <summary>
/// Base for the dual ripple counters, the 74390 (two decade counters, each a ÷2 and a ÷5 with their
/// own clocks, like a pair of 7490s) and the 74393 (two four-bit binary counters, each chained inside
/// the package). Each half has a clear that is active <b>high</b> and asynchronous.
/// </summary>
public abstract class DualRippleCounterIc : DigitalIc
{
    private readonly RippleStages[] _stages;
    private readonly Terminal[] _clockA = new Terminal[2];
    private readonly Terminal?[] _clockB = new Terminal?[2];
    private readonly Terminal[] _clear = new Terminal[2];
    private readonly Terminal[][] _q = [new Terminal[4], new Terminal[4]];

    /// <param name="pinout">
    /// Per half: CKA, CKB (or 0 when chained inside), CLR, QA, QB, QC, QD.
    /// </param>
    protected DualRippleCounterIc(int pinCount, int secondModulus, int[][] pinout, int vccPin, int gndPin)
        : base(pinCount)
    {
        PropagationDelay = 18e-9;
        _stages = [new RippleStages(secondModulus), new RippleStages(secondModulus)];

        var pins = new Terminal[pinCount + 1];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, pinCount));

        for (var half = 0; half < 2; half++)
        {
            var p = pinout[half];
            var n = half + 1;
            var chained = p[1] == 0;

            _clockA[half] = Pin(p[0], chained ? $"{n}CLK" : $"{n}CKA", TerminalType.Input);
            if (!chained) _clockB[half] = Pin(p[1], $"{n}CKB", TerminalType.Input);
            _clear[half] = Pin(p[2], $"{n}CLR", TerminalType.Input);
            for (var i = 0; i < 4; i++) _q[half][i] = Pin(p[3 + i], $"{n}Q{(char)('A' + i)}", TerminalType.Output);
        }

        Gnd = Pin(gndPin, "GND", TerminalType.Ground);
        Vcc = Pin(vccPin, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _clockA, .. _clockB.OfType<Terminal>(), .. _clear], [.. _q[0], .. _q[1]]);
    }

    /// <summary>A half's first clock: CKA on the 74390, the only clock on the 74393.</summary>
    public Terminal ClockA(int half) => _clockA[half];

    /// <summary>A half's second-stage clock on the 74390; null on the 74393, where it is inside.</summary>
    public Terminal? ClockB(int half) => _clockB[half];

    /// <summary>A half's clear, active high.</summary>
    public Terminal Clear(int half) => _clear[half];

    /// <summary>A half's QA to QD.</summary>
    public IReadOnlyList<Terminal> Outputs(int half) => _q[half];

    /// <summary>The value on a half's QD..QA.</summary>
    public int Count(int half) => _stages[half].Value;

    public override string ValueLabel => $"{Count(0)} / {Count(1)}";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        for (var half = 0; half < 2; half++)
        {
            var stages = _stages[half];
            var a = context.ReadInput(_clockA[half], Levels);
            var b = _clockB[half] is { } pin ? context.ReadInput(pin, Levels) : LogicState.Unknown;

            if (context.ReadInput(_clear[half], Levels).IsHigh())
            {
                stages.Clear();
                stages.Track(a, b);
            }
            else if (_clockB[half] is null)
            {
                stages.ClockChained(a);
            }
            else
            {
                stages.Clock(a, b);
            }

            stages.Drive(this, context, half * 4, delay);
        }

        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        foreach (var s in _stages) s.Reset();
    }
}

/// <summary>
/// 74390 dual decade counter: two 7490s without the set-to-nine, each a ÷2 (CKA to QA) and a ÷5 (CKB
/// to QB-QD). QA into CKB of the same half counts BCD; QD of one half into CKA of the other counts to
/// 99. Pinout: 1=1CKA, 2=1CLR, 3=1QA, 4=1CKB, 5=1QB, 6=1QC, 7=1QD, 8=GND, 9=2QD, 10=2QC, 11=2QB,
/// 12=2CKB, 13=2QA, 14=2CLR, 15=2CKA, 16=VCC.
/// </summary>
public sealed class Ic74390() : DualRippleCounterIc(16, 5,
    [[1, 4, 2, 3, 5, 6, 7], [15, 12, 14, 13, 11, 10, 9]], vccPin: 16, gndPin: 8)
{
    public override string PartNumber => "74390";
}

/// <summary>
/// 74393 dual four-bit binary ripple counter, each half chained inside the package: one clock, a clear,
/// and QA to QD counting 0 to 15 on the falling edge. Two halves in series divide by 256. Pinout: 1=1CLK,
/// 2=1CLR, 3=1QA, 4=1QB, 5=1QC, 6=1QD, 7=GND, 8=2QD, 9=2QC, 10=2QB, 11=2QA, 12=2CLR, 13=2CLK, 14=VCC.
/// </summary>
public sealed class Ic74393() : DualRippleCounterIc(14, 8,
    [[1, 0, 2, 3, 4, 5, 6], [13, 0, 12, 11, 10, 9, 8]], vccPin: 14, gndPin: 7)
{
    public override string PartNumber => "74393";
}
