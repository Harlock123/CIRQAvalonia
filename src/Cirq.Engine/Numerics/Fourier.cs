using System.Numerics;

namespace Cirq.Engine.Numerics;

/// <summary>
/// An in-place radix-2 fast Fourier transform.
/// <para>
/// Written out rather than taken from a library because it is forty lines and the alternative is
/// a dependency for forty lines. It is the ordinary iterative Cooley-Tukey: bit-reverse the input,
/// then combine in passes of doubling width.
/// </para>
/// </summary>
public static class Fourier
{
    /// <summary>
    /// Transforms in place. The length must be a power of two, which is the whole basis of the
    /// algorithm and is checked rather than assumed.
    /// </summary>
    public static void Transform(Complex[] values)
    {
        var n = values.Length;
        if (n <= 1) return;

        if ((n & (n - 1)) != 0)
            throw new ArgumentException($"Length must be a power of two, not {n}.", nameof(values));

        // Bit-reversal permutation: the decimation-in-time ordering the passes below expect.
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;

            for (; (j & bit) != 0; bit >>= 1) j ^= bit;

            j ^= bit;

            if (i < j) (values[i], values[j]) = (values[j], values[i]);
        }

        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2.0 * Math.PI / length;
            var step = new Complex(Math.Cos(angle), Math.Sin(angle));

            for (var start = 0; start < n; start += length)
            {
                var twiddle = Complex.One;

                for (var k = 0; k < length / 2; k++)
                {
                    var even = values[start + k];
                    var odd = values[start + k + (length / 2)] * twiddle;

                    values[start + k] = even + odd;
                    values[start + k + (length / 2)] = even - odd;

                    twiddle *= step;
                }
            }
        }
    }

    /// <summary>The largest power of two at or below a count, and at least one.</summary>
    public static int PowerOfTwoAtMost(int count)
    {
        if (count < 1) return 1;

        var size = 1;
        while (size * 2 <= count) size *= 2;

        return size;
    }
}
