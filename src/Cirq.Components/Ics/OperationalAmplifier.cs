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
/// <item>The output buffer follows the internal node through a second saturating function clamped
/// to the supply rails, behind the datasheet output resistance.</item>
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

        StampInputStage(system, inP, inN, gain);
        StampGainNode(system, state, gain);
        StampOutputStage(system, state, gain, outNode, vPos, vNeg, branch);

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
        var conductance = gm * (1.0 - tanh * tanh);

        IsSlewing = Math.Abs(tanh) > 0.99;

        // Keep a small floor on the linearised conductance: a fully saturated tanh has zero
        // derivative, which would disconnect the input stage from the matrix entirely.
        conductance = Math.Max(conductance, gm * 1e-6);

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

    /// <summary>Rail-clamped output buffer behind the datasheet output resistance.</summary>
    private void StampOutputStage(
        MnaSystem system, SimulationState state, int gain, int outNode, int vPos, int vNeg, int branch)
    {
        var railHigh = system.IterationVoltage(vPos) - Model.OutputSwingHeadroom;
        var railLow = system.IterationVoltage(vNeg) + Model.OutputSwingHeadroom;

        var mid = (railHigh + railLow) * 0.5;
        var half = Math.Max((railHigh - railLow) * 0.5, 1e-6);

        var x = system.IterationVoltage(gain);
        var t = Math.Tanh(Math.Clamp((x - mid) / half, -40, 40));

        var value = mid + half * t;
        var slope = Math.Max(1.0 - t * t, 1e-9);

        IsSaturated = Math.Abs(t) > 0.99;

        // v_out - Rout·i - slope·v_gain = value - slope·x
        system.Add(outNode, branch, 1.0);
        system.Add(branch, outNode, 1.0);
        system.Add(branch, branch, -Model.OutputResistance);
        system.Add(branch, gain, -slope);
        system.AddRhs(branch, value - slope * x);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var gain = system.InternalNode(this);
        var v = system.NodeVoltage(gain);

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
