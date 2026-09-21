using Cirq.Components.Buses;
using Cirq.Components.Electromechanical;
using Cirq.Components.Ics;
using Cirq.Components.Passive;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The five examples built for the resettable fuse, the solid-state relay, the quad op-amp, the
/// microstepping driver and the RS-232 transceiver. Each is checked against the thing it exists
/// to show, and against a figure worked out from the circuit rather than read off a previous run.
/// </summary>
public class PowerAndInterfaceExampleTests
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

    [Fact]
    public void MicrosteppingDriveHoldsItsCurrentLimitAndTurnsTheShaft()
    {
        var vm = Load("Microstepping Drive");
        var sim = vm.Simulation.Simulator!;

        var driver = vm.Circuit.Components.OfType<StepperDriver>().Single();
        var motor = vm.Circuit.Components.OfType<StepperMotor>().Single();

        var lowest = 0.0;
        var highest = 0.0;

        while (sim.Time < 60e-3)
        {
            sim.Step();
            lowest = Math.Min(lowest, driver.WindingCurrent(0));
            highest = Math.Max(highest, driver.WindingCurrent(0));
        }

        // Twelve volts into roughly 2.8 Ω would be 4.3 A if the bridge simply applied it. The
        // chopper holds it to the 0.8 A limit in both directions.
        Assert.InRange(highest, 0.78, 0.85);
        Assert.InRange(lowest, -0.85, -0.78);

        // 200 Hz for 60 ms is twelve steps, and a 200-step motor moves 1.8° each: 21.6°.
        Assert.Equal(21.6, motor.Angle, 1);
    }

    [Fact]
    public void ZeroCrossingSwitchWaitsForTheMainsAfterTheButtonIsPressed()
    {
        var vm = Load("Zero-Crossing Switch");
        var sim = vm.Simulation.Simulator!;

        var relay = vm.Circuit.Components.OfType<SolidStateRelay>().Single();
        var command = vm.Circuit.Components.OfType<ToggleSwitch>().Single();

        // A quarter of a 50 Hz cycle in, which is a peak — the worst moment to switch, and the
        // whole reason a zero-crossing type exists.
        sim.Run(25e-3);
        Assert.False(relay.IsConducting);

        command.IsClosed = true;
        vm.Simulation.InvalidateTopology();
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        sim = vm.Simulation.Simulator!;
        sim.Run(25e-3);

        var commandedAt = sim.Time;
        var firedAt = double.NaN;
        var peak = 0.0;

        while (sim.Time < commandedAt + 30e-3)
        {
            sim.Step();
            if (relay.IsConducting && double.IsNaN(firedAt)) firedAt = sim.Time;
            peak = Math.Max(peak, Math.Abs(relay.LoadCurrent));
        }

        Assert.False(double.IsNaN(firedAt));

        // 340 V peak into 100 Ω is 3.4 A at the top of the sine, and it starts from a crossing,
        // so a 30 ms window sees well over an amp.
        Assert.InRange(peak, 1.0, 3.5);
    }

    [Fact]
    public void QuadOpAmpChainBiases_Buffers_AmplifiesAndInverts()
    {
        var vm = Load("Quad Op-Amp");
        var sim = vm.Simulation.Simulator!;

        var quad = vm.Circuit.Components.OfType<QuadOpAmp>().Single();
        var outputs = Enumerable.Range(0, 4).Select(i => quad.Channel(i).Output).ToArray();

        // The coupling capacitor works against 10 kΩ, so give the bias a few time constants.
        sim.Run(300e-3);

        var low = outputs.Select(_ => double.MaxValue).ToArray();
        var high = outputs.Select(_ => double.MinValue).ToArray();
        var worstInversion = 0.0;

        while (sim.Time < 305e-3)
        {
            sim.Step();

            for (var i = 0; i < 4; i++)
            {
                var v = sim.NodeVoltage(outputs[i]);
                low[i] = Math.Min(low[i], v);
                high[i] = Math.Max(high[i], v);
            }

            // The fourth stage is a unity inverter about the reference, so at every instant it
            // should be as far below 2.5 V as the third stage is above it.
            var reference = sim.NodeVoltage(outputs[0]);
            var mirrored = reference - (sim.NodeVoltage(outputs[2]) - reference);
            worstInversion = Math.Max(worstInversion, Math.Abs(sim.NodeVoltage(outputs[3]) - mirrored));
        }

        // Channel A: half of a 5 V rail, and dead steady because it is a follower on a divider.
        Assert.Equal(2.5, low[0], 2);
        Assert.Equal(2.5, high[0], 2);

        // Channel B: the signal, now sitting on that reference rather than on ground.
        Assert.Equal(0.4, high[1] - low[1], 2);
        Assert.Equal(2.5, (high[1] + low[1]) / 2, 1);

        // Channel C: a gain of exactly two, and still inside what the part can swing.
        Assert.Equal(2.0 * (high[1] - low[1]), high[2] - low[2], 2);
        Assert.False(quad.IsChannelSaturated(2));

        // Channel D: the same size, upside down.
        Assert.Equal(high[2] - low[2], high[3] - low[3], 2);
        Assert.True(worstInversion < 0.05, $"the inverting stage was out by {worstInversion:F3} V");

        // And all four ran off one supply pair, which is the point of the package.
        Assert.Single(vm.Circuit.Components.OfType<QuadOpAmp>());
    }

    [Fact]
    public void ResettableFuseTripsOnTheFaultAndRecoversWhenItIsCleared()
    {
        var vm = Load("Resettable Fuse");
        var sim = vm.Simulation.Simulator!;

        var fuse = vm.Circuit.Components.OfType<ResettableFuse>().Single();
        var fault = vm.Circuit.Components.OfType<ToggleSwitch>().Single();

        sim.Run(1.0);

        // 5 V into 22 Ω is 227 mA, under the 500 mA hold, so it sits there indefinitely.
        Assert.False(fuse.IsTripped);
        Assert.Equal(0.227, fuse.Current, 2);

        fault.IsClosed = true;
        vm.Simulation.InvalidateTopology();
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        sim = vm.Simulation.Simulator!;
        sim.Run(4.0);

        Assert.True(fuse.IsTripped);

        // The lesson: a tripped PPTC is not an open circuit. It is passing a few milliamps, and
        // those few milliamps are what keep it hot enough to stay tripped.
        Assert.InRange(fuse.Current, 1e-3, 100e-3);

        fault.IsClosed = false;
        vm.Simulation.InvalidateTopology();
        Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

        sim = vm.Simulation.Simulator!;
        sim.Run(10.0);

        Assert.False(fuse.IsTripped);
        Assert.Equal(0.227, fuse.Current, 2);
    }

    [Fact]
    public void Rs232LinkCarriesTextBothWaysAtRealLineLevels()
    {
        var vm = Load("RS-232 Link");
        var sim = vm.Simulation.Simulator!;

        var transceivers = vm.Circuit.Components.OfType<Max232>().ToArray();
        var terminal = vm.Circuit.Components.OfType<SerialTerminal>().Single();
        var device = vm.Circuit.Components.OfType<SerialDevice>().Single();

        Assert.Equal(2, transceivers.Length);

        var lowest = double.MaxValue;
        var highest = double.MinValue;

        while (sim.Time < 12e-3)
        {
            sim.Step();

            var v = sim.NodeVoltage(transceivers[0].DriverOutputs[0]);
            lowest = Math.Min(lowest, v);
            highest = Math.Max(highest, v);
        }

        // The cable between the two chips is nothing like TTL: it swings either side of ground,
        // and both ends of it are outside what the 5 V supply could produce on its own.
        Assert.True(lowest < -5.0, $"the line only reached {lowest:F2} V on a mark");
        Assert.True(highest > 5.0, $"the line only reached {highest:F2} V on a space");

        Assert.InRange(transceivers[0].PositiveRail, 8.0, 8.6);
        Assert.InRange(transceivers[0].NegativeRail, -8.6, -8.0);

        // And the text got through in both directions, which is the only proof that the two
        // inversions cancelled rather than adding up.
        Assert.Contains("hi", device.ReceivedText);
        Assert.Contains("READY", terminal.ReceivedText);
    }
}
