using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Core.Units;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Electromechanical;

/// <summary>
/// A hobby RC servo: three wires, and an angle set by how long a pulse is.
/// <para>
/// The signal is not an analogue voltage and not a duty cycle — it is a <b>pulse width</b>.
/// Between about one and two milliseconds maps across the servo's travel, repeated every twenty
/// milliseconds or so, and the gap between pulses carries no information at all. That is why a
/// servo is driven from a timer or a board's PWM pin and why changing the PWM frequency rather
/// than the pulse length makes it behave oddly.
/// </para>
/// <para>
/// It holds position between pulses and only moves at a finite speed, so a commanded jump takes
/// time to arrive. Stop sending pulses and it goes limp rather than snapping back.
/// </para>
/// </summary>
public partial class Servo : CircuitComponent
{
    /// <summary>How long without a pulse before it decides nothing is driving it, in seconds.</summary>
    private const double SignalTimeout = 100e-3;

    private bool _signalHigh;
    private double _lastRisingEdge = double.NaN;
    private double _lastPulseArrived = double.NegativeInfinity;

    public Servo()
    {
        Supply = new Terminal("v+", "V+", TerminalType.Power, new Point(-40, -24));
        Ground = new Terminal("gnd", "GND", TerminalType.Ground, new Point(-40, 24));
        Signal = new Terminal("sig", "SIG", TerminalType.Input, new Point(-40, 0));

        Terminals = [Supply, Signal, Ground];
        Angle = 0;
        CommandedAngle = 0;
    }

    public Terminal Supply { get; }

    public Terminal Ground { get; }

    /// <summary>The pulse input. Width, not level, is what it reads.</summary>
    public Terminal Signal { get; }

    /// <summary>Pulse width at one end of travel, in seconds.</summary>
    [ObservableProperty]
    public partial double MinimumPulseWidth { get; set; } = 1e-3;

    /// <summary>Pulse width at the other end of travel, in seconds.</summary>
    [ObservableProperty]
    public partial double MaximumPulseWidth { get; set; } = 2e-3;

    /// <summary>Travel either side of centre, in degrees.</summary>
    [ObservableProperty]
    public partial double TravelDegrees { get; set; } = 90.0;

    /// <summary>How fast it can move, in degrees per second.</summary>
    [ObservableProperty]
    public partial double SlewRate { get; set; } = 400.0;

    /// <summary>Current it draws while holding position, in amps.</summary>
    [ObservableProperty]
    public partial double IdleCurrent { get; set; } = 8e-3;

    /// <summary>Current it draws while moving, in amps.</summary>
    [ObservableProperty]
    public partial double MovingCurrent { get; set; } = 250e-3;

    /// <summary>Voltage above which the signal line counts as high.</summary>
    [ObservableProperty]
    public partial double LogicThreshold { get; set; } = 1.5;

    public override string ComponentType => "Servo";

    public override string DesignatorPrefix => "M";

    public override string ValueLabel => IsDriven ? $"{Angle:0.#}°" : "no signal";

    /// <summary>Where the shaft actually is, in degrees from centre.</summary>
    public double Angle { get; private set; }

    /// <summary>Where the last pulse asked it to be, in degrees from centre.</summary>
    public double CommandedAngle { get; private set; }

    /// <summary>Width of the last complete pulse, in seconds.</summary>
    public double LastPulseWidth { get; private set; }

    /// <summary>True while pulses are still arriving.</summary>
    public bool IsDriven { get; private set; }

    /// <summary>True while the shaft is still travelling towards the commanded angle.</summary>
    public bool IsMoving => IsDriven && Math.Abs(CommandedAngle - Angle) > 0.05;

    public IReadOnlyList<string> Violations
    {
        get
        {
            if (!IsDriven || double.IsNaN(LastPulseWidth)) return [];

            // A pulse outside the servo's range does not give more travel; it hits the stop and
            // the servo buzzes against it.
            if (LastPulseWidth < MinimumPulseWidth * 0.85 || LastPulseWidth > MaximumPulseWidth * 1.15)
            {
                return [$"{SiPrefix.Format(LastPulseWidth, "s")} pulse is outside the " +
                        $"{SiPrefix.Format(MinimumPulseWidth, "s")} to " +
                        $"{SiPrefix.Format(MaximumPulseWidth, "s")} range — a real servo drives " +
                        "against its end stop and stalls there"];
            }

            return [];
        }
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        // The signal input is high impedance, as a servo's is.
        system.StampResistor(system.Node(Signal), system.Node(Ground), 100e3);

        // What it draws from the supply, which is most of why servos need their own battery.
        var draw = IsMoving ? MovingCurrent : IdleCurrent;
        if (!IsDriven) draw = 0;

        system.StampCurrentSource(system.Node(Supply), system.Node(Ground), draw);
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        var reference = system.NodeVoltage(Ground);
        var level = system.NodeVoltage(Signal) - reference > LogicThreshold;

        if (level && !_signalHigh) _lastRisingEdge = state.Time;

        if (!level && _signalHigh && !double.IsNaN(_lastRisingEdge))
        {
            LastPulseWidth = state.Time - _lastRisingEdge;
            _lastPulseArrived = state.Time;

            var span = Math.Max(MaximumPulseWidth - MinimumPulseWidth, 1e-9);
            var fraction = Math.Clamp((LastPulseWidth - MinimumPulseWidth) / span, 0.0, 1.0);

            CommandedAngle = ((fraction * 2.0) - 1.0) * TravelDegrees;
        }

        _signalHigh = level;
        IsDriven = state.Time - _lastPulseArrived < SignalTimeout;

        if (!IsDriven || !state.IsTransient) return;

        // It moves at a finite speed, so a commanded jump takes time to arrive.
        var step = SlewRate * state.TimeStep;
        var error = CommandedAngle - Angle;

        Angle += Math.Abs(error) <= step ? error : Math.Sign(error) * step;

        NotifyValueChanged();
    }

    public override void ResetState()
    {
        Angle = 0;
        CommandedAngle = 0;
        LastPulseWidth = 0;
        IsDriven = false;
        _signalHigh = false;
        _lastRisingEdge = double.NaN;
        _lastPulseArrived = double.NegativeInfinity;
    }
}
