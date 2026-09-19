using Cirq.Components.Passive;
using Cirq.Components.Sources;
using Cirq.Core.Topology;
using Cirq.UI.Services;

namespace Cirq.UI.Tests;

/// <summary>
/// Covers the boundary between the UI and the engine: compiling on demand, advancing on a
/// background thread, and the pause/step/reset transport.
/// </summary>
public class SimulationControllerTests
{
    /// <summary>Source -> resistor -> capacitor -> ground, with a probe on the capacitor.</summary>
    private static (Circuit Circuit, SimulationController Controller, Capacitor Cap) RcCircuit()
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(5.0));
        var r = circuit.Add(new Resistor(1e3));
        var c = circuit.Add(new Capacitor(1e-6));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(source.Positive, r.A);
        circuit.Connect(r.B, c.A);
        circuit.Connect(c.B, gnd.Pin);
        circuit.Connect(source.Negative, gnd.Pin);

        var controller = new SimulationController(circuit);
        controller.Settings.TimeStep = 1e-6;
        controller.Settings.MaxTimeStep = 1e-6;
        return (circuit, controller, c);
    }

    /// <summary>Waits for a condition, so tests never depend on a fixed sleep.</summary>
    private static bool WaitFor(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    [Fact]
    public void RebuildCompilesTheCircuitAndReportsItsSize()
    {
        var (_, controller, _) = RcCircuit();

        Assert.True(controller.Rebuild());
        Assert.NotNull(controller.Simulator);
        Assert.False(controller.HasError);
        Assert.Contains("Ready", controller.Status);

        // Two non-ground nodes, plus one branch current for the voltage source.
        Assert.Equal(2, controller.Simulator!.Netlist.NodeCount);
        Assert.Equal(3, controller.Simulator.System.Size);

        controller.Dispose();
    }

    [Fact]
    public void RebuildReportsAMissingGroundInsteadOfThrowing()
    {
        var circuit = new Circuit();
        var source = circuit.Add(new DcVoltageSource(5.0));
        var r = circuit.Add(new Resistor(1e3));
        circuit.Connect(source.Positive, r.A);
        circuit.Connect(source.Negative, r.B);

        var controller = new SimulationController(circuit);

        Assert.False(controller.Rebuild());
        Assert.True(controller.HasError);
        Assert.Contains("ground", controller.Status, StringComparison.OrdinalIgnoreCase);

        controller.Dispose();
    }

    [Fact]
    public void PlayAdvancesSimulatedTimeOnABackgroundThread()
    {
        var (_, controller, _) = RcCircuit();
        controller.IsMaximumThroughput = true;

        controller.Play();
        Assert.True(controller.IsRunning);

        var advanced = WaitFor(() => controller.SimulationTime > 1e-4);
        controller.Pause();

        Assert.True(advanced, $"Simulation only reached t={controller.SimulationTime:g3}s.");
        Assert.False(controller.IsRunning);

        controller.Dispose();
    }

    [Fact]
    public void PauseStopsTheClockAndItStaysStopped()
    {
        var (_, controller, _) = RcCircuit();
        controller.IsMaximumThroughput = true;

        controller.Play();
        WaitFor(() => controller.SimulationTime > 1e-5);
        controller.Pause();

        var stoppedAt = controller.SimulationTime;
        Thread.Sleep(150);

        Assert.Equal(stoppedAt, controller.SimulationTime);
        controller.Dispose();
    }

    [Fact]
    public void StepOnceAdvancesExactlyOneTimePoint()
    {
        var (_, controller, _) = RcCircuit();
        controller.Rebuild();

        controller.StepOnce();
        var first = controller.SimulationTime;
        controller.StepOnce();
        var second = controller.SimulationTime;

        Assert.Equal(1e-6, first, 1e-12);
        Assert.Equal(2e-6, second, 1e-12);
        Assert.False(controller.IsRunning);

        controller.Dispose();
    }

    [Fact]
    public void ResetReturnsTheSimulationToItsBiasPoint()
    {
        var (_, controller, _) = RcCircuit();
        controller.IsMaximumThroughput = true;

        controller.Play();
        WaitFor(() => controller.SimulationTime > 1e-4);
        controller.Pause();
        Assert.True(controller.SimulationTime > 0);

        controller.ResetSimulation();

        Assert.Equal(0, controller.SimulationTime);
        Assert.False(controller.IsRunning);
        Assert.False(controller.HasError);

        controller.Dispose();
    }

    [Fact]
    public void EditingTheTopologyForcesARecompileBeforeTheNextRun()
    {
        var (circuit, controller, _) = RcCircuit();
        controller.Rebuild();
        var before = controller.Simulator;

        // Add a second resistor, as the canvas would when a component is dropped.
        var extra = circuit.Add(new Resistor(2e3));
        circuit.Connect(extra.A, circuit.Components.OfType<Capacitor>().First().A);
        controller.InvalidateTopology();

        controller.Play();
        WaitFor(() => controller.SimulationTime > 1e-5);
        controller.Pause();

        Assert.NotSame(before, controller.Simulator);
        Assert.Equal(3, controller.Simulator!.Netlist.NodeCount);

        controller.Dispose();
    }

    [Fact]
    public void RealTimePacingRunsSlowerThanMaximumThroughput()
    {
        // At a speed factor of 1e-4, one wall-clock second is 100 us of simulated time. The
        // unpaced run should get far further in the same window.
        static double AdvanceFor(bool maximumThroughput, int milliseconds)
        {
            var (_, controller, _) = RcCircuit();
            controller.IsMaximumThroughput = maximumThroughput;
            controller.SpeedFactor = 1e-4;
            controller.Play();
            Thread.Sleep(milliseconds);
            controller.Pause();
            var reached = controller.SimulationTime;
            controller.Dispose();
            return reached;
        }

        var paced = AdvanceFor(false, 300);
        var unpaced = AdvanceFor(true, 300);

        Assert.True(paced > 0, "Paced run made no progress.");
        Assert.True(unpaced > paced * 5,
            $"Expected maximum throughput to outpace real time: {unpaced:g3}s vs {paced:g3}s.");
    }

    [Fact]
    public void ASolverFailureIsSurfacedAsAStatusRatherThanACrash()
    {
        // Two ideal voltage sources of different values in parallel have no consistent solution,
        // which makes the MNA matrix singular.
        var circuit = new Circuit();
        var a = circuit.Add(new DcVoltageSource(5.0));
        var b = circuit.Add(new DcVoltageSource(3.0));
        var gnd = circuit.Add(new Ground());

        circuit.Connect(a.Positive, b.Positive);
        circuit.Connect(a.Negative, gnd.Pin);
        circuit.Connect(b.Negative, gnd.Pin);

        var controller = new SimulationController(circuit);
        var rebuilt = controller.Rebuild();

        Assert.False(rebuilt);
        Assert.True(controller.HasError);
        Assert.False(string.IsNullOrWhiteSpace(controller.Status));

        controller.Dispose();
    }
}
