using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Ics;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The examples for the parts that cover what the palette previously could not show at all.
/// </summary>
public class NewPartExampleTests
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
    /// One transceiver drives the bus and the other listens; the counter's byte arrives at the far
    /// side unchanged, which means it crossed two parts and a shared set of wires.
    /// </summary>
    [Fact]
    public void TheSharedBusCarriesTheCounterToTheListener()
    {
        using var vm = Load("Shared Bus");

        var transceivers = vm.Circuit.Components.OfType<Ic74245>().ToList();
        var counter = vm.Circuit.Components.OfType<Ic74161>().Single();
        var sim = vm.Simulation.Simulator!;

        Assert.Equal(2, transceivers.Count);

        sim.Run(3e-3);

        // Exactly one of the two is driving: that is the discipline the whole arrangement exists
        // to keep, and two drivers at once is the fault it is designed to avoid.
        Assert.Single(transceivers, t => t.IsEnabled);

        var talker = transceivers.Single(t => t.IsEnabled);
        var listener = transceivers.Single(t => !t.IsEnabled);

        // The low nibble of what the talker put on the bus is what the counter is holding.
        Assert.Equal(counter.Count & 0x0F, talker.Value & 0x0F);
        Assert.False(listener.IsEnabled);
    }

    /// <summary>
    /// And releasing the bus really releases it: with the driver disabled the pull-downs alone
    /// decide the wires, which only a tri-state output allows.
    /// </summary>
    [Fact]
    public void AndAReleasedBusFallsToItsPullDowns()
    {
        using var vm = Load("Shared Bus");

        var talker = vm.Circuit.Components.OfType<Ic74245>().First();
        var select = vm.Circuit.Components.OfType<LogicToggle>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(2e-3);

        // Hand the bus over, then let it settle.
        select.State = !select.State;
        sim.Run(3e-3);

        Assert.False(talker.IsEnabled);

        foreach (var pin in talker.B)
            Assert.True(sim.NodeVoltage(pin) < 0.3,
                $"a released bus wire sat at {sim.NodeVoltage(pin):0.00} V");
    }

    /// <summary>
    /// The bus goes dominant whenever anybody is sending a zero, and the node that sent a one
    /// hears it — which is the arbitration, and it costs nothing.
    /// </summary>
    [Fact]
    public void TheCanExampleShowsADominantBitWinning()
    {
        using var vm = Load("CAN Arbitration");

        var nodes = vm.Circuit.Components.OfType<CanTransceiver>().ToList();
        var sim = vm.Simulation.Simulator!;

        Assert.Equal(2, nodes.Count);

        var sawDominant = false;
        var sawRecessive = false;
        var sawArbitration = false;

        while (sim.Time < 6e-3)
        {
            sim.Step();

            if (nodes[0].IsBusDominant) sawDominant = true; else sawRecessive = true;

            // One node sending recessive while the bus is dominant: somebody outranked it.
            if (nodes.Any(n => n.LostArbitration)) sawArbitration = true;

            // Both ends always agree about the bus, which is what makes it a bus.
            Assert.Equal(nodes[0].IsBusDominant, nodes[1].IsBusDominant);
        }

        Assert.True(sawDominant && sawRecessive, "the bus never changed state");
        Assert.True(sawArbitration, "the two nodes never talked over each other");
    }

    /// <summary>
    /// The comparison the example exists for: at the same current the IGBT holds over a volt and
    /// the MOSFET a few tens of millivolts. It is the reason to choose one over the other, and it
    /// goes the other way entirely at six hundred volts.
    /// </summary>
    [Fact]
    public void TheIgbtHoldsAVoltageWhereTheMosfetHoldsAResistance()
    {
        using var vm = Load("IGBT and MOSFET");

        var igbt = vm.Circuit.Components.OfType<Igbt>().Single();
        var mosfet = vm.Circuit.Components.OfType<Mosfet>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(300e-6);

        var igbtOn = double.MaxValue;
        var mosfetOn = double.MaxValue;

        while (sim.Time < 1.5e-3)
        {
            sim.Step();

            if (!igbt.IsOn) continue;

            igbtOn = Math.Min(igbtOn, sim.NodeVoltage(igbt.Collector));
            mosfetOn = Math.Min(mosfetOn, sim.NodeVoltage(mosfet.Drain));
        }

        Assert.True(igbtOn > 1.0, $"the IGBT dropped only {igbtOn:0.000} V");
        Assert.True(mosfetOn < 0.5, $"the MOSFET dropped {mosfetOn:0.000} V");
        Assert.True(igbtOn > mosfetOn * 3);
    }

    /// <summary>
    /// The amplifier holds the diode at zero volts, which is the whole trick: the photocurrent has
    /// nowhere to go but the feedback resistor, and the output is that current times it.
    /// </summary>
    [Fact]
    public void TheTransimpedanceAmpHoldsTheDiodeAtZeroAndReadsItsCurrent()
    {
        using var vm = Load("Transimpedance Amp");

        var diode = vm.Circuit.Components.OfType<Photodiode>().Single();
        var amplifier = vm.Circuit.Components.OfType<OperationalAmplifier>().Single();
        var feedback = vm.Circuit.Components.OfType<Resistor>()
            .Single(r => Math.Abs(r.Resistance - 1e6) < 1.0);

        var sim = vm.Simulation.Simulator!;
        sim.Run(20e-3);

        // The summing junction is a virtual earth, so the diode sees no bias at all.
        Assert.True(Math.Abs(sim.NodeVoltage(diode.Cathode)) < 0.05,
            $"the summing junction sat at {sim.NodeVoltage(diode.Cathode):0.000} V");

        // And the output is the photocurrent through the feedback resistor. Positive here because
        // the cathode is the end at the summing junction, so the diode draws its current out of
        // that node and the amplifier has to supply it back through the feedback resistor.
        var expected = diode.PhotoCurrent * feedback.Resistance;

        Assert.Equal(expected, sim.NodeVoltage(amplifier.Output), Math.Abs(expected) * 0.1);
    }

    /// <summary>
    /// And unlike a load resistor, it does not saturate: a hundred times the light gives a hundred
    /// times the output, which is the whole reason the circuit is built this way.
    /// </summary>
    [Fact]
    public void AndStaysLinearWhereALoadResistorWouldHaveGivenUp()
    {
        static double OutputAt(double lux)
        {
            using var vm = Load("Transimpedance Amp");

            vm.Circuit.Components.OfType<Photodiode>().Single().Illuminance = lux;
            Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

            var amplifier = vm.Circuit.Components.OfType<OperationalAmplifier>().Single();
            vm.Simulation.Simulator!.Run(20e-3);

            return Math.Abs(vm.Simulation.Simulator!.NodeVoltage(amplifier.Output));
        }

        var dim = OutputAt(10);
        var bright = OutputAt(1000);

        Assert.Equal(100.0, bright / dim, 15.0);
    }
}
