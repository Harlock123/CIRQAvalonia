using Cirq.Components.Digital;
using Cirq.Components.Sources;
using Cirq.Core.Digital;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;

namespace Cirq.Engine.Tests;

/// <summary>
/// What a circuit made mostly of logic needs from the engine: a factorisation reused while the
/// matrix stands still, a matrix cleared exactly, the latest scheduling of an output winning, and
/// a clock that never loses an edge to floating point.
/// </summary>
public class LogicSolveTests
{
    private static (Circuit Circuit, DcVoltageSource Rail, Ground Ground) Bench()
    {
        var circuit = new Circuit();
        var rail = circuit.Add(new DcVoltageSource(5.0));
        var ground = circuit.Add(new Ground());
        circuit.Connect(rail.Negative, ground.Pin);
        return (circuit, rail, ground);
    }

    /// <summary>
    /// A counter clocked for a while stamps the same matrix at nearly every time point, so most
    /// solves reuse the last factorisation — and the count is still exactly right.
    /// </summary>
    [Fact]
    public void ACountingCircuitReusesItsFactorisation()
    {
        var (circuit, rail, ground) = Bench();
        var counter = circuit.Add(new Ic74161());
        var clock = circuit.Add(new ClockSource(1e6));
        circuit.Connect(counter.Vcc, rail.Positive);
        circuit.Connect(counter.Gnd, ground.Pin);
        circuit.Connect(counter.Clock, clock.Out);
        foreach (var pin in new[] { counter.MasterReset, counter.ParallelEnable, counter.CountEnableP, counter.CountEnableT })
            circuit.Connect(pin, rail.Positive);
        foreach (var d in counter.Data) circuit.Connect(d, ground.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 0.25e-6, MaxTimeStep = 1e-6, UseInitialConditions = true });
        int Count() => counter.Outputs.Select((q, i) => sim.NodeVoltage(q) > 2.5 ? 1 << i : 0).Sum();

        sim.RunTransient(20.75e-6);
        var before = Count();

        // Sixteen more microseconds is sixteen more edges: all the way round and back.
        sim.Run(16e-6);
        Assert.Equal(before, Count());
        sim.Run(3e-6);
        Assert.Equal((before + 3) % 16, Count());
        Assert.True(sim.ReusedFactorisations > 40, $"only {sim.ReusedFactorisations} solves reused a factorisation");
    }

    /// <summary>Clearing the matrix zeroes every entry that was stamped, however it was stamped.</summary>
    [Fact]
    public void ClearingTheMatrixZeroesEverythingStamped()
    {
        var (circuit, _, _) = Bench();
        var system = new MnaSystem(circuit.BuildNetlist(), 3, new Dictionary<(Guid, int), int> { [(Guid.NewGuid(), 0)] = 0 });

        system.StampConductance(0, 1, 2.0);
        system.Add(2, 3, 5.0);
        system.Add(3, 3, -1.0);
        system.AddRhs(1, 7.0);

        var snapshot = new MatrixSnapshot();
        system.Record(snapshot);
        Assert.True(system.Matches(snapshot));

        system.Clear();
        Assert.All(system.Matrix, row => Assert.All(row, v => Assert.Equal(0.0, v)));
        Assert.All(system.Rhs, v => Assert.Equal(0.0, v));
        Assert.False(system.Matches(snapshot));

        // The same stamps again match what was recorded; a different value does not.
        system.StampConductance(0, 1, 2.0);
        system.Add(2, 3, 5.0);
        system.Add(3, 3, -1.0);
        Assert.True(system.Matches(snapshot));
        system.Add(3, 3, 1e-9);
        Assert.False(system.Matches(snapshot));
    }

    /// <summary>
    /// A part asks for one output state a long delay away, then — before that arrives — for another
    /// a short delay away. The second request is the one that stands: the first must not apply when
    /// it comes due, which is what stops a memory that has let go of a bus from grabbing it again.
    /// </summary>
    [Fact]
    public void TheLatestSchedulingOfAnOutputWins()
    {
        var (circuit, rail, ground) = Bench();
        var part = circuit.Add(new Changeable());
        var load = circuit.Add(new Cirq.Components.Passive.Resistor(10e3));
        circuit.Connect(part.Out, load.A);
        circuit.Connect(load.B, ground.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 10e-9, MaxTimeStep = 10e-9, UseInitialConditions = true });
        sim.RunTransient(1e-6);

        Assert.Equal(LogicState.HighImpedance, part.GetOutputState(0));
        Assert.True(part.WentHigh == false, "the superseded transition to high was applied");
    }

    /// <summary>
    /// A 1 MHz clock with a time step that lands exactly on its edges: every edge is there, a
    /// microsecond apart. A plain floor of the phase lost some of them, because 15.5 µs ÷ 1 µs is
    /// not always 15.5 in floating point.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AClockLosesNoEdgesToRounding(bool oscillator)
    {
        var (circuit, rail, ground) = Bench();
        IDigitalDevice source;
        Terminal output;

        if (oscillator)
        {
            var osc = circuit.Add(new OscillatorModule(1e6));
            circuit.Connect(osc.Vcc, rail.Positive);
            circuit.Connect(osc.Gnd, ground.Pin);
            (source, output) = (osc, osc.Output);
        }
        else
        {
            var clock = circuit.Add(new ClockSource(1e6));
            (source, output) = (clock, clock.Out);
        }

        var load = circuit.Add(new Cirq.Components.Passive.Resistor(10e3));
        circuit.Connect(output, load.A);
        circuit.Connect(load.B, ground.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 0.5e-6, MaxTimeStep = 1e-6, UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();

        List<double> rising = [];
        sim.Scheduler.EventApplied += e =>
        {
            if (ReferenceEquals(e.Device, source) && e.NewState == LogicState.High) rising.Add(e.Timestamp);
        };

        sim.Run(60e-6);

        var gaps = rising.Zip(rising.Skip(1), (a, b) => b - a).ToList();
        Assert.True(rising.Count >= 59, $"{rising.Count} rising edges in 60 µs");
        Assert.All(gaps, g => Assert.Equal(1e-6, g, 9));
    }

    /// <summary>Asks for high 300 ns out at 100 ns, then for released 20 ns out at 150 ns.</summary>
    private sealed class Changeable : DigitalComponent
    {
        public Changeable()
        {
            Out = new Terminal("out", "OUT", TerminalType.Output, new Cirq.Core.Primitives.Point(40, 0));
            Terminals = [Out];
            ConfigurePins([], [Out]);
        }

        public Terminal Out { get; }

        public bool WentHigh { get; private set; }

        public override string ComponentType => "Test part";

        private bool _first, _second;

        public override void EvaluateLogic(IDigitalContext context)
        {
            if (!_first && context.Time >= 100e-9)
            {
                _first = true;
                context.Schedule(this, 0, LogicState.High, 300e-9);
            }

            if (!_second && context.Time >= 150e-9)
            {
                _second = true;
                context.Schedule(this, 0, LogicState.HighImpedance, 20e-9);
            }

            if (GetOutputState(0) == LogicState.High) WentHigh = true;
        }

        public override void ResetLogic()
        {
            base.ResetLogic();
            _first = _second = WentHigh = false;
        }
    }

    /// <summary>
    /// A board big enough to be eliminated sparsely, with a capacitor on it, stepped at lengths that
    /// never repeat: each length changes the capacitor's companion in four entries, which is solved
    /// from the previous factorisation with a low-rank correction rather than by factoring again —
    /// and the charging curve is still the RC one.
    /// </summary>
    [Fact]
    public void AFewChangedEntriesAreSolvedWithoutRefactoring()
    {
        var (circuit, rail, ground) = Bench();
        Terminal at = rail.Positive;
        for (var i = 0; i < 60; i++)
        {
            var r = circuit.Add(new Cirq.Components.Passive.Resistor(1000.0 / 60));
            circuit.Connect(r.A, at);
            at = r.B;
        }

        var c = circuit.Add(new Cirq.Components.Passive.Capacitor(1e-6) { InitialVoltage = 0 });
        circuit.Connect(c.A, at);
        circuit.Connect(c.B, ground.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { TimeStep = 1e-6, MinTimeStep = 1e-12, MaxTimeStep = 1e-4, UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();

        double[] steps = [1e-6, 3.1e-6, 0.7e-6, 5.3e-6, 2.2e-6];
        for (var i = 0; sim.Time < 2e-3; i++) sim.Step(steps[i % steps.Length]);

        Assert.True(sim.CorrectedSolves > 100, $"only {sim.CorrectedSolves} corrected solves");
        Assert.Equal(5 * (1 - Math.Exp(-sim.Time / 1e-3)), sim.NodeVoltage(c.A), 3e-3);
    }
}
