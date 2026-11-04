using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The 74189 16 × 4 RAM and the 74670 4 × 4 register file.
/// </summary>
public class NibbleMemoryTests
{
    // ---- memories ---------------------------------------------------------

    [Fact]
    public void The74189StoresWordsAndReadsThemBackInverted()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74189());
        Assert.Equal(["A0", "CS", "WE", "D1", "Q1", "D2", "Q2", "GND", "Q3", "D3", "Q4", "D4", "A3", "A2", "A1", "VCC"],
            ic.Terminals.Select(t => t.Name));

        var address = bench.Toggles(ic.Address);
        var data = bench.Toggles(ic.Data);
        var cs = bench.Toggle(ic.ChipSelect, true);
        var we = bench.Toggle(ic.WriteEnable, true);

        for (var word = 0; word < 16; word++)
        {
            LogicBench.Set(address, word);
            LogicBench.Set(data, (word * 7) & 0xF);
            cs.State = false;
            we.State = false;
            bench.Run();

            // Writing: the outputs are off the bus.
            Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(0));
            we.State = true;
            cs.State = true;
            bench.Run();
        }

        cs.State = false;
        for (var word = 0; word < 16; word++)
        {
            LogicBench.Set(address, word);
            bench.Run();
            Assert.Equal(~(word * 7) & 0xF, bench.Byte(ic.Outputs));
        }

        cs.State = true;
        bench.Run();
        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(i)));
        Assert.Equal("07E5C3A18F6D4B29", ic.Contents);
    }

    /// <summary>A preset in Contents is saved in the file, and a reset puts it back over the circuit's writes.</summary>
    [Fact]
    public void A74189PresetTravelsInTheFileAndComesBackOnReset()
    {
        var circuit = new Circuit();
        var ram = circuit.Add(new Ic74189 { Contents = "0123 4567 89AB CDEF" });
        Assert.Equal(0xC, ram.ReadWord(12));

        var reopened = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit.Components.OfType<Ic74189>().Single();
        Assert.Equal("0123456789ABCDEF", reopened.Contents);
        Assert.Throws<FormatException>(() => reopened.Contents = "12G4");
    }

    /// <summary>
    /// The 74670 writes one word while reading another — separate addresses, separate enables — and its
    /// outputs let go while /GR is high.
    /// </summary>
    [Fact]
    public void The74670ReadsAndWritesAtOnce()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74670());
        Assert.Equal(["D2", "D3", "D4", "RB", "RA", "Q4", "Q3", "GND", "Q2", "Q1", "GR", "GW", "WB", "WA", "D1", "VCC"],
            ic.Terminals.Select(t => t.Name));

        var data = bench.Toggles(ic.Data);
        var wa = bench.Toggle(ic.WriteA);
        var wb = bench.Toggle(ic.WriteB);
        var ra = bench.Toggle(ic.ReadA);
        var rb = bench.Toggle(ic.ReadB);
        var gw = bench.Toggle(ic.WriteEnable, true);
        var gr = bench.Toggle(ic.ReadEnable, true);

        bench.Run();
        for (var word = 0; word < 4; word++)
        {
            wa.State = (word & 1) != 0;
            wb.State = (word & 2) != 0;
            LogicBench.Set(data, 9 + word);
            gw.State = false;
            bench.Run();
            gw.State = true;
            bench.Run();
        }

        Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(i)));

        // Read word 2 while writing word 1.
        gr.State = false;
        ra.State = false;
        rb.State = true;
        wa.State = true;
        wb.State = false;
        LogicBench.Set(data, 3);
        gw.State = false;
        bench.Run();
        Assert.Equal(11, bench.Byte(ic.Outputs));
        Assert.Equal(3, ic.ReadWord(1));
        Assert.Equal("93BC", ic.Contents);
    }
}
