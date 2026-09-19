using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class CrystalTests
{
    /// <summary>Drives a crystal at its own frequency and lets it ring.</summary>
    private static (CircuitSimulator Sim, Crystal Xtal) Ringing(CrystalModel model, double drive)
    {
        var circuit = new Circuit();
        var source = circuit.Add(new FunctionGenerator(Waveform.Square, drive, 2.0));
        var feed = circuit.Add(new Resistor(100e3));
        var xtal = circuit.Add(new Crystal(model));
        var load = circuit.Add(new Resistor(1e6));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Return, gnd.Pin);
        circuit.Connect(source.Output, feed.A);
        circuit.Connect(feed.B, xtal.A);
        circuit.Connect(xtal.B, gnd.Pin);
        circuit.Connect(xtal.A, load.A);
        circuit.Connect(load.B, gnd.Pin);

        // A resonance needs plenty of steps per cycle to land on the right frequency.
        var settings = new SimulationSettings { TimeStep = 1e-7, MaxTimeStep = 1e-7 };
        var sim = new CircuitSimulator(circuit, settings);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, xtal);
    }

    /// <summary>Counts the motional current's zero crossings to measure what it settled at.</summary>
    private static double MeasureFrequency(CircuitSimulator sim, Crystal xtal, double seconds)
    {
        var last = xtal.MotionalCurrent;
        int crossings = 0;
        double first = 0, latest = 0;

        var steps = (int)(seconds / 1e-7);
        for (var i = 0; i < steps; i++)
        {
            sim.Run(1e-7);
            var now = xtal.MotionalCurrent;

            if (last <= 0 && now > 0)
            {
                if (crossings == 0) first = sim.Time; else latest = sim.Time;
                crossings++;
            }

            last = now;
        }

        return crossings > 1 ? (crossings - 1) / (latest - first) : 0;
    }

    /// <summary>
    /// The whole point of the part: the frequency is the crystal's, set by the mechanical
    /// resonance it stands for, and not by anything around it.
    /// </summary>
    [Fact]
    public void ItResonatesAtItsRatedFrequency()
    {
        var (sim, xtal) = Ringing(CrystalModel.Watch32k, 32768.0);
        sim.Run(3e-3);

        var measured = MeasureFrequency(sim, xtal, 3e-3);

        Assert.Equal(32768.0, measured, 32768.0 * 0.001);
    }

    /// <summary>
    /// What a resonance is: the part is almost a short at its own frequency and almost an open a
    /// few percent either side. That sharpness is the whole reason to use a crystal instead of an
    /// RC network, and it is measured as how much current flows, not as what frequency it is at —
    /// a driven crystal carries the drive's frequency like anything else.
    /// </summary>
    [Fact]
    public void ItRespondsEnormouslyMoreAtItsOwnFrequency()
    {
        static double PeakCurrent(double drive)
        {
            var (sim, xtal) = Ringing(CrystalModel.Watch32k, drive);
            sim.Run(3e-3);

            var peak = 0.0;
            for (var i = 0; i < 20000; i++)
            {
                sim.Run(1e-7);
                peak = Math.Max(peak, Math.Abs(xtal.MotionalCurrent));
            }
            return peak;
        }

        var onResonance = PeakCurrent(32768.0);
        var below = PeakCurrent(32768.0 * 0.97);
        var above = PeakCurrent(32768.0 * 1.03);

        // Only a few times larger, rather than the hundreds the Q implies, and that is itself
        // the point: a resonance this sharp takes about Q cycles to settle, which is some eighty
        // milliseconds here. Inside a window this short the off-resonance figures are still
        // dominated by the crystal ringing at its own frequency from the initial kick, which is
        // exactly the behaviour that makes high-Q oscillators slow to start.
        Assert.True(onResonance > below * 2.5,
            $"{onResonance:g3} A on resonance against {below:g3} A below it is not a resonance");
        Assert.True(onResonance > above * 2.5,
            $"{onResonance:g3} A on resonance against {above:g3} A above it is not a resonance");
    }

    /// <summary>
    /// A huge inductance and a tiny capacitance is what a high Q looks like written as components.
    /// The inductance follows from the frequency and the motional capacitance, not the other way.
    /// </summary>
    [Fact]
    public void TheMotionalArmIsAHugeInductanceAndATinyCapacitance()
    {
        var xtal = new Crystal(CrystalModel.Watch32k);

        var expected = 1.0 / (Math.Pow(2 * Math.PI * 32768.0, 2) * CrystalModel.Watch32k.MotionalCapacitance);

        Assert.Equal(expected, xtal.MotionalInductance, expected * 1e-9);
        Assert.True(xtal.MotionalInductance > 1.0, "a crystal's motional inductance is in henries");
        Assert.True(xtal.QualityFactor > 1000, $"Q of {xtal.QualityFactor:0} is not crystal-like");
    }

    /// <summary>Each stocked part lands on its own number.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void EveryStockedCrystalResonatesWhereItSays(int index)
    {
        var model = CrystalModel.Library[index];
        var (sim, xtal) = Ringing(model, model.Frequency);

        sim.Run(2e-3);
        var measured = MeasureFrequency(sim, xtal, 2e-3);

        Assert.Equal(model.Frequency, measured, model.Frequency * 0.01);
    }
}

public class MicrophoneTests
{
    private static (CircuitSimulator Sim, Microphone Mic, Resistor Bias) Rig(double bias)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var resistor = circuit.Add(new Resistor(bias));
        var mic = circuit.Add(new Microphone());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, resistor.A);
        circuit.Connect(resistor.B, mic.A);
        circuit.Connect(mic.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, mic, resistor);
    }

    /// <summary>
    /// The bias resistor is what turns the capsule's current into a signal. With a sensible one
    /// the capsule sits about half way down the rail, which is where it has room to swing.
    /// </summary>
    [Fact]
    public void WithABiasResistorItSitsPartWayDownTheRail()
    {
        var (sim, mic, bias) = Rig(4.7e3);

        var atCapsule = sim.NodeVoltage(bias.B);

        Assert.True(mic.IsBiased);
        Assert.InRange(atCapsule, 1.5, 4.0);
        Assert.Empty(mic.Violations);
    }

    /// <summary>Sound modulates the current, which the bias resistor turns into a voltage swing.</summary>
    [Fact]
    public void SoundSwingsTheOutputAndSilenceDoesNot()
    {
        var (loud, micL, biasL) = Rig(4.7e3);
        var (quiet, micQ, biasQ) = Rig(4.7e3);
        micQ.IsHearingSound = false;

        double SwingOf(CircuitSimulator sim, Resistor bias)
        {
            double min = 99, max = -99;
            for (var i = 0; i < 4000; i++)
            {
                sim.Run(1e-6);
                var v = sim.NodeVoltage(bias.B);
                min = Math.Min(min, v);
                max = Math.Max(max, v);
            }
            return max - min;
        }

        var withSound = SwingOf(loud, biasL);
        var without = SwingOf(quiet, biasQ);

        Assert.True(withSound > 0.5, $"only {withSound:0.000} V of swing from a sounding capsule");
        Assert.True(without < 1e-3, $"{without:0.000} V of swing from a silent one");
        Assert.True(micL.IsHearingSound && !micQ.IsHearingSound);
    }

    /// <summary>
    /// Too large a bias resistor starves the capsule: there is not enough voltage left across it
    /// to run the JFET inside, and it stops working rather than just working quietly.
    /// </summary>
    [Fact]
    public void TooLargeABiasResistorStarvesIt()
    {
        var (_, mic, _) = Rig(1e6);

        Assert.False(mic.IsBiased);
        Assert.Contains("starved", string.Join(" ", mic.Violations));
    }

    /// <summary>
    /// The opposite mistake is not something the capsule can see. Wired almost straight to the
    /// rail it is perfectly happy — it is the circuit that has no resistance for the signal to
    /// develop across, which is why the output never moves.
    /// </summary>
    [Fact]
    public void WiredStraightToTheRailTheCapsuleItselfIsFine()
    {
        var (_, mic, _) = Rig(1.0);

        Assert.True(mic.IsBiased);
        Assert.Empty(mic.Violations);
    }
}

public class ServoTests
{
    private static (CircuitSimulator Sim, Servo Unit, DcVoltageSource Signal) Rig()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var signal = circuit.Add(new DcVoltageSource(0.0));
        var servo = circuit.Add(new Servo());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, servo.Supply);
        circuit.Connect(servo.Ground, gnd.Pin);
        circuit.Connect(signal.Negative, gnd.Pin);
        circuit.Connect(signal.Positive, servo.Signal);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, servo, signal);
    }

    /// <summary>Sends pulses of a given width for long enough for the shaft to get there.</summary>
    private static void Drive(CircuitSimulator sim, DcVoltageSource signal, double width, int pulses)
    {
        for (var i = 0; i < pulses; i++)
        {
            signal.Voltage = 5.0;
            sim.Run(width);
            signal.Voltage = 0.0;
            sim.Run(20e-3 - width);
        }
    }

    /// <summary>
    /// The defining behaviour: the angle comes from how long the pulse is, not from its level or
    /// its duty cycle.
    /// </summary>
    [Theory]
    [InlineData(1.0e-3, -90.0)]
    [InlineData(1.5e-3, 0.0)]
    [InlineData(2.0e-3, 90.0)]
    public void ThePulseWidthSetsTheAngle(double width, double expected)
    {
        var (sim, servo, signal) = Rig();

        Drive(sim, signal, width, 40);

        Assert.True(servo.IsDriven);
        Assert.Equal(expected, servo.CommandedAngle, 1.0);
        Assert.Equal(expected, servo.Angle, 2.0);
    }

    /// <summary>It moves at a finite speed, so a commanded jump takes time to arrive.</summary>
    [Fact]
    public void ItTakesTimeToGetThere()
    {
        var (sim, servo, signal) = Rig();

        Drive(sim, signal, 1.0e-3, 30);
        Assert.Equal(-90.0, servo.Angle, 2.0);

        // One pulse asking for the far end is not enough to get there.
        Drive(sim, signal, 2.0e-3, 1);
        Assert.Equal(90.0, servo.CommandedAngle, 1.0);
        Assert.True(servo.Angle < 0, $"it should still be travelling, not already at {servo.Angle:0}");

        Drive(sim, signal, 2.0e-3, 40);
        Assert.Equal(90.0, servo.Angle, 2.0);
    }

    /// <summary>Stop sending pulses and it goes limp rather than holding or snapping back.</summary>
    [Fact]
    public void ItGivesUpWhenThePulsesStop()
    {
        var (sim, servo, signal) = Rig();

        Drive(sim, signal, 1.5e-3, 20);
        Assert.True(servo.IsDriven);

        sim.Run(200e-3);

        Assert.False(servo.IsDriven);
        Assert.Contains("no signal", servo.ValueLabel);
    }

    /// <summary>A pulse outside the range is a servo driving against its end stop.</summary>
    [Fact]
    public void AnOutOfRangePulseIsReported()
    {
        var (sim, servo, signal) = Rig();

        Drive(sim, signal, 2.6e-3, 10);

        Assert.NotEmpty(servo.Violations);
        Assert.Contains("end stop", string.Join(" ", servo.Violations));
    }
}

public class StepperMotorTests
{
    /// <summary>A stepper driven straight from four logic toggles through a ULN2003.</summary>
    private static (CircuitSimulator Sim, StepperMotor Motor, DcVoltageSource[] Drive) Rig()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(12.0));
        var driver = circuit.Add(new Uln2003());
        var motor = circuit.Add(new StepperMotor());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(driver.Gnd, gnd.Pin);
        circuit.Connect(driver.Common, rail.Positive);
        circuit.Connect(motor.Common, rail.Positive);

        var drive = new DcVoltageSource[4];
        for (var i = 0; i < 4; i++)
        {
            circuit.Connect(motor.Coils[i], driver.Outputs[i]);

            drive[i] = circuit.Add(new DcVoltageSource(0.0));
            circuit.Connect(drive[i].Negative, gnd.Pin);
            circuit.Connect(drive[i].Positive, driver.Inputs[i]);
        }

        for (var i = 4; i < 7; i++) circuit.Connect(driver.Inputs[i], gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, motor, drive);
    }

    /// <summary>Energises one pattern and holds it long enough for the winding current to build.</summary>
    private static void Energise(CircuitSimulator sim, DcVoltageSource[] drive, int pattern, double hold)
    {
        for (var i = 0; i < 4; i++) drive[i].Voltage = (pattern & (1 << i)) != 0 ? 5.0 : 0.0;
        sim.Run(hold);
    }

    /// <summary>Wave drive: one coil at a time, in order, and the rotor follows.</summary>
    [Fact]
    public void SteppingTheCoilsInOrderTurnsIt()
    {
        var (sim, motor, drive) = Rig();

        int[] wave = [0b0001, 0b0010, 0b0100, 0b1000];
        for (var turn = 0; turn < 2; turn++)
            foreach (var pattern in wave)
                Energise(sim, drive, pattern, 5e-3);

        Assert.True(motor.IsEnergised);
        Assert.True(motor.Angle > 0, $"it went {motor.Angle:0.###}° — the wrong way, or not at all");

        // Seven steps, not eight. The first energisation does not step: it pulls the rotor into
        // line with a coil from wherever it happened to be sitting, which is why a stepper's
        // position is only ever relative to where it was switched on.
        Assert.Equal(7.0 * 360.0 / 2048.0, motor.Angle, 0.02);
    }

    /// <summary>Reverse the order and it goes the other way. There is nothing else to it.</summary>
    [Fact]
    public void ReversingTheOrderReversesTheRotation()
    {
        var (sim, motor, drive) = Rig();

        int[] backwards = [0b1000, 0b0100, 0b0010, 0b0001];
        foreach (var pattern in backwards) Energise(sim, drive, pattern, 5e-3);

        Assert.True(motor.Angle < 0, $"it went {motor.Angle:0.###}° rather than backwards");
    }

    /// <summary>
    /// Half stepping falls out of the vector sum rather than being selected: two adjacent coils
    /// together pull the rotor to the point between them.
    /// </summary>
    [Fact]
    public void TwoAdjacentCoilsHoldItBetweenTheirSteps()
    {
        var (sim, motor, drive) = Rig();

        Energise(sim, drive, 0b0001, 5e-3);
        var single = motor.Angle;

        Energise(sim, drive, 0b0011, 5e-3);
        var both = motor.Angle;

        Energise(sim, drive, 0b0010, 5e-3);
        var next = motor.Angle;

        Assert.True(both > single && both < next,
            $"{single:0.####} then {both:0.####} then {next:0.####} is not a half step in between");
    }

    /// <summary>
    /// The first coil energised aligns the rotor rather than advancing it. A stepper knows only
    /// where it has been driven since, never where it actually is.
    /// </summary>
    [Fact]
    public void TheFirstEnergisationAlignsRatherThanSteps()
    {
        var (sim, motor, drive) = Rig();

        Energise(sim, drive, 0b0001, 5e-3);

        Assert.True(motor.IsEnergised);
        Assert.Equal(0.0, motor.Angle, 1e-9);
    }

    /// <summary>With nothing energised there is nothing holding the rotor.</summary>
    [Fact]
    public void WithNoCoilsEnergisedItIsNotHolding()
    {
        var (sim, motor, drive) = Rig();

        Energise(sim, drive, 0b0000, 5e-3);

        Assert.False(motor.IsEnergised);
        Assert.Equal("unpowered", motor.ValueLabel);
    }

    /// <summary>
    /// Driven faster than the rotor can follow, a stepper does not lag gracefully — it stops, and
    /// every step after that is lost. With no feedback there is no way for it to notice.
    /// </summary>
    [Fact]
    public void SteppingTooFastLosesSteps()
    {
        var (sim, motor, drive) = Rig();
        motor.MaximumStepRate = 20.0;             // a deliberately feeble motor

        int[] wave = [0b0001, 0b0010, 0b0100, 0b1000];
        for (var turn = 0; turn < 4; turn++)
            foreach (var pattern in wave)
                Energise(sim, drive, pattern, 2e-3);   // 500 steps/s at a 20 step/s motor

        Assert.True(motor.IsSlipping);
        Assert.Contains("lost", string.Join(" ", motor.Violations));

        // It has fallen well short of the sixteen steps it was asked for.
        Assert.True(motor.Angle < 16.0 * 360.0 / 2048.0 * 0.5,
            $"it kept up with {motor.Angle:0.###}°, which it should not have");
    }
}
