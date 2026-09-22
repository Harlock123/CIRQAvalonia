using Cirq.Components.Buses;
using Cirq.Components.Ics;
using Cirq.Components.Sources;
using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Engine.Simulation;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The shipped examples run for real, each asserted against the thing it is there to show. A
/// circuit that merely compiles and advances in time is not an example of anything.
/// </summary>
public class NewExampleTests
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
    /// Unterminated, the far end doubles the incident wave and the near end sits at half the
    /// driver's voltage until the reflection gets home a whole delay later.
    /// </summary>
    [Fact]
    public void TheReflectionsExampleShowsTheFarEndDoubling()
    {
        using var vm = Load("Reflections");

        var line = vm.Circuit.Components.OfType<TransmissionLine>().Single();
        var sim = vm.Simulation.Simulator!;

        // Part way down the line: the wave has been launched and has not arrived.
        while (sim.Time < line.Delay * 0.6) sim.Step();

        Assert.Equal(5.0, sim.NodeVoltage(line.NearPlus), 0.3);
        Assert.Equal(0.0, sim.NodeVoltage(line.FarPlus), 0.3);

        // Past the delay: all of it has come back, so the far end is at twice what arrived.
        while (sim.Time < line.Delay * 1.5) sim.Step();

        Assert.Equal(10.0, sim.NodeVoltage(line.FarPlus), 0.3);
        Assert.Equal(5.0, sim.NodeVoltage(line.NearPlus), 0.3);
    }

    /// <summary>And closing the switch terminates it, which is what the control is there for.</summary>
    [Fact]
    public void ClosingTheSwitchTerminatesTheLine()
    {
        using var vm = Load("Reflections");

        var line = vm.Circuit.Components.OfType<TransmissionLine>().Single();
        var terminate = vm.Circuit.Components.OfType<ToggleSwitch>().Single();

        terminate.IsClosed = true;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var sim = vm.Simulation.Simulator!;
        while (sim.Time < line.Delay * 1.5) sim.Step();

        // Matched: the wave is absorbed, so the far end gets what was sent and no more.
        Assert.Equal(5.0, sim.NodeVoltage(line.FarPlus), 0.3);
    }

    /// <summary>
    /// The bead attenuates the noise on the rail. Measured as RMS either side of it, because that
    /// is what "quieter" means for something with no particular shape.
    /// </summary>
    [Fact]
    public void TheFerriteBeadExampleQuietensTheRail()
    {
        using var vm = Load("Ferrite Bead");

        var bead = vm.Circuit.Components.OfType<FerriteBead>().Single();
        var sim = vm.Simulation.Simulator!;

        // Past the power-on transient first: the examples start with everything discharged, so
        // the first microsecond is the rail coming up rather than the noise being filtered.
        sim.Run(2e-6);

        List<double> before = [];
        List<double> after = [];

        for (var i = 0; i < 4000; i++)
        {
            sim.Step();
            before.Add(sim.NodeVoltage(bead.A));
            after.Add(sim.NodeVoltage(bead.B));
        }

        static double Ripple(List<double> v)
        {
            var mean = v.Average();
            return Math.Sqrt(v.Sum(x => (x - mean) * (x - mean)) / v.Count);
        }

        Assert.True(Ripple(after) < Ripple(before) / 2.0,
            $"the bead should cut the noise: {Ripple(after) * 1e3:0.0} mV after, {Ripple(before) * 1e3:0.0} mV before");
    }

    /// <summary>
    /// The driven gate gets to the driver's twelve volt supply; the one hanging off the logic pin
    /// cannot go above the pin's own rail however long it is given.
    /// </summary>
    [Fact]
    public void TheGateDriverExampleShowsBothHalvesOfTheProblem()
    {
        using var vm = Load("Gate Driver");

        var driver = vm.Circuit.Components.OfType<GateDriver>().Single();
        // The driven one sits above the direct one on the canvas.
        var fets = vm.Circuit.Components.OfType<Mosfet>().OrderBy(f => f.Y).ToList();
        var sim = vm.Simulation.Simulator!;

        var driven = 0.0;
        var direct = 0.0;

        for (var i = 0; i < 20000; i++)
        {
            sim.Step();
            driven = Math.Max(driven, sim.NodeVoltage(driver.Output));
            direct = Math.Max(direct, sim.NodeVoltage(fets[1].Gate));
        }

        Assert.True(driven > 11.0, $"the driven gate should reach the 12 V rail, not {driven:0.0} V");
        Assert.True(direct < 4.0, $"a 3.3 V pin cannot exceed its own rail, yet the gate reached {direct:0.0} V");
    }

    /// <summary>
    /// The whole exchange: the sensor answers the reset, converts, and the master reads a
    /// temperature back that is the one the sensor was set to.
    /// </summary>
    [Fact]
    public void TheOneWireExampleReadsARealTemperature()
    {
        using var vm = Load("1-Wire Thermometer");

        var master = vm.Circuit.Components.OfType<OneWireMaster>().Single();
        var sensor = vm.Circuit.Components.OfType<Ds18b20>().Single();

        vm.Simulation.Simulator!.Run(0.2);

        Assert.True(master.PresenceDetected, "nothing answered the reset");
        Assert.True(master.IsFinished, "the sequence did not finish inside 200 ms");
        Assert.Equal(9, master.ReceivedBytes.Count);

        var reading = (short)(master.ReceivedBytes[0] | (master.ReceivedBytes[1] << 8)) * 0.0625;

        Assert.Equal(sensor.Temperature, reading, 0.5);
        Assert.Equal(master.ReceivedBytes[8], Ds18b20.Crc8(master.ReceivedBytes, 8));
    }

    /// <summary>
    /// The loop: the DAC's code becomes a voltage, the follower delivers it into a load the DAC
    /// could not have driven, and the ADC reads back the number that went out.
    /// </summary>
    [Fact]
    public void TheDacExampleComesBackRoundToTheSameNumber()
    {
        using var vm = Load("DAC and ADC");

        var dac = vm.Circuit.Components.OfType<Mcp4725>().Single();
        var adc = vm.Circuit.Components.OfType<Ads1115>().Single();

        vm.Simulation.Simulator!.Run(0.5);

        // Half of 4095 on a 5 V supply.
        Assert.Equal(2.5, dac.AnalogOutput, 0.02);
        Assert.Equal(2.5, adc.LastVoltage, 0.05);
    }

    /// <summary>And moving the code moves the voltage, which is what the slider is for.</summary>
    [Fact]
    public void MovingTheCodeMovesTheVoltage()
    {
        using var vm = Load("DAC and ADC");

        var dac = vm.Circuit.Components.OfType<Mcp4725>().Single();
        var adc = vm.Circuit.Components.OfType<Ads1115>().Single();

        dac.Code = 1024;
        vm.Simulation.Simulator!.Run(0.5);

        Assert.Equal(1.25, adc.LastVoltage, 0.05);
    }

    /// <summary>
    /// One magnet brought up, and the counter moves more than one place — which is the entire
    /// point of the example and the reason debouncing exists.
    /// </summary>
    [Fact]
    public void TheReedSwitchExampleCountsOneMagnetSeveralTimes()
    {
        using var vm = Load("Reed Switch Bounce");

        var reed = vm.Circuit.Components.OfType<ReedSwitch>().Single();
        var counter = vm.Circuit.Components.OfType<Ic7490>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(1e-3);

        // Bring the magnet up once and let the contacts settle.
        reed.FluxDensity = 12.0;
        sim.Run(5e-3);

        Assert.True(counter.Count > 1,
            $"one magnet should have been counted several times, not {counter.Count}");
    }

    /// <summary>
    /// Movement lights the lamp, and it stays lit well after the movement stops — which is the
    /// behaviour that makes a PIR confusing to read from code.
    /// </summary>
    [Fact]
    public void TheMotionLightHoldsAfterTheMovementStops()
    {
        using var vm = Load("Motion Light");

        var pir = vm.Circuit.Components.OfType<PirSensor>().Single();
        var lamp = vm.Circuit.Components.OfType<Led>().Single();
        var sim = vm.Simulation.Simulator!;

        // Past the warm-up first, or it ignores everything.
        sim.Run(1.5);
        Assert.False(pir.IsTriggered);

        pir.Movement = true;
        sim.Run(0.2);
        pir.Movement = false;

        sim.Run(0.5);
        Assert.True(pir.IsTriggered, "the lamp should be on");
        Assert.True(lamp.Brightness > 0.2, $"and lit, not {lamp.Brightness:0.00}");

        // Well past the four second hold.
        sim.Run(5.0);
        Assert.False(pir.IsTriggered, "and out again once the hold ran out");
    }

    /// <summary>The loop closes and the VCO ends up on the signal, which is the whole example.</summary>
    [Fact]
    public void ThePllExampleLocksOntoItsSignal()
    {
        using var vm = Load("Phase-Locked Loop");

        var pll = vm.Circuit.Components.OfType<Ic4046>().Single();
        var signal = vm.Circuit.Components.OfType<FunctionGenerator>().Single();

        vm.Simulation.Simulator!.Run(20e-3);

        Assert.Equal(signal.Frequency, pll.VcoFrequency, signal.Frequency * 0.05);
        Assert.True(pll.IsLocked, "and it should say so");
    }

    /// <summary>
    /// The envelope follows the tone. Measured at the modulation's peak and its trough, because
    /// that difference is what "modulated" means.
    /// </summary>
    [Fact]
    public void TheModulationExampleHasAnEnvelope()
    {
        using var vm = Load("Amplitude Modulation");

        var multiplier = vm.Circuit.Components.OfType<AnalogMultiplier>().Single();
        var sim = vm.Simulation.Simulator!;

        double PeakBetween(double from, double until)
        {
            while (sim.Time < from) sim.Step();

            var highest = 0.0;
            while (sim.Time < until)
            {
                sim.Step();
                highest = Math.Max(highest, Math.Abs(sim.NodeVoltage(multiplier.Output)));
            }

            return highest;
        }

        // A 2 kHz tone peaks at 125 µs and troughs at 375 µs.
        var atPeak = PeakBetween(110e-6, 140e-6);
        var atTrough = PeakBetween(360e-6, 390e-6);

        Assert.True(atPeak > atTrough * 2.0,
            $"the envelope should follow the tone: {atPeak:0.00} V against {atTrough:0.00} V");
    }

    /// <summary>
    /// The sensor reads the load, and switching the second load in makes the reading change —
    /// which is the thing to watch while it runs.
    /// </summary>
    [Fact]
    public void TheCurrentSensingExampleFollowsTheLoad()
    {
        using var vm = Load("Current Sensing");

        var sensor = vm.Circuit.Components.OfType<Ina219>().Single();
        var extra = vm.Circuit.Components.OfType<ToggleSwitch>().Single();

        vm.Simulation.Simulator!.Run(5e-3);

        // 12 V across 100 Ω, less the shunt's own tenth of an ohm.
        Assert.Equal(0.1199, sensor.Current, 0.002);
        Assert.False(sensor.IsOutOfCommonModeRange);

        extra.IsClosed = true;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        vm.Simulation.Simulator!.Run(5e-3);

        // 100 Ω and 47 Ω in parallel is 32 Ω, so a good deal more.
        Assert.True(sensor.Current > 0.35, $"switching the second load in should show, not {sensor.Current:0.000} A");
    }

    /// <summary>
    /// The whole supply end to end, at five settings of the knob. A 7805 holds its reference above
    /// its own GND pin, so lifting that pin through R1 and the potentiometer lifts the output with
    /// it: V = Vref·(1 + R2/R1) + Iq·R2, which with 220 Ω and a 470 Ω pot runs from 5 V to 18 V.
    /// <para>
    /// The nominal figures are that formula with the reference at its 25 °C value. A working part
    /// is not at 25 °C — it is at the room plus whatever it is dissipating — and a 7805's
    /// reference falls about a millivolt a degree, so the real output sits a little <i>under</i>
    /// the nominal, multiplied up by the gain of the adjustment network. Both are checked: the
    /// divider law exactly, against the reference the die is actually at, and the drift as a
    /// bounded amount in the right direction.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0.0, 5.00)]
    [InlineData(0.25, 8.26)]
    [InlineData(0.5, 11.51)]
    [InlineData(0.75, 14.77)]
    [InlineData(1.0, 18.03)]
    public void TheAdjustableSupplyFollowsItsPotentiometer(double position, double expected)
    {
        using var vm = Load("Adjustable Supply");

        var pot = vm.Circuit.Components.OfType<Potentiometer>().Single();
        var regulator = vm.Circuit.Components.OfType<VoltageRegulator>().Single();

        pot.Position = position;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var sim = vm.Simulation.Simulator!;
        sim.Run(0.4);

        var measured = sim.NodeVoltage(regulator.Output);

        const double setter = 220.0;
        var rheostat = pot.Resistance * position;

        var reference = regulator.Model.ReferenceAt(regulator.JunctionTemperature);
        var predicted = (reference * (1 + (rheostat / setter)))
                        + (regulator.Model.QuiescentCurrent * rheostat);

        Assert.Equal(predicted, measured, 0.05);

        // Warmer than the 25 °C the nominal figure assumes, so below it — but only by the drift,
        // never by more than a couple of hundred millivolts at the top of the range.
        Assert.InRange(measured, expected - 0.25, expected + 0.01);
        Assert.True(regulator.JunctionTemperature > 25.0,
            $"the die should be above 25 °C, not {regulator.JunctionTemperature:F1}");
    }

    /// <summary>
    /// And rejects the ripple it is sitting on. The reservoir leaves well over a volt of it on the
    /// rectified rail; almost none of that reaches the output, which is what the regulator is for.
    /// </summary>
    [Fact]
    public void AndRejectsTheRippleOnTheRailBelowIt()
    {
        using var vm = Load("Adjustable Supply");

        var regulator = vm.Circuit.Components.OfType<VoltageRegulator>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(0.4);

        double railLow = double.MaxValue, railHigh = double.MinValue;
        double outLow = double.MaxValue, outHigh = double.MinValue;

        // A couple of mains cycles, once the reservoir has charged.
        while (sim.Time < 0.44)
        {
            sim.Step();

            var rail = sim.NodeVoltage(regulator.Input);
            var output = sim.NodeVoltage(regulator.Output);

            railLow = Math.Min(railLow, rail);
            railHigh = Math.Max(railHigh, rail);
            outLow = Math.Min(outLow, output);
            outHigh = Math.Max(outHigh, output);
        }

        Assert.True(railHigh - railLow > 0.8,
            $"the rectified rail should ripple, not sit at {railHigh - railLow:0.000} V of it");

        Assert.True(outHigh - outLow < 0.02,
            $"and the regulator should reject it, not pass {(outHigh - outLow) * 1e3:0.0} mV");
    }

    /// <summary>
    /// The rail has to stay above the output by the regulator's dropout at every setting, ripple
    /// troughs included — which is what sizes the transformer and the reservoir, and the thing
    /// that goes wrong when either is chosen too small.
    /// </summary>
    [Fact]
    public void TheRailClearsDropoutRightAcrossTheRange()
    {
        using var vm = Load("Adjustable Supply");

        var pot = vm.Circuit.Components.OfType<Potentiometer>().Single();
        var regulator = vm.Circuit.Components.OfType<VoltageRegulator>().Single();

        pot.Position = 1.0;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        var sim = vm.Simulation.Simulator!;
        sim.Run(0.4);

        var lowest = double.MaxValue;
        while (sim.Time < 0.44)
        {
            sim.Step();
            lowest = Math.Min(lowest, sim.NodeVoltage(regulator.Input));
        }

        // 18 V out, plus the 7805's two volts of dropout.
        Assert.True(lowest > 20.0, $"the trough falls to {lowest:0.0} V, which is into dropout");
        Assert.False(regulator.IsInDropout);
    }

    /// <summary>
    /// The link carries the bit down fifty metres of cable, and the terminator is what makes the
    /// far end's copy of the edge a clean one rather than a staircase.
    /// </summary>
    [Fact]
    public void TheRs485LinkCarriesItsDataAndTheTerminatorMatters()
    {
        using var vm = Load("RS-485 Link");

        var ends = vm.Circuit.Components.OfType<Rs485Transceiver>().OrderBy(t => t.X).ToList();
        var near = ends[0];
        var far = ends[1];
        var terminator = vm.Circuit.Components.OfType<ToggleSwitch>().Single();

        var sim = vm.Simulation.Simulator!;
        sim.Run(2e-6);

        Assert.True(near.IsDriving);
        Assert.False(far.IsDriving);

        // With the terminator in, the far end settles at the differential voltage that was sent.
        var settled = Overshoot(sim, far, 2e-6);
        Assert.True(settled < 1.4,
            $"a terminated line should not overshoot much, and this reached {settled:0.00}x");

        terminator.IsClosed = false;
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);
        vm.Simulation.Simulator!.Run(2e-6);

        var ringing = Overshoot(vm.Simulation.Simulator!, far, 2e-6);

        Assert.True(ringing > settled + 0.3,
            $"taking the terminator out should ring: {ringing:0.00}x against {settled:0.00}x");
    }

    /// <summary>The largest the far end's difference gets, against what was actually driven.</summary>
    private static double Overshoot(CircuitSimulator sim, Rs485Transceiver far, double duration)
    {
        var until = sim.Time + duration;
        var highest = 0.0;

        while (sim.Time < until)
        {
            sim.Step();
            highest = Math.Max(highest, Math.Abs(far.Difference));
        }

        return highest / far.DifferentialDrive;
    }

    /// <summary>
    /// Turning the tuning control moves the resonance, which is the whole example — and it is
    /// measured with a frequency sweep, because that is the instrument the question belongs to.
    /// </summary>
    [Fact]
    public void TheVaractorExampleTunesItsTank()
    {
        static double ResonanceAt(double position)
        {
            using var vm = Load("Varactor Tuning");

            vm.Circuit.Components.OfType<Potentiometer>().Single().Position = position;
            Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

            var simulator = vm.Simulation.Simulator!;
            simulator.ResolveProbes();

            var sweep = new AcSweep(simulator).Run(new AcSweepRequest(1e6, 3e8, 300));
            var trace = sweep.Traces[0];

            var peak = 0;
            for (var i = 1; i < sweep.Frequencies.Count; i++)
                if (trace.Decibels(i) > trace.Decibels(peak)) peak = i;

            return sweep.Frequencies[peak];
        }

        var low = ResonanceAt(0.1);
        var high = ResonanceAt(1.0);

        Assert.True(high > low * 1.5,
            $"the knob should move the tuning, not from {low / 1e6:0.0} MHz to {high / 1e6:0.0} MHz");
    }

    /// <summary>
    /// The LCD example writes both lines, which means the whole exchange worked: the eight-bit
    /// function set that switches the controller to four bits, four commands, twenty-six nibble
    /// pairs of text, and a latch on every falling edge of E. Any of that out of step and what
    /// comes out is not the text.
    /// </summary>
    [Fact]
    public void TheLcdExampleWritesBothLines()
    {
        using var vm = Load("Character LCD");

        var lcd = vm.Circuit.Components.OfType<CharacterLcd>().Single();

        vm.Simulation.Simulator!.Run(0.2);

        Assert.True(lcd.IsFourBitMode, "the eight-bit function set should have switched it to four");
        Assert.True(lcd.DisplayOn, "and the display-on command should have turned it on");

        Assert.Equal("CIRQ LCD DEMO", lcd.Line(0).TrimEnd());
        Assert.Equal("HD44780 4-BIT", lcd.Line(1).TrimEnd());
    }

    /// <summary>
    /// And the second line was reached by addressing it rather than by running off the end of the
    /// first — which is the single most surprising thing about these modules, and the reason the
    /// example sends 0x80 | 0x40 between the two strings.
    /// </summary>
    [Fact]
    public void AndGetsToTheSecondLineByAddressingIt()
    {
        using var vm = Load("Character LCD");

        var lcd = vm.Circuit.Components.OfType<CharacterLcd>().Single();

        vm.Simulation.Simulator!.Run(0.2);

        var (line, column) = lcd.Cursor;

        Assert.Equal(1, line);
        Assert.Equal("HD44780 4-BIT".Length, column);

        // Line one holds only what was written to it: nothing spilled over.
        Assert.Equal(13, lcd.Line(0).TrimEnd().Length);
    }
}
