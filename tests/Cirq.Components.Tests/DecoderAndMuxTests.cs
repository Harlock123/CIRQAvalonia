using Cirq.Components.Digital;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// The 7442, 74154 and 4028 decoders and the 74153, 74157, 74158, 74251 and 74257 multiplexers:
/// pinouts, every code, the enables, and the tri-state parts letting go.
/// </summary>
public class DecoderAndMuxTests
{
    [Theory]
    [InlineData(typeof(Ic7442), "p1=Y0 p7=Y6 p8=GND p9=Y7 p11=Y9 p12=D p13=C p14=B p15=A p16=VCC")]
    [InlineData(typeof(Ic74154), "p1=Y0 p11=Y10 p12=GND p13=Y11 p17=Y15 p18=G1 p19=G2 p20=D p21=C p22=B p23=A p24=VCC")]
    [InlineData(typeof(Ic4028), "p1=Q4 p2=Q2 p3=Q0 p4=Q7 p5=Q9 p6=Q5 p7=Q6 p8=VSS p9=Q8 p10=A p11=D p12=C p13=B p14=Q1 p15=Q3 p16=VDD")]
    [InlineData(typeof(Ic74153), "p1=1G p2=B p3=1C3 p6=1C0 p7=1Y p8=GND p9=2Y p10=2C0 p13=2C3 p14=A p15=2G p16=VCC")]
    [InlineData(typeof(Ic74157), "p1=SEL p2=1A p3=1B p4=1Y p5=2A p6=2B p7=2Y p8=GND p9=3Y p10=3B p11=3A p12=4Y p13=4B p14=4A p15=G p16=VCC")]
    [InlineData(typeof(Ic74257), "p15=OE p4=1Y")]
    [InlineData(typeof(Ic74251), "p4=D0 p5=Y p6=W p7=OE p9=A0 p12=D7")]
    public void EachIsOnItsDatasheetPinout(Type type, string expected)
    {
        var ic = (DigitalIc)Activator.CreateInstance(type)!;

        foreach (var pair in expected.Split(' '))
        {
            var (id, name) = (pair.Split('=')[0], pair.Split('=')[1]);
            Assert.Equal(name, ic.Terminals.Single(t => t.Id == id).Name);
        }

        Assert.Equal(ic.PinCount, ic.Terminals.Count);
        Assert.Equal(ic.PinCount, ic.Terminals.Select(t => t.Id).Distinct().Count());
    }

    /// <summary>Every one of the sixteen codes: one output selected for a valid one, none past the last.</summary>
    [Theory]
    [InlineData(typeof(Ic7442))]
    [InlineData(typeof(Ic74154))]
    [InlineData(typeof(Ic4028))]
    public void EachCodeSelectsItsOutput(Type type)
    {
        var ic = (LineDecoderIc)Activator.CreateInstance(type)!;
        var bench = new LogicBench { ToggleLevels = ic.IsActiveLow ? LogicLevels.Ttl : LogicLevels.Cmos5V };
        bench.Add(ic);
        var address = bench.Toggles(ic.Address);
        foreach (var e in ic.Enables) bench.Tie(e, false);

        var none = ic.IsActiveLow ? (1 << ic.Outputs.Count) - 1 : 0;

        for (var code = 0; code < 16; code++)
        {
            LogicBench.Set(address, code);
            bench.Run();

            var expected = code < ic.Outputs.Count ? none ^ (1 << code) : none;
            Assert.Equal(expected, Word(bench, ic.Outputs));
            Assert.Equal(code < ic.Outputs.Count ? code : -1, ic.SelectedOutput);
        }
    }

    /// <summary>Wider than <see cref="LogicBench.Byte"/> goes: the 74154 has sixteen outputs.</summary>
    private static int Word(LogicBench bench, IReadOnlyList<Cirq.Core.Topology.Terminal> pins) =>
        pins.Select((p, i) => bench.High(p) ? 1 << i : 0).Sum();

    /// <summary>Either 74154 enable high and all sixteen outputs stay high.</summary>
    [Fact]
    public void The74154NeedsBothEnablesLow()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74154());
        bench.Toggles(ic.Address, 9);
        var g1 = bench.Toggle(ic.Enables[0], true);
        var g2 = bench.Toggle(ic.Enables[1]);

        bench.Run();
        Assert.Equal(0xFFFF, Word(bench, ic.Outputs));

        g1.State = false;
        bench.Run();
        Assert.Equal(0xFFFF ^ (1 << 9), Word(bench, ic.Outputs));

        g2.State = true;
        bench.Run();
        Assert.Equal(0xFFFF, Word(bench, ic.Outputs));
    }

    /// <summary>Each 74153 half picks its own input by the shared selects, and a high strobe forces it low.</summary>
    [Fact]
    public void The74153HalvesShareTheirSelects()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74153());
        var a = bench.Toggle(ic.SelectA);
        var b = bench.Toggle(ic.SelectB);
        bench.Toggles(ic.Data(0), 0b1010);
        bench.Toggles(ic.Data(1), 0b0110);
        var g1 = bench.Toggle(ic.Strobe(0));
        bench.Tie(ic.Strobe(1), false);

        for (var select = 0; select < 4; select++)
        {
            a.State = (select & 1) != 0;
            b.State = (select & 2) != 0;
            bench.Run();
            Assert.Equal((0b1010 >> select & 1) == 1, bench.High(ic.Output(0)));
            Assert.Equal((0b0110 >> select & 1) == 1, bench.High(ic.Output(1)));
        }

        g1.State = true;
        bench.Run();
        Assert.False(bench.High(ic.Output(0)));

        // The other half carries on: input 3 of 2C0-2C3 = 0110 is a 0, and input 2 a 1.
        Assert.False(bench.High(ic.Output(1)));
        a.State = false;
        bench.Run();
        Assert.True(bench.High(ic.Output(1)));
    }

    /// <summary>The quad 2-to-1 parts pick A or B, the 158 inverting, and the strobe does what each part's strobe does.</summary>
    [Theory]
    [InlineData(typeof(Ic74157))]
    [InlineData(typeof(Ic74158))]
    [InlineData(typeof(Ic74257))]
    public void AQuadTwoToOneMuxPicksAOrB(Type type)
    {
        var bench = new LogicBench();
        var ic = bench.Add((QuadTwoToOneMultiplexerIc)Activator.CreateInstance(type)!);
        var select = bench.Toggle(ic.Select);
        var strobe = bench.Toggle(ic.Strobe);
        bench.Toggles(ic.A, 0b0011);
        bench.Toggles(ic.B, 0b0101);
        var invert = ic.IsInverting ? 0xF : 0;

        bench.Run();
        Assert.Equal(0b0011 ^ invert, bench.Byte(ic.Outputs));

        select.State = true;
        bench.Run();
        Assert.Equal(0b0101 ^ invert, bench.Byte(ic.Outputs));

        strobe.State = true;
        bench.Run();

        if (ic.IsTriState)
            Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(i)));
        else
            Assert.Equal(invert, bench.Byte(ic.Outputs));
    }

    /// <summary>
    /// A 74251 does the 74151's job while OE is low and lets go of both outputs while it is high — so
    /// two of them on one wire, enabled in turn, make a 16-to-1 multiplexer.
    /// </summary>
    [Fact]
    public void Two74251sOnOneWireMakeASixteenToOneMultiplexer()
    {
        var bench = new LogicBench();
        var low = bench.Add(new Ic74251());
        var high = bench.Add(new Ic74251());
        bench.Toggles(low.Data, 0b0010_0001);
        bench.Toggles(high.Data, 0b1000_0000);

        var select = new[] { bench.Toggle(low.SelectA), bench.Toggle(low.SelectB), bench.Toggle(low.SelectC) };
        bench.Circuit.Connect(low.SelectA, high.SelectA);
        bench.Circuit.Connect(low.SelectB, high.SelectB);
        bench.Circuit.Connect(low.SelectC, high.SelectC);

        // The fourth address bit enables one and, through an inverter, disables the other.
        var inverter = bench.Add(new Ic7404());
        var (inA, inY) = inverter.Inverter(0);
        var top = bench.Toggle(high.Strobe);
        bench.Circuit.Connect(high.Strobe, inA);
        bench.Circuit.Connect(inY, low.Strobe);

        bench.Circuit.Connect(low.Output, high.Output);
        bench.Circuit.Connect(low.InvertedOutput, high.InvertedOutput);

        for (var code = 0; code < 16; code++)
        {
            LogicBench.Set(select, code & 7);
            top.State = code < 8;   // high disables the 'high' part, which is enabled for codes 8-15
            bench.Run();

            var expected = code < 8 ? (0b0010_0001 >> code & 1) == 1 : (0b1000_0000 >> (code - 8) & 1) == 1;
            Assert.Equal(expected, bench.High(low.Output));
            Assert.Equal(!expected, bench.High(low.InvertedOutput));
        }
    }
}
