using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>A manually toggled logic level, the digital equivalent of a bench switch.</summary>
public partial class LogicToggle : DigitalComponent
{
    public LogicToggle(bool state = false)
    {
        State = state;
        Out = new Terminal("y", "OUT", TerminalType.Output, new Point(30, 0));
        Terminals = [Out];
        ConfigurePins([], [Out]);
        PropagationDelay = 1e-9;
    }

    public Terminal Out { get; }

    [ObservableProperty]
    [Operable("Level")]
    public partial bool State { get; set; }

    public override string ComponentType => "Logic Toggle";

    public override string DesignatorPrefix => "SW";

    public override string ValueLabel => State ? "1" : "0";

    public override void EvaluateLogic(IDigitalContext context) =>
        context.Schedule(this, 0, LogicStateExtensions.FromBool(State), DelayFor(context));

    partial void OnStateChanged(bool value) => NotifyValueChanged();
}

/// <summary>
/// Free-running logic clock. Edges are registered as solver breakpoints so the analog side always
/// places a time point exactly on them.
/// </summary>
public partial class ClockSource : DigitalComponent, IBreakpointSource
{
    public ClockSource(double frequency = 1e3, double dutyCycle = 0.5)
    {
        Frequency = frequency;
        DutyCycle = dutyCycle;
        Out = new Terminal("clk", "CLK", TerminalType.Output, new Point(30, 0));
        Terminals = [Out];
        ConfigurePins([], [Out]);
        PropagationDelay = 0;
    }

    public Terminal Out { get; }

    [ObservableProperty]
    [Operable("Frequency", Minimum = 0.1, Maximum = 1e6, Unit = "Hz", IsLogarithmic = true)]
    public partial double Frequency { get; set; }

    [ObservableProperty]
    public partial double DutyCycle { get; set; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; } = true;

    public override string ComponentType => "Clock";

    public override string DesignatorPrefix => "CLK";

    public override string ValueLabel => Cirq.Core.Units.SiPrefix.Format(Frequency, "Hz");

    public double Period => Frequency > 0 ? 1.0 / Frequency : double.PositiveInfinity;

    /// <summary>Logic level the clock should be driving at <paramref name="time"/>.</summary>
    public LogicState StateAt(double time)
    {
        if (!IsEnabled || Frequency <= 0) return LogicState.Low;
        var phase = time * Frequency;
        phase -= Math.Floor(phase);
        return phase < Math.Clamp(DutyCycle, 1e-6, 1 - 1e-6) ? LogicState.High : LogicState.Low;
    }

    public override void EvaluateLogic(IDigitalContext context) =>
        context.Schedule(this, 0, StateAt(context.Time), 0);

    public double? NextBreakpointAfter(double time)
    {
        if (!IsEnabled || Frequency <= 0) return null;

        var duty = Math.Clamp(DutyCycle, 1e-6, 1 - 1e-6);
        var period = Period;
        var cycle = Math.Floor(time / period);

        foreach (var candidate in new[] { cycle * period + duty * period, (cycle + 1) * period, (cycle + 1) * period + duty * period })
            if (candidate > time + 1e-15) return candidate;

        return null;
    }

    partial void OnFrequencyChanged(double value) => NotifyValueChanged();
}
