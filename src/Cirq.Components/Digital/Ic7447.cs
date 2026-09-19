using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// BCD to seven-segment decoder/driver with active-low outputs, intended to sink current from a
/// common-anode display.
/// <para>
/// Pinout: 1=B, 2=C, 3=LT, 4=BI/RBO, 5=RBI, 6=D, 7=A, 8=GND,
/// 9=e, 10=d, 11=c, 12=b, 13=a, 14=g, 15=f, 16=VCC.
/// </para>
/// <para>
/// Inputs 10-15 are not "don't care": the real part shows a specific set of partial glyphs for
/// them, reproduced here so a counter overrunning its range looks the way it does on a bench.
/// </para>
/// </summary>
public sealed class Ic7447 : DigitalIc
{
    /// <summary>
    /// Segment patterns indexed by BCD input, bit 6 = a through bit 0 = g. A set bit lights the
    /// segment; the outputs invert this because they are active low.
    /// </summary>
    private static readonly int[] SegmentPatterns =
    [
        0b1111110, // 0
        0b0110000, // 1
        0b1101101, // 2
        0b1111001, // 3
        0b0110011, // 4
        0b1011011, // 5
        0b1011111, // 6
        0b1110000, // 7
        0b1111111, // 8
        0b1111011, // 9
        0b0001101, // 10
        0b0011001, // 11
        0b0100011, // 12
        0b1001011, // 13
        0b0001111, // 14
        0b0000000, // 15 (blank)
    ];

    private readonly Terminal[] _segments = new Terminal[7];

    public Ic7447() : base(16)
    {
        PropagationDelay = 100e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        InputB = Pin(1, "B", TerminalType.Input);
        InputC = Pin(2, "C", TerminalType.Input);
        LampTest = Pin(3, "LT", TerminalType.Input);
        BlankingInput = Pin(4, "BI", TerminalType.Bidirectional);
        RippleBlankingInput = Pin(5, "RBI", TerminalType.Input);
        InputD = Pin(6, "D", TerminalType.Input);
        InputA = Pin(7, "A", TerminalType.Input);
        Gnd = Pin(8, "GND", TerminalType.Ground);

        _segments[4] = Pin(9, "e", TerminalType.Output);
        _segments[3] = Pin(10, "d", TerminalType.Output);
        _segments[2] = Pin(11, "c", TerminalType.Output);
        _segments[1] = Pin(12, "b", TerminalType.Output);
        _segments[0] = Pin(13, "a", TerminalType.Output);
        _segments[6] = Pin(14, "g", TerminalType.Output);
        _segments[5] = Pin(15, "f", TerminalType.Output);

        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [InputA, InputB, InputC, InputD, LampTest, BlankingInput, RippleBlankingInput],
            _segments);
    }

    public Terminal InputA { get; }
    public Terminal InputB { get; }
    public Terminal InputC { get; }
    public Terminal InputD { get; }

    /// <summary>Active low: lights every segment, for checking the display.</summary>
    public Terminal LampTest { get; }

    /// <summary>Active low: blanks every segment.</summary>
    public Terminal BlankingInput { get; }

    /// <summary>Active low: blanks the display when the input value is zero (leading-zero suppression).</summary>
    public Terminal RippleBlankingInput { get; }

    public override string PartNumber => "7447";

    /// <summary>The seven segment outputs in order a..g.</summary>
    public IReadOnlyList<Terminal> Segments => _segments;

    /// <summary>Segment output pin by name, e.g. "a".</summary>
    public Terminal Segment(char name) => _segments[name - 'a'];

    /// <summary>The BCD value currently presented on the inputs, or -1 while it is indeterminate.</summary>
    public int DecodedValue { get; private set; } = -1;

    /// <summary>The segment pattern the part is currently displaying, bit 6 = a.</summary>
    public int SegmentPattern { get; private set; }

    /// <summary>Segment pattern for a BCD value, exposed so tests and the display can agree.</summary>
    public static int PatternFor(int value) => SegmentPatterns[value & 0xF];

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var lampTest = context.ReadInput(LampTest, Levels);
        var blanking = context.ReadInput(BlankingInput, Levels);
        var rippleBlank = context.ReadInput(RippleBlankingInput, Levels);

        int pattern;

        if (blanking.IsLow())
        {
            // Blanking beats everything, including lamp test.
            pattern = 0;
            DecodedValue = -1;
        }
        else if (lampTest.IsLow())
        {
            pattern = 0b1111111;
            DecodedValue = -1;
        }
        else
        {
            var value = ReadBcd(context);
            DecodedValue = value;

            // Ripple blanking suppresses a leading zero.
            pattern = value == 0 && rippleBlank.IsLow() ? 0 : SegmentPatterns[value];
        }

        SegmentPattern = pattern;

        var delay = DelayFor(context);
        for (var i = 0; i < 7; i++)
        {
            // The outputs are open-collector current sinks, not push-pull drivers: a lit segment
            // is pulled down, and an unlit one is released rather than driven high. Driving it
            // high would leave a volt or so across the segment and light it faintly.
            var lit = (pattern & (1 << (6 - i))) != 0;
            context.Schedule(this, i, lit ? LogicState.Low : LogicState.HighImpedance, delay);
        }
    }

    private int ReadBcd(IDigitalContext context)
    {
        var value = 0;
        if (context.ReadInput(InputA, Levels).IsHigh()) value |= 1;
        if (context.ReadInput(InputB, Levels).IsHigh()) value |= 2;
        if (context.ReadInput(InputC, Levels).IsHigh()) value |= 4;
        if (context.ReadInput(InputD, Levels).IsHigh()) value |= 8;
        return value;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        DecodedValue = -1;
        SegmentPattern = 0;
    }
}
