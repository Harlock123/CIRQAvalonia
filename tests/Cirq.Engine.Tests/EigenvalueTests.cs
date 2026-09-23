using System.Numerics;
using Cirq.Engine.Numerics;

namespace Cirq.Engine.Tests;

/// <summary>
/// The eigenvalue solver, against matrices whose eigenvalues are known in advance.
/// <para>
/// Everything here is checked against an answer that can be written down — a triangular matrix's
/// diagonal, the roots of a polynomial put into companion form, the trace and determinant that
/// the eigenvalues must sum and multiply to. An eigensolver that is subtly wrong still returns
/// plausible numbers, so nothing is compared against a previous run.
/// </para>
/// </summary>
public class EigenvalueTests
{
    private static double[][] Matrix(params double[][] rows) => rows;

    /// <summary>Sorted so two lists of the same eigenvalues can be compared.</summary>
    private static List<Complex> Sorted(IEnumerable<Complex> values) =>
        [.. values.OrderBy(v => v.Real).ThenBy(v => v.Imaginary)];

    [Fact]
    public void ATriangularMatrixHasItsDiagonal()
    {
        var found = Sorted(Eigenvalues.Of(Matrix(
            [2.0, 7.0, -3.0],
            [0.0, -5.0, 11.0],
            [0.0, 0.0, 0.5])));

        Assert.Equal(3, found.Count);
        Assert.Equal(-5.0, found[0].Real, 10);
        Assert.Equal(0.5, found[1].Real, 10);
        Assert.Equal(2.0, found[2].Real, 10);
        Assert.All(found, v => Assert.Equal(0.0, v.Imaginary, 10));
    }

    /// <summary>
    /// A companion matrix's eigenvalues are the roots of the polynomial it was built from, which
    /// is the cleanest way to ask for an answer that is known exactly.
    /// </summary>
    [Fact]
    public void ACompanionMatrixHasItsPolynomialsRoots()
    {
        // (x − 1)(x − 2)(x − 3)(x + 4) = x⁴ − 2x³ − 13x² + 38x − 24
        double[] coefficients = [-24.0, 38.0, -13.0, -2.0];

        var n = coefficients.Length;
        var a = new double[n][];

        for (var i = 0; i < n; i++) a[i] = new double[n];
        for (var i = 1; i < n; i++) a[i][i - 1] = 1.0;
        for (var i = 0; i < n; i++) a[i][n - 1] = -coefficients[i];

        var found = Sorted(Eigenvalues.Of(a));

        Assert.Equal(4, found.Count);
        Assert.Equal(-4.0, found[0].Real, 8);
        Assert.Equal(1.0, found[1].Real, 8);
        Assert.Equal(2.0, found[2].Real, 8);
        Assert.Equal(3.0, found[3].Real, 8);
        Assert.All(found, v => Assert.Equal(0.0, v.Imaginary, 8));
    }

    /// <summary>
    /// A real matrix's complex eigenvalues come in conjugate pairs, and this one's are exactly
    /// ±2i — a trace of zero and a determinant of four.
    /// </summary>
    [Fact]
    public void AComplexPairComesOutConjugate()
    {
        var found = Sorted(Eigenvalues.Of(Matrix(
            [1.0, -5.0],
            [1.0, -1.0])));

        Assert.Equal(2, found.Count);
        Assert.All(found, v => Assert.Equal(0.0, v.Real, 10));
        Assert.Equal(-2.0, found[0].Imaginary, 10);
        Assert.Equal(2.0, found[1].Imaginary, 10);
    }

    /// <summary>A rotation has its eigenvalues on the unit circle, and nowhere else.</summary>
    [Fact]
    public void ARotationSitsOnTheUnitCircle()
    {
        var angle = 0.7;

        var found = Eigenvalues.Of(Matrix(
            [Math.Cos(angle), -Math.Sin(angle)],
            [Math.Sin(angle), Math.Cos(angle)]));

        Assert.All(found, v => Assert.Equal(1.0, v.Magnitude, 10));
        Assert.All(found, v => Assert.Equal(angle, Math.Abs(v.Phase), 10));
    }

    /// <summary>
    /// The two invariants every set of eigenvalues has to satisfy: they sum to the trace and
    /// multiply to the determinant. A solver can be wrong in a way that still looks like a
    /// spectrum, and this catches that where eyeballing does not.
    /// </summary>
    [Fact]
    public void TheySumToTheTraceAndMultiplyToTheDeterminant()
    {
        var a = Matrix(
            [4.0, 1.0, -2.0, 2.0],
            [1.0, 2.0, 0.0, 1.0],
            [-2.0, 0.0, 3.0, -2.0],
            [2.0, 1.0, -2.0, -1.0]);

        var found = Eigenvalues.Of(a);

        var trace = 4.0 + 2.0 + 3.0 - 1.0;
        var sum = found.Aggregate(Complex.Zero, (acc, v) => acc + v);

        Assert.Equal(trace, sum.Real, 8);
        Assert.Equal(0.0, sum.Imaginary, 8);

        var product = found.Aggregate(Complex.One, (acc, v) => acc * v);

        Assert.Equal(Determinant(a), product.Real, 6);
        Assert.Equal(0.0, product.Imaginary, 6);
    }

    /// <summary>
    /// Rows and columns eighteen orders of magnitude apart, which is what a circuit matrix looks
    /// like when conductances in millisiemens sit beside capacitances in picofarads.
    /// <para>
    /// Built as a similarity — <c>D·M·D⁻¹</c> for a badly scaled diagonal D — so the eigenvalues
    /// are known to be M's exactly, however violent the scaling. The smallest is the one to watch:
    /// poles are reciprocals of these, so the smallest eigenvalue is the slowest mode, and a
    /// solver that loses it loses the part of the answer anybody cares about. Six significant
    /// figures survive an eighteen-decade spread, which is the honest limit here and is checked
    /// rather than hoped for.
    /// </para>
    /// </summary>
    [Fact]
    public void ABadlyScaledMatrixStillResolvesItsSmallEigenvalues()
    {
        double[] wanted = [1.0, 1e-6, 1e-12];

        // A mixing matrix with entries above and below the diagonal, so the result is nowhere
        // near triangular and the solver has to do real work rather than read a diagonal off.
        double[][] v =
        [
            [1.0, 0.0, 1.0],
            [1.0, 1.0, 0.0],
            [0.0, 1.0, 1.0],
        ];

        var m = Multiply(Multiply(v, Diagonal(wanted)), Inverse(v));

        double[] scale = [1e9, 1.0, 1e-9];

        var a = new double[3][];
        for (var i = 0; i < 3; i++)
        {
            a[i] = new double[3];
            for (var j = 0; j < 3; j++) a[i][j] = scale[i] * m[i][j] / scale[j];
        }

        // The scaling really is as bad as claimed.
        var entries = a.SelectMany(r => r).Where(x => x != 0).Select(Math.Abs).ToList();
        Assert.True(entries.Max() / entries.Min() > 1e15);

        var found = Sorted(Eigenvalues.Of(a));

        Assert.Equal(3, found.Count);

        for (var i = 0; i < 3; i++)
        {
            var expected = wanted[2 - i];
            Assert.Equal(expected, found[i].Real, Math.Abs(expected) * 1e-4);
            Assert.Equal(0.0, found[i].Imaginary, Math.Abs(expected) * 1e-4);
        }
    }

    private static double[][] Diagonal(double[] values)
    {
        var n = values.Length;
        var a = new double[n][];

        for (var i = 0; i < n; i++)
        {
            a[i] = new double[n];
            a[i][i] = values[i];
        }

        return a;
    }

    private static double[][] Multiply(double[][] left, double[][] right)
    {
        var n = left.Length;
        var result = new double[n][];

        for (var i = 0; i < n; i++)
        {
            result[i] = new double[n];

            for (var j = 0; j < n; j++)
            {
                var sum = 0.0;
                for (var k = 0; k < n; k++) sum += left[i][k] * right[k][j];

                result[i][j] = sum;
            }
        }

        return result;
    }

    /// <summary>Gauss-Jordan, which is enough for the three-by-three this test builds.</summary>
    private static double[][] Inverse(double[][] a)
    {
        var n = a.Length;
        var work = a.Select(r => (double[])r.Clone()).ToArray();
        var result = Diagonal([.. Enumerable.Repeat(1.0, n)]);

        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
                if (Math.Abs(work[row][col]) > Math.Abs(work[pivot][col])) pivot = row;

            (work[pivot], work[col]) = (work[col], work[pivot]);
            (result[pivot], result[col]) = (result[col], result[pivot]);

            var scale = work[col][col];
            for (var k = 0; k < n; k++)
            {
                work[col][k] /= scale;
                result[col][k] /= scale;
            }

            for (var row = 0; row < n; row++)
            {
                if (row == col) continue;

                var factor = work[row][col];
                for (var k = 0; k < n; k++)
                {
                    work[row][k] -= factor * work[col][k];
                    result[row][k] -= factor * result[col][k];
                }
            }
        }

        return result;
    }

    [Fact]
    public void AOneByOneIsItself()
    {
        var found = Assert.Single(Eigenvalues.Of(Matrix([7.5])));

        Assert.Equal(7.5, found.Real, 12);
        Assert.Equal(0.0, found.Imaginary, 12);
    }

    [Fact]
    public void AnEmptyMatrixHasNoEigenvalues() => Assert.Empty(Eigenvalues.Of([]));

    private static double Determinant(double[][] a)
    {
        var n = a.Length;
        var m = a.Select(r => (double[])r.Clone()).ToArray();
        var determinant = 1.0;

        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var row = col + 1; row < n; row++)
                if (Math.Abs(m[row][col]) > Math.Abs(m[pivot][col])) pivot = row;

            if (m[pivot][col] == 0) return 0;

            if (pivot != col)
            {
                (m[pivot], m[col]) = (m[col], m[pivot]);
                determinant = -determinant;
            }

            determinant *= m[col][col];

            for (var row = col + 1; row < n; row++)
            {
                var factor = m[row][col] / m[col][col];
                for (var k = col; k < n; k++) m[row][k] -= factor * m[col][k];
            }
        }

        return determinant;
    }
}
