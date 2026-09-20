using Cirq.Core.Audio;
using Cirq.Core.Simulation;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>
/// A moving-coil loudspeaker, electrically a voice coil: a few ohms of wire with some inductance.
/// <para>
/// The impedance on the box is a nominal figure, not a resistance — it is the DC resistance plus
/// whatever the coil's inductance adds at frequency, which is why an "8 ohm" speaker measures
/// about six with a meter and rather more than eight at the top of its range. Both parts are here,
/// so an amplifier driving one sees a load that gets harder with frequency rather than a fixed
/// resistor.
/// </para>
/// <para>
/// What it reports is power, because that is what a speaker is rated in and what decides whether
/// it survives. The rating is thermal — a coil is a heater with a magnet near it — so it is the
/// average that counts, not the peaks, and the average is what is tracked.
/// </para>
/// </summary>
public partial class Speaker : Inductor
{
    /// <summary>Time constant the power average is taken over — a coil's thermal memory.</summary>
    private const double ThermalTimeConstant = 0.25;

    /// <summary>How much audio is captured before the file is rewritten, in seconds.</summary>
    private const double FlushInterval = 0.25;

    private readonly List<double> _captured = [];

    private int _clippedSamples;
    private double _nextSampleTime;
    private double _previousTime = double.NaN;
    private double _previousVoltage;
    private int _flushedAt;

    public Speaker(double impedance = 8.0)
    {
        NominalImpedance = impedance;
        Inductance = 500e-6;
        SeriesResistance = impedance * 0.8;     // DC resistance runs below the nominal figure
    }

    /// <summary>The figure on the box, in ohms.</summary>
    [ObservableProperty]
    public partial double NominalImpedance { get; set; }

    /// <summary>Continuous power it can take before the coil cooks, in watts.</summary>
    [ObservableProperty]
    public partial double PowerRating { get; set; } = 0.5;

    public override string ComponentType => "Speaker";

    public override string DesignatorPrefix => "LS";

    public override string ValueLabel => IsSounding
        ? $"{SiPrefix.Format(AveragePower, "W")}"
        : $"{SiPrefix.Format(NominalImpedance, "Ω")}";

    /// <summary>Power being dissipated in the coil right now, in watts.</summary>
    public double InstantaneousPower { get; private set; }

    /// <summary>Power averaged over the coil's thermal time constant, in watts.</summary>
    public double AveragePower { get; private set; }

    /// <summary>True while enough is going through it to hear.</summary>
    public bool IsSounding => AveragePower > PowerRating * 1e-3;

    public override IReadOnlyList<string> Violations
    {
        get
        {
            List<string> found = [.. base.Violations];

            if (AveragePower > PowerRating)
            {
                found.Add(
                    $"taking {SiPrefix.Format(AveragePower, "W")} against a " +
                    $"{SiPrefix.Format(PowerRating, "W")} rating — the coil is a heater and this " +
                    "one is being asked to run hot");
            }

            if (RecordingError is not null) found.Add($"cannot write the recording: {RecordingError}");

            // A steady fraction of the file against the end stop, rather than the handful of
            // samples a real transient costs: an amplifier's turn-on thump clips a couple of
            // milliseconds and is part of what the circuit did, not a fault in the recorder.
            if (ClippedFraction > 0.01)
            {
                found.Add(
                    $"{ClippedFraction:P0} of the recording is clipping, peaking at " +
                    $"{RecordedPeak:0.0}x full scale — raise Recording Full Scale Volts, which is " +
                    "a level on the recorder rather than anything the circuit is doing");
            }

            return found;
        }
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        base.CommitTimeStep(system, state);

        if (IsRecording && state.IsTransient) Capture(system, state);

        InstantaneousPower = Current * Current * Math.Max(SeriesResistance, 1e-9);

        if (!state.IsTransient)
        {
            AveragePower = InstantaneousPower;
            return;
        }

        // A first-order average rather than the instantaneous value: a speaker fed a sine wave is
        // at zero watts twice a cycle, and the coil does not care.
        var alpha = Math.Clamp(state.TimeStep / ThermalTimeConstant, 0.0, 1.0);
        AveragePower += (InstantaneousPower - AveragePower) * alpha;
    }

    public override void ResetState()
    {
        // Whatever was captured belongs to the run that just ended, so it goes to disc before the
        // buffer is thrown away: a reset is the usual way a run stops.
        Flush();

        base.ResetState();
        InstantaneousPower = 0;
        AveragePower = 0;

        _captured.Clear();
        _nextSampleTime = 0;
        _previousTime = double.NaN;
        _previousVoltage = 0;
        _flushedAt = 0;
        _clippedSamples = 0;
        RecordedPeak = 0;
    }

    partial void OnNominalImpedanceChanged(double value) => NotifyValueChanged();

    /// <summary>
    /// Where to write what the speaker is being fed, as a WAV file. Empty — the default — records
    /// nothing.
    /// <para>
    /// The point of it is that a waveform on a scope and a sound are not the same evidence. An
    /// amplifier clipping on its peaks is a small flat spot on the trace and an unmistakable noise,
    /// and the second one is what tells you how bad it is. Set a path, run the circuit, and open
    /// the file in anything.
    /// </para>
    /// <para>
    /// It records the voltage across the terminals resampled onto a fixed grid, which it has to be:
    /// the solver's time points are wherever the circuit needed them and a sound card wants
    /// <see cref="RecordingSampleRate"/> of them a second, evenly. Anything above half that rate
    /// cannot be represented and will come back as something else, which is aliasing and is a
    /// property of sampling rather than of this program.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial string RecordingPath { get; set; } = string.Empty;

    /// <summary>Samples a second in the recorded file.</summary>
    [ObservableProperty]
    public partial int RecordingSampleRate { get; set; } = 44_100;

    /// <summary>
    /// Volts across the speaker that fill the file's range. Fixed rather than normalised to the
    /// loudest peak, because a recorder that normalises makes a quiet circuit and a loud one sound
    /// identical — which would hide exactly the thing most worth listening for.
    /// </summary>
    [ObservableProperty]
    public partial double RecordingFullScaleVolts { get; set; } = 2.0;

    /// <summary>True while a path is set, so the part can say on the canvas that it is recording.</summary>
    public bool IsRecording => !string.IsNullOrWhiteSpace(RecordingPath);

    /// <summary>How much audio has been captured so far, in seconds.</summary>
    public double RecordedSeconds => _captured.Count / (double)Math.Max(RecordingSampleRate, 1);

    /// <summary>Loudest sample captured, relative to full scale — over 1.0 means it is clipping.</summary>
    public double RecordedPeak { get; private set; }

    /// <summary>
    /// How much of the recording is hard against the end of the range, from 0 to 1.
    /// <para>
    /// Reported alongside the peak because the two say different things. A single loud instant
    /// clips a handful of samples and is usually a real event — an amplifier's turn-on thump is a
    /// couple of milliseconds of it and belongs in the recording. A level set too low clips a
    /// steady fraction of everything, which is the case worth warning about.
    /// </para>
    /// </summary>
    public double ClippedFraction =>
        _captured.Count == 0 ? 0 : _clippedSamples / (double)_captured.Count;

    /// <summary>Anything that went wrong writing the file, rather than an exception mid-solve.</summary>
    public string? RecordingError { get; private set; }

    /// <summary>
    /// Resamples the terminal voltage onto the fixed grid and writes out whatever has built up.
    /// <para>
    /// Time points arrive wherever the solver put them, so each one covers a stretch of the grid
    /// rather than a single position on it, and the samples in between are interpolated. Without
    /// that, the recording's pitch would follow the solver's step size: a stretch where the
    /// circuit was easy to solve would play back faster than one where it was not.
    /// </para>
    /// </summary>
    private void Capture(MnaSystem system, SimulationState state)
    {
        var voltage = system.NodeVoltage(A) - system.NodeVoltage(B);

        if (double.IsNaN(_previousTime) || state.Time <= _previousTime)
        {
            _previousTime = state.Time;
            _previousVoltage = voltage;
            return;
        }

        var interval = 1.0 / Math.Max(RecordingSampleRate, 1);
        var scale = Math.Max(RecordingFullScaleVolts, 1e-9);
        var span = state.Time - _previousTime;

        while (_nextSampleTime <= state.Time)
        {
            var through = (_nextSampleTime - _previousTime) / span;
            var value = _previousVoltage + ((voltage - _previousVoltage) * Math.Clamp(through, 0.0, 1.0));

            var sample = value / scale;

            _captured.Add(sample);
            RecordedPeak = Math.Max(RecordedPeak, Math.Abs(sample));

            if (Math.Abs(sample) >= 1.0) _clippedSamples++;

            _nextSampleTime += interval;
        }

        _previousTime = state.Time;
        _previousVoltage = voltage;

        if (RecordedSeconds - (_flushedAt / (double)Math.Max(RecordingSampleRate, 1)) >= FlushInterval)
            Flush();
    }

    /// <summary>
    /// Writes the file. Called as the recording grows rather than only at the end, because nothing
    /// tells a component that a run has stopped — so the file is never more than a quarter of a
    /// second behind whatever is on the scope, and is a complete, playable WAV at every moment.
    /// </summary>
    public void Flush()
    {
        if (!IsRecording || _captured.Count == 0) return;

        try
        {
            WaveFile.Write(RecordingPath, _captured, Math.Max(RecordingSampleRate, 1));

            _flushedAt = _captured.Count;
            RecordingError = null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            // A bad path must not take the simulation down with it: the circuit is still valid.
            RecordingError = e.Message;
        }

        NotifyValueChanged();
    }

    partial void OnRecordingPathChanged(string value)
    {
        RecordingError = null;
        NotifyValueChanged();
    }
}
