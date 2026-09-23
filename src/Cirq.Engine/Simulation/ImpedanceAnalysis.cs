using System.Numerics;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>What an impedance sweep was asked for.</summary>
/// <param name="Sweep">The band to look across.</param>
/// <param name="At">The point to look into.</param>
/// <param name="Against">The point to measure back to, or null for ground.</param>
public sealed record ImpedanceRequest(AcSweepRequest Sweep, Terminal At, Terminal? Against = null);

/// <summary>What the circuit looks like, seen from one pair of points.</summary>
/// <param name="Frequencies">Where it looked, in hertz.</param>
/// <param name="Impedance">The complex impedance at each frequency, in ohms.</param>
/// <param name="Problem">Why the answer should not be believed, or null when it can be.</param>
public sealed record ImpedanceResult(
    IReadOnlyList<double> Frequencies,
    IReadOnlyList<Complex> Impedance,
    string? Problem = null)
{
    public bool IsUsable => Problem is null && Frequencies.Count > 0;

    /// <summary>Magnitude in ohms at one point.</summary>
    public double Ohms(int index) => Impedance[index].Magnitude;

    /// <summary>
    /// Phase in degrees at one point. Positive is inductive and negative is capacitive, and the
    /// two are the whole reason this is worth plotting rather than tabulating: a number cannot
    /// say which side of resonance you are on.
    /// </summary>
    public double Degrees(int index) => Impedance[index].Phase * 180.0 / Math.PI;

    /// <summary>The resistive part at one point, in ohms — what dissipates.</summary>
    public double Resistance(int index) => Impedance[index].Real;

    /// <summary>The reactive part at one point, in ohms. Positive inductive, negative capacitive.</summary>
    public double Reactance(int index) => Impedance[index].Imaginary;

    /// <summary>
    /// The reactance at one point read back as the component it behaves like: henries when it is
    /// inductive, farads when it is capacitive, and null at a frequency of zero or a reactance of
    /// none.
    /// <para>
    /// This is the number people actually want. "Forty milliohms of reactance at a megahertz" is
    /// arithmetic away from "six nanohenries", and six nanohenries is a length of track.
    /// </para>
    /// </summary>
    public (double Value, string Unit)? Equivalent(int index)
    {
        var omega = 2.0 * Math.PI * Frequencies[index];
        var x = Reactance(index);

        if (omega <= 0 || Math.Abs(x) < 1e-15) return null;

        return x > 0 ? (x / omega, "H") : (-1.0 / (omega * x), "F");
    }

    /// <summary>The lowest impedance in the band, and where.</summary>
    public (double Hertz, double Ohms)? Minimum => Extreme(least: true);

    /// <summary>The highest impedance in the band, and where.</summary>
    public (double Hertz, double Ohms)? Maximum => Extreme(least: false);

    private (double Hertz, double Ohms)? Extreme(bool least)
    {
        if (Frequencies.Count == 0) return null;

        var at = 0;

        for (var i = 1; i < Impedance.Count; i++)
        {
            var better = least ? Ohms(i) < Ohms(at) : Ohms(i) > Ohms(at);
            if (better) at = i;
        }

        return (Frequencies[at], Ohms(at));
    }

    /// <summary>
    /// Every frequency where the reactance passes through zero, interpolated — the resonances.
    /// <para>
    /// A reactance changing sign is the definition of one, and which way it changes says which
    /// kind. Falling through zero from inductive to capacitive is a <b>parallel</b> resonance,
    /// where the impedance peaks; rising through it is a <b>series</b> resonance, where the
    /// impedance dips. A decoupling capacitor's series resonance is where it stops being a
    /// capacitor and starts being the loop of wire it is soldered into, and it is the single most
    /// useful number on this plot.
    /// </para>
    /// </summary>
    public IReadOnlyList<(double Hertz, bool Series)> Resonances
    {
        get
        {
            List<(double, bool)> found = [];

            for (var i = 1; i < Impedance.Count; i++)
            {
                var before = Reactance(i - 1);
                var after = Reactance(i);

                if (before == 0 || after == 0 || Math.Sign(before) == Math.Sign(after)) continue;

                // Interpolated in the logarithm of frequency, because that is the axis the points
                // are spaced on.
                var t = before / (before - after);

                var lo = Math.Log10(Frequencies[i - 1]);
                var hi = Math.Log10(Frequencies[i]);

                found.Add((Math.Pow(10, lo + ((hi - lo) * t)), before < 0));
            }

            return found;
        }
    }

    public static ImpedanceResult Unusable(string problem) => new([], [], problem);
}

/// <summary>
/// What the circuit looks like from one pair of points: impedance against frequency.
/// <para>
/// Every bench question about loading is this one. What does this amplifier present to whatever
/// drives it; what can that regulator hold its output against when the load steps; where does the
/// decoupling network resonate, and how much does it actually help at the frequency the chip
/// switches at. A gain plot cannot answer any of them, because gain is a ratio between two points
/// and this is a property of one.
/// </para>
/// <para>
/// <b>How it is done.</b> One amp of small-signal current is pushed in at the probe and the volts
/// that appear across it are read: with a current of exactly one, the voltage <i>is</i> the
/// impedance. Every other small-signal source is silenced for the length of the sweep, because an
/// impedance is defined with the circuit's own sources dead — that is what makes it the Thévenin
/// impedance rather than a number with the circuit's own signal mixed into it.
/// </para>
/// <para>
/// The same sweep serves both directions. Probing an input measures what a source would have to
/// drive; probing an output measures what the circuit can hold a load against. Nothing in here
/// knows the difference, and there is none.
/// </para>
/// </summary>
public sealed class ImpedanceAnalysis
{
    private readonly CircuitSimulator _simulator;

    public ImpedanceAnalysis(CircuitSimulator simulator)
    {
        _simulator = simulator;
    }

    /// <summary>Runs the sweep, putting every source it silenced back afterwards.</summary>
    public ImpedanceResult Run(ImpedanceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var system = _simulator.System;
        var state = _simulator.State;

        var at = system.Node(request.At);
        var against = request.Against is null ? -1 : system.Node(request.Against);

        if (at < 0 && against < 0)
        {
            return ImpedanceResult.Unusable(
                "Both ends of the probe are on ground, so there is nothing between them to measure.");
        }

        if (at == against)
        {
            return ImpedanceResult.Unusable(
                "Both ends of the probe are on the same net, which is a short by definition.");
        }

        var frequencies = request.Sweep.Frequencies();

        var ac = new AcSystem(system);
        var solver = new ComplexLuSolver(ac.Size);

        var previousMode = state.Mode;
        var previousOmega = state.AngularFrequency;

        // An impedance is what the circuit looks like with its own sources dead. Leaving one
        // driving would add its response to the voltage that the injected current produced, and
        // the ratio would stop being an impedance without ever stopping being a plausible number.
        var others = _simulator.Circuit.Components
            .OfType<IAcExcitation>()
            .Where(s => s.AcMagnitude != 0)
            .Select(s => (Source: s, Was: s.AcMagnitude))
            .ToList();

        List<Complex> impedance = [];

        try
        {
            foreach (var (source, _) in others) source.AcMagnitude = 0;

            state.Mode = AnalysisMode.SmallSignal;

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

                // One amp in at the probe and out at its reference, so the volts across it are the
                // ohms. The right-hand side holds currents into a node, which is why this is the
                // whole of the excitation.
                ac.AddRhs(at, Complex.One);
                ac.AddRhs(against, -Complex.One);

                try
                {
                    solver.Factor(ac.Matrix);
                    solver.Solve(ac.Rhs, ac.Solution);
                }
                catch (SingularMatrixException)
                {
                    impedance.Add(Complex.Zero);
                    continue;
                }

                impedance.Add(ac.NodeVoltage(at) - ac.NodeVoltage(against));
            }
        }
        finally
        {
            foreach (var (source, was) in others) source.AcMagnitude = was;

            state.Mode = previousMode;
            state.AngularFrequency = previousOmega;
        }

        return new ImpedanceResult(frequencies, impedance);
    }
}
