using Cirq.Engine.Numerics;

namespace Cirq.Engine.Tests;

/// <summary>
/// The solver against a plain textbook implementation, and against the system it claims to solve.
/// <para>
/// <see cref="LuSolver"/> reorders the unknowns before eliminating them and then iterates only over
/// the columns each pivot row has something in. Both are worth checking hard, because this is the
/// one piece of code every answer in the application passes through and neither change is visible
/// from anywhere else.
/// </para>
/// <para>
/// The agreement is to a <b>tolerance</b> rather than to the last bit, and that is a real
/// consequence rather than a weakened test: eliminating in a different order does the same
/// operations on the same values in a different sequence, and floating-point addition is not
/// associative. An earlier version of this file demanded bit equality, which was right when the
/// solver only skipped multiplications by zero and became wrong the moment it reordered anything.
/// </para>
/// <para>
/// The matrices are <b>well conditioned</b> on purpose, and that took a wrong answer to learn. Built
/// from random conductances spanning six decades, a graph like this leaves some nodes attached to
/// nothing but the tiny shunt every node gets, the solution runs to 10¹², and the residual computed
/// against it cancels catastrophically — which looks exactly like a broken solver and is not one.
/// A matrix whose condition number is a handful says something about the code; one that is nearly
/// singular says something about the matrix.
/// </para>
/// </summary>
public class LuSolverAgreementTests
{
    /// <summary>Textbook dense LU with partial pivoting: what the fast one has to match.</summary>
    private static double[] Reference(double[][] a, double[] b)
    {
        var n = b.Length;

        var lu = new double[n][];
        for (var i = 0; i < n; i++) lu[i] = (double[])a[i].Clone();

        var pivot = new int[n];
        for (var i = 0; i < n; i++) pivot[i] = i;

        for (var k = 0; k < n; k++)
        {
            var max = Math.Abs(lu[k][k]);
            var maxRow = k;

            for (var i = k + 1; i < n; i++)
            {
                if (Math.Abs(lu[i][k]) <= max) continue;

                max = Math.Abs(lu[i][k]);
                maxRow = i;
            }

            if (maxRow != k)
            {
                (lu[k], lu[maxRow]) = (lu[maxRow], lu[k]);
                (pivot[k], pivot[maxRow]) = (pivot[maxRow], pivot[k]);
            }

            for (var i = k + 1; i < n; i++)
            {
                var factor = lu[i][k] / lu[k][k];
                lu[i][k] = factor;

                if (factor == 0) continue;

                for (var j = k + 1; j < n; j++) lu[i][j] -= factor * lu[k][j];
            }
        }

        var x = new double[n];

        for (var i = 0; i < n; i++)
        {
            var sum = b[pivot[i]];
            for (var j = 0; j < i; j++) sum -= lu[i][j] * x[j];
            x[i] = sum;
        }

        for (var i = n - 1; i >= 0; i--)
        {
            var sum = x[i];
            for (var j = i + 1; j < n; j++) sum -= lu[i][j] * x[j];
            x[i] = sum / lu[i][i];
        }

        return x;
    }

    /// <summary>
    /// A matrix of the shape a circuit produces: a dominant diagonal, a few off-diagonal entries a
    /// row, the rest empty — and every node connected to something, with conductances within a
    /// decade of each other, so that it is well conditioned.
    /// </summary>
    private static (double[][] A, double[] B) Circuitish(int n, int seed, int perRow = 3)
    {
        var random = new Random(seed);

        var a = new double[n][];
        for (var i = 0; i < n; i++) a[i] = new double[n];

        var b = new double[n];

        void Join(int i, int j)
        {
            var g = 1.0 + random.NextDouble();

            // Conductances, as a nodal matrix has: symmetric, negative off the diagonal.
            a[i][j] -= g;
            a[j][i] -= g;
            a[i][i] += g;
            a[j][j] += g;
        }

        for (var i = 0; i < n; i++)
        {
            // A chain first, so nothing is left attached to nothing.
            if (i + 1 < n) Join(i, i + 1);

            for (var k = 0; k < perRow - 1; k++)
            {
                var j = random.Next(n);
                if (j != i) Join(i, j);
            }

            // A shunt to ground on every node, which is what makes the matrix non-singular.
            a[i][i] += 1.0 + random.NextDouble();

            b[i] = random.NextDouble() * 2 - 1;
        }

        return (a, b);
    }

    /// <summary>How far A·x is from b, relative to the largest term that went into it.</summary>
    private static double Residual(double[][] a, double[] x, double[] b)
    {
        var worst = 0.0;
        var scale = 0.0;

        for (var i = 0; i < b.Length; i++)
        {
            var sum = 0.0;

            for (var j = 0; j < b.Length; j++)
            {
                sum += a[i][j] * x[j];
                scale = Math.Max(scale, Math.Abs(a[i][j] * x[j]));
            }

            worst = Math.Max(worst, Math.Abs(sum - b[i]));
            scale = Math.Max(scale, Math.Abs(b[i]));
        }

        return worst / scale;
    }

    [Theory]
    [InlineData(4, 1)]
    [InlineData(10, 2)]
    [InlineData(40, 3)]
    [InlineData(120, 4)]
    [InlineData(250, 5)]
    public void ItAgreesWithATextbookEliminationOnCircuitShapedMatrices(int n, int seed)
    {
        var (a, b) = Circuitish(n, seed);

        var expected = Reference(a, b);

        var solver = new LuSolver(n);
        solver.Factor(a);

        var actual = new double[n];
        solver.Solve(b, actual);

        for (var i = 0; i < n; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= 1e-9 * Math.Max(1, Math.Abs(expected[i])),
                $"row {i}: textbook {expected[i]:R}, solver {actual[i]:R}");

        // And it actually solves the system, which is the statement that matters and does not
        // depend on the reference being right either.
        Assert.True(Residual(a, actual, b) < 1e-12, $"residual {Residual(a, actual, b):E3}");
    }

    /// <summary>
    /// And on dense matrices, where nothing can be skipped and the two must do identical work.
    /// </summary>
    [Theory]
    [InlineData(5, 11)]
    [InlineData(30, 12)]
    [InlineData(80, 13)]
    public void ItAgreesOnFullMatricesToo(int n, int seed)
    {
        var random = new Random(seed);

        var a = new double[n][];
        for (var i = 0; i < n; i++)
        {
            a[i] = new double[n];
            for (var j = 0; j < n; j++) a[i][j] = random.NextDouble() * 2 - 1;

            a[i][i] += n;
        }

        var b = new double[n];
        for (var i = 0; i < n; i++) b[i] = random.NextDouble();

        var expected = Reference(a, b);

        var solver = new LuSolver(n);
        solver.Factor(a);

        var actual = new double[n];
        solver.Solve(b, actual);

        for (var i = 0; i < n; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= 1e-9 * Math.Max(1, Math.Abs(expected[i])),
                $"row {i}");

        Assert.True(Residual(a, actual, b) < 1e-12, $"residual {Residual(a, actual, b):E3}");
    }

    /// <summary>
    /// A matrix that needs pivoting to be solvable at all — a zero on the diagonal — because the
    /// fast path swaps its bookkeeping along with its rows and getting that wrong would be silent.
    /// </summary>
    [Fact]
    public void ItAgreesWhenPivotingIsForced()
    {
        double[][] a =
        [
            [0, 2, 0, 1],
            [2, 0, 1, 0],
            [0, 1, 0, 3],
            [1, 0, 3, 0],
        ];

        double[] b = [3, 3, 4, 4];

        var expected = Reference(a, b);

        var solver = new LuSolver(4);
        solver.Factor(a);

        var actual = new double[4];
        solver.Solve(b, actual);

        for (var i = 0; i < 4; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) < 1e-9, $"row {i}");

        Assert.True(Residual(a, actual, b) < 1e-12, $"residual {Residual(a, actual, b):E3}");
    }

    /// <summary>
    /// Refactoring a different matrix through the same solver gives the same answer as a fresh one.
    /// The column bookkeeping is reused between factorisations, so a stale entry left over from the
    /// previous matrix would show up here and nowhere else.
    /// </summary>
    [Fact]
    public void AReusedSolverIsAFreshOne()
    {
        var (first, b1) = Circuitish(60, 21);
        var (second, b2) = Circuitish(60, 22);

        var reused = new LuSolver(60);

        reused.Factor(first);
        var thrownAway = new double[60];
        reused.Solve(b1, thrownAway);

        reused.Factor(second);
        var actual = new double[60];
        reused.Solve(b2, actual);

        var expected = Reference(second, b2);

        for (var i = 0; i < 60; i++)
            Assert.True(Math.Abs(expected[i] - actual[i]) <= 1e-9 * Math.Max(1, Math.Abs(expected[i])),
                $"row {i}");

        Assert.True(Residual(second, actual, b2) < 1e-12, $"residual {Residual(second, actual, b2):E3}");
    }

    /// <summary>A singular matrix is still refused rather than producing nonsense.</summary>
    [Fact]
    public void ASingularMatrixIsStillRefused()
    {
        double[][] a = [[1, 2], [2, 4]];

        Assert.Throws<SingularMatrixException>(() => new LuSolver(2).Factor(a));
    }
}
