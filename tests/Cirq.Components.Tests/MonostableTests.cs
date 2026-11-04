using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Core.Digital;
using Cirq.Core.Topology;

namespace Cirq.Components.Tests;

/// <summary>
/// The 74121, 74123 and 74221 one-shots, the 4528 and 4538, and the 4047: a pulse of the datasheet's
/// K·R·C from a real resistor and capacitor, what triggers each, retriggering against not, clears, and
/// the 4047's astable period.
/// </summary>
public class MonostableTests
{
    /// <summary>The timing parts as the datasheets draw them: C from CEXT to RX/CX, R from RX/CX to the supply.</summary>
    private static void Time(LogicBench bench, Terminal capacitorPin, Terminal timingPin, double r, double c)
    {
        var cap = bench.Circuit.Add(new Capacitor(c));
        bench.Circuit.Connect(cap.A, capacitorPin);
        bench.Circuit.Connect(cap.B, timingPin);

        var res = bench.Circuit.Add(new Resistor(r));
        bench.Circuit.Connect(res.A, bench.Rail.Positive);
        bench.Circuit.Connect(res.B, timingPin);
    }

    /// <summary>How long a pin stays high from now, sampled every <paramref name="step"/>.</summary>
    private static double HighFor(LogicBench bench, Terminal pin, double step = 2e-6, double limit = 0.05)
    {
        var start = bench.Simulator.Time;
        while (bench.High(pin) && bench.Simulator.Time - start < limit) bench.Run(step);
        return bench.Simulator.Time - start;
    }

    [Theory]
    [InlineData(typeof(Ic74121), "p1=Q' p2=NC p3=A1 p4=A2 p5=B p6=Q p7=GND p8=NC p9=RINT p10=CEXT p11=REXT/CEXT p12=NC p13=NC p14=VCC")]
    [InlineData(typeof(Ic74123), "p1=1A p2=1B p3=1CLR p4=1Q' p5=2Q p6=2CEXT p7=2REXT/CEXT p8=GND p9=2A p10=2B p11=2CLR p12=2Q' p13=1Q p14=1CEXT p15=1REXT/CEXT p16=VCC")]
    [InlineData(typeof(Ic74221), "p1=1A p3=1CLR p13=1Q p15=1REXT/CEXT")]
    [InlineData(typeof(Ic4538), "p1=1CX p2=1RX/CX p3=1RST p4=1+TR p5=1-TR p6=1Q p7=1Q' p8=VSS p9=2Q' p10=2Q p11=2-TR p12=2+TR p13=2RST p14=2RX/CX p15=2CX p16=VDD")]
    [InlineData(typeof(Ic4528), "p2=1RX/CX p4=1+TR p14=2RX/CX")]
    [InlineData(typeof(Ic4047), "p1=C p2=R p3=RC p4=AST p5=AST' p6=-TR p7=VSS p8=+TR p9=RST p10=Q p11=Q' p12=RETR p13=OSC p14=VDD")]
    public void EachIsOnItsDatasheetPinout(Type type, string expected)
    {
        var ic = (DigitalIc)Activator.CreateInstance(type)!;

        foreach (var pair in expected.Split(' '))
        {
            var (id, name) = (pair.Split('=', 2)[0], pair.Split('=', 2)[1]);
            Assert.Equal(name, ic.Terminals.Single(t => t.Id == id).Name);
        }

        Assert.Equal(ic.PinCount, ic.Terminals.Count);
    }

    /// <summary>
    /// The 74121 triggered by B rising, with 10 kΩ and 100 nF: 0.7·R·C, 693 µs — and with the capacitor
    /// doubled, twice that. A second trigger in the middle of the pulse changes nothing.
    /// </summary>
    [Theory]
    [InlineData(100e-9)]
    [InlineData(200e-9)]
    public void The74121GivesSevenTenthsOfRcAndIgnoresARetrigger(double c)
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74121());
        var (triggers, _, q, qNot, cext, timing) = ic.Pins(0);
        bench.Tie(triggers[0], false);
        bench.Tie(triggers[1], false);
        var b = bench.Toggle(triggers[2]);
        Time(bench, cext, timing, 10e3, c);

        bench.Run(5e-3);
        Assert.False(bench.High(q));
        Assert.True(bench.High(qNot));

        b.State = true;
        bench.Run(2e-6);
        Assert.True(bench.High(q));

        // B down and up again a third of the way in: not retriggerable, so no effect.
        var expected = Math.Log(2) * 10e3 * c;
        b.State = false;
        bench.Run(expected / 3);
        b.State = true;
        var width = expected / 3 + 2e-6 + HighFor(bench, q);

        Assert.Equal(expected, width, expected * 0.03);
        Assert.True(bench.High(qNot));
    }

    /// <summary>RINT tied to VCC and no external resistor: the internal 2 kΩ times the capacitor.</summary>
    [Fact]
    public void The74121CanUseItsInternalResistor()
    {
        var bench = new LogicBench();
        var ic = bench.Add(new Ic74121());
        var (triggers, _, q, _, cext, timing) = ic.Pins(0);
        var a1 = bench.Toggle(triggers[0], true);
        bench.Tie(triggers[1], true);
        bench.Tie(triggers[2], true);
        bench.Tie(ic.InternalResistor, true);
        var cap = bench.Circuit.Add(new Capacitor(470e-9));
        bench.Circuit.Connect(cap.A, cext);
        bench.Circuit.Connect(cap.B, timing);

        bench.Run(10e-3);

        // A1 falling with A2 and B high is the other way to trigger it.
        a1.State = false;
        bench.Run(2e-6);
        var width = 2e-6 + HighFor(bench, q);
        var expected = Math.Log(2) * Ic74121.InternalResistance * 470e-9;
        Assert.Equal(expected, width, expected * 0.04);
    }

    /// <summary>
    /// A 74123 retriggered partway through runs on for a whole K·R·C from the retrigger; a 74221 in the
    /// same circuit ignores it. Both are cut short by CLR.
    /// </summary>
    [Theory]
    [InlineData(typeof(Ic74123))]
    [InlineData(typeof(Ic74221))]
    public void RetriggeringRestartsOnlyTheRetriggerablePart(Type type)
    {
        var bench = new LogicBench();
        var ic = bench.Add((MonostableIc)Activator.CreateInstance(type)!);
        var (triggers, clear, q, _, cext, timing) = ic.Pins(0);
        var a = bench.Toggle(triggers[0], true);
        bench.Tie(triggers[1], true);
        var clr = bench.Toggle(clear!, true);
        Time(bench, cext, timing, 20e3, 100e-9);
        var tw = ic.TimingConstant * 20e3 * 100e-9;

        for (var i = 0; i < 2; i++) bench.Run(5e-3);

        a.State = false;     // A falling with B high
        bench.Run(tw / 2);
        a.State = true;
        bench.Run(2e-6);
        a.State = false;     // again, half way through
        var width = tw / 2 + 2e-6 + HighFor(bench, q);

        Assert.Equal(ic.IsRetriggerable ? 1.5 * tw : tw, width, tw * 0.05);

        // And CLR ends a pulse at once.
        a.State = true;
        bench.Run(10e-3);
        a.State = false;
        bench.Run(tw / 4);
        Assert.True(bench.High(q));
        clr.State = false;
        bench.Run(2e-6);
        Assert.False(bench.High(q));
    }

    /// <summary>
    /// The 74123 also triggers on CLR going high with A low and B high; the 74221 does not.
    /// </summary>
    [Theory]
    [InlineData(typeof(Ic74123), true)]
    [InlineData(typeof(Ic74221), false)]
    public void OnlyThe74123TriggersOnReleasingClear(Type type, bool triggers)
    {
        var bench = new LogicBench();
        var ic = bench.Add((MonostableIc)Activator.CreateInstance(type)!);
        var (pins, clear, q, _, cext, timing) = ic.Pins(1);
        bench.Tie(pins[0], false);
        bench.Tie(pins[1], true);
        var clr = bench.Toggle(clear!);
        Time(bench, cext, timing, 10e3, 100e-9);

        bench.Run(5e-3);
        Assert.False(bench.High(q));

        clr.State = true;
        bench.Run(10e-6);
        Assert.Equal(triggers, bench.High(q));
    }

    /// <summary>
    /// The 4538 at R·C, triggered either way: +TR rising with −TR high, and −TR falling with +TR low.
    /// A rising +TR with −TR low is not a trigger. RESET low ends the pulse.
    /// </summary>
    [Fact]
    public void The4538GivesRcFromEitherTrigger()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4538());
        var (triggers, reset, q, qNot, cx, rxcx) = ic.Pins(0);
        var plus = bench.Toggle(triggers[0]);
        var minus = bench.Toggle(triggers[1], true);
        var rst = bench.Toggle(reset!, true);
        Time(bench, cx, rxcx, 10e3, 100e-9);
        const double tw = 10e3 * 100e-9;

        bench.Run(5e-3);
        plus.State = true;
        bench.Run(2e-6);
        Assert.Equal(tw, 2e-6 + HighFor(bench, q), tw * 0.03);
        Assert.True(bench.High(qNot));

        plus.State = false;
        bench.Run(5e-3);
        minus.State = false;
        bench.Run(2e-6);
        Assert.Equal(tw, 2e-6 + HighFor(bench, q), tw * 0.03);

        // +TR rising while −TR is low does nothing.
        bench.Run(5e-3);
        plus.State = true;
        bench.Run(50e-6);
        Assert.False(bench.High(q));

        // −TR back high then low again with +TR low triggers; reset cuts it short.
        plus.State = false;
        minus.State = true;
        bench.Run(5e-3);
        minus.State = false;
        bench.Run(tw / 3);
        Assert.True(bench.High(q));
        rst.State = false;
        bench.Run(2e-6);
        Assert.False(bench.High(q));
    }

    /// <summary>
    /// A 4538 as a missing-pulse detector: triggers every 0.6 R·C hold Q high throughout, because each
    /// restarts the timing, and Q falls R·C after the last.
    /// </summary>
    [Fact]
    public void A4538HeldByATrainOfTriggersStaysHigh()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4538());
        var (triggers, reset, q, _, cx, rxcx) = ic.Pins(1);
        var plus = bench.Toggle(triggers[0]);
        bench.Tie(triggers[1], true);
        bench.Tie(reset!, true);
        Time(bench, cx, rxcx, 10e3, 100e-9);
        const double tw = 1e-3;

        bench.Run(5e-3);
        for (var i = 0; i < 6; i++)
        {
            plus.State = true;
            bench.Run(0.3 * tw);
            Assert.True(bench.High(q));
            plus.State = false;
            bench.Run(0.3 * tw);
            Assert.True(bench.High(q));
        }

        plus.State = true;
        bench.Run(2e-6);
        Assert.Equal(tw, 2e-6 + HighFor(bench, q), tw * 0.03);
    }

    /// <summary>The 4528's pulse is 0.2·R·C·ln(VDD), about a third of R·C at 5 V.</summary>
    [Fact]
    public void The4528PulseFollowsItsSupplyFormula()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4528());
        var (triggers, reset, q, _, cx, rxcx) = ic.Pins(0);
        var plus = bench.Toggle(triggers[0]);
        bench.Tie(triggers[1], true);
        bench.Tie(reset!, true);
        Time(bench, cx, rxcx, 47e3, 100e-9);

        bench.Run(20e-3);
        plus.State = true;
        bench.Run(2e-6);
        var expected = 0.2 * Math.Log(5.0) * 47e3 * 100e-9;
        Assert.Equal(expected, 2e-6 + HighFor(bench, q), expected * 0.03);
    }

    /// <summary>The 4047 monostable: 2.48·R·C from a rising +TRIGGER with −TRIGGER low.</summary>
    [Fact]
    public void The4047MonostableGivesTwoAndAHalfRc()
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4047());
        bench.Tie(ic.Astable, false);
        bench.Tie(ic.AstableNot, true);
        bench.Tie(ic.NegativeTrigger, false);
        bench.Tie(ic.ExternalReset, false);
        bench.Tie(ic.Retrigger, false);
        var trigger = bench.Toggle(ic.PositiveTrigger);
        Wire4047(bench, ic, 10e3, 10e-9);

        bench.Run(2e-3);
        Assert.False(ic.IsAstable);
        trigger.State = true;
        bench.Run(1e-6);

        var expected = 2.48 * 10e3 * 10e-9;
        Assert.Equal(expected, 1e-6 + HighFor(bench, ic.Q, step: 0.5e-6), expected * 0.03);
    }

    /// <summary>
    /// The 4047 astable: OSC at 2.2·R·C, Q at 4.4·R·C with a half duty cycle — from the R and C on the
    /// canvas, so ten times the capacitor is ten times the period.
    /// </summary>
    [Theory]
    [InlineData(10e-9)]
    [InlineData(100e-9)]
    public void The4047AstableRunsAtFourPointFourRc(double c)
    {
        var bench = new LogicBench { ToggleLevels = LogicLevels.Cmos5V };
        var ic = bench.Add(new Ic4047());
        bench.Tie(ic.Astable, true);
        bench.Tie(ic.AstableNot, true);
        bench.Tie(ic.NegativeTrigger, false);
        bench.Tie(ic.PositiveTrigger, false);
        bench.Tie(ic.ExternalReset, false);
        bench.Tie(ic.Retrigger, false);
        Wire4047(bench, ic, 10e3, c);

        var period = 4.4 * 10e3 * c;
        var step = period / 2000;

        // Find three rising edges of Q after the first few cycles, and the high time between.
        bench.Run(3 * period);
        var rises = new List<double>();
        var highTime = 0.0;
        var was = bench.High(ic.Q);
        while (rises.Count < 3)
        {
            bench.Run(step);
            var now = bench.High(ic.Q);
            if (now && !was) rises.Add(bench.Simulator.Time);
            if (now && rises.Count is 1 or 2) highTime += step;
            was = now;
        }

        var measured = (rises[2] - rises[0]) / 2;
        Assert.Equal(period, measured, period * 0.04);
        Assert.Equal(0.5, highTime / (rises[2] - rises[0]), 2);
    }

    private static void Wire4047(LogicBench bench, Ic4047 ic, double r, double c)
    {
        var cap = bench.Circuit.Add(new Capacitor(c));
        bench.Circuit.Connect(cap.A, ic.CapacitorPin);
        bench.Circuit.Connect(cap.B, ic.Timing);
        var res = bench.Circuit.Add(new Resistor(r));
        bench.Circuit.Connect(res.A, ic.ResistorPin);
        bench.Circuit.Connect(res.B, ic.Timing);
    }
}
