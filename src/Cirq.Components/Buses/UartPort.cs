using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>The extra bit a frame can carry, and what it is for.</summary>
public enum UartParity
{
    /// <summary>No parity bit. Eight data bits, one stop bit — what nearly everything uses.</summary>
    None,

    /// <summary>A bit making the number of ones even.</summary>
    Even,

    /// <summary>A bit making the number of ones odd.</summary>
    Odd,
}

/// <summary>
/// One end of an asynchronous serial link.
/// <para>
/// UART is the bus with no clock line, and everything awkward about it follows from that. The
/// receiver has no way of being told when a bit begins, so it is built to assume: it waits for the
/// line to fall out of idle, starts its <b>own</b> clock, and samples in the middle of where it
/// believes each bit to be. The two ends never agree on timing — they only agree in advance on a
/// number, and then each counts for itself.
/// </para>
/// <para>
/// That is why a wrong baud rate produces garbage rather than nothing, and why the garbage is
/// reproducible rather than random: a receiver counting at the wrong rate samples at the wrong
/// instants and reads a perfectly definite wrong byte. It is modelled that way here — the receiver
/// really does sample on its own clock — so setting the two ends to different rates shows you what
/// it actually does, and the stop bit arriving low is the <b>framing error</b> that tells you.
/// </para>
/// </summary>
public abstract partial class UartPort : DigitalComponent, IBreakpointSource
{
    /// <summary>Levels of the frame being transmitted, start bit first, or empty while idle.</summary>
    private bool[] _frame = [];

    private double _frameStart = double.NaN;

    private readonly Queue<int> _outgoing = new();

    private bool _previousReceive = true;
    private double _sampleAt = double.NaN;
    private int _sampleIndex;
    private int _shift;

    protected UartPort()
    {
        PropagationDelay = 20e-9;
        Levels = LogicLevels.Cmos33V;

        Transmit = new Terminal("tx", "TX", TerminalType.Output, new Point(40, -20));
        Receive = new Terminal("rx", "RX", TerminalType.Input, new Point(40, 20));
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-40, -20));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-40, 20));

        Terminals = [Vcc, Transmit, Receive, Gnd];
        ConfigurePins([Receive], [Transmit]);
    }

    /// <summary>Data out of this end. It goes to the other end's <see cref="Receive"/>, never its TX.</summary>
    public Terminal Transmit { get; }

    public Terminal Receive { get; }

    public Terminal Vcc { get; }

    public Terminal Gnd { get; }

    /// <summary>
    /// Bits per second. Both ends have one, and nothing makes them agree — which is the point.
    /// </summary>
    [ObservableProperty]
    public partial double BaudRate { get; set; } = 9600;

    [ObservableProperty]
    public partial UartParity Parity { get; set; } = UartParity.None;

    public override string DesignatorPrefix => "U";

    /// <summary>One bit's worth of time at this end's rate, in seconds.</summary>
    public double BitSeconds => 1.0 / Math.Max(BaudRate, 1.0);

    /// <summary>Characters this end has received, in order.</summary>
    public List<int> ReceivedBytes { get; } = [];

    /// <summary>What has arrived, as text, with anything unprintable shown as a dot.</summary>
    public string ReceivedText =>
        string.Concat(ReceivedBytes.Select(b => b is >= 0x20 and < 0x7F ? (char)b : '.'));

    /// <summary>
    /// Frames whose stop bit was not high. This is what a baud mismatch looks like from the
    /// receiving end, and it is the only symptom the hardware gives you.
    /// </summary>
    public int FramingErrors { get; private set; }

    /// <summary>Frames whose parity bit did not agree with the data.</summary>
    public int ParityErrors { get; private set; }

    /// <summary>True while a frame is going out.</summary>
    public bool IsSending => _frame.Length > 0;

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    /// <summary>Queues a byte to go out as soon as the line is free.</summary>
    protected void Send(int value) => _outgoing.Enqueue(value & 0xFF);

    /// <summary>Queues every character of a string.</summary>
    protected void Send(string text)
    {
        foreach (var c in text) Send(c);
    }

    /// <summary>Called for each byte this end decodes. Errors arrive as their own calls.</summary>
    protected virtual void OnByteReceived(int value)
    {
    }

    /// <summary>Called once, the first time the part is evaluated with power on it.</summary>
    protected virtual void OnStart()
    {
    }

    private bool _started;

    /// <summary>Supply below which the port stops driving, in volts.</summary>
    [ObservableProperty]
    public partial double MinimumSupplyVoltage { get; set; } = 2.0;

    public override void EvaluateLogic(IDigitalContext context)
    {
        // Unpowered it lets go of the line rather than holding it idle-high, because a port with
        // no supply is not politely idle — it is absent, and the other end's pull-up is what
        // decides what the wire does.
        if (context.NodeVoltage(Vcc) - context.NodeVoltage(Gnd) < MinimumSupplyVoltage)
        {
            context.Schedule(this, 0, LogicState.HighImpedance, DelayFor(context));
            return;
        }

        if (!_started && !context.IsInitializing)
        {
            _started = true;
            OnStart();
        }

        ReceiveStep(context);
        TransmitStep(context);
    }

    // ---- transmitting ----------------------------------------------------

    private void TransmitStep(IDigitalContext context)
    {
        var bit = BitSeconds;

        if (_frame.Length > 0)
        {
            var index = (int)Math.Floor((context.Time - _frameStart) / bit);

            if (index >= _frame.Length)
            {
                _frame = [];
                _frameStart = double.NaN;
            }
            else if (index >= 0)
            {
                Drive(context, _frame[index]);
                return;
            }
        }

        if (_frame.Length == 0 && _outgoing.Count > 0)
        {
            _frame = BuildFrame(_outgoing.Dequeue());
            _frameStart = context.Time;
            Drive(context, _frame[0]);
            return;
        }

        // Idle is high. A line resting low is a break, not a rest.
        Drive(context, true);
    }

    private void Drive(IDigitalContext context, bool high) =>
        context.Schedule(this, 0, high ? LogicState.High : LogicState.Low, DelayFor(context));

    /// <summary>
    /// A frame: the start bit low, eight data bits least significant first, parity if it is on,
    /// then the stop bit high. The order matters — sending the most significant bit first is a
    /// classic way to produce something that looks almost right.
    /// </summary>
    private bool[] BuildFrame(int value)
    {
        List<bool> bits = [false];

        var ones = 0;
        for (var i = 0; i < 8; i++)
        {
            var high = (value & (1 << i)) != 0;
            if (high) ones++;
            bits.Add(high);
        }

        if (Parity != UartParity.None)
            bits.Add(Parity == UartParity.Even ? ones % 2 != 0 : ones % 2 == 0);

        bits.Add(true);
        return [.. bits];
    }

    // ---- receiving -------------------------------------------------------

    /// <summary>Bits in a frame after the start bit: eight data, parity if on, and the stop.</summary>
    private int FrameLength => 8 + (Parity == UartParity.None ? 0 : 1) + 1;

    private void ReceiveStep(IDigitalContext context)
    {
        var line = context.ReadInput(Receive, Levels) != LogicState.Low;
        var bit = BitSeconds;

        // Out of idle: the falling edge is the only synchronisation there is.
        if (double.IsNaN(_sampleAt) && _previousReceive && !line)
        {
            _sampleIndex = 0;
            _shift = 0;

            // Half a bit in, to land in the middle of the start bit rather than on its edge.
            _sampleAt = context.Time + (bit / 2.0);
        }

        _previousReceive = line;

        while (!double.IsNaN(_sampleAt) && context.Time >= _sampleAt)
        {
            Sample(line);
            _sampleAt = double.IsNaN(_sampleAt) ? double.NaN : _sampleAt + bit;
        }
    }

    private void Sample(bool high)
    {
        if (_sampleIndex == 0)
        {
            // A start bit that is not still low half a bit later was noise, not a byte.
            if (high)
            {
                _sampleAt = double.NaN;
                return;
            }

            _sampleIndex++;
            return;
        }

        var dataBits = 8;
        var parityAt = Parity == UartParity.None ? -1 : dataBits + 1;
        var stopAt = FrameLength;

        if (_sampleIndex <= dataBits)
        {
            if (high) _shift |= 1 << (_sampleIndex - 1);
            _sampleIndex++;
            return;
        }

        if (_sampleIndex == parityAt)
        {
            var ones = System.Numerics.BitOperations.PopCount((uint)_shift);
            var expected = Parity == UartParity.Even ? ones % 2 != 0 : ones % 2 == 0;

            if (high != expected) ParityErrors++;

            _sampleIndex++;
            return;
        }

        if (_sampleIndex == stopAt)
        {
            // The stop bit is high by definition, so finding it low means this end counted the
            // frame out wrongly — which is what a baud rate that does not match feels like.
            if (!high) FramingErrors++;
            else
            {
                ReceivedBytes.Add(_shift);
                OnByteReceived(_shift);
            }

            _sampleAt = double.NaN;
            _sampleIndex = 0;
            _shift = 0;
        }
    }

    // ---- timing ----------------------------------------------------------

    /// <summary>
    /// The engine is told about both clocks: the bit boundaries this end is transmitting on, and
    /// the instants it intends to sample at. Without the second, a receiver would only look at the
    /// line when something else happened to move it, and would read whatever it found.
    /// </summary>
    public double? NextBreakpointAfter(double time)
    {
        double? next = null;

        if (_frame.Length > 0)
        {
            var bit = BitSeconds;

            for (var i = 0; i <= _frame.Length; i++)
            {
                var at = _frameStart + (i * bit);
                if (at > time) { next = at; break; }
            }
        }

        if (!double.IsNaN(_sampleAt) && _sampleAt > time && (next is null || _sampleAt < next))
            next = _sampleAt;

        var scheduled = NextScheduledSend(time);
        if (scheduled is { } send && send > time && (next is null || send < next)) next = send;

        return next;
    }

    /// <summary>A subclass that starts talking at a particular moment says so here.</summary>
    protected virtual double? NextScheduledSend(double time) => null;

    public override void ResetLogic()
    {
        base.ResetLogic();

        _frame = [];
        _frameStart = double.NaN;
        _outgoing.Clear();

        _previousReceive = true;
        _sampleAt = double.NaN;
        _sampleIndex = 0;
        _shift = 0;
        _started = false;

        ReceivedBytes.Clear();
        FramingErrors = 0;
        ParityErrors = 0;
    }

    /// <summary>
    /// Written out rather than run through the SI formatter: nobody writes a serial rate as
    /// "9.6 kbd", and the number is the thing both ends have to be set to.
    /// </summary>
    protected string BaudLabel => $"{BaudRate:0} baud";

    partial void OnBaudRateChanged(double value) => NotifyValueChanged();

    partial void OnParityChanged(UartParity value) => NotifyValueChanged();
}
