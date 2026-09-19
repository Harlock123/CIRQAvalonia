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

    /// <summary>Zeroes the matrix and right-hand side ahead of a fresh stamping pass.</summary>
    public void Clear()
    {
        for (var i = 0; i < Size; i++) Array.Clear(Matrix[i]);
        Array.Clear(Rhs);
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
