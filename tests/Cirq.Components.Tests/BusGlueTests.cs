using Cirq.Components.Digital;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// The 74 series glue a small computer's bus is built from: the 74139 decoder, the 74244 buffer,
/// and the 74374, 74573 and 74574 registers — datasheet pinouts and truth tables.
/// </summary>
public class BusGlueTests
{
    [Fact]
    public void The74139IsOnItsDatasheetPinout()
    {
        var ic = new Ic74139();
        string[] names = ["1G", "1A", "1B", "1Y0", "1Y1", "1Y2", "1Y3", "GND", "2Y3", "2Y2", "2Y1", "2Y0", "2B", "2A", "2G", "VCC"];
        Assert.Equal(names, ic.Terminals.Select(t => t.Name));
        Assert.Equal(Enumerable.Range(1, 16).Select(n => $"p{n}"), ic.Terminals.Select(t => t.Id));
    }

    /// <summary>All eight rows of each half's truth table: enabled, one output low; disabled, none.</summary>
    [Fact]
    public void Each74139HalfDecodesTwoLinesToOneOfFour()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74139());

        var enable = bench.Toggles(ic.Enable, 0b11);
        var a = bench.Toggles(ic.SelectA);
        var b = bench.Toggles(ic.SelectB);

        for (var half = 0; half < 2; half++)
        {
            for (var select = 0; select < 4; select++)
            {
                a[half].State = (select & 1) != 0;
                b[half].State = (select & 2) != 0;

                enable[half].State = true;
                bench.Run();
                Assert.Equal(0b1111, bench.Byte(ic.Outputs(half)));

                enable[half].State = false;
                bench.Run();
                Assert.Equal(0b1111 & ~(1 << select), bench.Byte(ic.Outputs(half)));

                // The other half is not disturbed.
                Assert.Equal(0b1111, bench.Byte(ic.Outputs(1 - half)));
                enable[half].State = true;
            }
        }
    }

    [Fact]
    public void The74244IsOnItsDatasheetPinout()
    {
        var ic = new Ic74244();
        string[] names = ["1G", "1A1", "2Y4", "1A2", "2Y3", "1A3", "2Y2", "1A4", "2Y1", "GND", "2A1", "1Y4", "2A2", "1Y3", "2A3", "1Y2", "2A4", "1Y1", "2G", "VCC"];
        Assert.Equal(names, ic.Terminals.Select(t => t.Name));
        Assert.Equal("1Y1", ic.Outputs[0].Name);
        Assert.Equal("2Y1", ic.Outputs[4].Name);
        Assert.Equal("2A4", ic.Inputs[7].Name);
    }

    /// <summary>Each group passes A to Y while its enable is low and lets go of the outputs while it is high.</summary>
    [Fact]
    public void The74244BuffersEachGroupOnlyWhileEnabled()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74244());

        var g1 = bench.Toggle(ic.Enable1, true);
        var g2 = bench.Toggle(ic.Enable2, true);
        bench.Toggles(ic.Inputs, 0b1001_0110);

        bench.Run();
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(i)));

        g1.State = false;
        bench.Run();
        Assert.Equal(0b0110, bench.Byte(ic.Outputs.Take(4).ToList()));
        Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(4));

        g2.State = false;
        bench.Run();
        Assert.Equal(0b1001_0110, bench.Byte(ic.Outputs));
    }

    [Fact]
    public void The74374And74574AreOnTheirPinouts()
    {
        var interleaved = new Ic74374();
        Assert.Equal(["OE", "Q0", "D0", "D1", "Q1", "Q2", "D2", "D3", "Q3", "GND", "CLK", "Q4", "D4", "D5", "Q5", "Q6", "D6", "D7", "Q7", "VCC"],
            interleaved.Terminals.Select(t => t.Name));

        var flow = new Ic74574();
        Assert.Equal(["OE", "D0", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "GND", "CLK", "Q7", "Q6", "Q5", "Q4", "Q3", "Q2", "Q1", "Q0", "VCC"],
            flow.Terminals.Select(t => t.Name));

        Assert.Equal("LE", new Ic74573().Terminals.Single(t => t.Id == "p11").Name);
    }

    /// <summary>A D flip-flop register takes the inputs on CLK's rising edge and ignores them otherwise.</summary>
    [Theory]
    [InlineData(typeof(Ic74374))]
    [InlineData(typeof(Ic74574))]
    public void AFlipFlopRegisterTakesTheInputsOnTheRisingEdge(Type type)
    {
        var bench = new LogicBench();
        var ic = bench.Add((OctalRegister)Activator.CreateInstance(type)!);

        bench.Tie(ic.OutputEnable, false);
        var clock = bench.Toggle(ic.Strobe);
        var data = bench.Toggles(ic.Data, 0x3C);

        bench.Run();
        Assert.Equal(0, bench.Byte(ic.Outputs));

        clock.State = true;
        bench.Run();
        Assert.Equal(0x3C, bench.Byte(ic.Outputs));

        // Held high, the inputs change and nothing follows; falling is not an edge it uses.
        LogicBench.Set(data, 0xC3);
        bench.Run();
        clock.State = false;
        bench.Run();
        Assert.Equal(0x3C, bench.Byte(ic.Outputs));

        clock.State = true;
        bench.Run();
        Assert.Equal(0xC3, ic.Value);
        Assert.Equal(0xC3, bench.Byte(ic.Outputs));
    }

    /// <summary>The 74573 follows while LE is high and holds what it had when LE fell.</summary>
    [Fact]
    public void The74573IsTransparentWhileLatchEnableIsHigh()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74573());

        var oe = bench.Toggle(ic.OutputEnable);
        var le = bench.Toggle(ic.Strobe, true);
        var data = bench.Toggles(ic.Data, 0x11);

        bench.Run();
        Assert.Equal(0x11, bench.Byte(ic.Outputs));

        LogicBench.Set(data, 0x22);
        bench.Run();
        Assert.Equal(0x22, bench.Byte(ic.Outputs));

        le.State = false;
        bench.Run();
        LogicBench.Set(data, 0x33);
        bench.Run();
        Assert.Equal(0x22, bench.Byte(ic.Outputs));

        // Off the bus, still holding.
        oe.State = true;
        bench.Run();
        Assert.True(ic.IsReleased);
        Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(3));
        oe.State = false;
        bench.Run();
        Assert.Equal(0x22, bench.Byte(ic.Outputs));
    }
}
