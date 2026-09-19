using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// An electret microphone capsule — the two-pin kind in every phone, intercom and cheap recorder.
/// <para>
/// It is not a passive transducer. There is a JFET inside the can, and the capsule works by
/// <b>sinking a bias current</b> that sound then modulates. That is why it has a polarity, and why
/// it does nothing at all until you give it a resistor to the supply: the resistor is what turns
/// the capsule's current into a voltage you can use. Wiring one straight across the rail is the
/// classic mistake — there is no resistor for the current to develop a signal across, so the
/// output sits at the supply and never moves.
/// </para>
/// <para>
/// With the LM386 and the speaker already here, this closes the loop: sound in, amplifier, sound
/// out, with nothing in the chain that is not a real part.
/// </para>
/// <para>
/// What the capsule can report is being <b>starved</b> — a bias resistor so large that there is
/// not enough voltage left across it to run the JFET. It cannot tell you the opposite mistake,
/// because a capsule wired straight to the rail is perfectly happy; it is the circuit that has no
/// resistance for the signal current to develop a voltage across, and the capsule has no way to
/// see that from its own two pins.
/// </para>
/// </summary>
public partial class Microphone : TwoTerminalComponent, IInteractiveComponent, ICurrentReporting
{
    public Microphone() : base("+", "-")
    {
    }

    /// <summary>Standing current the internal JFET draws, in amps.</summary>
    [ObservableProperty]
    public partial double BiasCurrent { get; set; } = 0.5e-3;

    /// <summary>
    /// How much of the bias current the sound swings, as a fraction. A quiet room is a percent or
    /// so; shouting at it is a third.
    /// </summary>
    [ObservableProperty]
    public partial double Sensitivity { get; set; } = 0.15;

    /// <summary>Frequency of the test tone it hears while it is switched on, in hertz.</summary>
    [ObservableProperty]
    public partial double ToneFrequency { get; set; } = 1e3;

    /// <summary>Whether there is currently a sound at it. Double-click the capsule to change.</summary>
    [ObservableProperty]
    public partial bool IsHearingSound { get; set; } = true;

    /// <summary>Voltage the internal JFET needs across it before it works at all.</summary>
    [ObservableProperty]
    public partial double MinimumOperatingVoltage { get; set; } = 1.0;

    public override string ComponentType => "Microphone";

    public override string DesignatorPrefix => "MK";

    public override string ValueLabel => IsHearingSound
        ? $"{SiPrefix.Format(ToneFrequency, "Hz")} tone"
        : "silent";

    public string InteractionHint => IsHearingSound ? "Silence it" : "Make a sound at it";

    public void Interact() => IsHearingSound = !IsHearingSound;

    public override bool IsNonlinear => true;

    /// <summary>Current the capsule is sinking at the last solved point, in amps.</summary>
    public double Current { get; private set; }

    /// <summary>Voltage across the capsule at the last solved point.</summary>
    public double Voltage { get; private set; }

    /// <summary>True while it has enough voltage across it to be working.</summary>
    public bool IsBiased => Voltage >= MinimumOperatingVoltage;

    public IReadOnlyList<string> Violations
    {
        get
        {
            // Nothing connected at all is not a fault worth reporting; a capsule sitting at the
            // rail with no resistor to work against is.
            if (Math.Abs(Voltage) < 1e-3 && Math.Abs(Current) < 1e-9) return [];

            if (!IsBiased)
            {
                return [$"only {SiPrefix.Format(Voltage, "V")} across it — the bias resistor is " +
                        "too large and the capsule is starved. Its JFET needs a volt or so before " +
                        "it will do anything at all"];
            }

            return [];
        }
    }

    /// <summary>The current it wants to sink right now, sound included.</summary>
    private double Demand(SimulationState state)
    {
        if (!IsHearingSound) return BiasCurrent;

        var swing = Math.Clamp(Sensitivity, 0.0, 0.95)
                    * Math.Sin(2.0 * Math.PI * Math.Max(ToneFrequency, 1e-6) * state.Time);

        return BiasCurrent * (1.0 + swing);
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var plus = system.Node(A);
        var minus = system.Node(B);

        var demand = Demand(state);

        // Never linearised below zero volts. A current sink has almost no conductance once it is
        // running, so the solve overshoots past zero, and evaluated there the device looks like a
        // perfect open and the node snaps back to the rail. Zero is where the conductance is
        // greatest, so it is the safe place to stand.
        var raw = system.IterationVoltageAcross(plus, minus);
        var v = Math.Max(raw, 0.0);

        // It cannot sink its full current until it has some voltage to do it across.
        var knee = Math.Max(MinimumOperatingVoltage, 1e-3);
        var exponent = Math.Exp(-v / knee);
        var current = demand * (1.0 - exponent);
        var conductance = demand * exponent / knee;

        system.StampConductance(plus, minus, conductance);
        system.StampCurrentSource(plus, minus, current - (conductance * v));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        Voltage = system.NodeVoltage(A) - system.NodeVoltage(B);

        var knee = Math.Max(MinimumOperatingVoltage, 1e-3);
        Current = Demand(state) * (1.0 - Math.Exp(-Math.Max(Voltage, 0.0) / knee));
    }

    public double TerminalCurrent(Terminal terminal, MnaSystem system, SimulationState state) =>
        CurrentIntoPin(terminal, Current);

    public override void ResetState()
    {
        Current = 0;
        Voltage = 0;
    }

    partial void OnIsHearingSoundChanged(bool value) => NotifyValueChanged();
}
