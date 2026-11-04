using Cirq.Components.Digital;
using Cirq.Core.Digital;

namespace Cirq.Components.Tests;

/// <summary>
/// The 74166, 4014 and 4021 beside the 74165, the 74194 and 74299 universal registers, the 74195 and
/// 4035 J-K̄ registers, the 4015 and the 4094: pinouts, the order bits come out in, synchronous and
/// asynchronous loads, the four modes, and the tri-state pins letting go.
/// </summary>
public class ShiftRegisterFamilyTests
{
    private static void Pulse(LogicBench bench, LogicToggle clock)
    {
        clock.State = !clock.State;
        bench.Run();
        clock.State = !clock.State;
        bench.Run();
    }

    [Theory]
    [InlineData(typeof(Ic74165), "p1=SH/LD p2=CLK p3=E p6=H p7=QH' p8=GND p9=QH p10=SER p11=A p14=D p15=CLK INH p16=VCC")]
    [InlineData(typeof(Ic74166), "p1=SER p2=A p3=B p4=C p5=D p6=CLK INH p7=CLK p8=GND p9=CLR p10=E p11=F p12=G p13=QH p14=H p15=SH/LD p16=VCC")]
    [InlineData(typeof(Ic4014), "p1=P8 p2=Q6 p3=Q8 p4=P4 p5=P3 p6=P2 p7=P1 p8=VSS p9=P/S p10=CLK p11=SER p12=Q7 p13=P5 p14=P6 p15=P7 p16=VDD")]
    [InlineData(typeof(Ic4021), "p1=P8 p3=Q8 p9=P/S p11=SER")]
    [InlineData(typeof(Ic74194), "p1=CLR p2=SR p3=A p4=B p5=C p6=D p7=SL p8=GND p9=S0 p10=S1 p11=CLK p12=QD p13=QC p14=QB p15=QA p16=VCC")]
    [InlineData(typeof(Ic74299), "p1=S0 p2=G1 p3=G2 p4=G/QG p5=E/QE p6=C/QC p7=A/QA p8=QA' p9=CLR p10=GND p11=SR p12=CLK p13=B/QB p14=D/QD p15=F/QF p16=H/QH p17=QH' p18=SL p19=S1 p20=VCC")]
    [InlineData(typeof(Ic74195), "p1=CLR p2=J p3=K' p4=A p5=B p6=C p7=D p8=GND p9=SH/LD p10=CLK p11=QD' p12=QD p13=QC p14=QB p15=QA p16=VCC")]
    [InlineData(typeof(Ic4035), "p1=Q4 p2=Q3 p3=Q2 p4=Q1 p5=T/C p6=RST p7=P/S p8=VSS p9=CLK p10=K' p11=J p12=P1 p15=P4 p16=VDD")]
    [InlineData(typeof(Ic4015), "p1=2CLK p2=2Q4 p3=1Q3 p4=1Q2 p5=1Q1 p6=1RST p7=1D p8=VSS p9=1CLK p10=1Q4 p11=2Q3 p12=2Q2 p13=2Q1 p14=2RST p15=2D p16=VDD")]
    [InlineData(typeof(Ic4094), "p1=STR p2=DATA p3=CLK p4=Q1 p7=Q4 p8=VSS p9=QS p10=Q'S p11=Q8 p12=Q7 p13=Q6 p14=Q5 p15=OE p16=VDD")]
    public void EachIsOnItsDatasheetPinout(Type type, string expected)
    {
        var ic = (DigitalIc)Activator.CreateInstance(type)!;

        foreach (var pair in expected.Split(' ', StringSplitOptions.None).Aggregate(new List<string>(), (list, word) =>
                 {
                     // "CLK INH" has a space in it; glue it back onto its pin.
                     if (word.Contains('=')) list.Add(word);
                     else list[^1] += " " + word;
                     return list;
                 }))
        {
            var (id, name) = (pair.Split('=', 2)[0], pair.Split('=', 2)[1]);
            Assert.Equal(name, ic.Terminals.Single(t => t.Id == id).Name);
        }

        Assert.Equal(ic.PinCount, ic.Terminals.Count);
        Assert.Equal(ic.PinCount, ic.Terminals.Select(t => t.Id).Distinct().Count());
    }

    /// <summary>
    /// Loaded, a parallel-in register puts its eighth input out first and its first input last, then
    /// the serial input — on the 74165 and 74166 alike.
    /// </summary>
    [Theory]
    [InlineData(typeof(Ic74165))]
    [InlineData(typeof(Ic74166))]
    public void ParallelInputsComeOutLastFirst(Type type)
    {
        var bench = new LogicBench();
        var ic = bench.Add((ParallelInSerialOutIc)Activator.CreateInstance(type)!);
        const int loaded = 0b1011_0010;

        var clock = bench.Toggle(ic.Clock);
        var load = bench.Toggle(ic.ShiftLoad);           // low: load
        bench.Toggles(ic.ParallelInputs, loaded);
        bench.Toggle(ic.SerialInput, true);
        if (ic.ClockInhibit is { } inhibit) bench.Tie(inhibit, false);
        if (ic.Clear is { } clear) bench.Tie(clear, true);

        bench.Run();
        if (!ic.LoadsAsynchronously)
        {
            Assert.Equal(0, ic.Value);   // the 74166 waits for the clock
            Pulse(bench, clock);
        }

        Assert.Equal(loaded, ic.Value);

        load.State = true;
        bench.Run();

        var seen = new List<bool>();
        for (var i = 0; i < 10; i++)
        {
            seen.Add(bench.High(ic.SerialOutputs[0]));
            Pulse(bench, clock);
        }

        // H first: 1, 0, 1, 1, 0, 0, 1, 0 — then the serial input's ones.
        Assert.Equal([true, false, true, true, false, false, true, false, true, true], seen);
    }

    /// <summary>The 74166's clear needs no clock, and its clock inhibit blocks loading as well as shifting.</summary>
    [Fact]
    public void The74166ClearsAtOnceAndInhibitBlocksItsLoad()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74166());
        var clock = bench.Toggle(ic.Clock);
        bench.Tie(ic.ShiftLoad, false);
        bench.Toggles(ic.ParallelInputs, 0xFF);
        bench.Tie(ic.SerialInput, false);
        var inhibit = bench.Toggle(ic.ClockInhibit!, true);
        var clear = bench.Toggle(ic.Clear!, true);

        bench.Run();
        Pulse(bench, clock);
        Assert.Equal(0, ic.Value);

        inhibit.State = false;
        bench.Run();
        Pulse(bench, clock);
        Assert.Equal(0xFF, ic.Value);

        clear.State = false;
        bench.Run();
        Assert.Equal(0, ic.Value);
    }

    /// <summary>The 4014 loads on the clock and the 4021 at once; both bring out stages 6, 7 and 8.</summary>
    [Theory]
    [InlineData(typeof(Ic4014))]
    [InlineData(typeof(Ic4021))]
    public void TheCmosParallelInRegistersShowStagesSixToEight(Type type)
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add((ParallelInSerialOutIc)Activator.CreateInstance(type)!);
        var clock = bench.Toggle(ic.Clock);
        var load = bench.Toggle(ic.ShiftLoad, true);    // high: load
        bench.Toggles(ic.ParallelInputs, 0b1010_0000);  // P6 and P8
        bench.Tie(ic.SerialInput, false);

        bench.Run();
        Assert.Equal(ic is Ic4021 ? 0b1010_0000 : 0, ic.Value);
        Pulse(bench, clock);
        Assert.Equal([true, false, true], ic.SerialOutputs.Select(bench.High));

        load.State = false;
        bench.Run();
        Pulse(bench, clock);
        Assert.Equal([false, true, false], ic.SerialOutputs.Select(bench.High));
    }

    /// <summary>The 74194's four modes: load, shift right, shift left, hold — and the clear.</summary>
    [Fact]
    public void The74194DoesWhatItsModePinsSay()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74194());
        var clock = bench.Toggle(ic.Clock);
        var s0 = bench.Toggle(ic.S0, true);
        var s1 = bench.Toggle(ic.S1, true);
        var clear = bench.Toggle(ic.Clear, true);
        var right = bench.Toggle(ic.SerialRight, true);
        bench.Toggle(ic.SerialLeft);
        bench.Toggles(ic.Data, 0b0110);

        bench.Run();
        Pulse(bench, clock);
        Assert.Equal(0b0110, bench.Byte(ic.Outputs));

        s1.State = false;   // 01: shift right, SR into QA
        bench.Run();
        Pulse(bench, clock);
        Assert.Equal(0b1101, bench.Byte(ic.Outputs));

        s1.State = true;    // 10: shift left, SL (low) into QD
        s0.State = false;
        bench.Run();
        Pulse(bench, clock);
        Assert.Equal(0b0110, bench.Byte(ic.Outputs));

        s1.State = false;   // 00: hold
        right.State = false;
        bench.Run();
        Pulse(bench, clock);
        Assert.Equal(0b0110, bench.Byte(ic.Outputs));

        clear.State = false;
        bench.Run();
        Assert.Equal(0, bench.Byte(ic.Outputs));
    }

    /// <summary>
    /// The 74299 takes a byte in through its I/O pins while both mode pins are high — when it has let go
    /// of them — then drives the shifted byte back out on the same pins, and lets go again with /G1 high.
    /// </summary>
    [Fact]
    public void The74299LoadsAndDrivesThroughTheSamePins()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74299());
        var buffer = bench.Add(new Ic74244());
        var drive = bench.Toggle(buffer.Enable1);
        bench.Circuit.Connect(buffer.Enable1, buffer.Enable2);
        bench.Toggles(buffer.Inputs, 0x5A);
        for (var i = 0; i < 8; i++) bench.Circuit.Connect(buffer.Outputs[i], ic.Data[i]);

        var clock = bench.Toggle(ic.Clock);
        var s0 = bench.Toggle(ic.S0, true);
        var s1 = bench.Toggle(ic.S1, true);
        var g1 = bench.Toggle(ic.OutputEnables.G1);
        bench.Tie(ic.OutputEnables.G2, false);
        bench.Tie(ic.Clear, true);
        bench.Tie(ic.SerialRight, false);
        bench.Tie(ic.SerialLeft, false);

        bench.Run();
        Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(0));
        Pulse(bench, clock);
        Assert.Equal(0x5A, ic.Value);

        // The buffer lets go of the bus, and the register shifts right once onto it.
        drive.State = true;
        s1.State = false;
        bench.Run();
        Pulse(bench, clock);
        Assert.Equal(0xB4, bench.Byte(ic.Outputs));
        Assert.True(bench.High(ic.ExtraOutputs[1]));     // QH'

        g1.State = true;
        bench.Run();
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(i)));
        Assert.True(bench.High(ic.ExtraOutputs[1]));     // the serial outputs stay driven
    }

    /// <summary>The 74195's first stage by J and K̄: clear, set, toggle, keep — and a parallel load.</summary>
    [Fact]
    public void The74195ShiftsThroughItsJkInput()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74195());
        var clock = bench.Toggle(ic.Clock);
        var load = bench.Toggle(ic.Load);    // low: load
        bench.Tie(ic.Clear, true);
        var j = bench.Toggle(ic.J);
        var k = bench.Toggle(ic.KBar);
        bench.Toggles(ic.Parallel, 0b1001);

        bench.Run();
        Pulse(bench, clock);
        Assert.Equal(0b1001, bench.Byte(ic.Outputs));
        Assert.False(bench.High(ic.LastInverted!));

        load.State = true;
        j.State = true;
        k.State = true;      // set
        bench.Run();
        Pulse(bench, clock);
        Assert.Equal(0b0011, bench.Byte(ic.Outputs));

        k.State = false;     // J high, K' low: toggle QA (1 -> 0)
        bench.Run();
        Pulse(bench, clock);
        Assert.Equal(0b0110, bench.Byte(ic.Outputs));

        j.State = false;
        k.State = true;      // keep QA (0)
        bench.Run();
        Pulse(bench, clock);
        Assert.Equal(0b1100, bench.Byte(ic.Outputs));
        Assert.False(bench.High(ic.LastInverted!));
    }

    /// <summary>The 4035 loads while P/S is high, resets on a high, and inverts its outputs while T/C is low.</summary>
    [Fact]
    public void The4035HasTrueAndComplementOutputs()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4035());
        var clock = bench.Toggle(ic.Clock);
        bench.Toggle(ic.Load, true);
        var reset = bench.Toggle(ic.Clear);
        var tc = bench.Toggle(ic.TrueComplement!, true);
        bench.Tie(ic.J, false);
        bench.Tie(ic.KBar, false);
        bench.Toggles(ic.Parallel, 0b0101);

        bench.Run();
        Pulse(bench, clock);
        Assert.Equal(0b0101, bench.Byte(ic.Outputs));

        tc.State = false;
        bench.Run();
        Assert.Equal(0b1010, bench.Byte(ic.Outputs));

        reset.State = true;
        bench.Run();
        Assert.Equal(0b1111, bench.Byte(ic.Outputs));
    }

    /// <summary>Each 4015 half shifts its own data on its own clock.</summary>
    [Fact]
    public void The4015HalvesAreIndependent()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4015());
        var clocks = new[] { bench.Toggle(ic.Clock(0)), bench.Toggle(ic.Clock(1)) };
        var data = new[] { bench.Toggle(ic.Data(0), true), bench.Toggle(ic.Data(1)) };
        var reset = bench.Toggle(ic.Reset(0));
        bench.Tie(ic.Reset(1), false);

        bench.Run();
        Pulse(bench, clocks[0]);
        data[0].State = false;
        bench.Run();
        Pulse(bench, clocks[0]);
        Pulse(bench, clocks[0]);
        Assert.Equal(0b0100, bench.Byte(ic.Outputs(0)));
        Assert.Equal(0, bench.Byte(ic.Outputs(1)));

        data[1].State = true;
        bench.Run();
        Pulse(bench, clocks[1]);
        Assert.Equal(0b0001, bench.Byte(ic.Outputs(1)));

        reset.State = true;
        bench.Run();
        Assert.Equal(0, bench.Byte(ic.Outputs(0)));
        Assert.Equal(0b0001, bench.Byte(ic.Outputs(1)));
    }

    /// <summary>
    /// The 4094's outputs change only while STROBE is high, let go while OE is low, and QS and Q'S carry
    /// stage 8 on the two clock edges.
    /// </summary>
    [Fact]
    public void The4094ShiftsThenStoresWhenStrobed()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4094());
        var clock = bench.Toggle(ic.Clock);
        var data = bench.Toggle(ic.Data);
        var strobe = bench.Toggle(ic.Strobe);
        var oe = bench.Toggle(ic.OutputEnable, true);

        bench.Run();
        foreach (var bit in new[] { true, false, false, true, true, false, true, false })
        {
            data.State = bit;
            bench.Run();
            Pulse(bench, clock);
        }

        // 1, 0, 0, 1, 1, 0, 1, 0 shifted in, the first now in stage 8.
        Assert.Equal(0b1001_1010, ic.ShiftRegister);
        Assert.Equal(0, bench.Byte(ic.Outputs));
        Assert.True(bench.High(ic.SerialOut));
        Assert.True(bench.High(ic.SerialOutDelayed));

        strobe.State = true;
        bench.Run();
        strobe.State = false;
        bench.Run();
        Assert.Equal(0b1001_1010, bench.Byte(ic.Outputs));

        // Shifting on with the strobe low leaves the outputs alone.
        Pulse(bench, clock);
        Assert.Equal(0b1001_1010, bench.Byte(ic.Outputs));
        Assert.False(bench.High(ic.SerialOut));

        oe.State = false;
        bench.Run();
        Assert.All(Enumerable.Range(0, 8), i => Assert.Equal(LogicState.HighImpedance, ic.GetOutputState(i)));
    }
}
