using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Electromechanical;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
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
}
