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
    private readonly OpAmpStage _stage = new();

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
    public double GainNodeVoltage => _stage.GainNodeVoltage;

    /// <summary>True when the output has run into a supply rail.</summary>
    public bool IsSaturated => _stage.IsSaturated;

    /// <summary>True when the output is moving at the model's slew-rate limit.</summary>
    public bool IsSlewing => _stage.IsSlewing;

    public override void StampMatrix(MnaSystem system, SimulationState state) =>
        _stage.Stamp(
            system, state, Model,
            system.Node(NonInverting), system.Node(Inverting), system.Node(Output),
            system.Node(PositiveSupply), system.Node(NegativeSupply),
            system.InternalNode(this), system.Branch(this));

    /// <summary>
    /// The compensation capacitor, and with it the whole of the amplifier's frequency response.
    /// Working into R1 it sets the dominant pole, and the gain falls from there at twenty decibels
    /// a decade until it reaches one — which is the gain-bandwidth product, arriving out of the
    /// model rather than being asserted by it.
    /// </summary>
    public override void StampAc(AcSystem system, SimulationState state) =>
        system.StampCapacitance(system.InternalNode(this), -1, Model.CompensationCapacitance);

    public override void CommitTimeStep(MnaSystem system, SimulationState state) =>
        _stage.Commit(system, state, Model, system.InternalNode(this), PositiveSupply, NegativeSupply);

    public override void ResetState() => _stage.Reset();

    partial void OnModelChanged(OpAmpModel value) => NotifyValueChanged();
}

/// <summary>The LM741 preset, exposed as its own palette entry.</summary>
public sealed class OpAmp741 : OperationalAmplifier
{
    public OpAmp741() : base(OpAmpModel.Lm741) { }

    public override string ComponentType => "LM741";
}
