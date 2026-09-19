using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// An HC-SR04 ultrasonic range finder — the pair of little cans on every robot ever built.
/// <para>
/// It does not report a distance. Pulse <c>TRIG</c> for ten microseconds and it chirps, then
/// raises <c>ECHO</c> for as long as the sound takes to come back. The distance is in <b>how long
/// that pulse lasts</b>, which is why reading one means timing an edge rather than reading a
/// value, and why it is the first thing anyone writes an interrupt handler for.
/// </para>
/// <para>
/// Sound goes about 343 metres a second, and the pulse covers the trip out and back — so it is
/// 58 microseconds per centimetre, near enough, and that factor is the whole of the arithmetic.
/// </para>
/// <para>
/// With nothing in range it does not stay quiet: it gives a long pulse of about 38 milliseconds
/// and gives up. Code that waits for the echo without a timeout therefore does not hang, it just
/// reports something absurd, which is a more annoying bug than hanging would have been.
/// </para>
/// </summary>
public sealed partial class UltrasonicRanger : DigitalComponent, IInteractiveComponent
{
    /// <summary>Microseconds of echo per centimetre, out and back at the speed of sound.</summary>
    private const double MicrosecondsPerCentimetre = 58.0;

    /// <summary>How long it waits after the trigger before the echo starts.</summary>
    private const double ChirpDelay = 450e-6;

    /// <summary>What it gives when nothing comes back.</summary>
    private const double TimeoutPulse = 38e-3;

    private bool _previousTrigger;
    private double _triggerRose = double.NaN;
    private double _echoStarts = double.NaN;
    private double _echoEnds = double.NaN;

    public UltrasonicRanger()
    {
        PropagationDelay = 1e-6;
        Levels = LogicLevels.Ttl;

        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-40, -30));
        Trigger = new Terminal("trig", "TRIG", TerminalType.Input, new Point(-40, -10));
        Echo = new Terminal("echo", "ECHO", TerminalType.Output, new Point(-40, 10));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-40, 30));

        Terminals = [Vcc, Trigger, Echo, Gnd];
        ConfigurePins([Trigger], [Echo]);
    }

    public Terminal Vcc { get; }

    /// <summary>A pulse of ten microseconds or more sets it off.</summary>
    public Terminal Trigger { get; }

    /// <summary>Goes high for as long as the sound takes to come back.</summary>
    public Terminal Echo { get; }

    public Terminal Gnd { get; }

    /// <summary>How far away whatever it is looking at is, in centimetres.</summary>
    [ObservableProperty]
    public partial double DistanceCentimetres { get; set; } = 50.0;

    /// <summary>Beyond this it reports nothing there. A real one manages about four metres.</summary>
    [ObservableProperty]
    public partial double MaximumRange { get; set; } = 400.0;

    /// <summary>Closer than this it cannot tell, the chirp still being in the air.</summary>
    [ObservableProperty]
    public partial double MinimumRange { get; set; } = 2.0;

    /// <summary>The distance it moves to when you double-click it.</summary>
    [ObservableProperty]
    public partial double AlternateDistance { get; set; } = 250.0;

    public override string ComponentType => "Ultrasonic";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => IsInRange
        ? $"{DistanceCentimetres:0.#} cm"
        : "out of range";

    public string InteractionHint => "Move the target";

    public void Interact() =>
        (DistanceCentimetres, AlternateDistance) = (AlternateDistance, DistanceCentimetres);

    /// <summary>True while there is something it can actually see.</summary>
    public bool IsInRange =>
        DistanceCentimetres >= MinimumRange && DistanceCentimetres <= MaximumRange;

    /// <summary>How long the echo pulse will be for the present distance, in seconds.</summary>
    public double EchoWidth => IsInRange
        ? DistanceCentimetres * MicrosecondsPerCentimetre * 1e-6
        : TimeoutPulse;

    /// <summary>True while the echo pin is up.</summary>
    public bool IsEchoing { get; private set; }

    protected override int ReferenceNode(Cirq.Core.Simulation.MnaSystem system) => system.Node(Gnd);

    public override void EvaluateLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);

        // Unpowered it does nothing at all, rather than sitting there echoing.
        if (context.NodeVoltage(Vcc) - context.NodeVoltage(Gnd) < 3.0)
        {
            IsEchoing = false;
            context.Schedule(this, 0, LogicState.Low, delay);
            return;
        }

        var trigger = context.ReadInput(Trigger, Levels) == LogicState.High;

        if (trigger && !_previousTrigger) _triggerRose = context.Time;

        if (!trigger && _previousTrigger && !double.IsNaN(_triggerRose))
        {
            // Ten microseconds is the figure in the datasheet; a shorter blip is ignored, which
            // is a real and irritating property of these.
            if (context.Time - _triggerRose >= 10e-6 && double.IsNaN(_echoStarts))
            {
                _echoStarts = context.Time + ChirpDelay;
                _echoEnds = _echoStarts + EchoWidth;
            }

            _triggerRose = double.NaN;
        }

        _previousTrigger = trigger;

        var echoing = !double.IsNaN(_echoStarts)
                      && context.Time >= _echoStarts
                      && context.Time < _echoEnds;

        if (!double.IsNaN(_echoEnds) && context.Time >= _echoEnds)
        {
            _echoStarts = double.NaN;
            _echoEnds = double.NaN;
        }

        IsEchoing = echoing;
        context.Schedule(this, 0, echoing ? LogicState.High : LogicState.Low, delay);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();

        _previousTrigger = false;
        _triggerRose = double.NaN;
        _echoStarts = double.NaN;
        _echoEnds = double.NaN;
        IsEchoing = false;
    }

    partial void OnDistanceCentimetresChanged(double value) => NotifyValueChanged();
}
