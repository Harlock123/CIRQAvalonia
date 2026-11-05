using System.Globalization;
using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Processors;

/// <summary>
/// The Zilog Z80 CTC, counter/timer circuit: four 8-bit down-counters, each a timer on the system
/// clock or a counter of edges on its own CLK/TRG pin, interrupting through the daisy chain.
/// <para>
/// <b>The bus.</b> CS1 and CS0 pick the channel; a read gives its down-counter as it stands, and a
/// write is a control word, a time constant or the vector — see <see cref="Z80Peripheral"/> for
/// the cycle. A byte with bit 0 set is a control word: bit 7 enables the channel's interrupt, bit 6
/// picks counter (1) or timer (0) mode, bit 5 the timer's prescaler (256 or 16), bit 4 the active
/// edge of CLK/TRG (1 rising), bit 3 how a timer starts (0 at once when its time constant is
/// loaded, 1 on the CLK/TRG edge after), bit 2 says a time constant follows as the next byte, and
/// bit 1 is a software reset, which stops the channel. A byte with bit 0 clear written to channel 0
/// is the interrupt vector: its bits 7-3, with the channel's number in bits 2-1 on an acknowledge.
/// </para>
/// <para>
/// <b>Counting.</b> The time constant, 1 to 256 (0 meaning 256), loads the down-counter. A timer
/// counts the system clock through its prescaler: one count every 16 or 256 CLK cycles. A counter
/// counts active edges on CLK/TRG. Each time the count reaches zero the down-counter reloads, the
/// channel's ZC/TO output goes high for one CLK cycle (channel 3 has no ZC/TO pin), and, if enabled,
/// the channel interrupts. A new time constant written while a channel runs is taken at the next
/// reload. Channel 0 has the highest priority, channel 3 the lowest. /RESET stops every channel,
/// disables its interrupt, takes the ZC/TO outputs low and waits for new control words; the vector
/// is kept.
/// </para>
/// <para>
/// Simplifications: a timer starts on the first CLK after its time constant (the chip waits for
/// the second), and the CLK/TRG edges are taken as they come rather than synchronised to CLK —
/// so a counter can count faster than the datasheet's half the system clock.
/// </para>
/// <para>
/// Pinout (DIP-28): 1-4 = D4-D7, 5 = GND, 6 = /RD, 7-9 = ZC/TO0-ZC/TO2, 10 = /IORQ, 11 = IEO,
/// 12 = /INT, 13 = IEI, 14 = /M1, 15 = CLK, 16 = CLK/TRG3, 17 = /RESET, 18 = CS1, 19 = CS0, 20 = /CE,
/// 21-23 = CLK/TRG2-CLK/TRG0, 24 = +5V, 25-28 = D0-D3.
/// </para>
/// </summary>
public sealed class Z80Ctc : Z80Peripheral, IRunState
{
    // Outputs: D0-D7 = 0-7, ZC/TO0-2 = 8-10, /INT = 11, IEO = 12.
    private const int ZeroIndex = 8;

    private static readonly int[] DataPins = [25, 26, 27, 28, 1, 2, 3, 4];

    private readonly Terminal[] _triggers = new Terminal[4];
    private readonly Channel[] _channels = [new(), new(), new(), new()];

    private byte _vector;
    private LogicState _lastClock = LogicState.Unknown;
    private bool _lastReset;

    /// <summary>One channel's registers and counting.</summary>
    private sealed class Channel
    {
        public byte Control;
        public int TimeConstant = 256;
        public int Counter;
        public int Prescaler;
        public bool Running;
        public bool AwaitingConstant;
        public bool AwaitingTrigger;
        public bool Pending;
        public int OutputClocks;
        public long ZeroCounts;
        public bool Trigger;

        public bool InterruptEnabled => (Control & 0x80) != 0;

        public bool CounterMode => (Control & 0x40) != 0;

        public int Prescale => (Control & 0x20) != 0 ? 256 : 16;

        public bool RisingEdge => (Control & 0x10) != 0;

        public void Reset()
        {
            Control = 0;
            Running = AwaitingConstant = AwaitingTrigger = Pending = false;
            OutputClocks = 0;
            Prescaler = 0;
        }

        public Channel Copy() => (Channel)MemberwiseClone();
    }

    public Z80Ctc() : base(28, 4)
    {
        PropagationDelay = 50e-9;

        var pins = new Terminal[29];

        Terminal Pin(int number, string name, TerminalType type) =>
            pins[number] = new Terminal($"p{number}", name, type, DipPackage.PinOffset(number, 28));

        for (var i = 0; i < 8; i++) SetDataPin(i, Pin(DataPins[i], $"D{i}", TerminalType.Bidirectional));
        Gnd = Pin(5, "GND", TerminalType.Ground);
        ReadStrobe = Pin(6, "RD", TerminalType.Input);
        var zc = new[] { Pin(7, "ZCTO0", TerminalType.Output), Pin(8, "ZCTO1", TerminalType.Output), Pin(9, "ZCTO2", TerminalType.Output) };
        IoRequest = Pin(10, "IORQ", TerminalType.Input);
        var ieo = Pin(11, "IEO", TerminalType.Output);
        var interrupt = Pin(12, "INT", TerminalType.Bidirectional);
        InterruptEnableIn = Pin(13, "IEI", TerminalType.Input);
        M1 = Pin(14, "M1", TerminalType.Input);
        Clock = Pin(15, "CLK", TerminalType.Input);
        _triggers[3] = Pin(16, "CLKTRG3", TerminalType.Input);
        Reset = Pin(17, "RESET", TerminalType.Input);
        ChannelSelect1 = Pin(18, "CS1", TerminalType.Input);
        ChannelSelect0 = Pin(19, "CS0", TerminalType.Input);
        ChipEnable = Pin(20, "CE", TerminalType.Input);
        _triggers[2] = Pin(21, "CLKTRG2", TerminalType.Input);
        _triggers[1] = Pin(22, "CLKTRG1", TerminalType.Input);
        _triggers[0] = Pin(23, "CLKTRG0", TerminalType.Input);
        Vcc = Pin(24, "VCC", TerminalType.Power);

        Terminals = [.. pins.Skip(1)];
        ConfigurePins(
            [ChipEnable, ChannelSelect0, ChannelSelect1, M1, IoRequest, ReadStrobe, InterruptEnableIn, Clock, Reset, .. _triggers, .. Data],
            [.. Data, .. zc, interrupt, ieo]);
    }

    private protected override int IntIndex => 11;

    private protected override int IeoIndex => 12;

    public override string PartNumber => "Z80 CTC";

    public override string ComponentType => "Z80 CTC";

    /// <summary>CLK/TRG0 to CLK/TRG3: a counter's input, or a timer's trigger.</summary>
    public IReadOnlyList<Terminal> Triggers => _triggers;

    /// <summary>ZC/TO0 to ZC/TO2: a pulse at each zero count. Channel 3 has none.</summary>
    public IReadOnlyList<Terminal> ZeroCount => [.. OutputTerminals.Skip(ZeroIndex).Take(3)];

    public Terminal ChannelSelect0 { get; }

    public Terminal ChannelSelect1 { get; }

    /// <summary>/RESET: low stops every channel.</summary>
    public Terminal Reset { get; }

    public override IReadOnlyList<string> InterruptSources { get; } = ["channel 0", "channel 1", "channel 2", "channel 3"];

    // ---- what a test or an agent wants to see -----------------------------------

    /// <summary>The vector register: bits 7-3 as written to channel 0.</summary>
    public byte Vector => _vector;

    /// <summary>A channel's control word, time constant (1-256), down-counter, whether it runs or waits, and how many times it has reached zero.</summary>
    public (byte Control, int TimeConstant, int Counter, bool Running, bool AwaitingConstant, bool AwaitingTrigger, bool Pending, long ZeroCounts) Describe(int channel)
    {
        var c = _channels[channel];
        return (c.Control, c.TimeConstant, c.Counter, c.Running, c.AwaitingConstant, c.AwaitingTrigger, c.Pending, c.ZeroCounts);
    }

    /// <summary>The period between a timer channel's zero counts at the clock given, in seconds; 0 for a counter or a stopped channel.</summary>
    public double TimerPeriod(int channel, double clockHz)
    {
        var c = _channels[channel];
        return c.CounterMode || clockHz <= 0 ? 0 : c.TimeConstant * c.Prescale / clockHz;
    }

    public override string ValueLabel =>
        string.Join(" ", _channels.Select((c, i) => c.Running ? string.Create(CultureInfo.InvariantCulture, $"{i}:{(c.CounterMode ? "C" : "T")}{c.TimeConstant}") : $"{i}:-"));

    public override string PinLabel(Terminal pin) => pin.Name switch
    {
        "CE" or "INT" or "RD" or "IORQ" or "M1" or "RESET" => "/" + pin.Name,
        "VCC" => "+5V",
        _ when pin.Name.StartsWith("ZCTO", StringComparison.Ordinal) => "ZC/TO" + pin.Name[4..],
        _ when pin.Name.StartsWith("CLKTRG", StringComparison.Ordinal) => "CLK/TRG" + pin.Name[6..],
        _ => pin.Name,
    };

    // ---- the logic --------------------------------------------------------------------

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var reset = context.ReadInput(Reset, Levels).IsLow();

        if (!context.IsInitializing)
        {
            if (reset && !_lastReset) ResetChannels();

            if (!reset)
            {
                var clock = context.ReadInput(Clock, Levels);
                if (clock.IsHigh() && _lastClock.IsLow()) SystemClock();
                _lastClock = clock;

                for (var i = 0; i < 4; i++)
                {
                    var high = context.ReadInput(_triggers[i], Levels).IsHigh();
                    var channel = _channels[i];
                    if (high != channel.Trigger && high == channel.RisingEdge) TriggerEdge(channel);
                    channel.Trigger = high;
                }
            }
        }

        _lastReset = reset;
        EvaluateBus(context);

        var delay = DelayFor(context);
        for (var i = 0; i < 3; i++) Request(context, ZeroIndex + i, _channels[i].OutputClocks > 0 ? LogicState.High : LogicState.Low, delay);
    }

    /// <summary>A rising edge on CLK: the ZC/TO pulses end, and the timers' prescalers count.</summary>
    private void SystemClock()
    {
        foreach (var channel in _channels)
        {
            if (channel.OutputClocks > 0) channel.OutputClocks--;
            if (!channel.Running || channel.CounterMode) continue;

            if (++channel.Prescaler >= channel.Prescale)
            {
                channel.Prescaler = 0;
                Count(channel);
            }
        }
    }

    /// <summary>An active edge on CLK/TRG: a counter counts; a timer waiting for its trigger starts.</summary>
    private void TriggerEdge(Channel channel)
    {
        if (channel.Running && channel.CounterMode)
        {
            Count(channel);
        }
        else if (channel.AwaitingTrigger)
        {
            channel.AwaitingTrigger = false;
            channel.Running = true;
            channel.Prescaler = 0;
        }
    }

    private void Count(Channel channel)
    {
        if (--channel.Counter > 0) return;

        channel.Counter = channel.TimeConstant;
        channel.OutputClocks = 1;
        channel.ZeroCounts++;
        if (channel.InterruptEnabled) channel.Pending = true;
        NotifyValueChanged();
    }

    private void ResetChannels()
    {
        foreach (var channel in _channels) channel.Reset();
        ResetChain();
        NotifyValueChanged();
    }

    // ---- the registers --------------------------------------------------------------

    private protected override int SelectedRegister(IDigitalContext context) =>
        (context.ReadInput(ChannelSelect0, Levels).IsHigh() ? 1 : 0) | (context.ReadInput(ChannelSelect1, Levels).IsHigh() ? 2 : 0);

    private protected override byte ReadRegister(int register) => (byte)_channels[register].Counter;

    private protected override void WriteRegister(int register, byte value)
    {
        var channel = _channels[register];
        if (channel.AwaitingConstant)
        {
            channel.AwaitingConstant = false;
            channel.TimeConstant = value == 0 ? 256 : value;
            if (!channel.Running && !channel.AwaitingTrigger)
            {
                // Started afresh: a timer at once or on its trigger, a counter at once.
                channel.Counter = channel.TimeConstant;
                channel.Prescaler = 0;
                if (!channel.CounterMode && (channel.Control & 0x08) != 0) channel.AwaitingTrigger = true;
                else channel.Running = true;
            }

            return;
        }

        if ((value & 0x01) != 0)
        {
            channel.Control = value;
            if ((value & 0x02) != 0)
            {
                channel.Running = false;
                channel.AwaitingTrigger = false;
            }

            if ((value & 0x04) != 0) channel.AwaitingConstant = true;
            if (!channel.InterruptEnabled) channel.Pending = false;
        }
        else if (register == 0)
        {
            _vector = (byte)(value & 0xF8);
        }
    }

    // ---- the chain ------------------------------------------------------------------

    private protected override bool Pending(int source) => _channels[source].Pending && _channels[source].InterruptEnabled;

    private protected override byte VectorFor(int source) => (byte)(_vector | (source << 1));

    private protected override void Acknowledged(int source) => _channels[source].Pending = false;

    public override void ResetLogic()
    {
        base.ResetLogic();
        foreach (var channel in _channels)
        {
            channel.Reset();
            channel.TimeConstant = 256;
            channel.Counter = 0;
            channel.ZeroCounts = 0;
            channel.Trigger = false;
        }

        _vector = 0;
        _lastClock = LogicState.Unknown;
        _lastReset = false;
    }

    private sealed record RunState(Channel[] Channels, byte Vector, ChainState Chain);

    public object CaptureRunState() => new RunState([.. _channels.Select(c => c.Copy())], _vector, CaptureChain());

    public void AdoptRunState(object state)
    {
        if (state is not RunState run) return;
        for (var i = 0; i < 4; i++) _channels[i] = run.Channels[i].Copy();
        _vector = run.Vector;
        AdoptChain(run.Chain);
        NotifyValueChanged();
    }
}
