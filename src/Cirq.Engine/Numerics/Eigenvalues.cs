using System.Numerics;

namespace Cirq.Engine.Numerics;

/// <summary>
/// The eigenvalues of a general real matrix.
/// <para>
/// Needed because a circuit's natural frequencies are exactly that. Write the small-signal system
/// as <c>G·x + C·dx/dt = 0</c> and a mode that decays or rings as <c>e^{st}</c> exists when
/// <c>(G + sC)·x = 0</c> — so the poles are the eigenvalues of a matrix built from the same two
/// matrices the solver already assembles. No amount of sweeping finds them directly: a sweep
/// samples the response, and a pole is a property of the circuit that the response only hints at.
/// </para>
/// <para>
/// The method is the standard one and is old enough to be boring, which is the point: balance the
/// matrix, reduce it to upper Hessenberg form, then run the Francis double-shift QR iteration
/// until the subdiagonal breaks up into one-by-one and two-by-two blocks. The diagonal blocks are
/// the eigenvalues — real ones singly, complex ones in conjugate pairs.
/// </para>
/// <para>
/// Only the eigenvalues are wanted here, never the vectors, which is what keeps this to one file:
/// the transformations do not have to be accumulated.
/// </para>
/// </summary>
public static class Eigenvalues
{
    /// <summary>The eigenvalues of a square matrix, in no particular order.</summary>
    /// <param name="matrix">Row-major and square. Not modified.</param>
    public static IReadOnlyList<Complex> Of(double[][] matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);

        var n = matrix.Length;
        if (n == 0) return [];

        // Copied into one-based storage so the classical algorithm can be written the way it is
        // written everywhere else. Every off-by-one in an eigensolver is silent.
        var a = new double[n + 1][];
        for (var i = 0; i <= n; i++) a[i] = new double[n + 1];

        for (var i = 1; i <= n; i++)
        {
            if (matrix[i - 1].Length != n)
                throw new ArgumentException("The matrix must be square.", nameof(matrix));

            for (var j = 1; j <= n; j++) a[i][j] = matrix[i - 1][j - 1];
        }

        Balance(a, n);
        Hessenberg(a, n);

        return Francis(a, n);
    }

    /// <summary>
    /// A diagonal similarity that makes the rows and columns comparable in size, chosen from exact
    /// powers of two so it introduces no rounding error of its own.
    /// <para>
    /// Worth having because a circuit matrix holds conductances in millisiemens beside
    /// capacitances in picofarads, so its rows can differ by many orders of magnitude, and an
    /// eigensolver's accuracy follows the norm of the matrix it is actually iterating on.
    /// </para>
    /// <para>
    /// Measured rather than assumed, though: on the worst-scaled matrix the tests build — a
    /// similarity spanning eighteen decades — removing this changes the smallest eigenvalue in the
    /// sixth significant figure and no more, because the pivoting in the Hessenberg reduction
    /// below is already doing most of the same work. It is kept because it is free and standard,
    /// not because it is what makes the answer come out.
    /// </para>
    /// </summary>
    private static void Balance(double[][] a, int n)
    {
        const double radix = 2.0;
        const double squared = radix * radix;

        var settled = false;

        while (!settled)
        {
            settled = true;

            for (var i = 1; i <= n; i++)
            {
                var column = 0.0;
                var row = 0.0;

                for (var j = 1; j <= n; j++)
                {
                    if (j == i) continue;

                    column += Math.Abs(a[j][i]);
                    row += Math.Abs(a[i][j]);
                }

                if (column == 0.0 || row == 0.0) continue;

                var g = row / radix;
                var f = 1.0;
                var s = column + row;

                while (column < g)
                {
                    f *= radix;
                    column *= squared;
                }

                g = row * radix;

                while (column > g)
                {
                    f /= radix;
                    column /= squared;
                }

                if ((column + row) / f >= 0.95 * s) continue;

                settled = false;

                var inverse = 1.0 / f;

                for (var j = 1; j <= n; j++) a[i][j] *= inverse;
                for (var j = 1; j <= n; j++) a[j][i] *= f;
            }
        }
    }

    /// <summary>
    /// Reduction to upper Hessenberg form by elimination with pivoting — everything below the
    /// first subdiagonal driven to zero by a similarity, so the QR iteration that follows costs
    /// order n² a step instead of n³.
    /// </summary>
    private static void Hessenberg(double[][] a, int n)
    {
        for (var m = 2; m < n; m++)
        {
            var x = 0.0;
            var pivot = m;

            for (var j = m; j <= n; j++)
            {
                if (Math.Abs(a[j][m - 1]) <= Math.Abs(x)) continue;

                x = a[j][m - 1];
                pivot = j;
            }

            if (pivot != m)
            {
                for (var j = m - 1; j <= n; j++) (a[pivot][j], a[m][j]) = (a[m][j], a[pivot][j]);
                for (var j = 1; j <= n; j++) (a[j][pivot], a[j][m]) = (a[j][m], a[j][pivot]);
            }

            if (x == 0.0) continue;

            for (var i = m + 1; i <= n; i++)
            {
                var y = a[i][m - 1];
                if (y == 0.0) continue;

                y /= x;
                a[i][m - 1] = y;

                for (var j = m; j <= n; j++) a[i][j] -= y * a[m][j];
                for (var j = 1; j <= n; j++) a[j][m] += y * a[j][i];
            }
        }

        // The multipliers were parked below the subdiagonal; they are not part of the result.
        for (var i = 3; i <= n; i++)
            for (var j = 1; j <= i - 2; j++)
                a[i][j] = 0.0;
    }

    /// <summary>
    /// The Francis double-shift QR iteration on an upper Hessenberg matrix, deflating a real
    /// eigenvalue or a complex pair off the bottom each time the subdiagonal goes negligible.
    /// </summary>
    private static List<Complex> Francis(double[][] a, int n)
    {
        var wr = new double[n + 1];
        var wi = new double[n + 1];

        var norm = 0.0;
        for (var i = 1; i <= n; i++)
            for (var j = Math.Max(i - 1, 1); j <= n; j++)
                norm += Math.Abs(a[i][j]);

        var nn = n;
        var t = 0.0;

        while (nn >= 1)
        {
            var iterations = 0;
            int l;

            do
            {
                // Look for a single small subdiagonal element to split the matrix on.
                for (l = nn; l >= 2; l--)
                {
                    var s0 = Math.Abs(a[l - 1][l - 1]) + Math.Abs(a[l][l]);
                    if (s0 == 0.0) s0 = norm;

                    if (Math.Abs(a[l][l - 1]) + s0 == s0)
                    {
                        a[l][l - 1] = 0.0;
                        break;
                    }
                }

                if (l < 1) l = 1;

                var x = a[nn][nn];

                if (l == nn)
                {
                    // One real root.
                    wr[nn] = x + t;
                    wi[nn] = 0.0;
                    nn--;
                }
                else
                {
                    var y = a[nn - 1][nn - 1];
                    var w = a[nn][nn - 1] * a[nn - 1][nn];

                    if (l == nn - 1)
                    {
                        // Two roots: a real pair, or a complex conjugate pair.
                        var p0 = 0.5 * (y - x);
                        var q0 = (p0 * p0) + w;
                        var z0 = Math.Sqrt(Math.Abs(q0));

                        x += t;

                        if (q0 >= 0.0)
                        {
                            z0 = p0 + Sign(z0, p0);
                            wr[nn - 1] = wr[nn] = x + z0;

                            if (z0 != 0.0) wr[nn] = x - (w / z0);

                            wi[nn - 1] = wi[nn] = 0.0;
                        }
                        else
                        {
                            wr[nn - 1] = wr[nn] = x + p0;
                            wi[nn] = z0;
                            wi[nn - 1] = -z0;
                        }

                        nn -= 2;
                    }
                    else
                    {
                        if (iterations == 60)
                            throw new InvalidOperationException("The eigenvalue iteration did not converge.");

                        if (iterations == 10 || iterations == 20 || iterations == 30 ||
                            iterations == 40 || iterations == 50)
                        {
                            // An exceptional shift, to break the cycle a stubborn matrix can fall
                            // into where the ordinary shift never separates anything.
                            t += x;

                            for (var i = 1; i <= nn; i++) a[i][i] -= x;

                            var s1 = Math.Abs(a[nn][nn - 1]) + Math.Abs(a[nn - 1][nn - 2]);

                            y = x = 0.75 * s1;
                            w = -0.4375 * s1 * s1;
                        }

                        iterations++;

                        double p = 0, q = 0, r = 0;
                        int m;

                        for (m = nn - 2; m >= l; m--)
                        {
                            var z1 = a[m][m];
                            var r1 = x - z1;
                            var s2 = y - z1;

                            p = (((r1 * s2) - w) / a[m + 1][m]) + a[m][m + 1];
                            q = a[m + 1][m + 1] - z1 - r1 - s2;
                            r = a[m + 2][m + 1];

                            var scale = Math.Abs(p) + Math.Abs(q) + Math.Abs(r);

                            p /= scale;
                            q /= scale;
                            r /= scale;

                            if (m == l) break;

                            var u = Math.Abs(a[m][m - 1]) * (Math.Abs(q) + Math.Abs(r));
                            var v = Math.Abs(p) *
                                    (Math.Abs(a[m - 1][m - 1]) + Math.Abs(z1) + Math.Abs(a[m + 1][m + 1]));

                            if (u + v == v) break;
                        }

                        for (var i = m + 2; i <= nn; i++)
                        {
                            a[i][i - 2] = 0.0;
                            if (i != m + 2) a[i][i - 3] = 0.0;
                        }

                        // The double QR step itself, as a sequence of Householder reflections
                        // chased down the subdiagonal.
                        for (var k = m; k <= nn - 1; k++)
                        {
                            if (k != m)
                            {
                                p = a[k][k - 1];
                                q = a[k + 1][k - 1];
                                r = 0.0;

                                if (k != nn - 1) r = a[k + 2][k - 1];

                                x = Math.Abs(p) + Math.Abs(q) + Math.Abs(r);

                                if (x != 0.0)
                                {
                                    p /= x;
                                    q /= x;
                                    r /= x;
                                }
                            }

                            var s3 = Sign(Math.Sqrt((p * p) + (q * q) + (r * r)), p);
                            if (s3 == 0.0) continue;

                            if (k == m)
                            {
                                if (l != m) a[k][k - 1] = -a[k][k - 1];
                            }
                            else
                            {
                                a[k][k - 1] = -s3 * x;
                            }

                            p += s3;

                            x = p / s3;
                            var y2 = q / s3;
                            var z2 = r / s3;

                            q /= p;
                            r /= p;

                            for (var j = k; j <= nn; j++)
                            {
                                var pp = a[k][j] + (q * a[k + 1][j]);

                                if (k != nn - 1)
                                {
                                    pp += r * a[k + 2][j];
                                    a[k + 2][j] -= pp * z2;
                                }

                                a[k + 1][j] -= pp * y2;
                                a[k][j] -= pp * x;
                            }

                            var limit = Math.Min(nn, k + 3);

                            for (var i = l; i <= limit; i++)
                            {
                                var pp = (x * a[i][k]) + (y2 * a[i][k + 1]);

                                if (k != nn - 1)
                                {
                                    pp += z2 * a[i][k + 2];
                                    a[i][k + 2] -= pp * r;
                                }

                                a[i][k + 1] -= pp * q;
                                a[i][k] -= pp;
                            }
                        }
                    }
                }
            }
            while (l < nn - 1 && nn >= 1);
        }

        List<Complex> values = [];
        for (var i = 1; i <= n; i++) values.Add(new Complex(wr[i], wi[i]));

        return values;
    }

    /// <summary>The magnitude of one with the sign of the other, as every numerical text writes it.</summary>
    private static double Sign(double magnitude, double sign) =>
        sign >= 0.0 ? Math.Abs(magnitude) : -Math.Abs(magnitude);
}
