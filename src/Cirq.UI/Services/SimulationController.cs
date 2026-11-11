using System.Diagnostics;
using Cirq.Components.Boards;
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

    /// <summary>
    /// Electrical rule violations found on any development board in the circuit, or empty.
    /// <para>
    /// Surfaced separately from <see cref="Status"/> because these are not solver failures — the
    /// circuit solves perfectly well, it is the hardware that would not survive it.
    /// </para>
    /// </summary>
    [ObservableProperty]
    public partial string BoardWarning { get; private set; } = string.Empty;

    /// <summary>Time points solved per wall-clock second, for the status bar.</summary>
    [ObservableProperty]
    public partial double StepsPerSecond { get; private set; }

    /// <summary>Raised on the worker thread after a batch of time points has been solved.</summary>
    public event Action? Advanced;

    /// <summary>
    /// Whether the current in each wire is worked out as the run goes. Off unless something is
    /// drawing it, because it costs a pass over every wire per slice.
    /// </summary>
    public bool TrackWireCurrents { get; set; }

    private IReadOnlyDictionary<WireSegment, double> _wireCurrents =
        new Dictionary<WireSegment, double>();

    /// <summary>
    /// What each wire was carrying at the last solved point.
    /// <para>
    /// A snapshot, replaced wholesale rather than edited in place, because it is written on the
    /// worker thread and read on the UI one. Swapping a reference is atomic; a reader either gets
    /// the whole of the previous answer or the whole of the new one, and never a half-built
    /// dictionary being enumerated as it grows.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<WireSegment, double> WireCurrents => _wireCurrents;

    /// <summary>
    /// Works the currents out now, for when nothing is running. Safe only because there is no
    /// worker touching the matrix while the simulation is stopped — during a run the snapshot is
    /// taken inside the same lock the solver holds.
    /// </summary>
    public void RefreshWireCurrents()
    {
        if (IsRunning) return;

        lock (_gate) SnapshotWireCurrents();
    }

    private void SnapshotWireCurrents()
    {
        if (!TrackWireCurrents || Simulator is null)
        {
            if (_wireCurrents.Count > 0) _wireCurrents = new Dictionary<WireSegment, double>();

            return;
        }

        _wireCurrents = WireCurrentsService.For(Circuit, Simulator);
    }

    /// <summary>
    /// Whether each net's voltage and each part's current are read off as the run goes. Off unless
    /// something is showing them, on the same terms as <see cref="TrackWireCurrents"/>.
    /// </summary>
    public bool TrackLiveValues { get; set; }

    private LiveSnapshot _liveValues = LiveSnapshot.Empty;

    /// <summary>
    /// What every net was sitting at and every part was doing at the last solved point.
    /// <para>
    /// A snapshot swapped wholesale, for the reason <see cref="WireCurrents"/> is: it is written on
    /// the worker thread and read on the UI one, and a reader has to get either the whole of one
    /// answer or the whole of another rather than a dictionary halfway through being filled.
    /// </para>
    /// </summary>
    public LiveSnapshot LiveValues => _liveValues;

    /// <summary>Reads them now, for when nothing is running. Same caveat as the currents.</summary>
    public void RefreshLiveValues()
    {
        if (IsRunning) return;

        lock (_gate) SnapshotLiveValues();
    }

    private void SnapshotLiveValues()
    {
        if (!TrackLiveValues || Simulator is null)
        {
            if (!_liveValues.IsEmpty) _liveValues = LiveSnapshot.Empty;

            return;
        }

        _liveValues = Services.LiveValues.For(Circuit, Simulator);
    }

    private readonly Dictionary<Terminal, double> _lastVolts = [];

    /// <summary>
    /// The voltage at a terminal's net in the solved circuit, for the multimeter; null when nothing is solved. It never
    /// waits on a running slice: while one holds the solver it answers with the last reading it had.
    /// </summary>
    public double? VoltageAt(Terminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (!_gate.TryEnter()) return _lastVolts.TryGetValue(terminal, out var last) ? last : null;
        try
        {
            if (Simulator is not { } simulator || _topologyDirty || !simulator.Netlist.Contains(terminal)) return null;
            var net = simulator.Netlist.NetOf(terminal);
            var volts = net.IsGround ? 0.0 : simulator.System.NodeVoltage(net.Index);
            if (!double.IsFinite(volts)) return null;
            _lastVolts[terminal] = volts;
            return volts;
        }
        finally
        {
            _gate.Exit();
        }
    }

    private readonly Dictionary<Terminal, double> _lastAmps = [];

    /// <summary>
    /// The current into a part through one of its pins, for the multimeter's current range: what a meter wired in series
    /// with that lead would read. Null when nothing is solved or the part doesn't report its current.
    /// </summary>
    public double? CurrentInto(Terminal terminal)
    {
        ArgumentNullException.ThrowIfNull(terminal);
        if (terminal.Owner is not Cirq.Core.Probing.ICurrentReporting reporting) return null;
        if (!_gate.TryEnter()) return _lastAmps.TryGetValue(terminal, out var last) ? last : null;
        try
        {
            if (Simulator is not { } simulator || _topologyDirty || !simulator.Netlist.Contains(terminal)) return null;
            var amps = reporting.TerminalCurrent(terminal, simulator.System, simulator.State);
            if (!double.IsFinite(amps)) return null;
            _lastAmps[terminal] = amps;
            return amps;
        }
        finally
        {
            _gate.Exit();
        }
    }

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
                // The circuit owns its temperature; the engine reads it from the settings. This
                // is where the two are joined, on every rebuild, so changing the ambient and
                // pressing Reset is all it takes.
                Settings.TemperatureKelvin = Circuit.AmbientTemperatureCelsius + 273.15;
                Settings.AdaptiveTimeStep = Circuit.AdaptiveTimeStep;

                Simulator = new CircuitSimulator(Circuit, Settings);
                _debugged = [.. Flattening.Flatten(Circuit.Components).OfType<Cirq.Components.Processors.Atmega328p>()];

                // A circuit with nothing that integrates and no waveform to follow changes only when its logic does:
                // it steps from one event to the next, landing on every one, instead of plodding at a step sized
                // for a filter. A 3-digit counter went from a seventh of real time to thirty times it.
                SteppingByEvents = !Circuit.AdaptiveTimeStep && SteppableByEvents(Circuit, Simulator);
                if (SteppingByEvents) Settings.AdaptiveTimeStep = true;

                Simulator.Reset();
                Simulator.SolveOperatingPoint();
                _topologyDirty = false;
                SimulationTime = 0;
                HasError = false;
                Status = $"Ready - {Simulator.Netlist.NodeCount} nodes, {Simulator.System.Size} unknowns{(SteppingByEvents ? ", stepping from event to event" : "")}";
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

    // The microcontrollers a breakpoint can stop the run in.
    private Cirq.Components.Processors.Atmega328p[] _debugged = [];

    private Cirq.Components.Processors.Atmega328p? HaltedChip()
    {
        foreach (var chip in _debugged)
            if (chip.Halted) return chip;
        return null;
    }

    /// <summary>Raised (on the run's thread) when a microcontroller's firmware stops at a breakpoint or after a step.</summary>
    public event Action<Cirq.Components.Processors.Atmega328p>? FirmwareHalted;

    /// <summary>The live run steps from one logic event to the next (see <see cref="Rebuild"/>).</summary>
    public bool SteppingByEvents { get; private set; }

    /// <summary>Waveforms that need fine steps between their breakpoints: a sine is smooth, not a sequence of events.</summary>
    private static readonly HashSet<string> Continuous = new(StringComparer.Ordinal)
    {
        "FunctionGenerator", "WaveformSource", "NoiseSource", "BehaviouralSource", "Microphone", "DcMotor", "Servo", "StepperMotor",
        "Speaker", "Thermocouple", "SolarCell", "LoadCell", "TransmissionLine",
    };

    /// <summary>Whether a circuit changes only when its logic does: nothing integrating, no continuous waveform.</summary>
    public static bool SteppableByEvents(Circuit circuit, CircuitSimulator simulator)
    {
        ArgumentNullException.ThrowIfNull(circuit);
        ArgumentNullException.ThrowIfNull(simulator);
        return !simulator.HasIntegratingParts
               && !Flattening.Flatten(circuit.Components).Any(c => Continuous.Contains(c.GetType().Name));
    }

    public void Play()
    {
        if (IsRunning) return;
        if (_topologyDirty || Simulator is null)
        {
            if (!Rebuild()) return;
        }

        // Running again carries on from a breakpoint.
        foreach (var chip in _debugged) chip.Resume();

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
                CheckBoards();
                SnapshotWireCurrents();
                SnapshotLiveValues();
            }
            catch (Exception ex)
            {
                HasError = true;
                Status = Describe(ex);
            }
        }

        Advanced?.Invoke();
    }

    /// <summary>
    /// Asks every board in the circuit whether it is being mistreated, and keeps the first
    /// complaint. Cheap enough to run per slice: it is a pass over the configurable pins of the
    /// handful of boards a schematic has, not over the whole netlist.
    /// </summary>
    private void CheckBoards()
    {
        var system = Simulator?.System;
        if (system is null) return;

        string? first = null;
        var total = 0;

        foreach (var board in Circuit.Components.OfType<DeveloperBoard>())
        {
            board.CheckLimits(system);
            total += board.Violations.Count;
            first ??= board.Violations.Count > 0 ? $"{board.Name}: {board.Violations[0]}" : null;
        }

        BoardWarning = first is null
            ? string.Empty
            : total > 1 ? $"{first}  (+{total - 1} more)" : first;
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
            Cirq.Components.Processors.Atmega328p? halted = null;

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
                            if (_debugged.Length > 0 && (halted = HaltedChip()) is not null) break;
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
                            if (_debugged.Length > 0 && (halted = HaltedChip()) is not null) break;
                        }
                    }

                    // Once per slice rather than per step: a violation that matters persists for
                    // far longer than one time point, and this way it costs nothing measurable.
                    CheckBoards();

                    SimulationTime = simulator.Time;

                    // Inside the lock, so the matrix is not being written while it is read.
                    SnapshotWireCurrents();
                    SnapshotLiveValues();
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

            if (halted is not null)
            {
                IsRunning = false;
                Status = $"{halted.Name} stopped at ${halted.Core.Pc * 2:X4} ({halted.HaltReason}): Firmware Debugger to look, Run to carry on";
                FirmwareHalted?.Invoke(halted);
                return;
            }

            // Yield so the render thread gets the CPU even at maximum throughput.
            Thread.Sleep(1);
        }
    }

    private static string Describe(Exception ex) => ex switch
    {
        CircuitTopologyException => $"Topology: {ex.Message}",
        // The message is already a sentence naming a net and the parts on it, so it is shown as
        // written rather than prefixed with a word nobody outside a solver uses.
        ConvergenceException => ex.Message,
        _ => $"{ex.GetType().Name}: {ex.Message}",
    };

    public static string FormatTime(double seconds) => Cirq.Core.Units.SiPrefix.Format(seconds, "s");

    public void Dispose()
    {
        Pause();
        _cancellation?.Dispose();
    }
}
