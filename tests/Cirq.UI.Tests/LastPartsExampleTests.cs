using Cirq.Components.Bridges;
using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The batch that finished the job: every part in the palette now has an example, and every one
/// of these is asserted against the thing it is there to show.
/// </summary>
public class LastPartsExampleTests
{
    private static MainWindowViewModel Load(string name)
    {
        var vm = new MainWindowViewModel();
        vm.Circuit.Clear();
        vm.Scope.ClearProbes();
        Examples.All.Single(e => e.Name == name).Build(vm);
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        return vm;
    }

    /// <summary>
    /// A light switch that works the way round its name says, which is worth asserting because
    /// getting a comparator's two inputs the wrong way up gives a working circuit that is wrong.
    /// </summary>
    [Fact]
    public void TheNightLightComesOnInTheDarkAndNotInTheLight()
    {
        Assert.True(LampBrightness(0.5) > 0.3, "the lamp was not lit in the dark");
        Assert.Equal(0.0, LampBrightness(500.0), 0.01);
        Assert.Equal(0.0, LampBrightness(20_000.0), 0.01);
    }

    /// <summary>
    /// And the threshold comes from a TL431 rather than another divider, so it stays where it is
    /// when the supply moves — which is the entire reason to spend a part on it.
    /// </summary>
    [Fact]
    public void AndItsThresholdDoesNotMoveWithTheSupply()
    {
        var atFive = ReferenceOn(5.0);
        var atFour = ReferenceOn(4.0);

        Assert.Equal(2.495, atFive, 0.05);

        // A divider would have fallen by a fifth along with the rail. This does not move at all.
        Assert.Equal(atFive, atFour, 0.02);
    }

    /// <summary>And the switch overrides it, because every real one has that.</summary>
    [Fact]
    public void AndTheOverrideSwitchLightsItRegardless()
    {
        using var vm = Load("Night Light");

        var sensor = vm.Circuit.Components.OfType<LightDependentResistor>().Single();
        var mode = vm.Circuit.Components.OfType<SpdtSwitch>().Single();

        sensor.Illuminance = 20_000.0;
        mode.IsThrownToB = true;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        vm.Simulation.Simulator!.Run(20e-3);

        Assert.True(vm.Circuit.Components.OfType<Led>().Single().Brightness > 0.3,
            "the manual position did not light it in broad daylight");
    }

    private static double LampBrightness(double lux)
    {
        using var vm = Load("Night Light");

        vm.Circuit.Components.OfType<LightDependentResistor>().Single().Illuminance = lux;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        vm.Simulation.Simulator!.Run(20e-3);

        return vm.Circuit.Components.OfType<Led>().Single().Brightness;
    }

    private static double ReferenceOn(double rail)
    {
        using var vm = Load("Night Light");

        vm.Circuit.Components.OfType<DcVoltageSource>().Single().Voltage = rail;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var reference = vm.Circuit.Components.OfType<ShuntReference>().Single();
        vm.Simulation.Simulator!.Run(20e-3);

        Assert.True(reference.IsRegulating);
        return reference.CathodeVoltage;
    }

    /// <summary>
    /// Forty-one microvolts a degree, which is what the whole circuit is built to deal with, and
    /// the gain that turns it into something a converter could read.
    /// </summary>
    [Fact]
    public void TheThermocoupleGivesMicrovoltsAndTheAmplifierGivesVolts()
    {
        using var vm = Load("Thermocouple");

        var probe = vm.Circuit.Components.OfType<Thermocouple>().Single();
        var amplifier = vm.Circuit.Components.OfType<Ina126>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(3e-3);

        // K-type against a cold junction at 25 C: 275 degrees of difference at 41 uV each.
        Assert.Equal(300.0, probe.Temperature, 1e-9);
        Assert.Equal(275 * 41e-6, probe.Emf, 0.1e-3);

        // Which the amplifier lifts to something usable, on top of its reference.
        var reference = sim.NodeVoltage(amplifier.Reference);
        Assert.Equal(reference + (100.0 * probe.Emf), sim.NodeVoltage(amplifier.Output), 0.05);
        Assert.False(amplifier.IsClipping);
    }

    /// <summary>
    /// The thing every thermocouple circuit has to deal with: it measures the difference between
    /// its two ends, so the reading is wrong by whatever the cold end is at.
    /// </summary>
    [Fact]
    public void AndItMeasuresADifferenceRatherThanATemperature()
    {
        using var vm = Load("Thermocouple");

        var probe = vm.Circuit.Components.OfType<Thermocouple>().Single();

        // Read straight off the voltage, the reading is short by exactly the cold junction.
        Assert.Equal(probe.Temperature - probe.ColdJunctionTemperature,
            probe.UncompensatedTemperature, 0.5);

        // Warm the cold end and the reading falls, without the hot end moving at all.
        probe.ColdJunctionTemperature = 50.0;

        Assert.Equal(250.0, probe.UncompensatedTemperature, 0.5);
        Assert.Equal(300.0, probe.Temperature, 1e-9);
    }

    /// <summary>
    /// The angle is in the width of the pulse, and the three figures are the three on every
    /// servo's datasheet.
    /// </summary>
    [Theory]
    [InlineData(0.05, 1.0e-3, -90.0)]
    [InlineData(0.075, 1.5e-3, 0.0)]
    [InlineData(0.10, 2.0e-3, 90.0)]
    public void TheServoFollowsThePulseWidthAndNothingElse(double duty, double width, double angle)
    {
        using var vm = Load("Servo Sweep");

        vm.Circuit.Components.OfType<FunctionGenerator>().Single().DutyCycle = duty;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var servo = vm.Circuit.Components.OfType<Servo>().Single();

        // Long enough for it to get there: it slews rather than jumping.
        vm.Simulation.Simulator!.Run(600e-3);

        Assert.True(servo.IsDriven);
        Assert.Equal(width, servo.LastPulseWidth, 50e-6);
        Assert.Equal(angle, servo.Angle, 2.0);
        Assert.Empty(servo.Violations);
    }

    /// <summary>
    /// A Hall switch counting a magnet, and counting it once each time — which is the difference
    /// between it and the reed switch in the same palette.
    /// </summary>
    [Fact]
    public void TheHallCounterCountsEachPassExactlyOnce()
    {
        using var vm = Load("Hall Counter");

        var sensor = vm.Circuit.Components.OfType<HallSensor>().Single();
        var counter = vm.Circuit.Components.OfType<Ic4040>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(5e-3);

        Assert.False(sensor.HasNoPullUp);
        Assert.True(sim.NodeVoltage(sensor.Output) > 4.0, "the output is not being pulled up at rest");

        // Four passes of a magnet. The counter's first stage toggles on each edge it is given,
        // so a clean count is one toggle per pass and anything more is a bounce being counted.
        var toggles = 0;
        var wasHigh = sim.NodeVoltage(counter.Outputs[0]) > 2.5;

        for (var pass = 0; pass < 4; pass++)
        {
            sensor.FluxDensity = 35.0;
            sim.Run(5e-3);

            Assert.True(sensor.IsDetecting);
            Assert.True(sim.NodeVoltage(sensor.Output) < 1.0, "it did not pull down with a magnet on it");

            sensor.FluxDensity = 0.0;
            sim.Run(5e-3);

            Assert.False(sensor.IsDetecting);

            var high = sim.NodeVoltage(counter.Outputs[0]) > 2.5;
            if (high != wasHigh) toggles++;
            wasHigh = high;
        }

        // Exactly one per pass. A mechanical contact in the same place would give a burst of
        // edges on each approach and count several — which is the reed switch example, and the
        // reason a Hall switch is worth the supply current it costs.
        Assert.Equal(4, toggles);
    }

    /// <summary>
    /// A 4066 doing what it is for: picking one source out of four, with eighty ohms of switch in
    /// the way that a high-impedance load does not notice.
    /// </summary>
    [Fact]
    public void TheAnalogSwitchPassesTheSelectedSourceAndNothingElse()
    {
        var (departure, _) = Selected(bothClosed: false);

        // Three millivolts away from the source it selected, across a two-volt swing.
        Assert.True(departure < 0.05,
            $"the selected source arrived {departure:0.000} V away from itself");
    }

    /// <summary>
    /// And the mistake it allows, which a 4051 does not: close two and the two sources are wired
    /// together, so what comes out is neither of them.
    /// </summary>
    [Fact]
    public void AndClosingTwoWiresTheSourcesTogether()
    {
        var (alone, _) = Selected(bothClosed: false);
        var (fighting, closed) = Selected(bothClosed: true);

        Assert.Equal(2, closed);
        Assert.True(fighting > 0.5,
            $"with two closed the output was still within {fighting:0.000} V of one source");

        Assert.True(fighting > alone * 100);
    }

    private static (double Departure, int Closed) Selected(bool bothClosed)
    {
        using var vm = Load("Analog Switch");

        vm.Circuit.Components.OfType<DipSwitch>().Single().Position2 = bothClosed;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var switches = vm.Circuit.Components.OfType<Ic4066>().Single();
        var slowest = vm.Circuit.Components.OfType<FunctionGenerator>().OrderBy(g => g.Frequency).First();
        var sim = vm.Simulation.Simulator!;

        var common = switches.Switch(0).B;
        sim.Run(5e-3);

        var departure = 0.0;

        while (sim.Time < 9e-3)
        {
            sim.Step();

            departure = Math.Max(departure,
                Math.Abs(sim.NodeVoltage(common) - sim.NodeVoltage(slowest.Output)));
        }

        var closed = Enumerable.Range(0, 4).Count(switches.IsClosed);
        return (departure, closed);
    }

    /// <summary>
    /// The ADC bridge's two thresholds, on the signal that needs them: a slow ramp with noise on
    /// it, which is the one case where a single threshold falls apart.
    /// </summary>
    [Fact]
    public void TheAnalogBoundaryNeedsTwoThresholdsOnASlowNoisyRamp()
    {
        var withHysteresis = Transitions(hysteresis: true);
        var without = Transitions(hysteresis: false);

        // A 200 Hz triangle over ten milliseconds crosses the threshold four times, so four or
        // five transitions is the right answer and anything more is chatter.
        Assert.InRange(withHysteresis, 3, 6);
        Assert.True(without > withHysteresis * 2,
            $"{without} transitions without hysteresis against {withHysteresis} with it");
    }

    /// <summary>And the DAC bridge puts it back as analog, at whatever height it is asked for.</summary>
    [Fact]
    public void AndTheDacBridgeRebuildsItAtTheLevelItIsGiven()
    {
        using var vm = Load("Analog and Logic");

        var dac = vm.Circuit.Components.OfType<DacBridge>().Single();
        var sim = vm.Simulation.Simulator!;

        var low = double.MaxValue;
        var high = double.MinValue;

        sim.Run(2e-3);

        while (sim.Time < 12e-3)
        {
            sim.Step();

            var v = sim.NodeVoltage(dac.AnalogOut);
            low = Math.Min(low, v);
            high = Math.Max(high, v);
        }

        // Its own two levels, not the ramp's: the signal has been through logic and come back.
        Assert.Equal(dac.AnalogHigh, high, 0.1);
        Assert.Equal(dac.AnalogLow, low, 0.1);
    }

    private static int Transitions(bool hysteresis)
    {
        using var vm = Load("Analog and Logic");

        var bridge = vm.Circuit.Components.OfType<AdcBridge>().Single();
        bridge.UseHysteresis = hysteresis;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var sim = vm.Simulation.Simulator!;
        sim.Run(2e-3);

        var transitions = 0;
        var wasHigh = false;

        while (sim.Time < 12e-3)
        {
            sim.Step();

            var high = sim.NodeVoltage(bridge.DigitalOut) > 2.5;
            if (high != wasHigh) transitions++;
            wasHigh = high;
        }

        return transitions;
    }
}
