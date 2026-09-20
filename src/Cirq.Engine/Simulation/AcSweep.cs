using System.Numerics;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>How the frequencies of a sweep are spaced.</summary>
public enum SweepSpacing
{
    /// <summary>Equal ratios — the spacing a Bode plot is drawn on, and what almost everything wants.</summary>
    Decade,

    /// <summary>Equal steps, for looking closely at a narrow span.</summary>
    Linear,
}

/// <summary>What a sweep was asked for.</summary>
/// <param name="StartHz">Lowest frequency, in hertz.</param>
/// <param name="StopHz">Highest frequency.</param>
/// <param name="PointsPerDecade">Points per decade for a decade sweep, or points in total for a linear one.</param>
/// <param name="Spacing">Decade or linear.</param>
public sealed record AcSweepRequest(
    double StartHz = 10.0,
    double StopHz = 1e6,
    int PointsPerDecade = 20,
    SweepSpacing Spacing = SweepSpacing.Decade)
{
    /// <summary>The frequencies this request works out to.</summary>
    public IReadOnlyList<double> Frequencies()
    {
        var start = Math.Max(StartHz, 1e-9);
        var stop = Math.Max(StopHz, start * 1.0000001);

        List<double> points = [];

        if (Spacing == SweepSpacing.Linear)
        {
            var count = Math.Max(PointsPerDecade, 2);
            for (var i = 0; i < count; i++)
                points.Add(start + ((stop - start) * i / (count - 1.0)));

            return points;
        }

        var perDecade = Math.Max(PointsPerDecade, 1);
        var decades = Math.Log10(stop / start);
        var total = Math.Max((int)Math.Round(decades * perDecade), 1);

        for (var i = 0; i <= total; i++)
            points.Add(start * Math.Pow(10, decades * i / total));

        return points;
    }
}

/// <summary>One probed node's response across the sweep.</summary>
/// <param name="Label">What the probe is called.</param>
/// <param name="Node">Its node index, or negative for ground.</param>
/// <param name="Response">The complex voltage at each frequency, in the sweep's order.</param>
public sealed record AcTrace(string Label, int Node, IReadOnlyList<Complex> Response)
{
    /// <summary>Magnitude in decibels relative to one volt at a given point.</summary>
    public double Decibels(int index) => 20.0 * Math.Log10(Math.Max(Response[index].Magnitude, 1e-30));

    /// <summary>Phase in degrees at a given point.</summary>
    public double Degrees(int index) => Response[index].Phase * 180.0 / Math.PI;
}

/// <summary>The whole answer: the frequencies, and one trace per probe.</summary>
/// <param name="Frequencies">Frequencies solved, in hertz.</param>
/// <param name="Traces">One per probe.</param>
public sealed record AcSweepResult(IReadOnlyList<double> Frequencies, IReadOnlyList<AcTrace> Traces)
{
    /// <summary>
    /// The frequency at which a trace has fallen a given number of decibels from its own maximum,
    /// interpolated between the two points that straddle it — which is how a −3 dB bandwidth is
    /// read off a plot, and what a test wants to assert.
    /// </summary>
    public double? CornerOf(string label, double fallDb = 3.0)
    {
        var trace = Traces.FirstOrDefault(t => t.Label == label);
        if (trace is null || Frequencies.Count < 2) return null;

        var peak = double.NegativeInfinity;
        var peakAt = 0;

        for (var i = 0; i < trace.Response.Count; i++)
        {
            var db = trace.Decibels(i);
            if (db <= peak) continue;

            peak = db;
            peakAt = i;
        }

        var target = peak - fallDb;

        for (var i = peakAt + 1; i < trace.Response.Count; i++)
        {
            var db = trace.Decibels(i);
            if (db > target) continue;

            var previous = trace.Decibels(i - 1);
            var span = previous - db;
            var t = span <= 0 ? 0.0 : (previous - target) / span;

            // Interpolated in the logarithm, because that is the axis the points are spaced on.
            var lo = Math.Log10(Frequencies[i - 1]);
            var hi = Math.Log10(Frequencies[i]);

            return Math.Pow(10, lo + ((hi - lo) * t));
        }

        return null;
    }
}

/// <summary>
/// A small-signal frequency sweep about the circuit's bias point.
/// <para>
/// This is the second way of looking at a circuit, and it answers questions the time domain can
/// only be badgered into answering. Where a filter turns over, how much gain an amplifier has left
/// at a megahertz, what a ferrite bead's impedance actually does across its band: all of those are
/// one solve per frequency here, and a morning of stepping a generator and reading a scope
/// otherwise.
/// </para>
/// <para>
/// It is honest about one thing and quiet about another. Every non-linear part is replaced by its
/// slope at the bias point, so the answer is exact for small signals and says nothing whatever
/// about what happens when a signal is large — no clipping, no slew limiting, no distortion. And
/// the bias point has to exist: a circuit that will not settle has nothing to linearise about.
/// </para>
/// </summary>
public sealed class AcSweep
{
    private readonly CircuitSimulator _simulator;

    public AcSweep(CircuitSimulator simulator)
    {
        _simulator = simulator;
    }

    /// <summary>
    /// Runs a sweep, leaving the simulator back at its bias point. The circuit must already have
    /// been solved — the sweep linearises about whatever operating point it finds.
    /// </summary>
    public AcSweepResult Run(AcSweepRequest request, CancellationToken cancellationToken = default)
    {
        var frequencies = request.Frequencies();
        var state = _simulator.State;
        var system = _simulator.System;

        var ac = new AcSystem(system);
        var solver = new ComplexLuSolver(ac.Size);

        // One list of complex responses per probe, filled in as the sweep goes.
        List<Complex>[] responses = [.. _simulator.Circuit.Probes.Select(_ => new List<Complex>())];

        var previousMode = state.Mode;
        var previousOmega = state.AngularFrequency;

        try
        {
            state.Mode = AnalysisMode.SmallSignal;

            foreach (var hertz in frequencies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                Solve(ac, solver, state, system, hertz);

                for (var i = 0; i < responses.Length; i++)
                {
                    var probe = _simulator.Circuit.Probes[i];
                    responses[i].Add(ac.NodeVoltage(probe.NodeIndex));
                }
            }
        }
        finally
        {
            state.Mode = previousMode;
            state.AngularFrequency = previousOmega;
        }

        List<AcTrace> traces = [];
        for (var i = 0; i < responses.Length; i++)
        {
            var probe = _simulator.Circuit.Probes[i];
            traces.Add(new AcTrace(probe.Label, probe.NodeIndex, responses[i]));
        }

        return new AcSweepResult(frequencies, traces);
    }

    /// <summary>One frequency: build the complex matrix, factor it, and solve.</summary>
    private void Solve(
        AcSystem ac, ComplexLuSolver solver, SimulationState state, MnaSystem system, double hertz)
    {
        var omega = 2.0 * Math.PI * hertz;

        state.AngularFrequency = omega;
        ac.AngularFrequency = omega;

        // The conductances first, from the ordinary stamps at the bias point. For anything linear
        // that is the component itself; for anything non-linear it is the slope it settled at.
        system.Clear();
        foreach (var component in _simulator.Circuit.Components) component.StampMatrix(system, state);

        ac.Clear();
        ac.FoldInConductances();

        // Then whatever a real number could not hold.
        foreach (var component in _simulator.Circuit.Components) component.StampAc(ac, state);

        solver.Factor(ac.Matrix);
        solver.Solve(ac.Rhs, ac.Solution);
    }
}
