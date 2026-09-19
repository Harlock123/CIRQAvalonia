using Cirq.Core.Simulation;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Passive;

/// <summary>Which sort of buzzer: one that needs driving, or one that drives itself.</summary>
public enum BuzzerKind
{
    /// <summary>
    /// A bare piezo element. Electrically a small capacitor, and it makes no sound at all unless
    /// the drive alternates.
    /// </summary>
    Passive,

    /// <summary>A packaged buzzer with its own oscillator. Apply DC and it sounds.</summary>
    Active,
}

/// <summary>
/// A piezo buzzer.
/// <para>
/// The distinction between the two sorts is the whole reason this is not just a load. A
/// <b>passive</b> element is a capacitor: it converts a changing voltage into movement, so a
/// steady one does nothing whatever. Wiring a passive piezo across a pin that is simply switched
/// high is the single most common reason a first buzzer circuit is silent, and this reports it
/// rather than drawing a plausible current and leaving you to wonder. An <b>active</b> buzzer has
/// its own oscillator behind the element, so DC is exactly what it wants.
/// </para>
/// <para>
/// The sounding frequency of a passive element is measured from the drive rather than assumed,
/// so it follows whatever is actually on the pin.
/// </para>
/// </summary>
public partial class Buzzer : Capacitor
{
    /// <summary>Drive below this fraction of the peak seen is not counted as a crossing.</summary>
    private const double CrossingFraction = 0.5;

    /// <summary>
    /// How long a driven element may go without the drive alternating before it counts as silent,
    /// in seconds. Fifty milliseconds is twenty hertz — below anything you would hear anyway.
    /// </summary>
    private const double SilenceWindow = 50e-3;

    private double _peakVoltage;
    private double _lastCrossing = double.NegativeInfinity;
    private double _period;
    private bool _wasAbove;

    public Buzzer(BuzzerKind kind = BuzzerKind.Passive)
        : base(15e-9)
    {
        Kind = kind;
    }

    [ObservableProperty]
    public partial BuzzerKind Kind { get; set; }

    /// <summary>Supply an active buzzer expects, in volts.</summary>
    [ObservableProperty]
    public partial double RatedVoltage { get; set; } = 5.0;

    /// <summary>Current an active buzzer draws at its rated voltage, in amps.</summary>
    [ObservableProperty]
    public partial double RatedCurrent { get; set; } = 25e-3;

    /// <summary>Note an active buzzer sounds, in hertz. A passive one sounds at whatever it is fed.</summary>
    [ObservableProperty]
    public partial double SelfOscillationFrequency { get; set; } = 2300.0;

    public override string ComponentType => Kind is BuzzerKind.Active ? "Active Buzzer" : "Piezo Buzzer";

    public override string DesignatorPrefix => "LS";

    public override string ValueLabel =>
        IsSounding ? SiPrefix.Format(SoundFrequency, "Hz") : Kind is BuzzerKind.Active ? "silent" : "piezo";

    /// <summary>True while the element is actually making a noise.</summary>
    public bool IsSounding { get; private set; }

    /// <summary>What it is sounding at, in hertz.</summary>
    public double SoundFrequency =>
        Kind is BuzzerKind.Active ? SelfOscillationFrequency : (_period > 0 ? 1.0 / _period : 0.0);

    /// <summary>Voltage across the element at the last solved point, in volts.</summary>
    public double DriveVoltage { get; private set; }

    /// <summary>True when a passive element is being driven, but with something that never changes.</summary>
    public bool IsDrivenWithoutAlternating { get; private set; }

    /// <summary>What is wrong with how this buzzer is being driven, if anything.</summary>
    public IReadOnlyList<string> Violations =>
        IsDrivenWithoutAlternating
            ? [$"driven with a steady {DriveVoltage:0.0} V — a passive piezo is a capacitor and " +
               "makes no sound without an alternating drive. Feed it a square wave, or fit an " +
               "active buzzer, which has its own oscillator"]
            : [];

    /// <summary>An active buzzer is a load; a passive one is the capacitor the base class stamps.</summary>
    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        if (Kind is BuzzerKind.Passive)
        {
            base.StampMatrix(system, state);
            return;
        }

        var resistance = RatedCurrent > 0 ? RatedVoltage / RatedCurrent : 1e9;
        system.StampResistor(system.Node(A), system.Node(B), Math.Max(resistance, 1e-3));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        if (Kind is BuzzerKind.Passive) base.CommitTimeStep(system, state);

        DriveVoltage = VoltageAcross(system, A, B);
        var magnitude = Math.Abs(DriveVoltage);
        _peakVoltage = Math.Max(_peakVoltage, magnitude);

        if (Kind is BuzzerKind.Active)
        {
            // An active buzzer just needs enough volts across it.
            IsSounding = magnitude > RatedVoltage * 0.5;
            IsDrivenWithoutAlternating = false;
            return;
        }

        // A passive element sounds only while the drive keeps changing, so the frequency is taken
        // from the drive itself: time between successive upward crossings of the half-way point.
        var threshold = _peakVoltage * CrossingFraction;
        var above = DriveVoltage > threshold;

        if (above && !_wasAbove && threshold > 1e-6)
        {
            if (!double.IsNegativeInfinity(_lastCrossing))
            {
                var interval = state.Time - _lastCrossing;
                if (interval > 0) _period = interval;
            }

            _lastCrossing = state.Time;
        }

        _wasAbove = above;

        var since = state.Time - _lastCrossing;
        var driven = _peakVoltage > 0.5;

        IsSounding = driven && since < SilenceWindow;
        IsDrivenWithoutAlternating = driven && magnitude > 0.5 && since > SilenceWindow;
    }

    public override void ResetState()
    {
        base.ResetState();
        _peakVoltage = 0;
        _lastCrossing = double.NegativeInfinity;
        _period = 0;
        _wasAbove = false;
        DriveVoltage = 0;
        IsSounding = false;
        IsDrivenWithoutAlternating = false;
    }

    partial void OnKindChanged(BuzzerKind value) => NotifyValueChanged();
}
