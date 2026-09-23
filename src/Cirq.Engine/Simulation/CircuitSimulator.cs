using Cirq.Core.Digital;
using Cirq.Core.Probing;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Digital;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>
/// The mixed-signal co-simulation engine: a Newton-Raphson/MNA analog solver synchronised with an
/// event-driven digital scheduler.
/// <para>
/// Each time point is solved to convergence, the digital devices are then evaluated against the
/// converged analog levels, and any transition that comes due at this instant forces the analog
/// point to be re-solved (a delta cycle) before the step is accepted. The next step is truncated
/// so that it lands exactly on the earliest queued transition.
/// </para>
/// </summary>
public sealed class CircuitSimulator
{
    private readonly List<CircuitComponent> _components = [];
    private readonly List<CircuitComponent> _nonlinear = [];
    private readonly List<IBreakpointSource> _breakpointSources = [];
    private double[] _iterationDelta = [];
    private double _lastProbeSampleTime = double.NegativeInfinity;
    private LuSolver? _lu;

    public CircuitSimulator(Circuit circuit, SimulationSettings? settings = null)
    {
        Circuit = circuit;
        Settings = settings ?? new SimulationSettings();
        State = new SimulationState(Settings);
        Compile();
    }

    public Circuit Circuit { get; }

    public SimulationSettings Settings { get; }

    public SimulationState State { get; }

    public MnaSystem System { get; private set; } = null!;

    public Netlist Netlist { get; private set; } = null!;

    public DigitalScheduler Scheduler { get; private set; } = null!;

    public double Time => State.Time;

    /// <summary>Number of Newton iterations used by the most recent time point.</summary>
    public int LastIterationCount { get; private set; }

    /// <summary>Number of delta cycles used by the most recent time point.</summary>
    public int LastDeltaCycles { get; private set; }

    /// <summary>Raised after every accepted time point, on the thread driving the simulation.</summary>
    public event Action<CircuitSimulator>? TimePointAccepted;

    // ---- compilation -----------------------------------------------------

    /// <summary>
    /// Rebuilds the netlist, allocates branch unknowns, and sizes the matrices. Call after any
    /// change to the circuit topology.
    /// </summary>
    public void Compile()
    {
        _components.Clear();
        _nonlinear.Clear();
        _breakpointSources.Clear();
        // Blocks are flattened for the solve: their contents stamp into the same matrix as
        // everything else, and in the same order they were drawn.
        _components.AddRange(Flattening.Flatten(Circuit.Components));

        Netlist = Circuit.BuildNetlist();
        if (Netlist.GroundNet is null && Netlist.NodeCount > 0)
            throw new CircuitTopologyException(
                "The circuit has no ground reference. Place a Ground symbol and connect it to the return path.");

        var branches = new Dictionary<(Guid, int), int>();
        var next = 0;
        foreach (var c in _components)
        {
            // Branch currents first, then internal nodes, matching MnaSystem.InternalNode.
            var auxiliary = c.VoltageSourceCount + c.InternalNodeCount;
            for (var i = 0; i < auxiliary; i++) branches[(c.Id, i)] = next++;
            if (c.IsNonlinear) _nonlinear.Add(c);
            if (c is IBreakpointSource bp) _breakpointSources.Add(bp);
        }

        System = new MnaSystem(Netlist, Netlist.NodeCount, branches);
        _lu = new LuSolver(System.Size);
        _iterationDelta = new double[System.Size];

        Scheduler = new DigitalScheduler(System);
        foreach (var d in _components.OfType<IDigitalDevice>()) Scheduler.Devices.Add(d);
        State.Digital = Scheduler;

        ResolveProbes();
    }

    /// <summary>Re-resolves every probe's node index against the current netlist.</summary>
    public void ResolveProbes()
    {
        foreach (var probe in Circuit.Probes)
        {
            if (probe.TargetTerminal is not null && Netlist.Contains(probe.TargetTerminal))
            {
                var net = Netlist.NetOf(probe.TargetTerminal);
                probe.NodeIndex = net.Index;
                probe.TargetNetId = net.Id;
            }
            else
            {
                probe.NodeIndex = Netlist.GroundIndex;
            }

            // The second point of a differential or power probe. An unset reference is ground,
            // which is what an ordinary voltage probe measures against anyway.
            probe.ReferenceNodeIndex =
                probe.ReferenceTerminal is not null && Netlist.Contains(probe.ReferenceTerminal)
                    ? Netlist.NetOf(probe.ReferenceTerminal).Index
                    : Netlist.GroundIndex;
        }
    }

    // ---- lifecycle -------------------------------------------------------

    /// <summary>Clears all component history, the event queue, the solution, and probe buffers.</summary>
    public void Reset()
    {
        State.Reset();
        System.ResetSolution();
        Scheduler.Reset();
        foreach (var c in _components) c.ResetState();
        foreach (var p in Circuit.Probes) p.ResetHistory();
        LastIterationCount = 0;
        LastDeltaCycles = 0;
        _lastProbeSampleTime = double.NegativeInfinity;
    }

    /// <summary>
    /// Solves the t=0 operating point: inductors are shorted and every source sits at its t=0
    /// value. Capacitors are open circuits, except under
    /// <see cref="SimulationSettings.UseInitialConditions"/>, where each one is instead pinned to
    /// its initial voltage (zero unless stated) so that a source applied at t=0 is a true step.
    /// <para>
    /// Either way the network is actually solved, so the rest of the circuit starts from a
    /// consistent state and an unsolvable topology is reported here rather than on the first step.
    /// </para>
    /// </summary>
    public void SolveOperatingPoint()
    {
        State.Mode = AnalysisMode.DcOperatingPoint;
        State.Time = 0;
        State.TimeStep = 0;

        try
        {
            SolveNonlinearPoint(gminBoost: 0);
        }
        catch (Exception ex) when (ex is ConvergenceException or SingularMatrixException)
        {
            // Gmin stepping: start from a heavily shunted, easy-to-solve circuit and walk the
            // extra conductance away, using each solution as the next initial guess.
            SolveWithGminStepping();
        }

        SettleDigital(0);
        CommitAll();
    }

    private void SolveWithGminStepping()
    {
        double[] boosts = [1e-1, 1e-2, 1e-3, 1e-4, 1e-5, 1e-6, 1e-8, 1e-10, 0];
        Exception? last = null;
        System.ResetSolution();

        foreach (var boost in boosts)
        {
            try
            {
                SolveNonlinearPoint(boost);
                last = null;
            }
            catch (Exception ex) when (ex is ConvergenceException or SingularMatrixException)
            {
                last = ex;
                break;
            }
        }

        if (last is not null) throw last;
    }

    // ---- stepping --------------------------------------------------------

    /// <summary>
    /// Advances one time point. The step is the configured step, shortened when a queued digital
    /// transition falls inside it so the analog solver lands exactly on the edge.
    /// </summary>
    /// <returns>The step actually taken, in seconds.</returns>
    public double Step() => Step(Settings.TimeStep);

    /// <summary>Advances one time point of at most <paramref name="requestedStep"/> seconds.</summary>
    public double Step(double requestedStep)
    {
        if (State.Mode == AnalysisMode.DcOperatingPoint && State.StepNumber == 0)
        {
            State.Mode = AnalysisMode.Transient;
            State.IsFirstTransientStep = true;
        }

        var dt = Math.Clamp(requestedStep, Settings.MinTimeStep, Settings.MaxTimeStep);

        // Never step over a pending logic transition or a source discontinuity: land on it instead.
        if (NextInterruptAfter(State.Time) is { } next)
        {
            var toEvent = next - State.Time;
            if (toEvent > 0 && toEvent < dt) dt = Math.Max(toEvent, Settings.MinTimeStep);
        }

        var attempts = 0;
        while (true)
        {
            var target = State.Time + dt;
            State.PreviousTimeStep = State.TimeStep <= 0 ? dt : State.TimeStep;
            State.TimeStep = dt;
            State.Time = target;

            try
            {
                foreach (var c in _components) c.BeginTimeStep(State);
                SolveTimePoint(target);
                break;
            }
            catch (Exception ex) when (ex is ConvergenceException or SingularMatrixException)
            {
                State.Time = target - dt;
                if (!Settings.EnableStepRejection || ++attempts > 12 || dt <= Settings.MinTimeStep * 2)
                    throw;
                dt = Math.Max(dt * 0.25, Settings.MinTimeStep);
            }
        }

        CommitAll();
        State.StepNumber++;
        State.IsFirstTransientStep = false;
        SampleProbes();
        TimePointAccepted?.Invoke(this);
        return dt;
    }

    /// <summary>
    /// The earliest point after <paramref name="time"/> that the analog solver must land on: either
    /// a queued logic transition or a waveform discontinuity.
    /// </summary>
    public double? NextInterruptAfter(double time)
    {
        var best = Scheduler.NextEventTime;
        foreach (var source in _breakpointSources)
        {
            if (source.NextBreakpointAfter(time) is not { } t) continue;
            if (best is null || t < best) best = t;
        }
        return best;
    }

    /// <summary>Runs the transient analysis forward by <paramref name="duration"/> seconds.</summary>
    public void Run(double duration, CancellationToken cancellationToken = default)
    {
        var end = State.Time + duration;
        while (State.Time < end - Settings.MinTimeStep)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = end - State.Time;
            Step(Math.Min(Settings.TimeStep, remaining));
        }
    }

    /// <summary>Convenience entry point: bias point followed by a transient run.</summary>
    public void RunTransient(double duration, CancellationToken cancellationToken = default)
    {
        Reset();
        SolveOperatingPoint();
        Run(duration, cancellationToken);
    }

    // ---- solver core -----------------------------------------------------

    /// <summary>
    /// Solves a single time point, interleaving analog convergence with digital delta cycles until
    /// both domains agree.
    /// </summary>
    private void SolveTimePoint(double time)
    {
        Scheduler.Time = time;

        // Transitions that came due strictly before this point are applied first.
        Scheduler.ApplyDueEvents(time);

        var deltaCycles = 0;
        while (true)
        {
            SolveNonlinearPoint(gminBoost: 0);

            Scheduler.Time = time;
            Scheduler.EvaluateDevices();

            if (!Scheduler.HasEventsDue(time)) break;
            if (!Scheduler.ApplyDueEvents(time)) break;

            if (++deltaCycles >= Settings.MaxDeltaCycles)
                throw new InvalidOperationException(
                    $"Digital logic failed to settle at t={time:g6}s after {deltaCycles} delta cycles. " +
                    "A zero-delay feedback loop needs a non-zero propagation delay to be simulable.");
        }

        LastDeltaCycles = deltaCycles;
    }

    private void SettleDigital(double time)
    {
        Scheduler.Time = time;
        Scheduler.IsInitializing = true;
        for (var i = 0; i < Settings.MaxDeltaCycles; i++)
        {
            Scheduler.EvaluateDevices();
            if (!Scheduler.HasEventsDue(time)) break;
            if (!Scheduler.ApplyDueEvents(time)) break;
            SolveNonlinearPoint(gminBoost: 0);
        }

        Scheduler.IsInitializing = false;
    }

    /// <summary>
    /// Damped Newton-Raphson over the MNA system. Linear circuits converge in a single pass; the
    /// loop runs until the iterate stops moving and every non-linear device agrees it has settled.
    /// </summary>
    private void SolveNonlinearPoint(double gminBoost)
    {
        var maxIterations = _nonlinear.Count == 0 ? 1 : Settings.MaxNewtonIterations;
        var residual = double.MaxValue;

        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            State.NewtonIteration = iteration;

            System.Clear();
            foreach (var c in _components) c.StampMatrix(System, State);
            StampGmin(Settings.Gmin + gminBoost);

            _lu!.Factor(System.Matrix);
            _lu.Solve(System.Rhs, System.Solution);

            residual = 0;
            for (var i = 0; i < System.Size; i++)
            {
                _iterationDelta[i] = System.Solution[i] - System.PreviousSolution[i];
                residual = Math.Max(residual, Math.Abs(_iterationDelta[i]));
            }

            System.CommitIteration();
            LastIterationCount = iteration + 1;

            if (_nonlinear.Count == 0) return;
            if (iteration == 0) continue;

            if (IsWithinTolerance() && _nonlinear.All(c => c.HasConverged(System, State))) return;
        }

        throw new ConvergenceException(State.Time, maxIterations, residual);
    }

    private bool IsWithinTolerance()
    {
        for (var i = 0; i < System.NodeCount; i++)
        {
            var tol = Settings.VoltageTolerance + Settings.RelativeTolerance * Math.Abs(System.Solution[i]);
            if (Math.Abs(_iterationDelta[i]) > tol) return false;
        }

        for (var i = System.NodeCount; i < System.Size; i++)
        {
            var tol = Settings.CurrentTolerance + Settings.RelativeTolerance * Math.Abs(System.Solution[i]);
            if (Math.Abs(_iterationDelta[i]) > tol) return false;
        }

        return true;
    }

    /// <summary>
    /// Shunts every node to ground with a tiny conductance. Without it, a node reachable only
    /// through an open capacitor produces an all-zero matrix row.
    /// </summary>
    private void StampGmin(double gmin)
    {
        if (gmin <= 0) return;
        for (var i = 0; i < System.NodeCount; i++) System.Matrix[i][i] += gmin;
    }

    private void CommitAll()
    {
        foreach (var c in _components) c.CommitTimeStep(System, State);
    }

    // ---- results ---------------------------------------------------------

    public double NodeVoltage(Terminal terminal) => System.NodeVoltage(terminal);

    public double NodeVoltage(int nodeIndex) => System.NodeVoltage(nodeIndex);

    /// <summary>Voltage across a component's two named terminals.</summary>
    public double VoltageAcross(CircuitComponent component, string plus, string minus) =>
        NodeVoltage(component.TerminalByName(plus)) - NodeVoltage(component.TerminalByName(minus));

    /// <summary>
    /// Appends the present values of every probe to its history buffer, honouring
    /// <see cref="SimulationSettings.ProbeSampleInterval"/> so fast circuits do not overrun the
    /// trace buffers.
    /// </summary>
    public void SampleProbes()
    {
        if (Settings.ProbeSampleInterval > 0 &&
            State.Time - _lastProbeSampleTime < Settings.ProbeSampleInterval)
            return;

        _lastProbeSampleTime = State.Time;

        foreach (var probe in Circuit.Probes)
        {
            var value = SampleProbe(probe);
            probe.Record(State.Time, value);
        }
    }

    public double SampleProbe(SignalProbe probe) => probe.Kind switch
    {
        ProbeKind.Voltage => System.NodeVoltage(probe.NodeIndex),
        ProbeKind.Logic => ProbeLogicLevel(probe),
        ProbeKind.Current => ProbeCurrent(probe),
        ProbeKind.Differential => ProbeDifference(probe),
        ProbeKind.Power => ProbeDifference(probe) * ProbeCurrent(probe),
        _ => 0,
    };

    /// <summary>
    /// The voltage between a probe's two points. With no reference set this is the same answer an
    /// ordinary voltage probe gives, which makes a differential probe on a grounded node behave
    /// exactly as expected rather than reading zero.
    /// </summary>
    private double ProbeDifference(SignalProbe probe) =>
        System.NodeVoltage(probe.NodeIndex) - System.NodeVoltage(probe.ReferenceNodeIndex);

    private double ProbeLogicLevel(SignalProbe probe)
    {
        var levels = probe.TargetTerminal?.Owner is IDigitalDevice d ? d.Levels : LogicLevels.Ttl;
        return levels.Classify(System.NodeVoltage(probe.NodeIndex)) switch
        {
            LogicState.High => 1.0,
            LogicState.Low => 0.0,
            _ => 0.5,
        };
    }

    private double ProbeCurrent(SignalProbe probe) => TerminalCurrent(probe.TargetTerminal);

    /// <summary>
    /// Current into a component through one of its pins, in amps, positive inwards — the
    /// convention a clamp meter uses.
    /// <para>
    /// A component that knows which pin was asked about is asked first. The generic branch current
    /// is the fallback, and it cannot tell one pin from another: on a package with ten driven
    /// outputs there are ten branches and no way to say which was meant.
    /// </para>
    /// </summary>
    public double TerminalCurrent(Terminal? terminal)
    {
        var owner = terminal?.Owner;
        if (owner is null) return 0;

        if (owner is ICurrentReporting reporting)
            return reporting.TerminalCurrent(terminal!, System, State);

        if (owner.VoltageSourceCount > 0) return System.BranchCurrent(owner);

        return 0;
    }
}

