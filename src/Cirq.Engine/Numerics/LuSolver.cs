namespace Cirq.Engine.Numerics;

/// <summary>
/// LU decomposition with partial pivoting (Doolittle), eliminated sparsely once a matrix is large
/// enough for that to pay.
/// <para>
/// A circuit matrix is beautifully empty — a node touches three or four others, so three entries a
/// row and under half a percent full — and a dense elimination spends nearly all of its time
/// subtracting zero. Two things are needed to exploit that, and only one of them is obvious.
/// </para>
/// <para>
/// The obvious one is to run the inner loop over the columns the pivot row actually has something
/// in. Done alone, that made this <b>twice as slow</b>. The reason is the second thing: the
/// unknowns arrive numbered in the order parts were added, so a node is routinely coupled to one
/// several hundred rows away — measured on a ladder of eight hundred rungs, three entries a row and
/// a bandwidth of four hundred. Eliminating that fills in the entire band, the rows become dense
/// anyway, and iterating their columns indirectly is simply a slower way of doing dense arithmetic.
/// </para>
/// <para>
/// So the unknowns are reordered first, by Reverse Cuthill-McKee, which walks the coupling graph
/// and numbers it so that couplings sit near the diagonal. With the ordering in place the sparsity
/// survives the elimination and the skipping is worth something. Measured on the same ladder, where
/// the matrix moves every Newton iteration and must be refactored:
/// </para>
/// <list type="table">
/// <item><term>102 unknowns</term><description>0.16 ms a time point, now 0.06</description></item>
/// <item><term>402</term><description>9.3 ms, now 1.1</description></item>
/// <item><term>802</term><description>47 ms, now 3.9</description></item>
/// <item><term>1602</term><description>238 ms, now 22 — four time points a second became forty-five</description></item>
/// </list>
/// <para>
/// Below <see cref="DenseBelow"/> unknowns none of this applies and the elimination is the dense
/// one it always was, to the last bit. Most circuits are below it, and on those the bookkeeping
/// costs more than it saves.
/// </para>
/// <para>
/// It is not a sparse solver in the full sense: the storage is still n², so a very large circuit
/// runs out of memory before it runs out of patience, and the ordering is bandwidth-reducing rather
/// than fill-minimising. Both are improvable from here without changing anything that uses it.
/// </para>
/// </summary>
public sealed class LuSolver
{
    private readonly int _n;
    private readonly double[][] _lu;
    private readonly int[] _pivot;
    private bool _factored;

    /// <summary>
    /// Cleared the first time reordering produces a singular matrix where the stamped order does
    /// not, after which this solver stops trying. See <see cref="Factor(double[][])"/>.
    /// </summary>
    private bool _orderingHelps = true;

    /// <summary>Whether <see cref="_order"/> has been worked out yet. See the note where it is.</summary>
    private bool _ordered;

    /// <summary>
    /// The columns each row has something in, ascending, and how many. Maintained through the
    /// elimination: a row that gains fill gains the column it gained it in.
    /// </summary>
    private readonly int[][] _columns;
    private readonly int[] _counts;

    /// <summary>Scratch for merging one row's columns into another's.</summary>
    private readonly int[] _merged;

    /// <summary>The order the unknowns are eliminated in: <c>_order[i]</c> is the caller's index.</summary>
    private readonly int[] _order;
    private readonly int[] _degree;
    private readonly bool[] _placed;

    /// <summary>The solution in the eliminated order, before it is put back into the caller's.</summary>
    private readonly double[] _scratch;

    public LuSolver(int n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        _n = n;
        _lu = new double[n][];
        for (var i = 0; i < n; i++) _lu[i] = new double[n];
        _pivot = new int[n];

        _columns = new int[n][];
        for (var i = 0; i < n; i++) _columns[i] = new int[n];
        _counts = new int[n];
        _merged = new int[n];

        _order = new int[n];
        _degree = new int[n];
        _placed = new bool[n];
        _scratch = new double[n];
    }

    public int Size => _n;

    /// <summary>
    /// Below this many unknowns the matrix is eliminated densely, exactly as it always was.
    /// <para>
    /// Sparsity is not free: it costs an indirection on every access, a merge of column lists per
    /// row per pivot, and a walk of the coupling graph to find an order. On a small matrix the
    /// dense loops are contiguous, vectorise, and finish before any of that has paid for itself —
    /// which showed up as the whole test suite, thousands of small circuits, getting slower.
    /// </para>
    /// <para>
    /// Most circuits anybody draws are below this, and they get the arithmetic they had before, in
    /// the order they had before, to the last bit. The ones that were unusably slow get the
    /// treatment that makes them usable. Forty-eight sits well under the size where the sparse path
    /// measured faster, so neither side of the crossover is losing.
    /// </para>
    /// </summary>
    public const int DenseBelow = 48;

    /// <summary>Factors the supplied matrix, overwriting any previous factorisation.</summary>
    public void Factor(double[][] a)
    {
        if (_n < DenseBelow)
        {
            Factor(a, ordered: false, sparse: false);
            return;
        }

        if (!_orderingHelps)
        {
            Factor(a, ordered: false, sparse: true);
            return;
        }

        try
        {
            Factor(a, ordered: true, sparse: true);
        }
        catch (SingularMatrixException)
        {
            // The ordering is an optimisation, and this is what makes it only that.
            //
            // Reordering the unknowns changes which pivots the elimination picks, and for a matrix
            // that is barely non-singular — a bus with no pull-ups, a node floating until something
            // drives it, which are circuits this application is expected to survive rather than
            // refuse — that can be the difference between a pivot of 1e-20 and a pivot of exactly
            // zero. The circuit has not changed and neither has its right to an answer; only the
            // order has.
            //
            // So a failure here is not reported. It falls back to the order the caller stamped in,
            // which is what this solver did before it reordered anything, and fails from there if
            // it is going to fail at all. Noted rather than rediscovered, because a solver lives as
            // long as the topology does: without that, every time point of a bus with no pull-ups
            // would pay for two factorisations instead of one.
            _orderingHelps = false;

            Factor(a, ordered: false, sparse: true);
        }
    }

    private void Factor(double[][] a, bool ordered, bool sparse)
    {
        if (!ordered)
        {
            for (var i = 0; i < _n; i++) _order[i] = i;
        }
        else if (!_ordered)
        {
            // Once, not per factorisation. A transient factors the matrix on every Newton
            // iteration of every time point, and working the order out each time cost more on a
            // small circuit than eliminating it — which is most circuits, and which showed up as
            // the whole test suite getting a quarter slower.
            //
            // Reusing it is safe in the only sense that matters: any permutation gives the right
            // answer, so an order that has stopped being the best one for a structure that has
            // since changed — a switch opened, an output released — is merely less good at keeping
            // the fill down. If it ever becomes singular, the fallback above catches that.
            Order(a);
            _ordered = true;
        }

        for (var i = 0; i < _n; i++)
        {
            // Permuted on the way in, so the elimination below sees a matrix whose couplings are
            // near its diagonal and stays sparse as it fills.
            var source = a[_order[i]];
            var row = _lu[i];

            for (var j = 0; j < _n; j++) row[j] = source[_order[j]];

            _pivot[i] = i;

            if (!sparse) continue;

            // Where this row starts out non-empty. Everything after this is maintained rather than
            // rediscovered, because rediscovering it would be the dense scan this exists to avoid.
            var columns = _columns[i];
            var count = 0;

            for (var j = 0; j < _n; j++)
                if (row[j] != 0)
                    columns[count++] = j;

            _counts[i] = count;
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

                if (sparse)
                {
                    (_columns[k], _columns[maxRow]) = (_columns[maxRow], _columns[k]);
                    (_counts[k], _counts[maxRow]) = (_counts[maxRow], _counts[k]);
                }
            }

            var pivotValue = _lu[k][k];
            var rowK = _lu[k];

            if (!sparse)
            {
                // The elimination this class has always done, for the sizes where it is fastest.
                for (var i = k + 1; i < _n; i++)
                {
                    var dense = _lu[i];
                    var scale = dense[k] / pivotValue;
                    dense[k] = scale;
                    if (scale == 0) continue;

                    for (var j = k + 1; j < _n; j++) dense[j] -= scale * rowK[j];
                }

                continue;
            }

            // The pivot row's columns beyond k: the only ones that can change anything below.
            var pivotColumns = _columns[k];
            var pivotCount = _counts[k];
            var from = 0;
            while (from < pivotCount && pivotColumns[from] <= k) from++;

            for (var i = k + 1; i < _n; i++)
            {
                var rowI = _lu[i];
                var factor = rowI[k] / pivotValue;
                rowI[k] = factor;
                if (factor == 0) continue;

                for (var p = from; p < pivotCount; p++)
                {
                    var j = pivotColumns[p];
                    rowI[j] -= factor * rowK[j];
                }

                // Whatever the pivot row touched, this row now has — whether it was there before or
                // has just been filled in.
                Absorb(i, pivotColumns, from, pivotCount);
            }
        }

        _factored = true;
    }

    /// <summary>
    /// Merges the pivot row's columns into row <paramref name="i"/>'s, keeping them ascending.
    /// <para>
    /// A union rather than a test for what actually became non-zero. A value that cancels exactly
    /// to zero leaves its column in the list, which costs one multiplication by zero later and
    /// keeps this linear — checking would cost a comparison now to save a multiply then, and would
    /// have to be right about floating-point cancellation to be worth it.
    /// </para>
    /// </summary>
    private void Absorb(int i, int[] pivotColumns, int from, int pivotCount)
    {
        var own = _columns[i];
        var count = _counts[i];

        var a = 0;
        var b = from;
        var n = 0;

        while (a < count && b < pivotCount)
        {
            var left = own[a];
            var right = pivotColumns[b];

            if (left == right) { _merged[n++] = left; a++; b++; }
            else if (left < right) { _merged[n++] = left; a++; }
            else { _merged[n++] = right; b++; }
        }

        while (a < count) _merged[n++] = own[a++];
        while (b < pivotCount) _merged[n++] = pivotColumns[b++];

        Array.Copy(_merged, own, n);
        _counts[i] = n;
    }

    /// <summary>Solves A·x = b using the stored factorisation, writing the result into <paramref name="x"/>.</summary>
    public void Solve(double[] b, double[] x)
    {
        if (!_factored) throw new InvalidOperationException("Factor must be called before Solve.");
        ArgumentOutOfRangeException.ThrowIfNotEqual(b.Length, _n);
        ArgumentOutOfRangeException.ThrowIfNotEqual(x.Length, _n);

        // Forward substitution, applying both permutations: the ordering chosen before the
        // elimination and the row swaps the pivoting made during it.
        for (var i = 0; i < _n; i++)
        {
            var sum = b[_order[_pivot[i]]];
            var row = _lu[i];
            for (var j = 0; j < i; j++) sum -= row[j] * _scratch[j];
            _scratch[i] = sum;
        }

        // Back substitution.
        for (var i = _n - 1; i >= 0; i--)
        {
            var sum = _scratch[i];
            var row = _lu[i];
            for (var j = i + 1; j < _n; j++) sum -= row[j] * _scratch[j];
            _scratch[i] = sum / row[i];
        }

        // And back out of the ordering, so the caller gets its own unknowns in its own order.
        for (var i = 0; i < _n; i++) x[_order[i]] = _scratch[i];
    }

    /// <summary>
    /// Chooses an order for the unknowns that keeps the elimination sparse, by Reverse
    /// Cuthill-McKee.
    /// <para>
    /// This is the part that makes sparsity worth exploiting, and leaving it out is why the first
    /// attempt at this was <i>slower</i> than eliminating densely. A circuit matrix is beautifully
    /// sparse as stamped — three entries a row, under half a percent full — but the numbering it
    /// arrives in comes from the order parts were added, so a node is routinely coupled to one
    /// several hundred rows away. Measured on a ladder of eight hundred rungs: three entries per
    /// row, and a bandwidth of four hundred. Eliminating that fills in the whole band, the rows
    /// become dense, and iterating over their columns indirectly is simply a slower way of doing
    /// the dense arithmetic.
    /// </para>
    /// <para>
    /// Cuthill-McKee walks the coupling graph breadth-first from a peripheral node, taking
    /// neighbours in order of how few neighbours they have; reversing the result is a refinement
    /// that is free and usually better. It is not the best ordering available — minimum degree
    /// generally fills less — but it is simple enough to be obviously right, and on the matrices
    /// this produces it turns a bandwidth of four hundred into one of about two.
    /// </para>
    /// </summary>
    private void Order(double[][] a)
    {
        // Degrees first: the walk visits the least-connected neighbour first.
        for (var i = 0; i < _n; i++)
        {
            var count = 0;
            var row = a[i];

            for (var j = 0; j < _n; j++)
                if (j != i && (row[j] != 0 || a[j][i] != 0))
                    count++;

            _degree[i] = count;
            _placed[i] = false;
        }

        var at = 0;

        for (var seed = 0; seed < _n; seed++)
        {
            if (_placed[seed]) continue;

            // A new component of the graph — a circuit can be two circuits on one sheet.
            var start = Peripheral(a, seed);

            _placed[start] = true;
            _order[at++] = start;

            for (var head = at - 1; head < at; head++)
            {
                var node = _order[head];
                var first = at;

                for (var j = 0; j < _n; j++)
                {
                    if (_placed[j] || j == node) continue;
                    if (a[node][j] == 0 && a[j][node] == 0) continue;

                    _placed[j] = true;
                    _order[at++] = j;
                }

                // The neighbours just added, least connected first.
                SortByDegree(first, at);
            }
        }

        // Reversed, which is Cuthill-McKee's cheap improvement on itself.
        for (var i = 0; i < _n / 2; i++)
            (_order[i], _order[_n - 1 - i]) = (_order[_n - 1 - i], _order[i]);
    }

    /// <summary>Sorts one stretch of the order by how many neighbours each node has.</summary>
    private void SortByDegree(int from, int to)
    {
        for (var i = from + 1; i < to; i++)
        {
            var value = _order[i];
            var degree = _degree[value];
            var j = i - 1;

            while (j >= from && _degree[_order[j]] > degree)
            {
                _order[j + 1] = _order[j];
                j--;
            }

            _order[j + 1] = value;
        }
    }

    /// <summary>
    /// A node at the edge of the graph to start the walk from: the least connected one reachable
    /// from the seed. A walk that starts in the middle of a circuit spreads both ways at once and
    /// produces a wider band than one that starts at an end.
    /// </summary>
    private int Peripheral(double[][] a, int seed)
    {
        var best = seed;
        var least = int.MaxValue;

        for (var i = seed; i < _n; i++)
        {
            if (_placed[i] || _degree[i] >= least) continue;

            least = _degree[i];
            best = i;
        }

        return least == int.MaxValue ? seed : best;
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
