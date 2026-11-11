using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// A TSOP38238 infrared receiver, and the remote pointed at it: OUT idles high and goes low for each 38 kHz burst the
/// remote sends, the carrier already taken off, as the real module's demodulator does.
/// <para>
/// The remote speaks NEC, as most cheap ones do: a 9 ms burst and a 4.5 ms space to start, then 32 bits least
/// significant first (the address, its inverse, the command, its inverse), each a 562.5 µs burst followed by a
/// 562.5 µs space for a 0 or a 1.6875 ms space for a 1, and a last burst to end. Double-click it to press the button
/// (<see cref="Command"/>), or set <see cref="SendAt"/> for a press at a time; a held button repeats with the short
/// repeat code every 108 ms when <see cref="Held"/> is on. There's no optics: the remote is built in.
/// </para>
/// </summary>
public sealed partial class IrReceiver : DigitalIc, IBreakpointSource, IInteractiveComponent
{
    private const double Unit = 562.5e-6;

    private readonly List<(double Time, bool Low)> _schedule = [];
    private int _next;
    private bool _pressRequested;
    private double _lastFrame = double.NaN;

    public IrReceiver() : base(3)
    {
        PropagationDelay = 0;
        Out = new Terminal("out", "OUT", TerminalType.Output, new Point(40, -14));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(0, 40));
        Vcc = new Terminal("vs", "VS", TerminalType.Power, new Point(-40, -14));
        Terminals = [Out, Gnd, Vcc];
        ConfigurePins([], [Out]);
    }

    public Terminal Out { get; }

    /// <summary>The remote's address, sent before the command.</summary>
    [ObservableProperty]
    public partial int Address { get; set; } = 0x00;

    /// <summary>The button pressed: 0x45 is a common remote's "1", 0x46 its "2".</summary>
    [ObservableProperty]
    public partial int Command { get; set; } = 0x45;

    /// <summary>A press at this time, in seconds; negative for none.</summary>
    [ObservableProperty]
    public partial double SendAt { get; set; } = -1;

    /// <summary>The button held down: repeat codes follow every 108 ms.</summary>
    [ObservableProperty]
    public partial bool Held { get; set; }

    public override string PartNumber => "TSOP38238";

    public override string ComponentType => "IR Receiver";

    public override string DesignatorPrefix => "IR";

    public override string ValueLabel => $"NEC {Address:X2}:{Command:X2}";

    public string InteractionHint => $"Press the remote's button (command 0x{Command:X2})";

    /// <summary>How many frames it has received from the remote.</summary>
    public int FramesSent { get; private set; }

    public void Interact() => _pressRequested = true;

    /// <summary>The bursts and spaces of a press, from <paramref name="start"/>: true while the carrier is on (OUT low).</summary>
    public static List<(double Time, bool Low)> Frame(int address, int command, double start)
    {
        var edges = new List<(double, bool)>();
        var t = start;
        void Burst(double length) { edges.Add((t, true)); t += length; }
        void Space(double length) { edges.Add((t, false)); t += length; }
        Burst(16 * Unit);
        Space(8 * Unit);
        var bits = (address & 0xFF) | ((~address & 0xFF) << 8) | ((command & 0xFF) << 16) | ((~command & 0xFF) << 24);
        for (var i = 0; i < 32; i++)
        {
            Burst(Unit);
            Space(((bits >> i) & 1) != 0 ? 3 * Unit : Unit);
        }

        Burst(Unit);
        Space(0);
        return edges;
    }

    /// <summary>The repeat code a held button sends: 9 ms, 2.25 ms, one burst.</summary>
    public static List<(double Time, bool Low)> Repeat(double start) =>
        [(start, true), (start + (16 * Unit), false), (start + (20 * Unit), true), (start + (21 * Unit), false)];

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var time = context.Time;
        if (context.IsInitializing)
        {
            Request(context, 0, LogicState.High, 0);
            return;
        }

        if (_pressRequested || (SendAt >= 0 && double.IsNaN(_lastFrame) && time >= SendAt))
        {
            _pressRequested = false;
            var start = SendAt >= 0 && double.IsNaN(_lastFrame) ? Math.Max(SendAt, time) : time;
            _schedule.Clear();
            _schedule.AddRange(Frame(Address, Command, start));
            _next = 0;
            _lastFrame = start;
            FramesSent++;
        }
        else if (Held && !double.IsNaN(_lastFrame) && _next >= _schedule.Count && time >= _lastFrame + 108e-3)
        {
            _lastFrame += 108e-3;
            _schedule.Clear();
            _schedule.AddRange(Repeat(_lastFrame));
            _next = 0;
        }

        var low = false;
        var any = false;
        while (_next < _schedule.Count && _schedule[_next].Time <= time + 1e-12)
        {
            low = _schedule[_next].Low;
            any = true;
            _next++;
        }

        if (any) Request(context, 0, low ? LogicState.Low : LogicState.High, 0);
    }

    public double? NextBreakpointAfter(double time)
    {
        foreach (var (t, _) in _schedule)
            if (t > time + 1e-12) return t;
        if (SendAt > time && double.IsNaN(_lastFrame)) return SendAt;
        if (Held && !double.IsNaN(_lastFrame)) return Math.Max(time + 1e-9, _lastFrame + 108e-3);
        return null;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        _schedule.Clear();
        _next = 0;
        _pressRequested = false;
        _lastFrame = double.NaN;
        FramesSent = 0;
    }
}
