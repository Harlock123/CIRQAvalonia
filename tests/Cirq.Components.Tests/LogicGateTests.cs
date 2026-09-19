using Cirq.Components.Digital;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Components.Tests;

public class LogicGateTests
{
    /// <summary>Drives a two-input gate from two logic toggles and returns the solved output.</summary>
    private static double EvaluateGate(GateFunction function, bool a, bool b)
    {
        var circuit = new Circuit();
        var gate = circuit.Add(new LogicGate(function, 2));
        var swA = circuit.Add(new LogicToggle(a));
        var swB = circuit.Add(new LogicToggle(b));
        circuit.Add(new Ground());

        circuit.Connect(swA.Out, gate.InputTerminals[0]);
        circuit.Connect(swB.Out, gate.InputTerminals[1]);

        var sim = new CircuitSimulator(circuit);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(100e-9);

        return sim.NodeVoltage(gate.Out);
    }

    [Theory]
    [InlineData(GateFunction.And, false, false, false)]
    [InlineData(GateFunction.And, true, false, false)]
    [InlineData(GateFunction.And, false, true, false)]
    [InlineData(GateFunction.And, true, true, true)]
    [InlineData(GateFunction.Or, false, false, false)]
    [InlineData(GateFunction.Or, true, false, true)]
    [InlineData(GateFunction.Or, true, true, true)]
    [InlineData(GateFunction.Nand, false, false, true)]
    [InlineData(GateFunction.Nand, true, true, false)]
    [InlineData(GateFunction.Nor, false, false, true)]
    [InlineData(GateFunction.Nor, true, false, false)]
    [InlineData(GateFunction.Xor, false, false, false)]
    [InlineData(GateFunction.Xor, true, false, true)]
    [InlineData(GateFunction.Xor, true, true, false)]
    [InlineData(GateFunction.Xnor, false, false, true)]
    [InlineData(GateFunction.Xnor, true, false, false)]
    [InlineData(GateFunction.Xnor, true, true, true)]
    public void TruthTablesHoldEndToEndThroughTheAnalogDomain(
        GateFunction function, bool a, bool b, bool expectedHigh)
    {
        var output = EvaluateGate(function, a, b);

        if (expectedHigh) Assert.True(output > LogicLevels.Ttl.Vih, $"Expected a logic high, measured {output:0.###} V.");
        else Assert.True(output < LogicLevels.Ttl.Vil, $"Expected a logic low, measured {output:0.###} V.");
    }

    [Fact]
    public void InverterFollowsItsInput()
    {
        var circuit = new Circuit();
        var not = circuit.Add(new LogicGate(GateFunction.Not));
        var sw = circuit.Add(new LogicToggle(false));
        circuit.Add(new Ground());
        circuit.Connect(sw.Out, not.InputTerminals[0]);

        // A toggle flipped between runs is only noticed on the next evaluation pass, and takes
        // effect a propagation delay after that, so the step has to be small enough to resolve it.
        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(100e-9);
        Assert.True(sim.NodeVoltage(not.Out) > LogicLevels.Ttl.Vih);

        sw.State = true;
        sim.Run(100e-9);
        Assert.True(sim.NodeVoltage(not.Out) < LogicLevels.Ttl.Vil);
    }

    [Fact]
    public void OutputChangesOnlyAfterThePropagationDelay()
    {
        var circuit = new Circuit();
        var not = circuit.Add(new LogicGate(GateFunction.Not) { PropagationDelay = 50e-9 });
        var sw = circuit.Add(new LogicToggle(false));
        circuit.Add(new Ground());
        circuit.Connect(sw.Out, not.InputTerminals[0]);

        var settings = new SimulationSettings { TimeStep = 1e-9, MaxTimeStep = 1e-9 };
        var sim = new CircuitSimulator(circuit, settings);
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-9);

        // Output starts high (input low). Flip the input and watch when the output responds.
        Assert.True(sim.NodeVoltage(not.Out) > LogicLevels.Ttl.Vih);
        sw.State = true;

        var startTime = sim.Time;
        double? switchedAt = null;
        while (sim.Time < startTime + 300e-9)
        {
            sim.Step();
            if (switchedAt is null && sim.NodeVoltage(not.Out) < LogicLevels.Ttl.Vil) switchedAt = sim.Time - startTime;
        }

        Assert.NotNull(switchedAt);
        // The toggle adds its own 1 ns, and the input edge is itself seen one step late.
        Assert.InRange(switchedAt.Value, 45e-9, 70e-9);
    }

    [Fact]
    public void GatesCascadeThroughAnalogNets()
    {
        // (A AND B) NOR C
        var circuit = new Circuit();
        var and = circuit.Add(new LogicGate(GateFunction.And));
        var nor = circuit.Add(new LogicGate(GateFunction.Nor));
        var swA = circuit.Add(new LogicToggle(true));
        var swB = circuit.Add(new LogicToggle(true));
        var swC = circuit.Add(new LogicToggle(false));
        circuit.Add(new Ground());

        circuit.Connect(swA.Out, and.InputTerminals[0]);
        circuit.Connect(swB.Out, and.InputTerminals[1]);
        circuit.Connect(and.Out, nor.InputTerminals[0]);
        circuit.Connect(swC.Out, nor.InputTerminals[1]);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 5e-9, MaxTimeStep = 5e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(200e-9);

        // AND is high, so NOR is low.
        Assert.True(sim.NodeVoltage(nor.Out) < LogicLevels.Ttl.Vil);

        swB.State = false;
        sim.Run(200e-9);
        // AND is now low and C is low, so NOR goes high.
        Assert.True(sim.NodeVoltage(nor.Out) > LogicLevels.Ttl.Vih);
    }

    [Fact]
    public void UnknownInputsPropagateOnlyWhenTheyMatter()
    {
        // A low input pins an AND gate low regardless of anything unknown alongside it.
        Assert.Equal(LogicState.Low, LogicGate.Evaluate(GateFunction.And, [LogicState.Low, LogicState.Unknown]));
        Assert.Equal(LogicState.Unknown, LogicGate.Evaluate(GateFunction.And, [LogicState.High, LogicState.Unknown]));
        Assert.Equal(LogicState.High, LogicGate.Evaluate(GateFunction.Or, [LogicState.High, LogicState.Unknown]));
        Assert.Equal(LogicState.Unknown, LogicGate.Evaluate(GateFunction.Xor, [LogicState.High, LogicState.Unknown]));
    }

    [Fact]
    public void RingOscillatorRunsAtTwiceItsLoopDelay()
    {
        // Three inverters in a loop. The delays are deliberately unequal: with three identical
        // gates all starting low the ring sits in a symmetric mode where every stage flips
        // together, which is a real solution of the ideal equations but not the mode a physical
        // ring settles into. Unequal delays break that symmetry the way device mismatch does.
        double[] delays = [15e-9, 20e-9, 25e-9];
        var loopDelay = delays.Sum();

        var circuit = new Circuit();
        var inverters = delays
            .Select(d => circuit.Add(new LogicGate(GateFunction.Not) { PropagationDelay = d }))
            .ToArray();
        circuit.Add(new Ground());

        for (var i = 0; i < inverters.Length; i++)
            circuit.Connect(inverters[i].Out, inverters[(i + 1) % inverters.Length].InputTerminals[0]);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-9, MaxTimeStep = 2e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();

        var risingEdges = new List<double>();
        var wasHigh = sim.NodeVoltage(inverters[0].Out) > LogicLevels.Ttl.Vih;

        while (sim.Time < 2e-6)
        {
            sim.Step();
            var isHigh = sim.NodeVoltage(inverters[0].Out) > LogicLevels.Ttl.Vih;
            if (isHigh && !wasHigh) risingEdges.Add(sim.Time);
            wasHigh = isHigh;
        }

        Assert.True(risingEdges.Count >= 6, $"Expected the ring to oscillate, saw {risingEdges.Count} edges.");

        // A signal must travel the loop twice to return to its starting polarity.
        var period = (risingEdges[^1] - risingEdges[0]) / (risingEdges.Count - 1);
        Assert.Equal(2 * loopDelay, period, 2 * loopDelay * 0.05);
    }

    [Fact]
    public void SymmetricRingHoldsItsLockstepMode()
    {
        // The companion to the test above: identical gates starting from identical states stay in
        // lockstep and flip together every propagation delay. Documented here so the behaviour is
        // pinned rather than rediscovered as a bug.
        const double delay = 20e-9;
        var circuit = new Circuit();
        var inverters = Enumerable.Range(0, 3)
            .Select(_ => circuit.Add(new LogicGate(GateFunction.Not) { PropagationDelay = delay }))
            .ToArray();
        circuit.Add(new Ground());

        for (var i = 0; i < 3; i++)
            circuit.Connect(inverters[i].Out, inverters[(i + 1) % 3].InputTerminals[0]);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-9, MaxTimeStep = 1e-9 });
        sim.Reset();
        sim.SolveOperatingPoint();
        sim.Run(500e-9);

        var levels = inverters.Select(g => sim.NodeVoltage(g.Out)).ToArray();
        Assert.All(levels, v => Assert.Equal(levels[0], v, 1e-6));
    }
}
