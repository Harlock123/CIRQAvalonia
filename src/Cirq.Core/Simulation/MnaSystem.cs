using Cirq.Core.Topology;

namespace Cirq.Core.Simulation;

/// <summary>
/// The Modified Nodal Analysis system: a dense matrix, a right-hand side, and the stamping
/// primitives every component uses to contribute to them.
/// <para>
/// Layout: rows/columns <c>[0, NodeCount)</c> are node voltages; rows/columns
/// <c>[NodeCount, Size)</c> are auxiliary branch currents contributed by voltage sources,
/// inductors and controlled sources. Ground is node index <c>-1</c> and is never stamped, which
/// is what grounds the system.
/// </para>
/// </summary>
public sealed class MnaSystem
{
    private readonly Dictionary<(Guid Component, int Local), int> _branchIndices;

    public MnaSystem(Netlist netlist, int nodeCount, Dictionary<(Guid, int), int> branchIndices)
    {
        Netlist = netlist;
        NodeCount = nodeCount;
        _branchIndices = branchIndices;
        BranchCount = branchIndices.Count;
        Size = NodeCount + BranchCount;

        Matrix = new double[Size][];
        for (var i = 0; i < Size; i++) Matrix[i] = new double[Size];
        _touched = new bool[Size * Size];
        Rhs = new double[Size];
        Solution = new double[Size];
        PreviousSolution = new double[Size];
    }

    public Netlist Netlist { get; }

    /// <summary>Number of non-ground nodes.</summary>
    public int NodeCount { get; }

    /// <summary>Number of auxiliary branch-current unknowns.</summary>
    public int BranchCount { get; }

    /// <summary>Total system order.</summary>
    public int Size { get; }

    /// <summary>The coefficient matrix, indexed <c>[row][column]</c>.</summary>
    public double[][] Matrix { get; }

    public double[] Rhs { get; }

    /// <summary>Solution of the current Newton iteration.</summary>
    public double[] Solution { get; }

    /// <summary>Solution of the previous Newton iteration, which non-linear stampers linearise about.</summary>
    public double[] PreviousSolution { get; }

    /// <summary>
    /// Which entries of the matrix have been stamped since it was last cleared, in the order they
    /// were first touched, with a flag per entry so each is listed once.
    /// <para>
    /// A circuit matrix is a fraction of a percent full, and zeroing all n² entries before every
    /// stamping pass came to most of the time a logic-heavy circuit spends per time point — a
    /// 6502 computer of 151 unknowns clears 182 KB to re-stamp a few hundred numbers. Every write
    /// goes through <see cref="Add"/>, so remembering where it wrote is enough to clear only that.
    /// </para>
    /// </summary>
    private readonly bool[] _touched;

    private readonly List<int> _touchedList = [];

    /// <summary>The row and column of each entry in <see cref="_touchedList"/>, so nothing has to divide to find them.</summary>
    private readonly List<(int Row, int Col)> _touchedCells = [];

    /// <summary>Zeroes the matrix and right-hand side ahead of a fresh stamping pass.</summary>
    public void Clear()
    {
        foreach (var index in _touchedList) _touched[index] = false;
        foreach (var (row, col) in _touchedCells) Matrix[row][col] = 0;

        _touchedList.Clear();
        _touchedCells.Clear();
        Array.Clear(Rhs);
    }

    /// <summary>
    /// True when the matrix is exactly the one <paramref name="snapshot"/> recorded: the same
    /// entries stamped, in the same order, to the same values. A matrix stamped identically to the
    /// one last factored can reuse that factorisation.
    /// </summary>
    public bool Matches(MatrixSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Indices.Count != _touchedList.Count) return false;

        for (var k = 0; k < _touchedList.Count; k++)
        {
            if (snapshot.Indices[k] != _touchedList[k]) return false;
            var (row, col) = _touchedCells[k];
            if (!snapshot.Values[k].Equals(Matrix[row][col])) return false;
        }

        return true;
    }

    /// <summary>
    /// The entries where the matrix differs from <paramref name="snapshot"/>, as (row, column, change),
    /// when the same entries were stamped in the same order and no more than <paramref name="limit"/>
    /// of them changed; false otherwise. This is what lets a matrix that moved in a handful of places
    /// — the companion conductances of a few capacitors when the step changes — be solved from the
    /// factorisation of the one before, corrected, rather than factored again.
    /// </summary>
    public bool Differences(MatrixSnapshot snapshot, List<(int Row, int Col, double Change)> changes, int limit)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(changes);
        changes.Clear();
        if (snapshot.Indices.Count != _touchedList.Count) return false;

        for (var k = 0; k < _touchedList.Count; k++)
        {
            if (snapshot.Indices[k] != _touchedList[k]) return false;
            var (row, col) = _touchedCells[k];
            var now = Matrix[row][col];
            if (snapshot.Values[k].Equals(now)) continue;
            if (changes.Count == limit) return false;
            changes.Add((row, col, now - snapshot.Values[k]));
        }

        return true;
    }

    /// <summary>
    /// The residual b − Ax into <paramref name="residual"/>, and whether it is within a relative
    /// <paramref name="tolerance"/> in every row — against the size of the terms in that row — or
    /// within <paramref name="floor"/> outright, for the rows of a node that only gmin holds.
    /// </summary>
    public bool Residual(double[] x, double[] residual, double tolerance, double floor)
    {
        ArgumentNullException.ThrowIfNull(x);
        ArgumentNullException.ThrowIfNull(residual);
        _scale ??= new double[Size];
        Array.Copy(Rhs, residual, Size);
        for (var i = 0; i < Size; i++) _scale[i] = Math.Abs(Rhs[i]);

        foreach (var (row, col) in _touchedCells)
        {
            var term = Matrix[row][col] * x[col];
            residual[row] -= term;
            _scale[row] += Math.Abs(term);
        }

        var good = true;
        for (var i = 0; i < Size && good; i++)
            if (Math.Abs(residual[i]) > (tolerance * _scale[i]) + floor) good = false;

        return good;
    }

    private double[]? _scale;

    /// <summary>Records the matrix as it stands into <paramref name="snapshot"/>.</summary>
    public void Record(MatrixSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        snapshot.Indices.Clear();
        snapshot.Values.Clear();

        for (var k = 0; k < _touchedList.Count; k++)
        {
            snapshot.Indices.Add(_touchedList[k]);
            var (row, col) = _touchedCells[k];
            snapshot.Values.Add(Matrix[row][col]);
        }
    }

    // ---- index helpers -------------------------------------------------

    public int Node(Terminal terminal) => Netlist.IndexOf(terminal);

    /// <summary>Auxiliary branch row for a component's <paramref name="local"/>-th branch unknown.</summary>
    public int Branch(CircuitComponent component, int local = 0)
    {
        if (_branchIndices.TryGetValue((component.Id, local), out var index)) return NodeCount + index;
        throw new CircuitTopologyException(
            $"Component '{component.Name}' did not reserve branch {local}. Check its VoltageSourceCount.");
    }

    /// <summary>
    /// Row for a component's <paramref name="local"/>-th internal node. Internal nodes share the
    /// auxiliary unknown pool with branch currents, allocated immediately after them.
    /// </summary>
    public int InternalNode(CircuitComponent component, int local = 0) =>
        Branch(component, component.VoltageSourceCount + local);

    // ---- raw accumulation ----------------------------------------------

    /// <summary>Accumulates into A[row][col], silently dropping stamps that touch ground.</summary>
    public void Add(int row, int col, double value)
    {
        if (row < 0 || col < 0) return;

        var index = (row * Size) + col;
        if (!_touched[index])
        {
            _touched[index] = true;
            _touchedList.Add(index);
            _touchedCells.Add((row, col));
        }

        Matrix[row][col] += value;
    }

    /// <summary>Accumulates into b[row], silently dropping stamps that touch ground.</summary>
    public void AddRhs(int row, double value)
    {
        if (row < 0) return;
        Rhs[row] += value;
    }

    // ---- element stamps -------------------------------------------------

    /// <summary>Stamps a conductance <paramref name="g"/> siemens between two nodes.</summary>
    public void StampConductance(int nodeA, int nodeB, double g)
    {
        Add(nodeA, nodeA, g);
        Add(nodeB, nodeB, g);
        Add(nodeA, nodeB, -g);
        Add(nodeB, nodeA, -g);
    }

    public void StampResistor(int nodeA, int nodeB, double resistance)
    {
        if (resistance <= 0) throw new ArgumentOutOfRangeException(nameof(resistance), "Resistance must be positive.");
        StampConductance(nodeA, nodeB, 1.0 / resistance);
    }

    /// <summary>
    /// Stamps an independent current source of <paramref name="current"/> amps flowing through the
    /// element from <paramref name="nodeFrom"/> to <paramref name="nodeTo"/>, i.e. drawn out of
    /// <paramref name="nodeFrom"/> and injected into <paramref name="nodeTo"/>.
    /// </summary>
    public void StampCurrentSource(int nodeFrom, int nodeTo, double current)
    {
        AddRhs(nodeFrom, -current);
        AddRhs(nodeTo, current);
    }

    /// <summary>
    /// Stamps a Norton companion branch <c>i = g·(v_p − v_n) + ieq</c> between two nodes, the form
    /// every linearised or discretised two-terminal element reduces to.
    /// </summary>
    public void StampNorton(int nodePlus, int nodeMinus, double conductance, double equivalentCurrent)
    {
        StampConductance(nodePlus, nodeMinus, conductance);
        StampCurrentSource(nodePlus, nodeMinus, equivalentCurrent);
    }

    /// <summary>
    /// Stamps an ideal voltage source of <paramref name="voltage"/> volts across the branch, using
    /// the auxiliary row <paramref name="branch"/> to carry its current.
    /// </summary>
    public void StampVoltageSource(int branch, int nodePlus, int nodeMinus, double voltage)
    {
        Add(nodePlus, branch, 1.0);
        Add(nodeMinus, branch, -1.0);
        Add(branch, nodePlus, 1.0);
        Add(branch, nodeMinus, -1.0);
        AddRhs(branch, voltage);
    }

    /// <summary>
    /// Stamps a voltage source with a series resistance (a Thevenin source), the model used for
    /// logic outputs and regulator outputs. Requires one auxiliary branch.
    /// </summary>
    public void StampTheveninSource(int branch, int nodePlus, int nodeMinus, double voltage, double seriesResistance)
    {
        if (seriesResistance <= 0)
        {
            StampVoltageSource(branch, nodePlus, nodeMinus, voltage);
            return;
        }

        // v_p - v_n - R·i = V
        Add(nodePlus, branch, 1.0);
        Add(nodeMinus, branch, -1.0);
        Add(branch, nodePlus, 1.0);
        Add(branch, nodeMinus, -1.0);
        Add(branch, branch, -seriesResistance);
        AddRhs(branch, voltage);
    }

    /// <summary>Voltage-controlled current source: i(out+ -&gt; out-) = gm · (v_cp − v_cn).</summary>
    public void StampVccs(int outPlus, int outMinus, int controlPlus, int controlMinus, double gm)
    {
        Add(outPlus, controlPlus, gm);
        Add(outPlus, controlMinus, -gm);
        Add(outMinus, controlPlus, -gm);
        Add(outMinus, controlMinus, gm);
    }

    /// <summary>Voltage-controlled voltage source: v_out = gain · (v_cp − v_cn). Requires one branch.</summary>
    public void StampVcvs(int branch, int outPlus, int outMinus, int controlPlus, int controlMinus, double gain)
    {
        Add(outPlus, branch, 1.0);
        Add(outMinus, branch, -1.0);
        Add(branch, outPlus, 1.0);
        Add(branch, outMinus, -1.0);
        Add(branch, controlPlus, -gain);
        Add(branch, controlMinus, gain);
    }

    /// <summary>
    /// Stamps the coupling term of two inductive branches: <c>v1 -= k·di2/dt</c> discretised as a
    /// coefficient on the other branch's current unknown.
    /// </summary>
    public void StampBranchCoupling(int branch, int otherBranch, double coefficient)
    {
        Add(branch, otherBranch, coefficient);
    }

    // ---- solution access -------------------------------------------------

    /// <summary>Node voltage from the current iteration; ground reads as exactly 0.</summary>
    public double NodeVoltage(int node) => node < 0 ? 0.0 : Solution[node];

    public double NodeVoltage(Terminal terminal) => NodeVoltage(Node(terminal));

    /// <summary>
    /// Any unknown's value from the previous Newton iteration: a node voltage, an internal node
    /// voltage, or a branch current, depending on which row is asked for.
    /// </summary>
    public double IterationValue(int row) => row < 0 ? 0.0 : PreviousSolution[row];

    /// <summary>Node voltage from the previous Newton iteration, used to linearise non-linear devices.</summary>
    public double IterationVoltage(int node) => IterationValue(node);

    public double IterationVoltage(Terminal terminal) => IterationVoltage(Node(terminal));

    /// <summary>Voltage across a pair of nodes as seen by the current Newton iterate.</summary>
    public double IterationVoltageAcross(int nodePlus, int nodeMinus) =>
        IterationVoltage(nodePlus) - IterationVoltage(nodeMinus);

    public double BranchCurrent(CircuitComponent component, int local = 0) => Solution[Branch(component, local)];

    public double BranchCurrentAt(int branchRow) => Solution[branchRow];

    /// <summary>Copies the current solution into the previous-iteration vector.</summary>
    public void CommitIteration() => Array.Copy(Solution, PreviousSolution, Size);

    public void ResetSolution()
    {
        Array.Clear(Solution);
        Array.Clear(PreviousSolution);
    }
}

/// <summary>A matrix's stamped entries and their values, to tell whether a later stamping produced the same one.</summary>
public sealed class MatrixSnapshot
{
    public List<int> Indices { get; } = [];

    public List<double> Values { get; } = [];
}
