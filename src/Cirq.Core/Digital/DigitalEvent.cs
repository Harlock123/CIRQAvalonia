using Cirq.Core.Topology;

namespace Cirq.Core.Digital;

/// <summary>
/// A pending logic transition: at <see cref="Timestamp"/>, <see cref="TargetNode"/> takes
/// <see cref="NewState"/>. <see cref="Sequence"/> keeps events at an identical timestamp in the
/// order they were scheduled, which makes the queue deterministic.
/// </summary>
public readonly record struct DigitalEvent(
    double Timestamp,
    IDigitalDevice Device,
    int OutputIndex,
    Terminal TargetNode,
    LogicState NewState,
    long Sequence)
{
    public override string ToString() =>
        $"t={Timestamp * 1e9:0.###}ns {TargetNode} <= {NewState.ToChar()}";
}
