using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>
/// Three-stage op-amp macromodel: a differential transconductance input stage, a dominant-pole
/// gain node, and a saturating output buffer.
/// <list type="number">
/// <item>The input stage converts the differential voltage into a current through a hyperbolic
/// tangent, so it runs out of tail current exactly where the datasheet slew rate says it should.</item>
/// <item>That current drives an internal node loaded by R1 and the compensation capacitor C1,
/// which together set the DC open-loop gain and the gain-bandwidth product.</item>
/// <item>The output buffer repeats that node behind the datasheet output resistance, and is
/// otherwise linear — the rails are what clamp the gain node, not the buffer, which is both where
/// a real amplifier does it and the only arrangement that leaves Newton something to steer with
/// when the output is hard against a rail.</item>
/// </list>
/// <para>
/// Known simplification: output current returns to ground rather than to the supply pins, so the
/// rails carry only the quiescent current. This is the usual macromodel trade-off and only shows
/// up when the supply rails themselves have series impedance.
/// </para>
/// </summary>
public partial class OperationalAmplifier : CircuitComponent
{
    private double _gainNodeVoltage;
    private double _capacitorCurrent;

    private double _companionConductance;
    private double _companionCurrent;

    public OperationalAmplifier(OpAmpModel? model = null)
    {
        Model = model ?? OpAmpModel.Lm741;

        NonInverting = new Terminal("in+", "IN+", TerminalType.Input, new Point(-40, 20));
        Inverting = new Terminal("in-", "IN-", TerminalType.Input, new Point(-40, -20));
        Output = new Terminal("out", "OUT", TerminalType.Output, new Point(40, 0));
        PositiveSupply = new Terminal("v+", "V+", TerminalType.Power, new Point(0, -30));
        NegativeSupply = new Terminal("v-", "V-", TerminalType.Power, new Point(0, 30));

        Terminals = [NonInverting, Inverting, Output, PositiveSupply, NegativeSupply];
    }

    public Terminal NonInverting { get; }
    public Terminal Inverting { get; }
    public Terminal Output { get; }
    public Terminal PositiveSupply { get; }
    public Terminal NegativeSupply { get; }

    [ObservableProperty]
    public partial OpAmpModel Model { get; set; }

    public override string ComponentType => "Op-Amp";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => Model.Name;

    public override bool IsNonlinear => true;

    /// <summary>One branch for the output buffer.</summary>
    public override int VoltageSourceCount => 1;

    /// <summary>One internal node for the compensated gain stage.</summary>
    public override int InternalNodeCount => 1;

    /// <summary>Voltage on the internal gain node at the last accepted point.</summary>
    public double GainNodeVoltage => _gainNodeVoltage;

    /// <summary>True when the output has run into a supply rail.</summary>
    public bool IsSaturated { get; private set; }

    /// <summary>True when the output is moving at the model's slew-rate limit.</summary>
    public bool IsSlewing { get; private set; }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var inP = system.Node(NonInverting);
        var inN = system.Node(Inverting);
        var outNode = system.Node(Output);
        var vPos = system.Node(PositiveSupply);
        var vNeg = system.Node(NegativeSupply);
        var gain = system.InternalNode(this);
        var branch = system.Branch(this);

        // The window the output buffer can swing in, worked out once: the gain node is held inside
        // it as well as the output, so both stages have to agree on where it is.
        var railHigh = system.IterationVoltage(vPos) - Model.OutputSwingHeadroom;
        var railLow = system.IterationVoltage(vNeg) + Model.NegativeSwingHeadroom;

        var mid = (railHigh + railLow) * 0.5;
        var half = Math.Max((railHigh - railLow) * 0.5, 1e-6);

        StampInputStage(system, inP, inN, gain);
        StampGainNode(system, state, gain);
        StampOutputStage(system, gain, outNode, branch);

        // Not on the first iteration of an operating point, where the supplies have not been
        // solved yet and the rails come out crossed: the window is not known yet, so there is
        // nothing to hold the node inside.
        if (railHigh > railLow) StampRailClamp(system, gain, mid, half);

        // Quiescent supply current flows from the positive rail to the negative rail.
        system.StampCurrentSource(vPos, vNeg, Model.QuiescentCurrent);
    }

    /// <summary>Differential input impedance, bias currents, and the slew-limited transconductance.</summary>
    private void StampInputStage(MnaSystem system, int inP, int inN, int gain)
    {
        system.StampConductance(inP, inN, 1.0 / Math.Max(Model.InputResistance, 1.0));

        if (Model.InputBiasCurrent != 0)
        {
            // Bias current is drawn into both inputs from ground.
            system.StampCurrentSource(inP, -1, Model.InputBiasCurrent);
            system.StampCurrentSource(inN, -1, Model.InputBiasCurrent);
        }

        var vd = system.IterationVoltageAcross(inP, inN) + Model.InputOffsetVoltage;

        var islew = Model.SlewCurrent;
        var gm = Model.Transconductance;
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
    private void StampGainNode(MnaSystem system, SimulationState state, int gain)
    {
        system.StampConductance(gain, -1, 1.0 / Model.GainResistance);

        if (!state.IsTransient)
        {
            _companionConductance = 0;
            _companionCurrent = 0;
            return;
        }

        var c = Model.CompensationCapacitance;
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
    private void StampOutputStage(MnaSystem system, int gain, int outNode, int branch)
    {
        // v_out - Rout·i - v_gain = 0
        system.Add(outNode, branch, 1.0);
        system.Add(branch, outNode, 1.0);
        system.Add(branch, branch, -Model.OutputResistance);
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
    private void StampRailClamp(MnaSystem system, int gain, double mid, double half)
    {
        // How sharp the corner is, as a fraction of the swing, and how hard the clamp pulls once
        // past it. Stiff enough that the whole tail current only pushes the node a millivolt or so
        // beyond the rail, soft enough that the exponential shoulder is a few steps wide.
        var softness = 0.002 * half;
        var strength = Model.SlewCurrent / softness;

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

    /// <summary>
    /// The compensation capacitor, and with it the whole of the amplifier's frequency response.
    /// Working into R1 it sets the dominant pole, and the gain falls from there at twenty decibels
    /// a decade until it reaches one — which is the gain-bandwidth product, arriving out of the
    /// model rather than being asserted by it.
    /// </summary>
    public override void StampAc(AcSystem system, SimulationState state) =>
        system.StampCapacitance(system.InternalNode(this), -1, Model.CompensationCapacitance);

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var gain = system.InternalNode(this);
        var v = system.NodeVoltage(gain);

        // Read off the accepted answer rather than off whichever iterate the last stamp happened
        // to see.
        var railHigh = system.NodeVoltage(PositiveSupply) - Model.OutputSwingHeadroom;
        var railLow = system.NodeVoltage(NegativeSupply) + Model.NegativeSwingHeadroom;

        IsSaturated = railHigh > railLow && (v > railHigh - (0.001 * (railHigh - railLow))
                                             || v < railLow + (0.001 * (railHigh - railLow)));

        if (state.IsTransient)
            _capacitorCurrent = _companionConductance * v + _companionCurrent;
        else
            _capacitorCurrent = 0;

        _gainNodeVoltage = v;
    }

    public override void ResetState()
    {
        _gainNodeVoltage = 0;
        _capacitorCurrent = 0;
        _companionConductance = 0;
        _companionCurrent = 0;
        IsSaturated = false;
        IsSlewing = false;
    }

    partial void OnModelChanged(OpAmpModel value) => NotifyValueChanged();
}

/// <summary>The LM741 preset, exposed as its own palette entry.</summary>
public sealed class OpAmp741 : OperationalAmplifier
{
    public OpAmp741() : base(OpAmpModel.Lm741) { }

    public override string ComponentType => "LM741";
}
