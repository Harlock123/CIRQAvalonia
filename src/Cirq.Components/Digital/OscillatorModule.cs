using Cirq.Core.Digital;
using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Digital;

/// <summary>
/// A crystal oscillator module: the four-pin metal can that is on almost every board.
/// <para>
/// A bare crystal is a resonator and needs an amplifier round it before it does anything. This is
/// that amplifier and the crystal together in one package, and it simply produces a square wave —
/// which is why it is what you actually find soldered next to a microcontroller rather than the
/// two-pin part.
/// </para>
/// <para>
/// It is not the same thing as the Clock in Digital I/O, which is an idealised source that runs
/// whatever is around it. This one has a supply and an enable, so it stops when either is missing,
/// and it drives at the logic levels of the rail it is given. Leaving the enable pin floating is
/// the usual mistake: on a real module that pin has a pull-up and the part runs, which is why the
/// enable is held high here unless something pulls it down.
/// </para>
/// </summary>
public sealed partial class OscillatorModule : DigitalIc, IBreakpointSource
{
    public OscillatorModule(double frequency = 16e6) : base(4)
    {
        Frequency = frequency;
        PropagationDelay = 1e-9;

        Enable = new Terminal("en", "EN", TerminalType.Input, new Point(-40, -22));
        Gnd = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-40, 22));
        Output = new Terminal("out", "OUT", TerminalType.Output, new Point(40, 22));
        Vcc = new Terminal("vcc", "VCC", TerminalType.Power, new Point(40, -22));

        Terminals = [Enable, Vcc, Gnd, Output];
        ConfigurePins([Enable], [Output]);
    }

    /// <summary>Pin 1: an output disable, pulled up inside the can.</summary>
    public Terminal Enable { get; }

    public Terminal Output { get; }

    /// <summary>What the can says on it, in hertz.</summary>
    [ObservableProperty]
    public partial double Frequency { get; set; }

    /// <summary>Fraction of each cycle the output is high.</summary>
    [ObservableProperty]
    public partial double DutyCycle { get; set; } = 0.5;

    public override string PartNumber => "OSC";

    public override string ComponentType => "Oscillator";

    public override string ValueLabel => SiPrefix.Format(Frequency, "Hz");

    /// <summary>Pull-up on the enable pin, in ohms.</summary>
    [ObservableProperty]
    public partial double EnablePullUp { get; set; } = 100e3;

    /// <summary>True while it is powered and not disabled.</summary>
    public bool IsRunning { get; private set; }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        base.StampMatrix(system, state);

        // The pull-up that makes a floating enable mean "running". Without it the base class's
        // input leakage to ground is the only thing on the pin, so an unwired enable reads low
        // and the part sits there doing nothing — which is neither what the datasheet says nor
        // what anyone wiring one would expect.
        system.StampResistor(system.Node(Enable), system.Node(Vcc), Math.Max(EnablePullUp, 1.0));
    }

    /// <summary>
    /// The enable pin is pulled up inside the can, so it runs unless something actively pulls it
    /// down. A floating pin means running, not stopped.
    /// </summary>
    private bool IsEnabled(IDigitalContext context) =>
        context.ReadInput(Enable, Levels) != LogicState.Low;

    protected override void EvaluatePoweredLogic(IDigitalContext context)
    {
        IsRunning = IsEnabled(context);

        if (!IsRunning)
        {
            // Disabled leaves the output floating rather than driving it low, as a real can does.
            context.Schedule(this, 0, LogicState.HighImpedance, DelayFor(context));
            return;
        }

        context.Schedule(this, 0, LevelAt(context.Time), DelayFor(context));
    }

    private LogicState LevelAt(double time)
    {
        var period = 1.0 / Math.Max(Frequency, 1e-9);
        var phase = time / period;
        phase -= Math.Floor(phase);

        return phase < Math.Clamp(DutyCycle, 1e-6, 1 - 1e-6) ? LogicState.High : LogicState.Low;
    }

    /// <summary>
    /// Edges land exactly rather than wherever the time step falls, which matters at these rates:
    /// a 16 MHz module has a 62 ns period and the solver's step is rarely finer than that.
    /// </summary>
    public double? NextBreakpointAfter(double time)
    {
        if (!IsRunning) return null;

        var period = 1.0 / Math.Max(Frequency, 1e-9);
        var duty = Math.Clamp(DutyCycle, 1e-6, 1 - 1e-6);

        var cycle = Math.Floor(time / period);
        var start = cycle * period;

        foreach (var edge in new[] { start + (duty * period), start + period })
            if (edge > time) return edge;

        return start + period + (duty * period);
    }

    public override void ResetLogic()
    {
        base.ResetLogic();
        IsRunning = false;
    }

    partial void OnFrequencyChanged(double value) => NotifyValueChanged();
}
