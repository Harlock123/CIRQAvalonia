using System.Numerics;
using Cirq.Core.Simulation;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>What a stability analysis was asked for.</summary>
/// <param name="Sweep">The band to look across.</param>
/// <param name="Probe">Where the loop is broken.</param>
public sealed record StabilityRequest(AcSweepRequest Sweep, ILoopBreak Probe);

/// <summary>What a stability analysis found.</summary>
/// <param name="Frequencies">Where it looked, in hertz.</param>
/// <param name="LoopGain">The complex loop gain at each frequency.</param>
/// <param name="CrossoverHz">Where the loop gain passes through one, or null if it never does.</param>
/// <param name="PhaseMarginDegrees">
/// How much phase is left at crossover before the loop would be inverting: 180° + ∠T. Positive is
/// stable, and more is calmer.
/// </param>
/// <param name="GainMarginDb">
/// How many decibels the loop gain is below one where the phase reaches −180°. Positive is stable.
/// Null when the phase never gets there, which is the ordinary case for a well-compensated loop.
/// </param>
/// <param name="Problem">Why the answer should not be believed, or null when it can be.</param>
public sealed record StabilityResult(
    IReadOnlyList<double> Frequencies,
    IReadOnlyList<Complex> LoopGain,
    double? CrossoverHz,
    double? PhaseMarginDegrees,
    double? GainMarginDb,
    string? Problem = null)
{
    public bool IsUsable => Problem is null && Frequencies.Count > 0;

    /// <summary>Loop gain in decibels at one point.</summary>
    public double Decibels(int index) => 20.0 * Math.Log10(Math.Max(LoopGain[index].Magnitude, 1e-30));

    /// <summary>
    /// Phase in degrees at one point, unwrapped so a loop that passes through −180° reads −200°
    /// rather than jumping to +160°. A wrapped phase plot is unreadable exactly where it matters.
    /// </summary>
    public IReadOnlyList<double> Degrees { get; init; } = [];

    /// <summary>The DC loop gain in decibels, which is how much the feedback has to work with.</summary>
    public double? LowFrequencyDecibels => LoopGain.Count > 0 ? Decibels(0) : null;

    /// <summary>
    /// What to make of it. The thresholds are the ones every control text gives: under 45° a loop
    /// rings, under 30° it barely settles, and at or below zero it does not stop.
    /// </summary>
    public string Verdict => PhaseMarginDegrees switch
    {
        null => "The loop gain never reaches one in this band, so there is no crossover to " +
                "measure a margin at. Widen the sweep.",
        <= 0 => "Unstable: the loop still has gain where it has already turned through 180°. " +
                "It will oscillate.",
        < 30 => "Marginal: it will ring hard and take a long time to settle.",
        < 45 => "Low: expect noticeable overshoot. Most designs aim for 45° or more.",
        < 60 => "Reasonable: a little overshoot, settles quickly.",
        _ => "Comfortable: well damped, and tolerant of the part-to-part spread that a real build " +
             "will have.",
    };

    public static StabilityResult Unusable(string problem) => new([], [], null, null, null, problem);
}

/// <summary>
/// How much gain goes round a feedback loop, and how close it is to going round it the wrong way.
/// <para>
/// This is the analysis that answers "will it oscillate", which is the single most common reason a
/// circuit that is correct on paper does not work on a bench. A regulator that rings, an amplifier
/// that sings at two megahertz, a servo that hunts: all of them are the same question, and none of
/// them can be answered by looking at gain alone. A loop with plenty of gain margin and no phase
/// margin is an oscillator.
/// </para>
/// <para>
/// <b>How it is done.</b> A <see cref="LoopProbe"/> sits in the loop as a zero-volt source, so the
/// circuit biases exactly as it would without one. For the sweep it injects a volt <i>across</i>
/// itself, which breaks the loop for small signals only — the DC operating point is untouched, so
/// nothing saturates and the linearisation is the one the working circuit has. The loop gain is
/// then the ratio of the two sides of the break.
/// </para>
/// </summary>
public sealed class StabilityAnalysis
{
    private readonly CircuitSimulator _simulator;

    public StabilityAnalysis(CircuitSimulator simulator)
    {
        _simulator = simulator;
    }

    /// <summary>Runs the sweep and puts the probe back to being a piece of wire.</summary>
    public StabilityResult Run(
        StabilityRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var system = _simulator.System;
        var state = _simulator.State;

        var from = system.Node(request.Probe.From);
        var to = system.Node(request.Probe.To);

        if (from < 0 && to < 0)
            return StabilityResult.Unusable(
                "Both sides of the loop probe are on ground, so there is no loop through it.");

        var frequencies = request.Sweep.Frequencies();

        var ac = new AcSystem(system);
        var solver = new ComplexLuSolver(ac.Size);

        var previousMode = state.Mode;
        var previousOmega = state.AngularFrequency;
        var previousInjection = request.Probe.Injection;

        // Everything else that drives a sweep is silenced for the length of this one. A loop gain
        // is the ratio either side of the break, and another source pushing the circuit at the
        // same time adds its own response to both — which does not look like an error, it looks
        // like a different and plausible answer. A follower measured with a generator still
        // connected reads 6 dB where it has 106.
        var others = _simulator.Circuit.Components
            .OfType<IAcExcitation>()
            .Where(s => s.AcMagnitude != 0)
            .Select(s => (Source: s, Was: s.AcMagnitude))
            .ToList();

        List<Complex> gains = [];

        try
        {
            foreach (var (source, _) in others) source.AcMagnitude = 0;

            state.Mode = AnalysisMode.SmallSignal;
            request.Probe.Injection = 1.0;

            foreach (var hertz in frequencies)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var omega = 2.0 * Math.PI * hertz;

                state.AngularFrequency = omega;
                ac.AngularFrequency = omega;

                system.Clear();
                foreach (var component in _simulator.Circuit.Components)
                    component.StampMatrix(system, state);

                ac.Clear();
                ac.FoldInConductances();

                foreach (var component in _simulator.Circuit.Components)
                    component.StampAc(ac, state);

                try
                {
                    solver.Factor(ac.Matrix);
                    solver.Solve(ac.Rhs, ac.Solution);
                }
                catch (SingularMatrixException)
                {
                    gains.Add(Complex.Zero);
                    continue;
                }

                // The loop gain is what came back round divided by what went in, with the sign
                // that makes negative feedback a positive number: T = −V(from)/V(to).
                var forward = ac.NodeVoltage(from);
                var back = ac.NodeVoltage(to);

                gains.Add(back == Complex.Zero ? Complex.Zero : -forward / back);
            }
        }
        finally
        {
            foreach (var (source, was) in others) source.AcMagnitude = was;

            request.Probe.Injection = previousInjection;
            state.Mode = previousMode;
            state.AngularFrequency = previousOmega;
        }

        var degrees = Unwrap(gains);
        var (crossover, margin) = Crossover(frequencies, gains, degrees);
        var gainMargin = GainMargin(frequencies, gains, degrees);

        return new StabilityResult(frequencies, gains, crossover, margin, gainMargin)
        {
            Degrees = degrees,
        };
    }

    /// <summary>
    /// Phase in degrees, unwrapped. A loop that goes past −180° should read −200°, not jump to
    /// +160° — the wrap lands exactly where the interesting part is, and a phase margin read off
    /// a wrapped plot comes out of the wrong branch.
    /// </summary>
    private static double[] Unwrap(IReadOnlyList<Complex> gains)
    {
        var degrees = new double[gains.Count];
        var offset = 0.0;

        for (var i = 0; i < gains.Count; i++)
        {
            var raw = gains[i].Phase * 180.0 / Math.PI;

            if (i > 0)
            {
                var previous = degrees[i - 1] - offset;
                var step = raw - previous;

                // Anything more than half a turn between neighbouring points is the branch cut
                // rather than the circuit.
                if (step > 180) offset -= 360;
                else if (step < -180) offset += 360;
            }

            degrees[i] = raw + offset;
        }

        return degrees;
    }

    /// <summary>
    /// Where the loop gain falls through one, and the phase left over there.
    /// <para>
    /// Interpolated in the logarithm of frequency between the two points that straddle it, because
    /// that is the axis the points are spaced on — and taking the nearer of the two instead can be
    /// several degrees out, which on a margin of forty-five matters.
    /// </para>
    /// </summary>
    private static (double? Crossover, double? Margin) Crossover(
        IReadOnlyList<double> frequencies, IReadOnlyList<Complex> gains, IReadOnlyList<double> degrees)
    {
        for (var i = 1; i < gains.Count; i++)
        {
            var above = gains[i - 1].Magnitude;
            var below = gains[i].Magnitude;

            if (above < 1.0 || below >= 1.0) continue;

            // In decibels, where the fall is a straight line.
            var hi = 20 * Math.Log10(Math.Max(above, 1e-30));
            var lo = 20 * Math.Log10(Math.Max(below, 1e-30));

            var t = hi - lo <= 0 ? 0.0 : hi / (hi - lo);

            var logHz = Math.Log10(frequencies[i - 1])
                        + (t * (Math.Log10(frequencies[i]) - Math.Log10(frequencies[i - 1])));

            var phase = degrees[i - 1] + (t * (degrees[i] - degrees[i - 1]));

            return (Math.Pow(10, logHz), 180.0 + phase);
        }

        return (null, null);
    }

    /// <summary>
    /// How far below one the loop gain is where the phase reaches −180°. Null when it never does,
    /// which is what a single-pole loop looks like and is not a fault.
    /// </summary>
    private static double? GainMargin(
        IReadOnlyList<double> frequencies, IReadOnlyList<Complex> gains, IReadOnlyList<double> degrees)
    {
        for (var i = 1; i < degrees.Count; i++)
        {
            if (degrees[i - 1] <= -180.0 || degrees[i] > -180.0) continue;

            var span = degrees[i - 1] - degrees[i];
            var t = span <= 0 ? 0.0 : (degrees[i - 1] + 180.0) / span;

            var hi = 20 * Math.Log10(Math.Max(gains[i - 1].Magnitude, 1e-30));
            var lo = 20 * Math.Log10(Math.Max(gains[i].Magnitude, 1e-30));

            return -(hi + (t * (lo - hi)));
        }

        return null;
    }
}
