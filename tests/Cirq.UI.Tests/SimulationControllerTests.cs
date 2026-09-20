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

    /// <summary>
    /// Runs the transport for a measured window and reports how far it got and how long that
    /// really took — the wall clock is measured rather than assumed, because a sleep on a busy
    /// machine is a lower bound and nothing more.
    /// </summary>
    private static (double Advanced, double Seconds) Advance(bool maximumThroughput, double speed)
    {
        var (_, controller, _) = RcCircuit();
        controller.IsMaximumThroughput = maximumThroughput;
        controller.SpeedFactor = speed;
        controller.Play();

        // Wait for the transport to actually be running before starting the clock. On a loaded
        // machine it can be a while before that thread is scheduled at all, and timing from before
        // then measures how busy the machine is rather than how the run is paced.
        Assert.True(WaitFor(() => controller.SimulationTime > 0), "the transport never started stepping");

        var from = controller.SimulationTime;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Thread.Sleep(300);

        clock.Stop();
        controller.Pause();

        var advanced = controller.SimulationTime - from;
        controller.Dispose();

        return (advanced, clock.Elapsed.TotalSeconds);
    }

    /// <summary>
    /// Pacing is a <b>ceiling</b>: at a hundredth of real time, a second of wall clock buys ten
    /// milliseconds of simulated time and no more, however fast the machine is.
    /// <para>
    /// Asserted as a ceiling rather than against a second run, because this runs on shared CI
    /// machines and a comparison between two runs measures the scheduler as much as the pacing.
    /// A busy machine can only make a paced run fall <i>behind</i> its cap, never overshoot it,
    /// so load moves this assertion in the safe direction.
    /// </para>
    /// <para>
    /// A hundredth rather than something slower because the pacer steps until it <i>passes</i> its
    /// target, so it always takes at least one step per slice. Below about a thousandth that floor
    /// governs instead of the speed factor — a run at 1e-4 lands seven times over its nominal cap,
    /// which is the model working as written but makes the cap the wrong thing to measure.
    /// </para>
    /// </summary>
    [Fact]
    public void RealTimePacingCapsHowFarARunCanGet()
    {
        const double speed = 1e-2;

        var paced = Advance(maximumThroughput: false, speed);
        var ceiling = paced.Seconds * speed * 1.5;

        Assert.True(paced.Advanced <= ceiling,
            $"paced reached {paced.Advanced:g3} s in {paced.Seconds:g3} s of wall clock, past its {ceiling:g3} s cap");
    }

    /// <summary>
    /// And maximum throughput does not consult the clock at all, so it leaves that ceiling far
    /// behind.
    /// <para>
    /// Measured against a <i>much</i> slower nominal speed than the test above uses, and that is
    /// the point of the number rather than an oversight. The ceiling scales with the speed factor
    /// while throughput is fixed by the machine, so a low factor is what puts distance between
    /// them: the margin here is about five hundredfold on a quiet machine and still around fifty
    /// on a loaded CI runner. At a hundredth it was five, and a slow afternoon on shared hardware
    /// was enough to fail it.
    /// </para>
    /// </summary>
    [Fact]
    public void AndMaximumThroughputLeavesThatCeilingFarBehind()
    {
        const double speed = 1e-4;

        var unpaced = Advance(maximumThroughput: true, speed);
        var ceiling = unpaced.Seconds * speed;

        Assert.True(unpaced.Advanced > ceiling * 50,
            $"maximum throughput only reached {unpaced.Advanced:g3} s in {unpaced.Seconds:g3} s of wall clock, " +
            $"against a pacing cap of {ceiling:g3} s");
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
