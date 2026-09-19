using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Numerics;
using Cirq.Engine.Simulation;
using Cirq.Engine.Tests.Support;

namespace Cirq.Engine.Tests;

/// <summary>
/// The t=0 state. Two modes have to behave differently and both have to actually solve the
/// network: a bias-point run settles the circuit as if it had been powered for ever, while a
/// use-initial-conditions run starts the storage elements empty so a source applied at t=0 is a
/// real step.
/// </summary>
public class InitialConditionTests
{
    [Fact]
    public void BiasPointChargesTheCapacitorToTheSupply()
    {
        var (circuit, output, _, _, _) = CircuitFactory.RcLowPass(1e3, 1e-6, 5.0);
        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = false });

        sim.Reset();
        sim.SolveOperatingPoint();

        // At DC the capacitor is an open circuit, so no current flows and the output sits at 5 V.
        Assert.Equal(5.0, sim.NodeVoltage(output), 1e-3);
    }

    [Fact]
    public void InitialConditionsStartTheCapacitorDischarged()
    {
        var (circuit, output, _, _, _) = CircuitFactory.RcLowPass(1e3, 1e-6, 5.0);
        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });

        sim.Reset();
        sim.SolveOperatingPoint();

        Assert.Equal(0.0, sim.NodeVoltage(output), 1e-3);
    }

    [Fact]
    public void AnExplicitInitialVoltageIsHonoured()
    {
        var (circuit, output, _, _, capacitor) = CircuitFactory.RcLowPass(1e3, 1e-6, 5.0);
        capacitor.InitialVoltage = 2.0;

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();

        Assert.Equal(2.0, sim.NodeVoltage(output), 1e-2);
    }

    [Fact]
    public void InitialConditionsStartTheInductorUnenergised()
    {
        var (circuit, _, inductor, _) = CircuitFactory.RlSeries(100, 10e-3, 5.0);
        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });

        sim.Reset();
        sim.SolveOperatingPoint();

        Assert.Equal(0.0, Math.Abs(inductor.Current), 1e-9);
    }

    [Fact]
    public void BiasPointShortsTheInductorToItsSteadyStateCurrent()
    {
        var (circuit, _, inductor, _) = CircuitFactory.RlSeries(100, 10e-3, 5.0);
        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = false });

        sim.Reset();
        sim.SolveOperatingPoint();

        // A DC-shorted inductor passes the full 5 V / 100 R.
        Assert.Equal(0.05, Math.Abs(inductor.Current), 1e-6);
    }

    [Fact]
    public void TheRestOfTheNetworkIsStillSolvedUnderInitialConditions()
    {
        // Skipping the bias solve entirely would leave every node at zero. The divider node must
        // already carry its correct voltage at t=0, even though the capacitor starts discharged.
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(10.0));
        var top = circuit.Add(new Resistor(1e3));
        var bottom = circuit.Add(new Resistor(1e3));
        var capacitor = circuit.Add(new Capacitor(1e-9));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Positive, top.A);
        circuit.Connect(top.B, bottom.A);
        circuit.Connect(bottom.B, gnd.Pin);
        circuit.Connect(source.Negative, gnd.Pin);
        // The capacitor hangs off the supply rail, not the divider node.
        circuit.Connect(capacitor.A, source.Positive);
        circuit.Connect(capacitor.B, gnd.Pin);

        var sim = new CircuitSimulator(circuit, new SimulationSettings { UseInitialConditions = true });
        sim.Reset();
        sim.SolveOperatingPoint();

        Assert.Equal(10.0, sim.NodeVoltage(top.A), 1e-3);
        Assert.Equal(5.0, sim.NodeVoltage(top.B), 1e-3);
    }

    [Fact]
    public void AnUnsolvableTopologyIsReportedAtTheOperatingPoint()
    {
        // Two ideal voltage sources of different values in parallel: the branch equations are
        // linearly dependent, so this must be caught rather than silently "solved".
        var circuit = new Circuit();
        var a = circuit.Add(new DcVoltageSource(5.0));
        var b = circuit.Add(new DcVoltageSource(3.0));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(a.Positive, b.Positive);
        circuit.Connect(a.Negative, gnd.Pin);
        circuit.Connect(b.Negative, gnd.Pin);

        foreach (var useInitialConditions in new[] { true, false })
        {
            var sim = new CircuitSimulator(circuit, new SimulationSettings
            {
                UseInitialConditions = useInitialConditions,
            });

            Assert.Throws<SingularMatrixException>(() =>
            {
                sim.Reset();
                sim.SolveOperatingPoint();
            });
        }
    }
}
