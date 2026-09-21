using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Digital;

/// <summary>
/// 74161 four-bit synchronous binary counter, with parallel load and carry.
/// <para>
/// The word that matters is <b>synchronous</b>, and it is the whole reason to have this next to
/// the 4040 and the 7490. Those are ripple counters: the first flip-flop clocks the second, the
/// second clocks the third, and a carry walks down the chain taking a propagation delay at every
/// stage. For a few nanoseconds after each clock the outputs are showing a number that was never
/// counted — 0111 on its way to 1000 passes through 0110, 0100 and 0000 — so anything decoding
/// them directly collects a glitch on every carry.
/// </para>
/// <para>
/// Here every flip-flop is clocked by the same edge and all four outputs change together. Decode
/// them with a 74138 and the decoded line is clean, which is why a counter feeding address logic
/// is one of these and not a 4040. What it costs is pins and a clock that must reach four
/// flip-flops at once.
/// </para>
/// <para>
/// Two enables rather than one, and the asymmetry is deliberate: <c>CET</c> gates the carry out as
/// well as the counting, <c>CEP</c> only the counting. That is what lets several be chained —
/// carry into the next stage's CET — without the carry rippling.
/// </para>
/// <para>
/// Pinout: 1=MR, 2=CP, 3-6=D0-D3, 7=CEP, 8=GND, 9=PE, 10=CET, 11-14=Q3-Q0, 15=TC, 16=VCC.
/// Master reset is <b>asynchronous</b> and active low — it clears the moment it is taken low,
/// without waiting for a clock, which is the one thing on this part that is not synchronous. Its
/// 74163 cousin makes that synchronous too.
/// </para>
/// </summary>
public sealed class Ic74161 : DigitalIc
{
    private const int Width = 4;

    private readonly Terminal[] _data = new Terminal[Width];
    private readonly Terminal[] _outputs = new Terminal[Width + 1];

    private bool _lastClock;
    private int _count;

    public Ic74161() : base(16)
    {
        PropagationDelay = 14e-9;

        var pins = new Terminal[17];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 16));

        MasterReset = Pin(1, "MR", TerminalType.Input);
        Clock = Pin(2, "CP", TerminalType.Input);

        for (var i = 0; i < Width; i++) _data[i] = Pin(3 + i, $"D{i}", TerminalType.Input);

        CountEnableP = Pin(7, "CEP", TerminalType.Input);
        Gnd = Pin(8, "GND", TerminalType.Ground);
        ParallelEnable = Pin(9, "PE", TerminalType.Input);
        CountEnableT = Pin(10, "CET", TerminalType.Input);

        for (var i = 0; i < Width; i++) _outputs[Width - 1 - i] = Pin(11 + i, $"Q{Width - 1 - i}", TerminalType.Output);

        _outputs[Width] = Pin(15, "TC", TerminalType.Output);
        Vcc = Pin(16, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];

        ConfigurePins(
            [Clock, MasterReset, ParallelEnable, CountEnableP, CountEnableT, .. _data],
            _outputs);
    }

    /// <summary>Asynchronous clear, active low — the one thing here that does not wait for a clock.</summary>
    public Terminal MasterReset { get; }

    public Terminal Clock { get; }

    /// <summary>Active-low parallel load, taken on the clock edge.</summary>
    public Terminal ParallelEnable { get; }

    /// <summary>Enables counting only.</summary>
    public Terminal CountEnableP { get; }

    /// <summary>Enables counting <i>and</i> the carry out, which is what chains stages.</summary>
    public Terminal CountEnableT { get; }

    /// <summary>The four load inputs, D0 through D3.</summary>
    public IReadOnlyList<Terminal> Data => _data;

    /// <summary>Q0 through Q3.</summary>
    public IReadOnlyList<Terminal> Outputs => [.. _outputs.Take(Width)];

    /// <summary>Terminal count: high at fifteen, with CET high. Feeds the next stage's CET.</summary>
    public Terminal TerminalCount => _outputs[Width];

    public override string PartNumber => "74161";

    public override string ValueLabel => $"{_count}";

    /// <summary>What it is holding, 0 to 15.</summary>
    public int Count => _count;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var clock = context.ReadInput(Clock, Levels).IsHigh();

        if (!context.ReadInput(MasterReset, Levels).IsHigh())
        {
            // Asynchronous: it does not wait to be asked twice, or at all.
            _count = 0;
        }
        else if (clock && !_lastClock)
        {
            if (!context.ReadInput(ParallelEnable, Levels).IsHigh())
            {
                var loaded = 0;
                for (var i = 0; i < Width; i++)
                    if (context.ReadInput(_data[i], Levels).IsHigh()) loaded |= 1 << i;

                _count = loaded;
            }
            else if (context.ReadInput(CountEnableP, Levels).IsHigh()
                     && context.ReadInput(CountEnableT, Levels).IsHigh())
            {
                _count = (_count + 1) & 0x0F;
            }
        }

        _lastClock = clock;

        // Every output scheduled at the same delay from the same edge, which is the point of the
        // part: they arrive together rather than in the order the carry reached them.
        for (var i = 0; i < Width; i++)
        {
            var bit = (_count & (1 << i)) != 0;
            context.Schedule(this, i, bit ? LogicState.High : LogicState.Low, delay);
        }

        var carry = _count == 0x0F && context.ReadInput(CountEnableT, Levels).IsHigh();
        context.Schedule(this, Width, carry ? LogicState.High : LogicState.Low, delay);

        NotifyValueChanged();
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _count = 0;
        _lastClock = false;
    }
}
