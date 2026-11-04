using Cirq.Components.Digital;
using Cirq.Components.Nonlinear;
using Cirq.Components.Passive;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// The 7448 and 74247 beside the 7447, the 4543 LCD driver, and the 4026 and 4033 counters with a
/// display driver inside: glyphs (with and without tails), output stages, blanking and chaining.
/// </summary>
public class DisplayDriverTests
{
    private static void Pulse(LogicBench bench, LogicToggle clock)
    {
        clock.State = !clock.State;
        bench.Run();
        clock.State = !clock.State;
        bench.Run();
    }

    /// <summary>Segments as a pattern, bit 6 = a, reading a lit segment as <paramref name="litHigh"/>.</summary>
    private static int Pattern(LogicBench bench, IReadOnlyList<Cirq.Core.Topology.Terminal> segments, bool litHigh) =>
        Enumerable.Range(0, 7).Sum(i => bench.High(segments[i]) == litHigh ? 1 << (6 - i) : 0);

    [Fact]
    public void Only6And9DifferBetweenTheTwoGlyphSets()
    {
        for (var digit = 0; digit <= 9; digit++)
        {
            var differ = SevenSegmentGlyphs.For(digit, tails: true) != SevenSegmentGlyphs.For(digit, tails: false);
            Assert.Equal(digit is 6 or 9, differ);
            Assert.Equal(digit, SevenSegmentGlyphs.DigitOf(SevenSegmentGlyphs.For(digit, tails: true)));
            Assert.Equal(digit, SevenSegmentGlyphs.DigitOf(SevenSegmentGlyphs.For(digit, tails: false)));
        }

        // A tail is one more segment: a on the 6, d on the 9.
        Assert.Equal(0b1000000, SevenSegmentGlyphs.For(6, true) ^ SevenSegmentGlyphs.For(6, false));
        Assert.Equal(0b0001000, SevenSegmentGlyphs.For(9, true) ^ SevenSegmentGlyphs.For(9, false));
    }

    /// <summary>
    /// Every value 0-15 through each TTL decoder: the 7447 and 74247 pull lit segments low, the 7448
    /// lets them up to its pull-ups and pulls the rest low, and the 74247's 6 and 9 have tails.
    /// </summary>
    [Theory]
    [InlineData(typeof(Ic7447), false, false)]
    [InlineData(typeof(Ic74247), true, false)]
    [InlineData(typeof(Ic7448), false, true)]
    public void EachTtlDecoderShowsItsGlyphs(Type type, bool tails, bool activeHigh)
    {
        var bench = new LogicBench();
        var ic = bench.Add((TtlSevenSegmentDecoderIc)Activator.CreateInstance(type)!);
        var bcd = bench.Toggles([ic.InputA, ic.InputB, ic.InputC, ic.InputD]);
        bench.Tie(ic.LampTest, true);
        bench.Tie(ic.BlankingInput, true);
        bench.Tie(ic.RippleBlankingInput, true);

        // The open-collector parts need pull-ups to read; the 7448 has its own.
        if (!activeHigh)
        {
            foreach (var segment in ic.Segments)
            {
                var pullUp = bench.Circuit.Add(new Resistor(4.7e3));
                bench.Circuit.Connect(pullUp.A, bench.Rail.Positive);
                bench.Circuit.Connect(pullUp.B, segment);
            }
        }

        for (var value = 0; value < 16; value++)
        {
            LogicBench.Set(bcd, value);
            bench.Run(1e-6);

            var expected = value < 10 ? SevenSegmentGlyphs.For(value, tails) : Ic7447.PatternFor(value);
            Assert.Equal(expected, Pattern(bench, ic.Segments, litHigh: activeHigh));
        }
    }

    /// <summary>The 7448 lights a common-cathode display through nothing but its own pull-ups — dimly.</summary>
    [Fact]
    public void A7448DrivesACommonCathodeDisplayThroughItsPullUps()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic7448());
        bench.Toggles([ic.InputA, ic.InputB, ic.InputC, ic.InputD], 6);
        bench.Tie(ic.LampTest, true);
        bench.Tie(ic.BlankingInput, true);
        bench.Tie(ic.RippleBlankingInput, true);

        var display = bench.Circuit.Add(new SevenSegmentDisplay(commonAnode: false));
        bench.Circuit.Connect(display.Common, bench.Ground.Pin);
        for (var i = 0; i < 7; i++) bench.Circuit.Connect(ic.Segments[i], display.Segments[i]);

        bench.Run(5e-6);
        Assert.Equal(6, display.DisplayedDigit);

        // Two kilohms from five volts into an LED: a couple of milliamps per segment.
        Assert.InRange(display.SegmentCurrents[2], 0.5e-3, 2.5e-3);
    }

    [Fact]
    public void The4543FollowsLatchesBlanksAndInvertsByPhase()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4543());
        Assert.Equal(["LD", "C", "B", "D", "A", "PH", "BI", "VSS", "a", "b", "c", "d", "e", "g", "f", "VDD"],
            ic.Terminals.Select(t => t.Name));

        var bcd = bench.Toggles([ic.A, ic.B, ic.C, ic.D], 9);
        var ld = bench.Toggle(ic.LatchDisable, true);
        var phase = bench.Toggle(ic.Phase);
        var blank = bench.Toggle(ic.Blanking);

        bench.Run();
        Assert.Equal(SevenSegmentGlyphs.For(9, tails: true), Pattern(bench, ic.Segments, litHigh: true));

        // Latched: the inputs move and the display does not.
        ld.State = false;
        bench.Run();
        LogicBench.Set(bcd, 2);
        bench.Run();
        Assert.Equal(9, ic.DisplayedValue);

        // Phase high inverts every output: what a common-anode display, or an LCD's backplane, wants.
        phase.State = true;
        bench.Run();
        Assert.Equal(SevenSegmentGlyphs.For(9, tails: true), Pattern(bench, ic.Segments, litHigh: false));

        blank.State = true;
        bench.Run();
        Assert.Equal(0b1111111, Pattern(bench, ic.Segments, litHigh: true));

        // Codes above nine blank too.
        blank.State = false;
        ld.State = true;
        LogicBench.Set(bcd, 12);
        bench.Run();
        Assert.Equal(0, Pattern(bench, ic.Segments, litHigh: false));
    }

    [Theory]
    [InlineData(typeof(Ic4026), "p1=CLK p2=INH p3=DEI p4=DEO p5=CO p6=f p7=g p8=VSS p9=d p10=a p11=e p12=b p13=c p14=UC p15=RST p16=VDD")]
    [InlineData(typeof(Ic4033), "p3=RBI p4=RBO p14=LT")]
    public void TheDisplayCountersAreOnTheirPinouts(Type type, string expected)
    {
        var ic = (DigitalIc)Activator.CreateInstance(type)!;

        foreach (var pair in expected.Split(' '))
        {
            var (id, name) = (pair.Split('=')[0], pair.Split('=')[1]);
            Assert.Equal(name, ic.Terminals.Single(t => t.Id == id).Name);
        }
    }

    /// <summary>
    /// Two 4026s, the first's carry out into the second's clock, count and display 00 to 99: each digit
    /// shows its own count and the carry rises as the units wrap.
    /// </summary>
    [Fact]
    public void Two4026sCountAndDisplayToNinetyNine()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var units = bench.Add(new Ic4026());
        var tens = bench.Add(new Ic4026());
        var clock = bench.Toggle(units.Clock);
        bench.Circuit.Connect(units.CarryOut, tens.Clock);

        foreach (var ic in new[] { units, tens })
        {
            bench.Tie(ic.ClockInhibit, false);
            bench.Tie(ic.Reset, false);
            bench.Tie(ic.DisplayEnableIn, true);
        }

        bench.Run();
        for (var n = 1; n <= 123; n++)
        {
            Pulse(bench, clock);
            Assert.Equal(n % 100, (10 * tens.Count) + units.Count);
        }

        Assert.Equal(SevenSegmentGlyphs.For(3, tails: false), Pattern(bench, units.Segments, litHigh: true));
        Assert.Equal(SevenSegmentGlyphs.For(2, tails: false), Pattern(bench, tens.Segments, litHigh: true));
    }

    /// <summary>The 4026's display enable blanks everything but the ungated c segment.</summary>
    [Fact]
    public void The4026DisplayEnableLeavesTheUngatedSegment()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4026());
        var clock = bench.Toggle(ic.Clock);
        bench.Tie(ic.ClockInhibit, false);
        bench.Tie(ic.Reset, false);
        var enable = bench.Toggle(ic.DisplayEnableIn);

        bench.Run();
        Pulse(bench, clock);   // 1: b and c
        Assert.Equal(0, Pattern(bench, ic.Segments, litHigh: true));
        Assert.True(bench.High(ic.UngatedC));
        Assert.False(bench.High(ic.DisplayEnableOut));

        enable.State = true;
        bench.Run();
        Assert.Equal(SevenSegmentGlyphs.For(1, tails: false), Pattern(bench, ic.Segments, litHigh: true));
        Assert.True(bench.High(ic.DisplayEnableOut));

        Pulse(bench, clock);   // 2 is the one digit without c
        Assert.False(bench.High(ic.UngatedC));
    }

    /// <summary>
    /// Two 4033s, tens RBO into units RBI: a leading zero goes dark and the units zero stays lit only
    /// while the tens is not blanking — "0" never shows as "00". Lamp test lights everything.
    /// </summary>
    [Fact]
    public void The4033BlanksALeadingZero()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var tens = bench.Add(new Ic4033());
        var units = bench.Add(new Ic4033());
        var clock = bench.Toggle(units.Clock);
        bench.Circuit.Connect(units.CarryOut, tens.Clock);
        bench.Tie(tens.RippleBlankingIn, false);
        bench.Tie(units.RippleBlankingIn, true);
        var lamp = bench.Toggle(tens.LampTest);
        bench.Tie(units.LampTest, false);

        foreach (var ic in new[] { tens, units })
        {
            bench.Tie(ic.ClockInhibit, false);
            bench.Tie(ic.Reset, false);
        }

        bench.Run();
        Assert.Equal(0, Pattern(bench, tens.Segments, litHigh: true));
        Assert.False(bench.High(tens.RippleBlankingOut));
        Assert.Equal(SevenSegmentGlyphs.For(0, tails: false), Pattern(bench, units.Segments, litHigh: true));

        for (var n = 0; n < 10; n++) Pulse(bench, clock);
        Assert.Equal(SevenSegmentGlyphs.For(1, tails: false), Pattern(bench, tens.Segments, litHigh: true));
        Assert.True(bench.High(tens.RippleBlankingOut));

        lamp.State = true;
        bench.Run();
        Assert.Equal(0b1111111, Pattern(bench, tens.Segments, litHigh: true));
    }
}
