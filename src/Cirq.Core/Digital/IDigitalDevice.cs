using Cirq.Core.Topology;

namespace Cirq.Core.Digital;

/// <summary>
/// A component with event-driven logic behaviour. The analog solver stamps its outputs as
/// Thevenin sources; the digital scheduler samples its inputs and queues output transitions.
/// </summary>
public interface IDigitalDevice
{
    /// <summary>The logic family thresholds this device reads and drives with.</summary>
    LogicLevels Levels { get; }

    /// <summary>Output pins, in the order used by <see cref="IDigitalContext.Schedule"/>.</summary>
    IReadOnlyList<Terminal> OutputTerminals { get; }

    /// <summary>
    /// Samples inputs and schedules any required output transitions. Called once per accepted
    /// analog time point and again for each zero-delay delta cycle until the logic settles.
    /// </summary>
    void EvaluateLogic(IDigitalContext context);

    /// <summary>Applies a transition that has reached its scheduled timestamp.</summary>
    void ApplyTransition(int outputIndex, LogicState newState);

    /// <summary>Current driven state of an output pin.</summary>
    LogicState GetOutputState(int outputIndex);

    /// <summary>Returns the device to its power-on state.</summary>
    void ResetLogic();
}
