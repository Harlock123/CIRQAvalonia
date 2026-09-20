using Cirq.Core.Digital;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Buses;

/// <summary>
/// A serial terminal: the end you type at.
/// <para>
/// It sends what you put in <see cref="Message"/> once, after a moment, and shows whatever comes
/// back. There is no processor here to run a driver, so this stands in for the terminal program
/// on the other side of a USB-serial cable.
/// </para>
/// <para>
/// Wire <b>TX to the other end's RX</b>. Joining TX to TX is the mistake everybody makes once, and
/// it is silent: both ends sit there holding the line high at each other, and nothing whatsoever
/// happens.
/// </para>
/// </summary>
public sealed partial class SerialTerminal : UartPort
{
    /// <summary>What to send, once, when the delay is up.</summary>
    [ObservableProperty]
    public partial string Message { get; set; } = "Hello\r\n";

    /// <summary>How long to wait before sending, in seconds.</summary>
    [ObservableProperty]
    public partial double StartDelay { get; set; } = 1e-3;

    /// <summary>
    /// Seconds between repeats, or zero to send once and stop. A link that says its piece and
    /// falls silent has nothing left to look at a moment later, so set this to watch it work.
    /// </summary>
    [ObservableProperty]
    public partial double RepeatInterval { get; set; }

    public override string ComponentType => "Serial Terminal";

    public override string ValueLabel => BaudLabel;

    /// <summary>How many times the message has gone out.</summary>
    public int MessagesSent { get; private set; }

    /// <summary>True once the message has gone out at least once.</summary>
    public bool HasSent => MessagesSent > 0;

    private double _nextSendAt = double.NaN;

    private double DueAt => double.IsNaN(_nextSendAt) ? Math.Max(StartDelay, 0.0) : _nextSendAt;

    protected override double? NextScheduledSend(double time) =>
        double.IsPositiveInfinity(DueAt) ? null : DueAt;

    public override void EvaluateLogic(IDigitalContext context)
    {
        if (!context.IsInitializing && context.Time >= DueAt)
        {
            var due = DueAt;

            MessagesSent++;
            Send(Message);

            _nextSendAt = RepeatInterval > 0 ? due + RepeatInterval : double.PositiveInfinity;
        }

        base.EvaluateLogic(context);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        MessagesSent = 0;
        _nextSendAt = double.NaN;
    }

    partial void OnMessageChanged(string value) => NotifyValueChanged();
}

/// <summary>
/// A serial device at the far end of the wire — the sort of module that announces itself when it
/// powers up and answers what you send it.
/// <para>
/// It has a baud rate of its own, which is the whole reason it is here. Set it to something other
/// than the terminal's and the link does not fall silent: it produces <b>definite wrong
/// characters</b>, because a receiver counting at the wrong rate samples at the wrong instants and
/// reads a perfectly real byte that nobody sent. That, and a rising count of framing errors, is
/// exactly what the hardware gives you.
/// </para>
/// </summary>
public sealed partial class SerialDevice : UartPort
{
    /// <summary>Sent once on power-up, the way a real module greets you.</summary>
    [ObservableProperty]
    public partial string Greeting { get; set; } = "READY\r\n";

    /// <summary>Whether it sends back what it is given.</summary>
    [ObservableProperty]
    public partial bool Echo { get; set; } = true;

    /// <summary>Echoes in capitals, so it is obvious the far end and not the wire did it.</summary>
    [ObservableProperty]
    public partial bool EchoInCapitals { get; set; } = true;

    public override string ComponentType => "Serial Device";

    public override string ValueLabel => BaudLabel;

    protected override void OnStart()
    {
        if (!string.IsNullOrEmpty(Greeting)) Send(Greeting);
    }

    protected override void OnByteReceived(int value)
    {
        if (!Echo) return;

        Send(EchoInCapitals && value is >= 'a' and <= 'z' ? value - 32 : value);
    }

    /// <summary>
    /// What is wrong with the link, as this end sees it. A framing error is the only warning a
    /// UART gives, and it is worth saying out loud rather than leaving in a counter.
    /// </summary>
    public IReadOnlyList<string> Violations
    {
        get
        {
            List<string> found = [];

            if (FramingErrors > 0)
            {
                found.Add($"{FramingErrors} framing error(s) — the stop bit was not high where " +
                          "this end expected it, which is what a baud rate that does not match " +
                          "the other end looks like");
            }

            if (ParityErrors > 0)
                found.Add($"{ParityErrors} parity error(s) — the two ends disagree about parity");

            return found;
        }
    }

    partial void OnGreetingChanged(string value) => NotifyValueChanged();
}
