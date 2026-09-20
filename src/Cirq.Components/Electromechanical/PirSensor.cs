using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// A PIR motion sensor — the HC-SR501 sort, the white plastic dome on every security light.
/// <para>
/// It is a <b>passive</b> infrared sensor, which is the first thing worth getting straight: it
/// emits nothing. Behind the lens is a pyroelectric element that notices the infrared a warm body
/// gives off, and the segmented lens chops the scene into stripes so that something moving across
/// it sweeps warmth from one segment to the next. That is what it actually detects — not heat,
/// and certainly not presence, but <b>change</b> in heat across its field of view.
/// </para>
/// <para>
/// Which explains the two complaints everybody has about them. It will not see somebody standing
/// still, because nothing is sweeping between segments; and it will happily trigger on a radiator
/// coming on, a curtain moving in a draught, or sunlight crossing the floor.
/// </para>
/// <para>
/// Two behaviours matter when you wire one up, and both are here:
/// </para>
/// <para>
/// <b>It holds its output high long after the movement stops</b> — seconds to minutes, set by a
/// trimmer on the board. It is not reporting what is happening now; it is reporting that something
/// happened recently. Anything that samples the pin and believes it is seeing the present will be
/// wrong for the whole of the hold time.
/// </para>
/// <para>
/// <b>Retriggering is a jumper, and the wrong setting is maddening.</b> Retriggerable restarts the
/// hold on every fresh movement, so the output stays high while somebody keeps moving. Single-shot
/// does not: the output goes low at the end of the hold whatever is going on, then blanks for a
/// moment before it can fire again — so a light on a single-shot sensor goes out while you are
/// still standing under it.
/// </para>
/// <para>
/// And when power is first applied it needs a <b>warm-up</b> of some tens of seconds, during which
/// a real one produces nonsense. Here it simply refuses to trigger, which is the tidier half of
/// what actually happens and enough to explain a sensor that seems dead for the first minute.
/// </para>
/// </summary>
public sealed partial class PirSensor : DigitalComponent, IInteractiveComponent, IBreakpointSource
{
    private double _poweredAt = double.NegativeInfinity;
    private double _holdUntil = double.NegativeInfinity;
    private double _blankUntil = double.NegativeInfinity;
    private bool _previousMotion;

    public PirSensor()
    {
        PropagationDelay = 1e-3;
        Levels = LogicLevels.Cmos33V;

        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-30, -30));
        Output = new Terminal("out", "OUT", TerminalType.Output, new Point(30, 0));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-30, 30));

        Terminals = [Vcc, Output, Gnd];
        ConfigurePins([], [Output]);
    }

    public Terminal Vcc { get; }

    /// <summary>Push-pull, unlike the Hall switch — no pull-up needed.</summary>
    public Terminal Output { get; }

    public Terminal Gnd { get; }

    /// <summary>
    /// Something warm moving across its field of view. This is the control a person works: turn it
    /// on to walk past, off to leave the room.
    /// </summary>
    [ObservableProperty]
    [Operable("Movement")]
    public partial bool Movement { get; set; }

    /// <summary>How long the output stays high after movement stops, in seconds.</summary>
    [ObservableProperty]
    [Operable("Hold", Minimum = 0.5, Maximum = 300, Unit = "s", IsLogarithmic = true)]
    public partial double HoldSeconds { get; set; } = 5.0;

    /// <summary>
    /// Whether fresh movement during the hold restarts it. The jumper on the board, and the
    /// difference between a light that stays on while you are there and one that does not.
    /// </summary>
    [ObservableProperty]
    [Operable("Retrigger")]
    public partial bool IsRetriggerable { get; set; } = true;

    /// <summary>
    /// How long it ignores everything after the hold ends, in seconds. Only applies in
    /// single-shot mode, where a real board blanks for a few seconds before it will fire again.
    /// </summary>
    [ObservableProperty]
    public partial double BlankingSeconds { get; set; } = 3.0;

    /// <summary>How long after power it refuses to trigger, in seconds.</summary>
    [ObservableProperty]
    public partial double WarmUpSeconds { get; set; } = 30.0;

    /// <summary>Supply below which it does nothing, in volts. These run from 5 V, not 3.3.</summary>
    [ObservableProperty]
    public partial double MinimumSupplyVoltage { get; set; } = 4.5;

    public override string ComponentType => "PIR Sensor";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => IsWarmingUp ? "warming up" : IsTriggered ? "triggered" : "idle";

    public string InteractionHint => Movement ? "Stop moving" : "Walk past it";

    /// <summary>True while the output is high.</summary>
    public bool IsTriggered { get; private set; }

    /// <summary>True while it is still warming up and will not trigger however much you move.</summary>
    public bool IsWarmingUp { get; private set; }

    /// <summary>
    /// True while a single-shot sensor is blanked — movement is happening and being ignored,
    /// which is the state people mistake for a broken sensor.
    /// </summary>
    public bool IsBlanked { get; private set; }

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    public void Interact() => Movement = !Movement;

    public override void EvaluateLogic(IDigitalContext context)
    {
        var supply = context.NodeVoltage(Vcc) - context.NodeVoltage(Gnd);
        var delay = DelayFor(context);

        if (supply < MinimumSupplyVoltage)
        {
            _poweredAt = double.NegativeInfinity;
            _holdUntil = double.NegativeInfinity;
            _blankUntil = double.NegativeInfinity;
            IsTriggered = false;
            IsWarmingUp = false;
            IsBlanked = false;

            context.Schedule(this, 0, LogicState.Low, delay);
            return;
        }

        if (double.IsNegativeInfinity(_poweredAt)) _poweredAt = context.Time;

        var now = context.Time;

        IsWarmingUp = now - _poweredAt < WarmUpSeconds;
        IsBlanked = false;

        // A fresh movement is an edge, not a level: the element responds to warmth crossing the
        // lens, so standing still inside the field of view is the same to it as an empty room.
        var started = Movement && !_previousMotion;
        _previousMotion = Movement;

        if (started && !IsWarmingUp)
        {
            if (now >= _blankUntil && (IsRetriggerable || now >= _holdUntil))
                _holdUntil = now + Math.Max(HoldSeconds, 0.0);
            else if (now < _blankUntil)
                IsBlanked = true;
        }

        var high = now < _holdUntil;

        // Single-shot blanks once the hold runs out, which is when it stops noticing you.
        if (!IsRetriggerable && !high && _holdUntil > double.NegativeInfinity && now < _holdUntil + BlankingSeconds)
        {
            _blankUntil = _holdUntil + Math.Max(BlankingSeconds, 0.0);
            IsBlanked = Movement;
        }

        IsTriggered = high;

        context.Schedule(this, 0, high ? LogicState.High : LogicState.Low, delay);
    }

    /// <summary>
    /// The end of the hold is a scheduled edge and nothing else in the circuit knows it is coming,
    /// so the solver is told: a five second hold on a circuit stepping in milliseconds would
    /// otherwise end somewhere in the middle of a step.
    /// </summary>
    public double? NextBreakpointAfter(double time)
    {
        double? next = null;

        void Consider(double at)
        {
            if (at > time && (next is null || at < next)) next = at;
        }

        if (!double.IsNegativeInfinity(_poweredAt)) Consider(_poweredAt + WarmUpSeconds);
        if (!double.IsNegativeInfinity(_holdUntil)) Consider(_holdUntil);
        if (!double.IsNegativeInfinity(_blankUntil)) Consider(_blankUntil);

        return next;
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _poweredAt = double.NegativeInfinity;
        _holdUntil = double.NegativeInfinity;
        _blankUntil = double.NegativeInfinity;
        _previousMotion = false;

        IsTriggered = false;
        IsWarmingUp = false;
        IsBlanked = false;
    }

    partial void OnMovementChanged(bool value) => NotifyValueChanged();

    partial void OnHoldSecondsChanged(double value) => NotifyValueChanged();
}
