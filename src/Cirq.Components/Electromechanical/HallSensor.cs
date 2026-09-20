using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// A Hall effect switch — the A3144 sort, and the part behind every fan tachometer, bicycle
/// speedometer and brushless motor that knows where its rotor is.
/// <para>
/// Two things about it catch people. The first is that the output is <b>open collector</b>: it can
/// pull the pin down or let go of it, and nothing else. With no pull-up resistor it does not work
/// badly, it does not work at all — exactly as an I²C bus does not, and for the same reason.
/// </para>
/// <para>
/// The second is <b>hysteresis</b>, and it is the whole reason the part is usable. A magnet
/// approaching a sensor with a single threshold would make the output chatter as it wobbled either
/// side of it, and a wheel magnet passing at speed would produce a burst of pulses instead of one.
/// So it turns on at a stronger field than it turns off at — here about twenty millitesla to
/// operate and ten to release — and between those two it simply remembers what it was doing.
/// </para>
/// <para>
/// Most of these are also <b>unipolar</b>: they answer to a south pole and ignore a north one
/// entirely. Turning the magnet round and getting nothing is the other half-hour people lose to
/// this part, so the polarity is modelled rather than taken as read.
/// </para>
/// </summary>
public sealed partial class HallSensor : DigitalComponent, IInteractiveComponent
{
    private bool _latched;

    public HallSensor()
    {
        PropagationDelay = 2e-6;
        Levels = LogicLevels.Ttl;

        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(-30, -30));
        Output = new Terminal("out", "OUT", TerminalType.Output, new Point(30, 0));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-30, 30));

        Terminals = [Vcc, Output, Gnd];
        ConfigurePins([], [Output]);
    }

    public Terminal Vcc { get; }

    /// <summary>Open collector: pulled down or released, never driven high.</summary>
    public Terminal Output { get; }

    public Terminal Gnd { get; }

    /// <summary>
    /// Field at the sensor face, in millitesla. Positive is the pole it answers to; negative is
    /// the one it ignores.
    /// </summary>
    [ObservableProperty]
    public partial double FluxDensity { get; set; }

    /// <summary>What the field swings to when you double-click it — a magnet brought up close.</summary>
    [ObservableProperty]
    public partial double AlternateFlux { get; set; } = 35.0;

    /// <summary>Field at which the output turns on, in millitesla.</summary>
    [ObservableProperty]
    public partial double OperatePoint { get; set; } = 20.0;

    /// <summary>Field at which it turns off again. Below the operate point — that is the hysteresis.</summary>
    [ObservableProperty]
    public partial double ReleasePoint { get; set; } = 10.0;

    /// <summary>
    /// Whether it answers to one pole only. Real switches mostly do; a latching or omnipolar part
    /// does not, and this is the switch that makes it behave like one of those instead.
    /// </summary>
    [ObservableProperty]
    public partial bool IsUnipolar { get; set; } = true;

    /// <summary>Supply below which it stops working, in volts.</summary>
    [ObservableProperty]
    public partial double MinimumSupplyVoltage { get; set; } = 4.5;

    public override string ComponentType => "Hall Sensor";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => $"{FluxDensity:0.#} mT";

    public string InteractionHint => "Bring a magnet up and take it away";

    /// <summary>True while the output is pulling down, which is what "detected" looks like.</summary>
    public bool IsDetecting => _latched;

    /// <summary>
    /// True when the output has been released and nothing pulled it up — the missing pull-up,
    /// which is the single most common reason one of these appears dead.
    /// </summary>
    public bool HasNoPullUp { get; private set; }

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    public void Interact() => (FluxDensity, AlternateFlux) = (AlternateFlux, FluxDensity);

    /// <summary>The field as this part sees it: signed if unipolar, magnitude if not.</summary>
    private double EffectiveField => IsUnipolar ? FluxDensity : Math.Abs(FluxDensity);

    public override void EvaluateLogic(IDigitalContext context)
    {
        var supply = context.NodeVoltage(Vcc) - context.NodeVoltage(Gnd);
        var delay = DelayFor(context);

        if (supply < MinimumSupplyVoltage)
        {
            _latched = false;
            HasNoPullUp = false;
            context.Schedule(this, 0, LogicState.HighImpedance, delay);
            return;
        }

        var field = EffectiveField;

        // Between the two thresholds it holds whatever it was doing, which is the whole point.
        if (field >= OperatePoint) _latched = true;
        else if (field < ReleasePoint) _latched = false;

        context.Schedule(this, 0, _latched ? LogicState.Low : LogicState.HighImpedance, delay);

        // Released and still sitting near ground means nothing is pulling it up. Only worth
        // looking at while released — while it is pulling down, low is the right answer.
        var atOutput = context.NodeVoltage(Output) - context.NodeVoltage(Gnd);
        HasNoPullUp = !_latched && atOutput < supply * 0.3;
    }

    /// <summary>What is wrong with how this part is wired, if anything.</summary>
    public IReadOnlyList<string> Violations =>
        HasNoPullUp
            ? ["the output is open collector and nothing is pulling it up, so it can only ever " +
               "read low — fit a resistor from OUT to the supply"]
            : [];

    public override void ResetLogic()
    {
        base.ResetLogic();

        _latched = false;
        HasNoPullUp = false;
    }

    partial void OnFluxDensityChanged(double value) => NotifyValueChanged();
}
