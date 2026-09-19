using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;

namespace Cirq.Engine.Digital;

/// <summary>
/// The event-driven half of the engine. Holds a timestamp-ordered queue of pending logic
/// transitions, hands devices their analog input levels, and tells the analog solver where the
/// next transition lands so it can place a time point exactly there.
/// </summary>
public sealed class DigitalScheduler : IDigitalContext
{
    private readonly PriorityQueue<DigitalEvent, (double Time, long Sequence)> _queue = new();
    private readonly Dictionary<(IDigitalDevice Device, int Pin), LogicState> _pendingTarget = new();
    private readonly HashSet<(IDigitalDevice Device, int Pin)> _cancelled = new();
    private readonly MnaSystem _system;
    private long _sequence;

    public DigitalScheduler(MnaSystem system)
    {
        _system = system;
    }

    /// <summary>Devices evaluated on every accepted time point and delta cycle.</summary>
    public List<IDigitalDevice> Devices { get; } = [];

    public double Time { get; internal set; }

    /// <summary>Set by the engine while it settles the power-on logic state.</summary>
    public bool IsInitializing { get; internal set; }

    public int PendingEventCount => _queue.Count;

    /// <summary>Timestamp of the earliest queued transition, or null when the queue is empty.</summary>
    public double? NextEventTime => _queue.TryPeek(out _, out var key) ? key.Time : null;

    public double NodeVoltage(Terminal terminal) => _system.NodeVoltage(terminal);

    public LogicState ReadInput(Terminal terminal, LogicLevels levels) =>
        levels.Classify(_system.NodeVoltage(terminal));

    public void Schedule(IDigitalDevice device, int outputIndex, LogicState newState, double delaySeconds)
    {
        var key = (device, outputIndex);

        // Already at, or already heading to, this state: nothing to queue. This is what makes it
        // safe for a device to re-assert its outputs on every single evaluation.
        var currentTarget = _pendingTarget.TryGetValue(key, out var pending)
            ? pending
            : device.GetOutputState(outputIndex);
        if (currentTarget == newState) return;

        _pendingTarget[key] = newState;
        _cancelled.Remove(key);

        var timestamp = Time + Math.Max(0, delaySeconds);
        var seq = _sequence++;
        _queue.Enqueue(
            new DigitalEvent(timestamp, device, outputIndex, OutputTerminal(device, outputIndex), newState, seq),
            (timestamp, seq));
    }

    public void CancelPending(IDigitalDevice device, int outputIndex)
    {
        var key = (device, outputIndex);
        if (!_pendingTarget.Remove(key)) return;
        // Lazy cancellation: the event stays queued but is discarded when it comes due.
        _cancelled.Add(key);
    }

    private static Terminal OutputTerminal(IDigitalDevice device, int outputIndex) =>
        outputIndex >= 0 && outputIndex < device.OutputTerminals.Count
            ? device.OutputTerminals[outputIndex]
            : device.OutputTerminals.Count > 0
                ? device.OutputTerminals[0]
                : throw new CircuitTopologyException("Digital device declared no output terminals.");

    /// <summary>
    /// Applies every transition whose timestamp has arrived. Returns true when at least one output
    /// actually changed, which tells the analog solver it must re-solve this time point.
    /// </summary>
    public bool ApplyDueEvents(double time, double tolerance = 1e-18)
    {
        var changed = false;
        while (_queue.TryPeek(out var evt, out _) && evt.Timestamp <= time + tolerance)
        {
            _queue.Dequeue();
            var key = (evt.Device, evt.OutputIndex);

            if (_cancelled.Contains(key))
            {
                _cancelled.Remove(key);
                continue;
            }

            // A later event for the same pin supersedes this one.
            if (_pendingTarget.TryGetValue(key, out var target) && target != evt.NewState) continue;

            _pendingTarget.Remove(key);
            if (evt.Device.GetOutputState(evt.OutputIndex) == evt.NewState) continue;

            evt.Device.ApplyTransition(evt.OutputIndex, evt.NewState);
            LastAppliedEvent = evt;
            EventApplied?.Invoke(evt);
            changed = true;
        }

        return changed;
    }

    /// <summary>Runs one evaluation pass over every registered device.</summary>
    public void EvaluateDevices()
    {
        foreach (var device in Devices) device.EvaluateLogic(this);
    }

    /// <summary>True when a queued transition is due at or before <paramref name="time"/>.</summary>
    public bool HasEventsDue(double time, double tolerance = 1e-18) =>
        _queue.TryPeek(out _, out var key) && key.Time <= time + tolerance;

    public DigitalEvent? LastAppliedEvent { get; private set; }

    public event Action<DigitalEvent>? EventApplied;

    public void Reset()
    {
        _queue.Clear();
        _pendingTarget.Clear();
        _cancelled.Clear();
        _sequence = 0;
        Time = 0;
        LastAppliedEvent = null;
        IsInitializing = false;
        foreach (var d in Devices) d.ResetLogic();
    }
}
