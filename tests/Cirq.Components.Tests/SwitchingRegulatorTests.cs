using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class SwitchingRegulatorTests
{
    /// <summary>A converter needs far finer steps than the default to place its edges.</summary>
    private static SimulationSettings Fine =>
        new() { TimeStep = 2e-7, MaxTimeStep = 2e-7 };

    private sealed record Buck(
        CircuitSimulator Sim, SwitchingRegulator Regulator, Inductor Coil, Resistor Load, Capacitor Timing);

    /// <summary>
    /// A step-down converter: the switch between the supply and the inductor, and a catch diode
    /// to carry the inductor's current while the switch is off.
    /// </summary>
    private static Buck StepDown(
        double input = 12.0, double topOhms = 3e3, double loadOhms = 100,
        // A millihenry rather than the 220 uH a real design would use, because at this
        // oscillator rate a smaller inductor lets the current ramp most of an amp inside one
        // on-time and the converter ends up regulating on its current limit instead of its
        // comparator. That is real behaviour, and it is the subject of its own test below.
        double inductance = 1e-3, double timing = 1e-9, double senseOhms = 1.0,
        SimulationSettings? settings = null)
    {
        var circuit = new Circuit();
        var vin = circuit.Add(new DcVoltageSource(input));
        var reg = circuit.Add(new SwitchingRegulator());
        var sense = circuit.Add(new Resistor(senseOhms));
        var coil = circuit.Add(new Inductor(inductance));
        var catchDiode = circuit.Add(new Diode(DiodeModel.D1N5817));
        var outCap = circuit.Add(new Capacitor(100e-6) { InitialVoltage = 0 });
        var top = circuit.Add(new Resistor(topOhms));
        var bottom = circuit.Add(new Resistor(1e3));
        var ct = circuit.Add(new Capacitor(timing) { InitialVoltage = 0 });
        var load = circuit.Add(new Resistor(loadOhms));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(vin.Negative, gnd.Pin);
        circuit.Connect(vin.Positive, reg.Supply);
        circuit.Connect(reg.Ground, gnd.Pin);
        circuit.Connect(reg.DriveCollector, reg.Supply);

        circuit.Connect(reg.Supply, sense.A);
        circuit.Connect(sense.B, reg.CurrentSense);
        circuit.Connect(reg.CurrentSense, reg.SwitchCollector);

        circuit.Connect(reg.SwitchEmitter, coil.A);
        circuit.Connect(catchDiode.Cathode, reg.SwitchEmitter);
        circuit.Connect(catchDiode.Anode, gnd.Pin);

        circuit.Connect(coil.B, outCap.A);
        circuit.Connect(outCap.B, gnd.Pin);
        circuit.Connect(coil.B, load.A);
        circuit.Connect(load.B, gnd.Pin);

        circuit.Connect(coil.B, top.A);
        circuit.Connect(top.B, reg.Feedback);
        circuit.Connect(reg.Feedback, bottom.A);
        circuit.Connect(bottom.B, gnd.Pin);

        circuit.Connect(reg.Timing, ct.A);
        circuit.Connect(ct.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, settings ?? Fine);
        sim.Reset();
        sim.SolveOperatingPoint();
        return new Buck(sim, reg, coil, load, ct);
    }

    /// <summary>
    /// The whole point of a switcher: twelve volts in, five out, and the difference is not turned
    /// into heat. A linear regulator doing the same job would be throwing away well over half of
    /// what goes in.
    /// </summary>
    [Fact]
    public void ItStepsTwelveVoltsDownToFive()
    {
        var buck = StepDown();
        buck.Sim.Run(25e-3);

        Assert.Equal(5.0, buck.Sim.NodeVoltage(buck.Load.A), 0.15);
        Assert.False(buck.Regulator.IsCurrentLimited);
    }

    /// <summary>
    /// The chip holds the feedback pin at its internal reference and nothing else, so the divider
    /// you hang on it is what decides the output: 1.25 × (1 + R1/R2).
    /// </summary>
    [Theory]
    [InlineData(1e3, 2.5)]
    [InlineData(3e3, 5.0)]
    [InlineData(5.4e3, 8.0)]
    public void TheDividerSetsTheOutputVoltage(double topOhms, double expected)
    {
        var buck = StepDown(topOhms: topOhms);
        buck.Sim.Run(25e-3);

        Assert.Equal(expected, buck.Sim.NodeVoltage(buck.Load.A), expected * 0.05);
        Assert.Equal(1.25, buck.Regulator.FeedbackVoltage, 0.02);
    }

    /// <summary>
    /// Same chip, rewired, steps up instead of down — which is why it is a controller and not a
    /// converter with a voltage on the label.
    /// </summary>
    [Fact]
    public void RewiredTheSameChipStepsUp()
    {
        var circuit = new Circuit();
        var vin = circuit.Add(new DcVoltageSource(5.0));
        var reg = circuit.Add(new SwitchingRegulator());
        var sense = circuit.Add(new Resistor(0.5));
        var coil = circuit.Add(new Inductor(220e-6));
        var rectifier = circuit.Add(new Diode(DiodeModel.D1N5817));
        var outCap = circuit.Add(new Capacitor(220e-6) { InitialVoltage = 0 });
        var top = circuit.Add(new Resistor(8.6e3));
        var bottom = circuit.Add(new Resistor(1e3));
        var ct = circuit.Add(new Capacitor(1e-9) { InitialVoltage = 0 });
        var load = circuit.Add(new Resistor(500));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(vin.Negative, gnd.Pin);
        circuit.Connect(vin.Positive, reg.Supply);
        circuit.Connect(reg.Ground, gnd.Pin);
        circuit.Connect(reg.DriveCollector, reg.Supply);

        circuit.Connect(reg.Supply, sense.A);
        circuit.Connect(sense.B, reg.CurrentSense);
        circuit.Connect(sense.B, coil.A);

        // The switch pulls the inductor's far end down; the diode lets it dump into the output.
        circuit.Connect(coil.B, reg.SwitchCollector);
        circuit.Connect(reg.SwitchEmitter, gnd.Pin);
        circuit.Connect(coil.B, rectifier.Anode);
        circuit.Connect(rectifier.Cathode, outCap.A);
        circuit.Connect(outCap.B, gnd.Pin);
        circuit.Connect(outCap.A, load.A);
        circuit.Connect(load.B, gnd.Pin);

        circuit.Connect(outCap.A, top.A);
        circuit.Connect(top.B, reg.Feedback);
        circuit.Connect(reg.Feedback, bottom.A);
        circuit.Connect(bottom.B, gnd.Pin);

        circuit.Connect(reg.Timing, ct.A);
        circuit.Connect(ct.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, Fine);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(30e-3);

        Assert.Equal(12.0, sim.NodeVoltage(outCap.A), 0.3);
        Assert.True(sim.NodeVoltage(outCap.A) > 5.0, "a boost has to end up above its input");
    }

    /// <summary>
    /// The frequency is the timing capacitor's, not a setting: the chip charges it at a fixed
    /// current and discharges it faster, and the rate falls out of that.
    /// </summary>
    [Theory]
    [InlineData(1e-9)]
    [InlineData(2.2e-9)]
    public void TheTimingCapacitorSetsTheFrequency(double timing)
    {
        var buck = StepDown(timing: timing);
        buck.Sim.Run(5e-3);

        // Count the ramp's crossings of the upper threshold.
        var reg = buck.Regulator;
        var crossings = 0;
        double first = 0, last = 0;
        var above = false;

        for (var i = 0; i < 40000; i++)
        {
            buck.Sim.Run(2e-7);

            var ramp = buck.Sim.NodeVoltage(buck.Timing.A);
            var nowAbove = ramp >= reg.RampUpper;

            if (nowAbove && !above)
            {
                if (crossings == 0) first = buck.Sim.Time; else last = buck.Sim.Time;
                crossings++;
            }

            above = nowAbove;
        }

        var measured = (crossings - 1) / (last - first);

        // t_on and t_off are the ramp's span divided by each current.
        var span = reg.RampUpper - reg.RampLower;
        var expected = 1.0 / (timing * span * ((1.0 / reg.ChargeCurrent) + (1.0 / reg.DischargeCurrent)));

        Assert.Equal(expected, measured, expected * 0.1);
    }

    /// <summary>
    /// It regulates by leaving cycles out rather than by narrowing the pulses, so a light load
    /// shows as a lower proportion of cycles used rather than as shorter ones.
    /// </summary>
    [Fact]
    public void ALighterLoadUsesFewerCycles()
    {
        var heavy = StepDown(loadOhms: 100);
        var light = StepDown(loadOhms: 1000);

        heavy.Sim.Run(30e-3);
        light.Sim.Run(30e-3);

        Assert.True(light.Regulator.DutyCycle < heavy.Regulator.DutyCycle * 0.6,
            $"light load used {light.Regulator.DutyCycle:0.00} of cycles against " +
            $"{heavy.Regulator.DutyCycle:0.00} for the heavy one");

        // Both still regulate.
        Assert.Equal(5.0, heavy.Sim.NodeVoltage(heavy.Load.A), 0.2);
        Assert.Equal(5.0, light.Sim.NodeVoltage(light.Load.A), 0.2);
    }

    /// <summary>
    /// The current limit is what stops the inductor current running away inside a cycle. Too
    /// large a sense resistor trips it early and the output cannot come up.
    /// </summary>
    [Fact]
    public void TooLargeASenseResistorTripsTheCurrentLimit()
    {
        var buck = StepDown(senseOhms: 47.0);
        buck.Sim.Run(20e-3);

        Assert.True(buck.Regulator.IsCurrentLimited);
        Assert.NotEmpty(buck.Regulator.Violations);
        Assert.Contains("current limit", string.Join(" ", buck.Regulator.Violations));

        // Held off, the output never reaches where the divider asked for.
        Assert.True(buck.Sim.NodeVoltage(buck.Load.A) < 4.0);
    }

    /// <summary>The switch is either on or off — that is what makes it a switcher.</summary>
    [Fact]
    public void TheSwitchIsOnlyEverFullyOnOrFullyOff()
    {
        var buck = StepDown();
        buck.Sim.Run(10e-3);

        var sawOn = false;
        var sawOff = false;

        for (var i = 0; i < 2000; i++)
        {
            buck.Sim.Run(2e-7);
            if (buck.Regulator.SwitchIsOn) sawOn = true; else sawOff = true;
        }

        Assert.True(sawOn && sawOff, "a switching regulator has to be doing both");
    }

    // ---- landing on the ramp ------------------------------------------------

    /// <summary>
    /// The oscillator's rate, measured off the ramp rather than asked of the part: count the times
    /// it crosses its upper threshold, and divide by how long that took.
    /// </summary>
    private static double MeasuredFrequency(Buck buck, double duration)
    {
        var reg = buck.Regulator;
        var above = false;
        var crossings = 0;
        double first = 0, last = 0;

        var end = buck.Sim.Time + duration;

        while (buck.Sim.Time < end)
        {
            buck.Sim.Step();

            var nowAbove = buck.Sim.NodeVoltage(buck.Timing.A) >= reg.RampUpper;

            if (nowAbove && !above)
            {
                if (crossings == 0) first = buck.Sim.Time; else last = buck.Sim.Time;
                crossings++;
            }

            above = nowAbove;
        }

        return crossings < 2 ? 0 : (crossings - 1) / (last - first);
    }

    /// <summary>What the oscillator's own arithmetic says its rate is.</summary>
    private static double ExpectedFrequency(SwitchingRegulator reg, double timing) =>
        1.0 / (timing * (reg.RampUpper - reg.RampLower) *
               ((1.0 / reg.ChargeCurrent) + (1.0 / reg.DischargeCurrent)));

    /// <summary>
    /// The discontinuity an error estimate cannot see, stated as a failure.
    /// <para>
    /// The timing ramp is a straight line between two thresholds, so there is no curvature for a
    /// step controller to notice and no reason for it to shorten anything. Take steps a fifth of the
    /// ramp long and the oscillator turns round wherever the step happened to land — past the
    /// threshold, every time, by an amount that is pure fiction — and the rate comes out well low.
    /// </para>
    /// </summary>
    [Fact]
    public void ACoarseFixedStepGetsTheRateWrong()
    {
        const double timing = 1e-9;

        var coarse = new SimulationSettings { TimeStep = 2e-6, MaxTimeStep = 2e-6 };
        var buck = StepDown(timing: timing, settings: coarse);

        var measured = MeasuredFrequency(buck, 2e-3);
        var expected = ExpectedFrequency(buck.Regulator, timing);

        // Not a little wrong: a fifth of a ramp of overshoot on every half cycle.
        Assert.True(measured < expected * 0.85,
            $"a coarse fixed step should have cost the rate: {measured / 1e3:0.0} kHz against " +
            $"{expected / 1e3:0.0} kHz expected");
    }

    /// <summary>
    /// And the same coarse ceiling, with the solver allowed to land on the crossing: the step is
    /// retaken to end exactly where the ramp met its threshold, the overshoot never happens, and the
    /// rate is the one the arithmetic says.
    /// </summary>
    [Fact]
    public void LandingOnTheThresholdGetsItRight()
    {
        const double timing = 1e-9;

        var adaptive = new SimulationSettings
        {
            AdaptiveTimeStep = true,
            TimeStep = 2e-6,
            MaxTimeStep = 2e-6,
            MinTimeStep = 1e-12,
        };

        var buck = StepDown(timing: timing, settings: adaptive);

        var measured = MeasuredFrequency(buck, 2e-3);
        var expected = ExpectedFrequency(buck.Regulator, timing);

        Assert.Equal(expected, measured, expected * 0.05);

        // Steps were thrown away to get there, which is what the accuracy cost.
        Assert.True(buck.Sim.RejectedSteps > 0, "no step was ever retaken");
    }
}
