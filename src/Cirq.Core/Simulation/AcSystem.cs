using System.Numerics;
using Cirq.Core.Topology;

namespace Cirq.Core.Simulation;

/// <summary>
/// The small-signal system: the same unknowns as <see cref="MnaSystem"/>, but complex, so that a
/// reactance can be a number rather than a companion model.
/// <para>
/// A transient solve has to approximate a capacitor by a resistor and a current source recomputed
/// every step, because time-stepping is all it has. A frequency sweep does not: at one frequency a
/// capacitor <i>is</i> an admittance of jωC, exactly, and the answer comes out of a single solve
/// with no integration error in it at all. That is what makes a Bode plot from one of these
/// trustworthy in a way that measuring the same thing by stepping a generator is not.
/// </para>
/// <para>
/// The catch is that it is only ever a <b>small-signal</b> answer. Everything non-linear has been
/// replaced by its slope at the bias point, so a sweep says nothing about clipping, slew limiting,
/// or any other thing a circuit does when asked for more than a little.
/// </para>
/// </summary>
public sealed class AcSystem
{
    private readonly MnaSystem _scratch;

    public AcSystem(MnaSystem scratch)
    {
        _scratch = scratch;
        Size = scratch.Size;

        Matrix = new Complex[Size][];
        for (var i = 0; i < Size; i++) Matrix[i] = new Complex[Size];

        Rhs = new Complex[Size];
        Solution = new Complex[Size];
    }

    /// <summary>Total system order — identical to the transient system's.</summary>
    public int Size { get; }

    public Complex[][] Matrix { get; }

    public Complex[] Rhs { get; }

    public Complex[] Solution { get; }

    /// <summary>Angular frequency this pass is being stamped at, in radians per second.</summary>
    public double AngularFrequency { get; internal set; }

    /// <summary>Frequency this pass is being stamped at, in hertz.</summary>
    public double Frequency => AngularFrequency / (2.0 * Math.PI);

    public Netlist Netlist => _scratch.Netlist;

    public int Node(Terminal terminal) => _scratch.Node(terminal);

    public int Branch(CircuitComponent component, int local = 0) => _scratch.Branch(component, local);

    public int InternalNode(CircuitComponent component, int local = 0) =>
        _scratch.InternalNode(component, local);

    public void Clear()
    {
        for (var i = 0; i < Size; i++) Array.Clear(Matrix[i]);

        Array.Clear(Rhs);
    }

    /// <summary>Accumulates into A[row][col], dropping anything that touches ground.</summary>
    public void Add(int row, int col, Complex value)
    {
        if (row < 0 || col < 0) return;

        Matrix[row][col] += value;
    }

    /// <summary>Accumulates into b[row], dropping anything that touches ground.</summary>
    public void AddRhs(int row, Complex value)
    {
        if (row < 0) return;

        Rhs[row] += value;
    }

    /// <summary>Stamps an admittance between two nodes, the complex form of a conductance.</summary>
    public void StampAdmittance(int nodeA, int nodeB, Complex y)
    {
        Add(nodeA, nodeA, y);
        Add(nodeB, nodeB, y);
        Add(nodeA, nodeB, -y);
        Add(nodeB, nodeA, -y);
    }

    /// <summary>Stamps the admittance of a capacitance, which is all a capacitor is here.</summary>
    public void StampCapacitance(int nodeA, int nodeB, double farads) =>
        StampAdmittance(nodeA, nodeB, new Complex(0, AngularFrequency * farads));

    /// <summary>
    /// Adds the reactance of an inductance to a branch that has already been stamped as a short.
    /// <para>
    /// An inductor's DC stamp is the row <c>v_a − v_b − R·i = 0</c>, so the impedance is already
    /// sitting on the branch diagonal with the winding resistance in it; all the frequency does is
    /// add jωL to it.
    /// </para>
    /// </summary>
    public void AddInductance(int branch, double henries) =>
        Add(branch, branch, new Complex(0, -AngularFrequency * henries));

    /// <summary>Adds a mutual inductance between two inductor branches.</summary>
    public void AddMutualInductance(int branchA, int branchB, double henries)
    {
        Add(branchA, branchB, new Complex(0, -AngularFrequency * henries));
        Add(branchB, branchA, new Complex(0, -AngularFrequency * henries));
    }

    /// <summary>An excitation of a given magnitude and phase, as a complex number.</summary>
    public static Complex Phasor(double magnitude, double phaseDegrees) =>
        Complex.FromPolarCoordinates(magnitude, phaseDegrees * Math.PI / 180.0);

    /// <summary>
    /// Folds the real matrix built by the ordinary DC stamps into this one.
    /// <para>
    /// The right-hand side is deliberately dropped. A DC stamp's right-hand side is the bias
    /// point's own excitation — a supply's voltage, a diode's linearisation offset — and none of
    /// that is a small signal. What a source contributes to a sweep is whatever AC magnitude it
    /// was given, which it adds itself.
    /// </para>
    /// </summary>
    internal void FoldInConductances()
    {
        for (var row = 0; row < Size; row++)
        {
            var source = _scratch.Matrix[row];
            var target = Matrix[row];

            for (var col = 0; col < Size; col++)
            {
                if (source[col] != 0) target[col] += source[col];
            }
        }
    }

    public Complex NodeVoltage(int node) => node < 0 ? Complex.Zero : Solution[node];

    public Complex NodeVoltage(Terminal terminal) => NodeVoltage(Node(terminal));

    public Complex BranchCurrent(CircuitComponent component, int local = 0) =>
        Solution[Branch(component, local)];
}
