using Cirq.Components.Digital;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The 74283 and 7483 adders and the 74192 and 74193 up/down counters, against the function tables
/// and pinouts in their datasheets.
/// </summary>
public class AdderAndUpDownCounterTests
{
    private static SimulationSettings Fast => new() { TimeStep = 2e-9, MaxTimeStep = 2e-9 };

    private static Terminal Power(Circuit circuit, DigitalIc ic)
    {
        var supply = circuit.Add(new DcVoltageSource(5.0));
        var gnd = circuit.Add(new Ground());
        circuit.Connect(supply.Negative, gnd.Pin);
        circuit.Connect(ic.Vcc, supply.Positive);
        circuit.Connect(ic.Gnd, gnd.Pin);
        return gnd.Pin;
    }

    private static bool High(CircuitSimulator sim, Terminal t) => sim.NodeVoltage(t) > LogicLevels.Ttl.Vih;

    // ---- the adders ----------------------------------------------------------

    private sealed class AdderBench
    {
        public AdderBench(FullAdderIc adder)
        {
            Adder = adder;
            Circuit.Add(adder);
            Power(Circuit, adder);

            foreach (var pin in adder.A.Concat(adder.B).Append(adder.CarryIn))
            {
                var toggle = Circuit.Add(new LogicToggle());
                Circuit.Connect(toggle.Out, pin);
                Toggles.Add(toggle);
            }

            Sim = new CircuitSimulator(Circuit, Fast);
            Sim.Reset();
            Sim.SolveOperatingPoint();
        }

        public Circuit Circuit { get; } = new();

        public FullAdderIc Adder { get; }

        public List<LogicToggle> Toggles { get; } = [];

        public CircuitSimulator Sim { get; }

        public int Add(int a, int b, int carry)
        {
            for (var i = 0; i < 4; i++)
            {
                Toggles[i].State = ((a >> i) & 1) == 1;
                Toggles[4 + i].State = ((b >> i) & 1) == 1;
            }

            Toggles[8].State = carry == 1;
            Sim.Run(60e-9);

            var result = 0;
            for (var i = 0; i < 4; i++)
                if (High(Sim, Adder.Sum[i])) result |= 1 << i;

            return High(Sim, Adder.CarryOut) ? result | 16 : result;
        }
    }

    /// <summary>All 512 input combinations: every sum and every carry, on the pins.</summary>
    [Fact]
    public void The74283AddsEveryPairOfNibblesWithAndWithoutACarry()
    {
        var bench = new AdderBench(new Ic74283());

        for (var a = 0; a < 16; a++)
            for (var b = 0; b < 16; b++)
                for (var carry = 0; carry < 2; carry++)
                    Assert.Equal(a + b + carry, bench.Add(a, b, carry));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(9, 6, 0)]
    [InlineData(15, 15, 1)]
    [InlineData(7, 8, 1)]
    [InlineData(10, 5, 1)]
    public void The7483AddsTheSame(int a, int b, int carry) =>
        Assert.Equal(a + b + carry, new AdderBench(new Ic7483()).Add(a, b, carry));

    /// <summary>The two adders differ only in where their pins are — the 7483's supply is in the middle.</summary>
    [Fact]
    public void TheirPinoutsAreTheDatasheetsOwn()
    {
        string Named(DigitalIc ic, int pin) => ic.Terminals.Single(t => t.Id == $"p{pin}").Name;

        var modern = new Ic74283();
        Assert.Equal(["S2", "B2", "A2", "S1", "A1", "B1", "C0", "GND", "C4", "S4", "B4", "A4", "S3", "A3", "B3", "VCC"],
            Enumerable.Range(1, 16).Select(p => Named(modern, p)));

        var original = new Ic7483();
        Assert.Equal(["A4", "S3", "A3", "B3", "VCC", "S2", "B2", "A2", "S1", "A1", "B1", "GND", "C0", "C4", "S4", "B4"],
            Enumerable.Range(1, 16).Select(p => Named(original, p)));
    }

    /// <summary>Two adders with C4 into C0 make an eight-bit adder.</summary>
    [Fact]
    public void TwoCascadedOnTheCarryAddEightBits()
    {
        var circuit = new Circuit();
        var low = circuit.Add(new Ic74283());
        var high = circuit.Add(new Ic74283());
        var gnd = Power(circuit, low);
        circuit.Connect(high.Vcc, low.Vcc);
        circuit.Connect(high.Gnd, gnd);
        circuit.Connect(low.CarryOut, high.CarryIn);
        circuit.Connect(low.CarryIn, gnd);

        // 0xB7 + 0x5C = 0x113.
        var a = 0xB7;
        var b = 0x5C;

        for (var i = 0; i < 8; i++)
        {
            var stage = i < 4 ? low : high;
            circuit.Connect(circuit.Add(new LogicToggle(((a >> i) & 1) == 1)).Out, stage.A[i % 4]);
            circuit.Connect(circuit.Add(new LogicToggle(((b >> i) & 1) == 1)).Out, stage.B[i % 4]);
        }

        var sim = new CircuitSimulator(circuit, Fast);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(100e-9);

        var sum = 0;
        for (var i = 0; i < 8; i++)
            if (High(sim, (i < 4 ? low : high).Sum[i % 4])) sum |= 1 << i;
        if (High(sim, high.CarryOut)) sum |= 0x100;

        Assert.Equal(a + b, sum);
    }

    // ---- the up/down counters -----------------------------------------------

    private sealed class CounterBench
    {
        public CounterBench(UpDownCounterIc counter, int load = 0)
        {
            Counter = counter;
            Circuit.Add(counter);
            Power(Circuit, counter);

            UpClock = Circuit.Add(new LogicToggle(true));
            DownClock = Circuit.Add(new LogicToggle(true));
            LoadPin = Circuit.Add(new LogicToggle(true));
            ClearPin = Circuit.Add(new LogicToggle(false));

            Circuit.Connect(UpClock.Out, counter.Up);
            Circuit.Connect(DownClock.Out, counter.Down);
            Circuit.Connect(LoadPin.Out, counter.Load);
            Circuit.Connect(ClearPin.Out, counter.Clear);

            for (var i = 0; i < 4; i++)
                Circuit.Connect(Circuit.Add(new LogicToggle(((load >> i) & 1) == 1)).Out, counter.Data[i]);

            Sim = new CircuitSimulator(Circuit, Fast);
            Sim.Reset();
            Sim.SolveOperatingPoint();
            Sim.Run(60e-9);
        }

        public Circuit Circuit { get; } = new();

        public UpDownCounterIc Counter { get; }

        public LogicToggle UpClock { get; }

        public LogicToggle DownClock { get; }

        public LogicToggle LoadPin { get; }

        public LogicToggle ClearPin { get; }

        public CircuitSimulator Sim { get; }

        public void Set(LogicToggle toggle, bool high)
        {
            toggle.State = high;
            Sim.Run(80e-9);
        }

        /// <summary>A clock pulse as the datasheet draws it: low, then back up — the count moves on the rise.</summary>
        public void Pulse(LogicToggle clock)
        {
            Set(clock, false);
            Set(clock, true);
        }

        public int Outputs => Enumerable.Range(0, 4).Sum(i => High(Sim, Counter.Outputs[i]) ? 1 << i : 0);

        public bool CarryHigh => High(Sim, Counter.Carry);

        public bool BorrowHigh => High(Sim, Counter.Borrow);
    }

    [Fact]
    public void The74193CountsUpThroughSixteenAndWraps()
    {
        var bench = new CounterBench(new Ic74193());

        for (var expected = 1; expected <= 17; expected++)
        {
            bench.Pulse(bench.UpClock);
            Assert.Equal(expected % 16, bench.Outputs);
        }
    }

    [Fact]
    public void The74193CountsDownFromZeroToFifteen()
    {
        var bench = new CounterBench(new Ic74193());

        bench.Pulse(bench.DownClock);
        Assert.Equal(15, bench.Outputs);

        bench.Pulse(bench.DownClock);
        Assert.Equal(14, bench.Outputs);
    }

    [Fact]
    public void The74192CountsInBcdBothWays()
    {
        var bench = new CounterBench(new Ic74192());

        List<int> up = [];
        for (var i = 0; i < 11; i++)
        {
            bench.Pulse(bench.UpClock);
            up.Add(bench.Outputs);
        }

        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 0, 1], up);

        bench.Pulse(bench.DownClock);
        bench.Pulse(bench.DownClock);
        Assert.Equal(9, bench.Outputs);
    }

    /// <summary>Counting needs the other clock held high; with it low an edge on this one does nothing.</summary>
    [Fact]
    public void AnEdgeCountsOnlyWhileTheOtherClockIsHigh()
    {
        var bench = new CounterBench(new Ic74193());

        bench.Set(bench.DownClock, false);
        bench.Pulse(bench.UpClock);

        Assert.Equal(0, bench.Outputs);
    }

    [Fact]
    public void LoadIsAsynchronousAndActiveLow()
    {
        var bench = new CounterBench(new Ic74193(), load: 11);

        bench.Set(bench.LoadPin, false);
        Assert.Equal(11, bench.Outputs);         // no clock needed

        bench.Set(bench.LoadPin, true);
        bench.Pulse(bench.UpClock);
        Assert.Equal(12, bench.Outputs);
    }

    [Fact]
    public void ClearIsAsynchronousActiveHighAndBeatsLoad()
    {
        var bench = new CounterBench(new Ic74193(), load: 7);

        bench.Pulse(bench.UpClock);
        bench.Pulse(bench.UpClock);
        Assert.Equal(2, bench.Outputs);

        bench.Set(bench.ClearPin, true);
        Assert.Equal(0, bench.Outputs);

        bench.Set(bench.LoadPin, false);
        Assert.Equal(0, bench.Outputs);         // clear wins while both are asserted
    }

    /// <summary>
    /// CO is low only at the top of the count and only while UP is low — it rises on the UP edge
    /// that wraps the count, which is the edge the next stage needs.
    /// </summary>
    [Fact]
    public void CarryIsLowAtTheTopOfTheCountWhileUpIsLow()
    {
        var bench = new CounterBench(new Ic74192());

        for (var i = 0; i < 9; i++) bench.Pulse(bench.UpClock);
        Assert.Equal(9, bench.Outputs);
        Assert.True(bench.CarryHigh);           // at 9, but UP is high

        bench.Set(bench.UpClock, false);
        Assert.False(bench.CarryHigh);          // at 9 with UP low

        bench.Set(bench.UpClock, true);
        Assert.Equal(0, bench.Outputs);
        Assert.True(bench.CarryHigh);
    }

    [Fact]
    public void BorrowIsLowAtZeroWhileDownIsLow()
    {
        var bench = new CounterBench(new Ic74193());

        Assert.True(bench.BorrowHigh);

        bench.Set(bench.DownClock, false);
        Assert.False(bench.BorrowHigh);

        bench.Set(bench.DownClock, true);
        Assert.Equal(15, bench.Outputs);
        Assert.True(bench.BorrowHigh);
    }

    /// <summary>Two 74192s cascaded CO to UP and BO to DOWN count 00 to 99 — and back down through zero.</summary>
    [Fact]
    public void TwoDecadesCascadedCountToNinetyNineAndBack()
    {
        var circuit = new Circuit();
        var units = circuit.Add(new Ic74192());
        var tens = circuit.Add(new Ic74192());
        var gnd = Power(circuit, units);
        circuit.Connect(tens.Vcc, units.Vcc);
        circuit.Connect(tens.Gnd, gnd);

        var up = circuit.Add(new LogicToggle(true));
        var down = circuit.Add(new LogicToggle(true));
        var load = circuit.Add(new LogicToggle(true));

        circuit.Connect(up.Out, units.Up);
        circuit.Connect(down.Out, units.Down);
        circuit.Connect(units.Carry, tens.Up);
        circuit.Connect(units.Borrow, tens.Down);

        foreach (var ic in new[] { units, tens })
        {
            circuit.Connect(load.Out, ic.Load);
            circuit.Connect(ic.Clear, gnd);
            foreach (var d in ic.Data) circuit.Connect(d, gnd);
        }

        var sim = new CircuitSimulator(circuit, Fast);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(60e-9);

        void Pulse(LogicToggle clock)
        {
            clock.State = false;
            sim.Run(100e-9);
            clock.State = true;
            sim.Run(100e-9);
        }

        for (var i = 0; i < 57; i++) Pulse(up);
        Assert.Equal(57, (tens.Count * 10) + units.Count);

        for (var i = 0; i < 43; i++) Pulse(up);
        Assert.Equal(0, (tens.Count * 10) + units.Count);     // 99, then round to 00

        Pulse(down);
        Assert.Equal(99, (tens.Count * 10) + units.Count);
    }

    [Fact]
    public void TheyAllRoundTripThroughTheFile()
    {
        var circuit = new Circuit();
        circuit.Add(new Ic74283());
        circuit.Add(new Ic7483());
        circuit.Add(new Ic74192());
        circuit.Add(new Ic74193());

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit;

        Assert.Equal(["74283", "7483", "74192", "74193"], back.Components.OfType<DigitalIc>().Select(c => c.PartNumber));
    }
}
