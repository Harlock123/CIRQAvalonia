using Cirq.Components.Digital;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// The dual JK packages (7473, 74107, 74109, 74112, 4027), the 7475 and 4042 latches and the 74174,
/// 74175 and 74273 registers: pinouts, the edge each acts on, the JK table, and the asynchronous pins.
/// </summary>
public class FlipFlopFamilyTests
{
    [Theory]
    [InlineData(typeof(Ic7473), "p1=1CLK p2=1CLR p3=1K p4=VCC p5=2CLK p6=2CLR p7=2J p8=2Q' p9=2Q p10=2K p11=GND p12=1Q p13=1Q' p14=1J")]
    [InlineData(typeof(Ic74107), "p1=1J p2=1Q' p3=1Q p4=1K p5=2Q p6=2Q' p7=GND p8=2J p9=2CLK p10=2CLR p11=2K p12=1CLK p13=1CLR p14=VCC")]
    [InlineData(typeof(Ic74109), "p1=1CLR p2=1J p3=1K' p4=1CLK p5=1PRE p6=1Q p7=1Q' p8=GND p9=2Q' p10=2Q p11=2PRE p12=2CLK p13=2K' p14=2J p15=2CLR p16=VCC")]
    [InlineData(typeof(Ic74112), "p1=1CLK p2=1K p3=1J p4=1PRE p5=1Q p6=1Q' p7=2Q' p8=GND p9=2Q p10=2PRE p11=2J p12=2K p13=2CLK p14=2CLR p15=1CLR p16=VCC")]
    [InlineData(typeof(Ic4027), "p1=1Q p2=1Q' p3=1CLK p4=1RST p5=1K p6=1J p7=1SET p8=VSS p9=2SET p10=2J p11=2K p12=2RST p13=2CLK p14=2Q' p15=2Q p16=VDD")]
    [InlineData(typeof(Ic7476), "p1=1CLK p5=VCC p12=GND p16=1K")]
    [InlineData(typeof(Ic7475), "p1=1Q' p2=1D p3=2D p4=3-4C p5=VCC p6=3D p7=4D p8=4Q' p9=4Q p10=3Q p11=3Q' p12=GND p13=1-2C p14=2Q' p15=2Q p16=1Q")]
    [InlineData(typeof(Ic4042), "p1=4Q p2=1Q p3=1Q' p4=1D p5=CLOCK p6=POLARITY p7=2D p8=VSS p9=2Q' p10=2Q p11=3Q p12=3Q' p13=3D p14=4D p15=4Q' p16=VDD")]
    [InlineData(typeof(Ic74174), "p1=CLR p2=1Q p3=1D p4=2D p5=2Q p6=3D p7=3Q p8=GND p9=CLK p10=4Q p11=4D p12=5Q p13=5D p14=6D p15=6Q p16=VCC")]
    [InlineData(typeof(Ic74175), "p1=CLR p2=1Q p3=1Q' p4=1D p5=2D p6=2Q' p7=2Q p8=GND p9=CLK p10=3Q p11=3Q' p12=3D p13=4D p14=4Q' p15=4Q p16=VCC")]
    [InlineData(typeof(Ic74273), "p1=CLR p2=1Q p3=1D p4=2D p5=2Q p6=3Q p7=3D p8=4D p9=4Q p10=GND p11=CLK p12=5Q p13=5D p14=6D p15=6Q p16=7Q p17=7D p18=8D p19=8Q p20=VCC")]
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

    /// <summary>
    /// The JK table on the part's own edge, both sections: set, hold, reset, toggle, toggle — and an
    /// edge the wrong way does nothing.
    /// </summary>
    [Theory]
    [InlineData(typeof(Ic7473))]
    [InlineData(typeof(Ic74107))]
    [InlineData(typeof(Ic74109))]
    [InlineData(typeof(Ic74112))]
    [InlineData(typeof(Ic7476))]
    [InlineData(typeof(Ic4027))]
    public void EachSectionFollowsTheJkTableOnItsOwnEdge(Type type)
    {
        var ic = (DualJkFlipFlopIc)Activator.CreateInstance(type)!;
        var bench = new LogicBench { ToggleLevels = ic.AsyncActiveHigh ? LogicLevels.Cmos5V : LogicLevels.Ttl };
        bench.Add(ic);

        var toggles = new (LogicToggle Clock, LogicToggle J, LogicToggle K)[2];

        for (var section = 0; section < 2; section++)
        {
            var (clockPin, preset, clear, jPin, kPin, _, _) = ic.Pins(section);

            // Asynchronous pins released: high on TTL, low on the 4027.
            if (preset is not null) bench.Tie(preset, !ic.AsyncActiveHigh);
            if (clear is not null) bench.Tie(clear, !ic.AsyncActiveHigh);

            // At rest the clock sits on the level it leaves when it acts.
            toggles[section] = (bench.Toggle(clockPin, !ic.IsRisingEdge), bench.Toggle(jPin), bench.Toggle(kPin, ic.HasInvertedK));
        }

        bench.Run();

        for (var section = 0; section < 2; section++)
        {
            var (_, _, _, _, _, q, qNot) = ic.Pins(section);
            var (clock, j, k) = toggles[section];

            void Clock(bool jHigh, bool kHigh)
            {
                j.State = jHigh;
                k.State = kHigh ^ ic.HasInvertedK;
                bench.Run();
                clock.State = !clock.State;   // the edge that acts
                bench.Run();
                clock.State = !clock.State;   // back, which must change nothing
                bench.Run();
            }

            Clock(true, false);
            Assert.True(bench.High(q));
            Assert.False(bench.High(qNot));

            Clock(false, false);
            Assert.True(bench.High(q));

            Clock(false, true);
            Assert.False(bench.High(q));

            Clock(true, true);
            Assert.True(bench.High(q));
            Clock(true, true);
            Assert.False(bench.High(q));
            Assert.True(bench.High(qNot));
        }
    }

    /// <summary>Preset and clear beat the clock, and both at once leave both outputs high.</summary>
    [Theory]
    [InlineData(typeof(Ic74109))]
    [InlineData(typeof(Ic74112))]
    [InlineData(typeof(Ic4027))]
    public void PresetAndClearAreAsynchronous(Type type)
    {
        var ic = (DualJkFlipFlopIc)Activator.CreateInstance(type)!;
        var bench = new LogicBench { ToggleLevels = ic.AsyncActiveHigh ? LogicLevels.Cmos5V : LogicLevels.Ttl };
        bench.Add(ic);

        var (clockPin, preset, clear, j, k, q, qNot) = ic.Pins(0);
        var off = !ic.AsyncActiveHigh;
        var pre = bench.Toggle(preset!, off);
        var clr = bench.Toggle(clear!, !off);
        bench.Tie(clockPin, false);
        bench.Tie(j, false);
        bench.Tie(k, ic.HasInvertedK);

        // Power-on is a race on TTL, whose toggles start low, so start from a clear.
        bench.Run();
        clr.State = off;
        bench.Run();
        Assert.False(bench.High(q));

        pre.State = !off;
        bench.Run();
        Assert.True(bench.High(q));

        pre.State = off;
        bench.Run();
        Assert.True(bench.High(q));

        clr.State = !off;
        bench.Run();
        Assert.False(bench.High(q));

        pre.State = !off;
        bench.Run();
        Assert.True(bench.High(q));
        Assert.True(bench.High(qNot));
    }

    /// <summary>Tied together, the 74109's J and K' make a D flip-flop.</summary>
    [Fact]
    public void A74109WithJTiedToKBarIsADFlipFlop()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74109());
        var (clockPin, preset, clear, j, k, q, _) = ic.Pins(0);
        bench.Tie(preset!, true);
        bench.Tie(clear!, true);
        var clock = bench.Toggle(clockPin);
        var d = bench.Toggle(j);
        bench.Circuit.Connect(j, k);

        foreach (var bit in new[] { true, true, false, true, false, false })
        {
            d.State = bit;
            bench.Run();
            clock.State = true;
            bench.Run();
            clock.State = false;
            bench.Run();
            Assert.Equal(bit, bench.High(q));
        }
    }

    /// <summary>The 7475's pairs open on their own enables, and hold what D was as the enable fell.</summary>
    [Fact]
    public void The7475LatchesInPairs()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic7475());
        var data = bench.Toggles(ic.Data, 0b1111);
        var e12 = bench.Toggle(ic.Enable12, true);
        var e34 = bench.Toggle(ic.Enable34);

        bench.Run();
        Assert.Equal(0b0011, bench.Byte(ic.Outputs));
        Assert.Equal(0b1100, bench.Byte(ic.InvertedOutputs));

        e12.State = false;
        bench.Run();
        LogicBench.Set(data, 0b0000);
        bench.Run();
        Assert.Equal(0b0011, bench.Byte(ic.Outputs));

        e34.State = true;
        LogicBench.Set(data, 0b1010);
        bench.Run();
        Assert.Equal(0b1011, bench.Byte(ic.Outputs));
    }

    /// <summary>The 4042 is open while CLOCK equals POLARITY, either way up.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The4042IsTransparentWhileClockMatchesPolarity(bool polarity)
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4042());
        var data = bench.Toggles(ic.Data, 0b0110);
        var clock = bench.Toggle(ic.Clock, polarity);
        bench.Toggle(ic.Polarity, polarity);

        bench.Run();
        Assert.Equal(0b0110, bench.Byte(ic.Outputs));

        clock.State = !polarity;
        bench.Run();
        LogicBench.Set(data, 0b1001);
        bench.Run();
        Assert.Equal(0b0110, bench.Byte(ic.Outputs));
        Assert.Equal(0b1001, bench.Byte(ic.InvertedOutputs));

        clock.State = polarity;
        bench.Run();
        Assert.Equal(0b1001, bench.Byte(ic.Outputs));
    }

    /// <summary>Every bit of each register taken on one rising edge, and emptied at once by /CLR.</summary>
    [Theory]
    [InlineData(typeof(Ic74174), 0b101101)]
    [InlineData(typeof(Ic74175), 0b1001)]
    [InlineData(typeof(Ic74273), 0xA6)]
    public void ARegisterTakesItsInputsOnTheRisingEdgeAndClearsAtOnce(Type type, int value)
    {
        var bench = new LogicBench();
        var ic = bench.Add((ClockedRegisterIc)Activator.CreateInstance(type)!);
        var clear = bench.Toggle(ic.Clear, true);
        var clock = bench.Toggle(ic.Clock);
        var data = bench.Toggles(ic.Data, value);

        bench.Run();
        Assert.Equal(0, bench.Byte(ic.Outputs));

        clock.State = true;
        bench.Run();
        Assert.Equal(value, bench.Byte(ic.Outputs));
        if (ic.HasComplements) Assert.Equal(~value & 0xF, bench.Byte(ic.InvertedOutputs));

        // Inputs moving with the clock high or falling change nothing.
        LogicBench.Set(data, 0);
        bench.Run();
        clock.State = false;
        bench.Run();
        Assert.Equal(value, bench.Byte(ic.Outputs));

        // Clear needs no clock.
        clear.State = false;
        bench.Run();
        Assert.Equal(0, bench.Byte(ic.Outputs));
        Assert.Equal(0, ic.Value);
    }
}
