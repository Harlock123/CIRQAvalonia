using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class Ic4013Tests
{
    private sealed record Rig(
        CircuitSimulator Sim,
        Ic4013 Ic,
        DcVoltageSource Clock,
        DcVoltageSource Set,
        DcVoltageSource Reset,
        DcVoltageSource Data);

    private static Rig Build(bool divideByTwo = false)
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ic = circuit.Add(new Ic4013());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, gnd.Pin);

        var (set, reset, data, clock, q, qNot) = ic.Section(0);

        var clockSource = CmosRig.Level(circuit, gnd, false);
        var setSource = CmosRig.Level(circuit, gnd, false);
        var resetSource = CmosRig.Level(circuit, gnd, false);
        var dataSource = CmosRig.Level(circuit, gnd, false);

        circuit.Connect(clockSource.Positive, clock);
        circuit.Connect(setSource.Positive, set);
        circuit.Connect(resetSource.Positive, reset);

        if (divideByTwo)
        {
            // Q' back to D is the classic use: the flip-flop toggles, halving the clock.
            circuit.Connect(qNot, data);
        }
        else
        {
            circuit.Connect(dataSource.Positive, data);
        }

        CmosRig.Load(circuit, gnd, q);
        CmosRig.Load(circuit, gnd, qNot);

        // The second flip-flop still needs its pins tied off.
        var (set2, reset2, data2, clock2, q2, qNot2) = ic.Section(1);
        foreach (var pin in new[] { set2, reset2, data2, clock2 }) circuit.Connect(gnd.Pin, pin);
        CmosRig.Load(circuit, gnd, q2);
        CmosRig.Load(circuit, gnd, qNot2);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return new Rig(sim, ic, clockSource, setSource, resetSource, dataSource);
    }

    /// <summary>One clock pulse, given long enough for the transition to land.</summary>
    private static void Pulse(Rig rig)
    {
        foreach (var level in new[] { 5.0, 0.0 })
        {
            rig.Clock.Voltage = level;
            rig.Sim.Run(1e-6);
            rig.Sim.Run(1e-6);
        }
    }

    [Fact]
    public void ItLatchesDataOnTheRisingEdge()
    {
        var rig = Build();

        rig.Data.Voltage = 5.0;
        Pulse(rig);
        Assert.Equal(LogicState.High, rig.Ic.QState(0));

        rig.Data.Voltage = 0.0;
        Pulse(rig);
        Assert.Equal(LogicState.Low, rig.Ic.QState(0));
    }

    /// <summary>Q' back to D halves the clock, which is what makes this a divider.</summary>
    [Fact]
    public void WithQNotFedBackToDataItTogglesOnEveryClock()
    {
        var rig = Build(divideByTwo: true);

        var states = new List<LogicState>();
        for (var i = 0; i < 6; i++)
        {
            Pulse(rig);
            states.Add(rig.Ic.QState(0));
        }

        for (var i = 1; i < states.Count; i++)
            Assert.NotEqual(states[i - 1], states[i]);
    }

    /// <summary>
    /// The difference from a 7474, and the one that catches people: reset here is active
    /// <b>high</b>, where a 7474's clear is active low.
    /// </summary>
    [Fact]
    public void ResetIsActiveHigh()
    {
        var rig = Build();

        rig.Data.Voltage = 5.0;
        Pulse(rig);
        Assert.Equal(LogicState.High, rig.Ic.QState(0));

        rig.Reset.Voltage = 5.0;
        rig.Sim.Run(1e-6);
        rig.Sim.Run(1e-6);
        Assert.Equal(LogicState.Low, rig.Ic.QState(0));

        // ...and it holds, beating the clock while it is asserted.
        Pulse(rig);
        Assert.Equal(LogicState.Low, rig.Ic.QState(0));
    }

    [Fact]
    public void SetIsActiveHighToo()
    {
        var rig = Build();

        rig.Set.Voltage = 5.0;
        rig.Sim.Run(1e-6);
        rig.Sim.Run(1e-6);

        Assert.Equal(LogicState.High, rig.Ic.QState(0));
    }

    /// <summary>Both asserted is the illegal state, and the datasheet leaves both outputs high.</summary>
    [Fact]
    public void SetAndResetTogetherDriveBothOutputsHigh()
    {
        var rig = Build();
        var (_, _, _, _, q, qNot) = rig.Ic.Section(0);

        rig.Set.Voltage = 5.0;
        rig.Reset.Voltage = 5.0;
        rig.Sim.Run(1e-6);
        rig.Sim.Run(1e-6);

        Assert.True(rig.Sim.NodeVoltage(q) > 2.5);
        Assert.True(rig.Sim.NodeVoltage(qNot) > 2.5);
    }
}

public class Ic4040Tests
{
    private static (CircuitSimulator Sim, Ic4040 Ic, DcVoltageSource Clock, DcVoltageSource Reset) Build()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ic = circuit.Add(new Ic4040());
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, gnd.Pin);

        var clock = CmosRig.Level(circuit, gnd, false);
        var reset = CmosRig.Level(circuit, gnd, false);
        circuit.Connect(clock.Positive, ic.Clock);
        circuit.Connect(reset.Positive, ic.Reset);

        foreach (var output in ic.Outputs) CmosRig.Load(circuit, gnd, output);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, ic, clock, reset);
    }

    private static void Settle(CircuitSimulator sim)
    {
        sim.Run(1e-6);
        sim.Run(1e-6);
    }

    private static void Pulse(CircuitSimulator sim, DcVoltageSource clock)
    {
        clock.Voltage = 5.0;
        Settle(sim);
        clock.Voltage = 0.0;
        Settle(sim);
    }

    /// <summary>The other way round from the 4017 beside it, which is easy to get wrong.</summary>
    [Fact]
    public void ItCountsOnTheFallingEdgeNotTheRising()
    {
        var (sim, ic, clock, _) = Build();

        clock.Voltage = 5.0;
        Settle(sim);
        Assert.Equal(0, ic.Count);      // rising edge: nothing

        clock.Voltage = 0.0;
        Settle(sim);
        Assert.Equal(1, ic.Count);      // falling edge: counts
    }

    [Fact]
    public void EachStageDividesTheOneBeforeItByTwo()
    {
        var (sim, ic, clock, _) = Build();

        // Q1 toggles every clock, Q2 every two, Q3 every four, and so on up the chain.
        var toggles = new int[12];
        var previous = new bool[12];

        for (var pulse = 0; pulse < 64; pulse++)
        {
            Pulse(sim, clock);

            for (var stage = 0; stage < 12; stage++)
            {
                var now = sim.NodeVoltage(ic.Outputs[stage]) > 2.5;
                if (now != previous[stage]) toggles[stage]++;
                previous[stage] = now;
            }
        }

        // 64 clocks: Q1 toggles 64 times, Q2 32, Q3 16, Q4 8, Q5 4, Q6 2, Q7 once.
        Assert.Equal(64, toggles[0]);
        Assert.Equal(32, toggles[1]);
        Assert.Equal(16, toggles[2]);
        Assert.Equal(8, toggles[3]);
        Assert.Equal(4, toggles[4]);
        Assert.Equal(2, toggles[5]);
        Assert.Equal(1, toggles[6]);
        Assert.Equal(0, toggles[7]);
    }

    [Fact]
    public void TheOutputsCarryTheBinaryCount()
    {
        var (sim, ic, clock, _) = Build();

        for (var pulse = 1; pulse <= 20; pulse++)
        {
            Pulse(sim, clock);

            Assert.Equal(pulse, ic.Count);

            for (var stage = 0; stage < 12; stage++)
            {
                var expected = (ic.Count & (1 << stage)) != 0;
                Assert.Equal(expected, sim.NodeVoltage(ic.Outputs[stage]) > 2.5);
            }
        }
    }

    [Fact]
    public void ResetIsActiveHighAndClearsEveryStage()
    {
        var (sim, ic, clock, reset) = Build();

        for (var i = 0; i < 7; i++) Pulse(sim, clock);
        Assert.Equal(7, ic.Count);

        reset.Voltage = 5.0;
        Settle(sim);

        Assert.Equal(0, ic.Count);
        foreach (var output in ic.Outputs) Assert.True(sim.NodeVoltage(output) < 2.5);
    }
}

public class Ic4051Tests
{
    /// <summary>
    /// Three channels fed from distinct voltages, the rest left open, and a high-impedance load on
    /// the common pin so it reads whatever is routed to it.
    /// </summary>
    private static (CircuitSimulator Sim, Ic4051 Ic, DcVoltageSource A, DcVoltageSource B,
        DcVoltageSource C, DcVoltageSource Inhibit, Resistor Load) Build()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ic = circuit.Add(new Ic4051());
        var load = circuit.Add(new Resistor(1e6));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(rail.Negative, gnd.Pin);
        circuit.Connect(rail.Positive, ic.Vcc);
        circuit.Connect(ic.Gnd, gnd.Pin);
        circuit.Connect(ic.NegativeSupply, gnd.Pin);

        var a = CmosRig.Level(circuit, gnd, false);
        var b = CmosRig.Level(circuit, gnd, false);
        var c = CmosRig.Level(circuit, gnd, false);
        var inhibit = CmosRig.Level(circuit, gnd, false);

        circuit.Connect(a.Positive, ic.A);
        circuit.Connect(b.Positive, ic.B);
        circuit.Connect(c.Positive, ic.C);
        circuit.Connect(inhibit.Positive, ic.Inhibit);

        // A different voltage on every channel, so the common pin says which one is through.
        for (var channel = 0; channel < 8; channel++)
        {
            var signal = circuit.Add(new DcVoltageSource(0.4 * (channel + 1)));
            circuit.Connect(signal.Negative, gnd.Pin);
            circuit.Connect(signal.Positive, ic.Channel(channel));
        }

        circuit.Connect(ic.Common, load.A);
        circuit.Connect(load.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        return (sim, ic, a, b, c, inhibit, load);
    }

    private static void Address(CircuitSimulator sim, DcVoltageSource a, DcVoltageSource b,
        DcVoltageSource c, int channel)
    {
        a.Voltage = (channel & 1) != 0 ? 5.0 : 0.0;
        b.Voltage = (channel & 2) != 0 ? 5.0 : 0.0;
        c.Voltage = (channel & 4) != 0 ? 5.0 : 0.0;
        sim.Run(1e-6);
        sim.Run(1e-6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void TheAddressPinsPickOneChannelThroughToCommon(int channel)
    {
        var (sim, ic, a, b, c, _, _) = Build();

        Address(sim, a, b, c, channel);

        Assert.Equal(channel, ic.SelectedChannel);
        Assert.Equal(0.4 * (channel + 1), sim.NodeVoltage(ic.Common), 0.01);
    }

    /// <summary>
    /// Unlike four separate 4066 switches, there is no way to get two sources onto the bus at
    /// once — the decoder only ever closes one path.
    /// </summary>
    [Fact]
    public void OnlyOneChannelIsEverConnected()
    {
        var (sim, ic, a, b, c, _, _) = Build();

        for (var channel = 0; channel < 8; channel++)
        {
            Address(sim, a, b, c, channel);
            Assert.Equal(channel, ic.SelectedChannel);
        }
    }

    [Fact]
    public void InhibitIsActiveHighAndDisconnectsEverything()
    {
        var (sim, ic, a, b, c, inhibit, _) = Build();

        Address(sim, a, b, c, 3);
        Assert.Equal(1.6, sim.NodeVoltage(ic.Common), 0.01);

        inhibit.Voltage = 5.0;
        sim.Run(1e-6);
        sim.Run(1e-6);

        Assert.Equal(-1, ic.SelectedChannel);

        // Not zero: eight off channels still feed the common pin through their off-resistance, and
        // with a 1 M load that is a divider like any other. The leak is what those resistances say
        // it should be — a few millivolts against the 1.6 V that was coming through a moment ago.
        var sources = Enumerable.Range(0, 8).Sum(channel => 0.4 * (channel + 1));
        var leak = (sources / ic.OffResistance) / ((1.0 / 1e6) + (8.0 / ic.OffResistance));

        Assert.Equal(leak, sim.NodeVoltage(ic.Common), leak * 0.05);
        Assert.True(sim.NodeVoltage(ic.Common) < 0.02);
    }

    /// <summary>A closed path is a real resistance, and it divides against whatever it feeds.</summary>
    [Fact]
    public void ItsOnResistanceDividesWithTheLoad()
    {
        var (sim, ic, a, b, c, _, load) = Build();

        Address(sim, a, b, c, 7);

        var source = 0.4 * 8;
        var expected = source * 1e6 / (1e6 + ic.OnResistance);

        Assert.Equal(expected, sim.NodeVoltage(load.A), 1e-4);
        Assert.True(sim.NodeVoltage(load.A) < source, "a real switch resistance must lose something");
    }
}
