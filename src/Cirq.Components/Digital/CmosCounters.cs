using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for the CMOS binary ripple counters — the 4020 (fourteen stages), the 4024 (seven) and the
/// 4040 (twelve). A chain of flip-flops, each the one before divided by two, counting on the
/// <b>falling</b> edge, with a reset that is active <b>high</b>; a part is the number of stages and
/// which of them reach a pin. The 4020 leaves out Q2 and Q3, so its divisions jump from ÷2 to ÷16.
/// <para>
/// Being ripple counters, a real one's stages do not change together: brief false codes appear as
/// the carry walks down the chain, which is why decoding the outputs directly collects glitches.
/// </para>
/// </summary>
public abstract class CmosRippleCounterIc : DigitalIc
{
    private readonly int[] _stageNumbers;
    private readonly int _mask;
    private LogicState _lastClock = LogicState.Unknown;
    private int _count;

    /// <param name="stages">Flip-flops in the chain.</param>
    /// <param name="outputs">Which stage each output pin carries (stage 1 is the clock halved), with its pin.</param>
    protected CmosRippleCounterIc(
        int pinCount, int stages, (int Stage, int Pin)[] outputs, int clockPin, int resetPin,
        int vddPin, int vssPin, params int[] unconnected)
        : base(pinCount)
    {
        PropagationDelay = 200e-9;
        Levels = LogicLevels.Cmos5V;
        _mask = (1 << stages) - 1;
        _stageNumbers = [.. outputs.Select(o => o.Stage)];

        var pins = new Terminal[pinCount + 1];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, pinCount));

        Outputs = [.. outputs.Select(o => Pin(o.Pin, $"Q{o.Stage}", TerminalType.Output))];
        Clock = Pin(clockPin, "CLK", TerminalType.Input);
        Reset = Pin(resetPin, "RST", TerminalType.Input);
        Gnd = Pin(vssPin, "VSS", TerminalType.Ground);
        Vcc = Pin(vddPin, "VDD", TerminalType.Power);
        foreach (var nc in unconnected) Pin(nc, "NC", TerminalType.Passive);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Clock, Reset], Outputs);
    }

    /// <summary>The stage outputs that reach pins, in stage order.</summary>
    public IReadOnlyList<Terminal> Outputs { get; }

    /// <summary>The stage number each entry of <see cref="Outputs"/> carries.</summary>
    public IReadOnlyList<int> Stages => _stageNumbers;

    public Terminal Clock { get; }

    /// <summary>Active high, and asynchronous.</summary>
    public Terminal Reset { get; }

    /// <summary>The internal count, every stage included.</summary>
    public int Count => _count;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        if (context.ReadInput(Reset, Levels) == LogicState.High)
        {
            _count = 0;
        }
        else
        {
            var clock = context.ReadInput(Clock, Levels);

            // Falling edge, which is the other way round from the 4017.
            if (clock == LogicState.Low && _lastClock == LogicState.High)
                _count = (_count + 1) & _mask;

            _lastClock = clock;
        }

        for (var i = 0; i < _stageNumbers.Length; i++)
        {
            var bit = (_count & (1 << (_stageNumbers[i] - 1))) != 0;
            context.Schedule(this, i, bit ? LogicState.High : LogicState.Low, delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _lastClock = LogicState.Unknown;
        _count = 0;
    }
}

/// <summary>
/// A 4040 twelve-stage binary ripple counter.
/// <para>
/// Twelve flip-flops in a chain, so Q1 is the clock divided by two and Q12 is the clock divided by
/// 4096. That is what it is for: dividing something fast down to something slow. A 32.768 kHz
/// crystal into Q12 gives you eight ticks a second, and the next part along gets you to one.
/// </para>
/// <para>
/// It counts on the <b>falling</b> edge, unlike the 4017 beside it, and its reset is active high —
/// tie it low or the counter never gets going.
/// </para>
/// <para>
/// Pinout: 1=Q12, 2=Q6, 3=Q5, 4=Q7, 5=Q4, 6=Q3, 7=Q2, 8=VSS,
/// 9=Q1, 10=CLK, 11=RST, 12=Q9, 13=Q8, 14=Q10, 15=Q11, 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4040() : CmosRippleCounterIc(16, 12,
    [(1, 9), (2, 7), (3, 6), (4, 5), (5, 3), (6, 2), (7, 4), (8, 13), (9, 12), (10, 14), (11, 15), (12, 1)],
    clockPin: 10, resetPin: 11, vddPin: 16, vssPin: 8)
{
    public override string PartNumber => "4040";
}

/// <summary>
/// A 4020 fourteen-stage binary ripple counter: the 4060's counter without its oscillator, with Q1
/// brought out and Q2 and Q3 not. Divides by up to 16384.
/// <para>
/// Pinout: 1=Q12, 2=Q13, 3=Q14, 4=Q6, 5=Q5, 6=Q7, 7=Q4, 8=VSS, 9=Q1, 10=CLK, 11=RST, 12=Q9, 13=Q8,
/// 14=Q10, 15=Q11, 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4020() : CmosRippleCounterIc(16, 14,
    [(1, 9), (4, 7), (5, 5), (6, 4), (7, 6), (8, 13), (9, 12), (10, 14), (11, 15), (12, 1), (13, 2), (14, 3)],
    clockPin: 10, resetPin: 11, vddPin: 16, vssPin: 8)
{
    public override string PartNumber => "4020";
}

/// <summary>
/// A 4024 seven-stage binary ripple counter, in fourteen pins. Q7 is the clock divided by 128.
/// <para>
/// Pinout: 1=CLK, 2=RST, 3=Q7, 4=Q6, 5=Q5, 6=Q4, 7=VSS, 8=NC, 9=Q3, 10=NC, 11=Q2, 12=Q1, 13=NC, 14=VDD.
/// </para>
/// </summary>
public sealed class Ic4024() : CmosRippleCounterIc(14, 7,
    [(1, 12), (2, 11), (3, 9), (4, 6), (5, 5), (6, 4), (7, 3)],
    clockPin: 1, resetPin: 2, vddPin: 14, vssPin: 7, 8, 10, 13)
{
    public override string PartNumber => "4024";
}

/// <summary>
/// Base for the counters that decode for you, the 4017 (one of ten outputs high) and the 4022 (one
/// of eight). A Johnson counter inside walks one high output along on each rising clock while
/// CLOCK INHIBIT is low; RESET, active high, puts it back on output 0. CARRY OUT is high for the
/// first half of the cycle, so it divides the clock by ten (or eight) and rises as the count wraps —
/// exactly when the next stage's clock wants an edge.
/// </summary>
public abstract class DecodedCounterIc : DigitalIc
{
    private LogicState _lastClock = LogicState.Unknown;
    private readonly int _modulus;
    private int _count;

    /// <param name="outputPins">The pin each decoded output is on, output 0 first.</param>
    protected DecodedCounterIc(int[] outputPins, int carryPin, int inhibitPin, int clockPin, int resetPin,
        params int[] unconnected)
        : base(16)
    {
        PropagationDelay = 200e-9;          // CMOS at 5 V is far slower than TTL
        Levels = LogicLevels.Cmos5V;
        _modulus = outputPins.Length;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        Outputs = [.. outputPins.Select((p, i) => Pin(p, $"Q{i}", TerminalType.Output))];
        CarryOut = Pin(carryPin, "CO", TerminalType.Output);
        ClockInhibit = Pin(inhibitPin, "INH", TerminalType.Input);
        Clock = Pin(clockPin, "CLK", TerminalType.Input);
        Reset = Pin(resetPin, "RST", TerminalType.Input);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        Vcc = Pin(16, "VDD", TerminalType.Power);
        foreach (var nc in unconnected) Pin(nc, "NC", TerminalType.Passive);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Clock, ClockInhibit, Reset], [.. Outputs, CarryOut]);
    }

    /// <summary>The decoded outputs, Q0 upwards.</summary>
    public IReadOnlyList<Terminal> Outputs { get; }

    public Terminal CarryOut { get; }

    public Terminal Clock { get; }

    public Terminal ClockInhibit { get; }

    public Terminal Reset { get; }

    /// <summary>Which output is currently high.</summary>
    public int Count => _count;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        if (context.ReadInput(Reset, Levels) == LogicState.High)
        {
            _count = 0;
        }
        else
        {
            var clock = context.ReadInput(Clock, Levels);
            var inhibited = context.ReadInput(ClockInhibit, Levels) == LogicState.High;

            // Rising edge, and only while the inhibit pin is low.
            if (!inhibited && clock == LogicState.High && _lastClock == LogicState.Low)
                _count = (_count + 1) % _modulus;

            _lastClock = clock;
        }

        for (var i = 0; i < _modulus; i++)
            context.Schedule(this, i, i == _count ? LogicState.High : LogicState.Low, delay);

        // Carry is high over the first half of the cycle, so it divides the clock by the modulus.
        context.Schedule(this, _modulus, _count < _modulus / 2 ? LogicState.High : LogicState.Low, delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _lastClock = LogicState.Unknown;
        _count = 0;
    }
}

/// <summary>
/// A 4017 decade counter with ten decoded outputs.
/// <para>
/// Unlike a 7490, which counts in BCD and needs a decoder to show anything, the 4017 decodes for
/// you: exactly one of its ten outputs is high at a time and it walks along them on each clock.
/// That is why it is the part behind every LED chaser — ten LEDs, ten pins, no decoder.
/// </para>
/// <para>
/// Carry-out is high for the first five counts and low for the last five, so it divides the clock
/// by ten and can be chained straight into the next stage's clock.
/// </para>
/// <para>
/// Pinout: 1=Q5, 2=Q1, 3=Q0, 4=Q2, 5=Q6, 6=Q7, 7=Q3, 8=VSS, 9=Q8, 10=Q4, 11=Q9, 12=CO, 13=INH,
/// 14=CLK, 15=RST, 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4017() : DecodedCounterIc([3, 2, 4, 7, 10, 1, 5, 6, 9, 11], carryPin: 12, inhibitPin: 13,
    clockPin: 14, resetPin: 15)
{
    public override string PartNumber => "4017";
}

/// <summary>
/// A 4022 divide-by-eight counter with eight decoded outputs: the 4017 cut short, so CARRY OUT is high
/// for counts 0 to 3. Its pinout is the 4017's with the outputs moved round and pins 6 and 9 empty —
/// one dropped into a 4017's socket walks its LEDs in a different order.
/// <para>
/// Pinout: 1=Q1, 2=Q0, 3=Q2, 4=Q5, 5=Q6, 6=NC, 7=Q3, 8=VSS, 9=NC, 10=Q7, 11=Q4, 12=CO, 13=INH,
/// 14=CLK, 15=RST, 16=VDD.
/// </para>
/// </summary>
public sealed class Ic4022() : DecodedCounterIc([2, 1, 3, 7, 11, 4, 5, 10], carryPin: 12, inhibitPin: 13,
    clockPin: 14, resetPin: 15, 6, 9)
{
    public override string PartNumber => "4022";
}

/// <summary>
/// Base for the presettable CMOS up/down counters on one pinout, the 4029 (binary or decade, chosen by
/// a pin), the 4510 (decade) and the 4516 (binary). One clock and a direction pin — U/D high counts
/// up — rather than the 74192's two clocks.
/// <para>
/// Counting happens on the rising clock edge while CARRY IN is low. PRESET ENABLE high copies P1-P4
/// to the outputs at once, without a clock; on the 4510 and 4516 RESET high clears at once and beats
/// it. CARRY OUT goes <b>low</b> at the top of the count going up, or at zero going down, while CARRY
/// IN is low — wire it to the next stage's CARRY IN and the clocks in common, and the stages count
/// together, synchronously.
/// </para>
/// <para>
/// Pinout: 1=PE, 2=Q4, 3=P4, 4=P1, 5=CI, 6=Q1, 7=CO, 8=VSS, 9=B/D (4029) or RST, 10=U/D, 11=Q2,
/// 12=P2, 13=P3, 14=Q3, 15=CLK, 16=VDD.
/// </para>
/// </summary>
public abstract class PresettableUpDownCounterIc : DigitalIc
{
    private readonly Terminal[] _p = new Terminal[4];
    private readonly Terminal[] _q = new Terminal[4];
    private bool _lastClock = true;
    private int _count;

    /// <param name="binaryDecadePin">The 4029's pin 9 is BINARY/DECADE; on the others it is RESET.</param>
    /// <param name="decade">For the 4510 and 4516, which modulus pin 9 does not choose.</param>
    protected PresettableUpDownCounterIc(bool binaryDecadePin, bool decade) : base(16)
    {
        PropagationDelay = 250e-9;
        Levels = LogicLevels.Cmos5V;
        _fixedDecade = decade;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        PresetEnable = Pin(1, "PE", TerminalType.Input);
        _q[3] = Pin(2, "Q4", TerminalType.Output);
        _p[3] = Pin(3, "P4", TerminalType.Input);
        _p[0] = Pin(4, "P1", TerminalType.Input);
        CarryIn = Pin(5, "CI", TerminalType.Input);
        _q[0] = Pin(6, "Q1", TerminalType.Output);
        CarryOut = Pin(7, "CO", TerminalType.Output);
        Gnd = Pin(8, "VSS", TerminalType.Ground);
        ModeOrReset = Pin(9, binaryDecadePin ? "B/D" : "RST", TerminalType.Input);
        UpDown = Pin(10, "U/D", TerminalType.Input);
        _q[1] = Pin(11, "Q2", TerminalType.Output);
        _p[1] = Pin(12, "P2", TerminalType.Input);
        _p[2] = Pin(13, "P3", TerminalType.Input);
        _q[2] = Pin(14, "Q3", TerminalType.Output);
        Clock = Pin(15, "CLK", TerminalType.Input);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        HasBinaryDecadePin = binaryDecadePin;
        Terminals = [.. pins.Skip(1)];
        ConfigurePins([Clock, UpDown, CarryIn, PresetEnable, ModeOrReset, .. _p], [.. _q, CarryOut]);
    }

    private readonly bool _fixedDecade;

    public Terminal Clock { get; }

    /// <summary>High counts up, low counts down.</summary>
    public Terminal UpDown { get; }

    /// <summary>Active low: enables counting and the carry out.</summary>
    public Terminal CarryIn { get; }

    /// <summary>Active low: low at the end of the count in the direction it is going, with CI low.</summary>
    public Terminal CarryOut { get; }

    /// <summary>Active high, asynchronous: copies P1-P4 to the outputs.</summary>
    public Terminal PresetEnable { get; }

    /// <summary>Pin 9: BINARY/DECADE on the 4029 (high is binary), RESET on the 4510 and 4516.</summary>
    public Terminal ModeOrReset { get; }

    /// <summary>True for the 4029, whose pin 9 picks the modulus.</summary>
    public bool HasBinaryDecadePin { get; }

    /// <summary>The preset inputs P1 to P4, P1 the least significant.</summary>
    public IReadOnlyList<Terminal> Preset => _p;

    /// <summary>Q1 to Q4.</summary>
    public IReadOnlyList<Terminal> Outputs => _q;

    public int Count => _count;

    public override string ValueLabel => $"{_count}";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        bool High(Terminal pin) => context.ReadInput(pin, Levels).IsHigh();

        var clock = High(Clock);
        var up = High(UpDown);
        var enabled = context.ReadInput(CarryIn, Levels).IsLow();
        var decade = HasBinaryDecadePin ? !High(ModeOrReset) : _fixedDecade;
        var top = decade ? 9 : 15;

        if (!HasBinaryDecadePin && High(ModeOrReset))
        {
            _count = 0;
        }
        else if (High(PresetEnable))
        {
            var loaded = 0;
            for (var i = 0; i < 4; i++)
                if (High(_p[i])) loaded |= 1 << i;

            _count = loaded;
        }
        else if (!context.IsInitializing && clock && !_lastClock && enabled)
        {
            _count = up
                ? _count >= top ? 0 : _count + 1
                : _count == 0 ? top : _count - 1;
        }

        _lastClock = clock;

        for (var i = 0; i < 4; i++)
            context.Schedule(this, i, LogicStateExtensions.FromBool((_count & (1 << i)) != 0), delay);

        var end = up ? _count == top : _count == 0;
        context.Schedule(this, 4, LogicStateExtensions.FromBool(!(enabled && end)), delay);

        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _count = 0;
        _lastClock = true;
    }
}

/// <summary>4029 presettable up/down counter, binary (B/D high) or decade (low).</summary>
public sealed class Ic4029() : PresettableUpDownCounterIc(binaryDecadePin: true, decade: false)
{
    public override string PartNumber => "4029";
}

/// <summary>4510 presettable BCD up/down counter with reset.</summary>
public sealed class Ic4510() : PresettableUpDownCounterIc(binaryDecadePin: false, decade: true)
{
    public override string PartNumber => "4510";
}

/// <summary>4516 presettable binary up/down counter with reset.</summary>
public sealed class Ic4516() : PresettableUpDownCounterIc(binaryDecadePin: false, decade: false)
{
    public override string PartNumber => "4516";
}

/// <summary>
/// Base for the 4518 (dual BCD) and 4520 (dual binary) synchronous up counters. Each half counts on
/// the rising edge of CLOCK while ENABLE is high — or on the <b>falling</b> edge of ENABLE while CLOCK
/// is low, which is how the halves are cascaded: Q4 of one into ENABLE of the next, CLOCK held low.
/// RESET is active high and clears at once.
/// <para>
/// Pinout: 1=1CLK, 2=1EN, 3=1Q1, 4=1Q2, 5=1Q3, 6=1Q4, 7=1RST, 8=VSS, 9=2CLK, 10=2EN, 11=2Q1, 12=2Q2,
/// 13=2Q3, 14=2Q4, 15=2RST, 16=VDD.
/// </para>
/// </summary>
public abstract class DualUpCounterIc : DigitalIc
{
    private readonly int _modulus;
    private readonly Terminal[] _clock = new Terminal[2];
    private readonly Terminal[] _enable = new Terminal[2];
    private readonly Terminal[] _reset = new Terminal[2];
    private readonly Terminal[][] _q = [new Terminal[4], new Terminal[4]];
    private readonly int[] _count = new int[2];
    private readonly LogicState[] _lastClock = [LogicState.Unknown, LogicState.Unknown];
    private readonly LogicState[] _lastEnable = [LogicState.Unknown, LogicState.Unknown];

    protected DualUpCounterIc(int modulus) : base(16)
    {
        _modulus = modulus;
        PropagationDelay = 220e-9;
        Levels = LogicLevels.Cmos5V;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        for (var half = 0; half < 2; half++)
        {
            var b = half * 8;
            var n = half + 1;
            _clock[half] = Pin(b + 1, $"{n}CLK", TerminalType.Input);
            _enable[half] = Pin(b + 2, $"{n}EN", TerminalType.Input);
            for (var i = 0; i < 4; i++) _q[half][i] = Pin(b + 3 + i, $"{n}Q{i + 1}", TerminalType.Output);
            _reset[half] = Pin(b + 7, $"{n}RST", TerminalType.Input);
        }

        Gnd = Pin(8, "VSS", TerminalType.Ground);
        Vcc = Pin(16, "VDD", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins([.. _clock, .. _enable, .. _reset], [.. _q[0], .. _q[1]]);
    }

    public Terminal Clock(int half) => _clock[half];

    public Terminal Enable(int half) => _enable[half];

    /// <summary>Active high.</summary>
    public Terminal Reset(int half) => _reset[half];

    /// <summary>Q1 to Q4 of one half.</summary>
    public IReadOnlyList<Terminal> Outputs(int half) => _q[half];

    public int Count(int half) => _count[half];

    public override string ValueLabel => $"{_count[0]} / {_count[1]}";

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        for (var half = 0; half < 2; half++)
        {
            var clock = context.ReadInput(_clock[half], Levels);
            var enable = context.ReadInput(_enable[half], Levels);

            if (context.ReadInput(_reset[half], Levels).IsHigh())
            {
                _count[half] = 0;
            }
            else if (!context.IsInitializing)
            {
                var clockRose = _lastClock[half].IsLow() && clock.IsHigh() && enable.IsHigh();
                var enableFell = _lastEnable[half].IsHigh() && enable.IsLow() && clock.IsLow();

                if (clockRose || enableFell) _count[half] = (_count[half] + 1) % _modulus;
            }

            _lastClock[half] = clock;
            _lastEnable[half] = enable;

            for (var i = 0; i < 4; i++)
                context.Schedule(this, (half * 4) + i,
                    LogicStateExtensions.FromBool((_count[half] & (1 << i)) != 0), delay);
        }

        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_count);
        Array.Fill(_lastClock, LogicState.Unknown);
        Array.Fill(_lastEnable, LogicState.Unknown);
    }
}

/// <summary>4518 dual synchronous BCD up counter.</summary>
public sealed class Ic4518() : DualUpCounterIc(10)
{
    public override string PartNumber => "4518";
}

/// <summary>4520 dual synchronous four-bit binary up counter.</summary>
public sealed class Ic4520() : DualUpCounterIc(16)
{
    public override string PartNumber => "4520";
}
