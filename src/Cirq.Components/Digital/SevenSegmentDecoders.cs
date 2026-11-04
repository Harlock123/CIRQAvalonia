using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// The seven-segment digits, as patterns with bit 6 for segment a down to bit 0 for g. Two sets,
/// because the decoders disagree about two digits: the 7447, 7448, 4026 and 4033 draw a 6 without its
/// top bar and a 9 without its bottom one, and the 74247 and 4543 add those "tails".
/// </summary>
public static class SevenSegmentGlyphs
{
    private static readonly int[] Tailless =
    [
        0b1111110, 0b0110000, 0b1101101, 0b1111001, 0b0110011,
        0b1011011, 0b0011111, 0b1110000, 0b1111111, 0b1110011,
    ];

    /// <summary>The pattern for a digit 0-9, with or without the tails on 6 and 9.</summary>
    public static int For(int digit, bool tails) => (tails, digit) switch
    {
        (true, 6) => 0b1011111,
        (true, 9) => 0b1111011,
        _ => Tailless[digit],
    };

    /// <summary>The digit a pattern spells, either way it draws 6 and 9, or null.</summary>
    public static int? DigitOf(int pattern)
    {
        for (var digit = 0; digit <= 9; digit++)
            if (For(digit, tails: false) == pattern || For(digit, tails: true) == pattern)
                return digit;

        return null;
    }
}

/// <summary>
/// Base for the TTL BCD to seven-segment decoder/drivers on one pinout: the 7447 and 74247, whose
/// outputs are open-collector sinks for a common-anode display, and the 7448, whose outputs are active
/// high with a 2 kΩ pull-up to VCC inside on each — enough to drive a common-cathode display's
/// segments through a buffer, or the inputs of something else. The 74247 is the 7447 with tails on its
/// 6 and 9.
/// <para>
/// Pinout: 1=B, 2=C, 3=LT, 4=BI/RBO, 5=RBI, 6=D, 7=A, 8=GND,
/// 9=e, 10=d, 11=c, 12=b, 13=a, 14=g, 15=f, 16=VCC.
/// </para>
/// <para>
/// Inputs 10-15 are not "don't care": the real parts show a specific set of partial glyphs for them,
/// reproduced here so a counter overrunning its range looks the way it does on a bench. BI/RBO is
/// taken as an input only; driving it as the ripple-blanking output is not modelled.
/// </para>
/// </summary>
public abstract class TtlSevenSegmentDecoderIc : DigitalIc
{
    /// <summary>The datasheet's partial glyphs for 10 to 15 (15 blank), bit 6 = a.</summary>
    private static readonly int[] Overrange = [0b0001101, 0b0011001, 0b0100011, 0b1001011, 0b0001111, 0b0000000];

    /// <summary>Each of the 7448's internal output pull-ups, in ohms.</summary>
    public const double PullUpResistance = 2e3;

    private readonly Terminal[] _segments = new Terminal[7];
    private readonly bool _tails;

    /// <param name="tails">Draw 6 and 9 with tails, as the 74247 does.</param>
    /// <param name="activeHigh">The 7448: a lit segment is released to its pull-up, an unlit one pulled low.</param>
    protected TtlSevenSegmentDecoderIc(bool tails, bool activeHigh) : base(16)
    {
        _tails = tails;
        IsActiveHigh = activeHigh;
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

    /// <summary>True for the 7448, whose lit segments are high.</summary>
    public bool IsActiveHigh { get; }

    /// <summary>The seven segment outputs in order a..g.</summary>
    public IReadOnlyList<Terminal> Segments => _segments;

    /// <summary>Segment output pin by name, e.g. "a".</summary>
    public Terminal Segment(char name) => _segments[name - 'a'];

    /// <summary>The BCD value currently presented on the inputs, or -1 while it is indeterminate.</summary>
    public int DecodedValue { get; private set; } = -1;

    /// <summary>The segment pattern the part is currently displaying, bit 6 = a.</summary>
    public int SegmentPattern { get; private set; }

    /// <summary>The pattern this part shows for a value 0-15, bit 6 = a.</summary>
    public int Glyph(int value) => GlyphFor(value, _tails);

    /// <summary>The pattern a TTL decoder shows for a value 0-15, with or without the tails.</summary>
    protected static int GlyphFor(int value, bool tails) =>
        (value & 0xF) < 10 ? SevenSegmentGlyphs.For(value & 0xF, tails) : Overrange[(value & 0xF) - 10];

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        if (!IsActiveHigh) return;

        // The 7448's pull-ups are always there: a lit segment is the output transistor letting go of
        // one, and an unlit one is the transistor sinking its current as well as holding the pin low.
        var vcc = system.Node(Vcc);
        foreach (var segment in _segments)
            system.StampResistor(system.Node(segment), vcc, PullUpResistance);
    }

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
            pattern = value == 0 && rippleBlank.IsLow() ? 0 : Glyph(value);
        }

        SegmentPattern = pattern;

        var delay = DelayFor(context);
        for (var i = 0; i < 7; i++)
        {
            // The outputs are open-collector, not push-pull: on the 7447 a lit segment is pulled down
            // and an unlit one is released rather than driven high, which would leave a volt or so
            // across the segment and light it faintly. On the 7448 it is the other way up, the pull-up
            // doing the lighting.
            var lit = (pattern & (1 << (6 - i))) != 0;
            var state = lit != IsActiveHigh ? LogicState.Low : LogicState.HighImpedance;
            context.Schedule(this, i, state, delay);
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

/// <summary>
/// 7447 BCD to seven-segment decoder/driver with open-collector, active-low outputs, to sink current
/// from a common-anode display. Its 6 has no top bar and its 9 no bottom one.
/// </summary>
public sealed class Ic7447() : TtlSevenSegmentDecoderIc(tails: false, activeHigh: false)
{
    public override string PartNumber => "7447";

    /// <summary>Segment pattern for a BCD value, exposed so tests and the display can agree.</summary>
    public static int PatternFor(int value) => GlyphFor(value, tails: false);
}

/// <summary>
/// 74247: the 7447 with tails — a 6 with its top bar and a 9 with its bottom one, which is the only
/// difference, and the reason it exists.
/// </summary>
public sealed class Ic74247() : TtlSevenSegmentDecoderIc(tails: true, activeHigh: false)
{
    public override string PartNumber => "74247";
}

/// <summary>
/// 7448: the 7447's decoder with active-high outputs, each pulled up to VCC by 2 kΩ inside — for a
/// common-cathode display through a buffer, or to feed logic. The pull-up is the only thing lighting a
/// segment, so a display hung straight on the outputs is dim.
/// </summary>
public sealed class Ic7448() : TtlSevenSegmentDecoderIc(tails: false, activeHigh: true)
{
    public override string PartNumber => "7448";
}

/// <summary>
/// 4543 BCD to seven-segment latch, decoder and driver for liquid-crystal displays — and for LEDs of
/// either polarity, because of its PHASE input: every segment output is the decoded segment
/// exclusive-ORed with PH.
/// <para>
/// An LCD must never see DC, so its backplane is driven with a square wave and the same wave goes to
/// PH: a lit segment is then out of phase with the backplane, an unlit one in phase, and the average
/// across every segment is zero. Tie PH low for a common-cathode LED display, high for a common-anode
/// one. LD high lets the inputs through; low latches them. BI high blanks, and codes above 9 blank too.
/// </para>
/// <para>
/// Pinout: 1=LD, 2=C, 3=B, 4=D, 5=A, 6=PH, 7=BI, 8=VSS, 9=a, 10=b, 11=c, 12=d, 13=e, 14=g, 15=f,
/// 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4543 : DigitalIc
{
    private readonly Terminal[] _segments = new Terminal[7];
    private int _latched;

    public Ic4543() : base(16)
    {
        PropagationDelay = 300e-9;
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        LatchDisable = Pin(1, "LD", TerminalType.Input);
        C = Pin(2, "C", TerminalType.Input);
        B = Pin(3, "B", TerminalType.Input);
        D = Pin(4, "D", TerminalType.Input);
        A = Pin(5, "A", TerminalType.Input);
        Phase = Pin(6, "PH", TerminalType.Input);
        Blanking = Pin(7, "BI", TerminalType.Input);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        _segments[0] = Pin(9, "a", TerminalType.Output);
        _segments[1] = Pin(10, "b", TerminalType.Output);
        _segments[2] = Pin(11, "c", TerminalType.Output);
        _segments[3] = Pin(12, "d", TerminalType.Output);
        _segments[4] = Pin(13, "e", TerminalType.Output);
        _segments[6] = Pin(14, "g", TerminalType.Output);
        _segments[5] = Pin(15, "f", TerminalType.Output);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([A, B, C, D, LatchDisable, Phase, Blanking], _segments);
    }

    public override string PartNumber => "4543";

    public Terminal A { get; }
    public Terminal B { get; }
    public Terminal C { get; }
    public Terminal D { get; }

    /// <summary>High: the latch follows the inputs. Low: it holds.</summary>
    public Terminal LatchDisable { get; }

    /// <summary>Every segment output is the decoded segment exclusive-ORed with this.</summary>
    public Terminal Phase { get; }

    /// <summary>Active high: blanks the display.</summary>
    public Terminal Blanking { get; }

    /// <summary>Segments a through g.</summary>
    public IReadOnlyList<Terminal> Segments => _segments;

    /// <summary>The digit latched, which is what is displayed unless blanked.</summary>
    public int DisplayedValue => _latched;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        bool High(Terminal pin) => context.ReadInput(pin, Levels).IsHigh();

        if (High(LatchDisable))
            _latched = (High(A) ? 1 : 0) | (High(B) ? 2 : 0) | (High(C) ? 4 : 0) | (High(D) ? 8 : 0);

        var pattern = High(Blanking) || _latched > 9 ? 0 : SevenSegmentGlyphs.For(_latched, tails: true);
        var phase = High(Phase);

        for (var segment = 0; segment < 7; segment++)
        {
            var lit = (pattern & (1 << (6 - segment))) != 0;
            Request(context, segment, LogicStateExtensions.FromBool(lit ^ phase), delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _latched = 0;
    }
}

/// <summary>
/// Base for the CMOS decade counters with a seven-segment driver built in, the 4026 and 4033: a
/// counter, a decoder and seven active-high outputs in one package, so a digit of a counter is the
/// part, a display and its resistors. Rising clocks count while CLOCK INHIBIT is low; RESET, active
/// high, clears. CARRY OUT is high for counts 0 to 4 and rises as the count wraps from 9, which is the
/// edge the next digit's clock wants.
/// <para>
/// The 4026 has DISPLAY ENABLE IN (high to light the display, passed through on DISPLAY ENABLE OUT)
/// and an UNGATED C segment output that stays on whatever the enable says. The 4033 has ripple
/// blanking instead: RBI low blanks a zero, and RBO goes low while it does, so the next digit down can
/// blank its own leading zero — and a LAMP TEST that lights everything.
/// </para>
/// <para>
/// Pinout: 1=CLK, 2=INH, 3=DEI (4026) or RBI (4033), 4=DEO or RBO, 5=CO, 6=f, 7=g, 8=VSS, 9=d, 10=a,
/// 11=e, 12=b, 13=c, 14=UNGATED c (4026) or LT (4033), 15=RST, 16=VDD.
/// </para>
/// </summary>
public abstract class DecadeDisplayCounterIc : DigitalIc
{
    private readonly Terminal[] _segments = new Terminal[7];
    private readonly bool _rippleBlanking;
    private LogicState _lastClock = LogicState.Unknown;
    private int _count;

    /// <param name="rippleBlanking">The 4033's pins 3, 4 and 14 rather than the 4026's.</param>
    protected DecadeDisplayCounterIc(bool rippleBlanking) : base(16)
    {
        _rippleBlanking = rippleBlanking;
        PropagationDelay = 250e-9;
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        Clock = Pin(1, "CLK", TerminalType.Input);
        ClockInhibit = Pin(2, "INH", TerminalType.Input);
        Pin3 = Pin(3, rippleBlanking ? "RBI" : "DEI", TerminalType.Input);
        Pin4 = Pin(4, rippleBlanking ? "RBO" : "DEO", TerminalType.Output);
        CarryOut = Pin(5, "CO", TerminalType.Output);
        _segments[5] = Pin(6, "f", TerminalType.Output);
        _segments[6] = Pin(7, "g", TerminalType.Output);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        _segments[3] = Pin(9, "d", TerminalType.Output);
        _segments[0] = Pin(10, "a", TerminalType.Output);
        _segments[4] = Pin(11, "e", TerminalType.Output);
        _segments[1] = Pin(12, "b", TerminalType.Output);
        _segments[2] = Pin(13, "c", TerminalType.Output);
        Pin14 = Pin(14, rippleBlanking ? "LT" : "UC", rippleBlanking ? TerminalType.Input : TerminalType.Output);
        Reset = Pin(15, "RST", TerminalType.Input);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            rippleBlanking ? [Clock, ClockInhibit, Pin3, Pin14, Reset] : [Clock, ClockInhibit, Pin3, Reset],
            rippleBlanking ? [.. _segments, CarryOut, Pin4] : [.. _segments, CarryOut, Pin4, Pin14]);
    }

    public Terminal Clock { get; }

    /// <summary>Active high: blocks the clock.</summary>
    public Terminal ClockInhibit { get; }

    /// <summary>Active high, asynchronous.</summary>
    public Terminal Reset { get; }

    /// <summary>High for counts 0 to 4: the clock divided by ten.</summary>
    public Terminal CarryOut { get; }

    /// <summary>Pin 3: DISPLAY ENABLE IN on the 4026, RIPPLE BLANKING IN on the 4033.</summary>
    protected Terminal Pin3 { get; }

    /// <summary>Pin 4: DISPLAY ENABLE OUT on the 4026, RIPPLE BLANKING OUT on the 4033.</summary>
    protected Terminal Pin4 { get; }

    /// <summary>Pin 14: UNGATED C SEGMENT on the 4026, LAMP TEST on the 4033.</summary>
    protected Terminal Pin14 { get; }

    /// <summary>Segments a through g.</summary>
    public IReadOnlyList<Terminal> Segments => _segments;

    /// <summary>The digit counted, 0 to 9.</summary>
    public int Count => _count;

    public override string ValueLabel => $"{_count}";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        bool High(Terminal pin) => context.ReadInput(pin, Levels).IsHigh();

        var clock = context.ReadInput(Clock, Levels);

        if (High(Reset))
            _count = 0;
        else if (!context.IsInitializing && !High(ClockInhibit) && _lastClock.IsLow() && clock.IsHigh())
            _count = (_count + 1) % 10;

        _lastClock = clock;

        var glyph = SevenSegmentGlyphs.For(_count, tails: false);
        int pattern;

        if (_rippleBlanking)
        {
            var blankZero = _count == 0 && !High(Pin3);
            pattern = High(Pin14) ? 0b1111111 : blankZero ? 0 : glyph;
            Request(context, 8, LogicStateExtensions.FromBool(!blankZero), delay);
        }
        else
        {
            var enabled = High(Pin3);
            pattern = enabled ? glyph : 0;
            Request(context, 8, LogicStateExtensions.FromBool(enabled), delay);
            Request(context, 9, LogicStateExtensions.FromBool((glyph & 0b0010000) != 0), delay);
        }

        for (var segment = 0; segment < 7; segment++)
            Request(context, segment, LogicStateExtensions.FromBool((pattern & (1 << (6 - segment))) != 0), delay);

        Request(context, 7, LogicStateExtensions.FromBool(_count < 5), delay);

        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _count = 0;
        _lastClock = LogicState.Unknown;
    }
}

/// <summary>4026 decade counter and seven-segment driver, with display enable and an ungated c segment.</summary>
public sealed class Ic4026() : DecadeDisplayCounterIc(rippleBlanking: false)
{
    public override string PartNumber => "4026";

    /// <summary>Pin 3, active high: lights the display.</summary>
    public Terminal DisplayEnableIn => Pin3;

    /// <summary>Pin 4: DISPLAY ENABLE IN passed on.</summary>
    public Terminal DisplayEnableOut => Pin4;

    /// <summary>Pin 14: segment c regardless of the enable.</summary>
    public Terminal UngatedC => Pin14;
}

/// <summary>4033 decade counter and seven-segment driver, with ripple blanking and lamp test.</summary>
public sealed class Ic4033() : DecadeDisplayCounterIc(rippleBlanking: true)
{
    public override string PartNumber => "4033";

    /// <summary>Pin 3, active low: blanks a zero.</summary>
    public Terminal RippleBlankingIn => Pin3;

    /// <summary>Pin 4: low while a zero is being blanked, for the next digit down's RBI.</summary>
    public Terminal RippleBlankingOut => Pin4;

    /// <summary>Pin 14, active high: lights every segment.</summary>
    public Terminal LampTest => Pin14;
}
