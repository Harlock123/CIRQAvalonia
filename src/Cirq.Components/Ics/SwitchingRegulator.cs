using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Ics;

/// <summary>
/// An MC34063 switching regulator controller.
/// <para>
/// Every other part in the Power group is linear: it drops the excess volts and turns them into
/// heat, so a 12 V rail brought down to 5 V throws away more than half of what goes in. A switcher
/// does not drop the difference, it chops it — the switch is either hard on or hard off, and an
/// inductor and a diode carry the energy across in between. That is why almost nothing is powered
/// the linear way any more.
/// </para>
/// <para>
/// This is a <b>controller</b>, not a converter. It brings the oscillator, the comparator, the
/// current limit and the switch; the topology is yours. Wired one way round it steps down, another
/// way it steps up, and another it inverts — the chip cannot tell the difference, which is the
/// thing worth understanding and the reason it is not supplied as a sealed block with a voltage
/// on the label. The inductor, the catch diode and the divider that sets the output are all parts
/// already in the palette.
/// </para>
/// <para>
/// It regulates by <b>skipping cycles</b> rather than by trimming the pulse width. The oscillator
/// runs continuously; each cycle the switch is allowed to conduct only if the feedback pin is
/// still below the internal 1.25 V reference. When the output is high enough, whole cycles are
/// simply left out. That is why a scope on the switch node of an MC34063 shows bursts rather than
/// an even train, and why its ripple is worse than a modern part's.
/// </para>
/// <para>
/// The oscillator frequency comes from the capacitor you hang on <c>CT</c>, not from a setting:
/// the chip charges it at a fixed current and discharges it faster, and the frequency falls out of
/// that. The datasheet's <c>Ct = 4.0e-5 × t_on</c> is what the currents here reproduce.
/// </para>
/// <para>
/// <b>On accuracy.</b> The switching instants are decided by the solve rather than known in
/// advance, so they land wherever the time step puts them — the same position the NE555 is in. For
/// believable ripple, shorten the simulation time step until the answer stops moving.
/// </para>
/// <para>
/// Pinout: 1=SWC, 2=SWE, 3=CT, 4=GND, 5=FB, 6=VCC, 7=IPK, 8=DRC.
/// </para>
/// </summary>
public partial class SwitchingRegulator : CircuitComponent
{
    private bool _charging = true;
    private bool _latched;

    /// <summary>Leaky accumulators for the duty readout: time on, against time altogether.</summary>
    private double _onTime;
    private double _totalTime;

    /// <summary>Window the duty and the current-limit indication are averaged over, in seconds.</summary>
    private const double ReportingWindow = 2e-3;

    /// <summary>When the current limit last tripped, so the readout survives the switch turning off.</summary>
    private double _lastLimitedAt = double.NegativeInfinity;

    public SwitchingRegulator()
    {
        SwitchCollector = new Terminal("swc", "SWC", TerminalType.Passive, new Point(-44, -40));
        SwitchEmitter = new Terminal("swe", "SWE", TerminalType.Passive, new Point(-44, -14));
        Timing = new Terminal("ct", "CT", TerminalType.Passive, new Point(-44, 14));
        Ground = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-44, 40));
        Feedback = new Terminal("fb", "FB", TerminalType.Input, new Point(44, 40));
        Supply = new Terminal("vcc", "VCC", TerminalType.Power, new Point(44, 14));
        CurrentSense = new Terminal("ipk", "IPK", TerminalType.Input, new Point(44, -14));
        DriveCollector = new Terminal("drc", "DRC", TerminalType.Passive, new Point(44, -40));

        Terminals =
        [
            SwitchCollector, SwitchEmitter, Timing, Ground,
            Feedback, Supply, CurrentSense, DriveCollector,
        ];
    }

    /// <summary>Pin 1: the top of the internal switch.</summary>
    public Terminal SwitchCollector { get; }

    /// <summary>Pin 2: the bottom of the internal switch.</summary>
    public Terminal SwitchEmitter { get; }

    /// <summary>Pin 3: the timing capacitor sets the frequency from here.</summary>
    public Terminal Timing { get; }

    public Terminal Ground { get; }

    /// <summary>Pin 5: held at the internal reference by the loop you build around it.</summary>
    public Terminal Feedback { get; }

    public Terminal Supply { get; }

    /// <summary>Pin 7: the current limit senses the drop from VCC to here.</summary>
    public Terminal CurrentSense { get; }

    /// <summary>
    /// Pin 8: base drive for the output Darlington on a real part. Nothing here depends on it,
    /// but tie it as you would on a board.
    /// </summary>
    public Terminal DriveCollector { get; }

    /// <summary>Voltage the feedback pin is held at, in volts.</summary>
    [ObservableProperty]
    public partial double ReferenceVoltage { get; set; } = 1.25;

    /// <summary>Current the timing capacitor is charged with, in amps.</summary>
    [ObservableProperty]
    public partial double ChargeCurrent { get; set; } = 20e-6;

    /// <summary>Current it is discharged with. Faster than charging, which is what sets the duty.</summary>
    [ObservableProperty]
    public partial double DischargeCurrent { get; set; } = 130e-6;

    /// <summary>Upper end of the timing ramp, in volts.</summary>
    [ObservableProperty]
    public partial double RampUpper { get; set; } = 1.25;

    /// <summary>Lower end of the timing ramp, in volts.</summary>
    [ObservableProperty]
    public partial double RampLower { get; set; } = 0.75;

    /// <summary>Drop from VCC to IPK that trips the current limit, in volts.</summary>
    [ObservableProperty]
    public partial double CurrentLimitVoltage { get; set; } = 0.3;

    /// <summary>
    /// Resistance of the switch when it is on, in ohms. A Darlington drops about a volt at an amp,
    /// and that is modelled as resistance rather than as a fixed offset.
    /// </summary>
    [ObservableProperty]
    public partial double SwitchResistance { get; set; } = 1.0;

    /// <summary>Resistance of the switch when it is off, in ohms.</summary>
    [ObservableProperty]
    public partial double OffResistance { get; set; } = 1e9;

    /// <summary>What the chip itself draws, in amps.</summary>
    [ObservableProperty]
    public partial double QuiescentCurrent { get; set; } = 2e-3;

    public override string ComponentType => "MC34063";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => IsCurrentLimited
        ? "current limit"
        : $"{DutyCycle * 100:0} % duty";

    public override bool IsNonlinear => true;

    /// <summary>True while the internal switch is conducting.</summary>
    public bool SwitchIsOn { get; private set; }

    /// <summary>
    /// True while the current limit has been tripping. Deliberately not the instantaneous
    /// comparison: the limit trips at the end of an on-time and the switch then opens, so at any
    /// moment you happen to look the current is usually zero and nothing appears to be wrong.
    /// </summary>
    public bool IsCurrentLimited { get; private set; }

    /// <summary>Feedback pin voltage at the last solved point.</summary>
    public double FeedbackVoltage { get; private set; }

    /// <summary>
    /// Fraction of recent time the switch was conducting.
    /// <para>
    /// Measured as time rather than as a count of cycles used, which is not the same thing and
    /// was wrong at first: a cycle the comparator allows and then cuts short a moment later is
    /// not the same as one that runs its full on-time, and counting both as "a cycle used" made a
    /// light load appear to work harder than a heavy one.
    /// </para>
    /// </summary>
    public double DutyCycle => _totalTime > 0 ? _onTime / _totalTime : 0;

    public IReadOnlyList<string> Violations
    {
        get
        {
            if (IsCurrentLimited)
            {
                return [$"the current limit is tripping — more than " +
                        $"{SiPrefix.Format(CurrentLimitVoltage, "V")} across the sense resistor. " +
                        "Either the load is too heavy for this inductor, or the inductor is too " +
                        "small and its current is ramping away inside a single cycle"];
            }

            return [];
        }
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var gnd = system.Node(Ground);

        // The timing capacitor is charged and discharged with fixed currents, so whatever you hang
        // on CT sets the frequency. This is the part that makes the chip's rate yours rather than
        // a number in the inspector.
        var timingCurrent = _charging ? ChargeCurrent : -DischargeCurrent;
        system.StampCurrentSource(gnd, system.Node(Timing), timingCurrent);

        // The feedback and sense pins are comparator inputs and draw next to nothing.
        system.StampResistor(system.Node(Feedback), gnd, 1e7);
        system.StampResistor(system.Node(CurrentSense), system.Node(Supply), 1e7);
        system.StampResistor(system.Node(DriveCollector), gnd, 1e9);

        system.StampCurrentSource(system.Node(Supply), gnd, QuiescentCurrent);

        system.StampResistor(
            system.Node(SwitchCollector), system.Node(SwitchEmitter),
            Math.Max(SwitchIsOn ? SwitchResistance : OffResistance, 1e-3));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var reference = system.NodeVoltage(Ground);

        var ramp = system.NodeVoltage(Timing) - reference;
        FeedbackVoltage = system.NodeVoltage(Feedback) - reference;

        var sense = system.NodeVoltage(Supply) - system.NodeVoltage(CurrentSense);
        var trippingNow = sense >= CurrentLimitVoltage;

        if (trippingNow) _lastLimitedAt = state.Time;
        IsCurrentLimited = state.Time - _lastLimitedAt < ReportingWindow;

        // The oscillator: a ramp between two thresholds, charging slowly and discharging fast.
        if (_charging && ramp >= RampUpper)
        {
            _charging = false;
        }
        else if (!_charging && ramp <= RampLower)
        {
            _charging = true;

            // A new cycle. The latch is set only if the output still needs help, which is the
            // whole of the regulation: when it does not, this cycle is skipped entirely.
            _latched = FeedbackVoltage < ReferenceVoltage && !trippingNow;
        }

        // Once the comparator or the current limit drops the latch, it stays down until the next
        // cycle begins. That is an S-R latch in the real part, not a continuous gate.
        if (_latched && (FeedbackVoltage >= ReferenceVoltage || trippingNow)) _latched = false;

        SwitchIsOn = _latched && _charging;

        // A leaky average, so the readout follows what it is doing now rather than the whole run.
        if (state.IsTransient)
        {
            _onTime += SwitchIsOn ? state.TimeStep : 0.0;
            _totalTime += state.TimeStep;

            if (_totalTime > ReportingWindow)
            {
                var scale = ReportingWindow / _totalTime;
                _onTime *= scale;
                _totalTime *= scale;
            }
        }

        NotifyValueChanged();
    }

    public override void ResetState()
    {
        _charging = true;
        _latched = false;
        _onTime = 0;
        _totalTime = 0;
        _lastLimitedAt = double.NegativeInfinity;
        SwitchIsOn = false;
        IsCurrentLimited = false;
        FeedbackVoltage = 0;
    }
}
