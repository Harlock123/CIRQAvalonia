using System.Numerics;
using Cirq.Core.Simulation;
using Cirq.Core.Topology;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Simulation;

/// <summary>What a pole-zero analysis was asked for.</summary>
/// <param name="Output">
/// The node the transfer function comes out at, or null to find the poles alone.
/// </param>
/// <param name="Input">
/// The source the transfer function goes in at. Zeros need both: a pole is a property of the
/// circuit, but a zero is a property of one path through it, so there is no such thing as "the
/// zeros" without saying which way you are looking.
/// </param>
public sealed record PoleZeroRequest(Terminal? Output = null, CircuitComponent? Input = null);

/// <summary>One natural frequency, read the several ways people read one.</summary>
/// <param name="S">The root itself, in radians per second.</param>
public sealed record Root(Complex S)
{
    /// <summary>Where it sits on the frequency axis, in hertz — |s|/2π.</summary>
    public double Hertz => S.Magnitude / (2.0 * Math.PI);

    /// <summary>True for a conjugate pair, which is a mode that rings rather than merely decays.</summary>
    public bool IsOscillatory => Math.Abs(S.Imaginary) > Math.Abs(S.Real) * 1e-9;

    /// <summary>
    /// How long the mode takes to decay to 1/e, in seconds. Negative when the mode grows, which
    /// is what an unstable circuit looks like written down.
    /// </summary>
    public double? TimeConstant => Math.Abs(S.Real) < 1e-30 ? null : -1.0 / S.Real;

    /// <summary>
    /// Damping ratio: one is critically damped, below one rings, at or below zero never settles.
    /// </summary>
    public double Damping => S.Magnitude < 1e-30 ? 0.0 : -S.Real / S.Magnitude;

    /// <summary>
    /// Q, the usual way of saying the same thing about a resonance — how many cycles of ringing
    /// there are before it dies away. Null for a mode that does not ring.
    /// </summary>
    public double? Q => !IsOscillatory || Math.Abs(Damping) < 1e-12 ? null : 1.0 / (2.0 * Damping);

    /// <summary>True when the mode grows instead of decaying.</summary>
    public bool IsUnstable => S.Real > 0;
}

/// <summary>What the analysis found.</summary>
/// <param name="Poles">The circuit's natural frequencies.</param>
/// <param name="Zeros">The zeros of the transfer function to the chosen output, if one was given.</param>
/// <param name="Problem">Why the answer should not be believed, or null when it can be.</param>
public sealed record PoleZeroResult(
    IReadOnlyList<Root> Poles,
    IReadOnlyList<Root> Zeros,
    string? Problem = null)
{
    public bool IsUsable => Problem is null;

    /// <summary>True when every mode decays, which is what stability means.</summary>
    public bool IsStable => Poles.Count > 0 && Poles.All(p => !p.IsUnstable);

    /// <summary>
    /// The pole nearest the imaginary axis — the slowest mode, and therefore the one that decides
    /// how long the circuit takes to settle however fast everything else is.
    /// </summary>
    public Root? Dominant =>
        Poles.Count == 0 ? null : Poles.OrderBy(p => Math.Abs(p.S.Real)).First();

    /// <summary>What to make of it, in the terms the poles are actually asked about.</summary>
    public string Verdict
    {
        get
        {
            if (Poles.Count == 0) return "No dynamic modes: nothing here stores energy.";

            if (Poles.Any(p => p.IsUnstable))
            {
                var worst = Poles.Where(p => p.IsUnstable).OrderByDescending(p => p.S.Real).First();

                return worst.IsOscillatory
                    ? $"Unstable: a pair in the right half plane at {worst.Hertz:0.###} Hz. " +
                      "It will oscillate there."
                    : "Unstable: a real pole in the right half plane. It will run away rather " +
                      "than oscillate — it latches.";
            }

            var ringing = Poles.Where(p => p.IsOscillatory && p.Q is > 0.5)
                               .OrderByDescending(p => p.Q)
                               .FirstOrDefault();

            if (ringing?.Q is { } q)
            {
                return q switch
                {
                    > 5 => $"Stable but lightly damped: Q of {q:0.#} at {ringing.Hertz:0.###} Hz. " +
                           "It will ring for many cycles.",
                    > 1 => $"Stable, with some overshoot: Q of {q:0.##} at {ringing.Hertz:0.###} Hz.",
                    _ => "Stable and well damped.",
                };
            }

            return "Stable and well damped: every mode decays without ringing.";
        }
    }

    public static PoleZeroResult Unusable(string problem) => new([], [], problem);
}

/// <summary>
/// The circuit's poles, and the zeros of a transfer function through it.
/// <para>
/// A stability sweep says a loop has eight degrees of phase margin. This says <i>why</i>: there is
/// a conjugate pair at a hundred and forty-five kilohertz with a Q of seven. A transient shows a
/// circuit ringing; this gives the frequency it rings at and how many cycles it takes to stop,
/// without having to measure either off a trace. They are the circuit's own properties, not
/// properties of whatever you happened to drive it with.
/// </para>
/// <para>
/// <b>How it is done.</b> The small-signal system is <c>G·x + C·dx/dt = 0</c>, where G is the
/// matrix the solver already builds at the bias point and C holds everything that stores energy.
/// A mode shaped like <c>e^{st}</c> exists exactly when <c>(G + sC)·x = 0</c>, so the poles are
/// the roots of that pencil — found here as the eigenvalues of <c>G⁻¹C</c>, whose reciprocals
/// they are, negated. The eigenvalues that come out at zero are modes at infinite frequency: the
/// algebraic constraints in the matrix, which store nothing and are discarded.
/// </para>
/// <para>
/// <b>Zeros</b> come from the same pencil with a border round it — the system matrix
/// <c>[[G+sC, −b], [cᵀ, 0]]</c>, whose roots are the frequencies at which the input b produces
/// nothing at the output c. The textbook shortcut of "the natural frequencies with the output
/// shorted" is not used, because it silently loses zeros at the origin: every AC-coupled stage
/// has one, and a high-pass reported as having no zeros at all is a wrong answer that looks like
/// a right one.
/// </para>
/// <para>
/// Both are solved by the same shift-and-invert. The eigenvalues of <c>(A + σE)⁻¹E</c> are
/// <c>−1/(s − σ)</c>, so the roots come back as <c>s = σ − 1/μ</c>, and the ones that arrive at
/// zero are modes at infinite frequency — the algebraic constraints in the matrix, which store
/// nothing. A shift of zero is tried first, because it is exact when it works; a bordered pencil
/// is routinely singular there, which is the whole reason the shift exists.
/// </para>
/// </summary>
public sealed class PoleZeroAnalysis
{
    /// <summary>
    /// How small an eigenvalue has to be, against the largest, before it is treated as a mode at
    /// infinity rather than a very fast one. Twelve decades of poles is more span than any real
    /// circuit has, and the rejected ones cluster at the rounding noise far below that.
    /// </summary>
    private const double InfinityThreshold = 1e-12;

    private readonly CircuitSimulator _simulator;

    public PoleZeroAnalysis(CircuitSimulator simulator)
    {
        _simulator = simulator;
    }

    public PoleZeroResult Run(PoleZeroRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var system = _simulator.System;
        var state = _simulator.State;

        if (system.Size == 0) return PoleZeroResult.Unusable("The circuit has nothing in it to solve.");

        // Asked outright before anything is sampled. A part that says its stamp is not a plain
        // G + jωC is telling the truth about itself; sampling it might not catch the same fact.
        var irrational = _simulator.Circuit.Components.FirstOrDefault(c => !c.HasLinearAcStamp);

        if (irrational is not null)
        {
            return PoleZeroResult.Unusable(
                $"{irrational.Name} ({irrational.ComponentType}) is a delay rather than a " +
                "reactance, and a delay has infinitely many poles — spaced out forever, which is " +
                "the same fact as an echo coming back again and again. There is no finite list to " +
                "report. Use a frequency sweep instead.");
        }

        var previousMode = state.Mode;
        var previousOmega = state.AngularFrequency;

        double[][] g;
        double[][] c;

        try
        {
            state.Mode = AnalysisMode.SmallSignal;

            // Sampled anyway, at frequencies far enough apart to see a curve that a declaration
            // might have forgotten to mention. This is the backstop, not the rule: it cannot go
            // stale when a part is added, where the declaration above can.
            var (g1, c1) = Sample(system, state, 1.0);

            g = g1;
            c = c1;

            foreach (var omega in (double[])[1e3, 1e6, 1e9, 1e12])
            {
                var (gn, cn) = Sample(system, state, omega);

                if (!Differs(g1, gn) && !Differs(c1, cn)) continue;

                return PoleZeroResult.Unusable(
                    "Something in this circuit is not a plain resistance, capacitance or " +
                    "inductance: its small-signal behaviour changes shape with frequency rather " +
                    "than simply scaling with it. There is no finite set of poles to report. " +
                    "Use a frequency sweep instead.");
            }
        }
        finally
        {
            state.Mode = previousMode;
            state.AngularFrequency = previousOmega;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var poles = Finite(g, c);

        if (poles is null)
        {
            return PoleZeroResult.Unusable(
                "The circuit's matrix is singular at its bias point, so it has no poles to find. " +
                "Usually that is a floating node or a loop of voltage sources.");
        }

        List<Root> zeros = [];

        if (request.Output is { } output && request.Input is { } input)
        {
            if (input is not IAcExcitation)
            {
                return PoleZeroResult.Unusable(
                    $"{input.Name} is not something that can drive a small-signal analysis, so " +
                    "there is no transfer function from it to take the zeros of.");
            }

            var node = system.Node(output);

            if (node < 0)
            {
                return PoleZeroResult.Unusable(
                    "The output is on ground, where every transfer function is zero at every " +
                    "frequency. There are no zeros to report because there is no function.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var excitation = Excitation(system, state, input);

            if (excitation is null)
            {
                return PoleZeroResult.Unusable(
                    $"{input.Name} contributes nothing to a small-signal analysis, so it cannot " +
                    "be the input of a transfer function.");
            }

            var (a, e) = Bordered(g, c, excitation, node);

            zeros = Finite(a, e) ?? [];
        }

        return new PoleZeroResult(poles, zeros);
    }

    /// <summary>
    /// The right-hand side one source puts into a small-signal solve, which is the input vector a
    /// transfer function is defined against. Taken from the source itself rather than assumed, so
    /// a part that drives through a branch rather than a node is handled without a special case.
    /// </summary>
    private double[]? Excitation(MnaSystem system, SimulationState state, CircuitComponent input)
    {
        var sources = _simulator.Circuit.Components
            .OfType<IAcExcitation>()
            .Select(s => (Source: s, Was: s.AcMagnitude))
            .ToList();

        var previousMode = state.Mode;
        var previousOmega = state.AngularFrequency;

        var ac = new AcSystem(system);

        try
        {
            foreach (var (source, _) in sources) source.AcMagnitude = 0;
            ((IAcExcitation)input).AcMagnitude = 1.0;

            state.Mode = AnalysisMode.SmallSignal;
            state.AngularFrequency = 1.0;
            ac.AngularFrequency = 1.0;

            system.Clear();
            foreach (var component in _simulator.Circuit.Components)
                component.StampMatrix(system, state);

            ac.Clear();
            ac.FoldInConductances();

            foreach (var component in _simulator.Circuit.Components) component.StampAc(ac, state);

            var b = new double[ac.Size];
            var any = false;

            for (var i = 0; i < ac.Size; i++)
            {
                b[i] = ac.Rhs[i].Real;
                if (ac.Rhs[i] != System.Numerics.Complex.Zero) any = true;
            }

            return any ? b : null;
        }
        finally
        {
            foreach (var (source, was) in sources) source.AcMagnitude = was;

            state.Mode = previousMode;
            state.AngularFrequency = previousOmega;
        }
    }

    /// <summary>
    /// The system matrix with its border: the input down the last column and the output along the
    /// last row. Its roots are the frequencies at which that input reaches that output as nothing.
    /// </summary>
    private static (double[][] A, double[][] E) Bordered(
        double[][] g, double[][] c, double[] b, int output)
    {
        var n = g.Length;
        var size = n + 1;

        var a = new double[size][];
        var e = new double[size][];

        for (var i = 0; i < size; i++)
        {
            a[i] = new double[size];
            e[i] = new double[size];
        }

        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                a[i][j] = g[i][j];
                e[i][j] = c[i][j];
            }

            a[i][n] = -b[i];
        }

        a[n][output] = 1.0;

        return (a, e);
    }

    /// <summary>
    /// The finite roots of <c>det(A + sE) = 0</c>, or null when no usable shift could be found.
    /// </summary>
    private static List<Root>? Finite(double[][] a, double[][] e)
    {
        var n = a.Length;
        if (n == 0) return [];

        // A characteristic rate for this pencil, in radians per second, so the trial shifts are
        // in the region the roots are rather than at some fixed number of hertz.
        var scale = Norm(a) / Math.Max(Norm(e), 1e-300);

        // Zero first, because an unshifted solve is exact when the matrix allows one. The
        // multipliers after it are deliberately unround: a shift that lands on a root makes the
        // matrix singular again, and round numbers are where roots like to be.
        double[] shifts = [0.0, scale * 0.8177, scale * 5.313, scale * 0.1279, scale * 23.71];

        foreach (var shift in shifts)
        {
            var shifted = new double[n][];

            for (var i = 0; i < n; i++)
            {
                shifted[i] = new double[n];
                for (var j = 0; j < n; j++) shifted[i][j] = a[i][j] + (shift * e[i][j]);
            }

            var solver = new LuSolver(n);

            try
            {
                solver.Factor(shifted);
            }
            catch (SingularMatrixException)
            {
                continue;
            }

            // (A + σE)⁻¹E, one column at a time.
            var product = new double[n][];
            for (var i = 0; i < n; i++) product[i] = new double[n];

            var column = new double[n];
            var solution = new double[n];

            for (var j = 0; j < n; j++)
            {
                for (var i = 0; i < n; i++) column[i] = e[i][j];

                solver.Solve(column, solution);

                for (var i = 0; i < n; i++) product[i][j] = solution[i];
            }

            var eigenvalues = Eigenvalues.Of(product);

            var largest = eigenvalues.Count == 0 ? 0.0 : eigenvalues.Max(v => v.Magnitude);
            var floor = largest * InfinityThreshold;

            List<Root> roots = [];

            foreach (var value in eigenvalues)
            {
                // An eigenvalue of zero is a root at infinity: an algebraic constraint rather than
                // anything that stores energy, and there is one for every node with no
                // capacitance on it and every branch current the matrix carries.
                if (value.Magnitude <= floor) continue;

                roots.Add(new Root(shift - (Complex.One / value)));
            }

            return roots;
        }

        return null;
    }

    private static double Norm(double[][] a)
    {
        var largest = 0.0;

        foreach (var row in a)
        {
            var sum = 0.0;
            foreach (var value in row) sum += Math.Abs(value);

            largest = Math.Max(largest, sum);
        }

        return largest;
    }

    /// <summary>The real and the per-radian imaginary parts of the system at one frequency.</summary>
    private (double[][] G, double[][] C) Sample(MnaSystem system, SimulationState state, double omega)
    {
        var ac = new AcSystem(system);

        state.AngularFrequency = omega;
        ac.AngularFrequency = omega;

        system.Clear();
        foreach (var component in _simulator.Circuit.Components) component.StampMatrix(system, state);

        ac.Clear();
        ac.FoldInConductances();

        foreach (var component in _simulator.Circuit.Components) component.StampAc(ac, state);

        var n = ac.Size;

        var g = new double[n][];
        var c = new double[n][];

        for (var i = 0; i < n; i++)
        {
            g[i] = new double[n];
            c[i] = new double[n];

            for (var j = 0; j < n; j++)
            {
                g[i][j] = ac.Matrix[i][j].Real;
                c[i][j] = ac.Matrix[i][j].Imaginary / omega;
            }
        }

        return (g, c);
    }

    /// <summary>True when two samples of the same matrix disagree by more than rounding.</summary>
    private static bool Differs(double[][] left, double[][] right)
    {
        var scale = 0.0;

        for (var i = 0; i < left.Length; i++)
            for (var j = 0; j < left.Length; j++)
                scale = Math.Max(scale, Math.Abs(left[i][j]));

        var tolerance = Math.Max(scale, 1e-30) * 1e-9;

        for (var i = 0; i < left.Length; i++)
            for (var j = 0; j < left.Length; j++)
                if (Math.Abs(left[i][j] - right[i][j]) > tolerance) return true;

        return false;
    }
}
