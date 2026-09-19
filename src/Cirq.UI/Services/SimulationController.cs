using System.Diagnostics;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Simulation;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cirq.UI.Services;

/// <summary>
/// Drives the engine from a background thread and keeps it decoupled from the render loop.
/// <para>
/// The worker advances simulated time in proportion to elapsed wall-clock time scaled by
/// <see cref="SpeedFactor"/>, and gives itself a fixed wall-clock budget per slice so a heavy
/// circuit falls behind real time instead of freezing the application. Probe samples land in
/// lock-guarded ring buffers, so the UI thread can snapshot them at its own rate without ever
/// blocking the solver.
/// </para>
/// </summary>
public sealed partial class SimulationController : ObservableObject, IDisposable
{
    /// <summary>Wall-clock time the worker may spend inside one slice before yielding.</summary>
    private static readonly TimeSpan SliceBudget = TimeSpan.FromMilliseconds(8);

    private readonly Lock _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private volatile bool _topologyDirty = true;

    public SimulationController(Circuit circuit)
    {
        Circuit = circuit;
    }

    public Circuit Circuit { get; }

    public SimulationSettings Settings { get; } = new()
    {
        TimeStep = 1e-6,
        MaxTimeStep = 1e-5,
        Integration = IntegrationMethod.Trapezoidal,
        UseInitialConditions = true,
    };

    /// <summary>The live simulator. Null until the circuit has been compiled at least once.</summary>
    public CircuitSimulator? Simulator { get; private set; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>
    /// Simulated seconds per wall-clock second. 1.0 is real time; the UI exposes 0.1x upward, and
    /// <see cref="IsMaximumThroughput"/> removes the pacing entirely.
    /// </summary>
    [ObservableProperty]
    public partial double SpeedFactor { get; set; } = 1e-3;

    /// <summary>When set, the worker runs flat out instead of pacing against the clock.</summary>
    [ObservableProperty]
    public partial bool IsMaximumThroughput { get; set; }

    [ObservableProperty]
    public partial double SimulationTime { get; private set; }

    [ObservableProperty]
    public partial string Status { get; private set; } = "Idle";

    [ObservableProperty]
    public partial bool HasError { get; private set; }

    /// <summary>Time points solved per wall-clock second, for the status bar.</summary>
    [ObservableProperty]
    public partial double StepsPerSecond { get; private set; }

    /// <summary>Raised on the worker thread after a batch of time points has been solved.</summary>
    public event Action? Advanced;

    /// <summary>Marks the topology as changed so the next run recompiles the netlist.</summary>
    public void InvalidateTopology()
    {
        _topologyDirty = true;
        if (IsRunning) Pause();
        Status = "Circuit modified - press Reset to rebuild";
    }

    /// <summary>Compiles the circuit and solves its bias point. Safe to call repeatedly.</summary>
    public bool Rebuild()
    {
        lock (_gate)
        {
            try
            {
                Simulator = new CircuitSimulator(Circuit, Settings);
                Simulator.Reset();
                Simulator.SolveOperatingPoint();
                _topologyDirty = false;
                SimulationTime = 0;
                HasError = false;
                Status = $"Ready - {Simulator.Netlist.NodeCount} nodes, {Simulator.System.Size} unknowns";
                return true;
            }
            catch (Exception ex)
            {
                Simulator = null;
                HasError = true;
                Status = Describe(ex);
                return false;
            }
        }
    }

    public void Play()
    {
        if (IsRunning) return;
        if (_topologyDirty || Simulator is null)
        {
            if (!Rebuild()) return;
        }

        _cancellation = new CancellationTokenSource();
        IsRunning = true;
        HasError = false;
        Status = "Running";
        _worker = Task.Run(() => RunLoop(_cancellation.Token));
    }

    public void Pause()
    {
        if (!IsRunning) return;
        _cancellation?.Cancel();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The cancellation itself is not an error worth surfacing.
        }

        _worker = null;
        _cancellation?.Dispose();
        _cancellation = null;
        IsRunning = false;
        if (!HasError) Status = "Paused";
    }

    public void TogglePlayPause()
    {
        if (IsRunning) Pause();
        else Play();
    }

    /// <summary>Advances exactly one time point, for stepping through an edge by hand.</summary>
    public void StepOnce()
    {
        Pause();
        if (_topologyDirty || Simulator is null)
        {
            if (!Rebuild()) return;
        }

        lock (_gate)
        {
            try
            {
                Simulator!.Step();
                SimulationTime = Simulator.Time;
                Status = $"Stepped to {FormatTime(SimulationTime)}";
            }
            catch (Exception ex)
            {
                HasError = true;
                Status = Describe(ex);
            }
        }

        Advanced?.Invoke();
    }

    /// <summary>Stops, clears all history, and re-solves the bias point.</summary>
    public void ResetSimulation()
    {
        Pause();
        Rebuild();
        Advanced?.Invoke();
    }

    private void RunLoop(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        var previous = clock.Elapsed;
        var stepsThisSecond = 0;
        var rateWindowStart = clock.Elapsed;

        while (!token.IsCancellationRequested)
        {
            var now = clock.Elapsed;
            var wallDelta = (now - previous).TotalSeconds;
            previous = now;

            // A long stall (a breakpoint, a slow repaint) should not be chased down in one slice.
            wallDelta = Math.Min(wallDelta, 0.05);

            var sliceStart = clock.Elapsed;
            var steps = 0;

            try
            {
                lock (_gate)
                {
                    var simulator = Simulator;
                    if (simulator is null) break;

                    if (IsMaximumThroughput)
                    {
                        while (clock.Elapsed - sliceStart < SliceBudget && !token.IsCancellationRequested)
                        {
                            simulator.Step();
                            steps++;
                        }
                    }
                    else
                    {
                        var budget = wallDelta * SpeedFactor;
                        var target = simulator.Time + budget;
                        while (simulator.Time < target &&
                               clock.Elapsed - sliceStart < SliceBudget &&
                               !token.IsCancellationRequested)
                        {
                            simulator.Step();
                            steps++;
                        }
                    }

                    SimulationTime = simulator.Time;
                }
            }
            catch (Exception ex)
            {
                HasError = true;
                Status = Describe(ex);
                IsRunning = false;
                return;
            }

            stepsThisSecond += steps;
            if (clock.Elapsed - rateWindowStart > TimeSpan.FromSeconds(0.5))
            {
                StepsPerSecond = stepsThisSecond / (clock.Elapsed - rateWindowStart).TotalSeconds;
                stepsThisSecond = 0;
                rateWindowStart = clock.Elapsed;
            }

            if (steps > 0) Advanced?.Invoke();

            // Yield so the render thread gets the CPU even at maximum throughput.
            Thread.Sleep(1);
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        CircuitTopologyException => $"Topology: {ex.Message}",
        ConvergenceException => $"Convergence: {ex.Message}",
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    public static string FormatTime(double seconds) => Cirq.Core.Units.SiPrefix.Format(seconds, "s");

    public void Dispose()
    {
        Pause();
        _cancellation?.Dispose();
    }
}
