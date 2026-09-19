using Cirq.Components.Digital;
using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Bridges;

/// <summary>
/// Analog-to-digital boundary: watches an analog node and drives a clean logic level from it.
/// Defaults to TTL thresholds on a 0-5 V rail. Schmitt hysteresis keeps a slowly moving analog
/// input from chattering across the threshold.
/// </summary>
public partial class AdcBridge : DigitalComponent
{
    public AdcBridge()
    {
        PropagationDelay = 5e-9;
        AnalogIn = new Terminal("ain", "A-IN", TerminalType.Input, new Point(-40, 0));
        DigitalOut = new Terminal("dout", "D-OUT", TerminalType.Output, new Point(40, 0));
        Terminals = [AnalogIn, DigitalOut];
        ConfigurePins([AnalogIn], [DigitalOut]);
    }

    public Terminal AnalogIn { get; }

    public Terminal DigitalOut { get; }

    /// <summary>Rising threshold in volts.</summary>
    [ObservableProperty]
    public partial double ThresholdHigh { get; set; } = 2.0;

    /// <summary>Falling threshold in volts. Below <see cref="ThresholdHigh"/> to give hysteresis.</summary>
    [ObservableProperty]
    public partial double ThresholdLow { get; set; } = 0.8;

    /// <summary>When false the bridge is a plain comparator with a single threshold.</summary>
    [ObservableProperty]
    public partial bool UseHysteresis { get; set; } = true;

    public override string ComponentType => "ADC Bridge";

    public override string DesignatorPrefix => "AD";

    public override string ValueLabel => UseHysteresis
        ? $"{ThresholdLow:0.##}/{ThresholdHigh:0.##} V"
        : $"{ThresholdHigh:0.##} V";

    public override void EvaluateLogic(IDigitalContext context)
    {
        var v = context.NodeVoltage(AnalogIn);
        var current = GetOutputState(0);

        var high = UseHysteresis
            ? current.IsHigh() ? v > ThresholdLow : v > ThresholdHigh
            : v > ThresholdHigh;

        context.Schedule(this, 0, LogicStateExtensions.FromBool(high), DelayFor(context));
    }

    partial void OnThresholdHighChanged(double value) => NotifyValueChanged();

    partial void OnThresholdLowChanged(double value) => NotifyValueChanged();
}

/// <summary>
/// Digital-to-analog boundary: reads a logic level and drives a configurable analog voltage pair,
/// so a TTL net can talk to an analog stage at whatever rail that stage needs.
/// </summary>
public partial class DacBridge : DigitalComponent
{
    public DacBridge()
    {
        PropagationDelay = 5e-9;
        DigitalIn = new Terminal("din", "D-IN", TerminalType.Input, new Point(-40, 0));
        AnalogOut = new Terminal("aout", "A-OUT", TerminalType.Output, new Point(40, 0));
        Terminals = [DigitalIn, AnalogOut];
        ConfigurePins([DigitalIn], [AnalogOut]);
    }

    public Terminal DigitalIn { get; }

    public Terminal AnalogOut { get; }

    /// <summary>Analog voltage driven for a logic high.</summary>
    [ObservableProperty]
    public partial double AnalogHigh { get; set; } = 5.0;

    /// <summary>Analog voltage driven for a logic low.</summary>
    [ObservableProperty]
    public partial double AnalogLow { get; set; }

    /// <summary>Thevenin output resistance in ohms.</summary>
    [ObservableProperty]
    public partial double SourceResistance { get; set; } = 50.0;

    public override string ComponentType => "DAC Bridge";

    public override string DesignatorPrefix => "DA";

    public override string ValueLabel => $"{AnalogLow:0.##}..{AnalogHigh:0.##} V";

    protected override double OutputVoltage(LogicState state) => state switch
    {
        LogicState.High => AnalogHigh,
        LogicState.Low => AnalogLow,
        _ => (AnalogHigh + AnalogLow) * 0.5,
    };

    public override void EvaluateLogic(IDigitalContext context) =>
        context.Schedule(this, 0, context.ReadInput(DigitalIn, Levels), DelayFor(context));

    public override void StampMatrix(Cirq.Core.Simulation.MnaSystem system, Cirq.Core.Simulation.SimulationState state)
    {
        Levels = Levels with { OutputResistance = SourceResistance };
        base.StampMatrix(system, state);
    }

    partial void OnAnalogHighChanged(double value) => NotifyValueChanged();

    partial void OnAnalogLowChanged(double value) => NotifyValueChanged();
}
