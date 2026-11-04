using Cirq.Components.Digital;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// The counters: the 7493, 74390 and 74393 ripple counters beside the 7490, the 74160, 74162 and
/// 74163 beside the 74161, and the CMOS 4020, 4022, 4024, 4029, 4510, 4516, 4518 and 4520 —
/// pinouts, sequences, the edge each counts on, and how each is cleared, loaded and chained.
/// </summary>
public class CounterFamilyTests
{
    /// <summary>A whole clock cycle, ending on the level it started at.</summary>
    private static void Pulse(LogicBench bench, LogicToggle clock)
    {
        clock.State = !clock.State;
        bench.Run();
        clock.State = !clock.State;
        bench.Run();
    }

    [Theory]
    [InlineData(typeof(Ic7493), "p1=CKB p2=R0(1) p3=R0(2) p4=NC p5=VCC p6=NC p7=NC p8=QC p9=QB p10=GND p11=QD p12=QA p13=NC p14=CKA")]
    [InlineData(typeof(Ic74390), "p1=1CKA p2=1CLR p3=1QA p4=1CKB p5=1QB p6=1QC p7=1QD p8=GND p9=2QD p10=2QC p11=2QB p12=2CKB p13=2QA p14=2CLR p15=2CKA p16=VCC")]
    [InlineData(typeof(Ic74393), "p1=1CLK p2=1CLR p3=1QA p4=1QB p5=1QC p6=1QD p7=GND p8=2QD p9=2QC p10=2QB p11=2QA p12=2CLR p13=2CLK p14=VCC")]
    [InlineData(typeof(Ic74163), "p1=MR p2=CP p3=D0 p6=D3 p7=CEP p8=GND p9=PE p10=CET p11=Q3 p14=Q0 p15=TC p16=VCC")]
    [InlineData(typeof(Ic4020), "p1=Q12 p2=Q13 p3=Q14 p4=Q6 p5=Q5 p6=Q7 p7=Q4 p8=VSS p9=Q1 p10=CLK p11=RST p12=Q9 p13=Q8 p14=Q10 p15=Q11 p16=VDD")]
    [InlineData(typeof(Ic4024), "p1=CLK p2=RST p3=Q7 p4=Q6 p5=Q5 p6=Q4 p7=VSS p8=NC p9=Q3 p10=NC p11=Q2 p12=Q1 p13=NC p14=VDD")]
    [InlineData(typeof(Ic4040), "p1=Q12 p2=Q6 p3=Q5 p4=Q7 p5=Q4 p6=Q3 p7=Q2 p8=VSS p9=Q1 p10=CLK p11=RST p12=Q9 p13=Q8 p14=Q10 p15=Q11 p16=VDD")]
    [InlineData(typeof(Ic4017), "p1=Q5 p2=Q1 p3=Q0 p4=Q2 p5=Q6 p6=Q7 p7=Q3 p8=VSS p9=Q8 p10=Q4 p11=Q9 p12=CO p13=INH p14=CLK p15=RST p16=VDD")]
    [InlineData(typeof(Ic4022), "p1=Q1 p2=Q0 p3=Q2 p4=Q5 p5=Q6 p6=NC p7=Q3 p8=VSS p9=NC p10=Q7 p11=Q4 p12=CO p13=INH p14=CLK p15=RST p16=VDD")]
    [InlineData(typeof(Ic4029), "p1=PE p2=Q4 p3=P4 p4=P1 p5=CI p6=Q1 p7=CO p8=VSS p9=B/D p10=U/D p11=Q2 p12=P2 p13=P3 p14=Q3 p15=CLK p16=VDD")]
    [InlineData(typeof(Ic4510), "p9=RST p10=U/D p15=CLK")]
    [InlineData(typeof(Ic4518), "p1=1CLK p2=1EN p3=1Q1 p6=1Q4 p7=1RST p8=VSS p9=2CLK p10=2EN p11=2Q1 p14=2Q4 p15=2RST p16=VDD")]
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

    /// <summary>QA into CKB, the 7493 counts 0 to 15 on falling edges and both resets clear it.</summary>
    [Fact]
    public void The7493CountsSixteenAndClears()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic7493());
        var clock = bench.Toggle(ic.ClockA, true);
        bench.Circuit.Connect(ic.Qa, ic.ClockB);
        var r1 = bench.Toggle(ic.Reset0A);
        bench.Toggle(ic.Reset0B, true);

        bench.Run();
        for (var n = 1; n <= 20; n++)
        {
            Pulse(bench, clock);
            Assert.Equal(n % 16, bench.Byte(ic.Outputs));
        }

        r1.State = true;
        bench.Run();
        Assert.Equal(0, bench.Byte(ic.Outputs));
    }

    /// <summary>Each half of a 74390 is a decade counter, and its own clear (active high) touches only it.</summary>
    [Fact]
    public void Each74390HalfCountsInBcd()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74390());
        var clocks = new LogicToggle[2];
        var clears = new LogicToggle[2];

        for (var half = 0; half < 2; half++)
        {
            clocks[half] = bench.Toggle(ic.ClockA(half), true);
            bench.Circuit.Connect(ic.Outputs(half)[0], ic.ClockB(half)!);
            clears[half] = bench.Toggle(ic.Clear(half));
        }

        bench.Run();
        for (var n = 1; n <= 12; n++)
        {
            Pulse(bench, clocks[0]);
            Assert.Equal(n % 10, bench.Byte(ic.Outputs(0)));
        }

        Pulse(bench, clocks[1]);
        Pulse(bench, clocks[1]);
        Assert.Equal(2, ic.Count(1));

        clears[0].State = true;
        bench.Run();
        Assert.Equal(0, bench.Byte(ic.Outputs(0)));
        Assert.Equal(2, bench.Byte(ic.Outputs(1)));
    }

    /// <summary>A 74393 half counts sixteen with one clock, and two halves in series divide by 256.</summary>
    [Fact]
    public void Two74393HalvesDivideBy256()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74393());
        var clock = bench.Toggle(ic.ClockA(0), true);
        bench.Circuit.Connect(ic.Outputs(0)[3], ic.ClockA(1));
        bench.Tie(ic.Clear(0), false);
        bench.Tie(ic.Clear(1), false);
        Assert.Null(ic.ClockB(0));

        bench.Run();
        for (var n = 1; n <= 300; n++)
        {
            Pulse(bench, clock);
            Assert.Equal(n % 16, ic.Count(0));
        }

        Assert.Equal(300 % 256, ic.Count(0) | (ic.Count(1) << 4));
        Assert.Equal((300 % 256) >> 4, bench.Byte(ic.Outputs(1)));
    }

    /// <summary>The decade parts count 0-9 and carry at 9; the binary ones carry at 15.</summary>
    [Theory]
    [InlineData(typeof(Ic74160), 10)]
    [InlineData(typeof(Ic74161), 16)]
    [InlineData(typeof(Ic74162), 10)]
    [InlineData(typeof(Ic74163), 16)]
    public void ASynchronousCounterCountsItsModulusAndCarriesAtTheTop(Type type, int modulus)
    {
        var bench = new LogicBench();
        var ic = bench.Add((SynchronousCounterIc)Activator.CreateInstance(type)!);
        var clock = bench.Toggle(ic.Clock);
        bench.Tie(ic.MasterReset, true);
        bench.Tie(ic.ParallelEnable, true);
        bench.Tie(ic.CountEnableP, true);
        bench.Tie(ic.CountEnableT, true);
        bench.Toggles(ic.Data);

        bench.Run();
        for (var n = 1; n <= 2 * modulus + 3; n++)
        {
            Pulse(bench, clock);
            Assert.Equal(n % modulus, bench.Byte(ic.Outputs));
            Assert.Equal(n % modulus == modulus - 1, bench.High(ic.TerminalCount));
        }
    }

    /// <summary>
    /// The 74160 and 74161 clear the moment MR goes low; the 74162 and 74163 wait for the next edge,
    /// which is what makes them the ones to truncate a count with.
    /// </summary>
    [Theory]
    [InlineData(typeof(Ic74161), false)]
    [InlineData(typeof(Ic74160), false)]
    [InlineData(typeof(Ic74163), true)]
    [InlineData(typeof(Ic74162), true)]
    public void TheClearIsAsynchronousOrWaitsForTheClock(Type type, bool synchronous)
    {
        var bench = new LogicBench();
        var ic = bench.Add((SynchronousCounterIc)Activator.CreateInstance(type)!);
        Assert.Equal(synchronous, ic.HasSynchronousReset);

        var clock = bench.Toggle(ic.Clock);
        var reset = bench.Toggle(ic.MasterReset, true);
        bench.Tie(ic.ParallelEnable, true);
        bench.Tie(ic.CountEnableP, true);
        bench.Tie(ic.CountEnableT, true);
        bench.Toggles(ic.Data);

        bench.Run();
        for (var n = 0; n < 5; n++) Pulse(bench, clock);
        Assert.Equal(5, bench.Byte(ic.Outputs));

        reset.State = false;
        bench.Run();
        Assert.Equal(synchronous ? 5 : 0, bench.Byte(ic.Outputs));

        Pulse(bench, clock);
        Assert.Equal(0, bench.Byte(ic.Outputs));
    }

    /// <summary>
    /// A 74163 truncated to divide by six: five (QA and QC) decoded by a NAND into MR, and the next
    /// edge clears it — five is shown for a whole clock, with no glimpse of six.
    /// </summary>
    [Fact]
    public void A74163WithFiveDecodedIntoClearCountsZeroToFive()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74163());
        var nand = bench.Add(new Ic7400());
        var clock = bench.Toggle(ic.Clock);
        bench.Tie(ic.ParallelEnable, true);
        bench.Tie(ic.CountEnableP, true);
        bench.Tie(ic.CountEnableT, true);
        bench.Toggles(ic.Data);

        var (a, b, y) = nand.Gate(0);
        bench.Circuit.Connect(ic.Outputs[0], a);
        bench.Circuit.Connect(ic.Outputs[2], b);
        bench.Circuit.Connect(y, ic.MasterReset);

        bench.Run();
        var seen = new List<int>();
        for (var n = 0; n < 12; n++)
        {
            Pulse(bench, clock);
            seen.Add(bench.Byte(ic.Outputs));
        }

        Assert.Equal([1, 2, 3, 4, 5, 0, 1, 2, 3, 4, 5, 0], seen);
    }

    /// <summary>The CMOS ripple counters divide by two per stage on falling edges; reset is active high.</summary>
    [Theory]
    [InlineData(typeof(Ic4020), 14)]
    [InlineData(typeof(Ic4024), 7)]
    [InlineData(typeof(Ic4040), 12)]
    public void ACmosRippleCounterDividesOnFallingEdges(Type type, int stages)
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add((CmosRippleCounterIc)Activator.CreateInstance(type)!);
        var clock = bench.Toggle(ic.Clock, true);
        var reset = bench.Toggle(ic.Reset);

        bench.Run();
        for (var n = 1; n <= 37; n++) Pulse(bench, clock);

        Assert.Equal(37, ic.Count);
        for (var i = 0; i < ic.Outputs.Count; i++)
            Assert.Equal((37 >> (ic.Stages[i] - 1) & 1) == 1, bench.High(ic.Outputs[i]));

        Assert.Equal(stages, ic.Stages.Max());

        reset.State = true;
        bench.Run();
        Assert.Equal(0, ic.Count);
        Assert.All(ic.Outputs, q => Assert.False(bench.High(q)));
    }

    /// <summary>The 4022 walks one high output along eight, with carry high for the first four.</summary>
    [Fact]
    public void The4022WalksEightOutputs()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4022());
        var clock = bench.Toggle(ic.Clock);
        var inhibit = bench.Toggle(ic.ClockInhibit);
        bench.Tie(ic.Reset, false);

        bench.Run();
        for (var n = 0; n < 17; n++)
        {
            Assert.Equal(1 << (n % 8), bench.Byte(ic.Outputs));
            Assert.Equal(n % 8 < 4, bench.High(ic.CarryOut));
            Pulse(bench, clock);
        }

        inhibit.State = true;
        bench.Run();
        Pulse(bench, clock);
        Assert.Equal(1, ic.Count);
    }

    /// <summary>
    /// The 4029 up and down in both moduli, its carry out low at the end of the count in the direction
    /// it is going, and its preset loading at once.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The4029CountsBothWaysInEitherModulus(bool binary)
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4029());
        var clock = bench.Toggle(ic.Clock);
        var up = bench.Toggle(ic.UpDown, true);
        var preset = bench.Toggle(ic.PresetEnable);
        bench.Toggle(ic.ModeOrReset, binary);
        bench.Tie(ic.CarryIn, false);
        bench.Toggles(ic.Preset, 7);

        var modulus = binary ? 16 : 10;
        bench.Run();

        for (var n = 1; n <= modulus + 2; n++)
        {
            Pulse(bench, clock);
            Assert.Equal(n % modulus, bench.Byte(ic.Outputs));
            Assert.Equal(n % modulus != modulus - 1, bench.High(ic.CarryOut));
        }

        up.State = false;
        bench.Run();
        for (var n = 1; n <= 4; n++) Pulse(bench, clock);
        Assert.Equal((2 - 4 + modulus) % modulus, ic.Count);

        preset.State = true;
        bench.Run();
        Assert.Equal(7, bench.Byte(ic.Outputs));
        Pulse(bench, clock);
        Assert.Equal(7, ic.Count);
    }

    /// <summary>
    /// Two 4510s on one clock, the first's carry out into the second's carry in, count 00 to 99 in
    /// step — and reset, active high, clears both at once.
    /// </summary>
    [Fact]
    public void Two4510sCascadeOnCarryToCountToNinetyNine()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var units = bench.Add(new Ic4510());
        var tens = bench.Add(new Ic4510());
        var clock = bench.Toggle(units.Clock);
        bench.Circuit.Connect(units.Clock, tens.Clock);
        var reset = bench.Toggle(units.ModeOrReset);
        bench.Circuit.Connect(units.ModeOrReset, tens.ModeOrReset);
        bench.Tie(units.CarryIn, false);
        bench.Circuit.Connect(units.CarryOut, tens.CarryIn);

        foreach (var ic in new[] { units, tens })
        {
            bench.Tie(ic.UpDown, true);
            bench.Tie(ic.PresetEnable, false);
            foreach (var p in ic.Preset) bench.Tie(p, false);
        }

        bench.Run();
        for (var n = 1; n <= 105; n++)
        {
            Pulse(bench, clock);
            Assert.Equal(n % 100, (10 * tens.Count) + units.Count);
        }

        reset.State = true;
        bench.Run();
        Assert.Equal(0, units.Count + tens.Count);
    }

    /// <summary>The 4516 counts 0-15 down from zero to fifteen, its carry low at zero going down.</summary>
    [Fact]
    public void The4516CountsDownThroughZero()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4516());
        var clock = bench.Toggle(ic.Clock);
        bench.Tie(ic.UpDown, false);
        bench.Tie(ic.ModeOrReset, false);
        bench.Tie(ic.PresetEnable, false);
        bench.Tie(ic.CarryIn, false);
        foreach (var p in ic.Preset) bench.Tie(p, false);

        bench.Run();
        Assert.False(bench.High(ic.CarryOut));
        Pulse(bench, clock);
        Assert.Equal(15, bench.Byte(ic.Outputs));
        Assert.True(bench.High(ic.CarryOut));
    }

    /// <summary>
    /// A 4518 half counts on CLOCK rising with ENABLE high, or on ENABLE falling with CLOCK low — so Q4
    /// of the units into ENABLE of the tens counts to 99 with the tens' clock held low.
    /// </summary>
    [Theory]
    [InlineData(typeof(Ic4518), 10)]
    [InlineData(typeof(Ic4520), 16)]
    public void TheDualCountersCascadeThroughEnable(Type type, int modulus)
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add((DualUpCounterIc)Activator.CreateInstance(type)!);
        var clock = bench.Toggle(ic.Clock(0));
        bench.Tie(ic.Enable(0), true);
        bench.Tie(ic.Clock(1), false);
        bench.Circuit.Connect(ic.Outputs(0)[3], ic.Enable(1));
        bench.Tie(ic.Reset(0), false);
        bench.Tie(ic.Reset(1), false);

        bench.Run();
        for (var n = 1; n <= modulus * 3 + 2; n++)
        {
            Pulse(bench, clock);
            Assert.Equal(n % modulus, bench.Byte(ic.Outputs(0)));
            Assert.Equal(n / modulus % modulus, ic.Count(1));
        }
    }
}
