using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// An incremental rotary encoder: two contacts, staggered, and a shaft with detents.
/// <para>
/// It does not report a position. Turning it makes the two contacts open and close a quarter of a
/// cycle apart, and which one changes first is the only thing that says which way it went. That is
/// what <i>quadrature</i> means, and reading it is the whole job.
/// </para>
/// <para>
/// These are <b>mechanical contacts</b>, and that is why encoder code is harder than it looks.
/// Every edge bounces for a millisecond or two, and a bounce read as a transition sends the count
/// backwards and forwards at random. The bounce is modelled here rather than assumed away, which
/// means a naive counter will misread this part exactly as it would misread a real one — and the
/// 4093 and the 74HC14 already in the palette are what you reach for to fix it.
/// </para>
/// <para>
/// Double-click it to turn it one detent. Set <c>Reverse</c> to turn it the other way.
/// </para>
/// </summary>
public partial class RotaryEncoder : CircuitComponent, IInteractiveComponent
{
    /// <summary>Quadrature states across one detent: 00, 10, 11, 01, and back round.</summary>
    private static readonly (bool A, bool B)[] Sequence =
        [(false, false), (true, false), (true, true), (false, true)];

    private double _turnStartedAt = double.NegativeInfinity;
    private int _turnDirection;
    private int _lastDetent;

    private bool _contactA;
    private bool _contactB;

    public RotaryEncoder()
    {
        Common = new Terminal("com", "C", TerminalType.Passive, new Point(-40, 0));
        OutputA = new Terminal("a", "A", TerminalType.Output, new Point(40, -20));
        OutputB = new Terminal("b", "B", TerminalType.Output, new Point(40, 20));

        Terminals = [Common, OutputA, OutputB];
    }

    /// <summary>The common contact, usually taken to ground.</summary>
    public Terminal Common { get; }

    public Terminal OutputA { get; }

    public Terminal OutputB { get; }

    /// <summary>Resistance of a closed contact, in ohms.</summary>
    [ObservableProperty]
    public partial double ClosedResistance { get; set; } = 0.05;

    /// <summary>Resistance of an open contact, in ohms.</summary>
    [ObservableProperty]
    public partial double OpenResistance { get; set; } = 1e9;

    /// <summary>How long one detent of rotation takes, in seconds.</summary>
    [ObservableProperty]
    public partial double DetentDuration { get; set; } = 20e-3;

    /// <summary>How long each contact chatters after it changes, in seconds.</summary>
    [ObservableProperty]
    public partial double BounceDuration { get; set; } = 1.5e-3;

    /// <summary>Which way a double-click turns it.</summary>
    [ObservableProperty]
    public partial bool Reverse { get; set; }

    /// <summary>Detents turned since the start, negative the other way.</summary>
    [ObservableProperty]
    [Operable("Detent", Minimum = -50, Maximum = 50)]
    public partial int Detent { get; set; }

    public override string ComponentType => "Rotary Encoder";

    public override string DesignatorPrefix => "SW";

    public override string ValueLabel => IsTurning ? "turning" : $"{Detent} detent{(Math.Abs(Detent) == 1 ? "" : "s")}";

    public string InteractionHint => Reverse ? "Turn it anticlockwise" : "Turn it clockwise";

    /// <summary>True while a detent of rotation is still playing out.</summary>
    public bool IsTurning { get; private set; }

    /// <summary>Whether contact A is closed at the last solved point.</summary>
    public bool IsAClosed => _contactA;

    /// <summary>Whether contact B is closed at the last solved point.</summary>
    public bool IsBClosed => _contactB;

    /// <summary>
    /// Turning it one detent. It goes through <see cref="Detent"/> rather than round it, so that
    /// double-clicking the part on the canvas and dragging the control on the panel are the same
    /// action and produce the same quadrature.
    /// </summary>
    public void Interact() => Detent += Reverse ? -1 : 1;

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        UpdateContacts(state);

        var common = system.Node(Common);

        system.StampResistor(common, system.Node(OutputA),
            Math.Max(_contactA ? ClosedResistance : OpenResistance, 1e-9));
        system.StampResistor(common, system.Node(OutputB),
            Math.Max(_contactB ? ClosedResistance : OpenResistance, 1e-9));
    }

    /// <summary>
    /// Where the two contacts are right now: which quarter of the detent the shaft has reached,
    /// and whether the contact that just changed is still chattering.
    /// </summary>
    private void UpdateContacts(SimulationState state)
    {
        if (double.IsNaN(_turnStartedAt)) _turnStartedAt = state.Time;

        var elapsed = state.Time - _turnStartedAt;
        var duration = Math.Max(DetentDuration, 1e-9);

        IsTurning = elapsed >= 0 && elapsed < duration;

        // At rest both contacts are open, which is where a detent leaves the shaft.
        if (!IsTurning)
        {
            _contactA = false;
            _contactB = false;
            return;
        }

        var quarter = (int)Math.Clamp(elapsed / duration * 4.0, 0, 3);
        var index = _turnDirection >= 0 ? quarter : 3 - quarter;

        var (a, b) = Sequence[index];

        // Each quarter begins with the contact that changed bouncing. Deterministic rather than
        // random, so a circuit that copes once copes every time it is run.
        var intoQuarter = elapsed - (quarter * duration / 4.0);

        if (intoQuarter < BounceDuration)
        {
            var previous = Sequence[Math.Clamp(_turnDirection >= 0 ? index - 1 : index + 1, 0, 3)];

            if (a != previous.A) a = Chatter(state.Time, 0);
            if (b != previous.B) b = Chatter(state.Time, 1);
        }

        _contactA = a;
        _contactB = b;
    }

    /// <summary>A contact mid-bounce, deterministic in time so runs repeat exactly.</summary>
    private static bool Chatter(double time, int contact)
    {
        var ticks = (long)(time * 1e6) + contact;
        var mixed = (ulong)ticks * 6364136223846793005UL;
        return ((mixed >> 33) & 1) == 0;
    }

    public override void ResetState()
    {
        _turnStartedAt = double.NegativeInfinity;
        _turnDirection = 0;
        _lastDetent = Detent;
        _contactA = false;
        _contactB = false;
        IsTurning = false;
        Detent = 0;
    }

    /// <summary>
    /// Moving the detent <i>is</i> the turn. Without this the control on the panel would change a
    /// number and nothing else, because the contacts are driven by a turn in progress rather than
    /// by where the shaft has come to rest — and at rest both contacts are open, so the outputs
    /// would never move whatever the panel said.
    /// </summary>
    partial void OnDetentChanged(int value)
    {
        if (value != _lastDetent)
        {
            _turnDirection = value > _lastDetent ? 1 : -1;
            _turnStartedAt = double.NaN;     // picked up at the next time point
            _lastDetent = value;
        }

        NotifyValueChanged();
    }
}
