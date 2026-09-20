using Cirq.Components.Passive;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// A reed switch: two springy ferrous blades in a glass tube that pull together when a magnet
/// comes near. It is the other way to notice a magnet, and comparing it with the
/// <see cref="HallSensor"/> beside it is most of the reason to have both.
/// <para>
/// What it has over a Hall switch is that it is a <b>contact</b>, not a semiconductor. It needs no
/// supply at all, it draws nothing when idle, it passes current either way round, and it will
/// switch mains if you ask it to. Every door and window sensor in every alarm system is one of
/// these, sitting on a long cable with nothing but the switch at the far end — which a part
/// needing three wires and a pull-up could not do.
/// </para>
/// <para>
/// What it has against it is everything mechanical. It is slow, a few hundred microseconds rather
/// than a few. It wears out, in millions of operations rather than never. And it
/// <b>bounces</b>: the blades snap together, spring apart, and snap together again, several times
/// over the first millisecond or so. A Hall switch does not do this, and that difference is the
/// point of the property below.
/// </para>
/// <para>
/// Bounce is invisible to anything slow — a lamp, a relay, a person — and impossible to ignore
/// for anything fast. Feed one of these straight into a counter and a single magnet passing will
/// be counted five times; feed it into a microcontroller interrupt and you will get five
/// interrupts. That is what debouncing is for, and this is the part that shows why it is needed
/// rather than merely asserting it.
/// </para>
/// </summary>
public sealed partial class ReedSwitch : MechanicalContact, IInteractiveComponent, IBreakpointSource
{
    private bool _commanded;
    private bool _contact;
    private double _commandedAt = double.NegativeInfinity;

    public ReedSwitch()
    {
        A = new Terminal("a", "A", TerminalType.Passive, new Point(-30, 0));
        B = new Terminal("b", "B", TerminalType.Passive, new Point(30, 0));

        Terminals = [A, B];

        // Glass-encapsulated contacts are cleaner than an ordinary switch's, and the blades are
        // thin, so the closed resistance is higher than a toggle's rather than lower.
        ClosedResistance = 0.1;
    }

    public Terminal A { get; }

    public Terminal B { get; }

    /// <summary>
    /// Field at the switch, in millitesla. Reed switches are usually specified in ampere-turns,
    /// but millitesla is what the Hall switch next to it uses and comparing the two is the point.
    /// </summary>
    [ObservableProperty]
    [Operable("Field", Minimum = -30, Maximum = 30, Unit = "mT")]
    public partial double FluxDensity { get; set; }

    /// <summary>What the field swings to when you double-click it — a magnet brought up close.</summary>
    [ObservableProperty]
    public partial double AlternateFlux { get; set; } = 12.0;

    /// <summary>Field at which the blades pull together, in millitesla.</summary>
    [ObservableProperty]
    public partial double OperatePoint { get; set; } = 8.0;

    /// <summary>
    /// Field at which they spring apart again. Lower than the operate point, because the blades
    /// are now touching and the gap the field has to hold shut is no gap at all — the hysteresis
    /// is mechanical here rather than designed in, and it is wider than a Hall switch's.
    /// </summary>
    [ObservableProperty]
    public partial double ReleasePoint { get; set; } = 4.0;

    /// <summary>
    /// A reed switch answers to either pole: the blades are ferrous, not magnetised, so they are
    /// drawn together whichever way round the magnet is. Most Hall switches are not like this.
    /// </summary>
    public bool IsOmnipolar => true;

    /// <summary>How long the contacts bounce for on closing, in seconds.</summary>
    [ObservableProperty]
    public partial double BounceSeconds { get; set; } = 500e-6;

    /// <summary>
    /// How many times they bounce. Odd, so that the last stretch is the closed one — an even
    /// count would end the bounce with the contact open, which is not what happens.
    /// </summary>
    [ObservableProperty]
    public partial int BounceCount { get; set; } = 5;

    public override string ComponentType => "Reed Switch";

    public override string DesignatorPrefix => "SW";

    public override string ValueLabel => $"{FluxDensity:0.#} mT";

    public string InteractionHint => "Bring a magnet up and take it away";

    /// <summary>
    /// True while the contacts are actually touching, bounce and all.
    /// <para>
    /// Before anything has been simulated there is no contact state to report, only a field, so
    /// it answers from that instead — otherwise a switch sitting in a magnet would be drawn open
    /// on the canvas until the simulation was started.
    /// </para>
    /// </summary>
    public bool IsClosed =>
        double.IsNegativeInfinity(_commandedAt) ? Math.Abs(FluxDensity) >= OperatePoint : _contact;

    /// <summary>True where the field says it should be closed, ignoring the bounce.</summary>
    public bool IsOperated =>
        double.IsNegativeInfinity(_commandedAt) ? Math.Abs(FluxDensity) >= OperatePoint : _commanded;

    /// <summary>True while the contacts are still settling, which is when they misbehave.</summary>
    public bool IsBouncing { get; private set; }

    public void Interact() => (FluxDensity, AlternateFlux) = (AlternateFlux, FluxDensity);

    /// <summary>How long one stretch of the bounce lasts, in seconds.</summary>
    private double BounceStep => Math.Max(BounceSeconds, 0.0) / Math.Max(BounceCount, 1);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        Update(state);
        StampContact(system, A, B, _contact);
    }

    /// <summary>
    /// Works out what the contacts are doing now. Called from the stamp rather than from the
    /// property setter, because a magnet moving is not an event the component is told about — the
    /// field is simply a different number the next time anybody looks.
    /// </summary>
    private void Update(SimulationState state)
    {
        var field = Math.Abs(FluxDensity);

        // Between the thresholds it holds whatever it was doing.
        var commanded = _commanded switch
        {
            false when field >= OperatePoint => true,
            true when field < ReleasePoint => false,
            _ => _commanded,
        };

        if (commanded != _commanded)
        {
            _commanded = commanded;
            _commandedAt = state.Time;
        }

        var elapsed = state.Time - _commandedAt;

        // Only closing bounces. Opening, the blades spring apart and stay apart: there is nothing
        // for them to rebound off.
        var bouncing = _commanded && BounceCount > 1 && BounceSeconds > 0 && elapsed < BounceSeconds;

        IsBouncing = bouncing;

        if (!bouncing)
        {
            _contact = _commanded;
            return;
        }

        // Alternating stretches, starting closed — the blades hit first and rebound after.
        var stretch = (int)Math.Floor(elapsed / BounceStep);

        _contact = stretch % 2 == 0;
    }

    /// <summary>
    /// Each rebound is a step change in resistance, so the solver is told where they are. Without
    /// this a bounce shorter than the time step is stepped straight over, and the part that the
    /// whole component exists to demonstrate quietly does not happen.
    /// </summary>
    public double? NextBreakpointAfter(double time)
    {
        if (!_commanded || BounceCount <= 1 || BounceSeconds <= 0) return null;
        if (double.IsNegativeInfinity(_commandedAt)) return null;

        var elapsed = time - _commandedAt;
        if (elapsed >= BounceSeconds) return null;

        var next = Math.Floor(Math.Max(elapsed, 0.0) / BounceStep) + 1;
        var at = _commandedAt + (next * BounceStep);

        return at <= time ? _commandedAt + BounceSeconds : Math.Min(at, _commandedAt + BounceSeconds);
    }

    public override double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state)
    {
        var current = ContactCurrent(system, A, B, _contact);

        return ReferenceEquals(terminal, A) ? current : -current;
    }

    public override void ResetState()
    {
        _commanded = false;
        _contact = false;
        _commandedAt = double.NegativeInfinity;
        IsBouncing = false;
    }

    partial void OnFluxDensityChanged(double value) => NotifyValueChanged();
}
