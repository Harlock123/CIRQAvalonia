using Cirq.Core.Digital;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// Base for a package whose inputs have hysteresis rather than a single threshold.
/// <para>
/// An ordinary gate has one threshold, so an input creeping slowly through it produces a burst of
/// output chatter as noise carries the level back and forth. A Schmitt input has two: it will not
/// call a rising input high until it clears the upper threshold, nor a falling one low until it
/// drops past the lower, and between them it keeps whatever it last decided. That memory is the
/// whole device — it is why a noisy edge crosses once instead of many times, and why the output of
/// one of these can be fed back to its own input to make an oscillator.
/// </para>
/// <para>
/// The thresholds are held as fractions of the supply rather than as volts, because that is how
/// the real parts behave: run one at 3.3 V and they move down with the rail.
/// </para>
/// </summary>
public abstract partial class SchmittGateIc : MultiGateIc
{
    /// <summary>Widest gate that still gets a stack buffer rather than a heap array.</summary>
    private const int StackLimit = 8;

    /// <summary>What each input was last called, one slot per input across the whole package.</summary>
    private readonly bool[] _above;

    private readonly int _widestGate;

    protected SchmittGateIc(
        int pinCount,
        GateFunction function,
        int vccPin,
        int gndPin,
        IReadOnlyList<GateDefinition> gates)
        : base(pinCount, function, vccPin, gndPin, gates)
    {
        var inputs = 0;

        for (var gate = 0; gate < GateCount; gate++)
        {
            inputs += GateInputs(gate).Count;
            _widestGate = Math.Max(_widestGate, GateInputs(gate).Count);
        }

        _above = new bool[inputs];
        Levels = LogicLevels.Cmos5V;
    }

    /// <summary>Upper threshold as a fraction of the supply — about 2.9 V on a 5 V rail.</summary>
    [ObservableProperty]
    public partial double UpperThresholdFraction { get; set; } = 0.58;

    /// <summary>Lower threshold as a fraction of the supply — about 1.9 V on a 5 V rail.</summary>
    [ObservableProperty]
    public partial double LowerThresholdFraction { get; set; } = 0.38;

    /// <summary>Hysteresis band in volts at the given supply — the gap between the two thresholds.</summary>
    public double HysteresisAt(double supplyVoltage) =>
        (UpperThresholdFraction - LowerThresholdFraction) * supplyVoltage;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        var delay = DelayFor(context);
        var supply = SupplyVoltage(context);
        var reference = context.NodeVoltage(Gnd);

        var upper = UpperThresholdFraction * supply;
        var lower = LowerThresholdFraction * supply;

        // One buffer for the whole call, re-sliced per gate: a stackalloc inside the loop is not
        // released until the method returns, so the frame would grow with every gate (CA2014).
        Span<LogicState> buffer = _widestGate <= StackLimit
            ? stackalloc LogicState[StackLimit]
            : new LogicState[_widestGate];

        var slot = 0;

        for (var gate = 0; gate < GateCount; gate++)
        {
            var inputs = GateInputs(gate);
            var states = buffer[..inputs.Count];

            for (var i = 0; i < inputs.Count; i++, slot++)
            {
                // Read the node directly rather than through the logic thresholds: the whole point
                // of the part is that it decides high and low differently from an ordinary input.
                var volts = context.NodeVoltage(inputs[i]) - reference;

                if (volts >= upper) _above[slot] = true;
                else if (volts <= lower) _above[slot] = false;

                states[i] = _above[slot] ? LogicState.High : LogicState.Low;
            }

            context.Schedule(this, gate, LogicGate.Evaluate(Function, states), delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_above);
    }
}
