namespace Cirq.Engine.Numerics;

/// <summary>
/// Dense LU decomposition with partial pivoting (Doolittle). Factorisation is done in place on a
/// working copy so the caller's matrix is preserved for inspection after a solve.
/// </summary>
public sealed class LuSolver
{
    private readonly int _n;
    private readonly double[][] _lu;
    private readonly int[] _pivot;
    private bool _factored;

    public LuSolver(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        _n = n;
        _lu = new double[n][];
        for (var i = 0; i < n; i++) _lu[i] = new double[n];
        _pivot = new int[n];
    }

    public int Size => _n;

    /// <summary>Factors the supplied matrix, overwriting any previous factorisation.</summary>
    public void Factor(double[][] a)
    {
        for (var i = 0; i < _n; i++)
        {
            Array.Copy(a[i], _lu[i], _n);
            _pivot[i] = i;
        }

        for (var k = 0; k < _n; k++)
        {
            // Partial pivot: pick the row with the largest magnitude in column k.
            var max = Math.Abs(_lu[k][k]);
            var maxRow = k;
            for (var i = k + 1; i < _n; i++)
            {
                var v = Math.Abs(_lu[i][k]);
                if (v > max)
                {
                    max = v;
                    maxRow = i;
                }
            }

            if (max < 1e-300) throw new SingularMatrixException(k);

            if (maxRow != k)
            {
                (_lu[k], _lu[maxRow]) = (_lu[maxRow], _lu[k]);
                (_pivot[k], _pivot[maxRow]) = (_pivot[maxRow], _pivot[k]);
            }

            var pivotValue = _lu[k][k];
            var rowK = _lu[k];
            for (var i = k + 1; i < _n; i++)
            {
                var rowI = _lu[i];
                var factor = rowI[k] / pivotValue;
                rowI[k] = factor;
                if (factor == 0) continue;
                for (var j = k + 1; j < _n; j++) rowI[j] -= factor * rowK[j];
            }
        }

        _factored = true;
    }

    /// <summary>Solves A·x = b using the stored factorisation, writing the result into <paramref name="x"/>.</summary>
    public void Solve(double[] b, double[] x)
    {
        if (!_factored) throw new InvalidOperationException("Factor must be called before Solve.");
        ArgumentOutOfRangeException.ThrowIfNotEqual(b.Length, _n);
        ArgumentOutOfRangeException.ThrowIfNotEqual(x.Length, _n);

        // Forward substitution applying the row permutation.
        for (var i = 0; i < _n; i++)
        {
            var sum = b[_pivot[i]];
            var row = _lu[i];
            for (var j = 0; j < i; j++) sum -= row[j] * x[j];
            x[i] = sum;
        }

        // Back substitution.
        for (var i = _n - 1; i >= 0; i--)
        {
            var sum = x[i];
            var row = _lu[i];
            for (var j = i + 1; j < _n; j++) sum -= row[j] * x[j];
            x[i] = sum / row[i];
        }
    }

    /// <summary>Convenience one-shot factor-and-solve.</summary>
    public static double[] SolveSystem(double[][] a, double[] b)
    {
        var solver = new LuSolver(b.Length);
        solver.Factor(a);
        var x = new double[b.Length];
        solver.Solve(b, x);
        return x;
    }
}
