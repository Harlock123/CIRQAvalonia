using Cirq.Core.Primitives;
using Cirq.Core.Probing;
using Cirq.Core.Audio;
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
    private double[] _clip = [];
    private int _clipRate = 44_100;

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

    /// <summary>
    /// A WAV file to hear instead of the built-in tone. Empty — the default — leaves it on the
    /// tone.
    /// <para>
    /// A sine is the right signal for measuring a stage and the wrong one for judging it. Gain and
    /// distortion are numbers you can read off a scope; whether an amplifier sounds like anything
    /// is not, and a circuit fed one frequency for ever cannot tell you. Point this at a recording
    /// and the whole chain has real programme material going through it, which can then come out
    /// of the speaker at the other end and be listened to.
    /// </para>
    /// <para>
    /// The file's own sample rate is used, and values between its samples are interpolated —
    /// the solver asks for the signal at whatever instants it needs, which will not be the
    /// instants the file was recorded at.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial string SourcePath { get; set; } = string.Empty;

    /// <summary>Whether a clip that has run out starts again, or leaves it silent.</summary>
    [ObservableProperty]
    public partial bool LoopSource { get; set; } = true;

    /// <summary>Whether there is currently a sound at it. Double-click the capsule to change.</summary>
    [ObservableProperty]
    [Operable("Sound")]
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
            if (SourceError is not null) return [$"cannot read {SourcePath}: {SourceError}"];

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

    /// <summary>True when a clip has been loaded and is what is being heard.</summary>
    public bool IsPlayingClip => _clip.Length > 0;

    /// <summary>How long the loaded clip is, in seconds.</summary>
    public double ClipSeconds => _clip.Length / (double)Math.Max(_clipRate, 1);

    /// <summary>Why the file could not be read, if it could not be.</summary>
    public string? SourceError { get; private set; }

    /// <summary>The current it wants to sink right now, sound included.</summary>
    private double Demand(SimulationState state)
    {
        if (!IsHearingSound) return BiasCurrent;

        var swing = Math.Clamp(Sensitivity, 0.0, 0.95) * Signal(state.Time);

        return BiasCurrent * (1.0 + swing);
    }

    /// <summary>
    /// What it is hearing at an instant, from -1 to 1: the clip if one is loaded, the tone if not.
    /// </summary>
    private double Signal(double time)
    {
        if (_clip.Length == 0)
            return Math.Sin(2.0 * Math.PI * Math.Max(ToneFrequency, 1e-6) * time);

        var position = Math.Max(time, 0.0) * _clipRate;

        if (position >= _clip.Length - 1)
        {
            if (!LoopSource) return 0.0;

            // Modulo on the sample position rather than on the time, so a clip whose length is not
            // a whole number of solver steps still repeats seamlessly.
            position %= _clip.Length;
        }

        var index = (int)position;
        var through = position - index;
        var next = index + 1 < _clip.Length ? index + 1 : 0;

        return _clip[index] + ((_clip[next] - _clip[index]) * through);
    }

    partial void OnSourcePathChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            _clip = [];
            SourceError = null;
            NotifyValueChanged();
            return;
        }

        try
        {
            (_clip, _clipRate) = WaveFile.Read(value);
            SourceError = _clip.Length == 0 ? "the file has no audio in it" : null;
        }
        catch (Exception e) when (e is IOException or InvalidDataException
                                      or UnauthorizedAccessException or ArgumentException)
        {
            // Reported rather than thrown: a bad path is a thing to fix, not a crash mid-solve.
            _clip = [];
            SourceError = e.Message;
        }

        NotifyValueChanged();
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
