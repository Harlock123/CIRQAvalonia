using Cirq.Core.Primitives;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.Components.Nonlinear;

/// <summary>
/// A TL431 programmable shunt reference: three terminals that behave like a zener you can set.
/// <para>
/// It sinks whatever current it takes from cathode to anode to hold its reference pin at 2.495 V.
/// Tie the reference to the cathode and it is a fixed 2.5 V shunt; feed the reference from a
/// divider off the cathode and it holds the cathode at whatever that divider scales 2.495 V up to.
/// That is why almost every switching supply has one — it is the adjustable part of the feedback
/// loop, and usually the thing on the other side of the optocoupler.
/// </para>
/// <para>
/// It needs a milliamp or so through it to regulate at all. Sizing the resistor from the supply so
/// that the load takes everything and the reference is starved is the classic way to end up with a
/// circuit that almost works, so the part reports it.
/// </para>
/// </summary>
public partial class ShuntReference : CircuitComponent
{
    /// <summary>Current at which the transition from off to conducting is centred, in amps.</summary>
    private const double MaximumSink = 0.1;

    /// <summary>The reference error the last iteration was linearised at. NaN before the first.</summary>
    private double _previousError = double.NaN;

    private bool _limitedThisIteration;

    public ShuntReference()
    {
        Cathode = new Terminal("k", "K", TerminalType.Passive, new Point(0, -40));
        Anode = new Terminal("a", "A", TerminalType.Passive, new Point(0, 40));
        Reference = new Terminal("r", "REF", TerminalType.Input, new Point(40, 0));

        Terminals = [Cathode, Anode, Reference];
    }

    public Terminal Cathode { get; }

    public Terminal Anode { get; }

    /// <summary>The pin the device works to hold at <see cref="SetpointVoltage"/>.</summary>
    public Terminal Reference { get; }

    /// <summary>The internal bandgap the reference pin is held at, in volts.</summary>
    [ObservableProperty]
    public partial double SetpointVoltage { get; set; } = 2.495;

    /// <summary>
    /// How far the reference bows over temperature, in volts per degree squared.
    /// <para>
    /// A bandgap reference does not drift <b>linearly</b>, and that is the entire point of one.
    /// It is built by adding two voltages that move opposite ways — a junction drop falling about
    /// two millivolts a degree, and a difference between two junctions rising — so the sum is
    /// flat to first order and what is left is the second-order term. The curve has a shallow
    /// maximum and falls away either side of it.
    /// </para>
    /// <para>
    /// Which is why the datasheet quotes a <i>deviation band</i> over a range rather than a
    /// coefficient in ppm per degree: there is no single slope to quote. Modelling it as a
    /// straight line would get the ends roughly right and the middle wrong, and would lose the
    /// reason anybody pays for a bandgap rather than using a zener.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial double CurvaturePerKelvinSquared { get; set; } = 8e-7;

    /// <summary>
    /// Where the bow peaks, in degrees Celsius. Trimmed in manufacture to put the flat part of
    /// the curve in the middle of the range the part is sold for.
    /// </summary>
    [ObservableProperty]
    public partial double CurvaturePeakCelsius { get; set; } = 30.0;

    /// <summary>The reference at a temperature, in volts.</summary>
    public double SetpointAt(double celsius)
    {
        var offset = celsius - CurvaturePeakCelsius;

        return SetpointVoltage - (Math.Max(CurvaturePerKelvinSquared, 0) * offset * offset);
    }

    /// <summary>
    /// Transconductance in amps per volt of reference error. High, because the open-loop gain of a
    /// real part is: a few millivolts of error swings it from off to fully conducting.
    /// </summary>
    [ObservableProperty]
    public partial double Transconductance { get; set; } = 10.0;

    /// <summary>Cathode voltage below which it stops behaving as a reference, in volts.</summary>
    [ObservableProperty]
    public partial double MinimumCathodeVoltage { get; set; } = 2.0;

    /// <summary>Current it needs to pass before it regulates properly, in amps.</summary>
    [ObservableProperty]
    public partial double MinimumOperatingCurrent { get; set; } = 1e-3;

    /// <summary>Resistance the reference pin presents, which sets its bias current.</summary>
    [ObservableProperty]
    public partial double ReferenceResistance { get; set; } = 1.25e6;

    public override string ComponentType => "TL431";

    public override string DesignatorPrefix => "U";

    public override string ValueLabel => "TL431";

    public override bool IsNonlinear => true;

    /// <summary>Current sunk from cathode to anode at the last solved point, in amps.</summary>
    public double CathodeCurrent { get; private set; }

    /// <summary>Voltage on the reference pin relative to the anode, in volts.</summary>
    public double ReferenceVoltage { get; private set; }

    /// <summary>Voltage from cathode to anode, in volts.</summary>
    public double CathodeVoltage { get; private set; }

    /// <summary>True while it is passing enough current, and has enough voltage, to regulate.</summary>
    public bool IsRegulating =>
        CathodeCurrent >= MinimumOperatingCurrent && CathodeVoltage >= MinimumCathodeVoltage;

    /// <summary>What is wrong with how this reference is biased, if anything.</summary>
    public IReadOnlyList<string> Violations
    {
        get
        {
            // An unpowered circuit is not a fault, but "starved" and "unpowered" both look like a
            // low cathode voltage — a reference fed through far too large a resistor is dragged
            // down to a fraction of a volt, which is exactly the case worth reporting. The two are
            // told apart by whether anything is happening at all rather than by how low it sits.
            if (CathodeCurrent < 1e-9 && CathodeVoltage < 0.05) return [];

            if (CathodeCurrent < MinimumOperatingCurrent)
            {
                return [$"only {CathodeCurrent * 1e6:0} µA through it against a " +
                        $"{MinimumOperatingCurrent * 1e3:0.#} mA minimum — it cannot regulate on " +
                        "that, so lower the resistor feeding the cathode"];
            }

            return [];
        }
    }

    /// <summary>How wide the sigmoid is: the slope through the setpoint is the transconductance.</summary>
    private double ErrorWidth => MaximumSink / (4.0 * Math.Max(Transconductance, 1e-6));

    /// <summary>
    /// How hard it wants to conduct for a given reference error, and the slope of that.
    /// <para>
    /// A bounded sigmoid rather than an unbounded gain: the derivative is largest at the setpoint
    /// and falls away either side, so Newton always has a finite step to take. An unbounded
    /// high-gain term would ask for hundreds of amps a few volts off the setpoint.
    /// </para>
    /// </summary>
    private (double Current, double Slope) Demand(double error)
    {
        var width = ErrorWidth;

        // The error is never linearised far out on the sigmoid's tails. Out there the curve is
        // flat, so the Jacobian carries no feedback at all and the device linearises as a plain
        // current sink — which drove the cathode hundreds of volts negative and straight back
        // again, iteration after iteration. Six widths still spans a quarter of a milliamp to
        // very nearly the full sink, which is the whole useful range, and keeps a real gradient
        // to walk down.
        var x = Math.Clamp(error / width, -6.0, 6.0);
        var sigmoid = 1.0 / (1.0 + Math.Exp(-x));

        return (MaximumSink * sigmoid, MaximumSink * sigmoid * (1.0 - sigmoid) / width);
    }

    public override void StampMatrix(MnaSystem system, SimulationState state)
    {
        var cathode = system.Node(Cathode);
        var anode = system.Node(Anode);
        var reference = system.Node(Reference);

        // The reference pin's own bias current.
        system.StampResistor(reference, anode, Math.Max(ReferenceResistance, 1.0));

        var setpoint = SetpointAt(state.TemperatureKelvin - 273.15);
        var error = LimitError(system.IterationVoltageAcross(reference, anode) - setpoint);

        // The reference voltage the linearisation was actually taken at, which after limiting is
        // not the one the matrix asked for. The companion's constant term has to use this one or
        // it describes a tangent at a different place from where it was measured.
        var vref = setpoint + error;

        var (demand, slope) = Demand(error);

        // Never linearised at a negative cathode voltage. Like the optocoupler's output, this is
        // close to a current sink once it is conducting, so the solve overshoots past zero; taken
        // literally there the device would look like an open and the node would snap back to the
        // rail. Zero is where the conductance is greatest, so it is the safe place to stand.
        var raw = system.IterationVoltageAcross(cathode, anode);
        var vka = Math.Max(raw, 0.0);

        var knee = Math.Max(MinimumCathodeVoltage, 1e-3);
        var exponent = Math.Exp(-vka / knee);
        var saturation = 1.0 - exponent;

        var current = demand * saturation;

        var gka = demand * exponent / knee;       // d(i)/d(v_ka): coming out of saturation
        var gm = slope * saturation;              // d(i)/d(v_ref): the feedback that does the work

        system.StampConductance(cathode, anode, gka);
        system.StampVccs(cathode, anode, reference, anode, gm);
        system.StampCurrentSource(cathode, anode, current - (gka * vka) - (gm * vref));
    }

    public override void CommitTimeStep(MnaSystem system, SimulationState state)
    {
        ReferenceVoltage = system.NodeVoltage(Reference) - system.NodeVoltage(Anode);
        CathodeVoltage = system.NodeVoltage(Cathode) - system.NodeVoltage(Anode);

        var (demand, _) = Demand(ReferenceVoltage - SetpointAt(state.TemperatureKelvin - 273.15));
        var saturation = 1.0 - Math.Exp(-Math.Max(CathodeVoltage, 0.0) / Math.Max(MinimumCathodeVoltage, 1e-3));

        CathodeCurrent = demand * saturation;
    }

    /// <summary>
    /// Holds the reference error to a step at a time, the way the diode limits its junction
    /// voltage.
    /// <para>
    /// This part is a very high-gain nonlinearity: ten siemens through a sigmoid a couple of
    /// millivolts wide, which is the whole of what makes it a useful reference. Newton walking
    /// that without a limit overshoots the active region entirely, lands far out on a tail where
    /// the slope is nearly nothing, and is thrown back past the setpoint on the next step — so
    /// for certain setpoints it oscillates and never converges, while neighbouring ones a
    /// millivolt away solve immediately. It was a knife edge, and which side of it a circuit fell
    /// on had no relation to anything a person could see.
    /// </para>
    /// <para>
    /// Limiting the step keeps every iterate inside the region where there is a gradient to
    /// follow. As with the diode, an iteration that had to be limited is not a converged one, so
    /// the solver is told to keep going.
    /// </para>
    /// </summary>
    private double LimitError(double error)
    {
        _limitedThisIteration = false;

        if (double.IsNaN(_previousError))
        {
            _previousError = error;
            return error;
        }

        // Only the active region is worth policing. Out on the tails the sigmoid is clamped flat,
        // so a volt of movement out there changes nothing the solver can see, and holding it back
        // a few millivolts at a time would just burn iterations until the solver gave up — which
        // is what a plain step limit did. Both iterates are therefore measured by where they sit
        // *within* the band, and a step outside it costs nothing.
        var band = ErrorWidth * 6.0;
        var step = ErrorWidth * 2.0;

        var previous = Math.Clamp(_previousError, -band, band);
        var wanted = Math.Clamp(error, -band, band);
        var limited = Math.Clamp(wanted, previous - step, previous + step);

        // Held back: the iterate is the in-band value, so an approach from far away lands on the
        // edge of the region rather than being dragged in millivolt by millivolt from a volt out.
        if (Math.Abs(limited - wanted) > 1e-15)
        {
            _limitedThisIteration = true;
            _previousError = limited;

            return limited;
        }

        _previousError = error;

        return error;
    }

    /// <summary>
    /// A limited iteration has not reached the point the matrix asked for, so the solve cannot be
    /// declared finished on it.
    /// </summary>
    public override bool HasConverged(MnaSystem system, SimulationState state) =>
        !_limitedThisIteration;

    public override void ResetState()
    {
        _previousError = double.NaN;
        _limitedThisIteration = false;

        CathodeCurrent = 0;
        ReferenceVoltage = 0;
        CathodeVoltage = 0;
    }
}
