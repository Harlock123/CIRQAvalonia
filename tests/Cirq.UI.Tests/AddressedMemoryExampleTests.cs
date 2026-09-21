using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.UI.ViewModels;

namespace Cirq.UI.Tests;

/// <summary>
/// The examples for the memory, the latch and the SPI converter.
/// </summary>
public class AddressedMemoryExampleTests
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
    /// The counter walks the addresses and the ROM answers with what is stored at each — which is
    /// a fetch, and the whole shape of a processor reading a program.
    /// </summary>
    [Fact]
    public void TheCounterFetchesEveryByteTheRomIsHolding()
    {
        using var vm = Load("Addressed Memory");

        var rom = vm.Circuit.Components.OfType<MemoryDevice>().Single();
        var sim = vm.Simulation.Simulator!;

        Assert.True(rom.IsReadOnly);

        Dictionary<int, int> fetched = [];

        // Long enough for the counter to go round more than once.
        while (sim.Time < 60e-3)
        {
            sim.Step();

            if (!rom.IsReading || rom.LastAddress < 0) continue;

            var bus = 0;
            for (var i = 0; i < 8; i++)
                if (sim.NodeVoltage(rom.Data[i]) > 2.0) bus |= 1 << i;

            fetched[rom.LastAddress] = bus;
        }

        // What came back off the bus is what was typed into the part.
        for (var address = 0; address < 8; address++)
            Assert.Equal(rom.ReadByte(address), fetched[address]);

        // And the bytes really are the ones in the preset, not zeros that happen to match.
        Assert.Equal(0x48, rom.ReadByte(0));
        Assert.Equal(0xFF, rom.ReadByte(7));
    }

    /// <summary>
    /// The latch is left open in this example, so it is a wire: what it holds is whatever the bus
    /// is carrying, and its outputs follow. Tie its enable low instead and it would freeze the
    /// last byte fetched, which is what a processor does with one.
    /// </summary>
    [Fact]
    public void TheOpenLatchFollowsWhateverTheBusIsCarrying()
    {
        using var vm = Load("Addressed Memory");

        var rom = vm.Circuit.Components.OfType<MemoryDevice>().Single();
        var latch = vm.Circuit.Components.OfType<Ic74373>().Single();
        var sim = vm.Simulation.Simulator!;

        sim.Run(5e-3);

        var checks = 0;
        HashSet<int> seen = [];

        while (sim.Time < 40e-3)
        {
            sim.Step();

            if (!rom.IsReading || rom.LastAddress < 0) continue;

            Assert.True(latch.IsTransparent);
            Assert.False(latch.IsReleased);

            var bus = 0;
            var latched = 0;

            for (var i = 0; i < 8; i++)
            {
                if (sim.NodeVoltage(rom.Data[i]) > 2.0) bus |= 1 << i;
                if (sim.NodeVoltage(latch.Outputs[i]) > 2.0) latched |= 1 << i;
            }

            // Settled samples only. The latch is a wire, but a wire with twelve nanoseconds in
            // it, and the bus is moving as the counter counts — so compare the two ends of the
            // chain only at the instants they agree, and check that what they agree on is what
            // the memory actually holds.
            if (bus != latched) continue;

            // Not compared against the address, deliberately: the memory knows which address it
            // has been given before its data pins have finished changing, so at a transition the
            // two disagree by one fetch. What is worth asserting is that everything arriving at
            // the far end is a byte the memory actually holds.
            seen.Add(latched);
            checks++;
        }

        Assert.True(checks > 10, $"only {checks} settled samples to check");

        // Every byte that reached the latch is one the ROM holds...
        var stored = Enumerable.Range(0, 16).Select(rom.ReadByte).Select(b => (int)b).ToHashSet();
        Assert.Empty(seen.Except(stored));

        // ...and enough of them got through that this is not one byte repeated.
        Assert.True(seen.Count >= 4, $"only {seen.Count} distinct bytes made it across");
        Assert.Contains(0x48, seen);
        Assert.Contains(0xFF, seen);
    }

    /// <summary>
    /// The converter reads the knob and the master gets the same number back, decoded the way the
    /// datasheet says — one transaction, with the answer arriving underneath the question.
    /// </summary>
    [Fact]
    public void TheSpiConverterReadsTheKnobAndTheMasterGetsTheSameNumber()
    {
        using var vm = Load("SPI ADC");

        var adc = vm.Circuit.Components.OfType<Mcp3008>().Single();
        var master = vm.Circuit.Components.OfType<SpiMaster>().Single();
        var knob = vm.Circuit.Components.OfType<Potentiometer>().Single();

        vm.Simulation.Simulator!.Run(3e-3);

        Assert.Equal(0, adc.LastChannel);
        Assert.False(adc.IsClipping);

        // The wiper is at six tenths of the rail, so the code is six tenths of full scale.
        Assert.Equal(knob.Position, (double)adc.LastCode / adc.FullScale, 0.05);

        Assert.Equal(3, master.ReceivedBytes.Count);
        Assert.Equal(adc.LastCode, ((master.ReceivedBytes[1] & 0x03) << 8) | master.ReceivedBytes[2]);
    }

    /// <summary>And turning the knob changes the reading, which is the whole point of it.</summary>
    [Fact]
    public void AndTurningTheKnobChangesTheReading()
    {
        static int CodeAt(double position)
        {
            using var vm = Load("SPI ADC");

            vm.Circuit.Components.OfType<Potentiometer>().Single().Position = position;
            Assert.True(vm.Simulation.Rebuild(), vm.Simulation.Status);

            var adc = vm.Circuit.Components.OfType<Mcp3008>().Single();
            vm.Simulation.Simulator!.Run(3e-3);

            return adc.LastCode;
        }

        var low = CodeAt(0.2);
        var high = CodeAt(0.9);

        Assert.True(high > low * 3, $"{low} at two tenths against {high} at nine");
    }
}
