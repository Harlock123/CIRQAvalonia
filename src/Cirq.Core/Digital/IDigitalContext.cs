using Cirq.Core.Topology;

namespace Cirq.Core.Digital;

/// <summary>
/// The scheduler surface a digital device sees: read the analog side of its input pins, and queue
/// transitions on its output pins.
/// </summary>
public interface IDigitalContext
{
    /// <summary>Current simulation time in seconds.</summary>
    double Time { get; }

    /// <summary>
    /// True while the power-on state is being settled. Devices schedule with zero delay in this
    /// phase so the circuit starts from a consistent logic state rather than one propagation
    /// delay's worth of undefined outputs.
    /// </summary>
    bool IsInitializing { get; }

    /// <summary>Analog voltage presently solved at a terminal's node.</summary>
    double NodeVoltage(Terminal terminal);

    /// <summary>Classifies a terminal's analog voltage into a logic state using the given family.</summary>
    LogicState ReadInput(Terminal terminal, LogicLevels levels);

    /// <summary>
    /// Queues a transition of an output pin, taking effect after <paramref name="delaySeconds"/>.
    /// A delay of zero resolves within the current time point as a delta cycle. Scheduling the
    /// state a pin is already heading to is a no-op, so this is safe to call every evaluation.
    /// </summary>
    void Schedule(IDigitalDevice device, int outputIndex, LogicState newState, double delaySeconds);

    /// <summary>Cancels any queued transitions for an output pin (used for asynchronous clear/preset).</summary>
    void CancelPending(IDigitalDevice device, int outputIndex);
}
