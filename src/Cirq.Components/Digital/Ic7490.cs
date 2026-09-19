using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

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
    private LogicState _lastClockA = LogicState.Unknown;
    private LogicState _lastClockB = LogicState.Unknown;

    /// <summary>Divide-by-two stage output (QA).</summary>
    private int _divideByTwo;

    /// <summary>Divide-by-five stage state, 0..4, mapped onto QB/QC/QD.</summary>
    private int _divideByFive;

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
    public int Count => _divideByTwo | (DivideByFiveBits() << 1);

    /// <summary>QB, QC and QD as a 3-bit value.</summary>
    private int DivideByFiveBits() => _divideByFive switch
    {
        0 => 0b000,
        1 => 0b001,
        2 => 0b010,
        3 => 0b011,
        _ => 0b100,
    };

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var r0 = context.ReadInput(Reset0A, Levels).IsHigh() && context.ReadInput(Reset0B, Levels).IsHigh();
        var r9 = context.ReadInput(Reset9A, Levels).IsHigh() && context.ReadInput(Reset9B, Levels).IsHigh();

        var clockA = context.ReadInput(ClockA, Levels);
        var clockB = context.ReadInput(ClockB, Levels);

        // Set-to-nine overrides reset-to-zero, and both override counting.
        if (r9)
        {
            _divideByTwo = 1;
            _divideByFive = 4;
        }
        else if (r0)
        {
            _divideByTwo = 0;
            _divideByFive = 0;
        }
        else
        {
            if (_lastClockA.IsHigh() && clockA.IsLow()) _divideByTwo ^= 1;
            if (_lastClockB.IsHigh() && clockB.IsLow()) _divideByFive = (_divideByFive + 1) % 5;
        }

        _lastClockA = clockA;
        _lastClockB = clockB;

        var bits = DivideByFiveBits();
        var delay = DelayFor(context);
        context.Schedule(this, 0, LogicStateExtensions.FromBool(_divideByTwo != 0), delay);
        context.Schedule(this, 1, LogicStateExtensions.FromBool((bits & 0b001) != 0), delay);
        context.Schedule(this, 2, LogicStateExtensions.FromBool((bits & 0b010) != 0), delay);
        context.Schedule(this, 3, LogicStateExtensions.FromBool((bits & 0b100) != 0), delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _divideByTwo = 0;
        _divideByFive = 0;
        _lastClockA = LogicState.Unknown;
        _lastClockB = LogicState.Unknown;
    }
}
