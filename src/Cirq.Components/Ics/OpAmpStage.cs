using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Components.Ics;

/// <summary>
/// One operational amplifier, as a piece of stamping rather than as a component.
/// <para>
/// A single op-amp and a quad package contain the same amplifier, and this is it. Kept in one
/// place because the model is delicate in ways that are not obvious from reading it — the input
/// stage is linearised on a chord rather than a tangent, the rails are clamped at the gain node
/// rather than at the output, and the buffer is deliberately linear. Every one of those is there
/// to stop Newton oscillating, and a second copy of this would be a second place for somebody to
/// quietly undo one of them.
/// </para>
/// <para>
/// It owns the per-amplifier state, so a package simply holds four of them and hands each one its
/// own internal node and branch.
/// </para>
/// </summary>
internal sealed class OpAmpStage
{
    private double _gainNodeVoltage;
    private double _capacitorCurrent;

    private double _companionConductance;
    private double _companionCurrent;

    /// <summary>Voltage on the internal gain node at the last accepted point.</summary>
    public double GainNodeVoltage => _gainNodeVoltage;

    /// <summary>True when the output has run into a supply rail.</summary>
    public bool IsSaturated { get; private set; }

    /// <summary>True when the output is moving at the model's slew-rate limit.</summary>
    public bool IsSlewing { get; private set; }

    /// <summary>
    /// Stamps one amplifier. <paramref name="gain"/> and <paramref name="branch"/> are this
    /// amplifier's own internal node and branch, which is what lets several share a package.
    /// </summary>
    public void Stamp(
        MnaSystem system, SimulationState state, OpAmpModel model,
        int inP, int inN, int outNode, int vPos, int vNeg, int gain, int branch)
    {
        // The window the output buffer can swing in, worked out once: the gain node is held inside
        // it as well as the output, so both stages have to agree on where it is.
        var railHigh = system.IterationVoltage(vPos) - model.OutputSwingHeadroom;
        var railLow = system.IterationVoltage(vNeg) + model.NegativeSwingHeadroom;

        var mid = (railHigh + railLow) * 0.5;
        var half = Math.Max((railHigh - railLow) * 0.5, 1e-6);

        StampInputStage(system, model, inP, inN, gain);
        StampGainNode(system, state, model, gain);
        StampOutputStage(system, model, gain, outNode, branch);

        // Not on the first iteration of an operating point, where the supplies have not been
        // solved yet and the rails come out crossed: the window is not known yet, so there is
        // nothing to hold the node inside.
        if (railHigh > railLow) StampRailClamp(system, model, gain, mid, half);

        // Quiescent supply current flows from the positive rail to the negative rail.
        system.StampCurrentSource(vPos, vNeg, model.QuiescentCurrent);
    }

    /// <summary>Differential input impedance, bias currents, and the slew-limited transconductance.</summary>
    private void StampInputStage(MnaSystem system, OpAmpModel model, int inP, int inN, int gain)
    {
        system.StampConductance(inP, inN, 1.0 / Math.Max(model.InputResistance, 1.0));

        if (model.InputBiasCurrent != 0)
        {
            // Bias current is drawn into both inputs from ground.
            system.StampCurrentSource(inP, -1, model.InputBiasCurrent);
            system.StampCurrentSource(inN, -1, model.InputBiasCurrent);
        }

        var vd = system.IterationVoltageAcross(inP, inN) + model.InputOffsetVoltage;

        var islew = model.SlewCurrent;
        var gm = model.Transconductance;
        var u = gm * vd / islew;

        // tanh saturates the stage at the tail current, which is what produces slew limiting.
        var tanh = Math.Tanh(Math.Clamp(u, -40, 40));
        var current = islew * tanh;

        IsSlewing = Math.Abs(tanh) > 0.99;

        // Linearised on the chord through zero rather than on the tangent. The tangent of a
        // saturated tanh is nothing at all, and a gain node stamped with nothing has no idea which
        // way the input stage is pushing it: the row is left holding R1 alone, a gigaohm, so the
        // solve answers with tens of kilovolts and the node slams from one rail clamp to the other
        // for ever. The chord is gm·tanh(u)/u, which falls off as 1/u instead of exponentially, so
        // deep in saturation there is still a few microsiemens of steering left.
        //
        // It costs nothing in accuracy. The chord passes through the same point on the curve as the
        // tangent does, so the converged answer is identical — only the route there changes, and it
        // changes from a limit cycle into a walk.
        var conductance = Math.Abs(vd) > 1e-12 ? current / vd : gm;

        // i = gm_eff·vd + i0 injected into the gain node.
        system.StampVccs(-1, gain, inP, inN, conductance);
        system.AddRhs(gain, current - conductance * vd);

    }

    /// <summary>R1 and the compensation capacitor that set the open-loop gain and dominant pole.</summary>
    private void StampGainNode(MnaSystem system, SimulationState state, OpAmpModel model, int gain)
    {
        system.StampConductance(gain, -1, 1.0 / model.GainResistance);

        if (!state.IsTransient)
        {
            _companionConductance = 0;
            _companionCurrent = 0;
            return;
        }

        var c = model.CompensationCapacitance;
        var h = state.TimeStep;

        if (state.EffectiveIntegration == IntegrationMethod.Trapezoidal)
        {
            _companionConductance = 2.0 * c / h;
            _companionCurrent = -(_companionConductance * _gainNodeVoltage + _capacitorCurrent);
        }
        else
        {
            _companionConductance = c / h;
            _companionCurrent = -_companionConductance * _gainNodeVoltage;
        }

        system.StampNorton(gain, -1, _companionConductance, _companionCurrent);
    }

    /// <summary>
    /// The output buffer: a unity-gain follower behind the datasheet output resistance.
    /// <para>
    /// Linear, deliberately. The rail clamping happens one stage earlier, on the gain node, which
    /// is where a real amplifier does it too — the second stage cannot drive its own output past
    /// its own supplies, and the buffer that follows only repeats whatever it is given. Modelling
    /// it the other way round, with the clamp in the buffer, puts a saturating function in the
    /// branch equation, and a saturated function has no slope to stamp: the row stops depending
    /// on the gain node, the output sits at a rail while the node wanders, and Newton has nothing
    /// left to steer with. Here the row is exact at every iterate, which also keeps whatever
    /// Newton has not yet converged away from the output pin.
    /// </para>
    /// </summary>
    private void StampOutputStage(MnaSystem system, OpAmpModel model, int gain, int outNode, int branch)
    {
        // v_out - Rout·i - v_gain = 0
        system.Add(outNode, branch, 1.0);
        system.Add(branch, outNode, 1.0);
        system.Add(branch, branch, -model.OutputResistance);
        system.Add(branch, gain, -1.0);
    }

    /// <summary>
    /// Holds the gain node inside the window the output can swing in.
    /// <para>
    /// This is what stops the output at the rails, and it is also the only thing that bounds the
    /// node at all. Without it a slew-saturated input stage delivers a constant tail current into
    /// R1, and with R1 at a gigaohm that is an equilibrium several thousand volts up — for the
    /// LM741, 8 µA into 2 GΩ, so 16 kV. Nothing inside a real amplifier goes there, and two things
    /// break when the model lets it: the operating point has to be found by walking a node across
    /// kilovolts, and in a transient the node then has to slew back from kilovolts at the datasheet
    /// slew rate, which turns a microsecond of overload recovery into seconds.
    /// </para>
    /// <para>
    /// It is a smooth conductance rather than a hard limit, because a corner is somewhere Newton
    /// can sit and oscillate; and it is asymptotically linear past the limit rather than
    /// exponential, so a solve that lands far outside comes back in one step instead of creeping.
    /// Inside the window it is inert — at the rail itself it passes less than a tail current, and
    /// a hundred millivolts in, nothing measurable — so the linear region, the open-loop gain and
    /// the slew rate are all exactly what they were.
    /// </para>
    /// </summary>
    private void StampRailClamp(MnaSystem system, OpAmpModel model, int gain, double mid, double half)
    {
        // How sharp the corner is, as a fraction of the swing, and how hard the clamp pulls once
        // past it. Stiff enough that the whole tail current only pushes the node a millivolt or so
        // beyond the rail, soft enough that the exponential shoulder is a few steps wide.
        var softness = 0.002 * half;
        var strength = model.SlewCurrent / softness;

        var v = system.IterationVoltage(gain);
        var offset = v - mid;

        var above = (offset - half) / softness;
        var below = (-offset - half) / softness;

        var current = strength * softness * (Softplus(above) - Softplus(below));
        var conductance = strength * (Sigmoid(above) + Sigmoid(below));

        system.StampNorton(gain, -1, conductance, current - (conductance * v));
    }

    /// <summary>log(1 + e^u), taken to its asymptotes rather than overflowing at either end.</summary>
    private static double Softplus(double u) =>
        u > 30 ? u : u < -30 ? 0.0 : Math.Log(1.0 + Math.Exp(u));

    /// <summary>The derivative of <see cref="Softplus"/>, which is never quite zero.</summary>
    private static double Sigmoid(double u) =>
        u > 30 ? 1.0 : u < -30 ? 0.0 : 1.0 / (1.0 + Math.Exp(-u));

    /// <summary>Reads the accepted answer back into this amplifier's own state.</summary>
    public void Commit(
        MnaSystem system, SimulationState state, OpAmpModel model,
        int gain, Terminal positiveSupply, Terminal negativeSupply)
    {
        var v = system.NodeVoltage(gain);

        // Read off the accepted answer rather than off whichever iterate the last stamp happened
        // to see.
        var railHigh = system.NodeVoltage(positiveSupply) - model.OutputSwingHeadroom;
        var railLow = system.NodeVoltage(negativeSupply) + model.NegativeSwingHeadroom;

        IsSaturated = railHigh > railLow && (v > railHigh - (0.001 * (railHigh - railLow))
                                             || v < railLow + (0.001 * (railHigh - railLow)));

        _capacitorCurrent = state.IsTransient ? (_companionConductance * v) + _companionCurrent : 0;
        _gainNodeVoltage = v;
    }

    public void Reset()
    {
        _gainNodeVoltage = 0;
        _capacitorCurrent = 0;
        _companionConductance = 0;
        _companionCurrent = 0;
        IsSaturated = false;
        IsSlewing = false;
    }
}
