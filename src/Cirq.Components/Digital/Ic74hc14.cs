using Cirq.Core.Topology;
using Cirq.Core.Digital;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// A 74HC14 hex Schmitt-trigger inverter.
/// <para>
/// The package and pinout are the 7404's; the difference is entirely in how the inputs are read.
/// An ordinary gate has one threshold, so an input creeping slowly through it produces a burst of
/// output chatter as noise carries the level back and forth. A Schmitt input has two: it will not
/// call a rising input high until it clears the upper threshold, nor a falling one low until it
/// drops past the lower. The gap between them is what makes this the part you reach for to clean
/// up a slow edge, debounce a contact, or — with a resistor from output back to input and a
/// capacitor to ground — build an oscillator out of a single gate.
/// </para>
/// <para>
/// The thresholds are held as fractions of the supply rather than as volts, because that is how
/// the real part behaves: run it at 3.3 V and they move down with the rail.
/// </para>
/// </summary>
public sealed partial class Ic74hc14 : MultiGateIc
{
    private static readonly GateDefinition[] Pinout =
    [
        new([1], 2),
        new([3], 4),
        new([5], 6),
        new([13], 12),
        new([11], 10),
        new([9], 8),
    ];

    private readonly bool[] _above;

    public Ic74hc14()
        : base(14, GateFunction.Not, vccPin: 14, gndPin: 7, Pinout)
    {
        _above = new bool[GateCount];
        Levels = LogicLevels.Cmos5V;
        PropagationDelay = 15e-9;
    }

    public override string PartNumber => "74HC14";

    /// <summary>Upper threshold as a fraction of the supply — about 2.9 V on a 5 V rail.</summary>
    [ObservableProperty]
    public partial double UpperThresholdFraction { get; set; } = 0.58;

    /// <summary>Lower threshold as a fraction of the supply — about 1.9 V on a 5 V rail.</summary>
    [ObservableProperty]
    public partial double LowerThresholdFraction { get; set; } = 0.38;

    /// <summary>The input and output pins of one of the six inverters, indexed from 0.</summary>
    public (Terminal A, Terminal Y) Inverter(int index) => (GateInputs(index)[0], GateOutput(index));

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

        for (var gate = 0; gate < GateCount; gate++)
        {
            var volts = context.NodeVoltage(GateInputs(gate)[0]) - reference;

            // Two thresholds, and between them the input keeps whatever it was last called. That
            // memory is the whole device: it is why a noisy edge crosses once instead of many
            // times, and why the output of one of these can be fed back to its own input.
            if (volts >= upper) _above[gate] = true;
            else if (volts <= lower) _above[gate] = false;

            context.Schedule(this, gate, _above[gate] ? LogicState.Low : LogicState.High, delay);
        }
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        Array.Clear(_above);
    }
}
