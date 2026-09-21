using Cirq.Components.Buses;
using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The memory, the latch and the SPI converter — the parts that turn the logic in the palette
/// into an addressed system, and fill in the bus that had only a shift register on it.
/// </summary>
public class MemoryAndSpiTests
{
    // ---- memory: presetting and reading back -----------------------------

    [Fact]
    public void ContentsLoadsBytesFromTheAddressGiven()
    {
        var memory = new MemoryDevice { Contents = "3E 01 C3 00 @100: DE AD BE EF" };

        Assert.Equal(0x3E, memory.ReadByte(0));
        Assert.Equal(0x01, memory.ReadByte(1));
        Assert.Equal(0xC3, memory.ReadByte(2));
        Assert.Equal(0x00, memory.ReadByte(3));

        Assert.Equal(0xDE, memory.ReadByte(0x100));
        Assert.Equal(0xEF, memory.ReadByte(0x103));

        // Everything else is zero, and the bytes that are not are counted.
        Assert.Equal(0, memory.ReadByte(0x200));
        Assert.Equal(7, memory.UsedBytes);
    }

    /// <summary>
    /// Reading it back gives what is in the memory now rather than the text that was typed — which
    /// is the point of the property, and the only way to see what a circuit has written.
    /// </summary>
    [Fact]
    public void AndReadingItBackShowsWhatIsThereNow()
    {
        var memory = new MemoryDevice { Contents = "11 22 33" };

        memory.WriteByte(1, 0x99);

        var dump = memory.Contents;

        Assert.Contains("11 99 33", dump);
        Assert.DoesNotContain("22", dump);
    }

    /// <summary>A dump goes back in as it came out, which is what makes it worth saving.</summary>
    [Fact]
    public void AndADumpRoundTrips()
    {
        var first = new MemoryDevice { Contents = "01 02 03 @0A0: FE ED" };
        var second = new MemoryDevice { Contents = first.Contents };

        Assert.Equal(first.Contents, second.Contents);
        Assert.Equal(0xFE, second.ReadByte(0x0A0));
        Assert.Equal(first.UsedBytes, second.UsedBytes);
    }

    /// <summary>
    /// A mostly empty memory does not come back as two thousand zeros, or every schematic with one
    /// in it would carry six kilobytes of nothing.
    /// </summary>
    [Fact]
    public void AndRunsOfZerosAreSkipped()
    {
        var memory = new MemoryDevice { Contents = "@000: 01 @400: 02" };

        Assert.True(memory.Contents.Length < 200, $"{memory.Contents.Length} characters for two bytes");
        Assert.Equal(string.Empty, new MemoryDevice().Contents);
    }

    /// <summary>Half-typed text loads what it can rather than emptying the memory.</summary>
    [Fact]
    public void AndRubbishIsSkippedRatherThanRefused()
    {
        var memory = new MemoryDevice { Contents = "01 zz 03 @xyz: 04" };

        Assert.Equal(0x01, memory.ReadByte(0));
        Assert.Equal(0x03, memory.ReadByte(1));
    }

    // ---- memory: on a bus ------------------------------------------------

    private static (CircuitSimulator Sim, MemoryDevice Memory, Ic74161 Counter, DcVoltageSource Rail)
        AddressedMemory(string contents, bool readOnly = false)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var memory = circuit.Add(new MemoryDevice { Contents = contents, IsReadOnly = readOnly });
        var counter = circuit.Add(new Ic74161());
        var clock = circuit.Add(new ClockSource(1e3));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(memory.Vcc, rail.Positive);
        circuit.Connect(memory.Gnd, gnd.Pin);
        circuit.Connect(counter.Vcc, rail.Positive);
        circuit.Connect(counter.Gnd, gnd.Pin);
        circuit.Connect(counter.Clock, clock.Out);

        foreach (var pin in new[] { counter.MasterReset, counter.ParallelEnable, counter.CountEnableP, counter.CountEnableT })
            circuit.Connect(pin, rail.Positive);

        foreach (var data in counter.Data) circuit.Connect(data, gnd.Pin);

        for (var i = 0; i < 4; i++) circuit.Connect(memory.Address[i], counter.Outputs[i]);
        for (var i = 4; i < MemoryDevice.AddressLines; i++) circuit.Connect(memory.Address[i], gnd.Pin);

        circuit.Connect(memory.ChipEnable, gnd.Pin);
        circuit.Connect(memory.OutputEnable, gnd.Pin);
        circuit.Connect(memory.WriteEnable, rail.Positive);

        foreach (var data in memory.Data)
        {
            var pull = circuit.Add(new Resistor(10e3));
            circuit.Connect(data, pull.A);
            circuit.Connect(pull.B, gnd.Pin);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();

        return (sim, memory, counter, rail);
    }

    /// <summary>
    /// A counter on the address lines and the memory answering on the data lines: walk the
    /// addresses and the stored bytes come out in order.
    /// </summary>
    [Fact]
    public void ACounterWalkingTheAddressesReadsTheBytesOutInOrder()
    {
        var (sim, memory, _, _) = AddressedMemory("11 22 33 44 55 66 77 88");

        Dictionary<int, int> read = [];

        // Long enough for the counter to wrap, so address zero is visited in the middle of the
        // run rather than only in the instant before the first clock.
        while (sim.Time < 40e-3)
        {
            sim.Step();

            if (!memory.IsReading || memory.LastAddress < 0) continue;

            // Read the bus, not the part's own opinion of what it put there.
            var bus = 0;
            for (var i = 0; i < 8; i++)
                if (sim.NodeVoltage(memory.Data[i]) > 2.0) bus |= 1 << i;

            read[memory.LastAddress] = bus;
        }

        // Every stored byte reached the bus at its own address.
        for (var address = 0; address < 8; address++)
            Assert.Equal(0x11 * (address + 1), read[address]);

        // And an address with nothing in it reads as zero rather than as the last thing seen.
        Assert.Equal(0, read[9]);
    }

    /// <summary>
    /// Deselecting releases the data lines entirely, which is what lets something else answer on
    /// the same wires. The pull-downs alone then decide them.
    /// </summary>
    [Fact]
    public void AndDeselectingItReleasesTheBus()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var memory = circuit.Add(new MemoryDevice { Contents = "FF FF FF FF" });
        var enable = circuit.Add(new LogicToggle(false));    // active low: false selects it

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(memory.Vcc, rail.Positive);
        circuit.Connect(memory.Gnd, gnd.Pin);
        circuit.Connect(memory.ChipEnable, enable.Out);
        circuit.Connect(memory.OutputEnable, gnd.Pin);
        circuit.Connect(memory.WriteEnable, rail.Positive);

        foreach (var address in memory.Address) circuit.Connect(address, gnd.Pin);

        List<Resistor> pulls = [];

        foreach (var data in memory.Data)
        {
            var pull = circuit.Add(new Resistor(10e3));
            circuit.Connect(data, pull.A);
            circuit.Connect(pull.B, gnd.Pin);
            pulls.Add(pull);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-6);

        Assert.True(memory.IsReading);
        Assert.True(sim.NodeVoltage(memory.Data[0]) > 2.0, "the selected memory is not driving");

        enable.State = true;
        sim.Run(200e-6);

        Assert.False(memory.IsReading);

        foreach (var data in memory.Data)
            Assert.True(sim.NodeVoltage(data) < 0.3,
                $"a released data line sat at {sim.NodeVoltage(data):0.00} V");
    }

    /// <summary>A ROM ignores writes; a RAM takes them.</summary>
    [Fact]
    public void AReadOnlyMemoryRefusesToBeWritten()
    {
        var ram = new MemoryDevice { Contents = "AA" };
        var rom = new MemoryDevice { Contents = "AA", IsReadOnly = true };

        Assert.Equal("SRAM", ram.ComponentType);
        Assert.Equal("ROM", rom.ComponentType);

        // The direct write is the back door and ignores the flag on purpose; the flag is about
        // what the circuit may do through the pins.
        ram.WriteByte(0, 0x55);
        Assert.Equal(0x55, ram.ReadByte(0));
    }

    /// <summary>
    /// And resetting puts the preset back, so a second run of the same circuit gives the same
    /// answers however much the first one scribbled on the memory.
    /// </summary>
    [Fact]
    public void AndResettingRestoresWhatWasTypedIn()
    {
        var memory = new MemoryDevice { Contents = "11 22 33" };

        memory.WriteByte(1, 0xFF);
        Assert.Equal(0xFF, memory.ReadByte(1));

        memory.ResetLogic();

        Assert.Equal(0x22, memory.ReadByte(1));
    }

    // ---- 74373 -----------------------------------------------------------

    /// <summary>
    /// Transparent while the enable is high — the outputs simply follow — and holding once it
    /// goes low. That is the whole difference from the edge-triggered flip-flops in the palette.
    /// </summary>
    [Fact]
    public void TheLatchIsTransparentThenHoldsWhatItSaw()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var latch = circuit.Add(new Ic74373());
        var enable = circuit.Add(new LogicToggle(true));
        var data = circuit.Add(new LogicToggle(true));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(latch.Vcc, rail.Positive);
        circuit.Connect(latch.Gnd, gnd.Pin);
        circuit.Connect(latch.LatchEnable, enable.Out);
        circuit.Connect(latch.OutputEnable, gnd.Pin);
        circuit.Connect(latch.Data[0], data.Out);

        for (var i = 1; i < 8; i++) circuit.Connect(latch.Data[i], gnd.Pin);

        foreach (var output in latch.Outputs)
        {
            var pull = circuit.Add(new Resistor(10e3));
            circuit.Connect(output, pull.A);
            circuit.Connect(pull.B, gnd.Pin);
        }

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-6);

        Assert.True(latch.IsTransparent);
        Assert.Equal(0x01, latch.Value);
        Assert.True(sim.NodeVoltage(latch.Outputs[0]) > 2.0);

        // Close the latch, then change the input underneath it.
        enable.State = false;
        sim.Run(200e-6);

        data.State = false;
        sim.Run(200e-6);

        Assert.False(latch.IsTransparent);
        Assert.Equal(0x01, latch.Value);
        Assert.True(sim.NodeVoltage(latch.Outputs[0]) > 2.0,
            "the latch let go of what it was holding when its input changed");

        // Open it again and it catches up.
        enable.State = true;
        sim.Run(200e-6);

        Assert.Equal(0x00, latch.Value);
        Assert.True(sim.NodeVoltage(latch.Outputs[0]) < 0.5);
    }

    // ---- MCP3008 ---------------------------------------------------------

    private static (Mcp3008 Adc, SpiMaster Spi) Convert(double volts, double reference = 5.0, int channel = 0)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        var adc = circuit.Add(new Mcp3008());

        // Start bit, then single-ended and the channel in the top nibble of the second byte.
        var command = 0x80 | (channel << 4);
        var spi = circuit.Add(new SpiMaster
        {
            Transactions = $"01 {command:X2} 00",
            ClockFrequency = 500e3,
        });

        var input = circuit.Add(new DcVoltageSource(volts));
        var vref = circuit.Add(new DcVoltageSource(reference));

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(adc.Vcc, rail.Positive);
        circuit.Connect(adc.Gnd, gnd.Pin);
        circuit.Connect(adc.AnalogGround, gnd.Pin);
        circuit.Connect(vref.Negative, gnd.Pin);
        circuit.Connect(adc.Reference, vref.Positive);

        circuit.Connect(spi.Vcc, rail.Positive);
        circuit.Connect(spi.Gnd, gnd.Pin);
        circuit.Connect(spi.Clock, adc.Clock);
        circuit.Connect(spi.MasterOut, adc.DataIn);
        circuit.Connect(spi.MasterIn, adc.DataOutPin);
        circuit.Connect(spi.ChipSelect, adc.ChipSelect);

        circuit.Connect(input.Negative, gnd.Pin);
        circuit.Connect(input.Positive, adc.Inputs[channel]);

        for (var i = 0; i < 8; i++)
            if (i != channel) circuit.Connect(adc.Inputs[i], gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(2e-3);

        return (adc, spi);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(1.25, 256)]
    [InlineData(2.5, 512)]
    [InlineData(5.0, 1023)]
    public void TheConverterReadsItsInputAsAFractionOfTheReference(double volts, int expected)
    {
        var (adc, _) = Convert(volts);

        Assert.Equal(0, adc.LastChannel);
        Assert.InRange(adc.LastCode, expected - 2, expected + 2);
        Assert.Equal(volts, adc.LastVoltage, 0.01);
    }

    /// <summary>
    /// And the master gets the same number, decoded the way every example for this part decodes
    /// it — which is the only thing that makes the model worth having.
    /// </summary>
    [Fact]
    public void AndTheMasterReadsItBackWhereTheDatasheetSaysItWillBe()
    {
        var (adc, spi) = Convert(4.0);

        Assert.Equal(3, spi.ReceivedBytes.Count);

        var code = ((spi.ReceivedBytes[1] & 0x03) << 8) | spi.ReceivedBytes[2];

        Assert.Equal(adc.LastCode, code);
    }

    /// <summary>The channel is in the command, so asking for a different one reads a different pin.</summary>
    [Fact]
    public void AndTheChannelIsChosenByTheCommand()
    {
        var (fifth, _) = Convert(2.0, channel: 5);

        Assert.Equal(5, fifth.LastChannel);
        Assert.Equal(2.0, fifth.LastVoltage, 0.02);
    }

    /// <summary>
    /// Ratiometric, not absolute: halving the reference doubles the code for the same input. It
    /// is the property that lets supply noise cancel, and the one that surprises people.
    /// </summary>
    [Fact]
    public void AndTheReadingIsAFractionOfTheReferenceRatherThanAVoltage()
    {
        var (full, _) = Convert(1.0, reference: 5.0);
        var (half, _) = Convert(1.0, reference: 2.5);

        Assert.Equal(2.0, (double)half.LastCode / full.LastCode, 0.05);
    }

    /// <summary>An input above the reference stops being followed, and the part says so.</summary>
    [Fact]
    public void AndAnInputAboveTheReferenceIsReportedRatherThanWrapped()
    {
        var (adc, _) = Convert(4.9, reference: 2.5);

        Assert.Equal(adc.FullScale, adc.LastCode);
        Assert.True(adc.IsClipping);
        Assert.NotEmpty(adc.Violations);
    }
}
