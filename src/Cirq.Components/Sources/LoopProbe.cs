using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Sources;

/// <summary>
/// A break point in a feedback loop, for measuring how much gain goes round it.
/// <para>
/// Put one in the feedback path — between a divider's tap and the input it feeds, or between an
/// error amplifier and whatever it drives — and <b>Simulate &gt; Stability</b> can measure the loop
/// gain, the phase margin and the gain margin there. Everywhere else it is a piece of wire: zero
/// volts across it, at DC and in a transient alike, so having one in a circuit changes no answer
/// the circuit would otherwise give.
/// </para>
/// <para>
/// It has to be a part rather than a menu command because <i>where</i> you break the loop is a
/// judgement the circuit cannot make for itself, and it changes the answer. Break it at a point
/// where the impedance looking forward is much higher than the impedance looking back — the input
/// of an op-amp is ideal, the output of one is not — or the injection loads the loop it is
/// measuring and the number comes out wrong.
/// </para>
/// </summary>
public partial class LoopProbe : TwoTerminalComponent, ILoopBreak
{
    public LoopProbe() : base("←", "→")
    {
    }

    /// <summary>The driven side: where the loop's output arrives from.</summary>
    public Terminal From => A;

    /// <summary>The driving side: what the loop's output goes on to feed.</summary>
    public Terminal To => B;

    /// <summary>
    /// What the stability analysis injects across the break, in volts. Zero at every other time,
    /// which is what makes this a short circuit rather than a component.
    /// <para>
    /// Not an editable parameter: it is set by the analysis for the length of a sweep and put back
    /// afterwards. A loop gain is a ratio, so the value never reaches the answer.
    /// </para>
    /// </summary>
    public double Injection { get; set; }

    public override string ComponentType => "Loop Probe";

    public override string DesignatorPrefix => "LP";

    public override string ValueLabel => "loop";

    /// <summary>A branch, so it is a genuine zero-volt source and its current can be read.</summary>
    public override int VoltageSourceCount => 1;

    /// <summary>Zero volts across it: the two ends are the same point as far as the circuit knows.</summary>
    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        system.StampTheveninSource(system.Branch(this), system.Node(A), system.Node(B), 0.0, 0.0);

    /// <summary>
    /// The break, which exists only in the small-signal world. The DC row is already written as a
    /// short, so all this adds is the injection — and with the injection at zero the loop is still
    /// closed, which is what lets an ordinary frequency response be run with one of these in place.
    /// </summary>
    public override void StampAc(AcSystem system, SimulationState state) =>
        system.AddRhs(system.Branch(this), Injection);
}
