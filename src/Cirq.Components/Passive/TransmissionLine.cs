using System.Numerics;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// A transmission line — a length of coax, or a pair of traces on a board, treated as what it
/// actually is rather than as a wire.
/// <para>
/// Every other connection in this library is instantaneous: change a voltage at one end and it is
/// that voltage at the other end in the same instant, because a wire is a wire. That assumption is
/// the one that fails first as things get faster, and it fails in a way nothing else in a
/// schematic hints at.
/// </para>
/// <para>
/// What a line really does is carry a <b>wave</b>. A step launched into one end travels at some
/// fraction of the speed of light and arrives later — five nanoseconds for a metre of ordinary
/// coax. While it is in flight the line looks to the driver like a plain resistor of
/// Z<sub>0</sub> ohms, whatever is at the far end, because nothing about the far end has reached
/// the driver yet. When the wave gets there, whatever it finds decides how much of it comes
/// <b>back</b>: a matched load absorbs it and that is the end of the story, an open end sends all
/// of it back the same way up, and a short sends all of it back inverted. Then that reflection
/// travels home and meets whatever the source impedance is, and some of it turns round again.
/// </para>
/// <para>
/// That is the whole of it, and it explains the things that otherwise look like faults in the
/// equipment: the staircase on a scope when a fast edge drives an unterminated cable, the
/// overshoot and ringing on a logic edge into a long trace, why a series resistor at the
/// <i>driver</i> quietens a line that a resistor at the far end could not, and why a 10 cm track
/// is a wire at 1 MHz and a component at 500 MHz.
/// </para>
/// <para>
/// The model is the classical one: each end is a source of whatever arrived from the other end one
/// delay ago, behind the characteristic impedance. It needs the solver to remember what the ends
/// were doing in the past rather than one step ago, which nothing else here does, and it needs the
/// time step kept below the delay — a line cannot be simulated by stepping over it.
/// </para>
/// </summary>
public partial class TransmissionLine : CircuitComponent, IBreakpointSource
{
    /// <summary>Speed of light in vacuum, metres per second.</summary>
    private const double SpeedOfLight = 299_792_458.0;

    private readonly record struct Sample(double Time, double V1, double I1, double V2, double I2);

    private readonly List<Sample> _history = [];

    public TransmissionLine()
    {
        NearPlus = new Terminal("a+", "A+", TerminalType.Passive, new Point(-50, -15));
        NearMinus = new Terminal("a-", "A-", TerminalType.Passive, new Point(-50, 15));
        FarPlus = new Terminal("b+", "B+", TerminalType.Passive, new Point(50, -15));
        FarMinus = new Terminal("b-", "B-", TerminalType.Passive, new Point(50, 15));

        Terminals = [NearPlus, NearMinus, FarPlus, FarMinus];
    }

    public Terminal NearPlus { get; }

    public Terminal NearMinus { get; }

    public Terminal FarPlus { get; }

    public Terminal FarMinus { get; }

    /// <summary>
    /// Characteristic impedance in ohms — what the line looks like to whatever drives it, for as
    /// long as it takes the far end to answer. Fifty for instrument coax, seventy-five for video,
    /// around a hundred for a differential pair on a board.
    /// </summary>
    [ObservableProperty]
    public partial double CharacteristicImpedance { get; set; } = 50.0;

    /// <summary>Length in metres.</summary>
    [ObservableProperty]
    public partial double Length { get; set; } = 1.0;

    /// <summary>
    /// How fast a wave travels along it, as a fraction of the speed of light. About two thirds for
    /// solid-dielectric coax and for a track in a board, nearer 0.85 for foam.
    /// </summary>
    [ObservableProperty]
    public partial double VelocityFactor { get; set; } = 0.66;

    /// <summary>
    /// One-way loss over the whole length, in decibels. Zero is a perfect line, which is the usual
    /// place to start; a few tenths is a real cable at a few hundred megahertz, and it is what
    /// makes a long line's ringing die away rather than going on for ever.
    /// </summary>
    [ObservableProperty]
    public partial double LossDecibels { get; set; }

    /// <summary>
    /// How finely the solver is made to step, as a fraction of the delay. A line cannot be
    /// simulated by stepping over it: a step longer than the delay steps past the whole of the
    /// behaviour, so the component asks the engine for time points of its own.
    /// </summary>
    [ObservableProperty]
    public partial int StepsPerDelay { get; set; } = 8;

    public override string ComponentType => "Transmission Line";

    public override string DesignatorPrefix => "TL";

    public override string ValueLabel =>
        $"{CharacteristicImpedance:0.#} Ω, {SiPrefix.Format(Delay, "s")}";

    /// <summary>One branch at each end, since each is a voltage source behind the line impedance.</summary>
    public override int VoltageSourceCount => 2;

    /// <summary>How long a wave takes to get from one end to the other, in seconds.</summary>
    public double Delay => Math.Max(Length, 0.0) / (SpeedOfLight * Math.Clamp(VelocityFactor, 0.01, 1.0));

    /// <summary>What a wave is multiplied by in travelling the length of the line, one way.</summary>
    public double Transmission => Math.Pow(10.0, -Math.Max(LossDecibels, 0.0) / 20.0);

    /// <summary>Voltage across the near end at the last accepted point.</summary>
    public double NearVoltage { get; private set; }

    /// <summary>Voltage across the far end at the last accepted point.</summary>
    public double FarVoltage { get; private set; }

    /// <summary>Current into the near end at the last accepted point, in amps.</summary>
    public double NearCurrent { get; private set; }

    /// <summary>Current into the far end, in amps.</summary>
    public double FarCurrent { get; private set; }

    /// <summary>
    /// The fraction of a wave that comes back from a load of <paramref name="load"/> ohms:
    /// zero for a matched load, +1 for an open end and −1 for a short. Not used by the solver —
    /// the circuit works it out for itself — but it is the number the textbook gives, and having
    /// it lets a test check the simulation against the arithmetic.
    /// </summary>
    public double ReflectionFrom(double load)
    {
        if (double.IsPositiveInfinity(load)) return 1.0;

        var z0 = Math.Max(CharacteristicImpedance, 1e-6);

        return (load - z0) / (load + z0);
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var a1 = system.Node(NearPlus);
        var b1 = system.Node(NearMinus);
        var a2 = system.Node(FarPlus);
        var b2 = system.Node(FarMinus);

        var near = system.Branch(this, 0);
        var far = system.Branch(this, 1);

        // A frequency sweep does not want the DC short: at one frequency a line has an exact
        // closed form, and StampAc writes it.
        if (state.Mode == AnalysisMode.SmallSignal) return;

        if (!state.IsTransient)
        {
            // At DC a line is a pair of wires, so each conductor is simply shorted end to end.
            // The branches are already there, which makes it an ideal short rather than a very
            // small resistance — except where both ends of a conductor are the same node, usually
            // because the return is grounded at each end, and the short would be a row saying
            // nothing at all.
            StampShort(system, near, a1, a2);
            StampShort(system, far, b1, b2);
            return;
        }

        var z0 = Math.Max(CharacteristicImpedance, 1e-6);
        var loss = Transmission;

        // What each end saw one delay ago is what the other end is about to hear. A positive
        // branch current flows into the line, so the wave leaving an end is v + Z0·i.
        var past = At(state.Time - Delay);

        var fromFar = loss * (past.V2 + (z0 * past.I2));
        var fromNear = loss * (past.V1 + (z0 * past.I1));

        system.StampTheveninSource(near, a1, b1, fromFar, z0);
        system.StampTheveninSource(far, a2, b2, fromNear, z0);
    }

    /// <summary>
    /// The line at one frequency, exactly — no history, no interpolation and no time step, because
    /// a delay in the frequency domain is a phase shift and nothing more.
    /// <para>
    /// The two Bergeron equations become <c>v₁ − Z₀·i₁ = (v₂ + Z₀·i₂)·e^(−jωT)</c> and its mirror,
    /// so the whole of what the line does to a signal — the delay, the reflections, the quarter-
    /// wave resonances that make an unterminated stub look like a short — falls out of two rows.
    /// </para>
    /// </summary>
    public override void StampAc(AcSystem system, SimulationState state)
    {
        var a1 = system.Node(NearPlus);
        var b1 = system.Node(NearMinus);
        var a2 = system.Node(FarPlus);
        var b2 = system.Node(FarMinus);

        var near = system.Branch(this, 0);
        var far = system.Branch(this, 1);

        var z0 = new Complex(Math.Max(CharacteristicImpedance, 1e-6), 0);

        // What a wave is multiplied by in crossing the line: the loss, and a phase shift of one
        // delay's worth.
        var travel = Transmission * Complex.Exp(new Complex(0, -state.AngularFrequency * Delay));

        // The branch currents flow into the line at each port, as they do in the transient form.
        system.Add(a1, near, 1.0);
        system.Add(b1, near, -1.0);
        system.Add(a2, far, 1.0);
        system.Add(b2, far, -1.0);

        // v1 - Z0·i1 - (v2 + Z0·i2)·travel = 0
        system.Add(near, a1, 1.0);
        system.Add(near, b1, -1.0);
        system.Add(near, near, -z0);
        system.Add(near, a2, -travel);
        system.Add(near, b2, travel);
        system.Add(near, far, -z0 * travel);

        // and the same the other way round.
        system.Add(far, a2, 1.0);
        system.Add(far, b2, -1.0);
        system.Add(far, far, -z0);
        system.Add(far, a1, -travel);
        system.Add(far, b1, travel);
        system.Add(far, near, -z0 * travel);
    }

    /// <summary>
    /// A conductor shorted end to end, or a branch pinned to no current where there is nothing to
    /// short because the two ends are already the same node.
    /// </summary>
    private static void StampShort(MnaSystem system, int branch, int from, int to)
    {
        if (from == to)
        {
            system.Add(branch, branch, 1.0);
            return;
        }

        system.StampTheveninSource(branch, from, to, 0.0, 0.0);
    }

    /// <summary>
    /// What the two ends were doing at a given moment, interpolated between the points actually
    /// solved. The delay almost never lands on a time point, and rounding to the nearest one turns
    /// a clean edge into a staircase with the wrong steps in it.
    /// </summary>
    private Sample At(double time)
    {
        if (_history.Count == 0) return new Sample(time, 0, 0, 0, 0);

        if (time <= _history[0].Time) return _history[0] with { Time = time };

        var last = _history[^1];
        if (time >= last.Time) return last with { Time = time };

        // Backwards from the end, because the point wanted is one delay ago and so lives near the
        // end of the list rather than the start of it. The pair wanted is the one that straddles
        // the time: walk down until the earlier of the two is no later than it.
        for (var i = _history.Count - 1; i > 0; i--)
        {
            var a = _history[i - 1];
            if (a.Time > time) continue;

            var b = _history[i];
            var span = b.Time - a.Time;
            var t = span <= 0 ? 0.0 : (time - a.Time) / span;

            return new Sample(
                time,
                a.V1 + ((b.V1 - a.V1) * t),
                a.I1 + ((b.I1 - a.I1) * t),
                a.V2 + ((b.V2 - a.V2) * t),
                a.I2 + ((b.I2 - a.I2) * t));
        }

        return _history[0] with { Time = time };
    }

    /// <summary>
    /// Time points of its own, spaced a fraction of the delay apart.
    /// <para>
    /// Everything else here can be stepped over: take too large a step across a capacitor and the
    /// answer is merely less accurate. A line is different, because the delay is the entire
    /// behaviour — step past it and the reflection does not arrive late, it does not arrive. So
    /// the component asks the solver to keep landing inside its own delay, which is the same thing
    /// a maximum time step would do if one could be asked for per component.
    /// </para>
    /// </summary>
    public double? NextBreakpointAfter(double time)
    {
        var delay = Delay;
        if (delay <= 0) return null;

        var spacing = delay / Math.Clamp(StepsPerDelay, 1, 64);
        var next = (Math.Floor(Math.Max(time, 0.0) / spacing) + 1) * spacing;

        return next > time ? next : next + spacing;
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        NearVoltage = system.NodeVoltage(NearPlus) - system.NodeVoltage(NearMinus);
        FarVoltage = system.NodeVoltage(FarPlus) - system.NodeVoltage(FarMinus);

        NearCurrent = system.BranchCurrent(this, 0);
        FarCurrent = system.BranchCurrent(this, 1);

        _history.Add(new Sample(state.Time, NearVoltage, NearCurrent, FarVoltage, FarCurrent));

        // Only the last delay's worth is ever asked for, so the rest is dropped — otherwise a long
        // run would keep every time point it ever solved.
        var keepFrom = state.Time - (Delay * 2.0) - 1e-12;

        var drop = 0;
        while (drop + 2 < _history.Count && _history[drop + 1].Time < keepFrom) drop++;

        if (drop > 0) _history.RemoveRange(0, drop);
    }

    public override void ResetState()
    {
        _history.Clear();

        NearVoltage = 0;
        FarVoltage = 0;
        NearCurrent = 0;
        FarCurrent = 0;
    }

    partial void OnCharacteristicImpedanceChanged(double value) => NotifyValueChanged();

    partial void OnLengthChanged(double value) => NotifyValueChanged();
}
