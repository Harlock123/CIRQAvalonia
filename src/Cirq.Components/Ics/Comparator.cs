using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>How a comparator drives its output pin.</summary>
public enum ComparatorOutput
{
    /// <summary>Pulls low or releases. Needs an external pull-up, and can drive a higher rail.</summary>
    OpenCollector,
    /// <summary>Drives both directions between the supply pins.</summary>
    PushPull,
}

/// <summary>Datasheet parameters for a voltage comparator.</summary>
public sealed record ComparatorModel(
    string Name,
    double ResponseTime,
    ComparatorOutput OutputStage,
    double InputOffsetVoltage,
    double InputResistance,
    double OutputSaturationVoltage,
    double OutputResistance,
    double QuiescentCurrent)
{
    /// <summary>Fast single comparator with an open-collector output.</summary>
    public static readonly ComparatorModel Lm311 =
        new("LM311", 200e-9, ComparatorOutput.OpenCollector, 2e-3, 1e7, 0.2, 20, 5e-3);

    /// <summary>The quad LM339's per-channel behaviour: slower, single-supply friendly.</summary>
    public static readonly ComparatorModel Lm339 =
        new("LM339", 1.3e-6, ComparatorOutput.OpenCollector, 2e-3, 1e8, 0.25, 30, 1e-3);

    /// <summary>Dual comparator, same family as the LM339.</summary>
    public static readonly ComparatorModel Lm393 =
        new("LM393", 1.3e-6, ComparatorOutput.OpenCollector, 2e-3, 1e8, 0.25, 30, 1e-3);

    /// <summary>Modern rail-to-rail push-pull part, for when an external pull-up is a nuisance.</summary>
    public static readonly ComparatorModel Tlv3501 =
        new("TLV3501", 4.5e-9, ComparatorOutput.PushPull, 1e-3, 1e10, 0.05, 15, 3.2e-3);

    public static readonly IReadOnlyList<ComparatorModel> Library = [Lm311, Lm339, Lm393, Tlv3501];

    public override string ToString() => Name;
}

/// <summary>
/// Voltage comparator. Unlike the op-amp macromodel this is event driven rather than continuous:
/// a comparator spends almost all its time slammed against one rail, so modelling it as a very
/// high gain analog stage would make the solver stiff for no benefit.
/// <para>
/// Each accepted time point compares the two inputs and schedules an output transition one
/// response time later. The open-collector output pulls down to the negative supply pin or
/// releases entirely, which is what lets it drive a rail higher than its own supply through an
/// external pull-up.
/// </para>
/// </summary>
public partial class Comparator : DigitalComponent
{
    public Comparator(ComparatorModel? model = null)
    {
        Model = model ?? ComparatorModel.Lm311;
        PropagationDelay = Model.ResponseTime;

        NonInverting = new Terminal("in+", "IN+", TerminalType.Input, new Point(-40, 20));
        Inverting = new Terminal("in-", "IN-", TerminalType.Input, new Point(-40, -20));
        Output = new Terminal("out", "OUT", TerminalType.Output, new Point(40, 0));
        PositiveSupply = new Terminal("v+", "V+", TerminalType.Power, new Point(0, -30));
        NegativeSupply = new Terminal("v-", "V-", TerminalType.Power, new Point(0, 30));

        Terminals = [NonInverting, Inverting, Output, PositiveSupply, NegativeSupply];
        ConfigurePins([NonInverting, Inverting], [Output]);
    }

    public Terminal NonInverting { get; }
    public Terminal Inverting { get; }
    public Terminal Output { get; }
    public Terminal PositiveSupply { get; }
    public Terminal NegativeSupply { get; }

    [ObservableProperty]
    public partial ComparatorModel Model { get; set; }

    /// <summary>
    /// Built-in hysteresis in volts, applied symmetrically. A real comparator has almost none and
    /// relies on external positive feedback; a small default keeps a slowly crossing input from
    /// chattering on the exact threshold.
    /// </summary>
    [ObservableProperty]
    public partial double HysteresisVoltage { get; set; } = 1e-3;

    /// <summary>Supply below which the part stops driving at all.</summary>
    [ObservableProperty]
    public partial double MinimumSupplyVoltage { get; set; } = 2.0;

    public override string ComponentType => "Comparator";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => Model.Name;

    /// <summary>One branch, used either as the pull-down or as the push-pull driver.</summary>
    public override int VoltageSourceCount => 1;

    /// <summary>True when the non-inverting input is the higher of the two.</summary>
    public bool IsOutputHigh => GetOutputState(0) is LogicState.High or LogicState.HighImpedance;

    /// <summary>Differential input voltage at the last solved point.</summary>
    public double DifferentialInput { get; private set; }

    protected override int ReferenceNode(MnaSystem system) => system.Node(NegativeSupply);

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var inP = system.Node(NonInverting);
        var inN = system.Node(Inverting);
        var outNode = system.Node(Output);
        var vPos = system.Node(PositiveSupply);
        var vNeg = system.Node(NegativeSupply);
        var branch = system.Branch(this, 0);

        // Inputs are close to ideal: a leakage path to each supply rail and nothing else.
        var inputConductance = 1.0 / Math.Max(Model.InputResistance, 1.0);
        system.StampConductance(inP, vNeg, inputConductance);
        system.StampConductance(inN, vNeg, inputConductance);

        system.StampCurrentSource(vPos, vNeg, Model.QuiescentCurrent);

        var driven = GetOutputState(0);

        if (Model.OutputStage == ComparatorOutput.OpenCollector)
        {
            if (driven == LogicState.Low)
            {
                // Output transistor on: pull down to the negative supply pin.
                system.StampTheveninSource(branch, outNode, vNeg,
                    Model.OutputSaturationVoltage, Model.OutputResistance);
            }
            else
            {
                // Released. Constraining the branch to zero current leaves the pin genuinely
                // floating while keeping the matrix row occupied.
                system.Add(branch, branch, 1.0);
            }

            return;
        }

        // Push-pull: swing between the rails, less the saturation drop at each end.
        var supplyHigh = system.IterationVoltage(vPos) - system.IterationVoltage(vNeg);
        var target = driven switch
        {
            LogicState.High => Math.Max(supplyHigh - Model.OutputSaturationVoltage, 0),
            LogicState.Low => Model.OutputSaturationVoltage,
            _ => supplyHigh * 0.5,
        };

        if (driven == LogicState.HighImpedance)
        {
            system.Add(branch, branch, 1.0);
            return;
        }

        system.StampTheveninSource(branch, outNode, vNeg, target, Model.OutputResistance);
    }

    public override void EvaluateLogic(IDigitalContext context)
    {
        var supply = context.NodeVoltage(PositiveSupply) - context.NodeVoltage(NegativeSupply);
        if (supply < MinimumSupplyVoltage)
        {
            DifferentialInput = 0;
            context.Schedule(this, 0, LogicState.HighImpedance, DelayFor(context));
            return;
        }

        DifferentialInput =
            context.NodeVoltage(NonInverting) - context.NodeVoltage(Inverting) + Model.InputOffsetVoltage;

        // Hysteresis is applied against the state the output is currently in, so the threshold
        // moves away from wherever the input just came from.
        var currentlyHigh = GetOutputState(0) is LogicState.High or LogicState.HighImpedance;
        var threshold = currentlyHigh ? -Math.Abs(HysteresisVoltage) : Math.Abs(HysteresisVoltage);

        var high = DifferentialInput > threshold;

        // An open-collector output releases rather than driving high.
        var state = high
            ? Model.OutputStage == ComparatorOutput.OpenCollector ? LogicState.HighImpedance : LogicState.High
            : LogicState.Low;

        context.Schedule(this, 0, state, DelayFor(context));
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        DifferentialInput = 0;
    }

    partial void OnModelChanged(ComparatorModel value)
    {
        PropagationDelay = value.ResponseTime;
        NotifyValueChanged();
    }
}
