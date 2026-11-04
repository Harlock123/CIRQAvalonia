using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for the 74192 and 74193 synchronous up/down counters: two clocks, one for each direction,
/// rather than one clock and a direction pin.
/// <para>
/// A rising edge on UP counts up while DOWN is held high, and a rising edge on DOWN counts down
/// while UP is held high; all four outputs change together on that edge, which is what
/// synchronous means. CLR is asynchronous and active high — it clears the moment it rises — and
/// LOAD is asynchronous and active low, copying A–D straight to the outputs while it is held low
/// rather than waiting for a clock.
/// </para>
/// <para>
/// The two terminal-count outputs are what make them cascade without any gates. CO goes low while
/// the count is at its top (9 or 15) <i>and</i> UP is low, so it rises again on the very UP edge
/// that wraps the count to zero — wire it to the next stage's UP and that stage counts at exactly
/// the right moment. BO does the same at zero with DOWN, for the next stage's DOWN.
/// </para>
/// <para>
/// Pinout: 1=B, 2=QB, 3=QA, 4=DOWN, 5=UP, 6=QC, 7=QD, 8=GND, 9=D, 10=C, 11=LOAD, 12=CO, 13=BO,
/// 14=CLR, 15=A, 16=VCC.
/// </para>
/// </summary>
public abstract class UpDownCounterIc : DigitalIc
{
    private const int Width = 4;

    private readonly Terminal[] _data = new Terminal[Width];
    private readonly Terminal[] _q = new Terminal[Width];

    private bool _lastUp = true;
    private bool _lastDown = true;
    private int _count;

    protected UpDownCounterIc() : base(16)
    {
        PropagationDelay = 20e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        _data[1] = Pin(1, "B", TerminalType.Input);
        _q[1] = Pin(2, "QB", TerminalType.Output);
        _q[0] = Pin(3, "QA", TerminalType.Output);
        Down = Pin(4, "DOWN", TerminalType.Input);
        Up = Pin(5, "UP", TerminalType.Input);
        _q[2] = Pin(6, "QC", TerminalType.Output);
        _q[3] = Pin(7, "QD", TerminalType.Output);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        _data[3] = Pin(9, "D", TerminalType.Input);
        _data[2] = Pin(10, "C", TerminalType.Input);
        Load = Pin(11, "LOAD", TerminalType.Input);
        Carry = Pin(12, "CO", TerminalType.Output);
        Borrow = Pin(13, "BO", TerminalType.Output);
        Clear = Pin(14, "CLR", TerminalType.Input);
        _data[0] = Pin(15, "A", TerminalType.Input);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        ConfigurePins([Up, Down, Load, Clear, .. _data], [.. _q, Carry, Borrow]);
    }

    /// <summary>Count-up clock: rising edges count up while DOWN is high.</summary>
    public Terminal Up { get; }

    /// <summary>Count-down clock: rising edges count down while UP is high.</summary>
    public Terminal Down { get; }

    /// <summary>Asynchronous parallel load, active low.</summary>
    public Terminal Load { get; }

    /// <summary>Asynchronous clear, active high.</summary>
    public Terminal Clear { get; }

    /// <summary>Terminal count up, active low: low at the top of the count while UP is low.</summary>
    public Terminal Carry { get; }

    /// <summary>Terminal count down, active low: low at zero while DOWN is low.</summary>
    public Terminal Borrow { get; }

    /// <summary>The load inputs A to D, A the least significant.</summary>
    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>QA to QD.</summary>
    public IReadOnlyList<Terminal> Outputs => _q;

    /// <summary>What it is holding.</summary>
    public int Count => _count;

    /// <summary>The top of the count: 9 for the decade part, 15 for the binary one.</summary>
    public abstract int Maximum { get; }

    public override string ValueLabel => $"{_count}";

    /// <summary>One count up from a value, the way this part does it.</summary>
    protected abstract int Next(int count);

    /// <summary>One count down.</summary>
    protected abstract int Previous(int count);

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        var up = context.ReadInput(Up, Levels).IsHigh();
        var down = context.ReadInput(Down, Levels).IsHigh();

        if (context.ReadInput(Clear, Levels).IsHigh())
        {
            _count = 0;
        }
        else if (context.ReadInput(Load, Levels).IsLow())
        {
            var loaded = 0;
            for (var i = 0; i < Width; i++)
                if (context.ReadInput(_data[i], Levels).IsHigh()) loaded |= 1 << i;

            _count = loaded;
        }
        else if (!context.IsInitializing)
        {
            if (up && !_lastUp && down) _count = Next(_count);
            else if (down && !_lastDown && up) _count = Previous(_count);
        }

        _lastUp = up;
        _lastDown = down;

        for (var i = 0; i < Width; i++)
            context.Schedule(this, i, LogicStateExtensions.FromBool((_count & (1 << i)) != 0), delay);

        // Active low, and gated by the clock that would carry the count past them.
        context.Schedule(this, Width, LogicStateExtensions.FromBool(!(_count == Maximum && !up)), delay);
        context.Schedule(this, Width + 1, LogicStateExtensions.FromBool(!(_count == 0 && !down)), delay);

        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _count = 0;
        _lastUp = true;
        _lastDown = true;
    }
}

/// <summary>
/// 74193 synchronous 4-bit binary up/down counter: 0 to 15 and round again either way.
/// </summary>
public sealed class Ic74193 : UpDownCounterIc
{
    public override string PartNumber => "74193";

    public override int Maximum => 15;

    protected override int Next(int count) => (count + 1) & 0x0F;

    protected override int Previous(int count) => (count + 15) & 0x0F;
}

/// <summary>
/// 74192 synchronous BCD (decade) up/down counter: 0 to 9 and round again either way, so a pair
/// cascaded on CO and BO count 00 to 99 and back.
/// <para>
/// The six codes above 9 are reachable only by loading one. Counting up from any of them returns to
/// 0 and counting down steps back towards 9; the datasheet's exact recovery sequence through those
/// codes is not modelled.
/// </para>
/// </summary>
public sealed class Ic74192 : UpDownCounterIc
{
    public override string PartNumber => "74192";

    public override int Maximum => 9;

    protected override int Next(int count) => count >= 9 ? 0 : count + 1;

    protected override int Previous(int count) => count == 0 ? 9 : count - 1;
}
