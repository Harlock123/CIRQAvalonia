using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for packaged logic ICs. Unlike a discrete gate symbol these have real supply pins: input
/// thresholds and output drive are referenced to the GND pin, and an unpowered package releases
/// every output, just as the silicon would.
/// </summary>
public abstract partial class DigitalIc : DigitalComponent
{
    protected DigitalIc(int pinCount)
    {
        PinCount = pinCount;
    }

    public int PinCount { get; }

    public Terminal Vcc { get; protected set; } = null!;

    public Terminal Gnd { get; protected set; } = null!;

    /// <summary>Supply voltage below which the package stops driving its outputs.</summary>
    [ObservableProperty]
    public partial double MinimumSupplyVoltage { get; set; } = 2.0;

    /// <summary>Part number shown on the symbol, e.g. "74LS00".</summary>
    public abstract string PartNumber { get; }

    public override string ComponentType => PartNumber;

    public override string ValueLabel => PartNumber;

    /// <summary>
    /// Whether the symbol prints each pin's name inside the package. Off for the small logic parts,
    /// whose pins everyone wires from the datasheet; on for memories and processors, where forty
    /// unlabelled legs would make the drawing unreadable.
    /// </summary>
    public virtual bool LabelsPins => false;

    /// <summary>How a pin is labelled on the symbol: its name, with a slash in front of an active-low one.</summary>
    public virtual string PinLabel(Terminal pin) => pin.Name;

    protected override int ReferenceNode(MnaSystem system) => system.Node(Gnd);

    /// <summary>Supply voltage across the package's own power pins.</summary>
    protected double SupplyVoltage(IDigitalContext context) =>
        context.NodeVoltage(Vcc) - context.NodeVoltage(Gnd);

    protected bool IsPowered(IDigitalContext context) => SupplyVoltage(context) >= MinimumSupplyVoltage;

    /// <summary>
    /// Releases every output. Called when the supply pin is missing or below the minimum, so an
    /// unwired Vcc shows up as floating outputs rather than as silently working logic.
    /// </summary>
    protected void ReleaseAllOutputs(IDigitalContext context)
    {
        for (var i = 0; i < OutputTerminals.Count; i++)
            context.Schedule(this, i, LogicState.HighImpedance, DelayFor(context));
    }

    public sealed override void EvaluateLogic(IDigitalContext context)
    {
        if (!IsPowered(context))
        {
            ReleaseAllOutputs(context);
            return;
        }

        EvaluatePoweredLogic(context);
    }

    /// <summary>Device logic, called only while the package is powered.</summary>
    protected abstract void EvaluatePoweredLogic(IDigitalContext context);
}
