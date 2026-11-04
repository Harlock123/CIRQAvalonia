using Cirq.Components.Digital;
using Cirq.Components.Passive;
using Cirq.Components.Serialization;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

/// <summary>
/// The generic storage blocks, each against its characteristic table, driven from toggles the way
/// a student would drive one on a bench: set the inputs, make a clock edge, read Q.
/// </summary>
public class FlipFlopTests
{
    private static SimulationSettings Fast => new() { TimeStep = 2e-9, MaxTimeStep = 2e-9 };

    /// <summary>A block with a toggle on every input, simulated and ready to be stepped.</summary>
    private sealed class Bench
    {
        public Bench(FlipFlopBlock block)
        {
            Block = block;
            Circuit.Add(block);
            Circuit.Add(new Ground());

            foreach (var input in block.InputTerminals)
            {
                var toggle = Circuit.Add(new LogicToggle());
                Circuit.Connect(toggle.Out, input);
                Toggles[input.Name] = toggle;
            }

            Sim = new CircuitSimulator(Circuit, Fast);
            Sim.Reset();
            Sim.SolveOperatingPoint();
            Sim.Run(50e-9);
        }

        public Circuit Circuit { get; } = new();

        public FlipFlopBlock Block { get; }

        public Dictionary<string, LogicToggle> Toggles { get; } = [];

        public CircuitSimulator Sim { get; }

        public void Set(string pin, bool high)
        {
            Toggles[pin].State = high;
            Sim.Run(60e-9);
        }

        /// <summary>A full clock pulse: up, then down.</summary>
        public void Pulse(string pin = "CLK")
        {
            Set(pin, true);
            Set(pin, false);
        }

        public bool Q => Sim.NodeVoltage(Block.Q) > LogicLevels.Ttl.Vih;

        public bool QNot => Sim.NodeVoltage(Block.QNot) > LogicLevels.Ttl.Vih;
    }

    [Fact]
    public void ADFlipFlopTakesDOnTheRisingEdgeOnly()
    {
        var bench = new Bench(new DFlipFlop());

        Assert.False(bench.Q);
        Assert.True(bench.QNot);

        bench.Set("D", true);
        Assert.False(bench.Q);          // no edge yet

        bench.Set("CLK", true);
        Assert.True(bench.Q);           // the rising edge
        Assert.False(bench.QNot);

        bench.Set("D", false);
        Assert.True(bench.Q);           // D moving with the clock high changes nothing

        bench.Set("CLK", false);
        Assert.True(bench.Q);           // nor does the falling edge

        bench.Pulse();
        Assert.False(bench.Q);
    }

    [Fact]
    public void SetToTheFallingEdgeItWaitsForTheClockToDrop()
    {
        var bench = new Bench(new DFlipFlop { TriggersOnFallingEdge = true });

        bench.Set("D", true);
        bench.Set("CLK", true);
        Assert.False(bench.Q);

        bench.Set("CLK", false);
        Assert.True(bench.Q);
    }

    /// <summary>J K → Q on the edge: 0 0 holds, 0 1 resets, 1 0 sets, 1 1 toggles.</summary>
    [Fact]
    public void AJkFlipFlopFollowsItsCharacteristicTable()
    {
        var bench = new Bench(new JkFlipFlop());

        bench.Set("J", true);
        bench.Pulse();
        Assert.True(bench.Q);           // set

        bench.Set("J", false);
        bench.Pulse();
        Assert.True(bench.Q);           // hold

        bench.Set("K", true);
        bench.Pulse();
        Assert.False(bench.Q);          // reset

        bench.Set("J", true);
        bench.Pulse();
        Assert.True(bench.Q);           // toggle
        bench.Pulse();
        Assert.False(bench.Q);          // and again
        Assert.True(bench.QNot);
    }

    [Fact]
    public void ATFlipFlopDividesItsClockByTwo()
    {
        var bench = new Bench(new TFlipFlop());

        bench.Pulse();
        Assert.False(bench.Q);          // T low: hold

        bench.Set("T", true);

        List<bool> seen = [];
        for (var i = 0; i < 4; i++)
        {
            bench.Pulse();
            seen.Add(bench.Q);
        }

        Assert.Equal([true, false, true, false], seen);
    }

    [Fact]
    public void ADLatchIsTransparentWhileEnabledAndHoldsOnceNot()
    {
        var bench = new Bench(new DLatch());

        bench.Set("D", true);
        Assert.False(bench.Q);          // closed

        bench.Set("EN", true);
        Assert.True(bench.Q);           // open: Q follows D straight away

        bench.Set("D", false);
        Assert.False(bench.Q);          // and keeps following

        bench.Set("D", true);
        bench.Set("EN", false);
        bench.Set("D", false);
        Assert.True(bench.Q);           // closed again: it kept what it last saw
    }

    [Fact]
    public void AnSrLatchSetsResetsAndHolds()
    {
        var bench = new Bench(new SrLatch());

        bench.Set("S", true);
        bench.Set("S", false);
        Assert.True(bench.Q);
        Assert.False(bench.QNot);

        bench.Set("R", true);
        bench.Set("R", false);
        Assert.False(bench.Q);
        Assert.True(bench.QNot);
    }

    /// <summary>
    /// Both high is the NOR pair's forbidden state, Q and Q' both low; both let go at once is a
    /// race the model will not pretend to resolve, so Q goes unknown.
    /// </summary>
    [Fact]
    public void AnSrLatchWithBothInputsHighDrivesBothOutputsLowAndReleasingThemTogetherIsUnknown()
    {
        var latch = new SrLatch();
        var bench = new Bench(latch);

        bench.Toggles["S"].State = true;
        bench.Set("R", true);

        Assert.True(latch.IsForbidden);
        Assert.False(bench.Q);
        Assert.False(bench.QNot);

        bench.Toggles["S"].State = false;
        bench.Set("R", false);

        Assert.Equal(LogicState.Unknown, latch.State);

        // Let go of one first and the other decides.
        bench.Toggles["S"].State = true;
        bench.Set("R", true);
        bench.Set("R", false);
        bench.Set("S", false);
        Assert.Equal(LogicState.High, latch.State);
    }

    /// <summary>
    /// Four T flip-flops in a chain, each clocked by the one before's Q', count up in binary: the
    /// ripple counter, which is what the T flip-flop is for.
    /// </summary>
    [Fact]
    public void FourTFlipFlopsChainedOnQBarCountUpInBinary()
    {
        var circuit = new Circuit();
        circuit.Add(new Ground());
        var high = circuit.Add(new LogicToggle(true));
        var clock = circuit.Add(new LogicToggle());

        var stages = Enumerable.Range(0, 4).Select(_ => circuit.Add(new TFlipFlop())).ToArray();

        for (var i = 0; i < stages.Length; i++)
        {
            circuit.Connect(high.Out, stages[i].T);
            circuit.Connect(i == 0 ? clock.Out : stages[i - 1].QNot, stages[i].Clock);
        }

        var sim = new CircuitSimulator(circuit, Fast);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(100e-9);

        int Count() => stages.Select((s, i) => sim.NodeVoltage(s.Q) > 2.0 ? 1 << i : 0).Sum();

        for (var expected = 1; expected <= 17; expected++)
        {
            clock.State = true;
            sim.Run(200e-9);
            clock.State = false;
            sim.Run(200e-9);

            Assert.Equal(expected % 16, Count());
        }
    }

    [Fact]
    public void ATriStateBufferPassesItsInputOnlyWhileEnabled()
    {
        var circuit = new Circuit();
        var input = circuit.Add(new LogicToggle(true));
        var enable = circuit.Add(new LogicToggle());
        var buffer = circuit.Add(new TriStateBuffer());
        var pullDown = circuit.Add(new Resistor(10e3));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(input.Out, buffer.In);
        circuit.Connect(enable.Out, buffer.Enable);
        circuit.Connect(buffer.Out, pullDown.A);
        circuit.Connect(pullDown.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, Fast);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(100e-9);

        // Released: the pull-down has the wire.
        Assert.False(buffer.IsEnabled);
        Assert.True(sim.NodeVoltage(buffer.Out) < 0.1);

        enable.State = true;
        sim.Run(100e-9);
        Assert.True(buffer.IsEnabled);
        Assert.True(sim.NodeVoltage(buffer.Out) > LogicLevels.Ttl.Vih);

        input.State = false;
        sim.Run(100e-9);
        Assert.True(sim.NodeVoltage(buffer.Out) < LogicLevels.Ttl.Vil);
    }

    [Fact]
    public void AnActiveLowEnableIsTheOtherWayRound()
    {
        var circuit = new Circuit();
        circuit.Add(new Ground());
        var input = circuit.Add(new LogicToggle(true));
        var enable = circuit.Add(new LogicToggle());
        var buffer = circuit.Add(new TriStateBuffer { EnableActiveLow = true });

        circuit.Connect(input.Out, buffer.In);
        circuit.Connect(enable.Out, buffer.Enable);

        var sim = new CircuitSimulator(circuit, Fast);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(100e-9);
        Assert.True(buffer.IsEnabled);

        enable.State = true;
        sim.Run(100e-9);
        Assert.False(buffer.IsEnabled);
    }

    /// <summary>
    /// Two buffers on one wire. Taking turns, the wire is whichever is enabled. Both enabled and
    /// disagreeing, it is the two output resistances' divider — mid-rail, which no input can call
    /// a level — and both report the fight.
    /// </summary>
    [Fact]
    public void TwoBuffersShareAWireByTakingTurnsAndFightWhenTheyDoNot()
    {
        var circuit = new Circuit();
        circuit.Add(new Ground());
        var one = circuit.Add(new LogicToggle(true));
        var zero = circuit.Add(new LogicToggle(false));
        var enableA = circuit.Add(new LogicToggle(true));
        var enableB = circuit.Add(new LogicToggle(false));
        var a = circuit.Add(new TriStateBuffer());
        var b = circuit.Add(new TriStateBuffer());
        var reader = circuit.Add(new LogicGate(GateFunction.Buffer));

        circuit.Connect(one.Out, a.In);
        circuit.Connect(zero.Out, b.In);
        circuit.Connect(enableA.Out, a.Enable);
        circuit.Connect(enableB.Out, b.Enable);
        circuit.Connect(a.Out, b.Out);
        circuit.Connect(a.Out, reader.InputTerminals[0]);

        var sim = new CircuitSimulator(circuit, Fast);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(100e-9);

        Assert.True(sim.NodeVoltage(a.Out) > LogicLevels.Ttl.Vih);
        Assert.False(a.IsContending);

        // Turn about.
        enableA.State = false;
        enableB.State = true;
        sim.Run(100e-9);
        Assert.True(sim.NodeVoltage(a.Out) < LogicLevels.Ttl.Vil);
        Assert.False(b.IsContending);

        // Both on, disagreeing: two 25 Ω outputs at 3.4 V and 0.2 V meet at 1.8 V.
        enableA.State = true;
        sim.Run(100e-9);

        var v = sim.NodeVoltage(a.Out);
        Assert.Equal((LogicLevels.Ttl.Voh + LogicLevels.Ttl.Vol) / 2, v, 0.05);
        Assert.InRange(v, LogicLevels.Ttl.Vil, LogicLevels.Ttl.Vih);
        Assert.True(a.IsContending && b.IsContending);
        Assert.NotEmpty(a.Violations);
        Assert.Equal(64e-3, Math.Abs(a.Current), 2e-3);
        Assert.Equal(-a.Current, b.Current, 1e-6);
    }

    [Fact]
    public void BothBuffersReleasedLeaveTheWireToWhateverElseIsOnIt()
    {
        var circuit = new Circuit();
        circuit.Add(new Ground());
        var off = circuit.Add(new LogicToggle(false));
        var a = circuit.Add(new TriStateBuffer());
        var b = circuit.Add(new TriStateBuffer());

        circuit.Connect(off.Out, a.Enable);
        circuit.Connect(off.Out, b.Enable);
        circuit.Connect(a.Out, b.Out);

        // Nothing else holds the wire, and it still solves: a released output leaks like an input.
        var sim = new CircuitSimulator(circuit, Fast);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(100e-9);

        Assert.Equal(0.0, sim.NodeVoltage(a.Out), 1e-3);
        Assert.Equal("Hi-Z", a.ValueLabel);
    }

    [Fact]
    public void TheBlocksAndTheirSettingsSurviveTheFile()
    {
        var circuit = new Circuit();
        circuit.Add(new JkFlipFlop { TriggersOnFallingEdge = true });
        circuit.Add(new DFlipFlop());
        circuit.Add(new TFlipFlop());
        circuit.Add(new SrLatch());
        circuit.Add(new DLatch());
        circuit.Add(new TriStateBuffer { EnableActiveLow = true });
        circuit.Add(new LogicGate(GateFunction.Buffer));

        var back = CircuitSerializer.FromJson(CircuitSerializer.ToJson(circuit)).Circuit;

        Assert.True(back.Components.OfType<JkFlipFlop>().Single().TriggersOnFallingEdge);
        Assert.True(back.Components.OfType<TriStateBuffer>().Single().EnableActiveLow);
        Assert.Single(back.Components.OfType<DFlipFlop>());
        Assert.Single(back.Components.OfType<TFlipFlop>());
        Assert.Single(back.Components.OfType<SrLatch>());
        Assert.Single(back.Components.OfType<DLatch>());
        Assert.Equal(GateFunction.Buffer, back.Components.OfType<LogicGate>().Single().Function);
    }
}
