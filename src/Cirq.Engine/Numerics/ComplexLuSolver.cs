using System.Numerics;

namespace Cirq.Engine.Numerics;

/// <summary>
/// Dense complex LU decomposition with partial pivoting — the same Doolittle factorisation the
/// real solver uses, over complex numbers, for the small-signal system.
/// </summary>
public sealed class ComplexLuSolver
{
    private readonly int _n;
    private readonly Complex[][] _lu;
    private readonly int[] _pivot;
    private bool _factored;

    public ComplexLuSolver(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);

        _n = n;
        _lu = new Complex[n][];
        for (var i = 0; i < n; i++) _lu[i] = new Complex[n];

        _pivot = new int[n];
    }

    public int Size => _n;

    /// <summary>Factors the supplied matrix, overwriting any previous factorisation.</summary>
    public void Factor(Complex[][] a)
    {
        for (var i = 0; i < _n; i++)
        {
            Array.Copy(a[i], _lu[i], _n);
            _pivot[i] = i;
        }

        for (var k = 0; k < _n; k++)
        {
            // Partial pivot on magnitude, which is the only ordering a complex number has that
            // means anything for numerical stability.
            var max = _lu[k][k].Magnitude;
            var maxRow = k;

            for (var i = k + 1; i < _n; i++)
            {
                var candidate = _lu[i][k].Magnitude;
                if (candidate <= max) continue;

                max = candidate;
                maxRow = i;
            }

            if (max == 0)
            {
                _factored = false;
                throw new SingularMatrixException(k);
            }

            if (maxRow != k)
            {
                (_lu[k], _lu[maxRow]) = (_lu[maxRow], _lu[k]);
                (_pivot[k], _pivot[maxRow]) = (_pivot[maxRow], _pivot[k]);
            }

            var pivot = _lu[k][k];

            for (var i = k + 1; i < _n; i++)
            {
                var factor = _lu[i][k] / pivot;
                _lu[i][k] = factor;

                if (factor == Complex.Zero) continue;

                for (var j = k + 1; j < _n; j++) _lu[i][j] -= factor * _lu[k][j];
            }
        }

        _factored = true;
    }

    /// <summary>Solves against the current factorisation, writing into <paramref name="x"/>.</summary>
    public void Solve(Complex[] b, Complex[] x)
    {
        if (!_factored) throw new InvalidOperationException("Factor must be called before Solve.");

        // Forward substitution, applying the pivot permutation on the way through.
        for (var i = 0; i < _n; i++)
        {
            var sum = b[_pivot[i]];
            for (var j = 0; j < i; j++) sum -= _lu[i][j] * x[j];
            x[i] = sum;
        }

        // Back substitution.
        for (var i = _n - 1; i >= 0; i--)
        {
            var sum = x[i];
            for (var j = i + 1; j < _n; j++) sum -= _lu[i][j] * x[j];
            x[i] = sum / _lu[i][i];
        }
    }
}
